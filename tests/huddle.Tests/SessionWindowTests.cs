using Huddle;
namespace Huddle.Tests;

public class SessionWindowTests
{
    private static WindowInfo Win(int handle, string process, string title) =>
        new(new IntPtr(handle), process, title);

    private static IReadOnlySet<IntPtr> Handles(params int[] ids) =>
        ids.Select(i => new IntPtr(i)).ToHashSet();

    private static readonly IReadOnlySet<IntPtr> None = new HashSet<IntPtr>();

    [Fact]
    public void Picks_the_window_still_carrying_the_launch_title()
    {
        var candidates = new[]
        {
            Win(10, "WindowsTerminal", "? Some other session topic"),
            Win(20, "WindowsTerminal", "huddle: myapp:architect"),
        };

        var hit = SessionWindow.PickWindow(
            Handles(10), candidates, SessionWindow.TitleMarker("myapp:architect"), None);

        Assert.Equal(new IntPtr(20), hit);
    }

    [Fact]
    public void Title_match_wins_over_an_unrelated_new_window()
    {
        // A new window from some other app appears in the same instant.
        var candidates = new[]
        {
            Win(30, "WindowsTerminal", "huddle: myapp:architect"),
            Win(40, "conhost", "unrelated console that just opened"),
        };

        var hit = SessionWindow.PickWindow(
            Handles(), candidates, SessionWindow.TitleMarker("myapp:architect"), None);

        Assert.Equal(new IntPtr(30), hit);
    }

    [Fact]
    public void Concurrent_spawns_do_not_claim_the_same_window()
    {
        // Two sessions start together; the first already holds handle 50.
        var candidates = new[]
        {
            Win(50, "WindowsTerminal", "huddle: myapp:architect"),
            Win(60, "WindowsTerminal", "huddle: myapp:reviewer"),
        };

        var hit = SessionWindow.PickWindow(
            Handles(), candidates, SessionWindow.TitleMarker("myapp:reviewer"), Handles(50));

        Assert.Equal(new IntPtr(60), hit);
    }

    [Fact]
    public void Falls_back_to_a_new_console_window_when_the_title_is_already_overwritten()
    {
        // Claude Code renamed the console before the capture ran.
        var candidates = new[]
        {
            Win(70, "explorer", "File Explorer"),
            Win(80, "WindowsTerminal", "? Conversation topic"),
        };

        var hit = SessionWindow.PickWindow(
            Handles(70), candidates, SessionWindow.TitleMarker("myapp:architect"), None);

        Assert.Equal(new IntPtr(80), hit);
    }

    [Fact]
    public void Ignores_new_windows_that_are_not_console_hosts()
    {
        var candidates = new[] { Win(90, "chrome", "Some page opened just now") };

        var hit = SessionWindow.PickWindow(
            Handles(), candidates, SessionWindow.TitleMarker("myapp:architect"), None);

        Assert.Equal(IntPtr.Zero, hit);
    }

    [Fact]
    public void Windows_that_existed_before_the_spawn_are_not_candidates()
    {
        var candidates = new[] { Win(100, "WindowsTerminal", "? A session already running") };

        var hit = SessionWindow.PickWindow(
            Handles(100), candidates, SessionWindow.TitleMarker("myapp:architect"), None);

        Assert.Equal(IntPtr.Zero, hit);
    }

    [Fact]
    public void A_claimed_window_is_never_returned_even_on_a_title_match()
    {
        // Guards against a recycled handle being handed to a second session.
        var candidates = new[] { Win(110, "WindowsTerminal", "huddle: myapp:architect") };

        var hit = SessionWindow.PickWindow(
            Handles(), candidates, SessionWindow.TitleMarker("myapp:architect"), Handles(110));

        Assert.Equal(IntPtr.Zero, hit);
    }

    [Fact]
    public void Enumerate_marshals_real_windows()
    {
        // Exercises the P/Invoke surface itself — delegate signature, StringBuilder
        // marshalling, PID lookup. Asserts only invariants that hold on any desktop.
        var windows = SessionWindow.Enumerate();

        Assert.All(windows, w =>
        {
            Assert.NotEqual(IntPtr.Zero, w.Handle);
            Assert.False(string.IsNullOrEmpty(w.Title));
            Assert.NotNull(w.ProcessName);
        });
        Assert.Equal(windows.Select(w => w.Handle).Distinct().Count(), windows.Count);
    }

