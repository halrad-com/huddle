using System.Runtime.InteropServices;

namespace Huddle;

/// <summary>
/// Sets the live console window's icon to huddle's own (2026-08-09 operator report:
/// "the icon is still the generic icon"). The .ico embedded via ApplicationIcon only
/// covers Explorer/shortcuts — the console WINDOW belongs to the console host
/// (conhost), which keeps its default icon unless the app sends WM_SETICON at
/// runtime. Same ownership wall as console titles (see SessionWindow).
///
/// Best-effort: under Windows Terminal GetConsoleWindow returns a pseudo-window and
/// the tab icon is WT's to manage — setting it is a harmless no-op there. Never
/// throws; a failed icon is cosmetic.
/// </summary>
public static class ConsoleIcon
{
    private const int WM_SETICON = 0x0080;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string file, int index, out IntPtr large, out IntPtr small, uint count);

    // Icon handles must stay alive as long as the window shows them — held for the
    // process lifetime, released by the OS on exit.
    private static IntPtr _large, _small;

    public static void TrySet(Action<string>? log = null)
    {
        try
        {
            var hwnd = GetConsoleWindow();
            if (hwnd == IntPtr.Zero) return;

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return; // dotnet-run host: no embedded icon to extract

            if (ExtractIconExW(exe, 0, out _large, out _small, 1) == 0)
                return; // no icon resource in the binary

            if (_small != IntPtr.Zero) SendMessage(hwnd, WM_SETICON, ICON_SMALL, _small);
            if (_large != IntPtr.Zero) SendMessage(hwnd, WM_SETICON, ICON_BIG, _large);
        }
        catch (Exception ex)
        {
            log?.Invoke($"ConsoleIcon: {ex.Message} (cosmetic — continuing)");
        }
    }
}
