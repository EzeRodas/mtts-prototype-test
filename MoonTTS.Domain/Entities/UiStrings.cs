namespace MoonTTS.Domain.Entities;

/* Strongly-typed UI localization strings with English fallback defaults */
public sealed class UiStrings
{
    /* Tray Menu & App */
    public string TrayOpen { get; init; } = "Open Moon-TTS";
    public string TraySettings { get; init; } = "Settings";
    public string TrayExit { get; init; } = "Exit";

    /* MainWindow HUD */
    public string AboutTooltip { get; init; } = "About";
    public string AboutAria { get; init; } = "About Moon-TTS on GitHub";
    public string DragWindowAria { get; init; } = "Drag Window";
    public string OpenSettingsAria { get; init; } = "Open Settings";
    public string InputBoxAria { get; init; } = "Text to synthesize";
    public string InsertTextPlaceholder { get; init; } = "Insert text to synthesize";
    public string PauseResumeAria { get; init; } = "Pause or Resume Playback";
    public string StopPlaybackAria { get; init; } = "Stop Playback";
    public string SpeakTextAria { get; init; } = "Speak Text";
    public string HideWindowAria { get; init; } = "Hide Window";
    public string NoModelSelected { get; init; } = "Please select a model before synthesizing.";
    public string SpeechSynthesisFailed { get; init; } = "Speech synthesis failed.";

    /* Quick Settings */
    public string QuickSettingsTitle { get; init; } = "Quick settings";
    public string SwitchSettingsModeAria { get; init; } = "Switch Settings Mode";
    public string AdvancedSettingsTooltip { get; init; } = "Advanced Settings";
    public string CloseSettingsAria { get; init; } = "Close Settings";
    public string CloseEscapeTooltip { get; init; } = "Close (Escape)";
    public string ModelLabel { get; init; } = "Model";
    public string EngineManagementInfoAria { get; init; } = "Engine Management Information";
    public string DownloadEngineInfoTooltip { get; init; } = "To download a TTS Engine please head to the \"Engine Management\" tab on the Advanced Settings menu.";
    public string DownloadEngineHoverTooltip { get; init; } = "To download a TTS Engine please head to the \"Engine Management\" tab on the Advanced Settings menu. Click to open it.";
    public string VoiceLabel { get; init; } = "Voice";
    public string LoadingVoices { get; init; } = "Loading...";
    public string AudioOutputLabel { get; init; } = "Audio Output";
    public string MonitoringLabel { get; init; } = "Monitoring";
    public string UnselectedModel { get; init; } = "Unselected";
    public string DefaultSystemDevice { get; init; } = "Default System Device";

    /* Advanced Sidebar */
    public string BackToQuickSettings { get; init; } = "< Quick Settings";
    public string ExitMoonTts { get; init; } = "Exit Moon-TTS";
    public string AdvancedHeader { get; init; } = "ADVANCED";
    public string GeneralTab { get; init; } = "General";
    public string AudioTab { get; init; } = "Audio";
    public string DictionaryTab { get; init; } = "Dictionary";
    public string TtsEngineTab { get; init; } = "TTS Engine";
    public string EngineManagementTab { get; init; } = "Engine Management";
    public string HotkeysTab { get; init; } = "Hotkeys";
    public string HistoryTab { get; init; } = "History";

    /* General Tab */
    public string GeneralSettingsTitle { get; init; } = "General Settings";
    public string GeneralSettingsDesc { get; init; } = "Configure startup display and window behavior.";
    public string LanguageLabel { get; init; } = "Language";
    public string AlwaysOnTopCheck { get; init; } = "Keep HUD always on top of other windows, including fullscreen and borderless apps";
    public string HideOnSynthCheck { get; init; } = "Hide HUD automatically when speech begins";
    public string QueueSpeechCheck { get; init; } = "Queue speech messages instead of interrupting active playback";
    public string RememberPositionCheck { get; init; } = "Remember HUD window coordinates on screen";
    public string StartMinimizedCheck { get; init; } = "Start minimized to system tray";
    public string CreateVirtualMicCheck { get; init; } = "Create Linux Virtual Microphone null-sink on startup";
    public string OpenLogsButton { get; init; } = "Open Logs Folder";

    /* Audio Tab */
    public string AudioSettingsTitle { get; init; } = "Audio Settings";
    public string AudioPlaybackLabel { get; init; } = "Audio Playback";
    public string AudioMonitoringVirtualMicLabel { get; init; } = "Audio Monitoring (Virtual Mic)";

