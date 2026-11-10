using MoonTTS.Engine.Protocol;

namespace MoonTTS.Engine.Kokoro;

/* File Structure:
        - Ids : string[]
        - Describe() : List<VoiceEntry>
        - GetLanguage(string voiceId) : string?
        - GetName(string voiceId) : string
*/

/* Voice presets shipped with Kokoro. The host learns them through ListVoices and caches them in voices.json */
public static class KokoroVoices
{
    public static readonly string[] Ids =
    [
        /*American Female*/
        "af_alloy", "af_aoede", "af_bella", "af_heart", "af_jessica", "af_kore", "af_nicole", "af_nova", "af_river",
        "af_sarah", "af_sky",

        /*American Male*/
        "am_adam", "am_echo", "am_eric", "am_fenrir", "am_liam", "am_michael", "am_onyx", "am_puck", "am_santa",

        /*British Female*/
        "bf_alice", "bf_emma", "bf_isabella", "bf_lily",

        /*British Male*/
        "bm_daniel", "bm_fable", "bm_george", "bm_lewis",

        /*Spanish Female*/
        "ef_dora",

        /*Spanish Male*/
        "em_alex", "em_santa",

        /*French Female*/
        "ff_siwis",

        /*Hindi Female*/
        "hf_alpha", "hf_beta",

        /*Hindi Male*/
        "hm_omega", "hm_psi",

        /*Italian Female*/
        "if_sara",

        /*Italian Male*/
        "im_nicola",

        /*Japanese Female*/
        "jf_alpha", "jf_gongitsune", "jf_nezumi", "jf_tebukuro",

        /*Japanese Male*/
        "jm_kumo",

        /*Brazilian Portuguese Female*/
        "pf_dora",

        /*Brazilian Portuguese Male*/
        "pm_alex", "pm_santa",

        /*Chinese Female*/
        "zf_xiaobei", "zf_xiaoni", "zf_xiaoxiao", "zf_xiaoyi",

        /*Chinese Male*/
        "zm_yunjian", "zm_yunxi", "zm_yunxia", "zm_yunyang",
    ];

    /* Builds the ListVoices reply */
    public static List<VoiceEntry> Describe() =>
        Ids.Select(id => new VoiceEntry { Id = id, Name = GetName(id), Language = GetLanguage(id) }).ToList();

    /* Kokoro encodes the language in the first letter of the voice id */
    private static string? GetLanguage(string voiceId) => voiceId[0] switch
    {
        'a' => "en-US",
        'b' => "en-GB",
        'e' => "es",
        'f' => "fr",
        'h' => "hi",
        'i' => "it",
        'j' => "ja",
        'p' => "pt-BR",
        'z' => "zh",
        _ => null
    };

    /* Turns "af_heart" into "Heart" */
    private static string GetName(string voiceId)
    {
        int separator = voiceId.IndexOf('_');
        string name = separator >= 0 ? voiceId[(separator + 1)..] : voiceId;
        return name.Length == 0 ? voiceId : char.ToUpperInvariant(name[0]) + name[1..];
    }
}
