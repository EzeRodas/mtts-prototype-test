using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace MoonTTS.Engine.Kitten;

/* File Structure:
        - IsLanguageSupported(string language) : bool
        - EnsureInitialized() : void
        - Phonemize(string text, string language, string phonemizerVersion) : string
        - Tokenize(string phonemizedText, IReadOnlyDictionary<string, int> vocab) : long[]
        - SetEspeakVoiceIfNeeded(string espeakVoice) : void
        - PhonemizeRaw(string text) : string
        - PhonemizeClause(string clause, StringBuilder sb) : void
        - ApplyEnglishPostProcessing(string phonemes) : string
        - ApplyGenericEspeakPostProcessing(string phonemes) : string
        - ApplyReplacements(string input, (string Old, string New)[] replacements) : string
        - ClauseOrTokenRegex() : Regex
        - SyllabicRegex() : Regex
        - HindiFlapWordRegex() : Regex
        - HindiFlapInsideWordRegex() : Regex
        - ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) : IntPtr
        - espeak_Initialize(int output, int buflength, string? path, int options) : int
        - espeak_SetVoiceByName(string name) : int
        - espeak_TextToPhonemes(byte** textptr, int textmode, int phonememode) : byte*
*/

/* Native multilingual phonemizer wrapping libespeak-ng with Misaki/KittenTTS 2.0 IPA post-processing and folding */
public static unsafe partial class EspeakPhonemizer
{
    private const string LibraryName = "espeak-ng";
    private const int AudioOutputSynchronous = 0x02;
    private const int EspeakCharsUtf8 = 1;

    /* espeakPHONEMES_IPA (0x02) | espeakPHONEMES_TIE (0x80) with '^' tie character in bits 8..23 */
    private const int EspeakPhonemesIpaWithTie = ('^' << 8) | 0x80 | 0x02;
    private const int EndToken = 10;

    private static readonly object s_initLock = new();
    private static bool s_isInitialized;
    private static int s_resolverRegistered;
    private static string s_activeEspeakVoice = string.Empty;

