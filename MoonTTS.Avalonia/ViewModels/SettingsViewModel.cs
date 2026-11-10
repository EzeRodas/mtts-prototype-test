using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoonTTS.Avalonia.Services;
using MoonTTS.Core.Services;
using MoonTTS.Domain.Entities;
using MoonTTS.Domain.Interfaces;
using MoonTTS.Domain.ValueObjects;

namespace MoonTTS.Avalonia.ViewModels;

/* File Structure:
        - AdvancedTab (Enum)
        - ModelStatusItem (Class)
        - SettingsViewModel(SettingsManager, IAudioPlayer, IWindowService, DownloadManager, DictionaryManager?, HotkeyManager?, HistoryManager?, TtsEngineProvider?)
        - IsLinux : bool
        - LoadAudioDevices() : void
        - LoadTtsEngines() : void
        - UpdateAvailableModelDownloadState(ModelId, bool) : void
        - RefreshVoicesAsync() : Task
        - SelectVoiceFromConfig() : void
        - LoadSettings() : void
        - LoadModelStatuses() : void
        - ShowModelStatusAsync(string message) : void
        - SwitchModelAsync(ModelMetadata model) : Task
        - RevertModelSelectionAsync() : Task
        - AutoSave() : void
        - SwitchToAdvanced() : void
        - SwitchToQuick() : void
        - SelectTab(AdvancedTab) : void
        - AddDictionaryEntry() : void
        - DeleteDictionaryEntry(Guid) : void
        - AddHotkeyPhraseAsync() : Task
        - DeleteHotkeyPhrase(Guid) : void
        - PlayHistoryItemAsync(Guid) : Task
        - ClearHistory() : void
        - DeleteHistoryItem(Guid) : void
        - NavigateToEngineManagement() : void
        - DownloadModelAsync(ModelStatusItem) : Task
        - DeleteModel(ModelStatusItem) : void
        - Close() : void
        - ExitApp() : void
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

public partial class ModelStatusItem : ObservableObject
{
    [ObservableProperty]
    private ModelId _model;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private bool _isSeparator;

    [ObservableProperty]
    private string _separatorTitle = string.Empty;

    [ObservableProperty]
    private string? _sizeTag;

    [ObservableProperty]
    private string? _ramUsageTag;

    [ObservableProperty]
    private string? _cpuUsageTag;

    [ObservableProperty]
    private bool _isCpuLow;

    [ObservableProperty]
    private bool _isCpuMedium;

    [ObservableProperty]
    private bool _isCpuHigh;

    public bool HasTags => !string.IsNullOrEmpty(SizeTag) || !string.IsNullOrEmpty(RamUsageTag) || !string.IsNullOrEmpty(CpuUsageTag);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    private bool _isDownloaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    private bool _hasDownloadableFiles;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private double _progress;

    /* True when the manifest declares downloadable files that are not yet downloaded */
    public bool CanDownload => !IsSeparator && HasDownloadableFiles && !IsDownloaded;

    /* True when the manifest declares downloadable files that are currently downloaded */
    public bool CanDelete => !IsSeparator && HasDownloadableFiles && IsDownloaded;
}

public partial class SettingsViewModel : ViewModelBase
{
    public const string ModelNotInstalledMessage =
        "Model not installed. For download please head to the advanced settings menu";

    private readonly SettingsManager? _settingsManager;
    private readonly IAudioPlayer? _audioPlayer;
    private readonly IWindowService? _windowService;
    private readonly DownloadManager? _downloadManager;
    private readonly DictionaryManager? _dictManager;
    private readonly HotkeyManager? _hotkeyManager;
    private readonly HistoryManager? _historyManager;
    private readonly ITtsEngineProvider? _engineProvider;

    private bool _isLoading;

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

    /* Feedback shown under the model selectors (e.g. the selected model is not installed) */
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
    private bool _rememberPosition;

