using System.Text.Json.Serialization;

namespace MoonTTS.Engine.Kitten;

/* File Structure:
        - KittenRepoConfig (Class)
            - SampleRate
            - Phonemizer
            - DefaultWeights
            - DefaultLanguage
            - Languages
            - Voices
        - KittenLanguageEntry (Class)
            - Model
            - ModelFile
            - ModelFiles
            - Voices
            - ResolveOnnxFile(string weights) : string
        - KittenVoiceDescriptor (Class)
            - Key
            - Name
            - Description
        - KittenJsonContext (Class)
*/

/* Root schema of a KittenTTS 2.0 Tiny repository config.json */
public sealed class KittenRepoConfig
{
    /* Output audio sample rate in Hz (24000 for KittenTTS 2.0 Tiny) */
    [JsonPropertyName("sample_rate")]
    public int SampleRate { get; set; } = 24000;

    /* Phonemizer revision declared by the model ("v1" for pico/nano, "v2" for mini) */
    [JsonPropertyName("phonemizer")]
    public string Phonemizer { get; set; } = "v1";

    /* Default ONNX weight quantization ("int8" or "int4") */
    [JsonPropertyName("default_weights")]
    public string DefaultWeights { get; set; } = "int8";

    /* Default language code when none is specified */
    [JsonPropertyName("default_language")]
    public string? DefaultLanguage { get; set; }

    /* Per-language ONNX graph and voice pack filenames */
    [JsonPropertyName("languages")]
    public Dictionary<string, KittenLanguageEntry> Languages { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /* Per-language voice metadata catalog */
    [JsonPropertyName("voices")]
    public Dictionary<string, List<KittenVoiceDescriptor>> Voices { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/* Per-language file mapping inside KittenTTS 2.0 config.json */
public sealed class KittenLanguageEntry
{
    /* Internal model identifier */
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    /* Default int8 ONNX filename */
    [JsonPropertyName("model_file")]
    public string ModelFile { get; set; } = string.Empty;

    /* Optional quantization-to-ONNX filename map ("int8", "int4") */
    [JsonPropertyName("model_files")]
    public Dictionary<string, string>? ModelFiles { get; set; }

    /* Voice style embedding archive filename (e.g. "voices_en.npz") */
    [JsonPropertyName("voices")]
    public string Voices { get; set; } = string.Empty;

    /* Resolves the ONNX filename for the requested weight format, falling back to ModelFile */
    public string ResolveOnnxFile(string weights)
    {
        if (ModelFiles != null && ModelFiles.TryGetValue(weights, out string? file) && !string.IsNullOrWhiteSpace(file))
            return file;

        return ModelFile;
    }
}

/* Voice entry metadata inside KittenTTS 2.0 config.json */
public sealed class KittenVoiceDescriptor
{
    /* Internal NPZ array key (e.g. "eleanor") */
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    /* Human-readable speaker name (e.g. "Eleanor") */
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /* Short speaker persona/gender description */
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
}

/* Native AOT source-generated JSON context for KittenTTS 2.0 config.json and vocab.json */
[JsonSerializable(typeof(KittenRepoConfig))]
[JsonSerializable(typeof(Dictionary<string, int>))]
public partial class KittenJsonContext : JsonSerializerContext
{
}
