using System.Text.Json;
using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using Microsoft.ML.OnnxRuntime;
using MoonTTS.Engine.Protocol;

namespace MoonTTS.Engine.Kokoro;

/* File Structure:
        - KokoroWorker(Stream output)
        - HandleAsync(EngineFrame frame) : Task<bool>
        - WaitForIdleAsync() : Task
        - HandleLoadModelAsync(EngineFrame frame) : Task
        - StartSynthesis(EngineFrame frame) : Task
        - SynthesizeAsync(SynthesizeRequest request, CancellationToken cancellationToken) : Task
        - SendAsync(EngineOpcode opcode, ReadOnlyMemory<byte> payload) : Task
        - SendJsonAsync<T>(EngineOpcode opcode, T message, JsonTypeInfo<T> typeInfo) : Task
        - SendErrorAsync(string code, string message) : Task
        - Dispose() : void
*/

/* Answers the Moon-TTS engine protocol with the Kokoro ONNX synthesizer.
   Synthesis runs in the background so a Cancel frame can be read while it is in progress */
public sealed class KokoroWorker : IDisposable
{
    private const string EngineId = "kokoro";
    private const int SampleRate = 24000;

    private readonly Stream _output;

    /* Frames from the synthesis task and the request loop must never interleave on stdout */
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private KokoroWavSynthesizer? _synthesizer;
    private Task? _activeSynthesis;
    private CancellationTokenSource? _activeCts;

    /* Binds the worker to the protocol output stream */
    public KokoroWorker(Stream output)
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
                    EngineName = "Kokoro ONNX Neural Synthesizer",
                    Version = "1.0.0",
                    Author = "Moon-TTS",
                    AudioFormat = new EngineAudioFormat { SampleRate = SampleRate, Channels = 1, BitDepth = 16 },
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
                    new VoicesResponse { Voices = KokoroVoices.Describe() },
                    EngineProtocolJsonContext.Default.VoicesResponse);
                return true;

            case EngineOpcode.Synthesize:
                await StartSynthesis(frame);
                return true;

            case EngineOpcode.Cancel:
                /* Cancel has no direct reply: the active synthesis ends with EndOfStream */
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

    /* Loads the ONNX model listed in the request from the host's model directory */
    private async Task HandleLoadModelAsync(EngineFrame frame)
    {
        try
        {
            var request = EngineIpcProtocol.ReadJson(frame, EngineProtocolJsonContext.Default.LoadModelRequest);
            string? modelFile = request.Files
                .Where(f => f.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
                .Select(f => Path.Combine(request.ModelDirectory, f))
                .FirstOrDefault(File.Exists);

            if (modelFile == null)
            {
                await SendErrorAsync("model_not_found",
                    $"No .onnx file of model '{request.ModelId}' was found in '{request.ModelDirectory}'.");
                return;
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

            (_synthesizer as IDisposable)?.Dispose();
            _synthesizer = synthesizer;
            await SendAsync(EngineOpcode.ModelLoaded, ReadOnlyMemory<byte>.Empty);
        }
        catch (Exception ex)
        {
            await SendErrorAsync("model_load_failed", ex.Message);
        }
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

    /* Renders the text with the requested voice and streams it back as one de-clicked PCM chunk */
    private async Task SynthesizeAsync(SynthesizeRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (_synthesizer == null)
            {
                await SendErrorAsync("model_not_loaded", "No model is loaded.");
                return;
            }
            if (!KokoroVoices.Ids.Contains(request.VoiceId))
            {
                await SendErrorAsync("unknown_voice", $"Voice '{request.VoiceId}' does not exist.");
                return;
            }

            if (!string.IsNullOrWhiteSpace(request.Text))
            {
                var voice = KokoroVoiceManager.GetVoice(request.VoiceId);
                var pipelineConfig = new KokoroTTSPipelineConfig { Speed = request.Speed > 0 ? request.Speed : 1.0f };

                byte[] rawPcmBytes = await _synthesizer.SynthesizeAsync(request.Text, voice, pipelineConfig).WaitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                AudioDsp.ApplyDeClickingInPlace(rawPcmBytes, SampleRate);
                await SendAsync(EngineOpcode.AudioChunk, rawPcmBytes);
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
    private Task SendJsonAsync<T>(EngineOpcode opcode, T message, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        SendAsync(opcode, JsonSerializer.SerializeToUtf8Bytes(message, typeInfo));

    /* Writes the terminal Error frame of a failed request */
    private Task SendErrorAsync(string code, string message) =>
        SendJsonAsync(EngineOpcode.Error, new EngineErrorMessage { Code = code, Message = message },
            EngineProtocolJsonContext.Default.EngineErrorMessage);

    /* Releases the ONNX session */
    public void Dispose()
    {
        (_synthesizer as IDisposable)?.Dispose();
        _synthesizer = null;
        _activeCts?.Dispose();
        _writeLock.Dispose();
    }
}
