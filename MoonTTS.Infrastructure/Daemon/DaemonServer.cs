using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using MoonTTS.Core.Services;
using MoonTTS.Core.Utilities;
using MoonTTS.Domain.Interfaces;
using MoonTTS.Infrastructure.Daemon.DTOs;
using MoonTTS.Infrastructure.Persistence;

namespace MoonTTS.Infrastructure.Daemon;

/* File Structure:
        - DaemonServer(
            ITtsEngineProvider engineProvider, 
            DictionaryManager dictionaryManager, 
            SettingsManager settingsManager)
        - RunAsync(CancellationToken cancellationToken) : Task
        - HandleClientAsync(NamedPipeServerStream stream, CancellationToken cancellationToken) : Task
        - CancelActiveSynthesis() : void
        - HandleSynthesizeAsync(Stream stream, DaemonRequest request, CancellationToken cancellationToken) : Task 
*/

public class DaemonServer
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);
    
    private readonly ITtsEngineProvider _engineProvider;
    private readonly DictionaryManager _dictionaryManager;
    private readonly SettingsManager _settingsManager;

    private readonly Lock _synthLock = new();
    private readonly SemaphoreSlim _engineLock = new(1, 1);
    private CancellationTokenSource? _activeSynthesisCts;

    private long _lastActivityTick = Environment.TickCount64;
    private DateTime _lastSettingsWriteUtc = DateTime.MinValue;
    private DateTime _lastDictWriteUtc = DateTime.MinValue;
    private volatile bool _stopRequested;

    /* Initializes the background daemon server with engine provider, dictionary, and settings */
    public DaemonServer(
        ITtsEngineProvider engineProvider,
        DictionaryManager dictionaryManager,
        SettingsManager settingsManager)
    {
        _engineProvider = engineProvider;
        _dictionaryManager = dictionaryManager;
        _settingsManager = settingsManager;
    }

    /* Runs the daemon named pipe accept loop and idle watchdog until killed or timed out */
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _lastActivityTick, Environment.TickCount64);

        /* Prewarms the model when the daemon spawns */
        await _engineProvider.GetEngineAsync(cancellationToken).ConfigureAwait(false);
        using var timerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = Task.Run(async () =>
        {
            while (!timerCts.Token.IsCancellationRequested && !_stopRequested)
            {
                await Task.Delay(5000, timerCts.Token);
                long elapsedMs = Environment.TickCount64 - Interlocked.Read(ref _lastActivityTick);
                if (elapsedMs >= IdleTimeout.TotalMilliseconds)
                {
                    _stopRequested = true;
                    break;
                }
            }
        }, timerCts.Token);

        while (!cancellationToken.IsCancellationRequested && !_stopRequested)
        {
            NamedPipeServerStream? serverPipe = null;
            try
            {
                serverPipe = new NamedPipeServerStream(
                    DaemonIpcProtocol.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                /* Waits for client connection with short timeout to periodically check _stopRequested */
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                waitCts.CancelAfter(TimeSpan.FromSeconds(2));

                try
                {
                    await serverPipe.WaitForConnectionAsync(waitCts.Token);
                }
                catch (OperationCanceledException)
                {
                    await serverPipe.DisposeAsync().ConfigureAwait(false);
                    continue;
                }

                NamedPipeServerStream acceptedPipe = serverPipe;
                serverPipe = null;
                _ = Task.Run(() => HandleClientAsync(acceptedPipe, cancellationToken), cancellationToken);
            }
            catch (Exception) when (_stopRequested || cancellationToken.IsCancellationRequested)
            {
                if (serverPipe != null)
                    await serverPipe.DisposeAsync().ConfigureAwait(false);
                break;
            }
            catch
            {
                if (serverPipe != null)
                    await serverPipe.DisposeAsync().ConfigureAwait(false);
            }
        }
        timerCts.Cancel();
    }

    /* Handles an individual client connection using binary protocol framing and disposes the pipe when done */
    private async Task HandleClientAsync(NamedPipeServerStream stream, CancellationToken cancellationToken)
    {
        await using (stream.ConfigureAwait(false))
        {
            (DaemonOpcode opcode, byte[] payload) frame;
            try
            {
                frame = await DaemonIpcProtocol.ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            if (frame.opcode != DaemonOpcode.Request) return;

            var request = JsonSerializer.Deserialize(frame.payload, AppJsonContext.Default.DaemonRequest);
            if (request == null) return;

            switch (request.Action)
            {
                case DaemonIpcProtocol.ActionKill:
                {
                    CancelActiveSynthesis();
                    _stopRequested = true;
                    var response = new DaemonResponse { Success = true };
                    byte[] responseJson =
                        JsonSerializer.SerializeToUtf8Bytes(response, AppJsonContext.Default.DaemonResponse);
                    await DaemonIpcProtocol.WriteFrameAsync(
                        stream,
                        DaemonOpcode.Response,
                        responseJson,
                        cancellationToken).ConfigureAwait(false);
                    break;
                }

                case DaemonIpcProtocol.ActionStop:
                {
                    CancelActiveSynthesis();
                    var response = new DaemonResponse { Success = true };
                    byte[] responseJson =
                        JsonSerializer.SerializeToUtf8Bytes(response, AppJsonContext.Default.DaemonResponse);
                    await DaemonIpcProtocol.WriteFrameAsync(
                        stream,
                        DaemonOpcode.Response,
                        responseJson,
                        cancellationToken).ConfigureAwait(false);
                    break;
                }

                case DaemonIpcProtocol.ActionStatus:
                {
                    int elapsed = (int)((Environment.TickCount64 - Interlocked.Read(ref _lastActivityTick)) / 1000);
                    var response = new DaemonResponse
                    {
                        Success = true,
                        ActiveModel = _settingsManager.CurrentAppConfig.ActiveModel.ToString(),
                        IdleSecondsRemaining = Math.Max(0, (int)IdleTimeout.TotalSeconds - elapsed)
                    };
                    byte[] responseJson =
                        JsonSerializer.SerializeToUtf8Bytes(response, AppJsonContext.Default.DaemonResponse);
                    await DaemonIpcProtocol.WriteFrameAsync(
                            stream,
                            DaemonOpcode.Response,
                            responseJson,
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                }

                case DaemonIpcProtocol.ActionSynthesize:
                default:
                    await HandleSynthesizeAsync(stream, request, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    /* Cancels any currently active synthesis job in the daemon */
    private void CancelActiveSynthesis()
    {
        lock (_synthLock)
        {
            _activeSynthesisCts?.Cancel();
        }
    }

    /* Reloads changed configs/dictionary, synthesizes sentences, and streams raw PCM chunks over IPC */
    public async Task HandleSynthesizeAsync(Stream stream, DaemonRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _lastActivityTick, Environment.TickCount64);

        CancellationTokenSource synthCts;
        lock (_synthLock)
        {
            /* Cancel-only on superseded synthesis; each invocation owns and disposes its own CTS in finally */
            _activeSynthesisCts?.Cancel();
            synthCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeSynthesisCts = synthCts;
        }

        var linkedToken = synthCts.Token;
        bool engineLockAcquired = false;

        try
        {
            try
            {
                await _engineLock.WaitAsync(linkedToken).ConfigureAwait(false);
                engineLockAcquired = true;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            ReloadConfigIfChanged();
            var targetModel = _settingsManager.CurrentAppConfig.ActiveModel;
            if (_engineProvider.CurrentModel != targetModel)
            {
                await _engineProvider.SetEngineAsync(targetModel, linkedToken).ConfigureAwait(false);
            }

            var engine = await _engineProvider.GetEngineAsync(linkedToken).ConfigureAwait(false);
            if (!engine.IsAvailable)
            {
                var failResponse = new DaemonResponse
                {
                    Success = false,
                    ErrorMessage = "TTS Model not loaded or downloaded."
                };
                byte[] failJson = JsonSerializer.SerializeToUtf8Bytes(failResponse, AppJsonContext.Default.DaemonResponse);
                await DaemonIpcProtocol.WriteFrameAsync(stream, DaemonOpcode.Response, failJson, linkedToken)
                    .ConfigureAwait(false);
                return;
            }

            var format = engine.OutputFormat;
            var initialResponse = new DaemonResponse
            {
                Success = true,
                Format = format,
                ActiveModel = targetModel.ToString()
            };
            byte[] initJson = JsonSerializer.SerializeToUtf8Bytes(initialResponse, AppJsonContext.Default.DaemonResponse);
            await DaemonIpcProtocol.WriteFrameAsync(stream, DaemonOpcode.Response, initJson, linkedToken)
                .ConfigureAwait(false);

            try
            {
                string processedText = _dictionaryManager.ReplaceText(request.Text);
                var sentences = TextSplitter.SplitIntoSentences(processedText);

                foreach (var sentence in sentences)
                {
                    linkedToken.ThrowIfCancellationRequested();
                    await foreach (var chunk in
                                   engine.SynthesizeStreamAsync(sentence, request.Speed, linkedToken)
                                       .ConfigureAwait(false))
                    {
                        await DaemonIpcProtocol
                            .WriteFrameAsync(stream, DaemonOpcode.AudioChunk, chunk, linkedToken)
                            .ConfigureAwait(false);
                    }
                }

                await DaemonIpcProtocol
                    .WriteFrameAsync(stream, DaemonOpcode.AudioEnd, ReadOnlyMemory<byte>.Empty, linkedToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                /* Client disconnected or stopped */
            }
            catch (Exception ex)
            {
                byte[] errorBytes = Encoding.UTF8.GetBytes(ex.Message);
                await DaemonIpcProtocol
                    .WriteFrameAsync(stream, DaemonOpcode.Error, errorBytes, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            if (engineLockAcquired)
                _engineLock.Release();
            lock (_synthLock)
            {
                if (ReferenceEquals(_activeSynthesisCts, synthCts))
                    _activeSynthesisCts = null;
            }
            synthCts.Dispose();
        }
    }

    /* Reloads repository config only when settings.json or dictionary.json changed on disk */
    private void ReloadConfigIfChanged()
    {
        string configDir = PathResolver.GetConfigDirectory();
        string settingsPath = Path.Combine(configDir, "settings.json");
        string dictPath = Path.Combine(configDir, "dictionary.json");

        DateTime settingsWrite = File.Exists(settingsPath) ? File.GetLastWriteTimeUtc(settingsPath) : DateTime.MinValue;
        DateTime dictWrite = File.Exists(dictPath) ? File.GetLastWriteTimeUtc(dictPath) : DateTime.MinValue;

        if (settingsWrite != _lastSettingsWriteUtc || dictWrite != _lastDictWriteUtc)
        {
            _lastSettingsWriteUtc = settingsWrite;
            _lastDictWriteUtc = dictWrite;
            _settingsManager.ReloadAppConfig();
        }
    }
}
