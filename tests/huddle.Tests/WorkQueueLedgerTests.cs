using Huddle;

namespace Huddle.Tests;

/// <summary>
/// Spec §5.3 — keep the queue's states, stop the lie.
///
/// <para><c>WorkQueue</c> set <c>Done</c> on claim release, which is why all 13 persisted
/// units read Done including the AutoCal unit the operator later found broken. Its states
/// are NOT changed; what changes is what a unit reaching Done means in the ledger. It
/// appends <c>task-delivered</c> and never <c>task-accepted</c>: "delivered" and
/// "accepted" become different words, which they were not before.</para>
/// </summary>
public class WorkQueueLedgerTests : IDisposable
{
    readonly string _dir;
    readonly List<string> _log = new();

    public WorkQueueLedgerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "huddle-queueledger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_dir, "docs", "ledger"));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    LedgerWriter Writer() => new(Path.Combine(_dir, "docs", "ledger"), _log.Add, createIfAbsent: true);
    static DateTimeOffset Now => new(2026, 8, 23, 15, 0, 0, TimeSpan.Zero);

    static WorkUnit Unit(string id = "B-1#one", string? ledger = null) =>
        new(id, "huddle", "backenddev", "Implement the writer and its rotation",
            new[] { "src/LedgerWriter.cs" }, Array.Empty<string>(), Ledger: ledger);

    IReadOnlyList<LedgerTask> Tasks(LedgerWriter w)
    {
        var problems = new List<string>();
        var tasks = TaskMaterializer.Materialize(w.ReadAll(problems), problems);
        Assert.Empty(problems);
        return tasks;
    }

    // ---- dispatch opens a row ----

    [Fact]
    public void Dispatching_a_unit_opens_a_task_owned_by_the_session_it_started()
    {
        var w = Writer();
        WorkQueueLedger.OnDispatched(w, Unit(), Now, _log.Add);

        var t = Assert.Single(Tasks(w));
        Assert.Equal("assigned", t.State);
        Assert.Equal("huddle:backenddev", t.Owner);
        Assert.Equal("_huddle", t.Actor);          // the orchestrator dispatched it
        Assert.Equal("Implement the writer and its rotation", t.Title);
    }

    [Fact]
    public void The_row_is_keyed_on_the_unit_so_it_can_be_found_again_at_Done()
    {
        var w = Writer();
        WorkQueueLedger.OnDispatched(w, Unit(), Now, _log.Add);
        Assert.Contains("unit:B-1#one", Assert.Single(Tasks(w)).Refs);
        Assert.True(w.TryFindTaskByRef("unit:B-1#one", out _));
    }

    [Fact]
    public void A_long_prompt_is_truncated_to_a_hundred_characters()
    {
        var w = Writer();
        var unit = Unit() with { Prompt = new string('x', 400) };
        WorkQueueLedger.OnDispatched(w, unit, Now, _log.Add);
        Assert.Equal(100, Assert.Single(Tasks(w)).Title.Length);
    }

    [Fact]
    public void Re_dispatching_the_same_unit_does_not_open_a_second_row()
    {
        var w = Writer();
        for (int i = 0; i < 3; i++) WorkQueueLedger.OnDispatched(w, Unit(), Now, _log.Add);
        Assert.Single(Tasks(w));
    }

    [Fact]
    public void A_unit_without_a_ledger_field_is_an_orphan()
    {
        // The project slug is a slug, not a ledger id, so nothing is inferred from it.
        var w = Writer();
        WorkQueueLedger.OnDispatched(w, Unit() with { Project = "oracle" }, Now, _log.Add);
        Assert.Null(Assert.Single(Tasks(w)).Parent);
    }

    [Fact]
    public void A_unit_that_names_a_ledger_parent_is_filed_under_it()
    {
        var w = Writer();
        WorkQueueLedger.OnDispatched(w, Unit(ledger: "D-014"), Now, _log.Add);
        Assert.Equal(new LedgerId(LedgerType.Deliverable, 14, "huddle"), Assert.Single(Tasks(w)).Parent);
    }