    [Theory]
    [InlineData("WindowsTerminal", true)]
    [InlineData("windowsterminal", true)]
    [InlineData("OpenConsole", true)]
    [InlineData("conhost", true)]
    [InlineData("cmd", true)]
    [InlineData("chrome", false)]
    [InlineData("", false)]
    public void Console_hosts_are_recognised_case_insensitively(string process, bool expected) =>
        Assert.Equal(expected, SessionWindow.IsConsoleHost(process));

    // --- PickWindowByPid: resolving a window for an ALREADY-RUNNING session ---
    // A classic console window reports the console APP as its owning process
    // (GetWindowThreadProcessId is special-cased for ConsoleWindowClass), so the
    // window's PID equals the cmd.exe PID huddle tracks. Measured live 2026-08-31:
    // window 5178116 -> pid 4852 (cmd) = huddle:architect's persisted pid.

    private static WindowInfo Win(int handle, string process, string title, uint pid) =>
        new(new IntPtr(handle), process, title, pid);

    [Fact]
    public void Pid_match_finds_the_window_of_a_recovered_session()
    {
        var candidates = new[]
        {
            Win(200, "cmd", "? Some conversation topic", 4852),
            Win(210, "cmd", "? Another topic", 33976),
        };

        var hit = SessionWindow.PickWindowByPid(candidates, 33976, None);

        Assert.Equal(new IntPtr(210), hit);
    }

    [Fact]
    public void Pid_match_never_returns_a_claimed_window()
    {
        // A recycled PID could point at a window another session already holds.
        var candidates = new[] { Win(220, "cmd", "topic", 4852) };

        var hit = SessionWindow.PickWindowByPid(candidates, 4852, Handles(220));

        Assert.Equal(IntPtr.Zero, hit);
    }

    [Fact]
    public void Pid_match_returns_zero_when_windows_terminal_owns_the_window()
    {
        // WT owns its windows itself; no window carries the session's PID.
        var candidates = new[]
        {
            Win(230, "WindowsTerminal", "? Conversation topic", 18964),
        };

        var hit = SessionWindow.PickWindowByPid(candidates, 33976, None);

        Assert.Equal(IntPtr.Zero, hit);
    }

    [Fact]
    public void Pid_zero_matches_nothing()
    {
        // Enumerated windows whose owner exited resolve to pid 0; a session with an
        // unknown pid must not be handed one of those.
        var candidates = new[] { Win(240, "cmd", "topic", 0) };

        var hit = SessionWindow.PickWindowByPid(candidates, 0, None);

        Assert.Equal(IntPtr.Zero, hit);
    }

    [Fact]
    public void Pid_match_prefers_a_console_host_window()
    {
        // If the same process somehow owns a non-console window too (a dialog),
        // the console window is the one focus should raise.
        var candidates = new[]
        {
            Win(250, "someapp", "a dialog", 4852),
            Win(260, "cmd", "? Conversation topic", 4852),
        };

        var hit = SessionWindow.PickWindowByPid(candidates, 4852, None);

        Assert.Equal(new IntPtr(260), hit);
    }

    // IsLive answers only "does this handle exist", which is not enough for anything
    // that means to raise a window: under Windows Terminal GetConsoleWindow returns a
    // hidden zero-size pseudoconsole, a real window that passes IsLive. IsVisible is the
    // second half of that gate. Only handles that need no desktop are asserted here.
    [Fact]
    public void A_zero_handle_is_neither_live_nor_visible()
    {
        Assert.False(SessionWindow.IsLive(IntPtr.Zero));
        Assert.False(SessionWindow.IsVisible(IntPtr.Zero));
    }

    [Fact]
    public void Old_windowinfo_shape_still_constructs()
    {
        // The 3-arg form (spawn-path tests, callers) defaults ProcessId to 0.
        var w = Win(270, "cmd", "title");
        Assert.Equal(0u, w.ProcessId);
    }
}
