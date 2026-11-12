namespace MoonTTS.Engine.Protocol;

/* On-disk files that describe an installed engine inside "<data>/Moon-TTS/engines/<Name>/" */

/* engine.json: identity of the engine, its worker executable and the models it can load */
public sealed class EngineManifest
{
    public const string FileName = "engine.json";

    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Version { get; set; }

    /* Path of the worker executable, relative to the engine folder. ".exe" is appended on Windows when missing */
    public string Executable { get; set; } = string.Empty;
    public List<EngineModelManifest> Models { get; set; } = new();
}

/* One downloadable model. DownloadFiles maps the local file name to its remote URL */
public sealed class EngineModelManifest
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int? Size { get; set; }
    public int? RamUsage { get; set; }
    public string? CpuUsage { get; set; }
    public List<string>? Languages { get; set; }
    public Dictionary<string, string> DownloadFiles { get; set; } = new();
}

/* voices.json: voices per model id. Written by the host from the ListVoices reply when an entry is missing */
public sealed class VoiceCatalogFile
{
    public const string FileName = "voices.json";

    public Dictionary<string, List<VoiceEntry>> Models { get; set; } = new();
}
