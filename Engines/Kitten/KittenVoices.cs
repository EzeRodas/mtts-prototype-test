using MoonTTS.Engine.Protocol;

namespace MoonTTS.Engine.Kitten;

/* File Structure:
        - KittenVoiceItem (Record)
        - LanguageDisplayInfo (Record)
        - BuildCatalog(KittenRepoConfig config, string modelDirectory, string weights, string filePrefix) : List<KittenVoiceItem>
        - Describe(IReadOnlyList<KittenVoiceItem> catalog) : List<VoiceEntry>
        - ResolveVoice(IReadOnlyList<KittenVoiceItem> catalog, string? voiceId, string defaultLanguage) : KittenVoiceItem?
        - GetLanguageInfo(string langCode) : LanguageDisplayInfo
*/

/* Resolved KittenTTS 2.0 voice entry binding a UI voice ID to its language and NPZ key */
public sealed record KittenVoiceItem(
    string Id,
    string Language,
    string VoiceKey,
    string Name,
    string DisplayName,
    string Bcp47);

/* Dynamic voice catalog builder and resolver for KittenTTS 2.0 Tiny multilingual models */
public static class KittenVoices
{
    private sealed record LanguageDisplayInfo(string NativeName, string Bcp47);

    private static readonly Dictionary<string, LanguageDisplayInfo> s_languages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = new("English", "en-US"),
        ["de"] = new("Deutsch", "de-DE"),
        ["es"] = new("Español", "es-ES"),
        ["it"] = new("Italiano", "it-IT"),
        ["pt"] = new("Português", "pt-BR"),
        ["ru"] = new("Русский", "ru-RU"),
        ["hi"] = new("हिन्दी", "hi-IN"),
        ["fr"] = new("Français", "fr-FR"),
        ["da"] = new("Dansk", "da-DK"),
        ["fi"] = new("Suomi", "fi-FI"),
        ["ko"] = new("한국어", "ko-KR"),
        ["nl"] = new("Nederlands", "nl-NL"),
        ["sv"] = new("Svenska", "sv-SE"),
        ["sw"] = new("Kiswahili", "sw-KE"),
        ["tr"] = new("Türkçe", "tr-TR")
    };

    /* Builds the sorted list of usable voices whose language ONNX graph and NPZ pack exist on disk */
    public static List<KittenVoiceItem> BuildCatalog(KittenRepoConfig config, string modelDirectory, string weights, string filePrefix)
    {
        var items = new List<KittenVoiceItem>();

        foreach ((string lang, KittenLanguageEntry langEntry) in config.Languages)
        {
            if (!EspeakPhonemizer.IsLanguageSupported(lang))
                continue;

            string onnxPath = Path.Combine(modelDirectory, langEntry.ResolveOnnxFile(weights));
            string npzPath = Path.Combine(modelDirectory, $"{filePrefix}{langEntry.Voices}");
            if (!File.Exists(onnxPath) || !File.Exists(npzPath))
                continue;

            if (!config.Voices.TryGetValue(lang, out List<KittenVoiceDescriptor>? descriptors) || descriptors.Count == 0)
                continue;

            LanguageDisplayInfo langInfo = GetLanguageInfo(lang);
            foreach (KittenVoiceDescriptor voice in descriptors)
            {
                if (string.IsNullOrWhiteSpace(voice.Key))
                    continue;

                string name = string.IsNullOrWhiteSpace(voice.Name) ? voice.Key : voice.Name;
                string displayName = string.IsNullOrWhiteSpace(voice.Description)
                    ? $"{langInfo.NativeName} · {name}"
                    : $"{langInfo.NativeName} · {name} ({voice.Description})";

                items.Add(new KittenVoiceItem(
                    Id: $"{lang.ToLowerInvariant()}:{voice.Key.ToLowerInvariant()}",
                    Language: lang.ToLowerInvariant(),
                    VoiceKey: voice.Key,
                    Name: name,
                    DisplayName: displayName,
                    Bcp47: langInfo.Bcp47));
            }
        }

        /* Keep English voices first, then sort remaining languages alphabetically by native name */
        items.Sort((a, b) =>
        {
            bool aEn = string.Equals(a.Language, "en", StringComparison.OrdinalIgnoreCase);
            bool bEn = string.Equals(b.Language, "en", StringComparison.OrdinalIgnoreCase);
            if (aEn != bEn)
                return aEn ? -1 : 1;

            int langCmp = string.Compare(GetLanguageInfo(a.Language).NativeName, GetLanguageInfo(b.Language).NativeName, StringComparison.OrdinalIgnoreCase);
            if (langCmp != 0)
                return langCmp;

            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        return items;
    }

    /* Converts the active voice catalog into protocol VoiceEntry objects for ListVoices */
    public static List<VoiceEntry> Describe(IReadOnlyList<KittenVoiceItem> catalog)
    {
        return catalog.Select(v => new VoiceEntry
        {
            Id = v.Id,
            Name = v.DisplayName,
            Language = v.Bcp47
        }).ToList();
    }

    /* Resolves a protocol voice ID (e.g. "en:eleanor" or "eleanor") to its catalog item */
    public static KittenVoiceItem? ResolveVoice(IReadOnlyList<KittenVoiceItem> catalog, string? voiceId, string defaultLanguage)
    {
        if (catalog.Count == 0)
            return null;

        if (string.IsNullOrWhiteSpace(voiceId))
            return catalog.FirstOrDefault(v => string.Equals(v.Language, defaultLanguage, StringComparison.OrdinalIgnoreCase)) ?? catalog[0];

        string trimmed = voiceId.Trim();

        KittenVoiceItem? exact = catalog.FirstOrDefault(v =>
            string.Equals(v.Id, trimmed, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return exact;

        List<KittenVoiceItem> matches = catalog
            .Where(v => string.Equals(v.Name, trimmed, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(v.VoiceKey, trimmed, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 1)
            return matches[0];

        if (matches.Count > 1)
            return matches.FirstOrDefault(v => string.Equals(v.Language, defaultLanguage, StringComparison.OrdinalIgnoreCase)) ?? matches[0];

        return catalog[0];
    }

    /* Returns the native language name and BCP-47 tag for a Kitten language code */
    private static LanguageDisplayInfo GetLanguageInfo(string langCode)
    {
        return s_languages.TryGetValue(langCode, out LanguageDisplayInfo? info)
            ? info
            : new LanguageDisplayInfo(langCode.ToUpperInvariant(), langCode);
    }
}
