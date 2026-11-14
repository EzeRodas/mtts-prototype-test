using System.Text.Json;
using MoonTTS.Core.Utilities;
using MoonTTS.Domain.Audio;
using MoonTTS.Domain.Common;
using MoonTTS.Domain.Constants;
using MoonTTS.Domain.Entities;
using MoonTTS.Domain.Interfaces;
using MoonTTS.Domain.ValueObjects;

namespace MoonTTS.Core.Services;

/* File Structure:
        - HistoryManager(IAudioPlayer audioPlayer, IConfigurationRepository repo)
        - GetHistory() : IReadOnlyList<HistoryEntry>
        - FindMatchingEntry(string rawText, string processedText, ModelId model, string voice, float speed) : HistoryEntry?
        - AddEntry(string text, byte[] wavData, ModelId model, string voice, string processedText, float speed) : Task<Result>
        - PlayEntryAsync(Guid id, CancellationToken cancellationToken) : Task<Result>
        - PlayEntryWithInfoAsync(Guid id, CancellationToken cancellationToken) : Task<Result<(TimeSpan Duration, int PcmBytes)>>
        - Pause() : void
        - Resume() : void
        - Stop() : void
        - DeleteEntry(Guid id) : Task<Result>
        - ClearHistory() : Task<Result>
        - ResolveEntryWavPath(HistoryEntry entry) : string
*/

public class HistoryManager
{
    private readonly IConfigurationRepository _repo;
    private readonly IAudioPlayer _audioPlayer;
    private readonly string _historyDirectory;
    private readonly Lock _playbackLock = new();
    private CancellationTokenSource? _activePlaybackCts;

    public bool IsPlaying { get; private set; }
    public bool IsPaused { get; private set; }

    public event Action? OnHistoryChanged;
    public event Action? OnPlaybackStateChanged;

    /* Initializes history manager, sets up storage directory, and loads cached history */
    public HistoryManager(IAudioPlayer audioPlayer, IConfigurationRepository repo)
    {
        _repo = repo;
        _audioPlayer = audioPlayer;
        _historyDirectory = Path.Combine(PathResolver.GetConfigDirectory(), "audio", "history");
        
        Directory.CreateDirectory(_historyDirectory);
        _repo.HistoryChanged += () => OnHistoryChanged?.Invoke();
    }
    
    /* Returns a copy of the current speech history entries */
    public IReadOnlyList<HistoryEntry> GetHistory() => _repo.HistoryEntries.ToList();

    /* Returns the most recent entry generated from matching raw text, processed text, model, voice and speed */
    public HistoryEntry? FindMatchingEntry(
        string rawText,
        string processedText,
        ModelId model,
        string voice,
        float speed = 1.0f)
    {
        if (model.IsUnselected || string.IsNullOrEmpty(voice))
            return null;

        float targetSpeed = speed > 0 ? speed : 1.0f;

        return _repo.HistoryEntries.FirstOrDefault(h =>
        {
            if (h.Model != model || h.Voice != voice)
                return false;

            float entrySpeed = h.Speed > 0 ? h.Speed : 1.0f;
            if (Math.Abs(entrySpeed - targetSpeed) >= 0.01f)
                return false;

            string entryProcessed = string.IsNullOrEmpty(h.ProcessedText) ? h.Text : h.ProcessedText;
            return string.Equals(h.Text, rawText, StringComparison.Ordinal)
                   && string.Equals(entryProcessed, processedText, StringComparison.Ordinal)
                   && File.Exists(ResolveEntryWavPath(h));
        });
    }

    /* Stores the full text with the model, voice, processed text and speed that produced the audio */
    public async Task<Result> AddEntry(
        string text,
        byte[] wavData,
        ModelId model = default,
        string voice = "",
        string processedText = "",
        float speed = 1.0f)
    {
        Guid newId = Guid.NewGuid();
        string fileName = $"tts_output_{newId}.wav";
        string filePath = Path.Combine(_historyDirectory, fileName);

        await File.WriteAllBytesAsync(filePath, wavData);

        HistoryEntry newEntry = new HistoryEntry
        {
            Id = newId, 
            Text = text, 
            CreatedAt = DateTime.Now,
            WavFileName = fileName,
            Model = model,
            Voice = voice,
            ProcessedText = string.IsNullOrEmpty(processedText) ? text : processedText,
            Speed = speed > 0 ? speed : 1.0f
        };

        /* Recovers the list and adds it at the top */
        var entries = _repo.HistoryEntries.ToList();
        entries.Insert(0, newEntry);
        
        /* Enforces limits and deletes old entries */
        if (entries.Count > AppConstants.MaxHistoryEntries)
        {
            var toRemove = entries.Last();
            entries.RemoveAt(entries.Count - 1);

            string oldWavPath = ResolveEntryWavPath(toRemove);
            if (File.Exists(oldWavPath))
            {
                try { File.Delete(oldWavPath); } catch { /* Ignores file locks */ }
            }
        }
        
        _repo.SaveHistory(entries);
        return Result.Success();
    }
    
    /* Audio Controls */

