using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using MoonTTS.Engine.Protocol;

namespace MoonTTS.Engine.Mbrola;

/* File Structure:
        - MbrolaWorker(Stream output)
        - GetMetadata() : EngineMetadataMessage
        - ListVoices() : List<VoiceEntry>
        - LoadModelAsync(LoadModelRequest request) : Task
        - EnsureEspeakInitialized() : void
        - SynthesizeAsync(SynthesizeRequest request, CancellationToken cancellationToken) : Task
        - RenderUtterance(string text, string voiceId, float speed, CancellationToken cancellationToken) : byte[]
        - OnSynthCallback(short* wav, int numSamples, IntPtr events) : int
        - ResamplePcm16Cubic(ReadOnlySpan<short> input, int sourceRate, int targetRate) : byte[]
*/

/* Answers the Moon-TTS engine protocol using eSpeak NG G2P + in-memory MBROLA diphone synthesis */
public sealed class MbrolaWorker : EngineWorkerBase
{
    private const string EngineId = "mbrola";
    private const int OutputSampleRate = 22050;
    private const int DefaultWpm = 175;
    private const int MinWpm = 80;
    private const int MaxWpm = 450;

    private static MemoryStream? s_activeBuffer;
    private static CancellationToken s_activeCancellationToken;

    private readonly object _synthLock = new();
    private bool _initialized;
    private bool _modelLoaded;

    /* Binds the worker to the protocol output stream */
    public MbrolaWorker(Stream output) : base(output)
    {
    }

    /* Returns MBROLA metadata and 22.05 kHz mono 16-bit PCM format */
    protected override EngineMetadataMessage GetMetadata() => new()
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
    };

    /* Returns all supported MBROLA diphone voices */
    protected override List<VoiceEntry> ListVoices() => MbrolaVoices.Describe();

    /* Configures the MBROLA voice directory, verifies required voice files, and initializes libespeak-ng */
    protected override Task LoadModelAsync(LoadModelRequest request)
    {
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
                throw new WorkerErrorException(
                    "model_not_found",
                    $"Missing MBROLA voice database '{file}' in '{request.ModelDirectory}'.");
            }
        }

        EnsureEspeakInitialized();
        _modelLoaded = true;
        return Task.CompletedTask;
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

    /* Renders the text with the requested MBROLA voice and streams it back as one de-clicked PCM chunk */
    protected override async Task SynthesizeAsync(SynthesizeRequest request, CancellationToken cancellationToken)
    {
        if (!_modelLoaded)
            throw new WorkerErrorException("model_not_loaded", "No model is loaded.");

        if (!MbrolaVoices.Ids.Contains(request.VoiceId))
            throw new WorkerErrorException("unknown_voice", $"Voice '{request.VoiceId}' does not exist.");

        if (string.IsNullOrWhiteSpace(request.Text))
            return;

        byte[] pcmBytes = RenderUtterance(
            request.Text,
            request.VoiceId,
            request.Speed > 0 ? request.Speed : 1.0f,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        if (pcmBytes.Length > 0)
        {
            AudioDsp.ApplyDeClickingInPlace(pcmBytes, OutputSampleRate);
            await SendAudioChunkAsync(pcmBytes).ConfigureAwait(false);
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
            return ResamplePcm16Cubic(samples, nativeRate, OutputSampleRate);
        }
    }

    /* Unmanaged C callback receiving 16-bit mono PCM chunks from libespeak-ng / MBROLA.
       Strictly catches all exceptions so a managed exception can never unwind across the native boundary */
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int OnSynthCallback(short* wav, int numSamples, IntPtr events)
    {
        try
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
        catch
        {
            return 1;
        }
    }

    /* Resamples 16-bit mono PCM using 4-tap Catmull-Rom cubic interpolation for reduced aliasing artifacts */
    private static byte[] ResamplePcm16Cubic(ReadOnlySpan<short> input, int sourceRate, int targetRate)
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
            double t = srcPos - idx;

            double y0 = input[Math.Clamp(idx - 1, 0, lastIndex)];
            double y1 = input[Math.Clamp(idx, 0, lastIndex)];
            double y2 = input[Math.Clamp(idx + 1, 0, lastIndex)];
            double y3 = input[Math.Clamp(idx + 2, 0, lastIndex)];

            double a0 = -0.5 * y0 + 1.5 * y1 - 1.5 * y2 + 0.5 * y3;
            double a1 = y0 - 2.5 * y1 + 2.0 * y2 - 0.5 * y3;
            double a2 = -0.5 * y0 + 0.5 * y2;
            double a3 = y1;

            double value = ((a0 * t + a1) * t + a2) * t + a3;
            output[i] = (short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue);
        }

        return outputBytes;
    }
}
