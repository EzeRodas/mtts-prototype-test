using MoonTTS.Core.Utilities;
using MoonTTS.Domain.Audio;
using MoonTTS.Domain.Common;
using MoonTTS.Domain.Constants;
using MoonTTS.Domain.Entities;
using MoonTTS.Domain.Interfaces;
using MoonTTS.Domain.ValueObjects;

namespace MoonTTS.Core.Services;

/* File Structure:
        - HotkeyManager(ISpeechPipeline speechPipeline, IConfigurationRepository repo)
        - GetHotkeys() : IReadOnlyList<HotkeyEntry>
        - GetToggleHotkey() : string
        - AssignToggleHotkey(string hotkey) : Result
        - LoadToggleHotkey() : string
        - AssignHotkeyAsync(string hotkey, string text) : Task<Result>
        - PlayHotkeyAsync(Guid entryId, CancellationToken cancellationToken) : Task<Result>
        - PlayHotkeyByStringAsync(string hotkeyString) : Task<Result>
        - DeleteHotkey(Guid entryId) : Result
        - GetWavPath(Guid id) : string
*/

public class HotkeyManager
{
    private readonly ISpeechPipeline _speechPipeline;
    private readonly IConfigurationRepository _repo;

    /* Base directory for profile hotkey folders and audio files */
    private readonly string _hotkeysBaseDir;

    /* Event for the AvaloniaUI to refresh */
    public event Action? OnHotkeysChanged;
    public event Action<string>? OnToggleHotkeyChanged;

    /* Initializes hotkey manager with the unified speech pipeline and configuration repository */
    public HotkeyManager(
        ISpeechPipeline speechPipeline,
        IConfigurationRepository repo)
    {
        _speechPipeline = speechPipeline;
        _repo = repo;

        /* Sets the correct paths */
        _hotkeysBaseDir = Path.Combine(PathResolver.GetConfigDirectory(), "audio", "profiles");

        /* pass through the repo for updating UI */
        _repo.HotkeysChanged += () => OnHotkeysChanged?.Invoke();
    }

    /* Returns a read-only list of all currently assigned hotkeys */
    public IReadOnlyList<HotkeyEntry> GetHotkeys() => _repo.Hotkeys;

    public string GetToggleHotkey() => _repo.AppConfig.ToggleAppHotkey;

