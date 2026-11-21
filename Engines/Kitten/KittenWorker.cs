using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using MoonTTS.Engine.Protocol;

namespace MoonTTS.Engine.Kitten;

/* File Structure:
        - KittenWorker(Stream output)
        - GetMetadata() : EngineMetadataMessage
        - ListVoices() : List<VoiceEntry>
        - LoadModelAsync(LoadModelRequest request) : Task
        - ResolveManifestFile(LoadModelRequest request, string suffix, string fallbackFileName) : string
        - ExtractFilePrefix(string filePath, string suffix) : string
        - EnsureLanguageLoaded(string language) : void
        - CreateSessionOptions() : SessionOptions
        - WarmUpSession(InferenceSession session, Dictionary<string, float[]> voices) : void
        - SynthesizeAsync(SynthesizeRequest request, CancellationToken cancellationToken) : Task
        - ComputeTailTrimSamples(ReadOnlySpan<float> waveform, ReadOnlySpan<long> durations) : int
        - DisposeResources() : void
*/

/* Answers the Moon-TTS engine protocol with KittenTTS 2.0 Tiny multilingual ONNX synthesizers */
public sealed class KittenWorker : EngineWorkerBase
{
    private const string EngineId = "kitten";
    private const int OutputSampleRate = 24000;

    private KittenRepoConfig? _config;
    private Dictionary<string, int>? _vocab;
    private List<KittenVoiceItem> _voiceCatalog = new();
    private string _modelDirectory = string.Empty;
    private string _filePrefix = string.Empty;
    private string _weights = "int8";
    private string _defaultLanguage = "en";

    private string? _activeOnnxPath;
    private InferenceSession? _activeSession;
    private string? _activeVoicesPath;
    private Dictionary<string, float[]>? _activeVoices;

    /* Binds the worker to the protocol output stream */
    public KittenWorker(Stream output) : base(output)
    {
    }

