using System.Diagnostics;
using System.Windows.Forms;

namespace Huddle;

/// <summary>
/// Owns the overlay's lifetime: one place that can show it, and therefore one place
/// that can leave it on screen.
///
/// <para>Sessions are re-read on EVERY show, never cached. corelib's switcher asked for
/// its data once at script load and reused the window, so the list froze after the
/// first summon; huddle's session set turns over far faster than their saved sets, so
/// a cache here would be wrong within minutes.</para>
/// </summary>
public static class PeekController
{
    private static int _showing;   // 0 or 1: a second summon while open is a no-op

    public static void Show(SessionManager manager, IpcManager? ipc, Action<string> log)
    {
        if (Interlocked.Exchange(ref _showing, 1) == 1)
        {
            // Never silent. _showing only clears in the finally below, after ui.Join()
            // returns, and there is a known stall (an overlay shown but never activated,
            // so OnDeactivate cannot fire and Esc has no focus) where it does not. From
            // then on the verb, the hotkey and every --peek click are no-ops while the
            // launcher still prints "signalled the huddle for ...". Spec section 10: a
            // switcher showing nothing must never be indistinguishable from one that was
            // never summoned.
            log("peek: an overlay is already open.");
            return;
        }

        try
        {
            var tiles = PeekModel.Build(Snapshot(manager, ipc));

            IntPtr? picked = null;

            // The overlay is constructed AND shown on this thread, not merely shown on
            // it: PeekWindow's constructor calls Screen.FromPoint and sets ClientSize
            // and Location, all of which are real WinForms work. huddle's console
            // thread is MTA (Program.Main has no [STAThread]) and so is the signal
            // listener, and Form.ShowDialog is unsupported on MTA — so every summon
            // gets its own STA thread rather than each caller remembering to provide one.
            var ui = new Thread(() =>
            {
                try
                {
                    // Load-bearing, not boilerplate, and it must come before any control
                    // exists. The catch below only covers construction, the CALL into
                    // ShowAndPick and the dispose — NOT a throw from inside ShowDialog's
                    // message pump, and OnShown (which builds ThumbnailHost and registers
                    // DWM thumbnails) and OnPaint are both live throw sites. Under the
                    // default UnhandledExceptionMode.Automatic, WinForms would swallow such
                    // a throw into a modal ThreadExceptionDialog that is neither owned nor
                    // top-most, leaving a TopMost overlay stuck on screen ignoring Esc, the
                    // error hidden underneath it, and huddle's prompt blocked on Join until
                    // the operator Alt+Tabs to find it. ThrowException instead lets the
                    // exception out of ShowDialog, so the catch logs it, the using disposes
                    // the form and Join returns. Thread-scoped, so nothing else is affected.
                    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);

                    // ShowDialog hides rather than disposes; without the using, the
                    // form's window handle would survive every summon.
                    using var window = new PeekWindow(tiles, log);
                    picked = window.ShowAndPick();
                }
                catch (Exception ex) { log($"peek: {ex.Message}"); }
            });
            ui.Name = "peek-ui";   // the console blocks on Join below: name it for hang dumps
            ui.SetApartmentState(ApartmentState.STA);
            ui.Start();
            ui.Join();

            if (picked is { } hWnd && hWnd != IntPtr.Zero && !WindowFocus.BringToFront(hWnd))
                log("peek: Windows denied the foreground switch. Try Alt+Tab.");
        }
        catch (Exception ex)
        {
            // The overlay is a convenience. It must never take huddle down with it.
            log($"peek: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _showing, 0);
        }
    }

