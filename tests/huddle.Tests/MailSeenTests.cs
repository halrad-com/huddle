using Huddle;
namespace Huddle.Tests;

/// <summary>
/// The obligation comes from the mail being there; the nudge comes from announcement.
///
/// <para>These two were one signal, and it cost eight days. Ledger ingest was wired to
/// <see cref="IpcManager.MessageReceived"/>, which fires once per file and is suppressed
/// forever afterwards by the delivered index. So a <c>type:"task"</c> mail announced
/// before the ledger existed — or announced while the recipient was down — could sit
/// unread in an inbox indefinitely and never open a row, while every ledger surface
/// truthfully reported nothing open. Two real assignments did exactly that.</para>
/// </summary>
public class MailSeenTests : IDisposable
{
    private readonly string _root;
    private readonly IpcManager _ipc;

    public MailSeenTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "huddle-mailseen-" + Guid.NewGuid().ToString("N"));
        _ipc = new IpcManager(_root, _ => { });
    }

    public void Dispose()
    {
        _ipc.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private const string Safe = "app_architect";
    private const string Instance = "app:architect";

    private void TaskMail(string name)
    {
        var dir = Path.Combine(_root, Safe, "inbox");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name),
            """{"from":"app:planner","to":"app:architect","type":"task","subject":"build the receiving half","body":{}}""");
    }

    [Fact]
    public void Mail_already_announced_is_still_seen_on_every_pass()
    {
        TaskMail("005-assignment.json");
        var seen = 0;
        var announced = 0;
        _ipc.MailSeen = (_, _, _) => seen++;
        _ipc.MessageReceived += (_, _, _) => { announced++; return true; };

        _ipc.Watch(Safe, Instance);   // first pass: announced + recorded
        _ipc.Watch(Safe, Instance);   // second pass: the delivered index suppresses the nudge

        Assert.Equal(1, announced);   // still once — no re-nagging
        Assert.Equal(2, seen);        // but the obligation is re-offered every pass
    }

    [Fact]
    public void Mail_is_seen_even_when_the_wake_could_not_be_delivered()
    {
        // A recipient that is stopped or mid-turn returns false, so nothing is recorded
        // as announced. The row must not depend on that: the work was assigned whether or
        // not anyone was awake to be told.
        TaskMail("005-assignment.json");
        var seen = 0;
        _ipc.MailSeen = (_, _, _) => seen++;
        _ipc.MessageReceived += (_, _, _) => false;

        _ipc.Watch(Safe, Instance);

        Assert.Equal(1, seen);
    }

    [Fact]
    public void Seen_carries_the_instance_and_the_path_the_ledger_keys_on()
    {
        TaskMail("005-assignment.json");
        string? gotInstance = null, gotPath = null;
        IpcMessage? gotMsg = null;
        _ipc.MailSeen = (i, m, p) => { gotInstance = i; gotMsg = m; gotPath = p; };

        _ipc.Watch(Safe, Instance);

        Assert.Equal(Instance, gotInstance);
        Assert.Equal("task", gotMsg?.Type);
        Assert.Equal("build the receiving half", gotMsg?.Subject);
        Assert.Equal(Path.Combine(_root, Safe, "inbox", "005-assignment.json"), gotPath);
    }

    [Fact]
    public void Sweep_reaches_an_inbox_no_session_is_watching()
    {
        // The residual half of the same bug: fixing "announced once" still left mail
        // belonging to a stopped session unreachable, because only watched inboxes are
        // scanned. A twenty-day-old assignment sat there.
        TaskMail("005-assignment.json");            // app_architect — never Watch()ed
        var seen = new List<string>();
        _ipc.MailSeen = (i, _, _) => seen.Add(i);

        _ipc.SweepAllInboxes();

        Assert.Equal(new[] { Instance }, seen);
    }

    [Fact]
    public void Sweep_skips_the_orchestrator_drop_and_non_mailbox_directories()
    {
        Directory.CreateDirectory(Path.Combine(_root, "_huddle", "inbox"));
        File.WriteAllText(Path.Combine(_root, "_huddle", "inbox", "cmd.json"),
            """{"from":"a:b","type":"task","subject":"a command, not an assignment","body":{}}""");
        Directory.CreateDirectory(Path.Combine(_root, "workledger", "claims"));

        var seen = 0;
        _ipc.MailSeen = (_, _, _) => seen++;

        _ipc.SweepAllInboxes();

        Assert.Equal(0, seen);
    }

    [Fact]
    public void Sweep_does_not_announce_and_so_cannot_consume_a_first_run_scan()
    {
        // The sweep has nobody to wake. If it recorded delivery, a session starting later
        // would never get its own wake line for mail it has still not read.
        TaskMail("005-assignment.json");
        _ipc.MailSeen = (_, _, _) => { };
        var announced = 0;
        _ipc.MessageReceived += (_, _, _) => { announced++; return true; };

        _ipc.SweepAllInboxes();
        Assert.Equal(0, announced);

        _ipc.Watch(Safe, Instance);
        Assert.Equal(1, announced);
    }

    [Fact]
    public void A_throwing_handler_never_stops_the_mail()
    {
        // MailSeen runs ahead of the announcement, so a handler that throws must not cost
        // the recipient their wake line.
        TaskMail("005-assignment.json");
        _ipc.MailSeen = (_, _, _) => throw new InvalidOperationException("ledger is unwritable");
        var announced = 0;
        _ipc.MessageReceived += (_, _, _) => { announced++; return true; };

        _ipc.Watch(Safe, Instance);

        Assert.Equal(1, announced);
    }
}
