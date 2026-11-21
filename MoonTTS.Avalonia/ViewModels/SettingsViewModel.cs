using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoonTTS.Avalonia.Services;
using MoonTTS.Avalonia.ViewModels.Settings;
using MoonTTS.Core.Services;
using MoonTTS.Core.Utilities;
using MoonTTS.Domain.Entities;
using MoonTTS.Domain.Interfaces;
using MoonTTS.Domain.ValueObjects;

namespace MoonTTS.Avalonia.ViewModels;

/* File Structure:
        - AdvancedTab (Enum)
        - SettingsViewModel(SettingsManager, IAudioPlayer, IWindowService, DownloadManager, ISpeechPipeline?, DictionaryManager?, HotkeyManager?, HistoryManager?, ITtsEngineProvider?, ProfileManager?)
        - Dictionary : DictionarySettingsViewModel
        - History : HistorySettingsViewModel
        - Hotkeys : HotkeysSettingsViewModel
        - EngineManagement : EngineManagementViewModel
        - IsLinux : bool
        - IsRecordingToggleHotkey : bool
        - IsRecordingPhraseHotkey : bool
        - ApplyRecordedHotkey(string) : void
        - CancelRecording() : void
        - LoadAudioDevices() : void
        - LoadTtsEngines() : void
        - UpdateAvailableModelDownloadState(ModelId, bool) : void
        - EnsureModelUnloadedBeforeDeleteAsync(ModelId) : Task
        - RefreshVoicesAsync() : Task
        - SelectVoiceFromConfig() : void
        - LoadSettings() : void
        - LoadLanguages() : void
        - OnLanguageChanged() : void
        - ShowModelStatus(string) : void
        - ClearModelStatusAfterDelayAsync(CancellationTokenSource, CancellationToken) : Task
        - SwitchModelAsync(ModelMetadata) : Task
        - RevertModelSelectionAsync(bool) : Task
        - AutoSave() : void
        - FlushPendingSave() : void
        - SwitchToAdvanced() : void
        - SwitchToQuick() : void
        - NavigateToEngineManagement() : void
        - SelectTab(AdvancedTab) : void
        - OpenLogsFolder() : void
        - Close() : void
        - ExitApp() : void
        - Dispose() : void
*/

public enum AdvancedTab
{
    General,
    Audio,
    Dictionary,
    Engine,
    EngineManagement,
    Hotkeys,
    History,
}

public partial class SettingsViewModel : ViewModelBase, IDisposable
{
    private readonly SettingsManager? _settingsManager;
    private readonly IAudioPlayer? _audioPlayer;
    private readonly IWindowService? _windowService;
    private readonly DownloadManager? _downloadManager;
    private readonly ITtsEngineProvider? _engineProvider;

    private bool _isLoading;
    private bool _disposed;
    private readonly Lock _saveLock = new();
    private CancellationTokenSource? _saveDebounceCts;
    private CancellationTokenSource? _statusCts;
    private bool _hasPendingSave;
    private ModelId? _pendingEngineModel;

    /* Composed per-tab child ViewModels */
    public DictionarySettingsViewModel Dictionary { get; }
    public HistorySettingsViewModel History { get; }
    public HotkeysSettingsViewModel Hotkeys { get; }
    public EngineManagementViewModel EngineManagement { get; }

    /* View Mode & Active Tab */
    [ObservableProperty]
    private bool _isAdvancedView;

    [ObservableProperty]
    private AdvancedTab _selectedTab = AdvancedTab.General;

    /* Quick & Audio Settings */
    [ObservableProperty]
    private ModelMetadata? _selectedModel;

    [ObservableProperty]
    private VoiceInfo? _selectedVoice;

    /* Feedback shown under the model selectors or on Engine Management / Hotkeys tabs */
    [ObservableProperty]
    private string? _modelStatusMessage;

    [ObservableProperty]
    private bool _enablePrimaryPlayback = true;

    [ObservableProperty]
    private AudioDeviceInfo? _selectedPrimaryDevice;