    private static List<PeekSource> Snapshot(SessionManager manager, IpcManager? ipc)
    {
        var projectsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

        var unread = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (ipc != null)
        {
            foreach (var row in ipc.GetBacklog())
                unread[row.Session] = row.Unread;
        }

        var sources = new List<PeekSource> { SelfSource(SelfWindow(), SelfUptime()) };

        // Copy first, then iterate. Snapshot's body does file reads and window P/Invokes
        // per session, and the console thread adds and removes instances the whole time
        // a session starts or stops — enumerating the live Dictionary across all of that
        // work is an InvalidOperationException waiting to happen, now that the hotkey and
        // the --peek signal make this reachable from three threads. The copy is not a
        // lock (it enumerates too, so a modification during the copy still throws, and
        // Show's catch-all still turns that into a logged no-op) but it shrinks the
        // window from the whole method to a tight loop. The real fix is a lock inside
        // SessionManager, which is not this feature's to make.
        var instances = manager.Instances.Values.ToList();

        // Titles by window handle, enumerated ONCE. Claude Code renames the console to
        // the conversation topic, which is what the operator actually recognises a
        // session by, so the tile carries it. One enumeration rather than a P/Invoke per
        // session because this runs on every summon.
        var titles = SessionWindow.Enumerate()
            .GroupBy(w => w.Handle)
            .ToDictionary(g => g.Key, g => g.First().Title);

        foreach (var instance in instances)
        {
            if (instance.Status != SessionStatus.Running) continue;

            // A recovered session may have no handle on record yet; resolving by the
            // tracked pid is the same path `focus` uses.
            var hWnd = instance.WindowHandle;
            if (!SessionWindow.IsLive(hWnd) && manager.TryCaptureWindowByPid(instance))
                hWnd = instance.WindowHandle;
            if (!SessionWindow.IsLive(hWnd)) hWnd = IntPtr.Zero;

            string? apiError = null;
            var idle = 0;
            if (instance.SessionId is Guid sid &&
                SessionTrouble.TranscriptPath(projectsRoot, instance.Root, sid) is { } tpath)
            {
                apiError = SessionTrouble.ApiErrorReason(tpath);
                if (apiError == null && SessionTrouble.LastActivity(tpath) is { } last)
                    idle = (int)(DateTime.Now - last).TotalMinutes;
            }

            unread.TryGetValue(instance.SafePathName, out var mail);

            sources.Add(new PeekSource(
                instance.InstanceId,
                instance.Project,
                instance.FormatUptime(),
                hWnd,
                apiError,
                idle,
                mail,
                hWnd != IntPtr.Zero && titles.TryGetValue(hWnd, out var t) ? t : null));
        }

        return sources;
    }

    /// <summary>
    /// huddle's own console as a tile source, always first in the list.
    ///
    /// <para>Spec section 9: "Huddle's own console is a tile. You need the way back."
    /// It matters most for the hotkey — pressed over a full-screen editor, a switcher
    /// built only from <c>manager.Instances</c> offers no route back to the orchestrator
    /// itself.</para>
    ///
    /// <para>Not a session, so no project, no API error, no idle and no unread: those
    /// are read from a session transcript and an IPC inbox that huddle's console does
    /// not have. Pure and handle-agnostic on purpose — liveness is resolved by the
    /// caller, so the tile's shape stays testable without a desktop.</para>
    /// </summary>
    public static PeekSource SelfSource(IntPtr consoleWindow, string uptime) =>
        new(PeekModel.SelfId, null, uptime, consoleWindow, null, 0, 0);

    // Visibility is required, not just liveness. Under Windows Terminal
    // GetConsoleWindow hands back a hidden zero-size pseudoconsole rather than zero, and
    // that handle passes IsLive: a bare IsLive gate would make the self tile selectable,
    // register a DWM thumbnail against a hidden window (a blank tile), and leave Enter
    // calling BringToFront on something that cannot come to the front. Requiring
    // IsVisible too is the filter SessionWindow.Enumerate already applies to every
    // session handle, so the self tile degrades to the same unselectable tile a
    // WT-hosted session gets. Spec section 5 rule 4.
    private static IntPtr SelfWindow()
    {
        var hWnd = ConsoleIcon.ConsoleWindow();
        return SessionWindow.IsLive(hWnd) && SessionWindow.IsVisible(hWnd) ? hWnd : IntPtr.Zero;
    }

    // A process whose start time cannot be read still gets a tile: the way back is the
    // point of it, and the uptime line is decoration by comparison.
    private static string SelfUptime()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            return SessionInstance.FormatUptime(DateTime.Now - self.StartTime);
        }
        catch { return ""; }
    }
}
