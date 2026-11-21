using MoonTTS.Core.Utilities;
using MoonTTS.Domain.Common;
using MoonTTS.Domain.Entities;
using MoonTTS.Domain.Interfaces;
using MoonTTS.Domain.ValueObjects;

namespace MoonTTS.Core.Services;

/* File Structure:
        - SettingsManager(IConfigurationRepository repo)
        - GetEngineConfig(ModelId model) : EngineConfig
        - SaveAppConfig(AppConfig newConfig) : Result
        - SaveEngineConfig(ModelId model, EngineConfig newConfig) : Result
        - GetModelsDirectory() : string
        - GetModelDirectory(ModelId model) : string
        - ReloadAppConfig() : void
*/

public class SettingsManager
{
    private readonly IConfigurationRepository _repo;
    
    /* Cache for current settings to avoid multiple reads on consecutive settings changes */
    public AppConfig CurrentAppConfig => _repo.AppConfig;
    
    /* Event for Avalonia to subscribe to refresh the UI */
    public event Action? SettingsChanged;

    /* Initializes settings manager and loads the current application configuration */
    public SettingsManager(IConfigurationRepository repo)
    {
        _repo = repo;
        _repo.AppConfigChanged += () => SettingsChanged?.Invoke();
        _repo.EngineConfigChanged += () => SettingsChanged?.Invoke();
    }

    /* Rwetrieves engine configuration */
    public EngineConfig GetEngineConfig(ModelId model) => _repo.GetEngineConfig(model);
    
    /* Saves global app configs and notifies the UI */
    public Result SaveAppConfig(AppConfig newConfig)
    {
        _repo.SaveAppConfig(newConfig);
        return Result.Success();
    }

    /* Saves engine specific configs and notifies the UI */
    public Result SaveEngineConfig(ModelId model, EngineConfig newConfig)
    {
        _repo.SaveEngineConfig(model, newConfig);
        return Result.Success();
    }

    /* Resolves the directory where TTS model files are stored */
    public string GetModelsDirectory()
    {
        /* If the user defined a custom path it gets returned and not overwritten */
        if (!string.IsNullOrWhiteSpace(CurrentAppConfig.CustomModelsDirectory))
        {
            return CurrentAppConfig.CustomModelsDirectory;
        }

        return PathResolver.GetModelsDirectory();
    }

    /* Resolves the folder holding the files of one engine's models, keeping engines from colliding on file names */
    public string GetModelDirectory(ModelId model) =>
        Path.Combine(GetModelsDirectory(), PathSanitizer.SanitizeFolderName(model.EngineId));

    public void ReloadAppConfig()
    {
        _repo.LoadAll();
        SettingsChanged?.Invoke();
    }
    
}