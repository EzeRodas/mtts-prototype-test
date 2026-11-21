using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using MoonTTS.Engine.Protocol;

namespace MoonTTS.Engine.Kitten;

/* File Structure:
        - KittenWorker(Stream output)
        - GetMetadata() : EngineMetadataMessage
        - ListVoices() : List<VoiceEntry>
        - LoadModelAsync(LoadModelRequest request) : Task
        - WarmUpSession(InferenceSession session, Dictionary<string, float[]> voices) : void
        - SynthesizeAsync(SynthesizeRequest request, CancellationToken cancellationToken) : Task
        - DisposeResources() : void
*/

/* Answers the Moon-TTS engine protocol with the lightweight KittenTTS ONNX synthesizer */
public sealed class KittenWorker : EngineWorkerBase
{
    private const string EngineId = "kitten";
    private const int OutputSampleRate = 24000;
    private const int TrimTailSamples = 5000;

    private InferenceSession? _session;
    private Dictionary<string, float[]>? _voices;
    private string _currentModelId = string.Empty;

    /* Binds the worker to the protocol output stream */
    public KittenWorker(Stream output) : base(output)
    {
    }

    /* Returns Kitten metadata and 24 kHz mono 16-bit PCM format */
    protected override EngineMetadataMessage GetMetadata() => new()
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
    };

    /* Returns all supported Kitten voice presets */
    protected override List<VoiceEntry> ListVoices() => KittenVoices.Describe();

    /* Loads the ONNX model and the voices.npz archive from the model directory */
    protected override Task LoadModelAsync(LoadModelRequest request)
    {
        string? onnxFile = request.Files
            .Where(f => f.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.Combine(request.ModelDirectory, f))
            .FirstOrDefault(File.Exists);

        onnxFile ??= Directory.EnumerateFiles(request.ModelDirectory, "*.onnx").FirstOrDefault();

        if (onnxFile == null)
        {
            throw new WorkerErrorException(
                "model_not_found",
                $"No .onnx file found for model '{request.ModelId}' in '{request.ModelDirectory}'.");
        }

        string? npzFile = request.Files
            .Where(f => f.EndsWith(".npz", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.Combine(request.ModelDirectory, f))
            .FirstOrDefault(File.Exists);

        npzFile ??= Directory.EnumerateFiles(request.ModelDirectory, "*.npz").FirstOrDefault();

        if (npzFile == null)
        {
            throw new WorkerErrorException(
                "voices_not_found",
                $"No voices .npz file found for model '{request.ModelId}' in '{request.ModelDirectory}'.");
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
        return Task.CompletedTask;
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

    /* Phonemizes text, constructs ONNX tensors, executes inference, and streams de-clicked PCM bytes */
    protected override async Task SynthesizeAsync(SynthesizeRequest request, CancellationToken cancellationToken)
    {
        if (_session == null || _voices == null)
            throw new WorkerErrorException("model_not_loaded", "No model is loaded.");

        if (string.IsNullOrWhiteSpace(request.Text))
            return;

        cancellationToken.ThrowIfCancellationRequested();

        string voiceKey = KittenVoices.ResolveVoiceKey(request.VoiceId);
        float speedMultiplier = KittenVoices.GetSpeedMultiplier(_currentModelId, voiceKey);
        float effectiveSpeed = (request.Speed > 0 ? request.Speed : 1.0f) * speedMultiplier;

        string phonemes = EspeakPhonemizer.Phonemize(request.Text);
        long[] tokens = EspeakPhonemizer.Tokenize(phonemes);

        var inputIdsTensor = new DenseTensor<long>(tokens, new[] { 1, tokens.Length });

        /* Index StyleTTS2 voice style embedding by phoneme token count (matching KittenTTS inference semantics) */
        float[] styleVector = NpzVoiceLoader.GetStyleVector(_voices, voiceKey, tokens.Length);
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
            await SendAudioChunkAsync(pcmBytes).ConfigureAwait(false);
        }
    }

    /* Releases ONNX runtime session */
    protected override void DisposeResources()
    {
        _session?.Dispose();
        _session = null;
    }
}