    // ---- Done means delivered ----

    [Fact]
    public void A_unit_reaching_Done_is_delivered_never_accepted()
    {
        var w = Writer();
        WorkQueueLedger.OnDispatched(w, Unit(), Now, _log.Add);
        WorkQueueLedger.OnSettled(w, Unit(), QueueState.Done, "2 commits", Now.AddHours(1), _log.Add);

        var t = Assert.Single(Tasks(w));
        Assert.Equal("delivered", t.State);
        // The whole point of §5.3. Acceptance is a separate, deliberate act.
        Assert.DoesNotContain(w.ReadAll(new List<string>()), e => e.Event == "task-accepted");
    }

    [Fact]
    public void A_unit_that_failed_is_abandoned_with_the_reason()
    {
        var w = Writer();
        WorkQueueLedger.OnDispatched(w, Unit(), Now, _log.Add);
        w.Append(new LedgerEvent(Now.AddMinutes(1), "task-acked", "T-001"));
        WorkQueueLedger.OnSettled(w, Unit(), QueueState.Failed, "crashed with no commits", Now.AddHours(1), _log.Add);

        var t = Assert.Single(Tasks(w));
        Assert.Equal("abandoned", t.State);
        Assert.Equal("crashed with no commits", t.LastNote);
    }

    [Fact]
    public void A_unit_that_failed_before_it_was_ever_acknowledged_is_declined()
    {
        // Never read, never started. "Abandoned" would claim someone tried.
        var w = Writer();
        WorkQueueLedger.OnDispatched(w, Unit(), Now, _log.Add);
        WorkQueueLedger.OnSettled(w, Unit(), QueueState.Failed, "failed to start", Now.AddHours(1), _log.Add);
        Assert.Equal("declined", Assert.Single(Tasks(w)).State);
    }

    [Fact]
    public void Settling_the_same_unit_twice_records_the_outcome_once()
    {
        var w = Writer();
        WorkQueueLedger.OnDispatched(w, Unit(), Now, _log.Add);
        for (int i = 0; i < 3; i++)
            WorkQueueLedger.OnSettled(w, Unit(), QueueState.Done, "2 commits", Now.AddHours(1), _log.Add);

        Assert.Equal("delivered", Assert.Single(Tasks(w)).State);
        Assert.Equal(1, w.ReadAll(new List<string>()).Count(e => e.Event == "task-delivered"));
    }

    [Fact]
    public void Settling_a_unit_that_was_never_dispatched_is_a_silent_no_op()
    {
        var w = Writer();
        WorkQueueLedger.OnSettled(w, Unit(), QueueState.Done, "x", Now, _log.Add);
        Assert.Empty(w.ReadAll(new List<string>()));
    }

    [Theory]
    [InlineData(QueueState.Queued)]
    [InlineData(QueueState.Active)]
    public void Only_a_terminal_queue_state_settles_the_task(QueueState state)
    {
        var w = Writer();
        WorkQueueLedger.OnDispatched(w, Unit(), Now, _log.Add);
        WorkQueueLedger.OnSettled(w, Unit(), state, "x", Now.AddHours(1), _log.Add);
        Assert.Equal("assigned", Assert.Single(Tasks(w)).State);
    }

    // ---- the queue never accepts anything, by any route ----

    [Fact]
    public void No_sequence_of_queue_events_can_produce_an_acceptance()
    {
        var w = Writer();
        foreach (var state in new[] { QueueState.Done, QueueState.Failed, QueueState.Active, QueueState.Queued })
        {
            var u = Unit($"B-1#{state}");
            WorkQueueLedger.OnDispatched(w, u, Now, _log.Add);
            WorkQueueLedger.OnSettled(w, u, state, "note", Now.AddHours(1), _log.Add);
        }
        Assert.DoesNotContain(w.ReadAll(new List<string>()), e => e.Event == "task-accepted");
    }
}
