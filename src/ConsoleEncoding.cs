using System.Text;

namespace Huddle;

/// <summary>
/// Puts the console into UTF-8 so the characters huddle actually prints survive.
///
/// Why: .NET defaults console output to the system ANSI codepage (1252 here), which has
/// no code point for most of what huddle emits — so it transliterates. The warning sign
/// on a status row (U+26A0) came out as a bare "?", em-dashes flattened to "-", and the
/// operator reasonably read the "?" as part of the message rather than as a dropped
/// glyph. A status annotation that renders as punctuation is worse than no annotation:
/// it looks like a bug in the thing being reported.
///
/// NO BOM, deliberately. Encoding.UTF8 carries a preamble, and .NET writes it at the
/// head of the stream — harmless on a real console, but it corrupts the first line when
/// stdout is redirected to a file or a pipe. Same trap as the claim journal
/// (<see cref="ClaimJournal"/>); same fix.
///
/// Never fatal: a process with no attached console (redirected, service-hosted) throws
/// here, and huddle must still start. When it fails, glyphs degrade exactly as they did
/// before — the caller says so once rather than leaving the operator to wonder.
/// </summary>
public static class ConsoleEncoding
{
    /// <summary>UTF-8 without the preamble. Exposed so the no-BOM property is testable —
    /// the console side is a side effect, but this part is a real invariant.</summary>
    public static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>True when console output is now UTF-8. False means the caller should
    /// expect "?" in place of non-ASCII and may want to say so.</summary>
    public static bool TryEnableUtf8()
    {
        try
        {
            // Already UTF-8 (a parent set the codepage, or the host defaults to it):
            // don't reassign, which would needlessly rebuild Console.Out.
            if (Console.OutputEncoding.CodePage == 65001) return true;
            Console.OutputEncoding = Utf8NoBom;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
