using System.Collections.Concurrent;
using System.Text.Json;
using MoonTTS.Core.Utilities;
using MoonTTS.Domain.Entities;
using MoonTTS.Domain.Interfaces;
using MoonTTS.Domain.ValueObjects;

namespace MoonTTS.Infrastructure.Persistence;

/* File Structure:
        - JsonConfigurationRepository()
        - LoadAll() : void
        - SaveAppConfig(AppConfig config) : void
        - SaveDictionary(List<DictionaryEntry> entries) : void
        - SaveProfileData(ProfileDataStore profile) : void
        - SaveHotkeys(List<HotkeyEntry> entries) : void
        - SaveHistory(List<HistoryEntry> entires) : void
        - GetEngineConfig(ModelId model) : EngineConfig
        - SaveEngineConfig(ModelId model, EngineConfig config) : void
        - GetEngineConfigPath(ModelId model) : string
        - LoadInternal<T>(string relativePath, JsonTypeInfo<T> typeInfo) : T?
        - SaveInternal<T>(string relativePath, T data, JsonTypeInfo<T> typeInfo) : void
        - GetActiveProfileFolder() : string
        - SetActiveProfile(string profileName) : void
*/

public class JsonConfigurationRepository : IConfigurationRepository
{
    private readonly string _configDirectory;
    private readonly Lock _processLock = new();

    public AppConfig AppConfig { get; private set; } = new();
    public IReadOnlyList<DictionaryEntry> DictionaryEntries { get; private set; } =  new List<DictionaryEntry>();
    public ProfileDataStore ProfileData { get; private set; } = new();
    public IReadOnlyList<HotkeyEntry> Hotkeys { get; private set; } = new List<HotkeyEntry>();
    public IReadOnlyList<HistoryEntry> HistoryEntries { get; private set; } = new List<HistoryEntry>();

    private readonly ConcurrentDictionary<ModelId, EngineConfig> _engineConfigs = new();
    
    public event Action? AppConfigChanged;
    public event Action? EngineConfigChanged;
    public event Action? DictionaryChanged;
    public event Action? ProfilesChanged;
    public event Action? HotkeysChanged;
    public event Action? HistoryChanged;

    public JsonConfigurationRepository()
    {
        _configDirectory = PathResolver.GetConfigDirectory();
        Directory.CreateDirectory(_configDirectory);
        LoadAll();
    }

    public void LoadAll()
    {
        _engineConfigs.Clear();
        AppConfig = LoadInternal("settings.json", AppJsonContext.Default.AppConfig) ?? new AppConfig();
        DictionaryEntries = LoadInternal("dictionary.json", AppJsonContext.Default.ListDictionaryEntry) 
                            ?? new List<DictionaryEntry>();
        
        string profilesPath = Path.Combine("audio", "profiles", "profiles.json");
        ProfileData = LoadInternal(profilesPath, AppJsonContext.Default.ProfileDataStore) 
                            ?? new ProfileDataStore();

        if (ProfileData.Profiles == null || ProfileData.Profiles.Count == 0)
        {
            ProfileData.Profiles = new List<HotkeyProfile>
            {
                new HotkeyProfile
                {
                    Name = "Default",
                    FolderName = "Default"
                }
            };
            ProfileData.ActiveProfileName = "Default";
            SaveProfileData(ProfileData);
        }
        
        string activeFolder = GetActiveProfileFolder();
        string hotkeysPath = Path.Combine("audio", "profiles", activeFolder, "hotkeys.json");
        Hotkeys = LoadInternal(hotkeysPath, AppJsonContext.Default.ListHotkeyEntry) 
                      ?? new List<HotkeyEntry>();

        string historyPath = Path.Combine("audio", "history", "history.json");
        HistoryEntries = LoadInternal(historyPath, AppJsonContext.Default.ListHistoryEntry) ?? new List<HistoryEntry>();
    }

    public void SaveAppConfig(AppConfig config)
    {
        AppConfig = config;
        SaveInternal("settings.json", config, AppJsonContext.Default.AppConfig);
        AppConfigChanged?.Invoke();
    }

    public void SaveDictionary(List<DictionaryEntry> entries)
    {
        List<DictionaryEntry> entriesList = entries.ToList();
        DictionaryEntries = entriesList;
        SaveInternal("dictionary.json", entriesList, AppJsonContext.Default.ListDictionaryEntry);
        DictionaryChanged?.Invoke();
    }

