using Huddle;
namespace Huddle.Tests;

/// <summary>
/// A wake line that names nobody is not a message, it is a doorbell.
///
/// <para>The retry tick re-drives a wake for a session with queued context, but it has no
/// IpcMessage — it knows only that pending.txt is non-empty. It used to inject the bare
/// carrier, which is survivable only while the hook fold always happens. When it does not,
/// the agent is told it has mail and nothing else: no sender, no subject, no path. It then
/// has to go hunting, and on 2026-08-28 a session that went hunting drew the wrong
/// conclusion and reported a colleague for fabricating a request that was real.</para>
/// </summary>
public class PendingWakeTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "huddle-pendingwake-" + Guid.NewGuid().ToString("N")[..8]);

    public PendingWakeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string Write(params string[] lines)
    {
        var p = Path.Combine(_dir, "pending.txt");
        File.WriteAllLines(p, lines);
        return p;
    }

    [Fact]
    public void The_wake_carries_the_sender_when_there_is_one_queued_mail()
    {
        var line = PendingWake.LineFor(Write(
            "[huddle mail from myapp:architect] Take it; here is the seam — read ipc/otherapp_architect/inbox/001.json"));

        Assert.Contains("myapp:architect", line);
        Assert.Contains("ipc/otherapp_architect/inbox/001.json", line);
        Assert.NotEqual(MailWake.WakeLine, line);
    }

    [Fact]
    public void The_newest_mail_leads_and_the_rest_are_counted()
    {
        var line = PendingWake.LineFor(Write(
            "[huddle mail from a:one] first",
            "[huddle mail from b:two] second",
            "[huddle mail from c:three] third"));

        Assert.StartsWith("[huddle mail from c:three] third", line);
        Assert.Contains("+2 more queued", line);
    }

    [Fact]
    public void Blank_lines_are_not_mail()
    {
        var line = PendingWake.LineFor(Write("", "   ", "[huddle mail from a:one] only real one", ""));

        Assert.StartsWith("[huddle mail from a:one] only real one", line);
        Assert.DoesNotContain("more queued", line);
    }

    [Fact]
    public void An_unreadable_queue_still_wakes_the_session()
    {
        // A contentless wake beats no wake: the mail is real either way, and a session
        // that is never woken is the dead-letter bug this whole path exists to fix.
        Assert.Equal(MailWake.WakeLine, PendingWake.LineFor(Path.Combine(_dir, "does-not-exist.txt")));
        Assert.Equal(MailWake.WakeLine, PendingWake.LineFor(Write("", "  ")));
    }

    [Fact]
    public void The_non_blocking_marker_never_reaches_the_console()
    {
        // AppendPending prefixes InfoPendingSentinel for lines that must not wake a stop
        // as an error. The hook strips it before display; this path types straight into a
        // console, so it has to strip it too or the control character goes to the terminal.
        var line = PendingWake.LineFor(Write(
            IpcManager.InfoPendingSentinel + "[huddle mail from a:one] quiet notice"));

        Assert.StartsWith("[huddle mail from a:one]", line);
        Assert.DoesNotContain(IpcManager.InfoPendingSentinel, line);
    }

    [Fact]
    public void Delivery_and_the_redrive_share_one_producer()
    {
        // The guard that replaces a test on Program.Main's lambda. Both inject sites call
        // LineFor, so a wake that names nobody has to fail HERE first. If someone reaches
        // for the bare carrier again, this is what goes red.
        var p = Write("[huddle mail from myapp:architect] here is the seam — read ipc/t/inbox/1.json");

        Assert.NotEqual(MailWake.WakeLine, PendingWake.LineFor(p));
        Assert.Contains("myapp:architect", PendingWake.LineFor(p));
    }
}