    [ObservableProperty]
    private int _primaryVolume = 100;

    [ObservableProperty]
    private bool _enableMonitorPlayback;

    [ObservableProperty]
    private AudioDeviceInfo? _selectedMonitorDevice;

    [ObservableProperty]
    private int _monitorVolume = 100;

    [ObservableProperty]
    private float _speechSpeed = 1.0f;

    /* General Settings */
    [ObservableProperty]
    private int _selectedMonitorIndex;

    [ObservableProperty]
    private bool _alwaysOnTop = true;

    [ObservableProperty]
    private bool _openOnStartup;

    [ObservableProperty]
    private bool _hideOnSynth;

    [ObservableProperty]
    private bool _queueSpeech;

    [ObservableProperty]
    private bool _rememberPosition;

    [ObservableProperty]
    private bool _createMicOnStartup;

    [ObservableProperty]
    private string _customModelsDirectory = string.Empty;

    /* Selected UI display language */
    [ObservableProperty]
    private LanguageOption? _selectedLanguage;

    /* Collections */
    public ObservableCollection<LanguageOption> AvailableLanguages { get; } = new();
    public ObservableCollection<ModelMetadata> AvailableModels { get; } = new();
    public ObservableCollection<VoiceInfo> AvailableVoices { get; } = new();
    public ObservableCollection<AudioDeviceInfo> AvailableOutputDevices { get; } = new();

    /* Forwarding properties/methods used by SettingsWindow keyboard-capture code-behind */
    public bool IsRecordingToggleHotkey => Hotkeys.IsRecordingToggleHotkey;
    public bool IsRecordingPhraseHotkey => Hotkeys.IsRecordingPhraseHotkey;
    public void ApplyRecordedHotkey(string shortcut) => Hotkeys.ApplyRecordedHotkey(shortcut);
    public void CancelRecording() => Hotkeys.CancelRecording();

    /* True only on Linux, where PulseAudio / PipeWire null-sink virtual microphones can be created */
    public bool IsLinux => OperatingSystem.IsLinux();

    /* Design-time constructor for Avalonia previewer */
    protected SettingsViewModel()
        : this(null!, null!, null!, null!, null, null, null, null, null, null) { }

    /* Main DI constructor */
    public SettingsViewModel(
        SettingsManager settingsManager,
        IAudioPlayer audioPlayer,
        IWindowService windowService,
        DownloadManager downloadManager,
        ISpeechPipeline? speechPipeline = null,
        DictionaryManager? dictManager = null,
        HotkeyManager? hotkeyManager = null,
        HistoryManager? historyManager = null,
        ITtsEngineProvider? engineProvider = null,
        ProfileManager? profileManager = null)
    {
        _settingsManager = settingsManager;
        _audioPlayer = audioPlayer;
        _windowService = windowService;
        _downloadManager = downloadManager;
        _engineProvider = engineProvider;

        Dictionary = new DictionarySettingsViewModel(dictManager, ShowModelStatus);
        History = new HistorySettingsViewModel(historyManager, speechPipeline, ShowModelStatus);
        Hotkeys = new HotkeysSettingsViewModel(settingsManager, hotkeyManager, profileManager, AutoSave, ShowModelStatus);
        EngineManagement = new EngineManagementViewModel(
            downloadManager,
            UpdateAvailableModelDownloadState,
            EnsureModelUnloadedBeforeDeleteAsync,
            RefreshVoicesAsync,
            OnEnginesRescanned,
            ShowModelStatus);

        if (_settingsManager != null && _audioPlayer != null)
        {
            LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
            LoadTtsEngines();
            EngineManagement.Load();
            LoadLanguages();
            LoadSettings();

            /* Voices come from the engine worker, so they are filled in once the engine finishes loading */
            _ = RefreshVoicesAsync();
        }
    }

    /* Populates the selectable UI languages from built-in and custom locale packs */
    private void LoadLanguages()
    {
        AvailableLanguages.Clear();
        foreach (var lang in LocalizationService.Instance.GetAvailableLanguages())
            AvailableLanguages.Add(lang);
    }

