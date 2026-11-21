using System.Diagnostics;
using System.Threading.Channels;
using MoonTTS.Core.Utilities;
using MoonTTS.Domain.Audio;
using MoonTTS.Domain.Common;
using MoonTTS.Domain.Interfaces;

namespace MoonTTS.Core.Services;

/* File Structure:
        - SpeechPipeline(
            ITtsEngineProvider engineProvider,
            DictionaryManager dictionaryManager,
            HistoryManager historyManager,
            SettingsManager settingsManager,
            IAudioPlayer audioPlayer)
        - SpeakAsync(string text, CancellationToken ct) : Task<Result<SpeechResult>>
        - SpeakAsync(
            string rawText,
            SpeechOptions options,
            CancellationToken cancellationToken) : Task<Result<SpeechResult>>
        - PlayWavFileAsync(string wavPath, CancellationToken cancellationToken) : Task<Result>
        - PlayWavFileWithInfoAsync(string wavPath, CancellationToken cancellationToken) : Task<Result<(TimeSpan Duration, int PcmBytes)>>
        - PlayWavCoreAsync(string wavPath, CancellationToken sessionToken) : Task<Result<(TimeSpan Duration, int PcmBytes)>>
        - Pause() : void
        - Resume() : void
        - Stop() : void
*/

/* Unified pipeline for all speech and audio playback actions (HUD, hotkeys, and history).
   Interactive speech matching a history entry replays it instead of re-synthesizing.
   Non-playback pre-rendering (such as hotkey phrase generation) intentionally interrupts active playback */
public class SpeechPipeline : ISpeechPipeline
{
    /* Returned by SpeakAsync when no model is selected in settings */
    public const string NoModelSelectedMessage = "Please select a model before synthesizing.";

    /* Returned by SpeakAsync when the session is cancelled through Stop or the caller's token */
    public const string CancelledMessage = "Speech synthesis was cancelled.";

    private readonly ITtsEngineProvider _engineProvider;
    private readonly DictionaryManager _dictionaryManager;
    private readonly HistoryManager _historyManager;
    private readonly SettingsManager _settingsManager;
    private readonly IAudioPlayer _audioPlayer;

    private readonly Lock _sessionLock = new();
    private readonly SemaphoreSlim _queueLock = new(1, 1);
    private CancellationTokenSource? _activeCts;
    private CancellationTokenSource _queueCts = new();

    public bool IsSpeaking { get; private set; }
    public bool IsPaused { get; private set; }

    /* Fired whenever IsSpeaking or IsPaused transitions */
    public event Action? PlaybackStateChanged;

    /* Initializes the unified speech pipeline with engine provider, dictionary, history, settings, and player */
    public SpeechPipeline(
        ITtsEngineProvider engineProvider,
        DictionaryManager dictionaryManager,
        HistoryManager historyManager,
        SettingsManager settingsManager,
        IAudioPlayer audioPlayer)
    {
        _engineProvider = engineProvider;
        _dictionaryManager = dictionaryManager;
        _historyManager = historyManager;
        _settingsManager = settingsManager;
        _audioPlayer = audioPlayer;
    }

    /* Synthesizes and streams speech using default accessibility options */
    public Task<Result<SpeechResult>> SpeakAsync(string text, CancellationToken ct = default) =>
        SpeakAsync(text, new SpeechOptions(), ct);

    /* Orchestrates dictionary substitution, sentence chunking, parallel synthesis, audio streaming, and history capture */
    public async Task<Result<SpeechResult>> SpeakAsync(
        string rawText,
        SpeechOptions options,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return Result<SpeechResult>.Failure("Text to speech cannot be empty.");

        /* Synthesis requires an explicitly selected model; checked before any engine worker is started */
        if (_settingsManager.CurrentAppConfig.ActiveModel.IsUnselected)
            return Result<SpeechResult>.Failure(NoModelSelectedMessage);

        var engine = await _engineProvider.GetEngineAsync(cancellationToken);
        if (!engine.IsAvailable)
            return Result<SpeechResult>.Failure("TTS model is not available or downloaded.");

        bool useQueue = options.EnablePlayback && _settingsManager.CurrentAppConfig.QueueSpeech;
        bool queueAcquired = false;
        CancellationToken queueStopToken;
        lock (_sessionLock)
        {
            queueStopToken = _queueCts.Token;
        }

        if (useQueue)
        {
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, queueStopToken);
            try
            {
                await _queueLock.WaitAsync(waitCts.Token);
                queueAcquired = true;
            }
            catch (OperationCanceledException)
            {
                return Result<SpeechResult>.Failure(CancelledMessage);
            }
        }

