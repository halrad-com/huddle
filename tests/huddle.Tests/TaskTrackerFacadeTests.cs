using Huddle;

namespace Huddle.Tests;

/// <summary>
/// Spec §5.2. TaskTracker keeps its public shape and becomes a façade over the ledger.
/// Two consequences fall out, and they are the point of the change:
///
/// <list type="bullet">
/// <item>ids stop resetting on restart, so <c>T001</c> is issued once rather than 23 times</item>
/// <item><c>HandleTaskUpdate</c> stops nacking "unknown task" for work that really happened</item>
/// </list>
/// </summary>
public class TaskTrackerFacadeTests : IDisposable
{
    readonly string _dir;
    readonly List<string> _log = new();

    public TaskTrackerFacadeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "huddle-tasktracker-" + Guid.NewGuid().ToString("N"));
        foreach (var repo in new[] { "huddle", "myapp" })
            Directory.CreateDirectory(Path.Combine(_dir, repo, "docs", "ledger"));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    string RootOf(string repo) => Path.Combine(_dir, repo);

    /// <summary>A fresh façade over the same directories — i.e. what a restart produces.</summary>
    TaskTracker New() => new(
        new LedgerWriters(repo => repo is "huddle" or "myapp" ? RootOf(repo) : null, _log.Add),
        () => new[] { "huddle", "myapp" },
        _log.Add);

    // ---- ids are durable ----

    [Fact]
    public void An_id_survives_a_restart_and_is_never_reissued()
    {
        var first = New().Create("write the writer", "huddle:backenddev", "huddle:architect");
        Assert.Equal("huddle:T-001", first!.TaskId);

        // The old tracker's counter lived in memory, so every restart began again at
        // T001 — the audit found that id issued 23 times to 23 different pieces of work.
        var afterRestart = New().Create("wire the console", "huddle:backenddev", "huddle:architect");
        Assert.Equal("huddle:T-002", afterRestart!.TaskId);
    }

    [Fact]
    public void Ids_are_allocated_per_repo_not_globally()
    {
        var t = New();
        Assert.Equal("huddle:T-001", t.Create("a", "huddle:backenddev", "x")!.TaskId);
        Assert.Equal("myapp:T-001", t.Create("b", "myapp:backenddev", "x")!.TaskId);
        Assert.Equal("huddle:T-002", t.Create("c", "huddle:architect", "x")!.TaskId);
    }

    [Fact]
    public void The_id_is_repo_qualified_because_T_001_exists_in_every_repo()
    {
        // Numbering is per repo, so a bare id handed back to an agent and echoed into a
        // task-complete would name a different task in every ledger.
        var t = New().Create("a", "myapp:backenddev", "x")!;
        Assert.StartsWith("myapp:", t.TaskId);
    }

    // ---- updates no longer nack for work that really happened ----

    [Fact]
    public void UpdateState_works_on_an_id_issued_before_a_restart()
    {
        var id = New().Create("a", "huddle:backenddev", "x")!.TaskId;

        // The nack this replaces: the id was real, the work was real, and the tracker
        // had simply forgotten it existed.
        Assert.True(New().UpdateState(id, TaskState.InProgress));
        Assert.True(New().UpdateState(id, TaskState.Completed, "done"));
        Assert.Equal(TaskState.Completed, New().Get(id)!.State);
    }

    [Fact]
    public void An_id_that_names_nothing_is_still_refused()
    {
        Assert.False(New().UpdateState("huddle:T-404", TaskState.Completed));
        Assert.Null(New().Get("huddle:T-404"));
    }

    [Theory]
    [InlineData("T001")]      // the old format, echoed back by an agent that saw it
    [InlineData("T-1")]
    [InlineData("t-001")]
    [InlineData("huddle:T-001")]
    public void An_id_is_matched_by_value_however_it_is_spelled(string spelling)
    {
        New().Create("a", "huddle:backenddev", "x");
        Assert.True(New().UpdateState(spelling, TaskState.InProgress));
    }

    [Fact]
    public void A_bare_id_that_exists_in_two_repos_is_refused_rather_than_guessed()
    {
        var t = New();
        t.Create("a", "huddle:backenddev", "x");
        t.Create("b", "myapp:backenddev", "x");
        // Updating the wrong repo's task silently would be worse than refusing.
        Assert.False(New().UpdateState("T-001", TaskState.Completed));
    }

    [Fact]
    public void A_bare_id_that_is_unambiguous_still_works()
    {
        New().Create("a", "myapp:backenddev", "x");
        Assert.True(New().UpdateState("T-001", TaskState.InProgress));
    }

    // ---- the state machine is enforced ----

    [Fact]
    public void An_illegal_transition_is_refused_and_appends_nothing()
    {
        var id = New().Create("a", "huddle:backenddev", "x")!.TaskId;
        var before = File.ReadAllLines(Path.Combine(RootOf("huddle"), "docs", "ledger", "events.jsonl")).Length;

        // delivered -> in-progress is backwards; the log is forward-only.
        Assert.True(New().UpdateState(id, TaskState.Completed));
        Assert.False(New().UpdateState(id, TaskState.InProgress));

        var after = File.ReadAllLines(Path.Combine(RootOf("huddle"), "docs", "ledger", "events.jsonl")).Length;
        Assert.Equal(before + 1, after);
    }

    [Fact]
    public void A_terminal_task_cannot_be_reopened()
    {
        var id = New().Create("a", "huddle:backenddev", "x")!.TaskId;
        Assert.True(New().UpdateState(id, TaskState.Failed, "crashed"));
        Assert.False(New().UpdateState(id, TaskState.Completed));
        Assert.Equal(TaskState.Failed, New().Get(id)!.State);
    }

    // ---- ledger state maps onto the shape callers already use ----

    [Fact]
    public void A_new_task_reads_as_delegated()
    {
        var id = New().Create("a", "huddle:backenddev", "huddle:architect")!.TaskId;
        var t = New().Get(id)!;
        Assert.Equal(TaskState.Delegated, t.State);
        Assert.Equal("a", t.Description);
        Assert.Equal("huddle:backenddev", t.AssignedTo);
        Assert.Equal("huddle:architect", t.DelegatedBy);
        Assert.Null(t.CompletedAt);
    }

    [Fact]
    public void Acknowledged_still_reads_as_delegated_because_nobody_has_started()
    {
        var id = New().Create("a", "huddle:backenddev", "x")!.TaskId;
        New().Writers.For("huddle")!.Append(new LedgerEvent(
            DateTimeOffset.UtcNow, "task-acked", "T-001", Actor: "huddle:backenddev"));
        Assert.Equal(TaskState.Delegated, New().Get(id)!.State);
    }

    [Fact]
    public void Completion_records_when_it_happened()
    {
        var id = New().Create("a", "huddle:backenddev", "x")!.TaskId;
        New().UpdateState(id, TaskState.Completed, "shipped");
        var t = New().Get(id)!;
        Assert.Equal(TaskState.Completed, t.State);
        Assert.NotNull(t.CompletedAt);
        Assert.Equal("shipped", t.Notes);
    }

    [Fact]
    public void A_declined_task_reads_as_failed_not_as_missing()
    {
        // §6.4 — nothing is deleted. The trail of work that did not happen is exactly
        // what was missing.
        var id = New().Create("a", "huddle:backenddev", "x")!.TaskId;
        New().Writers.For("huddle")!.Append(new LedgerEvent(
            DateTimeOffset.UtcNow, "task-declined", "T-001", Actor: "huddle:backenddev", Note: "not mine"));
        var t = New().Get(id)!;
        Assert.Equal(TaskState.Failed, t.State);
        Assert.Equal("not mine", t.Notes);
    }

    // ---- the getters ----

    [Fact]
    public void GetAll_spans_every_repo_oldest_first()
    {
        var t = New();
        t.Create("first", "huddle:backenddev", "x");
        t.Create("second", "myapp:backenddev", "x");
        t.Create("third", "huddle:architect", "x");

        var all = New().GetAll();
        Assert.Equal(3, all.Count);
        Assert.Equal(new[] { "first", "second", "third" }, all.Select(x => x.Description));
    }

    [Fact]
    public void GetBySession_filters_by_owner_not_by_dispatcher()
    {
        var t = New();
        t.Create("mine", "huddle:backenddev", "huddle:architect");
        t.Create("theirs", "huddle:architect", "huddle:backenddev");

        var mine = New().GetBySession("huddle:backenddev");
        Assert.Equal("mine", Assert.Single(mine).Description);
    }

    [Fact]
    public void GetBySession_matches_the_instance_id_case_insensitively()
    {
        New().Create("mine", "huddle:backenddev", "x");
        Assert.Single(New().GetBySession("Huddle:BackendDev"));
    }

    [Fact]
    public void An_empty_ledger_yields_no_tasks_rather_than_throwing()
    {
        Assert.Empty(New().GetAll());
        Assert.Empty(New().GetBySession("huddle:backenddev"));
    }

    // ---- parenting ----

    [Fact]
    public void A_delegate_task_may_name_its_parent()
    {
        New().Create("a", "huddle:backenddev", "x", ledgerParent: "D-014");
        var events = New().Writers.For("huddle")!.ReadAll(new List<string>());
        Assert.Equal("huddle:D-014", events.Single().Parent);
    }

    [Fact]
    public void Without_a_parent_the_task_is_an_orphan_and_that_is_recorded_not_rejected()
    {
        New().Create("a", "huddle:backenddev", "x");
        var events = New().Writers.For("huddle")!.ReadAll(new List<string>());
        Assert.Null(events.Single().Parent);
    }

    // ---- a repo with nowhere to write ----

    [Fact]
    public void Creating_for_an_unregistered_repo_returns_null_rather_than_an_untracked_task()
    {
        // Handing back a task that is in no ledger would recreate exactly the bug this
        // change exists to remove: an obligation that looks tracked and disappears at
        // restart. The caller nacks instead.
        Assert.Null(New().Create("a", "nosuchrepo:backenddev", "x"));
        Assert.Contains(_log, l => l.Contains("nosuchrepo"));
    }
}