    /* Applies and persists a newly selected UI display language immediately */
    partial void OnSelectedLanguageChanged(LanguageOption? value)
    {
        if (_isLoading || value == null)
            return;

        LocalizationService.Instance.SetLanguage(value.Code);
        AutoSave();
    }

    /* Refreshes localized dynamic text items and formatted tags after a live language switch */
    private void OnLanguageChanged()
    {
        Hotkeys.RefreshLocalizedProperties();

        bool prevLoading = _isLoading;
        _isLoading = true;
        try
        {
            if (AvailableModels.Count > 0 && AvailableModels[0].IsUnselected)
            {
                bool wasUnselected = SelectedModel is { IsUnselected: true };
                var localizedUnselected = ModelMetadata.Unselected with { DisplayName = L.Strings.UnselectedModel };
                AvailableModels[0] = localizedUnselected;
                if (wasUnselected)
                    SelectedModel = localizedUnselected;
            }

            string? primaryId = SelectedPrimaryDevice?.DeviceId;
            string? monitorId = SelectedMonitorDevice?.DeviceId;
            LoadAudioDevices();
            SelectedPrimaryDevice = AvailableOutputDevices.FirstOrDefault(d => d.DeviceId == primaryId)
                                    ?? AvailableOutputDevices.FirstOrDefault();
            SelectedMonitorDevice = AvailableOutputDevices.FirstOrDefault(d => d.DeviceId == monitorId)
                                    ?? AvailableOutputDevices.FirstOrDefault(d => d.DeviceId == "-1");

            EngineManagement.Load();
        }
        finally
        {
            _isLoading = prevLoading;
        }
    }

    /* Populates audio outputs from platform sink enumerator */
    private void LoadAudioDevices()
    {
        AvailableOutputDevices.Clear();

        if (_audioPlayer != null)
        {
            var result = _audioPlayer.GetAvailableDevices();
            if (result.IsSuccess)
            {
                foreach (var dev in result.Value)
                {
                    if (AvailableOutputDevices.All(d => d.DeviceId != dev.DeviceId))
                    {
                        var localizedDev = dev.DeviceId == "-1"
                            ? new AudioDeviceInfo { DeviceId = dev.DeviceId, DeviceName = L.Strings.DefaultSystemDevice }
                            : dev;
                        AvailableOutputDevices.Add(localizedDev);
                    }
                }
            }
        }

        if (AvailableOutputDevices.All(d => d.DeviceId != "-1"))
        {
            AvailableOutputDevices.Insert(0, new AudioDeviceInfo
            {
                DeviceId = "-1",
                DeviceName = L.Strings.DefaultSystemDevice,
            });
        }
    }

    /* Populates every model offered by the installed engines grouped by engine with separators */
    private void LoadTtsEngines()
    {
        AvailableModels.Clear();
        AvailableModels.Add(ModelMetadata.Unselected with { DisplayName = L.Strings.UnselectedModel });
        if (_downloadManager == null)
            return;

        foreach (var engine in _downloadManager.GetEngines())
        {
            if (engine.Models.Count == 0)
                continue;

            string engineTitle = !string.IsNullOrWhiteSpace(engine.DisplayName) ? engine.DisplayName : engine.Id;
            AvailableModels.Add(ModelMetadata.CreateSeparator(engineTitle));

            foreach (var model in engine.Models)
            {
                bool isDownloaded = _downloadManager.IsModelDownloaded(model.Id);
                AvailableModels.Add(model with { IsDownloaded = isDownloaded });
            }
        }
    }

    /* Refreshes model dropdowns after EngineCatalog rescans disk */
    private void OnEnginesRescanned()
    {
        bool prevLoading = _isLoading;
        _isLoading = true;
        try
        {
            ModelId active = _settingsManager?.CurrentAppConfig.ActiveModel ?? ModelId.Unselected;
            LoadTtsEngines();
            SelectedModel = AvailableModels.FirstOrDefault(m => m.Id == active)
                ?? AvailableModels.FirstOrDefault(m => m.IsUnselected)
                ?? ModelMetadata.Unselected;
        }
        finally
        {
            _isLoading = prevLoading;
        }
    }

