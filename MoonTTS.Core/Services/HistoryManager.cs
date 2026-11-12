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
        - FindMatchingEntry(string text, ModelId model, string voice) : HistoryEntry?
        - AddEntry(string text, byte[] wavData, ModelId model, string voice) : Task<Result>
        - PlayEntryAsync(Guid id, CancellationToken cancellationToken) : Task<Result>
        - Pause() : void
        - Resume() : void
        - Stop() : void
        - DeleteEntry(Guid id) : Task<Result>
        - ClearHistory() : Task<Result>
        - GetWavPath(Guid id) : string
*/

public class HistoryManager
{
    private readonly IConfigurationRepository _repo;
    private readonly IAudioPlayer _audioPlayer;
    private readonly string _historyDirectory;

    public event Action? OnHistoryChanged;

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

    /* Returns the most recent entry generated from exactly this text, model and voice whose audio file still
       exists, or null. Unselected models and empty voices never match */
    public HistoryEntry? FindMatchingEntry(string text, ModelId model, string voice)
    {
        if (model.IsUnselected || string.IsNullOrEmpty(voice))
            return null;

        return _repo.HistoryEntries.FirstOrDefault(h =>
            h.Model == model
            && h.Voice == voice
            && string.Equals(h.Text, text, StringComparison.Ordinal)
            && File.Exists(Path.Combine(_historyDirectory, h.WavFileName)));
    }

    /* Stores the full text with the model and voice that produced the audio, so it can be recalled and replayed */
    public async Task<Result> AddEntry(string text, byte[] wavData, ModelId model = default, string voice = "")
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
            Voice = voice
        };

        /* Recovers the list and adds it at the top */
        var entries = _repo.HistoryEntries.ToList();
        entries.Insert(0, newEntry);
        
        /* Enforces limits and deletes old entries */
        if (entries.Count > AppConstants.MaxHistoryEntries)
        {
            var toRemove = entries.Last();
            entries.RemoveAt(entries.Count - 1);

            string oldWavPath = Path.Combine(_historyDirectory, toRemove.WavFileName);
            if (File.Exists(oldWavPath))
            {
                try { File.Delete(oldWavPath); } catch { /* Ignores file locks */ }
            }
        }
        
        _repo.SaveHistory(entries);
        return Result.Success();
    }
    
    /* Audio Controls */

    /* Play file from the history */
    public async Task<Result> PlayEntryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entry = _repo.HistoryEntries.FirstOrDefault(h => h.Id == id);
        if (entry == null)
            return Result.Failure($"History entry was not found.");
        
        /* Gets the file path */
        string wavPath = Path.Combine(_historyDirectory, entry.WavFileName);
        if (!File.Exists(wavPath))
            return Result.Failure("Audio file not found or missing from disk.");

        var config = _repo.AppConfig;
        if (!config.EnablePrimaryPlayback && !config.EnableMonitorPlayback) return Result.Success();

        try
        {
            /* Reproduces and listens for cancellation token */
            byte[] audioBytes = await File.ReadAllBytesAsync(wavPath, cancellationToken);

            AudioFormat format = FormatReader.GetFormatFromWavHeader(audioBytes);

            ReadOnlyMemory<byte> pcm = audioBytes.Length > 44 ? audioBytes.AsMemory(44) : audioBytes;
            
            string? primaryId = config.EnablePrimaryPlayback ? config.PrimaryAudioDeviceId : null;
            string? monitorId = config.EnableMonitorPlayback ? config.MonitorAudioDeviceId : null;
            
            _audioPlayer.SetOutputDevices(primaryId, monitorId);
            _audioPlayer.SetVolume(config.PrimaryVolume, config.MonitorVolume);
            
            return await _audioPlayer.PlayPcmAsync(pcm, format, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return  Result.Failure($"History playback was canceled.");
        }
        catch (Exception ex)
        {
            return Result.Failure($"Failed to play history entry: {ex.Message}", ex);
        }
    }
    
    /* Once the audio player has been initiated, we can just use the methods defined in its corresponding OS player */
    public void Pause() =>  _audioPlayer.Pause();
    public void Resume() =>  _audioPlayer.Resume();
    public void Stop() =>  _audioPlayer.Stop();
    
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

        string path = GetWavPath(id);
        if (File.Exists(path))
        {
            try {File.Delete(path);} catch { /* Ignore file lock */ }
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
                string path = GetWavPath(historyEntry.Id);
                if (File.Exists(path))
                {
                    try {File.Delete(path);} catch { /* Ignore file lock */ }
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
    
    /* Returns the path of the file with matching Guid */
    private string GetWavPath(Guid id) => Path.Combine(_historyDirectory, $"tts_output_{id}.wav");
    
}