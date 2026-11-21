using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using Microsoft.ML.OnnxRuntime;
using MoonTTS.Engine.Protocol;

namespace MoonTTS.Engine.Kokoro;

/* File Structure:
        - KokoroWorker(Stream output)
        - GetMetadata() : EngineMetadataMessage
        - ListVoices() : List<VoiceEntry>
        - LoadModelAsync(LoadModelRequest request) : Task
        - WarmUpSynthesizer(KokoroWavSynthesizer synthesizer) : void
        - SynthesizeAsync(SynthesizeRequest request, CancellationToken cancellationToken) : Task
        - DisposeResources() : void
*/

/* Answers the Moon-TTS engine protocol with the Kokoro ONNX synthesizer */
public sealed class KokoroWorker : EngineWorkerBase
{
    private const string EngineId = "kokoro";
    private const int SampleRate = 24000;

    /* Serializes underlying KokoroSharp inference so an abandoned WaitAsync never overlaps the next synthesis */
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);
    private KokoroWavSynthesizer? _synthesizer;

    /* Binds the worker to the protocol output stream */
    public KokoroWorker(Stream output) : base(output)
    {
    }

    /* Returns Kokoro metadata and 24 kHz mono 16-bit PCM format */
    protected override EngineMetadataMessage GetMetadata() => new()
    {
        EngineId = EngineId,
        EngineName = "Kokoro ONNX Neural Synthesizer",
        Version = "1.0.0",
        Author = "Moon-TTS",
        AudioFormat = new EngineAudioFormat { SampleRate = SampleRate, Channels = 1, BitDepth = 16 },
        SupportsStreaming = false,
        SupportsSpeedAdjustment = true,
        SupportsPitchAdjustment = false
    };

    /* Returns all supported Kokoro voice presets */
    protected override List<VoiceEntry> ListVoices() => KokoroVoices.Describe();

    /* Loads the ONNX model listed in the request and warms up its CPU memory arena */
    protected override async Task LoadModelAsync(LoadModelRequest request)
    {
        string? modelFile = request.Files
            .Where(f => f.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.Combine(request.ModelDirectory, f))
            .FirstOrDefault(File.Exists);

        if (modelFile == null)
        {
            throw new WorkerErrorException(
                "model_not_found",
                $"No .onnx file of model '{request.ModelId}' was found in '{request.ModelDirectory}'.");
        }

        /* Suppress constant-folding warnings and configure optimal single-utterance CPU execution */
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

        var synthesizer = KokoroWavSynthesizer.LoadModel(modelFile, options);
        await WarmUpSynthesizerAsync(synthesizer).ConfigureAwait(false);

        await _inferenceGate.WaitAsync().ConfigureAwait(false);
        try
        {
            (_synthesizer as IDisposable)?.Dispose();
            _synthesizer = synthesizer;
        }
        finally
        {
            _inferenceGate.Release();
        }
    }

    /* Runs a tiny warm-up synthesis pass so ONNX Runtime pre-allocates its memory arena at load time */
    private static async Task WarmUpSynthesizerAsync(KokoroWavSynthesizer synthesizer)
    {
        try
        {
            string? firstVoiceId = KokoroVoices.Ids.FirstOrDefault();
            if (string.IsNullOrEmpty(firstVoiceId))
                return;

            var voice = KokoroVoiceManager.GetVoice(firstVoiceId);
            var cfg = new KokoroTTSPipelineConfig { Speed = 1.0f };
            _ = await synthesizer.SynthesizeAsync("Hi.", voice, cfg).ConfigureAwait(false);
        }
        catch
        {
            /* Warm-up failure is non-fatal */
        }
    }

    /* Renders the text with the requested voice and streams it back as one de-clicked PCM chunk */
    protected override async Task SynthesizeAsync(SynthesizeRequest request, CancellationToken cancellationToken)
    {
        if (_synthesizer == null)
            throw new WorkerErrorException("model_not_loaded", "No model is loaded.");

        if (!KokoroVoices.Ids.Contains(request.VoiceId))
            throw new WorkerErrorException("unknown_voice", $"Voice '{request.VoiceId}' does not exist.");

        if (string.IsNullOrWhiteSpace(request.Text))
            return;

        await _inferenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var voice = KokoroVoiceManager.GetVoice(request.VoiceId);
            var pipelineConfig = new KokoroTTSPipelineConfig { Speed = request.Speed > 0 ? request.Speed : 1.0f };

            Task<byte[]> synthTask = _synthesizer.SynthesizeAsync(request.Text, voice, pipelineConfig);
            byte[] rawPcmBytes;
            try
            {
                rawPcmBytes = await synthTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                /* Keep _inferenceGate held until the underlying ONNX call finishes on a background continuation */
                _ = synthTask.ContinueWith(_ => _inferenceGate.Release(), TaskScheduler.Default);
                throw;
            }

            cancellationToken.ThrowIfCancellationRequested();
            AudioDsp.ApplyDeClickingInPlace(rawPcmBytes, SampleRate);
            await SendAudioChunkAsync(rawPcmBytes).ConfigureAwait(false);
            _inferenceGate.Release();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            _inferenceGate.Release();
            throw;
        }
    }

    /* Releases the ONNX session and inference gate */
    protected override void DisposeResources()
    {
        (_synthesizer as IDisposable)?.Dispose();
        _synthesizer = null;
        _inferenceGate.Dispose();
    }
}
