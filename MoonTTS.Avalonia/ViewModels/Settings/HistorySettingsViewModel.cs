using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using MoonTTS.Core.Services;
using MoonTTS.Domain.Entities;
using MoonTTS.Domain.Interfaces;

namespace MoonTTS.Avalonia.ViewModels.Settings;

/* File Structure:
        - HistorySettingsViewModel(HistoryManager?, ISpeechPipeline?, Action<string>)
        - Load() : void
        - PlayItemAsync(Guid) : Task
        - ClearAll() : void
        - DeleteItem(Guid) : void
*/

/* Manages the Synthesis History tab, delegating audio playback to ISpeechPipeline so HUD controls stay unified */
public partial class HistorySettingsViewModel : ViewModelBase
{
    private readonly HistoryManager? _historyManager;
    private readonly ISpeechPipeline? _speechPipeline;
    private readonly Action<string> _showStatus;

    public ObservableCollection<HistoryEntry> Entries { get; } = new();

    /* Initializes history settings with HistoryManager and the unified ISpeechPipeline */
    public HistorySettingsViewModel(
        HistoryManager? historyManager,
        ISpeechPipeline? speechPipeline,
        Action<string> showStatus)
    {
        _historyManager = historyManager;
        _speechPipeline = speechPipeline;
        _showStatus = showStatus;
    }

    /* Reloads all history entries from storage */
    public void Load()
    {
        if (_historyManager == null)
            return;

        Entries.Clear();
        foreach (var entry in _historyManager.GetHistory())
            Entries.Add(entry);
    }

    /* Replays a history entry's master WAV file through the unified speech pipeline */
    [RelayCommand]
    private async Task PlayItemAsync(Guid id)
    {
        if (_historyManager == null || _speechPipeline == null)
            return;

        var wavPathResult = _historyManager.GetEntryWavPath(id);
        if (!wavPathResult.IsSuccess)
        {
            _showStatus(wavPathResult.ErrorMessage ?? L.Strings.SpeechSynthesisFailed);
            return;
        }

        var playResult = await _speechPipeline.PlayWavFileAsync(wavPathResult.Value);
        if (playResult.IsFailure && playResult.ErrorMessage != SpeechPipeline.CancelledMessage)
            _showStatus(playResult.ErrorMessage ?? L.Strings.SpeechSynthesisFailed);
    }

    /* Clears all history entries and associated WAV files */
    [RelayCommand]
    private void ClearAll()
    {
        if (_historyManager == null)
            return;

        var result = _historyManager.ClearHistory();
        if (result.IsSuccess)
            Entries.Clear();
    }

    /* Deletes a single history entry and removes its WAV file */
    [RelayCommand]
    private void DeleteItem(Guid id)
    {
        if (_historyManager == null)
            return;

        var result = _historyManager.DeleteEntry(id);
        if (result.IsSuccess)
            Load();
    }
}
