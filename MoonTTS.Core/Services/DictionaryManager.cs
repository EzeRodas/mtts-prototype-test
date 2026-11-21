using System.Text.RegularExpressions;
using MoonTTS.Domain.Common;
using MoonTTS.Domain.Constants;
using MoonTTS.Domain.Entities;
using MoonTTS.Domain.Interfaces;

namespace MoonTTS.Core.Services;

/* File Structure:
        - DictionaryManager(IConfigurationRepository repo)
        - GetEntries() : IReadOnlyList<DictionaryEntry>
        - AddEntry(string original, string spelling, bool caseSensitive) : Result
        - UpdateEntry(Guid id, string original, string spelling, bool caseSensitive) : Result
        - DeleteEntry(Guid id) : Result
        - ReplaceText(string input) : string
        - RebuildRegex() : void
        - TrimString(string input, int maxLength) : string
*/

public class DictionaryManager
{
    private const int MaxLength = 100;

    /* Immutable snapshot swapped atomically on dictionary edits to ensure lock-free O(1) lookup */
    private sealed record CompiledDictionary(
        Regex? Pattern,
        Dictionary<string, string> ExactReplacements,
        Dictionary<string, string> IgnoreCaseReplacements);

    private readonly IConfigurationRepository _repo;
    private volatile CompiledDictionary _compiled = new(
        null,
        new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    
    /* Initializes the manager, subscribes to repository updates, and compiles the initial dictionary snapshot */
    public DictionaryManager(IConfigurationRepository repo)
    {
        _repo = repo;
        /* Automatically rebuilds the regex when the state changes */
        _repo.DictionaryChanged += RebuildRegex;
        
        RebuildRegex();
    }

    /* Returns list with all the dictionary entries */
    public IReadOnlyList<DictionaryEntry> GetEntries() => _repo.DictionaryEntries;

    /* Receives parameters, turns into DictionaryEntry object, and adds to list */
    public Result AddEntry(string original, string spelling, bool caseSensitive = false)
    {
        /* Checks for errors */
        if (string.IsNullOrWhiteSpace(original)) 
            return Result.Failure("Original word cannot be empty.");
        if (string.IsNullOrWhiteSpace(spelling))
            return Result.Failure("Replacement spelling cannot be empty.");
        if (_repo.DictionaryEntries.Count >= AppConstants.MaxDictionaryEntries)
            return Result.Failure($"Dictionary has reached its maximum limit of {AppConstants.MaxDictionaryEntries} entries.");


        string trimmedOriginal = TrimString(original, MaxLength);
        string trimmedSpelling = TrimString(spelling, MaxLength);

        if (HasConflictingEntry(_repo.DictionaryEntries, trimmedOriginal, caseSensitive, excludeId: null))
            return Result.Failure($"An entry for '{trimmedOriginal}' already exists.");

        var entries = _repo.DictionaryEntries.ToList();
        entries.Add(new DictionaryEntry
        {
            Id = Guid.NewGuid(),
            Original = trimmedOriginal,
            Spelling = trimmedSpelling,
            CaseSensitive = caseSensitive
        });

        _repo.SaveDictionary(entries);
        return Result.Success();
    }

    /* Searches for the corresponding ID, modifies it, and updates the list */
    public Result UpdateEntry(Guid id, string original, string spelling, bool caseSensitive)
    {
        /* Error checking */
        List<DictionaryEntry> entries = _repo.DictionaryEntries.ToList();
        var entry = entries.FirstOrDefault(d => d.Id == id);
        if (entry == null)
            return Result.Failure($"Dictionary entry with id {id} was not found.");
        if (string.IsNullOrWhiteSpace(original))
            return Result.Failure("Original word cannot be empty.");
        if (string.IsNullOrWhiteSpace(spelling))
            return Result.Failure("Replacement spelling cannot be empty.");

        string trimmedOriginal = TrimString(original, MaxLength);
        string trimmedSpelling = TrimString(spelling, MaxLength);

        if (HasConflictingEntry(entries, trimmedOriginal, caseSensitive, excludeId: id))
            return Result.Failure($"Another entry for '{trimmedOriginal}' already exists.");

        entry.Original = trimmedOriginal;
        entry.Spelling = trimmedSpelling;
        entry.CaseSensitive = caseSensitive;

        _repo.SaveDictionary(entries);
        return Result.Success();
    }

    /* Searches for the ID and deletes it */
    public Result DeleteEntry(Guid id)
    {
        List<DictionaryEntry> entries = _repo.DictionaryEntries.ToList();
        var entry = entries.FirstOrDefault(d => d.Id == id);
        if (entry == null)
        {
            return Result.Failure($"Dictionary entry with id {id} was not found.");
        }
        entries.Remove(entry);
        
        _repo.SaveDictionary(entries);
        return Result.Success();
    }

    /* Replaces matching dictionary terms in O(1) per match against an immutable compiled snapshot */
    public string ReplaceText(string input)
    {
        CompiledDictionary snapshot = _compiled;
        if (string.IsNullOrWhiteSpace(input) || snapshot.Pattern == null)
            return input;

        return snapshot.Pattern.Replace(input, match =>
        {
            if (snapshot.ExactReplacements.TryGetValue(match.Value, out string? exact))
                return exact;
            if (snapshot.IgnoreCaseReplacements.TryGetValue(match.Value, out string? insensitive))
                return insensitive;
            return match.Value;
        });
    }

    /* Compiles non-capturing alternation rules sorted longest-first and atomically swaps the lookup snapshot */
    private void RebuildRegex()
    {
        var rawEntries = _repo.DictionaryEntries;
        if (rawEntries.Count == 0)
        {
            _compiled = new CompiledDictionary(
                null,
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            return;
        }

        List<DictionaryEntry> ordered = rawEntries
            .Where(e => !string.IsNullOrEmpty(e.Original))
            .OrderByDescending(e => e.Original.Length)
            .ToList();

        if (ordered.Count == 0)
        {
            _compiled = new CompiledDictionary(
                null,
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            return;
        }

        var parts = new List<string>(ordered.Count);
        var exactMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var ignoreCaseMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in ordered)
        {
            string escaped = Regex.Escape(entry.Original);

            /* Only add word boundary \b if the edge character is a letter or digit */
            string prefix = char.IsLetterOrDigit(entry.Original[0]) ? @"\b" : string.Empty;
            string suffix = char.IsLetterOrDigit(entry.Original[^1]) ? @"\b" : string.Empty;

            string core = $"(?:{prefix}{escaped}{suffix})";
            if (entry.CaseSensitive)
            {
                parts.Add(core);
                exactMap.TryAdd(entry.Original, entry.Spelling);
            }
            else
            {
                parts.Add($@"(?i:{core})");
                ignoreCaseMap.TryAdd(entry.Original, entry.Spelling);
            }
        }

        string combinedPattern = string.Join("|", parts);
        var compiledRegex = new Regex(combinedPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        _compiled = new CompiledDictionary(compiledRegex, exactMap, ignoreCaseMap);
    }

    /* Checks whether an existing entry conflicts with the proposed original word and case sensitivity */
    private static bool HasConflictingEntry(
        IReadOnlyList<DictionaryEntry> entries,
        string original,
        bool caseSensitive,
        Guid? excludeId)
    {
        return entries.Any(e =>
        {
            if (excludeId.HasValue && e.Id == excludeId.Value)
                return false;

            if (caseSensitive && e.CaseSensitive)
                return string.Equals(e.Original, original, StringComparison.Ordinal);

            return string.Equals(e.Original, original, StringComparison.OrdinalIgnoreCase);
        });
    }

    /* If the string received goes over the limit gets trimmed, otherwise remains untouched */
    private static string TrimString(string input, int maxLength)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        return input.Length > maxLength ? input[..maxLength] : input;
    }
}