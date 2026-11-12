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

        var sw = Stopwatch.StartNew();
        TimeSpan firstChunkLatency = TimeSpan.Zero;
        var sessionToken = sessionCts.Token;

        try
        {
            /* Interactive speech identical to a recorded entry (text, model and voice) replays its audio
               instead of synthesizing again, which also avoids a duplicate history entry */
            if (options.EnablePlayback && options.RecordHistory && string.IsNullOrWhiteSpace(options.ExportPath))
            {
                var cached = _historyManager.FindMatchingEntry(
                    rawText, _settingsManager.CurrentAppConfig.ActiveModel, engine.ActiveVoice);
                if (cached != null)
                {
                    var replay = await _historyManager.PlayEntryAsync(cached.Id, sessionToken);
                    if (sessionToken.IsCancellationRequested)
                    {
                        _audioPlayer.Stop();
                        return Result<SpeechResult>.Failure(CancelledMessage);
                    }
                    if (replay.IsFailure)
                        return Result<SpeechResult>.Failure(replay.ErrorMessage ?? "Playback failed.");

                    return Result<SpeechResult>.Success(new SpeechResult
                    {
                        AudioDuration = TimeSpan.Zero,
                        TimeToFirstSound = sw.Elapsed,
                        TotalPcmBytes = 0
                    });
                }
            }

            string processedText = options.ApplyDictionary 
                ? _dictionaryManager.ReplaceText(rawText) 
                : rawText;

            var sentences = processedText.Length > 80
                ? TextSplitter.SplitIntoSentences(processedText)
                : new List<string> { processedText };

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

            byte[] masterWav = WavWriter.CreateWav(pcmAccumulator.ToArray(), format);

            if (options.RecordHistory && config.SaveHistory && masterWav.Length > WavWriter.HeaderSize)
            {
                await _historyManager.AddEntry(rawText, masterWav, config.ActiveModel, engine.ActiveVoice);
            }

            if (!string.IsNullOrWhiteSpace(options.ExportPath) && masterWav.Length > WavWriter.HeaderSize)
            {
                string? exportDir = Path.GetDirectoryName(options.ExportPath);
                if (!string.IsNullOrEmpty(exportDir))
                    Directory.CreateDirectory(exportDir);
                await File.WriteAllBytesAsync(options.ExportPath, masterWav, sessionToken);
            }

            return Result<SpeechResult>.Success(new SpeechResult
            {
                AudioDuration = format.GetDuration((int)pcmAccumulator.Length),
                TimeToFirstSound = firstChunkLatency,
                TotalPcmBytes = (int)pcmAccumulator.Length
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
            lock (_sessionLock)
            {
                if (_activeCts == sessionCts)
                {
                    _activeCts = null;
                    IsSpeaking = false;
                    IsPaused = false;
                }
            }
            sessionCts.Dispose();
        }
    }

    /* Pauses the current audio playback process */
    public void Pause() { _audioPlayer.Pause(); IsPaused = true; }

    /* Resumes the paused audio playback process */
    public void Resume() { _audioPlayer.Resume(); IsPaused = false; }

    /* Immediately cancels ongoing synthesis and terminates audio playback */
    public void Stop()
    {
        lock (_sessionLock)
        {
            _activeCts?.Cancel();
            _activeCts?.Dispose();
            _activeCts = null;
            IsSpeaking = false;
            IsPaused = false;
        }
        _audioPlayer.Stop();
    }
}