    [ObservableProperty]
    private bool _createMicOnStartup;

    [ObservableProperty]
    private string _customModelsDirectory = string.Empty;

    /* Hotkey Settings */
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleHotkeyDisplayText))]
    private string _toggleAppHotkey = "Ctrl+Alt+M";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhraseHotkeyDisplayText))]
    private string _newHotkeyKey = string.Empty;

    [ObservableProperty]
    private string _newHotkeyPhrase = string.Empty;

    /* Hotkey Recording State */
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleHotkeyDisplayText))]
    [NotifyPropertyChangedFor(nameof(ToggleRecordButtonText))]
    private bool _isRecordingToggleHotkey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhraseHotkeyDisplayText))]
    private bool _isRecordingPhraseHotkey;

    /* Dictionary Composer */
    [ObservableProperty]
    private string _newDictOriginal = string.Empty;

    [ObservableProperty]
    private string _newDictSpelling = string.Empty;

    [ObservableProperty]
    private bool _newDictCaseSensitive;

    /* Collections */
    public ObservableCollection<ModelMetadata> AvailableModels { get; } = new();
    public ObservableCollection<VoiceInfo> AvailableVoices { get; } = new();
    public ObservableCollection<AudioDeviceInfo> AvailableOutputDevices { get; } = new();
    public ObservableCollection<DictionaryEntry> DictionaryEntries { get; } = new();
    public ObservableCollection<HotkeyEntry> HotkeyEntries { get; } = new();
    public ObservableCollection<HistoryEntry> HistoryEntries { get; } = new();
    public ObservableCollection<ModelStatusItem> ModelStatuses { get; } = new();

    /* Constructor for previewer */
    protected SettingsViewModel()
        : this(null!, null!, null!, null!, null!, null!, null!, null!) { }

    /* Main Constructor */
    public SettingsViewModel(
        SettingsManager settingsManager,
        IAudioPlayer audioPlayer,
        IWindowService windowService,
        DownloadManager downloadManager,
        DictionaryManager? dictManager = null,
        HotkeyManager? hotkeyManager = null,
        HistoryManager? historyManager = null,
        ITtsEngineProvider? engineProvider = null
    )
    {
        _settingsManager = settingsManager;
        _audioPlayer = audioPlayer;
        _windowService = windowService;
        _downloadManager = downloadManager;
        _dictManager = dictManager;
        _hotkeyManager = hotkeyManager;
        _historyManager = historyManager;
        _engineProvider = engineProvider;

        if (_settingsManager != null && _audioPlayer != null)
        {
            LoadAudioDevices();
            LoadTtsEngines();
            LoadModelStatuses();
            LoadSettings();

            /* Voices come from the engine worker, so they are filled in once the engine finishes loading */
            _ = RefreshVoicesAsync();
        }
    }

    public string ToggleHotkeyDisplayText =>
        IsRecordingToggleHotkey
            ? "Press keys... (Esc to cancel)"
            : (string.IsNullOrWhiteSpace(ToggleAppHotkey) ? "None" : ToggleAppHotkey);

    public string PhraseHotkeyDisplayText =>
        IsRecordingPhraseHotkey
            ? "Press keys..."
            : (string.IsNullOrWhiteSpace(NewHotkeyKey) ? "Record shortcut..." : NewHotkeyKey);

    public string ToggleRecordButtonText => IsRecordingToggleHotkey ? "Cancel" : "Change";

    /* True only on Linux, where PulseAudio / PipeWire null-sink virtual microphones can be created */
    public bool IsLinux => OperatingSystem.IsLinux();

    /* Populates audio outputs from platform sink enumerator */
    private void LoadAudioDevices()
    {
        AvailableOutputDevices.Clear();

        if (_audioPlayer != null)
        {
            var result = _audioPlayer.GetAvailableDevices();
            if (result.IsSuccess && result.Value != null)
            {
                foreach (var dev in result.Value)
                {
                    if (AvailableOutputDevices.All(d => d.DeviceId != dev.DeviceId))
                        AvailableOutputDevices.Add(dev);
                }
            }
        }

        if (AvailableOutputDevices.All(d => d.DeviceId != "-1"))
        {
            AvailableOutputDevices.Insert(0, new AudioDeviceInfo
            {
                DeviceId = "-1",
                DeviceName = "Default System Device",
            });
        }
    }

    /* Populates every model offered by the installed engines grouped by engine with separators, preceded by the "Unselected" entry */
    private void LoadTtsEngines()
    {
        AvailableModels.Clear();
        AvailableModels.Add(ModelMetadata.Unselected);
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

    /* Populates model download statuses with engine separators and resource tags */
    private void LoadModelStatuses()
    {
        ModelStatuses.Clear();
        if (_downloadManager == null)
            return;

        foreach (var engine in _downloadManager.GetEngines())
        {
            if (engine.Models.Count == 0)
                continue;

            string engineTitle = !string.IsNullOrWhiteSpace(engine.DisplayName) ? engine.DisplayName : engine.Id;
            ModelStatuses.Add(new ModelStatusItem
            {
                IsSeparator = true,
                SeparatorTitle = engineTitle
            });

            foreach (var model in engine.Models)
            {
                string? cpu = model.CpuUsage?.Trim().ToLowerInvariant();
                ModelStatuses.Add(
                    new ModelStatusItem
                    {
                        Model = model.Id,
                        Name = model.DisplayName,
                        IsDownloaded = _downloadManager.IsModelDownloaded(model.Id),
                        HasDownloadableFiles = model.DownloadFiles.Count > 0,
                        Progress = 0,
                        SizeTag = model.Size.HasValue ? $"Size: {model.Size.Value} MB" : null,
                        RamUsageTag = model.RamUsage.HasValue ? $"Ram usage: {model.RamUsage.Value} MB" : null,
                        CpuUsageTag = !string.IsNullOrEmpty(model.CpuUsage) ? $"CPU usage: {model.CpuUsage}" : null,
                        IsCpuLow = cpu == "low",
                        IsCpuMedium = cpu == "medium",
                        IsCpuHigh = cpu == "high"
                    }
                );
            }
        }
    }

    /* Loads all configuration values and binds selection items */
    public void LoadSettings()
    {
        if (_settingsManager == null)
            return;

        _isLoading = true;
        try
        {
            var appConfig = _settingsManager.CurrentAppConfig;
            SelectedModel = AvailableModels.FirstOrDefault(m => m.Id == appConfig.ActiveModel) ?? ModelMetadata.Unselected;
            var engineConfig = !SelectedModel.IsUnselected
                ? _settingsManager.GetEngineConfig(SelectedModel.Id)
                : new EngineConfig();
            SelectVoiceFromConfig();

            EnablePrimaryPlayback = appConfig.EnablePrimaryPlayback;
            SelectedPrimaryDevice =
                AvailableOutputDevices.FirstOrDefault(d =>
                    d.DeviceId == appConfig.PrimaryAudioDeviceId
                ) ?? AvailableOutputDevices.FirstOrDefault();
            PrimaryVolume = appConfig.PrimaryVolume;

            EnableMonitorPlayback = appConfig.EnableMonitorPlayback;
            SelectedMonitorDevice =
                AvailableOutputDevices.FirstOrDefault(d =>
                    d.DeviceId == appConfig.MonitorAudioDeviceId
                ) ?? AvailableOutputDevices.FirstOrDefault(d => d.DeviceId == "-1");
            MonitorVolume = appConfig.MonitorVolume;

            SpeechSpeed = engineConfig.Speed > 0 ? engineConfig.Speed : 1.0f;
            SelectedMonitorIndex = appConfig.DefaultMonitor;
            OpenOnStartup = appConfig.OpenOnStartup;
            AlwaysOnTop = appConfig.AlwaysOnTop;
            HideOnSynth = appConfig.HideOnSynth;
            RememberPosition = appConfig.RememberPosition;
            CreateMicOnStartup = appConfig.CreateMicOnStartup;
            ToggleAppHotkey = appConfig.ToggleAppHotkey;
            CustomModelsDirectory = appConfig.CustomModelsDirectory ?? string.Empty;

            CancelRecording();

            if (_dictManager != null)
            {
                DictionaryEntries.Clear();
                foreach (var entry in _dictManager.GetEntries())
                    DictionaryEntries.Add(entry);
            }

            if (_hotkeyManager != null)
            {
                HotkeyEntries.Clear();
                foreach (var hk in _hotkeyManager.GetHotkeys())
                    HotkeyEntries.Add(hk);
            }

            if (_historyManager != null)
            {
                HistoryEntries.Clear();
                foreach (var h in _historyManager.GetHistory())
                    HistoryEntries.Add(h);
            }
        }
        finally
        {
            _isLoading = false;
        }
    }

    /* Auto-save triggers */
    partial void OnSelectedModelChanged(ModelMetadata? value)
    {
        if (_isLoading || value == null || value.IsSeparator)
            return;

        /* Every installed engine's models are listed, but only downloaded ones can be activated.
           Unselected has no files and is always allowed */
        if (!value.IsUnselected && _downloadManager != null && !_downloadManager.IsModelDownloaded(value.Id))
        {
            _ = RevertModelSelectionAsync();
            ShowModelStatusAsync(ModelNotInstalledMessage);
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

    /* Shows a transient message in the model status popup and clears it after a few seconds,
       unless a newer message replaced it in the meantime */
    private async void ShowModelStatusAsync(string message)
    {
        try
        {
            ModelStatusMessage = message;
            await Task.Delay(5000);
            if (ModelStatusMessage == message)
                ModelStatusMessage = null;
        }
        catch
        {
            /* Ignored */
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
            /* Model switch failed (e.g. the engine worker could not start) - revert dropdown without saving */
            await Console.Error.WriteLineAsync($"Model switch failed: {switchResult.ErrorMessage}");
            ShowModelStatusAsync(switchResult.ErrorMessage ?? "Model switch failed.");
            await RevertModelSelectionAsync();
        }
    }

    /* Restores the dropdown to the saved active model without saving. Yields first so the ComboBox finishes
       applying the user's selection before it is replaced */
    private async Task RevertModelSelectionAsync()
    {
        await Task.Yield();

        var activeModel = _settingsManager?.CurrentAppConfig.ActiveModel ?? ModelId.Unselected;
        _isLoading = true;
        try
        {
            SelectedModel = AvailableModels.FirstOrDefault(m => m.Id == activeModel) ?? ModelMetadata.Unselected;
        }
        finally
        {
            _isLoading = false;
        }
    }

    partial void OnSelectedVoiceChanged(VoiceInfo? value)
    {
        if (!_isLoading && _engineProvider != null && value != null)
        {
            _engineProvider.CurrentEngine.ActiveVoice = value.Id;
        }
        AutoSave();
    }

    partial void OnSpeechSpeedChanged(float value)
    {
        if (!_isLoading && _engineProvider != null)
        {
            _engineProvider.CurrentEngine.Speed = value;
        }
        AutoSave();
    }

    partial void OnToggleAppHotkeyChanged(string value)
    {
        if (_isLoading || _settingsManager == null)
            return;

        if (!ShortcutCombo.TryParse(value, out _))
        {
            _isLoading = true;
            try
            {
                ToggleAppHotkey = _settingsManager.CurrentAppConfig.ToggleAppHotkey;
            }
            finally
            {
                _isLoading = false;
            }
            return;
        }

        if (_hotkeyManager != null)
        {
            var result = _hotkeyManager.AssignToggleHotkey(value);
            if (result.IsFailure)
                return;
        }

        AutoSave();
    }

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

    partial void OnRememberPositionChanged(bool value) => AutoSave();

    partial void OnCreateMicOnStartupChanged(bool value) => AutoSave();

    partial void OnCustomModelsDirectoryChanged(string value) => AutoSave();

    /* Persists active settings immediately to disk and applies audio changes */
    private void AutoSave()
    {
        if (_isLoading || _settingsManager == null || _audioPlayer == null)
            return;

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
        appConfig.RememberPosition = RememberPosition;
        appConfig.CreateMicOnStartup = CreateMicOnStartup;
        appConfig.CustomModelsDirectory = string.IsNullOrWhiteSpace(CustomModelsDirectory)
            ? null
            : CustomModelsDirectory.Trim();

        _settingsManager.SaveAppConfig(appConfig);

        /* Voice and speed are remembered per model; Unselected has no engine settings */
        if (SelectedModel != null && !SelectedModel.IsUnselected)
        {
            var engineConfig = _settingsManager.GetEngineConfig(SelectedModel.Id);
            engineConfig.VoiceId = SelectedVoice?.Id ?? engineConfig.VoiceId;
            engineConfig.Speed = SpeechSpeed;
            _settingsManager.SaveEngineConfig(SelectedModel.Id, engineConfig);
        }

        _audioPlayer.SetOutputDevices(
            appConfig.PrimaryAudioDeviceId,
            appConfig.MonitorAudioDeviceId
        );
        _audioPlayer.SetVolume(appConfig.PrimaryVolume, appConfig.MonitorVolume);
    }

    /* Handles applying the recorded hotkey combination */
    public void ApplyRecordedHotkey(string shortcut)
    {
        if (IsRecordingToggleHotkey)
        {
            ToggleAppHotkey = shortcut;
            IsRecordingToggleHotkey = false;
        }
        else if (IsRecordingPhraseHotkey)
        {
            NewHotkeyKey = shortcut;
            IsRecordingPhraseHotkey = false;
        }
    }

    /* Cancels any ongoing hotkey recording */
    public void CancelRecording()
    {
        IsRecordingToggleHotkey = false;
        IsRecordingPhraseHotkey = false;
    }

    [RelayCommand]
    private void ToggleRecordingToggleHotkey()
    {
        if (IsRecordingToggleHotkey)
        {
            CancelRecording();
        }
        else
        {
            CancelRecording();
            IsRecordingToggleHotkey = true;
        }
    }

    [RelayCommand]
    private void ToggleRecordingPhraseHotkey()
    {
        if (IsRecordingPhraseHotkey)
        {
            CancelRecording();
        }
        else
        {
            CancelRecording();
            IsRecordingPhraseHotkey = true;
        }
    }

    [RelayCommand]
    private void ClearNewHotkeyKey()
    {
        NewHotkeyKey = string.Empty;
        CancelRecording();
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

    /* Dictionary Actions */
    [RelayCommand]
    private void AddDictionaryEntry()
    {
        if (
            _dictManager == null
            || string.IsNullOrWhiteSpace(NewDictOriginal)
            || string.IsNullOrWhiteSpace(NewDictSpelling)
        )
            return;
        var res = _dictManager.AddEntry(
            NewDictOriginal.Trim(),
            NewDictSpelling.Trim(),
            NewDictCaseSensitive
        );
        if (res.IsSuccess)
        {
            NewDictOriginal = string.Empty;
            NewDictSpelling = string.Empty;
            DictionaryEntries.Clear();
            foreach (var e in _dictManager.GetEntries())
                DictionaryEntries.Add(e);
        }
    }

    [RelayCommand]
    private void DeleteDictionaryEntry(Guid id)
    {
        if (_dictManager == null)
            return;
        var res = _dictManager.DeleteEntry(id);
        if (res.IsSuccess)
        {
            DictionaryEntries.Clear();
            foreach (var e in _dictManager.GetEntries())
                DictionaryEntries.Add(e);
        }
    }

    /* Hotkey Actions */
    [RelayCommand]
    private async Task AddHotkeyPhraseAsync()
    {
        if (
            _hotkeyManager == null
            || string.IsNullOrWhiteSpace(NewHotkeyKey)
            || string.IsNullOrWhiteSpace(NewHotkeyPhrase)
        )
            return;
        var res = await _hotkeyManager.AssignHotkeyAsync(
            NewHotkeyKey.Trim(),
            NewHotkeyPhrase.Trim()
        );
        if (res.IsSuccess)
        {
            NewHotkeyKey = string.Empty;
            NewHotkeyPhrase = string.Empty;
            HotkeyEntries.Clear();
            foreach (var hk in _hotkeyManager.GetHotkeys())
                HotkeyEntries.Add(hk);
        }
        else
        {
            ShowModelStatusAsync(res.ErrorMessage ?? "Failed to assign phrase shortcut.");
        }
    }

    [RelayCommand]
    private void DeleteHotkeyPhrase(Guid id)
    {
        if (_hotkeyManager == null)
            return;
        var res = _hotkeyManager.DeleteHotkey(id);
        if (res.IsSuccess)
        {
            HotkeyEntries.Clear();
            foreach (var hk in _hotkeyManager.GetHotkeys())
                HotkeyEntries.Add(hk);
        }
    }

    /* History Actions */
    [RelayCommand]
    private async Task PlayHistoryItemAsync(Guid id)
    {
        if (_historyManager == null)
            return;
        await _historyManager.PlayEntryAsync(id);
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        if (_historyManager == null)
            return;

        var result = await _historyManager.ClearHistory();
        if (result.IsSuccess)
            HistoryEntries.Clear();
    }

    [RelayCommand]
    private async Task DeleteHistoryItemAsync(Guid id)
    {
        if (_historyManager == null)
            return;

        var result = await _historyManager.DeleteEntry(id);
        if (result.IsSuccess)
        {
            HistoryEntries.Clear();
            foreach (var entry in _historyManager.GetHistory())
                HistoryEntries.Add(entry);
        }
    }

    /* Engine Management Actions */
    [RelayCommand]
    private async Task DownloadModelAsync(ModelStatusItem item)
    {
        if (_downloadManager == null || item == null || item.IsSeparator || item.IsDownloading)
            return;

        item.IsDownloading = true;
        try
        {
            var progress = new Progress<double>(p => item.Progress = p * 100);
            var result = await _downloadManager.DownloadModelAsync(item.Model, progress);
            item.IsDownloaded = result.IsSuccess;
            if (result.IsSuccess)
            {
                UpdateAvailableModelDownloadState(item.Model, true);
                await RefreshVoicesAsync();
            }
        }
        catch (Exception ex)
        {
            item.IsDownloaded = false;
            /* Surface this through a bound status property in the UI. */
            await Console.Error.WriteLineAsync($"Model download failed: {ex.Message}");
        }
        finally
        {
            item.IsDownloading = false;
        }
    }

    [RelayCommand]
    private void DeleteModel(ModelStatusItem item)
    {
        if (_downloadManager == null || item == null || item.IsSeparator)
            return;
        var res = _downloadManager.DeleteModel(item.Model);
        if (res.IsSuccess)
        {
            item.IsDownloaded = false;
            item.Progress = 0;
            UpdateAvailableModelDownloadState(item.Model, false);

            if (SelectedModel != null && SelectedModel.Id == item.Model)
            {
                SelectedModel = AvailableModels.FirstOrDefault(m => m.IsUnselected) ?? ModelMetadata.Unselected;
            }
        }
    }

    [RelayCommand]
    private void Close() => _windowService?.HideSettingsWindow();

    [RelayCommand]
    private void ExitApp() => _windowService?.ExitApplication();
}
