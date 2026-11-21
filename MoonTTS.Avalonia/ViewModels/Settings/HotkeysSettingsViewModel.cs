using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoonTTS.Core.Services;
using MoonTTS.Domain.Entities;
using MoonTTS.Domain.ValueObjects;

namespace MoonTTS.Avalonia.ViewModels.Settings;

/* File Structure:
        - HotkeysSettingsViewModel(SettingsManager?, HotkeyManager?, ProfileManager?, Action, Action<string>)
        - IsEditingProfile : bool
        - ToggleHotkeyDisplayText : string
        - PhraseHotkeyDisplayText : string
        - ToggleRecordButtonText : string
        - Load() : void
        - LoadProfiles() : void
        - RefreshHotkeyEntries() : void
        - RefreshLocalizedProperties() : void
        - ApplyRecordedHotkey(string) : void
        - CancelRecording() : void
        - ToggleRecordingToggleHotkey() : void
        - ToggleRecordingPhraseHotkey() : void
        - ClearNewHotkeyKey() : void
        - StartAddProfile() : void
        - StartRenameProfile() : void
        - ConfirmAddProfile() : void
        - CancelAddProfile() : void
        - DeleteProfile() : void
        - AddHotkeyPhraseAsync() : Task
        - PlayHotkeyPhraseAsync(Guid) : Task
        - DeleteHotkeyPhrase(Guid) : void
*/

/* Manages Global Hotkeys, interactive shortcut recording, soundboard profiles, and phrase previews */
public partial class HotkeysSettingsViewModel : ViewModelBase
{
    private readonly SettingsManager? _settingsManager;
    private readonly HotkeyManager? _hotkeyManager;
    private readonly ProfileManager? _profileManager;
    private readonly Action _onConfigChanged;
    private readonly Action<string> _showStatus;
    private bool _isLoading;