    /* Synchronizes the download status of an entry in AvailableModels so model dropdowns update live */
    private void UpdateAvailableModelDownloadState(ModelId modelId, bool isDownloaded)
    {
        for (int i = 0; i < AvailableModels.Count; i++)
        {
            var model = AvailableModels[i];
            if (model.Id == modelId && !model.IsSeparator)
            {
                AvailableModels[i] = model with { IsDownloaded = isDownloaded };
                break;
            }
        }

        if (!isDownloaded && SelectedModel != null && SelectedModel.Id == modelId)
        {
            SelectedModel = AvailableModels.FirstOrDefault(m => m.IsUnselected) ?? ModelMetadata.Unselected;
        }
    }

    /* Unloads the model worker before deleting its files on disk to avoid OS file-lock failures on Windows */
    private async Task EnsureModelUnloadedBeforeDeleteAsync(ModelId modelId)
    {
        bool isCurrentSelected = SelectedModel != null && SelectedModel.Id == modelId;
        bool isLoaded = _engineProvider != null && _engineProvider.CurrentModel == modelId;

        if (!isCurrentSelected && !isLoaded)
            return;

        if (_engineProvider != null)
            await _engineProvider.SwitchModelAsync(ModelId.Unselected);

        _isLoading = true;
        try
        {
            SelectedModel = AvailableModels.FirstOrDefault(m => m.IsUnselected) ?? ModelMetadata.Unselected;
            AvailableVoices.Clear();
            SelectedVoice = null;
        }
        finally
        {
            _isLoading = false;
        }
    }

    /* Populates the voices reported by the active engine and selects the configured one */
    private async Task RefreshVoicesAsync()
    {
        if (_engineProvider == null)
            return;

        var engine = await _engineProvider.GetEngineAsync();

        _isLoading = true;
        try
        {
            AvailableVoices.Clear();
            foreach (var voice in engine.ListVoices())
                AvailableVoices.Add(voice);
            SelectVoiceFromConfig();
        }
        finally
        {
            _isLoading = false;
        }
    }

    /* Selects the voice saved for the selected model, falling back to the model's first voice */
    private void SelectVoiceFromConfig()
    {
        if (_settingsManager == null || SelectedModel == null || SelectedModel.IsUnselected)
        {
            SelectedVoice = AvailableVoices.FirstOrDefault();
            return;
        }

        string configuredVoice = _settingsManager.GetEngineConfig(SelectedModel.Id).VoiceId;
        SelectedVoice = AvailableVoices.FirstOrDefault(v => v.Id == configuredVoice) ?? AvailableVoices.FirstOrDefault();
    }

