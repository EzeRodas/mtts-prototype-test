using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using MoonTTS.Engine.Protocol;

namespace MoonTTS.Engine.Kitten;

/* File Structure:
        - KittenWorker(Stream output)
        - HandleAsync(EngineFrame frame) : Task<bool>
        - WaitForIdleAsync() : Task
        - HandleLoadModelAsync(EngineFrame frame) : Task
        - WarmUpSession(InferenceSession session, Dictionary<string, float[]> voices) : void
        - StartSynthesis(EngineFrame frame) : Task
        - SynthesizeAsync(SynthesizeRequest request, CancellationToken cancellationToken) : Task
        - SendAsync(EngineOpcode opcode, ReadOnlyMemory<byte> payload) : Task
        - SendJsonAsync<T>(EngineOpcode opcode, T message, JsonTypeInfo<T> typeInfo) : Task
        - SendErrorAsync(string code, string message) : Task
        - Dispose() : void
*/

/* Answers the Moon-TTS engine protocol with the lightweight KittenTTS ONNX synthesizer.
   Synthesis runs on a background task so a Cancel frame can interrupt active speech immediately */
public sealed class KittenWorker : IDisposable
{
    private const string EngineId = "kitten";
    private const int OutputSampleRate = 24000;
    private const int TrimTailSamples = 5000;

    private readonly Stream _output;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private InferenceSession? _session;
    private Dictionary<string, float[]>? _voices;
    private string _currentModelId = string.Empty;

    private Task? _activeSynthesis;
    private CancellationTokenSource? _activeCts;

