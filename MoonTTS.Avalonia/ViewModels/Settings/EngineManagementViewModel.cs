using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoonTTS.Avalonia.Services;
using MoonTTS.Core.Services;
using MoonTTS.Core.Utilities;
using MoonTTS.Domain.ValueObjects;

namespace MoonTTS.Avalonia.ViewModels.Settings;

/* File Structure:
        - ModelStatusItem (Class)
        - EngineManagementViewModel(DownloadManager?, Action<ModelId, bool>, Func<ModelId, Task>, Func<Task>, Action, Action<string>)
        - Load() : void
        - RescanEngines() : void
        - DownloadModelAsync(ModelStatusItem) : Task
        - CancelDownload(ModelStatusItem) : void
        - DeleteModelAsync(ModelStatusItem) : Task
        - CancelAllDownloads() : void
*/

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

    [ObservableProperty]
    private string? _languageTag;

    [ObservableProperty]
    private string? _languagesPopupText;

    [ObservableProperty]
    private bool _hasMultipleLanguages;

    public bool HasTags =>
        !string.IsNullOrEmpty(SizeTag)
        || !string.IsNullOrEmpty(RamUsageTag)
        || !string.IsNullOrEmpty(CpuUsageTag)
        || !string.IsNullOrEmpty(LanguageTag);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    private bool _isDownloaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    private bool _hasDownloadableFiles;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    private bool _isDownloading;

    [ObservableProperty]
    private double _progress;

    /* Localization service for model status card bindings */
    public LocalizationService L => LocalizationService.Instance;

    /* True when the manifest declares downloadable files that are not yet downloaded and no download is active */
    public bool CanDownload => !IsSeparator && HasDownloadableFiles && !IsDownloaded && !IsDownloading;

    /* True when the manifest declares downloadable files that are currently downloaded and not downloading */
    public bool CanDelete => !IsSeparator && HasDownloadableFiles && IsDownloaded && !IsDownloading;
}

/* Manages Engine Management tab: installed status, resumable downloads with cancellation, safe deletion, and engine rescanning */
public partial class EngineManagementViewModel : ViewModelBase
{
    private readonly DownloadManager? _downloadManager;
    private readonly Action<ModelId, bool> _onDownloadStateChanged;
    private readonly Func<ModelId, Task> _ensureModelUnloadedAsync;
    private readonly Func<Task> _refreshVoicesAsync;
    private readonly Action _onEnginesRescanned;
    private readonly Action<string> _showStatus;
    private readonly Dictionary<ModelId, CancellationTokenSource> _activeDownloads = new();

    public ObservableCollection<ModelStatusItem> ModelStatuses { get; } = new();

    /* Initializes engine management view model with download manager and parent synchronization callbacks */
    public EngineManagementViewModel(
        DownloadManager? downloadManager,
        Action<ModelId, bool> onDownloadStateChanged,
        Func<ModelId, Task> ensureModelUnloadedAsync,
        Func<Task> refreshVoicesAsync,
        Action onEnginesRescanned,
        Action<string> showStatus)
    {
        _downloadManager = downloadManager;
        _onDownloadStateChanged = onDownloadStateChanged;
        _ensureModelUnloadedAsync = ensureModelUnloadedAsync;
        _refreshVoicesAsync = refreshVoicesAsync;
        _onEnginesRescanned = onEnginesRescanned;
        _showStatus = showStatus;
    }

