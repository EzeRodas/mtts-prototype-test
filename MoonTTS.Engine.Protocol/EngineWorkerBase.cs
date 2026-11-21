using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace MoonTTS.Engine.Protocol;

/* File Structure:
        - EngineWorkerBase(Stream output)
        - HandleAsync(EngineFrame frame) : Task<bool>
        - WaitForIdleAsync() : Task
        - GetMetadata() : EngineMetadataMessage
        - ListVoices() : List<VoiceEntry>
        - LoadModelAsync(LoadModelRequest request) : Task
        - SynthesizeAsync(SynthesizeRequest request, CancellationToken cancellationToken) : Task
        - HandleLoadModelFrameAsync(EngineFrame frame) : Task
        - StartSynthesis(EngineFrame frame) : Task
        - RunSynthesisAsync(SynthesizeRequest request, CancellationToken cancellationToken) : Task
        - SendAsync(EngineOpcode opcode, ReadOnlyMemory<byte> payload) : Task
        - SendAudioChunkAsync(ReadOnlyMemory<byte> pcmBytes) : Task
        - SendJsonAsync<T>(EngineOpcode opcode, T message, JsonTypeInfo<T> typeInfo) : Task
        - SendErrorAsync(string code, string message) : Task
        - DisposeResources() : void
        - Dispose() : void
        - EngineWorkerException(string code, string message)
        - EngineWorkerRunner
            - RunAsync<TWorker>(Func<Stream, TWorker> factory) : Task<int>
*/

/* Structured worker-side error carrying a protocol error code and human-readable message */
public sealed class WorkerErrorException : Exception
{
    public string Code { get; }

    /* Initializes a worker error with protocol code and message */
    public WorkerErrorException(string code, string message) : base(message)
    {
        Code = code;
    }
}

/* Shared protocol host base class for out-of-process C# TTS engine workers.
   Owns binary frame dispatch, serialized stdout writes, background synthesis scheduling, and cancellation */
public abstract class EngineWorkerBase : IDisposable
{
    private readonly Stream _output;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Lock _ctsLock = new();

    private Task? _activeSynthesis;
    private CancellationTokenSource? _activeCts;
    private bool _disposed;

    /* Binds the worker to the protocol stdout stream */
    protected EngineWorkerBase(Stream output)
    {
        _output = output;
    }

    /* Returns the static metadata describing this engine and its PCM output geometry */
    protected abstract EngineMetadataMessage GetMetadata();

    /* Returns the voice presets offered by the currently loaded model */
    protected abstract List<VoiceEntry> ListVoices();

    /* Loads the requested model files from disk; throws WorkerErrorException on expected domain errors */
    protected abstract Task LoadModelAsync(LoadModelRequest request);

    /* Synthesizes the requested utterance and emits PCM via SendAudioChunkAsync */
    protected abstract Task SynthesizeAsync(SynthesizeRequest request, CancellationToken cancellationToken);

    /* Dispatches one decoded protocol frame. Returns false when Shutdown is received */
    public async Task<bool> HandleAsync(EngineFrame frame)
    {
        switch (frame.Opcode)
        {
            case EngineOpcode.Ping:
                await SendAsync(EngineOpcode.Pong, ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);
                return true;

            case EngineOpcode.GetMetadata:
                await SendJsonAsync(
                    EngineOpcode.MetadataResponse,
                    GetMetadata(),
                    EngineProtocolJsonContext.Default.EngineMetadataMessage).ConfigureAwait(false);
                return true;

            case EngineOpcode.LoadModel:
                await HandleLoadModelFrameAsync(frame).ConfigureAwait(false);
                return true;

            case EngineOpcode.ListVoices:
                await SendJsonAsync(
                    EngineOpcode.VoicesResponse,
                    new VoicesResponse { Voices = ListVoices() },
                    EngineProtocolJsonContext.Default.VoicesResponse).ConfigureAwait(false);
                return true;

            case EngineOpcode.Synthesize:
                await StartSynthesis(frame).ConfigureAwait(false);
                return true;

            case EngineOpcode.Cancel:
                /* Cancel has no direct reply: active synthesis concludes with EndOfStream */
                lock (_ctsLock)
                {
                    _activeCts?.Cancel();
                }
                return true;

            case EngineOpcode.Shutdown:
                return false;

            default:
                await SendErrorAsync("unsupported_opcode", $"Opcode {frame.Opcode} is not supported.")
                    .ConfigureAwait(false);
                return true;
        }
    }

    /* Cancels any active synthesis and awaits completion so its terminal frame is written cleanly */
    public async Task WaitForIdleAsync()
    {
        lock (_ctsLock)
        {
            _activeCts?.Cancel();
        }
        if (_activeSynthesis != null)
            await _activeSynthesis.ConfigureAwait(false);
    }

