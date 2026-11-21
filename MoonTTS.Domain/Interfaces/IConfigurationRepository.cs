using MoonTTS.Domain.Entities;
using MoonTTS.Domain.ValueObjects;

namespace MoonTTS.Domain.Interfaces;

public interface IConfigurationRepository
{
    void LoadAll();
    
    /* App Config */
    AppConfig AppConfig { get; }
    void SaveAppConfig(AppConfig config);
    event Action? AppConfigChanged;
    
    /* Engine Config */
    EngineConfig GetEngineConfig(ModelId model);
    void SaveEngineConfig(ModelId model, EngineConfig config);
    event Action? EngineConfigChanged;
    
    /* Dictionary */
    IReadOnlyList<DictionaryEntry> DictionaryEntries { get; }
    void SaveDictionary(IReadOnlyList<DictionaryEntry> entries);
    event Action? DictionaryChanged;
    
    /* Profiles */
    ProfileDataStore ProfileData { get; }
    void SaveProfileData(ProfileDataStore profile);
    void SetActiveProfile(string profileName);
    event Action? ProfilesChanged;
    
    /* Hotkeys */
    IReadOnlyList<HotkeyEntry>  Hotkeys { get; }
    void SaveHotkeys(IReadOnlyList<HotkeyEntry> entries);
    event Action? HotkeysChanged;
    
    /* History */
    IReadOnlyList<HistoryEntry> HistoryEntries { get; }
    void SaveHistory(IReadOnlyList<HistoryEntry> entries);
    event Action? HistoryChanged;
}