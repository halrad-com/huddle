namespace Huddle;

/// <summary>
/// Parses the operator-editable <c>peekHotkey</c> chord into the flags
/// <c>RegisterHotKey</c> wants.
///
/// <para>A chord with no modifier is refused rather than registered: it would swallow
/// a bare key across every application on the desktop, and a global hotkey that eats
/// "h" everywhere is a bug report, not a feature.</para>
/// </summary>
public static class PeekChord
{
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;

    /// <summary>
    /// The chords huddle tries, in order, when the operator has NOT set <c>peekHotkey</c>.
    ///
    /// <para>A single default is a feature that ships dead the moment a machine already runs
    /// something owning that chord: the startup line said the key was taken and the only way
    /// out was guessing chords by hand. Trying a short list and taking the first one Windows
    /// grants makes the hotkey work on arrival, and the startup line names the chord that
    /// actually bound so the operator never has to read this file to find out.</para>
    ///
    /// <para>Order is deliberate. <c>Win+Alt+H</c> is first because the operator chose it.
    /// The rest climb away from the crowded <c>Ctrl+Alt+letter</c> space that made the old
    /// single default collide. Every entry was probe-registered on the operator's desktop on
    /// 2026-09-05 and measured free; a guessed entry that is already taken is precisely the
    /// dead default this list exists to replace, so a new candidate gets measured, not
    /// reasoned about.</para>
    ///
    /// <para>This list applies ONLY to the unset case. An explicit <c>peekHotkey</c> is
    /// registered alone and never falls back: a chosen chord that cannot be taken has to be
    /// reported, not quietly replaced by a chord the operator did not ask for.</para>
    /// </summary>
    public static readonly IReadOnlyList<string> Candidates =
    [
        "Win+Alt+H",
        "Ctrl+Alt+0",
        "Win+Ctrl+Alt+H",
    ];

    public static bool TryParse(string text, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Trim();
            if (part.Length == 0) return false;

            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= MOD_CONTROL; continue;
                case "alt": modifiers |= MOD_ALT; continue;
                case "shift": modifiers |= MOD_SHIFT; continue;
                case "win" or "windows" or "super": modifiers |= MOD_WIN; continue;
            }

            if (virtualKey != 0) return false;   // two keys in one chord

            if (part.Length == 1 && char.IsLetterOrDigit(part[0]))
            {
                virtualKey = char.ToUpperInvariant(part[0]);
                continue;
            }

            if ((part[0] is 'f' or 'F') && int.TryParse(part[1..], out var fn) && fn is >= 1 and <= 24)
            {
                virtualKey = (uint)(0x70 + fn - 1);   // VK_F1 = 0x70
                continue;
            }

            return false;
        }

        return modifiers != 0 && virtualKey != 0;
    }
}