    /* Loads all configuration values and refreshes audio output devices for hot-plugged hardware */
    public void LoadSettings()
    {
        if (_settingsManager == null)
            return;

        _isLoading = true;
        try
        {
            LoadAudioDevices();
            LoadLanguages();

            var appConfig = _settingsManager.CurrentAppConfig;
            SelectedLanguage = AvailableLanguages.FirstOrDefault(l =>
                    string.Equals(l.Code, appConfig.Language, StringComparison.OrdinalIgnoreCase))
                ?? AvailableLanguages.FirstOrDefault();
            SelectedModel = AvailableModels.FirstOrDefault(m => m.Id == appConfig.ActiveModel)
                ?? AvailableModels.FirstOrDefault(m => m.IsUnselected)
                ?? ModelMetadata.Unselected;
            var engineConfig = !SelectedModel.IsUnselected
                ? _settingsManager.GetEngineConfig(SelectedModel.Id)
                : new EngineConfig();
            SelectVoiceFromConfig();

            EnablePrimaryPlayback = appConfig.EnablePrimaryPlayback;
            SelectedPrimaryDevice =
                AvailableOutputDevices.FirstOrDefault(d => d.DeviceId == appConfig.PrimaryAudioDeviceId)
                ?? AvailableOutputDevices.FirstOrDefault();
            PrimaryVolume = appConfig.PrimaryVolume;

            EnableMonitorPlayback = appConfig.EnableMonitorPlayback;
            SelectedMonitorDevice =
                AvailableOutputDevices.FirstOrDefault(d => d.DeviceId == appConfig.MonitorAudioDeviceId)
                ?? AvailableOutputDevices.FirstOrDefault(d => d.DeviceId == "-1");
            MonitorVolume = appConfig.MonitorVolume;

            SpeechSpeed = engineConfig.Speed > 0 ? engineConfig.Speed : 1.0f;
            SelectedMonitorIndex = appConfig.DefaultMonitor;
            OpenOnStartup = appConfig.OpenOnStartup;
            AlwaysOnTop = appConfig.AlwaysOnTop;
            HideOnSynth = appConfig.HideOnSynth;
            QueueSpeech = appConfig.QueueSpeech;
            RememberPosition = appConfig.RememberPosition;
            CreateMicOnStartup = appConfig.CreateMicOnStartup;
            CustomModelsDirectory = appConfig.CustomModelsDirectory ?? string.Empty;

            Dictionary.Load();
            Hotkeys.Load();
            History.Load();
        }
        finally
        {
            _isLoading = false;
        }
    }

    /* Handles model selection, redirecting uninstalled models to Engine Management */
    partial void OnSelectedModelChanged(ModelMetadata? value)
    {
        if (_isLoading || value == null || value.IsSeparator)
            return;

        if (!value.IsUnselected && _downloadManager != null && !_downloadManager.IsModelDownloaded(value.Id))
        {
            _ = RevertModelSelectionAsync(navigateToEngineManagement: true);
            return;
        }

        ModelStatusMessage = null;
        if (_engineProvider != null && _engineProvider.CurrentModel != value.Id)
        {
            _ = SwitchModelAsync(value);
        }
        else
        {
            AutoSave();
        }
    }

    /* Shows a transient status message for 5 seconds, cancelling any earlier timer cleanly */
    private void ShowModelStatus(string message)
    {
        _statusCts?.Cancel();
        var cts = new CancellationTokenSource();
        _statusCts = cts;
        var token = cts.Token;

        ModelStatusMessage = message;
        _ = ClearModelStatusAfterDelayAsync(cts, token);
    }

    /* Clears ModelStatusMessage after 5 seconds unless superseded by a newer status */
    private async Task ClearModelStatusAfterDelayAsync(CancellationTokenSource cts, CancellationToken token)
    {
        try
        {
            await Task.Delay(5000, token);
            if (!token.IsCancellationRequested)
                ModelStatusMessage = null;
        }
        catch (OperationCanceledException)
        {
            /* Superseded by newer status message */
        }
        finally
        {
            if (ReferenceEquals(_statusCts, cts))
                _statusCts = null;
            cts.Dispose();
        }
    }

    /* Loads the selected model in its engine worker and refreshes voices and speed for it */
    private async Task SwitchModelAsync(ModelMetadata model)
    {
        if (_engineProvider == null)
            return;

        var switchResult = await _engineProvider.SwitchModelAsync(model.Id);
        if (switchResult.IsSuccess)
        {
            await RefreshVoicesAsync();
            if (_settingsManager != null && !model.IsUnselected)
            {
                _isLoading = true;
                try
                {
                    float speed = _settingsManager.GetEngineConfig(model.Id).Speed;
                    SpeechSpeed = speed > 0 ? speed : 1.0f;
                }
                finally
                {
                    _isLoading = false;
                }
            }
            AutoSave();
        }
        else
        {
            AppLogger.Error("SettingsViewModel", $"Model switch failed: {switchResult.ErrorMessage}", switchResult.Exception);
            ShowModelStatus(switchResult.ErrorMessage ?? L.Strings.ModelSwitchFailed);
            await RevertModelSelectionAsync();
        }
    }

