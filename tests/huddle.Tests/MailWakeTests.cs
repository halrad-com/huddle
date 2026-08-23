using Huddle;

namespace Huddle.Tests;

/// <summary>
/// Mail to an IDLE session used to die in pending.txt: the hooks that drain it fire on a
/// turn boundary, and an idle session ends no turn (2026-08-22 — two fix tasks, 27
/// minutes, both recipients idle, operator injected by hand). These pin when huddle
/// nudges the console so the hook has a submit to fold the pending context onto.
/// </summary>
public class MailWakeTests
{
    static readonly DateTime Now = new(2026, 8, 22, 14, 0, 0, DateTimeKind.Utc);
    static readonly TimeSpan Idle = TimeSpan.FromSeconds(20);

    [Fact]
    public void Idle_session_is_woken() =>
        Assert.True(MailWake.ShouldWake(Now.AddSeconds(-60), Now, Idle));

    [Fact]
    public void Busy_session_is_not_woken() =>
        Assert.False(MailWake.ShouldWake(Now.AddSeconds(-5), Now, Idle));

    [Fact]
    public void Unknown_activity_is_treated_as_idle() =>
        Assert.True(MailWake.ShouldWake(null, Now, Idle));

    [Fact]
    public void Exactly_at_the_threshold_wakes() =>
        Assert.True(MailWake.ShouldWake(Now - Idle, Now, Idle));

    [Fact]
    public void Wake_line_is_short_and_has_no_newline()
    {
        Assert.DoesNotContain('\n', MailWake.WakeLine);
        Assert.DoesNotContain('\r', MailWake.WakeLine);
        Assert.True(MailWake.WakeLine.Length < 40);
    }

    // The clock pairing is the whole fix. SessionTrouble.LastActivity returns the
    // transcript's mtime in LOCAL time; comparing it against UtcNow makes every
    // reading on a UTC+n machine negative, so nothing is ever idle and the wake
    // never fires. ShouldWakeSession owns both halves so the pairing is testable.
    [Fact]
    public void A_transcript_untouched_for_a_minute_reads_as_idle()
    {
        var path = Path.Combine(Path.GetTempPath(), "huddle-mailwake-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(path, "{}\n");
        try
        {
            File.SetLastWriteTime(path, DateTime.Now.AddSeconds(-60));
            Assert.True(MailWake.ShouldWakeSession(path, Idle));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_transcript_written_just_now_reads_as_busy()
    {
        var path = Path.Combine(Path.GetTempPath(), "huddle-mailwake-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(path, "{}\n");
        try { Assert.False(MailWake.ShouldWakeSession(path, Idle)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void No_transcript_at_all_reads_as_idle() =>
        Assert.True(MailWake.ShouldWakeSession(null, Idle));

    [Fact]
    public void A_transcript_path_that_does_not_exist_reads_as_idle() =>
        Assert.True(MailWake.ShouldWakeSession(
            Path.Combine(Path.GetTempPath(), "huddle-mailwake-absent-" + Guid.NewGuid().ToString("N") + ".jsonl"), Idle));

    // Only a session with something actually queued is worth nudging — the retry tick
    // walks every watched session and must not inject into ones with an empty queue.
    [Fact]
    public void Pending_file_that_is_absent_or_blank_is_not_worth_a_nudge()
    {
        var dir = Path.Combine(Path.GetTempPath(), "huddle-pending-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "pending.txt");
            Assert.False(MailWake.HasPending(path));
            File.WriteAllText(path, "   \n\n");
            Assert.False(MailWake.HasPending(path));
            File.WriteAllText(path, "[huddle mail from x] hi — read a.json\n");
            Assert.True(MailWake.HasPending(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Projects_root_is_the_claude_transcript_dir()
    {
        var root = MailWake.ProjectsRoot;
        Assert.EndsWith(Path.Combine(".claude", "projects"), root);
        Assert.True(Path.IsPathRooted(root));
    }
}
