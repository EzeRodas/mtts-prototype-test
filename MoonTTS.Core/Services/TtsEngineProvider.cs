using MoonTTS.Core.Helpers;
using MoonTTS.Core.Utilities;
using MoonTTS.Domain.Common;
using MoonTTS.Domain.Interfaces;
using MoonTTS.Domain.ValueObjects;

namespace MoonTTS.Core.Services;

/* File Structure:
        - TtsEngineProvider(
            ITtsEngineFactory engineFactory,
            SettingsManager settingsManager,
            DownloadManager? downloadManager,
            IModelRegistry? modelRegistry)
        - CurrentEngine : ITtsEngine
        - GetEngineAsync(CancellationToken cancellationToken) : Task<ITtsEngine>
        - SetEngineAsync(ModelId modelId, CancellationToken cancellationToken) : Task<Result>
        - SwitchModelAsync(ModelId modelId, CancellationToken cancellationToken) : Task<Result>
        - ReloadFromSettingsAsync(CancellationToken cancellationToken) : Task<Result>
        - SetEngineLockedAsync(ModelId modelId, CancellationToken cancellationToken) : Task<(Result Result, ITtsEngine? PreviousEngine)>
        - Dispose() : void
*/

public class TtsEngineProvider : ITtsEngineProvider, IDisposable
{
    private static readonly EmptyTtsEngine EmptyEngine = new();

    private readonly ITtsEngineFactory _engineFactory;
    private readonly SettingsManager _settingsManager;
    private readonly DownloadManager? _downloadManager;
    private readonly IModelRegistry? _modelRegistry;

    /* Serializes engine loads so two callers never start two workers for the same switch */
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private volatile ITtsEngine? _currentEngine;
    private bool _disposed;

    public ModelId CurrentModel { get; private set; } = ModelId.Unselected;

    public event Action? OnEngineChanged;

    /* Main constructor */
    public TtsEngineProvider(
        ITtsEngineFactory engineFactory,
        SettingsManager settingsManager,
        DownloadManager? downloadManager = null,
        IModelRegistry? modelRegistry = null
    )
    {
        _engineFactory = engineFactory;
        _settingsManager = settingsManager;
        _downloadManager = downloadManager;
        _modelRegistry = modelRegistry;
    }

    /* Returns the loaded engine, or the shared empty engine while nothing has been loaded */
    public ITtsEngine CurrentEngine => _currentEngine ?? EmptyEngine;

    /* Lazily loads the engine specified in AppConfig the first time it is requested */
    public async Task<ITtsEngine> GetEngineAsync(CancellationToken cancellationToken = default)
    {
        var existing = _currentEngine;
        if (existing != null)
            return existing;

        ITtsEngine? previousEngine = null;
        bool engineChanged = false;

        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_currentEngine == null)
            {
                var (result, prev) = await SetEngineLockedAsync(_settingsManager.CurrentAppConfig.ActiveModel, cancellationToken)
                    .ConfigureAwait(false);
                previousEngine = prev;

                /* A failed load leaves the shared empty engine in place so callers get a clear "not available" state */
                if (result.IsFailure)
                {
                    AppLogger.Error("TtsEngineProvider", $"Failed to load TTS engine: {result.ErrorMessage}", result.Exception);
                    _currentEngine = EmptyEngine;
                }
                else
                {
                    engineChanged = true;
                }
            }
            existing = _currentEngine!;
        }
        finally
        {
            _loadLock.Release();
        }

        DisposeEngineOffThread(previousEngine);
        if (engineChanged)
            OnEngineChanged?.Invoke();

        return existing;
    }

    /* Creates and validates a candidate engine before replacing the active engine */
    public async Task<Result> SetEngineAsync(ModelId modelId, CancellationToken cancellationToken = default)
    {
        Result result;
        ITtsEngine? previousEngine = null;

        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            (result, previousEngine) = await SetEngineLockedAsync(modelId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _loadLock.Release();
        }

        DisposeEngineOffThread(previousEngine);
        if (result.IsSuccess)
            OnEngineChanged?.Invoke();

        return result;
    }

    /* Validates and activates a model before saving it as the selected model */
    public async Task<Result> SwitchModelAsync(ModelId modelId, CancellationToken cancellationToken = default)
    {
        if (!modelId.IsUnselected)
        {
            if (_modelRegistry != null && _modelRegistry.GetModelMetadata(modelId) == null)
                return Result.Failure($"Model '{modelId}' is not provided by any installed engine.");

            if (_downloadManager != null && !_downloadManager.IsModelDownloaded(modelId))
                return Result.Failure(
                    $"Cannot switch to '{modelId}'. Model files are not downloaded."
                );
        }

        var result = await SetEngineAsync(modelId, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
            return result;

        var config = _settingsManager.CurrentAppConfig;
        config.ActiveModel = modelId;
        return _settingsManager.SaveAppConfig(config);
    }

    /* Reloads the active TTS engine based on current application configuration */
    public Task<Result> ReloadFromSettingsAsync(CancellationToken cancellationToken = default) =>
        SetEngineAsync(_settingsManager.CurrentAppConfig.ActiveModel, cancellationToken);

    /* Performs the engine swap. The caller must hold _loadLock and dispose previousEngine / raise events after releasing it */
    private async Task<(Result Result, ITtsEngine? PreviousEngine)> SetEngineLockedAsync(
        ModelId modelId,
        CancellationToken cancellationToken)
    {
        var createResult = await _engineFactory.CreateAsync(modelId, cancellationToken).ConfigureAwait(false);
        if (createResult.IsFailure || createResult.Value == null)
            return (Result.Failure(
                $"Failed to load TTS engine '{modelId}': {createResult.ErrorMessage}", createResult.Exception), null);

        var candidate = createResult.Value;
        if (!candidate.IsAvailable && !modelId.IsUnselected)
        {
            candidate.Dispose();
            return (Result.Failure(
                $"Model '{modelId}' was selected but files are not available or loaded."
            ), null);
        }

        var previousEngine = _currentEngine;
        _currentEngine = candidate;
        CurrentModel = modelId;
        return (Result.Success(), previousEngine);
    }

    /* Disposes a superseded engine on a background thread so UI/callers do not stall waiting for worker shutdown */
    private static void DisposeEngineOffThread(ITtsEngine? engine)
    {
        if (engine == null || ReferenceEquals(engine, EmptyEngine))
            return;

        _ = Task.Run(() =>
        {
            try { engine.Dispose(); }
            catch (Exception ex) { AppLogger.Warning("TtsEngineProvider", $"Error disposing previous engine: {ex.Message}"); }
        });
    }

    /* Shuts down the active engine (and with it its worker process) */
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var engine = _currentEngine;
        _currentEngine = null;
        if (engine != null && !ReferenceEquals(engine, EmptyEngine))
            engine.Dispose();

        _loadLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
