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
        - ReadAndCaptureStream() : IAsyncEnumerable<ReadOnlyMemory<byte>>
        - Pause() : void
        - Resume() : void
        - Stop() : void
*/

/* Unified pipeline for all speech actions. Interactive speech matching a history entry replays it instead of synthesizing */
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
    private CancellationTokenSource? _activeCts;

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

        CancellationTokenSource sessionCts;
        lock (_sessionLock)
        {
            _activeCts?.Cancel();
            _activeCts?.Dispose();
            _activeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            sessionCts = _activeCts;
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
                    var replay = await _historyManager.PlayEntryWithInfoAsync(cached.Id, sessionToken);
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

            var sentences = TextSplitter.SplitIntoSentences(processedText);

            var config = _settingsManager.CurrentAppConfig;
            var format = engine.OutputFormat;

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

            using var pcmAccumulator = new MemoryStream();
            pcmAccumulator.Write(new byte[WavWriter.HeaderSize]);

            async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAndCaptureStream()
            {
                await foreach (var chunk in channel.Reader.ReadAllAsync(sessionToken))
                {
                    pcmAccumulator.Write(chunk.Span);
                    yield return chunk;
                }
            }

            if (options.EnablePlayback)
            {
                var playResult = await _audioPlayer.StreamPcmAsync(ReadAndCaptureStream(), format, sessionToken);
                if (playResult.IsFailure)
                    return Result<SpeechResult>.Failure(playResult.ErrorMessage ?? "Playback failed.");
            }
            else
            {
                await foreach (var chunk in channel.Reader.ReadAllAsync(sessionToken))
                {
                    pcmAccumulator.Write(chunk.Span);
                }
            }

            await producerTask;

            int totalPcmBytes = Math.Max(0, (int)pcmAccumulator.Length - WavWriter.HeaderSize);
            byte[] headerBytes = new byte[WavWriter.HeaderSize];
            WavWriter.WriteWavHeader(headerBytes, totalPcmBytes, format);
            pcmAccumulator.Position = 0;
            pcmAccumulator.Write(headerBytes);
            byte[] masterWav = pcmAccumulator.ToArray();

            if (options.RecordHistory && config.SaveHistory && totalPcmBytes > 0)
            {
                await _historyManager.AddEntry(
                    rawText,
                    masterWav,
                    config.ActiveModel,
                    engine.ActiveVoice,
                    processedText,
                    effectiveSpeed);
            }

            if (!string.IsNullOrWhiteSpace(options.ExportPath) && totalPcmBytes > 0)
            {
                string? exportDir = Path.GetDirectoryName(options.ExportPath);
                if (!string.IsNullOrEmpty(exportDir))
                    Directory.CreateDirectory(exportDir);
                await File.WriteAllBytesAsync(options.ExportPath, masterWav, sessionToken);
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
            return Result<SpeechResult>.Failure($"Speech error: {ex.Message}", ex);
        }
        finally
        {
            bool changed = false;
            lock (_sessionLock)
            {
                if (_activeCts == sessionCts)
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

    /* Plays a pre-rendered WAV file through the active session lock so HUD controls work uniformly */
    public async Task<Result> PlayWavFileAsync(string wavPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(wavPath))
            return Result.Failure($"Audio file '{wavPath}' was not found.");

        var config = _settingsManager.CurrentAppConfig;
        if (!config.EnablePrimaryPlayback && !config.EnableMonitorPlayback)
            return Result.Success();

        CancellationTokenSource sessionCts;
        lock (_sessionLock)
        {
            _activeCts?.Cancel();
            _activeCts?.Dispose();
            _activeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            sessionCts = _activeCts;
            IsSpeaking = true;
            IsPaused = false;
        }
        PlaybackStateChanged?.Invoke();

        var sessionToken = sessionCts.Token;
        try
        {
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
                return Result.Failure(CancelledMessage);

            return playResult;
        }
        catch (OperationCanceledException)
        {
            _audioPlayer.Stop();
            return Result.Failure(CancelledMessage);
        }
        catch (Exception ex)
        {
            _audioPlayer.Stop();
            return Result.Failure($"Failed to play audio file: {ex.Message}", ex);
        }
        finally
        {
            bool changed = false;
            lock (_sessionLock)
            {
                if (_activeCts == sessionCts)
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

    /* Pauses the current audio playback process */
    public void Pause()
    {
        _audioPlayer.Pause();
        IsPaused = true;
        PlaybackStateChanged?.Invoke();
    }

    /* Resumes the paused audio playback process */
    public void Resume()
    {
        _audioPlayer.Resume();
        IsPaused = false;
        PlaybackStateChanged?.Invoke();
    }

    /* Immediately cancels ongoing synthesis and terminates audio playback */
    public void Stop()
    {
        bool changed;
        lock (_sessionLock)
        {
            changed = IsSpeaking || IsPaused;
            _activeCts?.Cancel();
            _activeCts?.Dispose();
            _activeCts = null;
            IsSpeaking = false;
            IsPaused = false;
        }
        _audioPlayer.Stop();
        if (changed)
            PlaybackStateChanged?.Invoke();
    }
}