    [ObservableProperty]
    private HotkeyProfile? _selectedProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingProfile))]
    private bool _isAddingProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingProfile))]
    private bool _isRenamingProfile;

    [ObservableProperty]
    private string _newProfileName = string.Empty;

    /* True when either creating a new profile or renaming the selected profile via the editable row */
    public bool IsEditingProfile => IsAddingProfile || IsRenamingProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleHotkeyDisplayText))]
    private string _toggleAppHotkey = "Ctrl+Alt+M";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhraseHotkeyDisplayText))]
    private string _newHotkeyKey = string.Empty;

    [ObservableProperty]
    private string _newHotkeyPhrase = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleHotkeyDisplayText))]
    [NotifyPropertyChangedFor(nameof(ToggleRecordButtonText))]
    private bool _isRecordingToggleHotkey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhraseHotkeyDisplayText))]
    private bool _isRecordingPhraseHotkey;

    public ObservableCollection<HotkeyProfile> AvailableProfiles { get; } = new();
    public ObservableCollection<HotkeyEntry> HotkeyEntries { get; } = new();

    /* Returns the localized display text for the global toggle shortcut box */
    public string ToggleHotkeyDisplayText =>
        IsRecordingToggleHotkey
            ? L.Strings.PressKeysEscToCancel
            : (string.IsNullOrWhiteSpace(ToggleAppHotkey) ? L.Strings.NoneText : ToggleAppHotkey);

    /* Returns the localized display text for the phrase shortcut recorder box */
    public string PhraseHotkeyDisplayText =>
        IsRecordingPhraseHotkey
            ? L.Strings.PressKeys
            : (string.IsNullOrWhiteSpace(NewHotkeyKey) ? L.Strings.RecordShortcut : NewHotkeyKey);

    /* Returns the localized label for the global toggle shortcut record/cancel button */
    public string ToggleRecordButtonText => IsRecordingToggleHotkey ? L.Strings.CancelButton : L.Strings.ChangeButton;

    /* Initializes hotkeys and profiles settings */
    public HotkeysSettingsViewModel(
        SettingsManager? settingsManager,
        HotkeyManager? hotkeyManager,
        ProfileManager? profileManager,
        Action onConfigChanged,
        Action<string> showStatus)
    {
        _settingsManager = settingsManager;
        _hotkeyManager = hotkeyManager;
        _profileManager = profileManager;
        _onConfigChanged = onConfigChanged;
        _showStatus = showStatus;
    }

    /* Loads toggle shortcut, soundboard profiles, and active profile hotkey entries */
    public void Load()
    {
        _isLoading = true;
        try
        {
            if (_settingsManager != null)
                ToggleAppHotkey = _settingsManager.CurrentAppConfig.ToggleAppHotkey;

            CancelRecording();
            IsAddingProfile = false;
            IsRenamingProfile = false;
            NewProfileName = string.Empty;

            LoadProfiles();
            RefreshHotkeyEntries();
        }
        finally
        {
            _isLoading = false;
        }
    }

    /* Populates available hotkey profiles and selects the currently active profile */
    public void LoadProfiles()
    {
        if (_profileManager == null)
            return;

        AvailableProfiles.Clear();
        foreach (var profile in _profileManager.GetProfiles())
            AvailableProfiles.Add(profile);

        var active = _profileManager.GetActiveProfile();
        SelectedProfile = AvailableProfiles.FirstOrDefault(p =>
            string.Equals(p.Name, active.Name, StringComparison.OrdinalIgnoreCase))
            ?? AvailableProfiles.FirstOrDefault();
    }

    /* Reloads the hotkey entries collection for the currently active profile */
    public void RefreshHotkeyEntries()
    {
        if (_hotkeyManager == null)
            return;

        HotkeyEntries.Clear();
        foreach (var hk in _hotkeyManager.GetHotkeys())
            HotkeyEntries.Add(hk);
    }

    /* Refreshes computed localized button/recorder strings on live language change */
    public void RefreshLocalizedProperties()
    {
        OnPropertyChanged(nameof(ToggleHotkeyDisplayText));
        OnPropertyChanged(nameof(PhraseHotkeyDisplayText));
        OnPropertyChanged(nameof(ToggleRecordButtonText));
    }

    /* Validates and persists changes to the global app toggle shortcut */
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

        _onConfigChanged();
    }

    /* Switches the active hotkey profile when selected from the ComboBox */
    partial void OnSelectedProfileChanged(HotkeyProfile? value)
    {
        if (_isLoading || _profileManager == null || value == null)
            return;

        var res = _profileManager.SetActiveProfile(value.Name);
        if (res.IsSuccess)
            RefreshHotkeyEntries();
        else
            _showStatus(res.ErrorMessage ?? L.Strings.ProfileSwitchFailed);
    }

    /* Applies a recorded shortcut string to whichever recorder is active */
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

    /* Enters the inline editable row mode to create a new hotkey profile */
    [RelayCommand]
    private void StartAddProfile()
    {
        NewProfileName = string.Empty;
        IsRenamingProfile = false;
        IsAddingProfile = true;
    }

    /* Enters the inline editable row mode to rename the currently selected hotkey profile */
    [RelayCommand]
    private void StartRenameProfile()
    {
        if (SelectedProfile == null)
            return;

        NewProfileName = SelectedProfile.Name;
        IsAddingProfile = false;
        IsRenamingProfile = true;
    }

    /* Confirms creating a new profile or renaming the selected profile */
    [RelayCommand]
    private void ConfirmAddProfile()
    {
        if (_profileManager == null || string.IsNullOrWhiteSpace(NewProfileName))
            return;

        string targetName = NewProfileName.Trim();

        if (IsRenamingProfile)
        {
            if (SelectedProfile == null)
                return;

            var renameRes = _profileManager.RenameProfile(SelectedProfile.Name, targetName);
            if (renameRes.IsSuccess)
            {
                NewProfileName = string.Empty;
                IsRenamingProfile = false;

                _isLoading = true;
                try
                {
                    LoadProfiles();
                    RefreshHotkeyEntries();
                }
                finally
                {
                    _isLoading = false;
                }
            }
            else
            {
                _showStatus(renameRes.ErrorMessage ?? L.Strings.ProfileRenameFailed);
            }
            return;
        }

        var createRes = _profileManager.CreateProfile(targetName);
        if (createRes.IsSuccess)
        {
            _profileManager.SetActiveProfile(targetName);
            NewProfileName = string.Empty;
            IsAddingProfile = false;

            _isLoading = true;
            try
            {
                LoadProfiles();
                RefreshHotkeyEntries();
            }
            finally
            {
                _isLoading = false;
            }
        }
        else
        {
            _showStatus(createRes.ErrorMessage ?? L.Strings.ProfileCreateFailed);
        }
    }

    /* Cancels creating or renaming a hotkey profile */
    [RelayCommand]
    private void CancelAddProfile()
    {
        NewProfileName = string.Empty;
        IsAddingProfile = false;
        IsRenamingProfile = false;
    }

    /* Deletes the currently selected hotkey profile and refreshes the active profile and its hotkeys */
    [RelayCommand]
    private void DeleteProfile()
    {
        if (_profileManager == null || SelectedProfile == null)
            return;

        var res = _profileManager.DeleteProfile(SelectedProfile.Name);
        if (res.IsSuccess)
        {
            _isLoading = true;
            try
            {
                LoadProfiles();
                RefreshHotkeyEntries();
            }
            finally
            {
                _isLoading = false;
            }
        }
        else
        {
            _showStatus(res.ErrorMessage ?? L.Strings.ProfileDeleteFailed);
        }
    }

    /* Pre-renders and binds a new phrase hotkey */
    [RelayCommand]
    private async Task AddHotkeyPhraseAsync()
    {
        if (_hotkeyManager == null
            || string.IsNullOrWhiteSpace(NewHotkeyKey)
            || string.IsNullOrWhiteSpace(NewHotkeyPhrase))
        {
            return;
        }

        var res = await _hotkeyManager.AssignHotkeyAsync(
            NewHotkeyKey.Trim(),
            NewHotkeyPhrase.Trim());

        if (res.IsSuccess)
        {
            NewHotkeyKey = string.Empty;
            NewHotkeyPhrase = string.Empty;
            RefreshHotkeyEntries();
        }
        else
        {
            _showStatus(res.ErrorMessage ?? L.Strings.PhraseAssignFailed);
        }
    }

    /* Plays pre-rendered audio for an existing hotkey phrase preview */
    [RelayCommand]
    private async Task PlayHotkeyPhraseAsync(Guid id)
    {
        if (_hotkeyManager == null)
            return;

        var res = await _hotkeyManager.PlayHotkeyAsync(id);
        if (res.IsFailure && res.ErrorMessage != SpeechPipeline.CancelledMessage)
            _showStatus(res.ErrorMessage ?? L.Strings.PhrasePlayFailed);
    }

    /* Deletes an assigned phrase hotkey */
    [RelayCommand]
    private void DeleteHotkeyPhrase(Guid id)
    {
        if (_hotkeyManager == null)
            return;

        var res = _hotkeyManager.DeleteHotkey(id);
        if (res.IsSuccess)
            RefreshHotkeyEntries();
    }
}
