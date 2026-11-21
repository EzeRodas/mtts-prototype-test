using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using MoonTTS.Core.Utilities;
using MoonTTS.Domain.Entities;
using MoonTTS.Infrastructure.Persistence;

namespace MoonTTS.Avalonia.Services;

/* File Structure:
        - LocalizationService (Class)
            - Instance
            - Strings
            - CurrentLanguage
            - LanguageChanged
            - LocalizationService()
            - GetAvailableLanguages()
            - SetLanguage()
            - LoadStringsForCode()
            - TryDeserializeFile()
            - TryDeserializeAsset()
            - ResolveDisplayName()
*/

/* Observable singleton managing bundled and user-provided UI language packs */
public sealed partial class LocalizationService : ObservableObject
{
    private static readonly LanguageOption[] BuiltInLanguages =
    [
        new("en", "English"),
        new("es", "Español")
    ];

    /* Singleton localization service instance shared across views and ViewModels */
    public static LocalizationService Instance { get; } = new();

    /* Currently active localized string table */
    [ObservableProperty]
    private UiStrings _strings = new();

    /* Currently active normalized language code (e.g. "en", "es") */
    [ObservableProperty]
    private string _currentLanguage = "en";

    /* Raised after the active language pack finishes updating */
    public event Action? LanguageChanged;

    /* Initializes the singleton localization service with English fallback defaults */
    private LocalizationService()
    {
    }

    /* Returns built-in languages plus any custom locale packs discovered in <ConfigDir>/locales/ */
    public IReadOnlyList<LanguageOption> GetAvailableLanguages()
    {
        var options = new List<LanguageOption>(BuiltInLanguages);
        var seenCodes = new HashSet<string>(BuiltInLanguages.Select(l => l.Code), StringComparer.OrdinalIgnoreCase);

        string localesDir = Path.Combine(PathResolver.GetConfigDirectory(), "locales");
        if (!Directory.Exists(localesDir))
            return options;

        try
        {
            var customOptions = new List<LanguageOption>();
            foreach (string file in Directory.EnumerateFiles(localesDir, "*.json", SearchOption.TopDirectoryOnly))
            {
                string code = Path.GetFileNameWithoutExtension(file).Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(code) || !seenCodes.Add(code))
                    continue;

                customOptions.Add(new LanguageOption(code, ResolveDisplayName(code)));
            }

            customOptions.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
            options.AddRange(customOptions);
        }
        catch (IOException)
        {
            /* Keep built-in languages usable if custom directory enumeration fails */
        }
        catch (UnauthorizedAccessException)
        {
            /* Keep built-in languages usable when directory access is restricted */
        }

        return options;
    }

    /* Loads the requested locale pack from user overrides or bundled Avalonia assets and notifies listeners */
    public void SetLanguage(string? languageCode)
    {
        string code = string.IsNullOrWhiteSpace(languageCode)
            ? "en"
            : languageCode.Trim().ToLowerInvariant();

        UiStrings loaded = LoadStringsForCode(code);
        CurrentLanguage = code;
        Strings = loaded;
        LanguageChanged?.Invoke();
    }

    /* Resolves localized strings from user overrides first, then bundled Avalonia assets */
    private static UiStrings LoadStringsForCode(string code)
    {
        if (!IsSafeLanguageCode(code))
            return TryDeserializeAsset("en") ?? new UiStrings();

        string localesDir = Path.GetFullPath(Path.Combine(PathResolver.GetConfigDirectory(), "locales"));
        string normalizedRoot = localesDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string customPath = Path.GetFullPath(Path.Combine(localesDir, $"{code}.json"));

        StringComparison cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (customPath.StartsWith(normalizedRoot, cmp) && File.Exists(customPath))
        {
            UiStrings? customStrings = TryDeserializeFile(customPath);
            if (customStrings != null)
                return customStrings;
        }

        UiStrings? bundledStrings = TryDeserializeAsset(code);
        return bundledStrings ?? new UiStrings();
    }

    /* Ensures language codes contain only safe BCP-47 identifier characters (no path separators or traversal) */
    private static bool IsSafeLanguageCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 32)
            return false;

        foreach (char c in code)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'))
                return false;
        }
        return true;
    }

    /* Deserializes a custom locale JSON file using the AOT source-generated JSON context */
    private static UiStrings? TryDeserializeFile(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, AppJsonContext.Default.UiStrings);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /* Loads and deserializes a bundled locale JSON resource from Avalonia assets */
    private static UiStrings? TryDeserializeAsset(string code)
    {
        try
        {
            var uri = new Uri($"avares://MoonTTS.Avalonia/Assets/Locales/{code}.json");
            if (!AssetLoader.Exists(uri))
                return null;

            using Stream stream = AssetLoader.Open(uri);
            return JsonSerializer.Deserialize(stream, AppJsonContext.Default.UiStrings);
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    /* Resolves a human-readable native display name for a custom locale code */
    private static string ResolveDisplayName(string code)
    {
        CultureInfo? match = CultureInfo.GetCultures(CultureTypes.AllCultures)
            .FirstOrDefault(c => !c.Equals(CultureInfo.InvariantCulture) &&
                                 string.Equals(c.Name, code, StringComparison.OrdinalIgnoreCase));

        if (match != null && !string.IsNullOrWhiteSpace(match.NativeName))
            return char.ToUpper(match.NativeName[0], match) + match.NativeName[1..];

        return code.ToUpperInvariant();
    }
}
