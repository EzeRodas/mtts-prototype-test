using MoonTTS.Core.Utilities;
using MoonTTS.Domain.Common;
using MoonTTS.Domain.Constants;
using MoonTTS.Domain.Entities;
using MoonTTS.Domain.Interfaces;
using MoonTTS.Domain.ValueObjects;

namespace MoonTTS.Core.Services;

/* File Structure:
        - HistoryManager(IConfigurationRepository repo)
        - GetHistory() : IReadOnlyList<HistoryEntry>
        - FindMatchingEntry(string rawText, string processedText, ModelId model, string voice, float speed) : HistoryEntry?
        - GetEntryWavPath(Guid id) : Result<string>
        - AddEntry(string text, ReadOnlyMemory<byte> wavData, ModelId model, string voice, string processedText, float speed) : Task<Result>
        - DeleteEntry(Guid id) : Result
        - ClearHistory() : Result
        - ResolveEntryWavPath(HistoryEntry entry) : string
*/

/* Manages recorded speech history metadata and master WAV files on disk.
   Audio playback itself is delegated to ISpeechPipeline so playback state stays unified */
public class HistoryManager
{
    private readonly IConfigurationRepository _repo;
    private readonly string _historyDirectory;

    public event Action? OnHistoryChanged;

    /* Initializes history manager, sets up storage directory, and subscribes to repository updates */
    public HistoryManager(IConfigurationRepository repo)
    {
        _repo = repo;
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

    /* Resolves and verifies the WAV file path for a stored history entry */
    public Result<string> GetEntryWavPath(Guid id)
    {
        var entry = _repo.HistoryEntries.FirstOrDefault(h => h.Id == id);
        if (entry == null)
            return Result<string>.Failure("History entry was not found.");

        string wavPath = ResolveEntryWavPath(entry);
        if (!File.Exists(wavPath))
            return Result<string>.Failure("Audio file not found or missing from disk.");

        return Result<string>.Success(wavPath);
    }

    /* Stores the full text with the model, voice, processed text and speed that produced the audio */
    public async Task<Result> AddEntry(
        string text,
        ReadOnlyMemory<byte> wavData,
        ModelId model = default,
        string voice = "",
        string processedText = "",
        float speed = 1.0f)
    {
        Guid newId = Guid.NewGuid();
        string fileName = $"tts_output_{newId}.wav";
        string filePath = Path.Combine(_historyDirectory, fileName);
        string tempPath = $"{filePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                await fs.WriteAsync(wavData);
                await fs.FlushAsync();
            }
            File.Move(tempPath, filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            return Result.Failure($"Failed to save history audio file: {ex.Message}", ex);
        }

        HistoryEntry newEntry = new HistoryEntry
        {
            Id = newId,
            Text = text,
            CreatedAt = DateTime.UtcNow,
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
        while (entries.Count > AppConstants.MaxHistoryEntries)
        {
            var toRemove = entries[^1];
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

    /* Removes a specific entry and deletes its WAV file from disk */
    public Result DeleteEntry(Guid id)
    {
        List<HistoryEntry> entriesList = _repo.HistoryEntries.ToList();

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
    public Result ClearHistory()
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

    /* Resolves the path of the WAV file stored on the history entry, stripping any directory traversal segments */
    private string ResolveEntryWavPath(HistoryEntry entry)
    {
        string rawName = string.IsNullOrWhiteSpace(entry.WavFileName)
            ? $"tts_output_{entry.Id}.wav"
            : entry.WavFileName;
        string safeFileName = Path.GetFileName(rawName);
        if (string.IsNullOrWhiteSpace(safeFileName))
            safeFileName = $"tts_output_{entry.Id}.wav";

        return Path.Combine(_historyDirectory, safeFileName);
    }
}
