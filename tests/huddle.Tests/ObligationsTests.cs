using Huddle;
using Xunit;

namespace HuddleTests;

/// <summary>
/// Phase 3, §5.6 and §5.7: huddle contradicts a session that reports itself idle while it
/// owes work, and tells it what it owes at every wake. The audited session reported
/// "nothing in flight, inbox clear" while holding four unread assignments, the oldest four
/// days old — huddle knew both facts and said nothing.
/// </summary>
public class ObligationsTests : IDisposable
{
    private readonly string _dir, _repoA, _repoB;
    private static readonly DateTimeOffset Now = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    public ObligationsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "huddle-oblig-" + Guid.NewGuid().ToString("N"));
        _repoA = Path.Combine(_dir, "repoA");
        _repoB = Path.Combine(_dir, "repoB");
        Directory.CreateDirectory(Path.Combine(_repoA, "docs", "ledger"));
        Directory.CreateDirectory(Path.Combine(_repoB, "docs", "ledger"));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private IEnumerable<(string, string)> Repos() => new[] { ("repoA", _repoA), ("repoB", _repoB) };

    private void Events(string repoRoot, params string[] lines) =>
        File.WriteAllText(Path.Combine(repoRoot, "docs", "ledger", "events.jsonl"),
            string.Join("\n", lines) + "\n");

    private static string Assigned(string id, string owner, string ts, string title = "do the thing") =>
        $$"""{"ts":"{{ts}}","event":"task-assigned","id":"{{id}}","owner":"{{owner}}","actor":"x:y","title":"{{title}}"}""";

    private static string Ev(string id, string ev, string ts, string owner = "app:backenddev") =>
        $$"""{"ts":"{{ts}}","event":"{{ev}}","id":"{{id}}","owner":"{{owner}}"}""";

    [Fact]
    public void Open_tasks_are_found_across_every_repo_oldest_first()
    {
        Events(_repoA, Assigned("T-001", "app:backenddev", "2026-08-19T12:00:00Z", "four days old"));
        Events(_repoB, Assigned("T-002", "app:backenddev", "2026-08-23T08:00:00Z", "four hours old"));
        var open = Obligations.For("app:backenddev", Repos(), Now);
        Assert.Equal(2, open.Count);
        Assert.Equal("T-001", open[0].Id.ToString());
        Assert.Equal("repoA", open[0].Repo);
        Assert.Equal(TimeSpan.FromDays(4), open[0].Age(Now));
    }

    [Fact]
    public void Terminal_tasks_are_not_owed()
    {
        Events(_repoA,
            Assigned("T-001", "app:backenddev", "2026-08-19T12:00:00Z"),
            Ev("T-001", "task-declined", "2026-08-20T12:00:00Z"),
            Assigned("T-002", "app:backenddev", "2026-08-21T12:00:00Z"),
            Ev("T-002", "task-acked", "2026-08-21T13:00:00Z"));
        var open = Obligations.For("app:backenddev", Repos(), Now);
        Assert.Equal("T-002", Assert.Single(open).Id.ToString());
        Assert.Equal("acked", open[0].State);
    }

    [Fact]
    public void Another_sessions_task_is_not_mine()
    {
        Events(_repoA, Assigned("T-001", "app:architect", "2026-08-19T12:00:00Z"));
        Assert.Empty(Obligations.For("app:backenddev", Repos(), Now));
    }

    [Fact]
    public void Status_note_states_the_count_and_the_oldest_age()
    {
        Events(_repoA,
            Assigned("T-001", "app:backenddev", "2026-08-19T12:00:00Z"),
            Assigned("T-002", "app:backenddev", "2026-08-23T08:00:00Z"));
        var note = Obligations.StatusNote(Obligations.For("app:backenddev", Repos(), Now), Now);
        Assert.Contains("2 open", note);
        Assert.Contains("4d", note);
    }

    [Fact]
    public void A_clean_session_gets_no_note_and_no_context_section()
    {
        var none = Obligations.For("app:backenddev", Repos(), Now);
        Assert.Empty(none);
        Assert.Equal("", Obligations.StatusNote(none, Now));
        Assert.Equal("", Obligations.ContextSection(none, Now));
    }

    [Fact]
    public void Context_section_lists_each_item_and_forbids_reporting_idle()
    {
        Events(_repoA, Assigned("T-001", "app:backenddev", "2026-08-19T12:00:00Z", "WMA transcode server half"));
        var text = Obligations.ContextSection(Obligations.For("app:backenddev", Repos(), Now), Now);
        Assert.Contains("YOU OWE", text);
        Assert.Contains("repoA:T-001", text);
        Assert.Contains("WMA transcode server half", text);
        Assert.Contains("4d ago", text);
        Assert.Contains("Do not report yourself idle", text);
    }

    [Fact]
    public void A_repo_with_no_ledger_or_a_broken_one_contributes_nothing_and_never_throws()
    {
        Directory.Delete(Path.Combine(_repoB, "docs", "ledger"), true);
        File.WriteAllText(Path.Combine(_repoA, "docs", "ledger", "events.jsonl"), "{ not json\n");
        Assert.Empty(Obligations.For("app:backenddev", Repos(), Now));
    }

    [Fact]
    public void A_missing_repo_root_is_not_an_error()
    {
        var gone = new[] { ("ghost", Path.Combine(_dir, "does-not-exist")) };
        Assert.Empty(Obligations.For("app:backenddev", gone, Now));
    }
}

/// <summary>§5.6 extension earned by I016: two live sessions on one identity are named.</summary>
public class DuplicateIdentityStatusTests
{
    [Fact]
    public void Two_pids_on_one_identity_are_reported()
    {
        var line = Assert.Single(Obligations.DuplicateIdentities(
            new[] { ("otherapp:architect", 3320), ("otherapp:architect", 16096), ("app:architect", 5) }));
        Assert.Contains("otherapp:architect", line);
        Assert.Contains("3320", line);
        Assert.Contains("16096", line);
        Assert.Contains("stop one by PID", line);
    }

    [Fact]
    public void One_session_per_identity_is_silent()
    {
        Assert.Empty(Obligations.DuplicateIdentities(
            new[] { ("otherapp:architect", 3320), ("app:architect", 5) }));
    }

    [Fact]
    public void The_same_pid_listed_twice_is_not_a_duplicate()
    {
        Assert.Empty(Obligations.DuplicateIdentities(
            new[] { ("otherapp:architect", 3320), ("otherapp:architect", 3320) }));
    }
}