    /* Restores the dropdown to the saved active model without saving */
    private async Task RevertModelSelectionAsync(bool navigateToEngineManagement = false)
    {
        await Task.Yield();

        var activeModel = _settingsManager?.CurrentAppConfig.ActiveModel ?? ModelId.Unselected;
        _isLoading = true;
        try
        {
            SelectedModel = null;
            SelectedModel = AvailableModels.FirstOrDefault(m => m.Id == activeModel)
                ?? AvailableModels.FirstOrDefault(m => m.IsUnselected)
                ?? ModelMetadata.Unselected;
        }
        finally
        {
            _isLoading = false;
        }

        if (navigateToEngineManagement)
            NavigateToEngineManagement();
    }

    partial void OnSelectedVoiceChanged(VoiceInfo? value) => AutoSave();
    partial void OnSpeechSpeedChanged(float value) => AutoSave();
    partial void OnEnablePrimaryPlaybackChanged(bool value) => AutoSave();
    partial void OnSelectedPrimaryDeviceChanged(AudioDeviceInfo? value) => AutoSave();
    partial void OnPrimaryVolumeChanged(int value) => AutoSave();
    partial void OnEnableMonitorPlaybackChanged(bool value) => AutoSave();
    partial void OnSelectedMonitorDeviceChanged(AudioDeviceInfo? value) => AutoSave();
    partial void OnMonitorVolumeChanged(int value) => AutoSave();
    partial void OnSelectedMonitorIndexChanged(int value) => AutoSave();
    partial void OnOpenOnStartupChanged(bool value) => AutoSave();

    /* Applies the new on-top state to the open windows right away, then persists it */
    partial void OnAlwaysOnTopChanged(bool value)
    {
        if (_isLoading)
            return;

        _windowService?.ApplyAlwaysOnTop(value);
        AutoSave();
    }

    partial void OnHideOnSynthChanged(bool value) => AutoSave();
    partial void OnQueueSpeechChanged(bool value) => AutoSave();
    partial void OnRememberPositionChanged(bool value) => AutoSave();
    partial void OnCreateMicOnStartupChanged(bool value) => AutoSave();
    partial void OnCustomModelsDirectoryChanged(string value) => AutoSave();