    /* KittenTTS 2.0 language code -> libespeak-ng voice name */
    private static readonly Dictionary<string, string> s_espeakVoices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "en-us",
        ["en-us"] = "en-us",
        ["en-gb"] = "en-gb",
        ["de"] = "de",
        ["es"] = "es",
        ["it"] = "it",
        ["pt"] = "pt-br",
        ["ru"] = "ru",
        ["hi"] = "hi",
        ["fr"] = "fr-fr",
        ["da"] = "da",
        ["fi"] = "fi",
        ["ko"] = "ko",
        ["nl"] = "nl",
        ["sv"] = "sv",
        ["sw"] = "sw",
        ["tr"] = "tr"
    };

    /* Misaki EspeakFallback E2M table for English (sorted longest-first) */
    private static readonly (string Old, string New)[] s_englishE2m =
    [
        ("ʔˌn\u0329", "ʔn"),
        ("ʔn\u0329", "ʔn"),
        ("a^ɪ", "I"),
        ("a^ʊ", "W"),
        ("d^ʒ", "ʤ"),
        ("e^ɪ", "A"),
        ("t^ʃ", "ʧ"),
        ("ɔ^ɪ", "Y"),
        ("ə^l", "ᵊl"),
        ("ʲo", "jo"),
        ("ʲə", "jə"),
        ("e", "A"),
        ("ʲ", ""),
        ("ɚ", "əɹ"),
        ("r", "ɹ"),
        ("x", "k"),
        ("ç", "k"),
        ("ɐ", "ə"),
        ("ɬ", "l"),
        ("\u0303", "")
    ];

    /* Misaki EspeakG2P e2m table for non-English languages */
    private static readonly (string Old, string New)[] s_genericE2m =
    [
        ("a^ɪ", "I"),
        ("a^ʊ", "W"),
        ("d^z", "ʣ"),
        ("d^ʒ", "ʤ"),
        ("e^ɪ", "A"),
        ("o^ʊ", "O"),
        ("s^s", "S"),
        ("t^s", "ʦ"),
        ("t^ʃ", "ʧ"),
        ("ɔ^ɪ", "Y"),
        ("ə^ʊ", "Q")
    ];

    /* KittenTTS 2.0 phonemize_v1 FOLD table */
    private static readonly (string Old, string New)[] s_foldV1 =
    [
        ("\u032a", ""),
        ("\u0329", ""),
        ("\u030d", ""),
        ("\u032f", ""),
        ("\u026d", "l"),
        ("\u026b", "l"),
        ("\u0275", "\u00f8"),
        ("\u03b5", "\u025b"),
        ("\u0005", ""),
        ("\u0006", ""),
        ("\u0007", ""),
        ("\u0008", ""),
        ("{", ""),
        ("}", ""),
        ("[", ""),
        ("]", ""),
        ("\u00bf", ""),
        ("\u00a1", ""),
        ("\u202f", " ")
    ];

    /* Additional folds introduced in KittenTTS 2.0 phonemize_v2 (Mini) */
    private static readonly (string Old, string New)[] s_foldExtraV2 =
    [
        ("\u2009", " "),
        ("\t", " "),
        ("«", ""),
        ("»", ""),
        ("‘", ""),
        ("’", ""),
        ("'", ""),
        ("~", ""),
        ("·", ""),
        ("#", ""),
        ("g", "ɡ")
    ];

    /* Returns true if the language is supported by the native eSpeak NG phonemizer */
    public static bool IsLanguageSupported(string language) =>
        !string.IsNullOrWhiteSpace(language) && s_espeakVoices.ContainsKey(language);

    /* Registers the native library resolver and initializes libespeak-ng */
    public static void EnsureInitialized()
    {
        if (s_isInitialized)
            return;

        lock (s_initLock)
        {
            if (s_isInitialized)
                return;

            if (Interlocked.Exchange(ref s_resolverRegistered, 1) == 0)
            {
                NativeLibrary.SetDllImportResolver(typeof(EspeakPhonemizer).Assembly, ResolveLibrary);
            }

            string baseDir = AppContext.BaseDirectory;
            string dataRoot = Path.Combine(baseDir, "espeak-ng-data");

            int rate = espeak_Initialize(AudioOutputSynchronous, 500, Directory.Exists(dataRoot) ? dataRoot : null, 0);
            if (rate <= 0)
                throw new InvalidOperationException($"espeak_Initialize failed with code {rate}.");

            SetEspeakVoiceIfNeeded("en-us");
            s_isInitialized = true;
        }
    }

    /* Converts text in the specified language into Misaki/KittenTTS 2.0 IPA phonemes */
    public static string Phonemize(string text, string language, string phonemizerVersion)
    {
        EnsureInitialized();

        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        string lang = string.IsNullOrWhiteSpace(language) ? "en" : language.Trim().ToLowerInvariant();
        if (!s_espeakVoices.TryGetValue(lang, out string? espeakVoice))
            espeakVoice = "en-us";

        bool isV2 = string.Equals(phonemizerVersion, "v2", StringComparison.OrdinalIgnoreCase);
        bool isEnglish = lang is "en" or "en-us" or "en-gb";

        string prepared = text;
        if (isV2 && lang == "hi")
        {
            prepared = prepared.Replace("।", ".").Replace("॥", ".");
        }

        if (!isEnglish)
        {
            prepared = prepared
                .Replace("«", "\u201c")
                .Replace("»", "\u201d")
                .Replace("(", "«")
                .Replace(")", "»");
        }

        string rawPhonemes;
        lock (s_initLock)
        {
            SetEspeakVoiceIfNeeded(espeakVoice);
            rawPhonemes = PhonemizeRaw(prepared);
        }

        string processed = isEnglish
            ? ApplyEnglishPostProcessing(rawPhonemes)
            : ApplyGenericEspeakPostProcessing(rawPhonemes);

        processed = ApplyReplacements(processed.Trim(), s_foldV1);
        if (isV2)
        {
            processed = ApplyReplacements(processed, s_foldExtraV2);
            if (lang == "hi")
            {
                processed = processed.Replace("r.h", "ɽʰ");
                processed = HindiFlapInsideWordRegex().Replace(processed, "ɽ");
                if (HindiFlapWordRegex().IsMatch(text))
                    processed = processed.Replace("r.", "ɽ");
            }
        }

        return processed;
    }

    /* Tokenizes phonemized text using the loaded vocab.json and wraps with [0] + ids + [10, 0] */
    public static long[] Tokenize(string phonemizedText, IReadOnlyDictionary<string, int> vocab)
    {
        if (string.IsNullOrWhiteSpace(phonemizedText))
            return [0, EndToken, 0];

        var indices = new List<long>(phonemizedText.Length + 3) { 0 };
        vocab.TryGetValue(",", out int commaId);

        foreach (char c in phonemizedText)
        {
            string key = c.ToString();
            if (vocab.TryGetValue(key, out int id))
            {
                indices.Add(id);
            }
            else if ((c == '(' || c == ')') && commaId > 0)
            {
                indices.Add(commaId);
            }
        }

        indices.Add(EndToken);
        indices.Add(0);
        return indices.ToArray();
    }

    /* Switches the active libespeak-ng voice only when the target language changes */
    private static void SetEspeakVoiceIfNeeded(string espeakVoice)
    {
        if (string.Equals(s_activeEspeakVoice, espeakVoice, StringComparison.OrdinalIgnoreCase))
            return;

        int err = espeak_SetVoiceByName(espeakVoice);
        if (err != 0)
            throw new InvalidOperationException($"espeak_SetVoiceByName('{espeakVoice}') failed with code {err}.");

        s_activeEspeakVoice = espeakVoice;
    }

    /* Phonemizes text clause-by-clause while preserving punctuation and whitespace */
    private static string PhonemizeRaw(string text)
    {
        var sb = new StringBuilder(text.Length * 2);
        MatchCollection matches = ClauseOrTokenRegex().Matches(text);

        foreach (Match match in matches)
        {
            string token = match.Value;
            if (char.IsLetterOrDigit(token[0]))
            {
                PhonemizeClause(token, sb);
            }
            else
            {
                sb.Append(token);
            }
        }

        return sb.ToString();
    }

    /* Invokes libespeak-ng on a contiguous word clause to preserve sentence stress and heteronym context */
    private static void PhonemizeClause(string clause, StringBuilder sb)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(clause + "\0");
        bool appendedAny = false;
        fixed (byte* pUtf8 = utf8)
        {
            byte* cur = pUtf8;
            while (cur != null && *cur != 0)
            {
                byte* ph = espeak_TextToPhonemes(&cur, EspeakCharsUtf8, EspeakPhonemesIpaWithTie);
                if (ph == null)
                    break;

                string? segment = Marshal.PtrToStringUTF8((IntPtr)ph);
                if (!string.IsNullOrEmpty(segment))
                {
                    if (appendedAny && !char.IsWhiteSpace(sb[^1]) && !char.IsWhiteSpace(segment[0]))
                        sb.Append(' ');

                    sb.Append(segment);
                    appendedAny = true;
                }
            }
        }
    }

    /* Applies Misaki EspeakFallback English (en-us) symbol normalization */
    private static string ApplyEnglishPostProcessing(string phonemes)
    {
        string ps = ApplyReplacements(phonemes, s_englishE2m);
        ps = SyllabicRegex().Replace(ps, "ᵊ$1").Replace("\u0329", "");
        ps = ps
            .Replace("o^ʊ", "O")
            .Replace("ɜːɹ", "ɜɹ")
            .Replace("ɜː", "ɜɹ")
            .Replace("ɪə", "iə")
            .Replace("ː", "")
            .Replace("o", "ɔ")
            .Replace("ɾ", "T")
            .Replace("ʔ", "t")
            .Replace("^", "");
        return ps;
    }

    /* Applies Misaki EspeakG2P symbol normalization for non-English languages */
    private static string ApplyGenericEspeakPostProcessing(string phonemes)
    {
        string ps = ApplyReplacements(phonemes, s_genericE2m);
        ps = ps
            .Replace("^", "")
            .Replace("-", "")
            .Replace("«", "(")
            .Replace("»", ")");
        return ps;
    }

    /* Sequentially applies a static replacement table to a phoneme string */
    private static string ApplyReplacements(string input, (string Old, string New)[] replacements)
    {
        string result = input;
        foreach ((string oldVal, string newVal) in replacements)
        {
            if (result.Contains(oldVal, StringComparison.Ordinal))
                result = result.Replace(oldVal, newVal, StringComparison.Ordinal);
        }
        return result;
    }

    /* Matches contiguous alphanumeric clauses, whitespace runs, or individual punctuation symbols */
    [GeneratedRegex(@"(?:[\p{L}\p{N}][\p{L}\p{N}'’]*(?:[^\S\r\n]+[\p{L}\p{N}][\p{L}\p{N}'’]*)*)|\s+|[^\p{L}\p{N}\s]")]
    private static partial Regex ClauseOrTokenRegex();

    /* Matches a non-whitespace character followed by the combining syllabic mark (U+0329) */
    [GeneratedRegex(@"(\S)\u0329")]
    private static partial Regex SyllabicRegex();

    /* Matches Devanagari retroflex flap characters (precomposed or decomposed) */
    [GeneratedRegex(@"[ड़ढ़]|[डढ]़")]
    private static partial Regex HindiFlapWordRegex();

    /* Matches eSpeak's internal Hindi flap marker "r." before non-punctuation characters */
    [GeneratedRegex(@"r\.(?=[^\s.,?!;:…])")]
    private static partial Regex HindiFlapInsideWordRegex();

    /* Resolves bundled or system libespeak-ng library across platforms */
    private static IntPtr ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal))
            return IntPtr.Zero;

        string baseDir = AppContext.BaseDirectory;
        (string rid, string fileName) = OperatingSystem.IsWindows()
            ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64", "libespeak-ng.dll")
            : OperatingSystem.IsMacOS()
                ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64", "libespeak-ng.dylib")
                : (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64", "libespeak-ng.so");

        string[] candidates =
        [
            Path.Combine(baseDir, "runtimes", rid, "native", fileName),
            Path.Combine(baseDir, fileName)
        ];

        foreach (string path in candidates)
        {
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out IntPtr handle))
                return handle;
        }

        return IntPtr.Zero;
    }

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int espeak_Initialize(int output, int buflength, string? path, int options);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int espeak_SetVoiceByName(string name);

    [LibraryImport(LibraryName)]
    private static partial byte* espeak_TextToPhonemes(byte** textptr, int textmode, int phonememode);
}
