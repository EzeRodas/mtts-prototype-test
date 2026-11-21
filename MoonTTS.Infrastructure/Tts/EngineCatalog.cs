using System.Text.Json;
using MoonTTS.Core.Utilities;
using MoonTTS.Domain.Interfaces;
using MoonTTS.Domain.ValueObjects;
using MoonTTS.Engine.Protocol;

namespace MoonTTS.Infrastructure.Tts;

/* File Structure:
        - EngineCatalog(string? enginesDirectory)
        - GetAvailableModels() : IEnumerable<ModelMetadata>
        - GetEngines() : IReadOnlyList<EngineDescriptor>
        - GetModelMetadata(ModelId modelId) : ModelMetadata?
        - FindModel(string query) : ModelMetadata?
        - GetEngine(string engineId) : EngineDescriptor?
        - Refresh() : void
        - GetCachedEngines() : IReadOnlyList<EngineDescriptor>
        - ScanEngines(string enginesDirectory) : IReadOnlyList<EngineDescriptor>
        - TryLoadEngine(string engineDirectory) : EngineDescriptor?
        - TryResolveExecutable(string engineDirectory, string executable) : string?
*/

/* Discovers installed engines by scanning "<data>/Moon-TTS/engines/<Name>/engine.json".
   Engines are never referenced by the host code; adding one only requires dropping its folder in place */
public class EngineCatalog : IModelRegistry
{
    private readonly string _enginesDirectory;
    private readonly Lock _cacheLock = new();
    private IReadOnlyList<EngineDescriptor>? _cachedEngines;

    /* Scans the given folder, or the platform engines folder when none is provided */
    public EngineCatalog(string? enginesDirectory = null)
    {
        _enginesDirectory = enginesDirectory ?? PathResolver.GetEnginesDirectory();
    }

    /* Returns every model of every installed engine */
    public IEnumerable<ModelMetadata> GetAvailableModels() => GetCachedEngines().SelectMany(e => e.Models);

    /* Returns every installed engine and its descriptors */
    public IReadOnlyList<EngineDescriptor> GetEngines() => GetCachedEngines();

    /* Looks up a model by its exact id */
    public ModelMetadata? GetModelMetadata(ModelId modelId) =>
        modelId.IsUnselected ? null : GetAvailableModels().FirstOrDefault(m => m.Id == modelId);

    /* Resolves "<engine>/<model>" or an unambiguous model name, ignoring case */
    public ModelMetadata? FindModel(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        string trimmed = query.Trim();
        var models = GetAvailableModels().ToList();

        var exact = models.FirstOrDefault(m =>
            string.Equals(m.Id.ToString(), trimmed, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return exact;

        var byName = models
            .Where(m => string.Equals(m.Id.ModelName, trimmed, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return byName.Count == 1 ? byName[0] : null;
    }

    /* Looks up an installed engine by id */
    public EngineDescriptor? GetEngine(string engineId) =>
        GetCachedEngines().FirstOrDefault(e => string.Equals(e.Id, engineId, StringComparison.Ordinal));

    /* Re-scans the engines directory on disk to pick up newly added or removed engine folders */
    public void Refresh()
    {
        var scanned = ScanEngines(_enginesDirectory);
        lock (_cacheLock)
        {
            _cachedEngines = scanned;
        }
    }

    /* Lazily scans and caches installed engine descriptors */
    private IReadOnlyList<EngineDescriptor> GetCachedEngines()
    {
        lock (_cacheLock)
        {
            _cachedEngines ??= ScanEngines(_enginesDirectory);
            return _cachedEngines;
        }
    }

    /* Loads every valid engine folder. Invalid manifests are reported and skipped so one broken engine
       never hides the others */
    private static IReadOnlyList<EngineDescriptor> ScanEngines(string enginesDirectory)
    {
        var engines = new List<EngineDescriptor>();
        if (!Directory.Exists(enginesDirectory))
            return engines;

        foreach (string engineDirectory in Directory.EnumerateDirectories(enginesDirectory).Order(StringComparer.Ordinal))
        {
            var engine = TryLoadEngine(engineDirectory);
            if (engine == null)
                continue;

            if (engines.Any(e => e.Id == engine.Id))
            {
                AppLogger.Warning("EngineCatalog", $"Skipping '{engineDirectory}': engine id '{engine.Id}' is already installed.");
                continue;
            }
            engines.Add(engine);
        }
        return engines;
    }

    /* Parses and validates one engine.json. Returns null when the folder is not a usable engine */
    private static EngineDescriptor? TryLoadEngine(string engineDirectory)
    {
        string manifestPath = Path.Combine(engineDirectory, EngineManifest.FileName);
        if (!File.Exists(manifestPath))
            return null;

        try
        {
            using var stream = File.OpenRead(manifestPath);
            var manifest = JsonSerializer.Deserialize(stream, EngineProtocolJsonContext.Default.EngineManifest);

            if (manifest == null
                || string.IsNullOrWhiteSpace(manifest.Id)
                || manifest.Id.Contains(ModelId.Separator)
                || string.IsNullOrWhiteSpace(manifest.Executable))
            {
                AppLogger.Warning("EngineCatalog", $"'{manifestPath}' needs a valid 'id' and 'executable'.");
                return null;
            }

            string? executablePath = TryResolveExecutable(engineDirectory, manifest.Executable);
            if (executablePath == null)
            {
                AppLogger.Warning("EngineCatalog", $"Skipping '{manifestPath}': executable '{manifest.Executable}' escapes the engine folder.");
                return null;
            }

            var models = manifest.Models
                .Where(m => !string.IsNullOrWhiteSpace(m.Id))
                .Select(m => new ModelMetadata(
                    new ModelId(manifest.Id, m.Id),
                    string.IsNullOrWhiteSpace(m.DisplayName) ? m.Id : m.DisplayName,
                    new Dictionary<string, string>(m.DownloadFiles),
                    m.Size,
                    m.RamUsage,
                    m.CpuUsage,
                    m.Languages,
                    FileChecksums: m.FileChecksums != null
                        ? new Dictionary<string, string>(m.FileChecksums, StringComparer.OrdinalIgnoreCase)
                        : null))
                .ToList();

            return new EngineDescriptor(
                manifest.Id,
                string.IsNullOrWhiteSpace(manifest.DisplayName) ? manifest.Id : manifest.DisplayName,
                engineDirectory,
                executablePath,
                models);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLogger.Error("EngineCatalog", $"Failed to read '{manifestPath}'", ex);
            return null;
        }
    }

    /* Resolves the worker path relative to the engine folder while preventing path traversal outside it */
    private static string? TryResolveExecutable(string engineDirectory, string executable)
    {
        string normalizedRoot = Path.GetFullPath(engineDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(engineDirectory, executable));

        StringComparison cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!path.StartsWith(normalizedRoot, cmp))
            return null;

        if (OperatingSystem.IsWindows() && !File.Exists(path) && File.Exists(path + ".exe"))
            return path + ".exe";

        return path;
    }
}