    /* Dictionary Tab */
    public string PhoneticDictionaryTitle { get; init; } = "Phonetic Dictionary";
    public string AddWordReplacementLabel { get; init; } = "Add Word Replacement";
    public string OriginalWordPlaceholder { get; init; } = "Original word";
    public string SpellingReplacementPlaceholder { get; init; } = "Spelling replacement";
    public string AddButton { get; init; } = "Add";
    public string CaseSensitiveCheck { get; init; } = "Case sensitive";
    public string DeleteDictionaryEntryAria { get; init; } = "Delete Dictionary Entry";
    public string DeleteButton { get; init; } = "Delete";

    /* TTS Engine Tab */
    public string TtsEngineConfigTitle { get; init; } = "TTS Engine Configuration";
    public string ActiveModelLabel { get; init; } = "Active Model";
    public string VoicePresetLabel { get; init; } = "Voice Preset";
    public string SynthesisSpeedLabel { get; init; } = "Synthesis Speed";
    public string CustomModelsDirectoryLabel { get; init; } = "Custom Models Directory";
    public string CustomModelsDirectoryPlaceholder { get; init; } = "Default (~/.local/share/Moon-TTS/models/)";

    /* Engine Management Tab */
    public string EngineManagementTitle { get; init; } = "Engine Management";
    public string EngineManagementDesc { get; init; } = "Manage, download, and delete local neural model files.";
    public string InstalledBadge { get; init; } = "✓ Installed";
    public string DownloadButton { get; init; } = "Download";
    public string CancelDownloadButton { get; init; } = "Cancel";
    public string RescanEnginesButton { get; init; } = "Rescan Engines";
    public string ModelDownloadFailed { get; init; } = "Model download failed.";
    public string SizeTagFormat { get; init; } = "Size: {0} MB";
    public string RamUsageTagFormat { get; init; } = "Ram usage: {0} MB";
    public string CpuUsageTagFormat { get; init; } = "CPU usage: {0}";
    public string SingleLanguageTagFormat { get; init; } = "Language: {0}";
    public string MultipleLanguagesTagFormat { get; init; } = "Languages: {0}";

    /* Hotkeys Tab */
    public string GlobalHotkeysTitle { get; init; } = "Global Hotkeys";
    public string GlobalToggleShortcutLabel { get; init; } = "Global Toggle Shortcut";
    public string ActiveProfileLabel { get; init; } = "Active Profile";
    public string RenameButton { get; init; } = "Rename";
    public string ProfileNamePlaceholder { get; init; } = "Profile name...";
    public string SaveButton { get; init; } = "Save";
    public string CancelButton { get; init; } = "Cancel";
    public string ChangeButton { get; init; } = "Change";
    public string PressKeysEscToCancel { get; init; } = "Press keys... (Esc to cancel)";
    public string PressKeys { get; init; } = "Press keys...";
    public string RecordShortcut { get; init; } = "Record shortcut...";
    public string NoneText { get; init; } = "None";
    public string AddPhraseShortcutLabel { get; init; } = "Add Phrase Shortcut";
    public string PhraseToSynthesizePlaceholder { get; init; } = "Phrase to synthesize";
    public string PlayHotkeyPhraseAria { get; init; } = "Play Hotkey Phrase";
    public string DeleteHotkeyAria { get; init; } = "Delete Hotkey";
    public string PlayButton { get; init; } = "Play";

    /* History Tab */
    public string SynthesisHistoryTitle { get; init; } = "Synthesis History";
    public string ClearAllButton { get; init; } = "Clear All";
    public string PlayHistoryEntryAria { get; init; } = "Play History Entry";
    public string DeleteHistoryEntryAria { get; init; } = "Delete History Entry";

    /* Status & Error Messages */
    public string ModelNotInstalled { get; init; } = "Model not installed. For download please head to the advanced settings menu";
    public string ModelSwitchFailed { get; init; } = "Model switch failed.";
    public string ProfileSwitchFailed { get; init; } = "Failed to switch profile.";
    public string ProfileRenameFailed { get; init; } = "Failed to rename profile.";
    public string ProfileCreateFailed { get; init; } = "Failed to create profile.";
    public string ProfileDeleteFailed { get; init; } = "Failed to delete profile.";
    public string PhraseAssignFailed { get; init; } = "Failed to assign phrase shortcut.";
    public string PhrasePlayFailed { get; init; } = "Failed to play hotkey phrase.";
}
