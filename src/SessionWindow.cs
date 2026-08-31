using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Huddle;

/// <summary>
/// A visible top-level window, as seen during a spawn-time enumeration.
/// <para><see cref="ProcessId"/> is the window's owning process. For a classic
/// console window this is the console APPLICATION (cmd.exe), not conhost —
/// GetWindowThreadProcessId is special-cased for ConsoleWindowClass — which is
/// exactly the PID huddle tracks per session. Defaults to 0 for callers that
/// only care about title/name matching.</para>
/// </summary>
public readonly record struct WindowInfo(IntPtr Handle, string ProcessName, string Title, uint ProcessId = 0);

/// <summary>
/// Identifies which console window a spawned session lives in.
///
/// <para>Process.MainWindowHandle cannot answer this: since Windows 7 the console
/// window belongs to the console host, not the console process, so every cmd.exe
/// and claude process reports a zero handle. Under Windows Terminal the windows
/// all belong to one shared WindowsTerminal process, so the owning PID identifies
/// nothing either.</para>
///
/// <para>So huddle captures the handle at spawn instead: snapshot the visible
/// windows, launch, then look for one that is new. The `title huddle: &lt;id&gt;`
/// set on the cmd line is a reliable discriminator during this window — Claude
/// Code overwrites the console title with the conversation topic soon after, but
/// not before the window has appeared. Matching it keeps concurrent spawns (a
/// dispatch-batch starting several sessions at once) from claiming each other's
/// windows; the unclaimed-new-window rule is only the fallback.</para>
/// </summary>
public static class SessionWindow
{
    /// <summary>Marker huddle puts in the console title at launch.</summary>
    public static string TitleMarker(string instanceId) => $"huddle: {instanceId}";

    // Processes that can own a console window: Windows Terminal, the modern and
    // legacy console hosts, and cmd itself on systems that still draw their own.
    private static readonly string[] ConsoleHosts =
        { "windowsterminal", "openconsole", "conhost", "cmd" };

    public static bool IsConsoleHost(string processName) =>
        ConsoleHosts.Contains(processName.ToLowerInvariant());

    /// <summary>
    /// Choose the window belonging to a just-spawned session.
    ///
    /// Prefers a window whose title still carries <paramref name="marker"/>. Falls
    /// back to the first newly-appeared console-host window that no other session
    /// already holds. Returns IntPtr.Zero when nothing qualifies.
    ///
    /// Pure: all window enumeration happens in the caller, so this is testable.
    /// </summary>
    public static IntPtr PickWindow(
        IReadOnlySet<IntPtr> before,
        IEnumerable<WindowInfo> candidates,
        string marker,
        IReadOnlySet<IntPtr> claimed)
    {
        var fresh = candidates
            .Where(w => !before.Contains(w.Handle) && !claimed.Contains(w.Handle))
            .ToList();

        // A titled match is authoritative even if the window predates the snapshot
        // (a console host can reuse a window we had already seen).
        var titled = candidates.FirstOrDefault(w =>
            !claimed.Contains(w.Handle) &&
            w.Title.Contains(marker, StringComparison.OrdinalIgnoreCase));
        if (titled.Handle != IntPtr.Zero) return titled.Handle;

        var host = fresh.FirstOrDefault(w => IsConsoleHost(w.ProcessName));
        return host.Handle;
    }

    /// <summary>
    /// Choose the window of an ALREADY-RUNNING session by its tracked process id —
    /// the recovery-path counterpart of <see cref="PickWindow"/>, which needs a
    /// before/after spawn snapshot this path does not have.
    ///
    /// <para>Works because a classic console window reports the console app (the
    /// session's cmd.exe) as its owning process — measured live 2026-08-31 against
    /// state.json's persisted PIDs. When Windows Terminal owns the windows they all
    /// belong to WindowsTerminal, nothing matches, and this correctly returns Zero
    /// (there is no per-session window to identify there).</para>
    ///
    /// Prefers a console-host-owned window when the process owns several. Pure.
    /// </summary>
    public static IntPtr PickWindowByPid(
        IEnumerable<WindowInfo> candidates,
        uint pid,
        IReadOnlySet<IntPtr> claimed)
    {
        if (pid == 0) return IntPtr.Zero;    // unknown owner: enumeration uses 0 for "exited"

        var mine = candidates
            .Where(w => w.ProcessId == pid && !claimed.Contains(w.Handle))
            .ToList();

        var host = mine.FirstOrDefault(w => IsConsoleHost(w.ProcessName));
        return host.Handle != IntPtr.Zero ? host.Handle : mine.FirstOrDefault().Handle;
    }

    /// <summary>Handles of every visible, titled top-level window.</summary>
    public static HashSet<IntPtr> Snapshot() =>
        Enumerate().Select(w => w.Handle).ToHashSet();

    /// <summary>
    /// Poll for the window of a session that has just been launched, giving the
    /// console host time to create it. Returns IntPtr.Zero if none appears within
    /// <paramref name="timeout"/> — the session still runs, it just cannot be focused.
    /// </summary>
    public static IntPtr WaitForWindow(
        IReadOnlySet<IntPtr> before,
        string marker,
        Func<IReadOnlySet<IntPtr>> claimed,
        TimeSpan timeout,
        TimeSpan pollInterval)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var hit = PickWindow(before, Enumerate(), marker, claimed());
            if (hit != IntPtr.Zero) return hit;
            Thread.Sleep(pollInterval);
        }
        return IntPtr.Zero;
    }

    /// <summary>True if the handle still refers to a live window.</summary>
    public static bool IsLive(IntPtr hWnd) => hWnd != IntPtr.Zero && IsWindow(hWnd);

    /// <summary>Enumerate visible, titled top-level windows with their owning process.</summary>
    public static List<WindowInfo> Enumerate()
    {
        var found = new List<WindowInfo>();
        var names = new Dictionary<uint, string>();

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;

            var len = GetWindowTextLength(hWnd);
            if (len == 0) return true;                 // untitled: tool windows, message sinks
            var sb = new StringBuilder(len + 1);
            GetWindowText(hWnd, sb, sb.Capacity);

            GetWindowThreadProcessId(hWnd, out var pid);
            if (!names.TryGetValue(pid, out var name))
            {
                try { name = Process.GetProcessById((int)pid).ProcessName; }
                catch { name = ""; }                   // exited between enumeration and lookup
                names[pid] = name;
            }

            found.Add(new WindowInfo(hWnd, name, sb.ToString(), pid));
            return true;
        }, IntPtr.Zero);

        return found;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