    /* Plays a history audio file and returns simple success or failure */
    public async Task<Result> PlayEntryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var result = await PlayEntryWithInfoAsync(id, cancellationToken);
        return result.IsSuccess
            ? Result.Success()
            : Result.Failure(result.ErrorMessage ?? "Failed to play history entry.", result.Exception);
    }

    /* Plays a history audio file and reports its parsed audio duration and PCM byte count */
    public async Task<Result<(TimeSpan Duration, int PcmBytes)>> PlayEntryWithInfoAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var entry = _repo.HistoryEntries.FirstOrDefault(h => h.Id == id);
        if (entry == null)
            return Result<(TimeSpan, int)>.Failure("History entry was not found.");
        
        string wavPath = ResolveEntryWavPath(entry);
        if (!File.Exists(wavPath))
            return Result<(TimeSpan, int)>.Failure("Audio file not found or missing from disk.");

        var config = _repo.AppConfig;
        if (!config.EnablePrimaryPlayback && !config.EnableMonitorPlayback)
            return Result<(TimeSpan, int)>.Success((TimeSpan.Zero, 0));

        CancellationTokenSource playbackCts;
        lock (_playbackLock)
        {
            _activePlaybackCts?.Cancel();
            _activePlaybackCts?.Dispose();
            _activePlaybackCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            playbackCts = _activePlaybackCts;
            IsPlaying = true;
            IsPaused = false;
        }
        OnPlaybackStateChanged?.Invoke();

        var token = playbackCts.Token;

        try
        {
            byte[] audioBytes = await File.ReadAllBytesAsync(wavPath, token);
            AudioFormat format = FormatReader.GetFormatFromWavHeader(audioBytes);
            ReadOnlyMemory<byte> pcm = audioBytes.Length > WavWriter.HeaderSize
                ? audioBytes.AsMemory(WavWriter.HeaderSize)
                : audioBytes;
            
            string? primaryId = config.EnablePrimaryPlayback ? config.PrimaryAudioDeviceId : null;
            string? monitorId = config.EnableMonitorPlayback ? config.MonitorAudioDeviceId : null;
            
            _audioPlayer.SetOutputDevices(primaryId, monitorId);
            _audioPlayer.SetVolume(config.PrimaryVolume, config.MonitorVolume);
            
            var playResult = await _audioPlayer.PlayPcmAsync(pcm, format, token);
            if (playResult.IsFailure)
                return Result<(TimeSpan, int)>.Failure(playResult.ErrorMessage ?? "Playback failed.", playResult.Exception);

            return Result<(TimeSpan, int)>.Success((format.GetDuration(pcm.Length), pcm.Length));
        }
        catch (OperationCanceledException)
        {
            _audioPlayer.Stop();
            return Result<(TimeSpan, int)>.Failure("History playback was canceled.");
        }
        catch (Exception ex)
        {
            _audioPlayer.Stop();
            return Result<(TimeSpan, int)>.Failure($"Failed to play history entry: {ex.Message}", ex);
        }
        finally
        {
            bool changed = false;
            lock (_playbackLock)
            {
                if (_activePlaybackCts == playbackCts)
                {
                    _activePlaybackCts = null;
                    IsPlaying = false;
                    IsPaused = false;
                    changed = true;
                }
            }
            playbackCts.Dispose();
            if (changed)
                OnPlaybackStateChanged?.Invoke();
        }
    }
    
    /* Pauses active history playback */
    public void Pause()
    {
        if (!IsPlaying) return;
        _audioPlayer.Pause();
        IsPaused = true;
        OnPlaybackStateChanged?.Invoke();
    }

    /* Resumes paused history playback */
    public void Resume()
    {
        if (!IsPlaying) return;
        _audioPlayer.Resume();
        IsPaused = false;
        OnPlaybackStateChanged?.Invoke();
    }

    /* Cancels active history playback and notifies state observers */
    public void Stop()
    {
        bool wasPlaying;
        lock (_playbackLock)
        {
            wasPlaying = IsPlaying;
            _activePlaybackCts?.Cancel();
            _activePlaybackCts?.Dispose();
            _activePlaybackCts = null;
            IsPlaying = false;
            IsPaused = false;
        }
        if (wasPlaying)
        {
            _audioPlayer.Stop();
            OnPlaybackStateChanged?.Invoke();
        }
    }
    
    /* Management */

    /* Removing a specific entry */
    public async Task<Result> DeleteEntry(Guid id)
    {
        List<HistoryEntry> entriesList = _repo.HistoryEntries.ToList();
        
        /* Retrieves the first entry that matches the id */
        HistoryEntry? historyEntry = entriesList.FirstOrDefault(h => h.Id == id);
        
        if (historyEntry == null)
            return Result.Failure($"History entry with ID {id} was not found.");
        
        entriesList.Remove(historyEntry);

        string path = ResolveEntryWavPath(historyEntry);
        if (File.Exists(path))
        {
            try { File.Delete(path); } catch { /* Ignore file lock */ }
        }
        
        _repo.SaveHistory(entriesList);
        return Result.Success();
    }

    /* Clears all speech history entries and deletes their audio files from disk */
    public async Task<Result> ClearHistory()
    {
        List<HistoryEntry> entriesList = _repo.HistoryEntries.ToList();
        try
        {
            foreach (var historyEntry in entriesList)
            {
                string path = ResolveEntryWavPath(historyEntry);
                if (File.Exists(path))
                {
                    try { File.Delete(path); } catch { /* Ignore file lock */ }
                }
            }
            
            entriesList.Clear();
            _repo.SaveHistory(entriesList);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Failed to clear history: {ex.Message}", ex);
        }
    }
    
    /* Persistence */
    
    /* Resolves the path of the WAV file stored on the history entry, falling back to legacy ID naming */
    private string ResolveEntryWavPath(HistoryEntry entry)
    {
        string fileName = string.IsNullOrWhiteSpace(entry.WavFileName)
            ? $"tts_output_{entry.Id}.wav"
            : entry.WavFileName;
        return Path.Combine(_historyDirectory, fileName);
    }
}