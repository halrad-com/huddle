using System.Runtime.InteropServices;

namespace Huddle;

/// <summary>
/// Enables Virtual Terminal (ANSI/VT) processing on this process's console output.
///
/// Why: huddle emits OSC 8 hyperlinks in `docs` / `history` listings. Windows Terminal
/// always understands them, but when huddle.exe is launched under the legacy console
/// host (conhost — e.g. started from a shortcut with the default terminal set to
/// "Windows Console Host"), VT processing is OFF by default and every escape sequence
/// prints as literal garbage characters.
///
/// TryEnable() turns VT processing on when possible. When it cannot (output redirected,
/// truly ancient host), callers should fall back to plain text — that is what
/// ConsoleUI.HyperlinksEnabled consumes. Note: conhost WITH VT enabled silently ignores
/// OSC 8 (titles render as plain text, not clickable) — readable either way; only
/// Windows Terminal makes them clickable.
/// </summary>
public static class VtConsole
{
    private const int StdOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    /// <summary>
    /// Ensure VT processing is on for stdout. Returns true when the console now
    /// processes VT sequences (already on, or successfully enabled); false when
    /// there is no console or the mode could not be set.
    /// </summary>
    public static bool TryEnable()
    {
        try
        {
            var handle = GetStdHandle(StdOutputHandle);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return false;
            if (!GetConsoleMode(handle, out var mode)) return false;   // not a console (redirected)
            if ((mode & EnableVirtualTerminalProcessing) != 0) return true;
            return SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
        }
        catch
        {
            return false;
        }
    }
}
