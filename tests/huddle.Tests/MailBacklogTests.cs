using Huddle;
namespace Huddle.Tests;

/// <summary>
/// Exercises the receipt bookkeeping against a real directory tree: what counts as
/// unread, and what huddle clears once an agent has acknowledged mail.
/// </summary>
public class MailBacklogTests : IDisposable
{
    private readonly string _root;
    private readonly IpcManager _ipc;

    public MailBacklogTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "huddle-backlog-" + Guid.NewGuid().ToString("N"));
        _ipc = new IpcManager(_root, _ => { });
    }

    public void Dispose()
    {
        _ipc.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void Mail(string session, string folder, string name)
    {
        var dir = Path.Combine(_root, session, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), "{\"from\":\"x\",\"subject\":\"y\"}");
    }

    private void Queue(string session, params string[] lines)
    {
        Directory.CreateDirectory(Path.Combine(_root, session));
        File.WriteAllLines(_ipc.PendingPath(session), lines);
    }

    [Fact]
    public void Mail_sitting_in_an_inbox_counts_as_unread()
    {
        Mail("app_architect", "inbox", "001-handoff.json");
        Mail("app_architect", "inbox", "002-review.json");

        var row = Assert.Single(_ipc.GetBacklog());
        Assert.Equal("app_architect", row.Session);
        Assert.Equal(2, row.Unread);
        Assert.Equal(0, row.Queued);
        Assert.NotNull(row.Oldest);
    }

    [Fact]
    public void Acknowledged_mail_is_cleared_and_stops_counting()
    {
        Mail("app_architect", "inbox", "001-handoff.json");
        Mail("app_architect", "inbox", "002-review.json");
        Mail("app_architect", "processed", "001-handoff.json");   // agent copied it

        var row = Assert.Single(_ipc.GetBacklog());
        Assert.Equal(1, row.Unread);
        Assert.False(File.Exists(Path.Combine(_root, "app_architect", "inbox", "001-handoff.json")));
        Assert.True(File.Exists(Path.Combine(_root, "app_architect", "inbox", "002-review.json")));
    }

    [Fact]
    public void Queued_wake_lines_are_reported_separately_from_unread_mail()
    {
        Queue("app_architect", "[huddle mail from x] one", "[huddle ack:claim] two");

        var row = Assert.Single(_ipc.GetBacklog());
        Assert.Equal(2, row.Queued);
        Assert.Equal(0, row.Unread);
    }

    [Fact]
    public void Blank_lines_in_the_queue_are_not_counted()
    {
        Queue("app_architect", "[huddle mail from x] one", "", "   ");

        var row = Assert.Single(_ipc.GetBacklog());
        Assert.Equal(1, row.Queued);
    }

    [Fact]
    public void Sessions_with_nothing_outstanding_are_omitted()
    {
        Mail("app_architect", "processed", "001-old.json");
        Directory.CreateDirectory(Path.Combine(_root, "app_reviewer", "inbox"));

        Assert.Empty(_ipc.GetBacklog());
    }

    [Fact]
    public void Orchestrator_directories_are_not_sessions()
    {
        Mail("_huddle", "inbox", "cmd-claim.json");

        Assert.Empty(_ipc.GetBacklog());
    }

    [Fact]
    public void Busiest_inbox_is_listed_first()
    {
        Mail("app_architect", "inbox", "001.json");
        Mail("app_reviewer", "inbox", "001.json");
        Mail("app_reviewer", "inbox", "002.json");

        var rows = _ipc.GetBacklog();
        Assert.Equal("app_reviewer", rows[0].Session);
        Assert.Equal(2, rows[0].Unread);
    }
}