    /* Decodes a LoadModel frame and delegates to the derived worker */
    private async Task HandleLoadModelFrameAsync(EngineFrame frame)
    {
        try
        {
            var request = EngineIpcProtocol.ReadJson(frame, EngineProtocolJsonContext.Default.LoadModelRequest);
            await LoadModelAsync(request).ConfigureAwait(false);
            await SendAsync(EngineOpcode.ModelLoaded, ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);
        }
        catch (WorkerErrorException ex)
        {
            await SendErrorAsync(ex.Code, ex.Message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await SendErrorAsync("model_load_failed", ex.Message).ConfigureAwait(false);
        }
    }

    /* Starts a synthesis job in the background; rejects overlapping requests while one is active */
    private async Task StartSynthesis(EngineFrame frame)
    {
        if (_activeSynthesis is { IsCompleted: false })
        {
            await SendErrorAsync("busy", "A synthesis job is already running.").ConfigureAwait(false);
            return;
        }

        SynthesizeRequest request;
        try
        {
            request = EngineIpcProtocol.ReadJson(frame, EngineProtocolJsonContext.Default.SynthesizeRequest);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            await SendErrorAsync("invalid_request", ex.Message).ConfigureAwait(false);
            return;
        }

        var cts = new CancellationTokenSource();
        lock (_ctsLock)
        {
            _activeCts = cts;
        }
        var token = cts.Token;
        _activeSynthesis = Task.Run(() => RunSynthesisAsync(request, cts, token));
    }

    /* Executes synthesis, emits terminal EndOfStream or Error frame, and disposes its own CTS */
    private async Task RunSynthesisAsync(
        SynthesizeRequest request,
        CancellationTokenSource cts,
        CancellationToken cancellationToken)
    {
        try
        {
            await SynthesizeAsync(request, cancellationToken).ConfigureAwait(false);
            await SendAsync(EngineOpcode.EndOfStream, ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await SendAsync(EngineOpcode.EndOfStream, ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);
        }
        catch (WorkerErrorException ex)
        {
            await SendErrorAsync(ex.Code, ex.Message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{GetType().Name}] Synthesis exception: {ex}");
            await SendErrorAsync("synthesis_failed", ex.Message).ConfigureAwait(false);
        }
        finally
        {
            lock (_ctsLock)
            {
                if (ReferenceEquals(_activeCts, cts))
                    _activeCts = null;
            }
            cts.Dispose();
        }
    }

    /* Sends a raw PCM audio chunk frame to the host */
    protected Task SendAudioChunkAsync(ReadOnlyMemory<byte> pcmBytes) =>
        pcmBytes.IsEmpty ? Task.CompletedTask : SendAsync(EngineOpcode.AudioChunk, pcmBytes);

    /* Writes one protocol frame to stdout under the write lock */
    protected async Task SendAsync(EngineOpcode opcode, ReadOnlyMemory<byte> payload)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await EngineIpcProtocol.WriteFrameAsync(_output, opcode, payload).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /* Serializes and writes a JSON control frame */
    protected Task SendJsonAsync<T>(EngineOpcode opcode, T message, JsonTypeInfo<T> typeInfo) =>
        SendAsync(opcode, JsonSerializer.SerializeToUtf8Bytes(message, typeInfo));

    /* Writes the terminal Error frame of a failed request */
    protected Task SendErrorAsync(string code, string message) =>
        SendJsonAsync(
            EngineOpcode.Error,
            new EngineErrorMessage { Code = code, Message = message },
            EngineProtocolJsonContext.Default.EngineErrorMessage);

    /* Releases derived worker resources */
    protected virtual void DisposeResources()
    {
    }

    /* Releases derived resources and synchronization primitives */
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        lock (_ctsLock)
        {
            _activeCts?.Cancel();
            _activeCts = null;
        }
        DisposeResources();
        _writeLock.Dispose();
        GC.SuppressFinalize(this);
    }
}

/* Standard stdin/stdout protocol runner shared by all C# engine worker entrypoints */
public static class EngineWorkerRunner
{
    /* Redirects Console.Out to stderr and pumps frames until Shutdown or stdin EOF */
    public static async Task<int> RunAsync<TWorker>(Func<Stream, TWorker> factory)
        where TWorker : EngineWorkerBase
    {
        Stream input = Console.OpenStandardInput();
        Stream output = Console.OpenStandardOutput();
        Console.SetOut(Console.Error);

        using TWorker worker = factory(output);
        try
        {
            while (await EngineIpcProtocol.ReadFrameAsync(input).ConfigureAwait(false) is { } frame)
            {
                if (!await worker.HandleAsync(frame).ConfigureAwait(false))
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            Console.Error.WriteLine($"Protocol channel closed: {ex.Message}");
        }
        finally
        {
            await worker.WaitForIdleAsync().ConfigureAwait(false);
        }

        return 0;
    }
}