    /* Returns Kitten 2.0 metadata and 24 kHz mono 16-bit PCM format */
    protected override EngineMetadataMessage GetMetadata() => new()
    {
        EngineId = EngineId,
        EngineName = "Kitten TTS 2.0 ONNX Synthesizer",
        Version = "2.0.0",
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

    /* Returns all supported voices for the currently loaded Kitten 2.0 model */
    protected override List<VoiceEntry> ListVoices() => KittenVoices.Describe(_voiceCatalog);

    /* Loads the model's config.json, vocab.json, and warms up the default language session */
    protected override Task LoadModelAsync(LoadModelRequest request)
    {
        string configPath = ResolveManifestFile(request, "config.json", "config.json");
        if (!File.Exists(configPath))
        {
            throw new WorkerErrorException(
                "model_not_found",
                $"Missing config JSON for model '{request.ModelId}' in '{request.ModelDirectory}'.");
        }

        string vocabPath = ResolveManifestFile(request, "vocab.json", "vocab.json");
        if (!File.Exists(vocabPath))
        {
            throw new WorkerErrorException(
                "model_not_found",
                $"Missing vocab JSON for model '{request.ModelId}' in '{request.ModelDirectory}'.");
        }

        string filePrefix = ExtractFilePrefix(configPath, "config.json");

        KittenRepoConfig? config;
        using (FileStream fs = File.OpenRead(configPath))
        {
            config = JsonSerializer.Deserialize(fs, KittenJsonContext.Default.KittenRepoConfig);
        }

        if (config == null || config.Languages.Count == 0)
        {
            throw new WorkerErrorException("model_load_failed", $"Invalid Kitten 2.0 config in '{configPath}'.");
        }

        Dictionary<string, int>? vocab;
        using (FileStream fs = File.OpenRead(vocabPath))
        {
            vocab = JsonSerializer.Deserialize(fs, KittenJsonContext.Default.DictionaryStringInt32);
        }

        if (vocab == null || vocab.Count == 0)
        {
            throw new WorkerErrorException("model_load_failed", $"Invalid Kitten 2.0 vocab in '{vocabPath}'.");
        }

        string weights = request.ModelId.Contains("int4", StringComparison.OrdinalIgnoreCase)
            ? "int4"
            : (string.IsNullOrWhiteSpace(config.DefaultWeights) ? "int8" : config.DefaultWeights);

        List<KittenVoiceItem> catalog = KittenVoices.BuildCatalog(config, request.ModelDirectory, weights, filePrefix);
        if (catalog.Count == 0)
        {
            throw new WorkerErrorException(
                "model_not_found",
                $"No usable Kitten 2.0 ONNX graphs and voice packs found in '{request.ModelDirectory}'.");
        }

        EspeakPhonemizer.EnsureInitialized();

        _activeSession?.Dispose();
        _activeSession = null;
        _activeOnnxPath = null;
        _activeVoices = null;
        _activeVoicesPath = null;

        _config = config;
        _vocab = vocab;
        _voiceCatalog = catalog;
        _modelDirectory = request.ModelDirectory;
        _filePrefix = filePrefix;
        _weights = weights;
        _defaultLanguage = !string.IsNullOrWhiteSpace(config.DefaultLanguage)
            ? config.DefaultLanguage!
            : catalog[0].Language;

        EnsureLanguageLoaded(catalog[0].Language);
        return Task.CompletedTask;
    }

    /* Resolves a model-scoped file from request.Files by suffix (e.g. "pico_config.json"), falling back to unprefixed name */
    private static string ResolveManifestFile(LoadModelRequest request, string suffix, string fallbackFileName)
    {
        string? matched = request.Files
            .FirstOrDefault(f => f.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(matched))
            return Path.Combine(request.ModelDirectory, matched);

        return Path.Combine(request.ModelDirectory, fallbackFileName);
    }

    /* Extracts the model filename prefix (e.g. "pico_" from "pico_config.json") so voices_<lang>.npz resolve per model */
    private static string ExtractFilePrefix(string filePath, string suffix)
    {
        string fileName = Path.GetFileName(filePath);
        if (fileName.Length > suffix.Length && fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return fileName[..^suffix.Length];

        return string.Empty;
    }

    /* Lazily loads the ONNX session and NPZ voice pack for the requested language, reusing shared graphs like Mini */
    private void EnsureLanguageLoaded(string language)
    {
        if (_config == null || !_config.Languages.TryGetValue(language, out KittenLanguageEntry? langEntry))
        {
            throw new WorkerErrorException("unsupported_language", $"Language '{language}' is not configured for this model.");
        }

        string onnxPath = Path.Combine(_modelDirectory, langEntry.ResolveOnnxFile(_weights));
        if (!File.Exists(onnxPath))
            throw new WorkerErrorException("model_not_found", $"Missing ONNX graph '{onnxPath}'.");

        string npzPath = Path.Combine(_modelDirectory, $"{_filePrefix}{langEntry.Voices}");
        if (!File.Exists(npzPath))
            throw new WorkerErrorException("voices_not_found", $"Missing voice archive '{npzPath}'.");

        bool sessionChanged = !string.Equals(_activeOnnxPath, onnxPath, StringComparison.OrdinalIgnoreCase) || _activeSession == null;
        bool voicesChanged = !string.Equals(_activeVoicesPath, npzPath, StringComparison.OrdinalIgnoreCase) || _activeVoices == null;

        if (voicesChanged)
        {
            _activeVoices = NpzVoiceLoader.Load(npzPath);
            _activeVoicesPath = npzPath;
        }

        if (sessionChanged)
        {
            using SessionOptions options = CreateSessionOptions();
            var session = new InferenceSession(onnxPath, options);
            WarmUpSession(session, _activeVoices!);

            _activeSession?.Dispose();
            _activeSession = session;
            _activeOnnxPath = onnxPath;
        }
    }

    /* Creates tuned CPU ONNX Runtime session options for low-latency synthesis */
    private static SessionOptions CreateSessionOptions()
    {
        int intraThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
        return new SessionOptions
        {
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            EnableCpuMemArena = true,
            EnableMemoryPattern = true,
            InterOpNumThreads = 1,
            IntraOpNumThreads = intraThreads
        };
    }

    /* Executes a tiny 3-token warm-up pass so ONNX Runtime pre-allocates its memory arena at load time */
    private static void WarmUpSession(InferenceSession session, Dictionary<string, float[]> voices)
    {
        try
        {
            float[] styleVector = NpzVoiceLoader.GetStyleVector(voices, voices.Keys.FirstOrDefault() ?? string.Empty, 3);
            var inputIdsTensor = new DenseTensor<long>(new long[] { 0, 10, 0 }, new[] { 1, 3 });
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

    /* Phonemizes text, runs Kitten 2.0 ONNX inference, trims via durations, and streams de-clicked PCM bytes */
    protected override async Task SynthesizeAsync(SynthesizeRequest request, CancellationToken cancellationToken)
    {
        if (_config == null || _vocab == null || _voiceCatalog.Count == 0)
            throw new WorkerErrorException("model_not_loaded", "No model is loaded.");

        if (string.IsNullOrWhiteSpace(request.Text))
            return;

        cancellationToken.ThrowIfCancellationRequested();

        KittenVoiceItem? voice = KittenVoices.ResolveVoice(_voiceCatalog, request.VoiceId, _defaultLanguage);
        if (voice == null)
            throw new WorkerErrorException("unknown_voice", $"Voice '{request.VoiceId}' is not available.");

        EnsureLanguageLoaded(voice.Language);

        if (_activeSession == null || _activeVoices == null)
            throw new WorkerErrorException("model_not_loaded", "Model session could not be initialized.");

        float effectiveSpeed = request.Speed > 0 ? request.Speed : 1.0f;
        string phonemes = EspeakPhonemizer.Phonemize(request.Text, voice.Language, _config.Phonemizer);
        long[] tokens = EspeakPhonemizer.Tokenize(phonemes, _vocab);

        var inputIdsTensor = new DenseTensor<long>(tokens, new[] { 1, tokens.Length });
        float[] styleVector = NpzVoiceLoader.GetStyleVector(_activeVoices, voice.VoiceKey, tokens.Length);
        var styleTensor = new DenseTensor<float>(styleVector, new[] { 1, 256 });
        var speedTensor = new DenseTensor<float>(new[] { effectiveSpeed }, new[] { 1 });

        var inputs = new NamedOnnxValue[]
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            NamedOnnxValue.CreateFromTensor("style", styleTensor),
            NamedOnnxValue.CreateFromTensor("speed", speedTensor)
        };

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _activeSession.Run(inputs);
        cancellationToken.ThrowIfCancellationRequested();

        Tensor<float> outputTensor = results[0].AsTensor<float>();
        ReadOnlySpan<float> allSamples = outputTensor is DenseTensor<float> denseWav
            ? denseWav.Buffer.Span
            : outputTensor.ToArray();

        int trailSamples = 0;
        if (results.Count > 1)
        {
            Tensor<long> durTensor = results[1].AsTensor<long>();
            ReadOnlySpan<long> durations = durTensor is DenseTensor<long> denseDur
                ? denseDur.Buffer.Span
                : durTensor.ToArray();
            trailSamples = ComputeTailTrimSamples(allSamples, durations);
        }

        int validLength = allSamples.Length - trailSamples;
        ReadOnlySpan<float> validSamples = validLength > 0
            ? allSamples.Slice(0, validLength)
            : allSamples;

        if (!validSamples.IsEmpty)
        {
            byte[] pcmBytes = new byte[validSamples.Length * sizeof(short)];
            AudioDsp.ConvertFloatToPcm16(validSamples, pcmBytes);
            AudioDsp.ApplyDeClickingInPlace(pcmBytes, OutputSampleRate);
            await SendAudioChunkAsync(pcmBytes).ConfigureAwait(false);
        }
    }

    /* Computes exact trailing end-token samples to trim from the ONNX durations output tensor */
    private static int ComputeTailTrimSamples(ReadOnlySpan<float> waveform, ReadOnlySpan<long> durations)
    {
        if (waveform.IsEmpty || durations.IsEmpty)
            return 0;

        long totalFrames = 0;
        for (int i = 0; i < durations.Length; i++)
            totalFrames += durations[i];

        int spf = Math.Max(1, (int)Math.Round((double)waveform.Length / Math.Max(totalFrames, 1)));
        int trail = Math.Max(0, (int)durations[^1] - 1) * spf;
        return trail + spf < waveform.Length ? trail : 0;
    }

    /* Releases ONNX runtime session */
    protected override void DisposeResources()
    {
        _activeSession?.Dispose();
        _activeSession = null;
        _activeOnnxPath = null;
        _activeVoices = null;
        _activeVoicesPath = null;
    }
}
