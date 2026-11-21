using MoonTTS.Domain.ValueObjects;

namespace MoonTTS.Domain.Entities;

public class AppConfig
{
    public string? CustomModelsDirectory { get; set; }
    public ModelId ActiveModel { get; set; } = ModelId.Unselected;
    public bool AlwaysOnTop { get; set; } = true; /* Keeps the HUD above other windows, including fullscreen / borderless apps */
    public bool HideOnSynth { get; set; } = false;
    public bool QueueSpeech { get; set; } = false; /* Queues consecutive utterances instead of interrupting active speech */
    public bool OpenOnStartup { get; set; } = false;
    public int DefaultMonitor { get; set; } = 0; /* Display monitor to start the app at */
    public bool RememberPosition { get; set; } = false;
    public int PosX { get; set; }
    public int PosY { get; set; }
    
    /* Hotkey for showing / hiding the app window */
    public string ToggleAppHotkey { get; set; } = "Ctrl+Alt+M";
    
    /* Virtual Mic (ONLY LINUX) */
    public bool CreateMicOnStartup { get; set; } = false;
    
    
    /* Primary audio output */
    public bool EnablePrimaryPlayback { get; set; } = true;
    public string? PrimaryAudioDeviceId { get; set; } = "-1"; /* -1 Is the default for an OS */
    public int PrimaryVolume { get; set; } = 100;
    
    /* Monitoring audio output */
    public bool EnableMonitorPlayback { get; set; } = false;
    public string? MonitorAudioDeviceId { get; set; } = null;
    public int MonitorVolume { get; set; } = 100;
    
    /* History */
    public bool SaveHistory { get; set; } = true;

    /* UI display language code (e.g. "en", "es") */
    public string Language { get; set; } = "en";
}