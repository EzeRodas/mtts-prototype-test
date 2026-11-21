using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoonTTS.Core.Services;
using MoonTTS.Domain.Entities;

namespace MoonTTS.Avalonia.ViewModels.Settings;

/* File Structure:
        - DictionarySettingsViewModel(DictionaryManager?, Action<string>)
        - Load() : void
        - AddEntry() : void
        - DeleteEntry(Guid) : void
*/

/* Manages the Phonetic Dictionary tab state and CRUD commands */
public partial class DictionarySettingsViewModel : ViewModelBase
{
    private readonly DictionaryManager? _dictManager;
    private readonly Action<string> _showStatus;

    [ObservableProperty]
    private string _newDictOriginal = string.Empty;

    [ObservableProperty]
    private string _newDictSpelling = string.Empty;

    [ObservableProperty]
    private bool _newDictCaseSensitive;

    public ObservableCollection<DictionaryEntry> Entries { get; } = new();

    /* Initializes dictionary settings with the core DictionaryManager */
    public DictionarySettingsViewModel(DictionaryManager? dictManager, Action<string> showStatus)
    {
        _dictManager = dictManager;
        _showStatus = showStatus;
    }

    /* Reloads all dictionary entries from the core manager */
    public void Load()
    {
        if (_dictManager == null)
            return;

        Entries.Clear();
        foreach (var entry in _dictManager.GetEntries())
            Entries.Add(entry);
    }

    /* Adds a new phonetic substitution rule and refreshes the list */
    [RelayCommand]
    private void AddEntry()
    {
        if (_dictManager == null
            || string.IsNullOrWhiteSpace(NewDictOriginal)
            || string.IsNullOrWhiteSpace(NewDictSpelling))
        {
            return;
        }

        var res = _dictManager.AddEntry(
            NewDictOriginal.Trim(),
            NewDictSpelling.Trim(),
            NewDictCaseSensitive);

        if (res.IsSuccess)
        {
            NewDictOriginal = string.Empty;
            NewDictSpelling = string.Empty;
            Load();
        }
        else if (!string.IsNullOrWhiteSpace(res.ErrorMessage))
        {
            _showStatus(res.ErrorMessage);
        }
    }

    /* Removes an existing dictionary entry by ID and refreshes the list */
    [RelayCommand]
    private void DeleteEntry(Guid id)
    {
        if (_dictManager == null)
            return;

        var res = _dictManager.DeleteEntry(id);
        if (res.IsSuccess)
            Load();
    }
}
