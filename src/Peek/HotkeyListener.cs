using System.Windows.Forms;

namespace Huddle;

/// <summary>
/// A global hotkey, owned by a hidden message-only window on its own thread.
///
/// <para>Registration fails when another application already owns the chord. That is
/// reported once, by name, and huddle carries on: an unavailable convenience key must
/// never stop the orchestrator starting.</para>
///
/// <para>A listener that lost the chord releases its window and ends its thread instead of
/// entering the message loop. It is a dead object that still answers <see cref="Registered"/>
/// with false, which is exactly what a caller needs to decide whether to try another chord,
/// and it costs nothing to hold while that decision is made.</para>
/// </summary>
public sealed class HotkeyListener : IPeekHotkey
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0xB00C;

    /// <summary>
    /// Registration flag that stops Windows posting a fresh <c>WM_HOTKEY</c> for every
    /// keyboard auto-repeat while the chord is held.
    ///
    /// <para>It is OR'd in HERE, at the call site, and never inside <see cref="PeekChord"/>:
    /// the chord parser's job is to say what the operator asked for, and its tests assert
    /// exact modifier values. This is a registration concern, not a parsing one.</para>
    ///
    /// <para>Without it, holding the chord for about a second queues a dozen summons. The
    /// pump is blocked while the overlay is up (the summon below is synchronous), so those
    /// land after the operator dismisses — the overlay reopens once per queued message and
    /// reads as a stuck window on the feature's primary interaction.</para>
    /// </summary>
    private const uint MOD_NOREPEAT = 0x4000;

    private readonly Thread _thread;
    private HotkeyWindow? _window;

    /// <summary>Set by <see cref="Dispose"/>, read by the listener thread the moment it has
    /// a window.
    ///
    /// <para>The constructor waits five seconds for that window. Nothing has been seen to
    /// take that long, but if it ever did, the constructor would return with the listener
    /// still starting, <see cref="Dispose"/> would find <c>_window</c> null and do nothing,
    /// and the thread would then register the chord and park in <c>Application.Run()</c> for
    /// the life of the process with nothing able to release it. The operator's next attempt
    /// at that chord would fail against huddle's own zombie. This flag is how a late starter
    /// finds out it was already abandoned and tears itself down instead.</para></summary>
    private volatile bool _disposed;

    /// <summary>True when Windows actually gave us the chord.
    ///
    /// <para>Written on the listener thread inside the try below, so it is written BEFORE
    /// the <c>finally</c> that calls <c>ready.Set()</c>, and the constructor does not
    /// return until it has waited on <c>ready</c>. That ordering is what makes the value
    /// safely visible to the caller the moment construction returns: a caller that sees a
    /// listener has already seen this flag settle.</para>
    ///
    /// <para>False means another application owns the chord, so no summon will ever arrive
    /// and the listener has already released its window and ended its thread rather than
    /// park serving a hotkey that cannot fire. Callers that care (the switch behind
    /// <c>settings peekHotkey</c>) use this to decide whether a candidate registration is
    /// worth keeping; disposing a false one is still correct and still a no-op.</para></summary>
    public bool Registered { get; private set; }

    private HotkeyListener(string chord, uint modifiers, uint virtualKey, Action onPressed, Action<string> log)
    {
        var ready = new ManualResetEventSlim(false);

        _thread = new Thread(() =>
        {
            HotkeyWindow? window = null;
            try
            {
                window = new HotkeyWindow(onPressed);
                _window = window;
                if (RegisterHotKey(window.Handle, HotkeyId, modifiers | MOD_NOREPEAT, virtualKey))
                    Registered = true;
                else
                    log($"peek hotkey: '{chord}' is already taken by another application; peek verb still works");
            }
            catch (Exception ex) { log($"peek hotkey: {ex.Message}"); }
            finally { ready.Set(); }

            // Two reasons this thread must NOT enter the message loop.
            //
            // It lost the chord. The loop would then serve a hotkey that can never fire,
            // parking a thread and a message-only window for the life of the process. This
            // branch used to fall through to Application.Run anyway, which is what made a
            // chord conflict terminal by construction: the caller was handed a listener it
            // could only keep, and every retry cost another parked thread. Standing down in
            // the branch that KNOWS it lost is what lets a caller walk a candidate list and
            // still end with one thread and one window.
            //
            // Or it was abandoned: Dispose already ran and found _window null, because the
            // constructor's five-second wait expired before the window above existed. This
            // thread is then the only code that can still let the chord go.
            if (!Registered || _disposed)
            {
                Release(window);
                return;
            }

            Application.Run();
        })
        { IsBackground = true, Name = "huddle-peek-hotkey" };

        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Register the chord, or return null when it cannot be parsed. Never throws.</summary>
    public static HotkeyListener? Start(string chord, Action onPressed, Action<string> log)
    {
        if (!PeekChord.TryParse(chord, out var mods, out var vk))
        {
            log($"peek hotkey: '{chord}' is not a usable chord (need at least one modifier and one key); peek verb still works");
            return null;
        }

        try { return new HotkeyListener(chord, mods, vk, onPressed, log); }
        catch (Exception ex) { log($"peek hotkey: {ex.Message}"); return null; }
    }

    /// <summary>Release the chord and end the message loop. Idempotent: shutdown disposes
    /// this explicitly, before the orchestrator is torn down, and the <c>using var</c> at
    /// the same call site then disposes it again on the way out.</summary>
    public void Dispose()
    {
        try
        {
            // Latched BEFORE the window is read, so a listener thread that has not created
            // its window yet is guaranteed to see this and clean up after itself. The other
            // order would let a late starter check the flag while it is still false and then
            // park in Application.Run holding a chord nobody can release.
            _disposed = true;

            // Exchange rather than read-then-write: the listener thread may be standing
            // itself down at this instant, and exactly one of the two must act on the
            // window. Whichever claims it does the release; the other finds null and stops.
            var window = Interlocked.Exchange(ref _window, null);
            if (window == null) return;

            // Both calls belong on the thread that owns the registration. UnregisterHotKey
            // is documented to fail from any other thread, so calling it from the console
            // thread was a no-op and the real release depended on ExitThread destroying the
            // window — cleanup by accident. Inside the lambda it happens for the stated
            // reason, and it happens before the loop that would otherwise deliver a summon
            // into a half-torn-down huddle.
            window.BeginInvoke(() =>
            {
                UnregisterHotKey(window.Handle, HotkeyId);
                Application.ExitThread();
            });
        }
        catch { /* shutting down; nothing useful left to do */ }
    }

    /// <summary>Give the window back, and any chord it holds, exactly once. Called on the
    /// listener thread when it stands down without entering the message loop, so both calls
    /// are on the thread that owns the window: <c>UnregisterHotKey</c> is documented to fail
    /// from anywhere else. The exchange is what keeps this and a concurrent
    /// <see cref="Dispose"/> from both acting on the same window.</summary>
    private void Release(HotkeyWindow? window)
    {
        if (window == null) return;
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _window, null, window), window)) return;

        try { UnregisterHotKey(window.Handle, HotkeyId); }
        catch { /* stood down before it began; nobody left to tell */ }
        try { window.Dispose(); }
        catch { /* same */ }
    }

    private sealed class HotkeyWindow : Form
    {
        private readonly Action _onPressed;

        public HotkeyWindow(Action onPressed)
        {
            _onPressed = onPressed;
            // Never shown: created only to own a window handle that can receive WM_HOTKEY.
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            Opacity = 0;
            _ = Handle;
        }

        protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false);

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
            {
                try { _onPressed(); } catch { /* a failed summon must not kill the message loop */ }

                // The summon above is synchronous — PeekController shows the overlay and
                // joins its UI thread — so this pump is stopped for as long as the overlay
                // is on screen, and WM_HOTKEY is POSTED, not sent. Every press in that
                // window is therefore still sitting in the queue right now, and dispatching
                // them would reopen the overlay once per press, each needing another Esc.
                // MOD_NOREPEAT already collapses hold-to-repeat; this is what also absorbs
                // a deliberate double-tap. Drop them: the operator's intent was one summon.
                while (PeekMessage(out _, IntPtr.Zero, WM_HOTKEY, WM_HOTKEY, PM_REMOVE)) { }
                return;
            }
            base.WndProc(ref m);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const uint PM_REMOVE = 0x0001;

    /// <summary>The Win32 MSG the drain loop must be handed. Its fields are filled by
    /// <c>PeekMessage</c> and never read here — the loop only cares that a message WAS
    /// removed — but the layout has to be right for the call to be safe.</summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PeekMessage(
        out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);
}
