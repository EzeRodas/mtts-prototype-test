using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MoonTTS.Engine.Protocol;

namespace MoonTTS.Engine.Mbrola;

/* File Structure:
        - MbrolaWorker(Stream output)
        - HandleAsync(EngineFrame frame) : Task<bool>
        - WaitForIdleAsync() : Task
        - HandleLoadModelAsync(EngineFrame frame) : Task
        - EnsureEspeakInitialized() : void
        - StartSynthesis(EngineFrame frame) : Task
        - SynthesizeAsync(SynthesizeRequest request, CancellationToken cancellationToken) : Task
        - RenderUtterance(string text, string voiceId, float speed, CancellationToken cancellationToken) : byte[]
        - OnSynthCallback(short* wav, int numSamples, IntPtr events) : int
        - ResamplePcm16(ReadOnlySpan<short> input, int sourceRate, int targetRate) : byte[]
        - SendAsync(EngineOpcode opcode, ReadOnlyMemory<byte> payload) : Task
        - SendJsonAsync<T>(EngineOpcode opcode, T message, JsonTypeInfo<T> typeInfo) : Task
        - SendErrorAsync(string code, string message) : Task
        - Dispose() : void
*/

/* Answers the Moon-TTS engine protocol using eSpeak NG G2P + in-memory MBROLA diphone synthesis.
   Synthesis runs on a background task so a Cancel frame can interrupt active speech immediately */
public sealed class MbrolaWorker : IDisposable
{
    private const string EngineId = "mbrola";
    private const int OutputSampleRate = 22050;
    private const int DefaultWpm = 175;
    private const int MinWpm = 80;
    private const int MaxWpm = 450;

    private static MemoryStream? s_activeBuffer;
    private static CancellationToken s_activeCancellationToken;

    private readonly Stream _output;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _synthLock = new();

    private bool _initialized;
    private bool _modelLoaded;
    private Task? _activeSynthesis;
    private CancellationTokenSource? _activeCts;

    /* Binds the worker to the protocol output stream */
    public MbrolaWorker(Stream output)
    {
        _output = output;
    }

    /* Dispatches one request frame. Returns false when the host asked the worker to shut down */
    public async Task<bool> HandleAsync(EngineFrame frame)
    {
        switch (frame.Opcode)
        {
            case EngineOpcode.Ping:
                await SendAsync(EngineOpcode.Pong, ReadOnlyMemory<byte>.Empty);
                return true;

            case EngineOpcode.GetMetadata:
                await SendJsonAsync(EngineOpcode.MetadataResponse, new EngineMetadataMessage
                {
                    EngineId = EngineId,
                    EngineName = "MBROLA Diphone Synthesizer",
                    Version = "1.0.0",
                    Author = "Moon-TTS",
                    AudioFormat = new EngineAudioFormat
                    {
                        SampleRate = OutputSampleRate,
                        Channels = 1,
                        BitDepth = 16
                    },
                    SupportsStreaming = false,
                    SupportsSpeedAdjustment = true,
                    SupportsPitchAdjustment = false
                }, EngineProtocolJsonContext.Default.EngineMetadataMessage);
                return true;

            case EngineOpcode.LoadModel:
                await HandleLoadModelAsync(frame);
                return true;

            case EngineOpcode.ListVoices:
                await SendJsonAsync(EngineOpcode.VoicesResponse,
                    new VoicesResponse { Voices = MbrolaVoices.Describe() },
                    EngineProtocolJsonContext.Default.VoicesResponse);
                return true;

            case EngineOpcode.Synthesize:
                await StartSynthesis(frame);
                return true;

            case EngineOpcode.Cancel:
                /* Cancel has no direct reply: the active synthesis aborts via the callback and ends with EndOfStream */
                _activeCts?.Cancel();
                return true;

            case EngineOpcode.Shutdown:
                return false;

            default:
                await SendErrorAsync("unsupported_opcode", $"Opcode {frame.Opcode} is not supported.");
                return true;
        }
    }

    /* Waits for the running synthesis (if any) so its terminal frame is written before exiting */
    public async Task WaitForIdleAsync()
    {
        _activeCts?.Cancel();
        if (_activeSynthesis != null)
            await _activeSynthesis;
    }