    public Result AssignToggleHotkey(string hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey) || !ShortcutCombo.TryParse(hotkey, out var parsed) || parsed == null)
            return Result.Failure("Invalid hotkey combination.");

        var config = _repo.AppConfig;
        config.ToggleAppHotkey = parsed.ToString();

        _repo.SaveAppConfig(config);
        OnToggleHotkeyChanged?.Invoke(config.ToggleAppHotkey);
        return Result.Success();
    }

    public string LoadToggleHotkey()
    {
        string hotkey = GetToggleHotkey();
        OnToggleHotkeyChanged?.Invoke(hotkey);
        return hotkey;
    }

    /* Pre-renders audio for a phrase and assigns it to a hotkey combination */
    public async Task<Result> AssignHotkeyAsync(string hotkey, string text)
    {
        if (string.IsNullOrWhiteSpace(hotkey))
            return Result.Failure("Hotkey combination cannot be empty.");
        if (string.IsNullOrWhiteSpace(text))
            return Result.Failure("Phrase text cannot be empty.");

        HotkeyEntry? existing = _repo.Hotkeys.FirstOrDefault(e => string.Equals(e.Hotkey, hotkey, StringComparison.OrdinalIgnoreCase));
        Guid entryId = existing?.Id ?? Guid.NewGuid();
        bool isNew = existing == null;

        /* Checks that the limit of entries hasn't been reached */
        if (isNew && _repo.Hotkeys.Count >= AppConstants.MaxHotkeyEntries)
            return Result.Failure($"Maximum limit of {AppConstants.MaxHotkeyEntries} entries reached.");

        /* Retrieves the path and ensures the profile folder exists before synthesis exports the WAV */
        string wavPath = GetWavPath(entryId);
        string? wavDir = Path.GetDirectoryName(wavPath);
        if (!string.IsNullOrEmpty(wavDir))
            Directory.CreateDirectory(wavDir);

        /* Synthesizes and exports directly through the speech pipeline with playback disabled.
           By design, this acquires the active pipeline session and interrupts any ongoing live speech */
        var synthResult = await _speechPipeline.SpeakAsync(text, new SpeechOptions
        {
            EnablePlayback = false,
            RecordHistory = false,
            ExportPath = wavPath
        });

        if (synthResult.IsFailure)
            return Result.Failure($"Failed to generate hotkey audio: {synthResult.ErrorMessage}");

        var entries = _repo.Hotkeys.ToList();
        /* Adds entry only if synthesis succeeded */
        if (isNew)
        {
            entries.Add(new HotkeyEntry { Id = entryId, Text = text, Hotkey = hotkey });
        }
        else
        {
            var target = entries.First(e => e.Id == entryId);
            target.Text = text;
        }
        
        _repo.SaveHotkeys(entries);
        return Result.Success();
    }

    /* Plays the pre-rendered audio associated with a hotkey by its unique entry ID */
    public async Task<Result> PlayHotkeyAsync(Guid entryId, CancellationToken cancellationToken = default)
    {
        var entry = _repo.Hotkeys.FirstOrDefault(e => e.Id == entryId);
        if (entry == null)
            return Result.Failure($"Hotkey entry {entryId} was not found.");
        
        string wavPath = GetWavPath(entryId);
        if (!File.Exists(wavPath)) 
            return Result.Failure($"Audio file for hotkey '{entry.Hotkey}' does not exist on disk or was not found");

        return await _speechPipeline.PlayWavFileAsync(wavPath, cancellationToken);
    }

    /* Plays the pre-rendered audio associated with a hotkey matching the given key string */
    public async Task<Result> PlayHotkeyByStringAsync(string hotkeyString)
    {
        /* Checks for every entry of the list if it already exists, ignoring Uppercases */
        var entry = _repo.Hotkeys.FirstOrDefault(e => 
            string.Equals(e.Hotkey, hotkeyString, StringComparison.OrdinalIgnoreCase));
        if (entry == null) 
            return Result.Failure($"No hotkey assigned to '{hotkeyString}'");

        /* Plays if exists */
        return await PlayHotkeyAsync(entry.Id);
    }

    /* Deletes a specific hotkey */
    public Result DeleteHotkey(Guid entryId)
    {
        /* Checks if the hotkey to delete exists */
        var entry = _repo.Hotkeys.FirstOrDefault(e => e.Id == entryId);
        if (entry == null) 
            return Result.Failure($"Hotkey entry '{entryId}' was not found.");

        /* If the hotkey exists, removes the associated WAV file */
        string wavPath = GetWavPath(entryId);
        if (File.Exists(wavPath))
        {
            try {File.Delete(wavPath);} catch { /* Ignore file lock */ }
        }
        
        /* Removes entry and saves the new set */
        var entries = _repo.Hotkeys.ToList();
        entries.RemoveAll(e => e.Id == entryId);
        _repo.SaveHotkeys(entries);
        
        return Result.Success();
    }

    /* Resolves the absolute file path for a hotkey WAV audio file */
    private string GetWavPath(Guid id)
    {
        var activeProfileName = _repo.ProfileData.ActiveProfileName;
        var activeProfile = _repo.ProfileData.Profiles.FirstOrDefault(p => p.Name == activeProfileName)
                            ?? _repo.ProfileData.Profiles.FirstOrDefault();

        string folderName = activeProfile?.FolderName ?? ProfileManager.DefaultProfileName;
        return Path.Combine(_hotkeysBaseDir, folderName, $"{id}.wav");
    }
}