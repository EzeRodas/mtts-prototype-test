using MoonTTS.Domain.ValueObjects;

namespace MoonTTS.Domain.Interfaces;

public interface IModelRegistry
{
    /* Returns every model offered by the installed engines */
    IEnumerable<ModelMetadata> GetAvailableModels();

    /* Returns every installed engine and its descriptors */
    IReadOnlyList<EngineDescriptor> GetEngines();

    /* Looks up the metadata for a specific model */
    ModelMetadata? GetModelMetadata(ModelId modelId);

    /* Resolves user input ("<engine>/<model>" or an unambiguous model name), ignoring case */
    ModelMetadata? FindModel(string query);

    /* Looks up the installed engine that owns a model */
    EngineDescriptor? GetEngine(string engineId);

    /* Re-scans the engines directory for newly installed or removed engine manifests */
    void Refresh();
}
