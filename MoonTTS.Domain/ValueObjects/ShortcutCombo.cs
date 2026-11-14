namespace MoonTTS.Domain.ValueObjects;

public class ShortcutCombo
{
    public bool HasCtrl { get; }
    public bool HasAlt { get; }
    public bool HasShift { get; }
    public bool HasWin { get; }
    public string MainKey { get; }

    public ShortcutCombo(bool ctrl, bool alt, bool shift, bool win, string mainKey)
    {
        /* Throws an error if it's created without a main key (Ex. "Ctrl+Shift") */
        if (string.IsNullOrEmpty(mainKey))
        {
            throw new ArgumentNullException("The shortcut must contain a main key.");
        }

        /* Validates that at least one modifier key is provided unless MainKey is a standalone F1-F24 function key */
        if (!ctrl && !alt && !shift && !win && !IsFunctionKey(mainKey))
        {
            throw new ArgumentException(
                "The shortcut must contain at least one modifier key (Ctrl, Alt, Shift, or Win) or be a standalone function key (F1-F24).",
                nameof(mainKey)
            );
        }

        HasCtrl = ctrl;
        HasAlt = alt;
        HasShift = shift;
        HasWin = win;
        /* Since we're defining this as Uppercase here, it'll have to be converted to lowercase for systems like
           X11 manually */
        MainKey = mainKey.Trim().ToUpperInvariant();
    }

    /* Parses a string representation into a ShortcutCombo instance */
    public static bool TryParse(string? input, out ShortcutCombo? combo)
    {
        combo = null;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var tokens = input.Split(
            '+',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        bool ctrl = false,
            alt = false,
            shift = false,
            win = false;
        string? mainKey = null;

        foreach (var token in tokens)
        {
            if (
                token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)
                || token.Equals("Control", StringComparison.OrdinalIgnoreCase)
            )
                ctrl = true;
            else if (token.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                alt = true;
            else if (token.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                shift = true;
            else if (
                token.Equals("Win", StringComparison.OrdinalIgnoreCase)
                || token.Equals("Super", StringComparison.OrdinalIgnoreCase)
            )
                win = true;
            else
                mainKey = token;
        }

        if (string.IsNullOrEmpty(mainKey) || (!ctrl && !alt && !shift && !win && !IsFunctionKey(mainKey)))
            return false;

        combo = new ShortcutCombo(ctrl, alt, shift, win, mainKey);
        return true;
    }

    /* Returns true when the key name represents a standalone function key F1 through F24 */
    public static bool IsFunctionKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        ReadOnlySpan<char> span = key.AsSpan().Trim();
        return span.Length is 2 or 3
               && (span[0] == 'F' || span[0] == 'f')
               && int.TryParse(span[1..], out int fnNumber)
               && fnNumber is >= 1 and <= 24;
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (HasCtrl)
            parts.Add("Ctrl");
        if (HasAlt)
            parts.Add("Alt");
        if (HasShift)
            parts.Add("Shift");
        if (HasWin)
            parts.Add("Win");
        parts.Add(MainKey);

        return string.Join("+", parts);
    }
}