    /* Binds the worker to the protocol output stream */
    public KittenWorker(Stream output)
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
                    EngineName = "Kitten TTS ONNX Synthesizer",
                    Version = "0.8.0",
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
                    new VoicesResponse { Voices = KittenVoices.Describe() },
                    EngineProtocolJsonContext.Default.VoicesResponse);
                return true;

            case EngineOpcode.Synthesize:
                await StartSynthesis(frame);
                return true;

            case EngineOpcode.Cancel:
                /* Cancel has no direct reply: active synthesis ends with EndOfStream */
                _activeCts?.Cancel();
                return true;

            case EngineOpcode.Shutdown:
                return false;

            default:
                await SendErrorAsync("unsupported_opcode", $"Opcode {frame.Opcode} is not supported.");
                return true;
        }
    }

    /* Waits for running synthesis (if any) so its terminal frame is written before exiting */
    public async Task WaitForIdleAsync()
    {
        _activeCts?.Cancel();
        if (_activeSynthesis != null)
            await _activeSynthesis;
    }

    /* Loads the ONNX model and the voices.npz archive from the model directory */
    private async Task HandleLoadModelAsync(EngineFrame frame)
    {
        try
        {
            var request = EngineIpcProtocol.ReadJson(frame, EngineProtocolJsonContext.Default.LoadModelRequest);

            string? onnxFile = request.Files
                .Where(f => f.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
                .Select(f => Path.Combine(request.ModelDirectory, f))
                .FirstOrDefault(File.Exists);

            onnxFile ??= Directory.EnumerateFiles(request.ModelDirectory, "*.onnx").FirstOrDefault();

            if (onnxFile == null)
            {
                await SendErrorAsync("model_not_found",
                    $"No .onnx file found for model '{request.ModelId}' in '{request.ModelDirectory}'.");
                return;
            }

            string? npzFile = request.Files
                .Where(f => f.EndsWith(".npz", StringComparison.OrdinalIgnoreCase))
                .Select(f => Path.Combine(request.ModelDirectory, f))
                .FirstOrDefault(File.Exists);

            npzFile ??= Directory.EnumerateFiles(request.ModelDirectory, "*.npz").FirstOrDefault();

            if (npzFile == null)
            {
                await SendErrorAsync("voices_not_found",
                    $"No voices .npz file found for model '{request.ModelId}' in '{request.ModelDirectory}'.");
                return;
            }

            int intraThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
            using var options = new SessionOptions
            {
                LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                EnableCpuMemArena = true,
                EnableMemoryPattern = true,
                InterOpNumThreads = 1,
                IntraOpNumThreads = intraThreads
            };

            var session = new InferenceSession(onnxFile, options);
            var voices = NpzVoiceLoader.Load(npzFile);

            EspeakPhonemizer.EnsureInitialized();
            WarmUpSession(session, voices);

            _session?.Dispose();
            _session = session;
            _voices = voices;
            _currentModelId = request.ModelId;

            await SendAsync(EngineOpcode.ModelLoaded, ReadOnlyMemory<byte>.Empty);
        }
        catch (Exception ex)
        {
            await SendErrorAsync("model_load_failed", ex.Message);
        }
    }

    /* Executes a tiny 3-token warm-up pass so ONNX Runtime pre-allocates its memory arena at load time */
    private static void WarmUpSession(InferenceSession session, Dictionary<string, float[]> voices)
    {
        try
        {
            float[] styleVector = NpzVoiceLoader.GetStyleVector(voices, voices.Keys.FirstOrDefault() ?? string.Empty, 1);
            var inputIdsTensor = new DenseTensor<long>(new long[] { 0, 4, 0 }, new[] { 1, 3 });
            var styleTensor = new DenseTensor<float>(styleVector, new[] { 1, 256 });
            var speedTensor = new DenseTensor<float>(new[] { 1.0f }, new[] { 1 });

            var inputs = new NamedOnnxValue[]
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
                NamedOnnxValue.CreateFromTensor("style", styleTensor),
                NamedOnnxValue.CreateFromTensor("speed", speedTensor)
            };

            using var _ = session.Run(inputs);
        }
        catch
        {
            /* Warm-up failure is non-fatal; actual synthesis will report real session errors */
        }
    }

    /* Starts a synthesis job on a background task */
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

    /* Phonemizes text, constructs ONNX tensors, executes inference, and streams de-clicked PCM bytes */
    private async Task SynthesizeAsync(SynthesizeRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (_session == null || _voices == null)
            {
                await SendErrorAsync("model_not_loaded", "No model is loaded.");
                return;
            }

            if (!string.IsNullOrWhiteSpace(request.Text))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string voiceKey = KittenVoices.ResolveVoiceKey(request.VoiceId);
                float speedMultiplier = KittenVoices.GetSpeedMultiplier(_currentModelId, voiceKey);
                float effectiveSpeed = (request.Speed > 0 ? request.Speed : 1.0f) * speedMultiplier;

                string phonemes = EspeakPhonemizer.Phonemize(request.Text);
                long[] tokens = EspeakPhonemizer.Tokenize(phonemes);

                var inputIdsTensor = new DenseTensor<long>(tokens, new[] { 1, tokens.Length });

                float[] styleVector = NpzVoiceLoader.GetStyleVector(_voices, voiceKey, request.Text.Length);
                var styleTensor = new DenseTensor<float>(styleVector, new[] { 1, 256 });

                var speedTensor = new DenseTensor<float>(new[] { effectiveSpeed }, new[] { 1 });

                var inputs = new NamedOnnxValue[]
                {
                    NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
                    NamedOnnxValue.CreateFromTensor("style", styleTensor),
                    NamedOnnxValue.CreateFromTensor("speed", speedTensor)
                };

                using var results = _session.Run(inputs);
                cancellationToken.ThrowIfCancellationRequested();

                Tensor<float> outputTensor = results.First().AsTensor<float>();
                ReadOnlySpan<float> allSamples = outputTensor is DenseTensor<float> dense
                    ? dense.Buffer.Span
                    : outputTensor.ToArray();

                /* Trim trailing artifact samples (matching outputs[0][..., :-5000]) */
                int trimmedLength = Math.Max(0, allSamples.Length - TrimTailSamples);
                ReadOnlySpan<float> validSamples = trimmedLength > 0
                    ? allSamples.Slice(0, trimmedLength)
                    : allSamples;

                if (!validSamples.IsEmpty)
                {
                    byte[] pcmBytes = new byte[validSamples.Length * sizeof(short)];
                    AudioDsp.ConvertFloatToPcm16(validSamples, pcmBytes);
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
            Console.Error.WriteLine($"[KittenWorker] Synthesis exception: {ex}");
            await SendErrorAsync("synthesis_failed", ex.Message);
        }
    }

    /* Writes one protocol frame to stdout under the write lock */
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

    /* Serializes and writes a JSON protocol frame */
    private Task SendJsonAsync<T>(EngineOpcode opcode, T message, JsonTypeInfo<T> typeInfo) =>
        SendAsync(opcode, JsonSerializer.SerializeToUtf8Bytes(message, typeInfo));

    /* Writes the terminal Error frame of a failed request */
    private Task SendErrorAsync(string code, string message) =>
        SendJsonAsync(EngineOpcode.Error, new EngineErrorMessage { Code = code, Message = message },
            EngineProtocolJsonContext.Default.EngineErrorMessage);

    /* Releases ONNX runtime and cancellation resources */
    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
        _activeCts?.Dispose();
        _writeLock.Dispose();
    }
}