    /* Populates model download statuses with engine separators and localized resource tags */
    public void Load()
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
                var sortedLanguages = model.Languages?
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Select(l => l.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                string? languageTag = null;
                string? languagesPopupText = null;
                bool hasMultipleLanguages = false;

                if (sortedLanguages is { Count: 1 })
                {
                    languageTag = string.Format(L.Strings.SingleLanguageTagFormat, sortedLanguages[0]);
                }
                else if (sortedLanguages is { Count: > 1 })
                {
                    languageTag = string.Format(L.Strings.MultipleLanguagesTagFormat, sortedLanguages.Count);
                    languagesPopupText = string.Join(", ", sortedLanguages);
                    hasMultipleLanguages = true;
                }

                ModelStatuses.Add(new ModelStatusItem
                {
                    Model = model.Id,
                    Name = model.DisplayName,
                    IsDownloaded = _downloadManager.IsModelDownloaded(model.Id),
                    HasDownloadableFiles = model.DownloadFiles.Count > 0,
                    IsDownloading = _activeDownloads.ContainsKey(model.Id),
                    Progress = 0,
                    SizeTag = model.Size.HasValue ? string.Format(L.Strings.SizeTagFormat, model.Size.Value) : null,
                    RamUsageTag = model.RamUsage.HasValue ? string.Format(L.Strings.RamUsageTagFormat, model.RamUsage.Value) : null,
                    CpuUsageTag = !string.IsNullOrEmpty(model.CpuUsage) ? string.Format(L.Strings.CpuUsageTagFormat, model.CpuUsage) : null,
                    IsCpuLow = cpu == "low",
                    IsCpuMedium = cpu == "medium",
                    IsCpuHigh = cpu == "high",
                    LanguageTag = languageTag,
                    LanguagesPopupText = languagesPopupText,
                    HasMultipleLanguages = hasMultipleLanguages
                });
            }
        }
    }

    /* Re-scans the engines directory on disk and refreshes model dropdowns and status cards */
    [RelayCommand]
    private void RescanEngines()
    {
        if (_downloadManager == null)
            return;

        _downloadManager.RefreshEngines();
        Load();
        _onEnginesRescanned();
    }

    /* Downloads model weights with live progress, cancellation support, and visible error reporting */
    [RelayCommand]
    private async Task DownloadModelAsync(ModelStatusItem item)
    {
        if (_downloadManager == null || item == null || item.IsSeparator || item.IsDownloading)
            return;

        var cts = new CancellationTokenSource();
        _activeDownloads[item.Model] = cts;
        item.IsDownloading = true;
        item.Progress = 0;

        try
        {
            var progress = new Progress<double>(p => item.Progress = p);
            var result = await _downloadManager.DownloadModelAsync(item.Model, progress, cts.Token);
            item.IsDownloaded = result.IsSuccess;

            if (result.IsSuccess)
            {
                _onDownloadStateChanged(item.Model, true);
                await _refreshVoicesAsync();
            }
            else if (!cts.IsCancellationRequested)
            {
                AppLogger.Error("EngineManagementViewModel", $"Model download failed for '{item.Model}': {result.ErrorMessage}", result.Exception);
                _showStatus(result.ErrorMessage ?? L.Strings.ModelDownloadFailed);
            }
        }
        catch (OperationCanceledException)
        {
            item.IsDownloaded = false;
        }
        catch (Exception ex)
        {
            item.IsDownloaded = false;
            AppLogger.Error("EngineManagementViewModel", $"Model download exception for '{item.Model}'", ex);
            _showStatus(ex.Message);
        }
        finally
        {
            if (_activeDownloads.TryGetValue(item.Model, out var active) && ReferenceEquals(active, cts))
                _activeDownloads.Remove(item.Model);
            cts.Dispose();
            item.IsDownloading = false;
        }
    }

    /* Cancels an active model download; partial .part file remains on disk for resumption */
    [RelayCommand]
    private void CancelDownload(ModelStatusItem item)
    {
        if (item == null)
            return;

        if (_activeDownloads.TryGetValue(item.Model, out var cts))
            cts.Cancel();
    }

    /* Unloads the active engine worker before deleting model files so Windows never encounters file locks */
    [RelayCommand]
    private async Task DeleteModelAsync(ModelStatusItem item)
    {
        if (_downloadManager == null || item == null || item.IsSeparator || item.IsDownloading)
            return;

        await _ensureModelUnloadedAsync(item.Model);

        var res = _downloadManager.DeleteModel(item.Model);
        if (res.IsSuccess)
        {
            item.IsDownloaded = false;
            item.Progress = 0;
            _onDownloadStateChanged(item.Model, false);
        }
        else
        {
            AppLogger.Error("EngineManagementViewModel", $"Failed to delete model '{item.Model}': {res.ErrorMessage}", res.Exception);
            _showStatus(res.ErrorMessage ?? "Failed to delete model files.");
        }
    }

    /* Cancels any in-flight model downloads during shutdown */
    public void CancelAllDownloads()
    {
        foreach (var cts in _activeDownloads.Values)
            cts.Cancel();
    }
}