    /* Configures the MBROLA voice directory, verifies required voice files, and initializes libespeak-ng */
    private async Task HandleLoadModelAsync(EngineFrame frame)
    {
        try
        {
            var request = EngineIpcProtocol.ReadJson(frame, EngineProtocolJsonContext.Default.LoadModelRequest);
            string bundledMbrolaDir = Path.Combine(AppContext.BaseDirectory, "espeak-ng-data", "mbrola");

            if (!string.IsNullOrWhiteSpace(request.ModelDirectory))
            {
                EspeakMbrolaNative.SetNativeEnvironmentVariable("MOON_MBROLA_DIR", request.ModelDirectory);
            }

            /* When the manifest specifies downloadable voice files, verify they exist in ModelDirectory or bundled dir */
            foreach (string file in request.Files)
            {
                string downloadedPath = Path.Combine(request.ModelDirectory, file);
                string bundledPath = Path.Combine(bundledMbrolaDir, file);
                if (!File.Exists(downloadedPath) && !File.Exists(bundledPath))
                {
                    await SendErrorAsync("model_not_found",
                        $"Missing MBROLA voice database '{file}' in '{request.ModelDirectory}'.");
                    return;
                }
            }

            EnsureEspeakInitialized();
            _modelLoaded = true;
            await SendAsync(EngineOpcode.ModelLoaded, ReadOnlyMemory<byte>.Empty);
        }
        catch (Exception ex)
        {
            await SendErrorAsync("model_load_failed", ex.Message);
        }
    }

    /* Initializes libespeak-ng once in synchronous callback mode */
    private unsafe void EnsureEspeakInitialized()
    {
        if (_initialized)
            return;

        EspeakMbrolaNative.EnsureResolverRegistered();

        string dataRoot = Path.Combine(AppContext.BaseDirectory, "espeak-ng-data");
        if (!Directory.Exists(dataRoot))
        {
            throw new DirectoryNotFoundException($"Missing espeak-ng-data directory at '{dataRoot}'.");
        }

        int rate = EspeakMbrolaNative.espeak_Initialize(
            EspeakMbrolaNative.AudioOutputSynchronous,
            500,
            dataRoot,
            0);

        if (rate <= 0)
        {
            throw new InvalidOperationException($"espeak_Initialize failed (code {rate}).");
        }

        EspeakMbrolaNative.espeak_SetSynthCallback(&OnSynthCallback);
        _initialized = true;
    }

    /* Starts a synthesis job in the background; only one job may run at a time */
    private async Task StartSynthesis(EngineFrame frame)
    {
        if (_activeSynthesis is { IsCompleted: false })
        {
            await SendErrorAsync("busy", "A synthesis job is already running.");
            return;
        }

        SynthesizeRequest request;
        try
        {
            request = EngineIpcProtocol.ReadJson(frame, EngineProtocolJsonContext.Default.SynthesizeRequest);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            await SendErrorAsync("invalid_request", ex.Message);
            return;
        }

        _activeCts?.Dispose();
        _activeCts = new CancellationTokenSource();
        var token = _activeCts.Token;
        _activeSynthesis = Task.Run(() => SynthesizeAsync(request, token));
    }

    /* Renders the text with the requested MBROLA voice and streams it back as one de-clicked PCM chunk */
    private async Task SynthesizeAsync(SynthesizeRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (!_modelLoaded)
            {
                await SendErrorAsync("model_not_loaded", "No model is loaded.");
                return;
            }
            if (!MbrolaVoices.Ids.Contains(request.VoiceId))
            {
                await SendErrorAsync("unknown_voice", $"Voice '{request.VoiceId}' does not exist.");
                return;
            }

            if (!string.IsNullOrWhiteSpace(request.Text))
            {
                byte[] pcmBytes = RenderUtterance(
                    request.Text,
                    request.VoiceId,
                    request.Speed > 0 ? request.Speed : 1.0f,
                    cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();

                if (pcmBytes.Length > 0)
                {
                    AudioDsp.ApplyDeClickingInPlace(pcmBytes, OutputSampleRate);
                    await SendAsync(EngineOpcode.AudioChunk, pcmBytes);
                }
            }

            await SendAsync(EngineOpcode.EndOfStream, ReadOnlyMemory<byte>.Empty);
        }
        catch (OperationCanceledException)
        {
            await SendAsync(EngineOpcode.EndOfStream, ReadOnlyMemory<byte>.Empty);
        }
        catch (Exception ex)
        {
            await SendErrorAsync("synthesis_failed", ex.Message);
        }
    }

