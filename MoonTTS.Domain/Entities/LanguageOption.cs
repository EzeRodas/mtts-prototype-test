namespace MoonTTS.Domain.Entities;

/* Represents a selectable UI display language in Settings */
public sealed record LanguageOption(string Code, string DisplayName)
{
    /* Returns the human-readable language name for ComboBox display */
    public override string ToString() => DisplayName;
}