    public void SaveProfileData(ProfileDataStore profile)
    {
        ProfileData = profile;
        string path = Path.Combine("audio", "profiles", "profiles.json");
        SaveInternal(path, ProfileData, AppJsonContext.Default.ProfileDataStore);
        ProfilesChanged?.Invoke();
    }

    public void SaveHotkeys(List<HotkeyEntry> entries)
    {
        List<HotkeyEntry> entriesList =  entries.ToList();
        Hotkeys = entriesList;

        /* Saves to the currently active subfolder */
        string activeFolder = GetActiveProfileFolder();
        string path = Path.Combine("audio", "profiles", activeFolder, "hotkeys.json");

        SaveInternal(path, entriesList, AppJsonContext.Default.ListHotkeyEntry);
        HotkeysChanged?.Invoke();
    }

    public void SaveHistory(List<HistoryEntry> entires)
    {
        List<HistoryEntry> entriesList = entires.ToList();
        HistoryEntries = entriesList;

        string path = Path.Combine("audio", "history", "history.json");
        SaveInternal(path, entriesList, AppJsonContext.Default.ListHistoryEntry);
        
        HistoryChanged?.Invoke();
    }

    /* Returns the cached per-model preferences, loading them from disk the first time */
    public EngineConfig GetEngineConfig(ModelId model)
    {
        return _engineConfigs.GetOrAdd(model, m =>
            LoadInternal(GetEngineConfigPath(m), AppJsonContext.Default.EngineConfig) ?? new EngineConfig());
    }

    /* Persists the per-model preferences and notifies listeners */
    public void SaveEngineConfig(ModelId model, EngineConfig config)
    {
        _engineConfigs[model] = config;
        SaveInternal(GetEngineConfigPath(model), config, AppJsonContext.Default.EngineConfig);
        EngineConfigChanged?.Invoke();
    }

    /* Builds "engine configurations/<engine>/<model>.json" with names made safe for every OS */
    private static string GetEngineConfigPath(ModelId model)
    {
        string engineFolder = model.IsUnselected ? "unselected" : PathSanitizer.SanitizeFolderName(model.EngineId);
        string modelFile = model.IsUnselected ? "unselected" : PathSanitizer.SanitizeFolderName(model.ModelName);
        return Path.Combine("engine configurations", engineFolder, $"{modelFile}.json");
    }

    private T? LoadInternal<T>(string relativePath, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        string fullPath = Path.Combine(_configDirectory, relativePath);
        if (!File.Exists(fullPath)) return default;
        try
        {
            lock (_processLock)
            {
                using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                return JsonSerializer.Deserialize(fs, typeInfo);
            }
        }
        catch
        {
            return default;
        }
    }

    private void SaveInternal<T>(string relativePath, T data, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        string fullPath = Path.Combine(_configDirectory, relativePath);
        string tempPath = fullPath + ".tmp";
        
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        lock (_processLock)
        {
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(fs, data, typeInfo);
            }
            File.Move(tempPath, fullPath, overwrite:true);
        }
    }

    private string GetActiveProfileFolder()
    {
        /* Ifno profiles available returns default */
        if (ProfileData.Profiles == null || ProfileData.Profiles.Count == 0)
            return "Default";

        /* Retrieves the profile with the matching name */
        var activeProfile = ProfileData.Profiles.FirstOrDefault(p => p.Name == ProfileData.ActiveProfileName);

        /* Returns it folder name, or fallbacks to the first profile's folder. */
        return activeProfile?.FolderName ?? ProfileData.Profiles[0].FolderName;
    }

    public void SetActiveProfile(string profileName)
    {
        ProfileData.ActiveProfileName = profileName;
        
        /* Saves the profiles.json to remember the newly active profile */
        string profilesPath = Path.Combine("audio", "profiles", "profiles.json");
        SaveInternal(profilesPath, ProfileData, AppJsonContext.Default.ProfileDataStore);
        ProfilesChanged?.Invoke();
        
        /* Swaps the hotkeys in memory to match the new profile */
        string activeFolder = GetActiveProfileFolder();
        string hotkeysPath = Path.Combine("audio", "profiles", activeFolder, "hotkeys.json");
        Hotkeys = LoadInternal(hotkeysPath, AppJsonContext.Default.ListHotkeyEntry) ?? new List<HotkeyEntry>();
        
        HotkeysChanged?.Invoke();
    }
}