        CancellationTokenSource sessionCts;
        lock (_sessionLock)
        {
            /* Cancel-only on previous session; each session owns and disposes its own CTS in finally */
            if (!useQueue)
                _activeCts?.Cancel();

            sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _queueCts.Token);
            _activeCts = sessionCts;
            IsSpeaking = true;
            IsPaused = false;
        }
        PlaybackStateChanged?.Invoke();

        var sw = Stopwatch.StartNew();
        TimeSpan firstChunkLatency = TimeSpan.Zero;
        var sessionToken = sessionCts.Token;

        try
        {
            string processedText = options.ApplyDictionary
                ? _dictionaryManager.ReplaceText(rawText)
                : rawText;
            float effectiveSpeed = options.Speed ?? engine.Speed;

            /* Interactive speech matching raw text, dictionary-processed text, model, voice, and speed
               replays cached audio instead of re-synthesizing */
            if (options.EnablePlayback && options.RecordHistory && string.IsNullOrWhiteSpace(options.ExportPath))
            {
                var cached = _historyManager.FindMatchingEntry(
                    rawText,
                    processedText,
                    _settingsManager.CurrentAppConfig.ActiveModel,
                    engine.ActiveVoice,
                    effectiveSpeed);
                if (cached != null)
                {
                    var wavPathRes = _historyManager.GetEntryWavPath(cached.Id);
                    if (wavPathRes.IsSuccess)
                    {
                        var replay = await PlayWavCoreAsync(wavPathRes.Value, sessionToken);
                        if (sessionToken.IsCancellationRequested)
                        {
                            _audioPlayer.Stop();
                            return Result<SpeechResult>.Failure(CancelledMessage);
                        }
                        if (replay.IsFailure)
                            return Result<SpeechResult>.Failure(replay.ErrorMessage ?? "Playback failed.");

                        return Result<SpeechResult>.Success(new SpeechResult
                        {
                            AudioDuration = replay.Value.Duration,
                            TimeToFirstSound = sw.Elapsed,
                            TotalPcmBytes = replay.Value.PcmBytes
                        });
                    }
                }
            }

            var sentences = TextSplitter.SplitIntoSentences(processedText);
            var config = _settingsManager.CurrentAppConfig;
            var format = engine.OutputFormat;

            bool needHistory = options.RecordHistory && config.SaveHistory;
            bool needExport = !string.IsNullOrWhiteSpace(options.ExportPath);
            bool needWavCapture = needHistory || needExport;

            if (options.EnablePlayback)
            {
                string? primaryId = config.EnablePrimaryPlayback ? config.PrimaryAudioDeviceId : null;
                string? monitorId = config.EnableMonitorPlayback ? config.MonitorAudioDeviceId : null;
                int primaryVol = options.PrimaryVolume ?? config.PrimaryVolume;
                int monitorVol = options.MonitorVolume ?? config.MonitorVolume;

                _audioPlayer.SetOutputDevices(primaryId, monitorId);
                _audioPlayer.SetVolume(primaryVol, monitorVol);
            }

            var channel = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(2)
            {
                SingleWriter = true,
                SingleReader = true
            });

            var producerTask = Task.Run(async () =>
            {
                try
                {
                    foreach (var sentence in sentences)
                    {
                        sessionToken.ThrowIfCancellationRequested();
                        await foreach (var chunk in engine.SynthesizeStreamAsync(sentence, options.Speed, sessionToken))
                        {
                            if (firstChunkLatency == TimeSpan.Zero)
                                firstChunkLatency = sw.Elapsed;

                            await channel.Writer.WriteAsync(chunk, sessionToken);
                        }
                    }
                    channel.Writer.Complete();
                }
                catch (Exception ex)
                {
                    channel.Writer.Complete(ex);
                }
            }, sessionToken);

            using var pcmAccumulator = needWavCapture ? new MemoryStream() : null;
            if (pcmAccumulator != null)
            {
                Span<byte> zeroHeader = stackalloc byte[WavWriter.HeaderSize];
                zeroHeader.Clear();
                pcmAccumulator.Write(zeroHeader);
            }

            int totalPcmBytes = 0;

            async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAndCaptureStream()
            {
                await foreach (var chunk in channel.Reader.ReadAllAsync(sessionToken))
                {
                    totalPcmBytes += chunk.Length;
                    pcmAccumulator?.Write(chunk.Span);
                    yield return chunk;
                }
            }

            if (options.EnablePlayback)
            {
                var playResult = await _audioPlayer.StreamPcmAsync(ReadAndCaptureStream(), format, sessionToken);
                if (playResult.IsFailure)
                {
                    /* Always cancel and drain producerTask so it never stalls on the bounded channel */
                    sessionCts.Cancel();
                    try { await producerTask; } catch { /* Expected cancellation */ }
                    return Result<SpeechResult>.Failure(playResult.ErrorMessage ?? "Playback failed.");
                }
            }
            else
            {
                await foreach (var chunk in channel.Reader.ReadAllAsync(sessionToken))
                {
                    totalPcmBytes += chunk.Length;
                    pcmAccumulator?.Write(chunk.Span);
                }
            }

            await producerTask;

            if (pcmAccumulator != null && totalPcmBytes > 0)
            {
                Span<byte> headerBytes = stackalloc byte[WavWriter.HeaderSize];
                WavWriter.WriteWavHeader(headerBytes, totalPcmBytes, format);
                pcmAccumulator.Position = 0;
                pcmAccumulator.Write(headerBytes);

                ReadOnlyMemory<byte> wavMemory = pcmAccumulator.GetBuffer().AsMemory(0, (int)pcmAccumulator.Length);

                if (needHistory)
                {
                    await _historyManager.AddEntry(
                        rawText,
                        wavMemory,
                        config.ActiveModel,
                        engine.ActiveVoice,
                        processedText,
                        effectiveSpeed);
                }

                if (needExport)
                {
                    string exportPath = options.ExportPath!;
                    string? exportDir = Path.GetDirectoryName(exportPath);
                    if (!string.IsNullOrEmpty(exportDir))
                        Directory.CreateDirectory(exportDir);

                    string tempExportPath = $"{exportPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
                    try
                    {
                        await using (var fs = new FileStream(tempExportPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                        {
                            await fs.WriteAsync(wavMemory, sessionToken);
                            await fs.FlushAsync(sessionToken);
                        }
                        File.Move(tempExportPath, exportPath, overwrite: true);
                    }
                    catch
                    {
                        try { if (File.Exists(tempExportPath)) File.Delete(tempExportPath); } catch { }
                        throw;
                    }
                }
            }

            return Result<SpeechResult>.Success(new SpeechResult
            {
                AudioDuration = format.GetDuration(totalPcmBytes),
                TimeToFirstSound = firstChunkLatency,
                TotalPcmBytes = totalPcmBytes
            });
        }
        catch (OperationCanceledException)
        {
            _audioPlayer.Stop();
            return Result<SpeechResult>.Failure(CancelledMessage);
        }
        catch (Exception ex)
        {
            _audioPlayer.Stop();
            AppLogger.Error("SpeechPipeline", "Speech synthesis failed", ex);
            return Result<SpeechResult>.Failure($"Speech error: {ex.Message}", ex);
        }
        finally
        {
            bool changed = false;
            lock (_sessionLock)
            {
                if (ReferenceEquals(_activeCts, sessionCts))
                {
                    _activeCts = null;
                    IsSpeaking = false;
                    IsPaused = false;
                    changed = true;
                }
            }
            sessionCts.Dispose();
            if (queueAcquired)
                _queueLock.Release();
            if (changed)
                PlaybackStateChanged?.Invoke();
        }
    }

    /* Plays a pre-rendered WAV file through the active session lock so HUD controls work uniformly */
    public async Task<Result> PlayWavFileAsync(string wavPath, CancellationToken cancellationToken = default)
    {
        var result = await PlayWavFileWithInfoAsync(wavPath, cancellationToken);
        return result.IsSuccess
            ? Result.Success()
            : Result.Failure(result.ErrorMessage ?? "Failed to play audio file.", result.Exception);
    }

    /* Plays a pre-rendered WAV file through the active session lock and reports its duration and PCM byte count */
    public async Task<Result<(TimeSpan Duration, int PcmBytes)>> PlayWavFileWithInfoAsync(
        string wavPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(wavPath))
            return Result<(TimeSpan, int)>.Failure($"Audio file '{wavPath}' was not found.");

        var config = _settingsManager.CurrentAppConfig;
        if (!config.EnablePrimaryPlayback && !config.EnableMonitorPlayback)
            return Result<(TimeSpan, int)>.Success((TimeSpan.Zero, 0));

        CancellationTokenSource sessionCts;
        lock (_sessionLock)
        {
            _activeCts?.Cancel();
            sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _queueCts.Token);
            _activeCts = sessionCts;
            IsSpeaking = true;
            IsPaused = false;
        }
        PlaybackStateChanged?.Invoke();

        var sessionToken = sessionCts.Token;
        try
        {
            return await PlayWavCoreAsync(wavPath, sessionToken);
        }
        catch (OperationCanceledException)
        {
            _audioPlayer.Stop();
            return Result<(TimeSpan, int)>.Failure(CancelledMessage);
        }
        catch (Exception ex)
        {
            _audioPlayer.Stop();
            AppLogger.Error("SpeechPipeline", $"Failed to play audio file '{wavPath}'", ex);
            return Result<(TimeSpan, int)>.Failure($"Failed to play audio file: {ex.Message}", ex);
        }
        finally
        {
            bool changed = false;
            lock (_sessionLock)
            {
                if (ReferenceEquals(_activeCts, sessionCts))
                {
                    _activeCts = null;
                    IsSpeaking = false;
                    IsPaused = false;
                    changed = true;
                }
            }
            sessionCts.Dispose();
            if (changed)
                PlaybackStateChanged?.Invoke();
        }
    }

    /* Reads a WAV file, parses its dynamic format header, configures active outputs, and plays PCM */
    private async Task<Result<(TimeSpan Duration, int PcmBytes)>> PlayWavCoreAsync(
        string wavPath,
        CancellationToken sessionToken)
    {
        var config = _settingsManager.CurrentAppConfig;
        if (!config.EnablePrimaryPlayback && !config.EnableMonitorPlayback)
            return Result<(TimeSpan, int)>.Success((TimeSpan.Zero, 0));

        byte[] wavBytes = await File.ReadAllBytesAsync(wavPath, sessionToken);
        AudioFormat format = FormatReader.GetFormatFromWavHeader(wavBytes);
        ReadOnlyMemory<byte> pcm = wavBytes.Length > WavWriter.HeaderSize
            ? wavBytes.AsMemory(WavWriter.HeaderSize)
            : wavBytes;

        string? primaryId = config.EnablePrimaryPlayback ? config.PrimaryAudioDeviceId : null;
        string? monitorId = config.EnableMonitorPlayback ? config.MonitorAudioDeviceId : null;

        _audioPlayer.SetOutputDevices(primaryId, monitorId);
        _audioPlayer.SetVolume(config.PrimaryVolume, config.MonitorVolume);

        var playResult = await _audioPlayer.PlayPcmAsync(pcm, format, sessionToken);
        if (sessionToken.IsCancellationRequested)
            return Result<(TimeSpan, int)>.Failure(CancelledMessage);
        if (playResult.IsFailure)
            return Result<(TimeSpan, int)>.Failure(playResult.ErrorMessage ?? "Playback failed.", playResult.Exception);

        return Result<(TimeSpan, int)>.Success((format.GetDuration(pcm.Length), pcm.Length));
    }

    /* Pauses the current audio playback process when speech is active */
    public void Pause()
    {
        bool changed;
        lock (_sessionLock)
        {
            if (!IsSpeaking || IsPaused)
                return;
            IsPaused = true;
            changed = true;
        }
        _audioPlayer.Pause();
        if (changed)
            PlaybackStateChanged?.Invoke();
    }

    /* Resumes the paused audio playback process */
    public void Resume()
    {
        bool changed;
        lock (_sessionLock)
        {
            if (!IsSpeaking || !IsPaused)
                return;
            IsPaused = false;
            changed = true;
        }
        _audioPlayer.Resume();
        if (changed)
            PlaybackStateChanged?.Invoke();
    }

    /* Immediately cancels ongoing synthesis, clears queued speech, and terminates audio playback */
    public void Stop()
    {
        bool changed;
        CancellationTokenSource oldQueueCts;
        lock (_sessionLock)
        {
            changed = IsSpeaking || IsPaused;
            _activeCts?.Cancel();
            oldQueueCts = _queueCts;
            _queueCts = new CancellationTokenSource();
            IsSpeaking = false;
            IsPaused = false;
        }
        oldQueueCts.Cancel();
        oldQueueCts.Dispose();
        _audioPlayer.Stop();
        if (changed)
            PlaybackStateChanged?.Invoke();
    }
}