    /* Applies changes immediately in memory and audio output, while debouncing disk persistence by 250ms */
    private void AutoSave()
    {
        if (_isLoading || _settingsManager == null || _audioPlayer == null)
            return;

        ModelId? dirtyEngineModel = null;
        CancellationTokenSource cts;
        CancellationToken token;

        lock (_saveLock)
        {
            var appConfig = _settingsManager.CurrentAppConfig;
            appConfig.ActiveModel = SelectedModel?.Id ?? ModelId.Unselected;
            appConfig.EnablePrimaryPlayback = EnablePrimaryPlayback;
            appConfig.PrimaryAudioDeviceId = SelectedPrimaryDevice?.DeviceId ?? "-1";
            appConfig.PrimaryVolume = PrimaryVolume;

            appConfig.EnableMonitorPlayback = EnableMonitorPlayback;
            appConfig.MonitorAudioDeviceId = SelectedMonitorDevice?.DeviceId ?? "-1";
            appConfig.MonitorVolume = MonitorVolume;

            appConfig.DefaultMonitor = SelectedMonitorIndex;
            appConfig.OpenOnStartup = OpenOnStartup;
            appConfig.AlwaysOnTop = AlwaysOnTop;
            appConfig.HideOnSynth = HideOnSynth;
            appConfig.QueueSpeech = QueueSpeech;
            appConfig.RememberPosition = RememberPosition;
            appConfig.CreateMicOnStartup = CreateMicOnStartup;
            appConfig.Language = SelectedLanguage?.Code ?? LocalizationService.Instance.CurrentLanguage;
            appConfig.CustomModelsDirectory = string.IsNullOrWhiteSpace(CustomModelsDirectory)
                ? null
                : CustomModelsDirectory.Trim();

            if (SelectedModel != null && !SelectedModel.IsUnselected)
            {
                var engineConfig = _settingsManager.GetEngineConfig(SelectedModel.Id);
                engineConfig.VoiceId = SelectedVoice?.Id ?? engineConfig.VoiceId;
                engineConfig.Speed = SpeechSpeed;
                dirtyEngineModel = SelectedModel.Id;
            }

            /* Respect EnablePrimaryPlayback / EnableMonitorPlayback toggles when syncing live outputs */
            string? effectivePrimary = appConfig.EnablePrimaryPlayback ? appConfig.PrimaryAudioDeviceId : null;
            string? effectiveMonitor = appConfig.EnableMonitorPlayback ? appConfig.MonitorAudioDeviceId : null;
            _audioPlayer.SetOutputDevices(effectivePrimary, effectiveMonitor);
            _audioPlayer.SetVolume(appConfig.PrimaryVolume, appConfig.MonitorVolume);

            _hasPendingSave = true;
            if (dirtyEngineModel.HasValue)
                _pendingEngineModel = dirtyEngineModel;

            /* Cancel-only on superseded debounce; each task owns and disposes its own CTS in finally */
            _saveDebounceCts?.Cancel();
            cts = new CancellationTokenSource();
            _saveDebounceCts = cts;
            token = cts.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250, token);
                if (!token.IsCancellationRequested)
                    FlushPendingSave();
            }
            catch (OperationCanceledException)
            {
                /* Superseded by newer slider tick or explicit flush */
            }
            finally
            {
                lock (_saveLock)
                {
                    if (ReferenceEquals(_saveDebounceCts, cts))
                        _saveDebounceCts = null;
                }
                cts.Dispose();
            }
        });
    }

    /* Immediately persists any debounced application and engine configuration changes to disk */
    public void FlushPendingSave()
    {
        if (_settingsManager == null)
            return;

        ModelId? modelToSave;
        lock (_saveLock)
        {
            if (!_hasPendingSave)
                return;

            _hasPendingSave = false;
            modelToSave = _pendingEngineModel;
            _pendingEngineModel = null;
            _saveDebounceCts?.Cancel();
            _saveDebounceCts = null;
        }

        _settingsManager.SaveAppConfig(_settingsManager.CurrentAppConfig);
        if (modelToSave.HasValue && !modelToSave.Value.IsUnselected)
        {
            _settingsManager.SaveEngineConfig(modelToSave.Value, _settingsManager.GetEngineConfig(modelToSave.Value));
        }
    }

    /* View Switchers */
    [RelayCommand]
    private void SwitchToAdvanced()
    {
        LoadSettings();
        IsAdvancedView = true;
    }

    [RelayCommand]
    private void SwitchToQuick()
    {
        LoadSettings();
        IsAdvancedView = false;
    }

    /* Navigates directly to the Engine Management tab in Advanced View */
    [RelayCommand]
    private void NavigateToEngineManagement()
    {
        LoadSettings();
        SelectedTab = AdvancedTab.EngineManagement;
        IsAdvancedView = true;
    }

    [RelayCommand]
    private void SelectTab(AdvancedTab tab) => SelectedTab = tab;

    /* Opens the rotating logs folder in the OS file manager */
    [RelayCommand]
    private void OpenLogsFolder()
    {
        try
        {
            string logDir = AppLogger.LogDirectory;
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer.exe", logDir) { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("open", logDir) { UseShellExecute = false });
            }
            else
            {
                Process.Start(new ProcessStartInfo("xdg-open", logDir) { UseShellExecute = false });
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warning("SettingsViewModel", $"Failed to open logs folder: {ex.Message}");
        }
    }

    [RelayCommand]
    private void Close() => _windowService?.HideSettingsWindow();

    [RelayCommand]
    private void ExitApp() => _windowService?.ExitApplication();

    /* Unsubscribes static localization events and cancels any active background timers/downloads */
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;
        lock (_saveLock)
        {
            _saveDebounceCts?.Cancel();
            _saveDebounceCts = null;
        }
        _statusCts?.Cancel();
        _statusCts = null;
        EngineManagement.CancelAllDownloads();
        GC.SuppressFinalize(this);
    }
}