    /* Invokes libespeak-ng + LibOneChannel synchronously and resamples to 22050 Hz when needed */
    private unsafe byte[] RenderUtterance(string text, string voiceId, float speed, CancellationToken cancellationToken)
    {
        lock (_synthLock)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int voiceErr = EspeakMbrolaNative.espeak_SetVoiceByName(voiceId);
            if (voiceErr != EspeakMbrolaNative.EeOk)
            {
                throw new InvalidOperationException(
                    $"Failed to activate MBROLA voice '{voiceId}' (espeak error {voiceErr}).");
            }

            int wpm = Math.Clamp((int)Math.Round(DefaultWpm * speed), MinWpm, MaxWpm);
            EspeakMbrolaNative.espeak_SetParameter(EspeakMbrolaNative.EspeakRate, wpm, 0);

            int nativeRate = EspeakMbrolaNative.getFreq_MBR();
            if (nativeRate <= 0)
                nativeRate = OutputSampleRate;

            using var buffer = new MemoryStream();
            s_activeBuffer = buffer;
            s_activeCancellationToken = cancellationToken;
            try
            {
                byte[] utf8Bytes = Encoding.UTF8.GetBytes(text + "\0");
                fixed (byte* ptr = utf8Bytes)
                {
                    int synthErr = EspeakMbrolaNative.espeak_Synth(
                        ptr,
                        (nuint)utf8Bytes.Length,
                        0,
                        EspeakMbrolaNative.PosCharacter,
                        0,
                        EspeakMbrolaNative.EspeakCharsUtf8,
                        null,
                        null);

                    if (synthErr != EspeakMbrolaNative.EeOk && !cancellationToken.IsCancellationRequested)
                    {
                        throw new InvalidOperationException($"espeak_Synth failed with code {synthErr}.");
                    }
                }
            }
            finally
            {
                s_activeBuffer = null;
                s_activeCancellationToken = CancellationToken.None;
            }

            cancellationToken.ThrowIfCancellationRequested();

            byte[] rawBytes = buffer.ToArray();
            if (rawBytes.Length == 0 || nativeRate == OutputSampleRate)
                return rawBytes;

            ReadOnlySpan<short> samples = MemoryMarshal.Cast<byte, short>(rawBytes.AsSpan());
            return ResamplePcm16(samples, nativeRate, OutputSampleRate);
        }
    }

    /* Unmanaged C callback receiving 16-bit mono PCM chunks from libespeak-ng / MBROLA */
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int OnSynthCallback(short* wav, int numSamples, IntPtr events)
    {
        if (s_activeCancellationToken.IsCancellationRequested)
            return 1;

        if (wav != null && numSamples > 0 && s_activeBuffer != null)
        {
            ReadOnlySpan<byte> bytes = new(wav, numSamples * sizeof(short));
            s_activeBuffer.Write(bytes);
        }
        return 0;
    }

    /* Linearly resamples 16-bit mono PCM from sourceRate (e.g. 16000 Hz) to targetRate (22050 Hz) */
    private static byte[] ResamplePcm16(ReadOnlySpan<short> input, int sourceRate, int targetRate)
    {
        if (input.IsEmpty)
            return Array.Empty<byte>();

        int outputSamples = (int)((long)input.Length * targetRate / sourceRate);
        if (outputSamples <= 0)
            return Array.Empty<byte>();

        byte[] outputBytes = new byte[outputSamples * sizeof(short)];
        Span<short> output = MemoryMarshal.Cast<byte, short>(outputBytes.AsSpan());

        double step = (double)sourceRate / targetRate;
        int lastIndex = input.Length - 1;

        for (int i = 0; i < outputSamples; i++)
        {
            double srcPos = i * step;
            int idx = (int)srcPos;
            if (idx >= lastIndex)
            {
                output[i] = input[lastIndex];
            }
            else
            {
                double frac = srcPos - idx;
                double s0 = input[idx];
                double s1 = input[idx + 1];
                output[i] = (short)Math.Clamp(Math.Round(s0 + (s1 - s0) * frac), short.MinValue, short.MaxValue);
            }
        }

        return outputBytes;
    }

    /* Writes one frame to stdout under the write lock */
    private async Task SendAsync(EngineOpcode opcode, ReadOnlyMemory<byte> payload)
    {
        await _writeLock.WaitAsync();
        try
        {
            await EngineIpcProtocol.WriteFrameAsync(_output, opcode, payload);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /* Serializes and writes a JSON control frame */
    private Task SendJsonAsync<T>(EngineOpcode opcode, T message, JsonTypeInfo<T> typeInfo) =>
        SendAsync(opcode, JsonSerializer.SerializeToUtf8Bytes(message, typeInfo));

    /* Writes the terminal Error frame of a failed request */
    private Task SendErrorAsync(string code, string message) =>
        SendJsonAsync(EngineOpcode.Error, new EngineErrorMessage { Code = code, Message = message },
            EngineProtocolJsonContext.Default.EngineErrorMessage);

    /* Releases synchronization primitives */
    public void Dispose()
    {
        _activeCts?.Cancel();
        _activeCts?.Dispose();
        _writeLock.Dispose();
    }
}
