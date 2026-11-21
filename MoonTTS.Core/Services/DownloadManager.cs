using MoonTTS.Domain.Common;
using MoonTTS.Domain.Interfaces;
using MoonTTS.Domain.ValueObjects;

namespace MoonTTS.Core.Services;

/* File Structure:
        - DownloadManager(
            IModelRegistry modelRegistry,
            IModelDownloader modelDownloader,
            SettingsManager settingsManager)
        - GetAvailableModels() : IEnumerable<ModelMetadata>
        - GetEngines() : IReadOnlyList<EngineDescriptor>
        - RefreshEngines() : void
        - IsModelDownloaded(ModelId modelId) : bool
        - DownloadModelAsync(
            ModelId modelId,
            IProgress<double>? progress,
            CancellationToken cancellationToken) : Task<Result>
        - DeleteModel(ModelId modelId) : Result
*/

public class DownloadManager
{
    private readonly IModelRegistry _modelRegistry;
    private readonly IModelDownloader _modelDownloader;
    private readonly SettingsManager _settingsManager;

    /* Gets the values through dependency injection */
    public DownloadManager(
        IModelRegistry modelRegistry,
        IModelDownloader modelDownloader,
        SettingsManager settingsManager
    )
    {
        _modelRegistry = modelRegistry;
        _modelDownloader = modelDownloader;
        _settingsManager = settingsManager;
    }

    /* Returns all supported models */
    public IEnumerable<ModelMetadata> GetAvailableModels()
    {
        return _modelRegistry.GetAvailableModels();
    }

    /* Returns all installed engines and their descriptors */
    public IReadOnlyList<EngineDescriptor> GetEngines()
    {
        return _modelRegistry.GetEngines();
    }

    /* Re-scans the engines directory to pick up newly added or removed engine folders */
    public void RefreshEngines() => _modelRegistry.Refresh();

    /* Checks if the file exists */
    public bool IsModelDownloaded(ModelId modelId)
    {
        var metadata = _modelRegistry.GetModelMetadata(modelId);
        if (metadata == null)
            return false;

        string modelsDir = _settingsManager.GetModelDirectory(modelId);

        foreach (string expectedFileName in metadata.DownloadFiles.Keys)
        {
            string expectedPath = Path.Combine(modelsDir, expectedFileName);
            if (!File.Exists(expectedPath))
            {
                return false;
            }
        }
        return true;
    }

    /* Manages the entire download process */
    public async Task<Result> DownloadModelAsync(
        ModelId modelId,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        /* Verifies the model is supported */
        var metadata = _modelRegistry.GetModelMetadata(modelId);
        if (metadata == null)
        {
            return Result.Failure(
                $"Model '{modelId}' is not provided by any installed engine."
            );
        }

        /* Gets the destination folder (one sub folder per engine) */
        string destinationFolder = _settingsManager.GetModelDirectory(modelId);
        Directory.CreateDirectory(destinationFolder);

        /* Commands the infrastructure to perform the download */
        return await _modelDownloader.DownloadModelAsync(
            metadata,
            destinationFolder,
            progress,
            cancellationToken
        );
    }

    public Result DeleteModel(ModelId modelId)
    {
        /* Checks the model is registered */
        var metadata = _modelRegistry.GetModelMetadata(modelId);
        if (metadata == null)
            return Result.Failure($"Model '{modelId}' is not provided by any installed engine.");

        string modelsDir = _settingsManager.GetModelDirectory(modelId);
        if (!Directory.Exists(modelsDir))
            return Result.Failure($"No files found for model '{modelId}'.");

        bool anyDeleted = false;

        /* Checks that the file to be deleted is not required by any other model
           (Ex. many models might share the same voice file, so unless all of them are deleted, it'll keep that
           shared file) */
        try
        {
            foreach (string fileName in metadata.DownloadFiles.Keys)
            {
                string filePath = Path.Combine(modelsDir, fileName);
                if (!File.Exists(filePath))
                    continue;

                /* Filters to see if the other models share that key within their registry */
                bool isSharedWithDownloadedModel = _modelRegistry
                    .GetAvailableModels()
                    .Where(m => m.Id != modelId && m.Id.EngineId == modelId.EngineId)
                    .Where(m => IsModelDownloaded(m.Id))
                    .Any(m => m.DownloadFiles.ContainsKey(fileName));

                if (!isSharedWithDownloadedModel)
                {
                    File.Delete(filePath);
                    anyDeleted = true;
                }
            }
            if (!anyDeleted)
                return Result.Failure($"No files found for model '{modelId}'.");

            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Failed to delete model files: {ex.Message}", ex);
        }
    }
}
