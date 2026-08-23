using Huddle;

namespace Huddle.Tests;

/// <summary>
/// The operator's three write verbs, and the overlay that makes them possible.
///
/// <para>Hierarchy rows live in ledger.md, which huddle NEVER rewrites — it is the
/// operator's file and its value is that it is reviewable in a diff. So a state change to
/// a hierarchy row is recorded as an event and applied as an OVERLAY at read time:
/// ledger.md's State column is the baseline, events win.</para>
/// </summary>
public class LedgerAcceptTests : IDisposable
{
    readonly string _dir;
    readonly List<string> _log = new();

    public LedgerAcceptTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "huddle-ledgeraccept-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(LedgerDir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    string LedgerDir => Path.Combine(_dir, "docs", "ledger");
    LedgerWriter Writer() => new(LedgerDir, _log.Add, createIfAbsent: true);
    static DateTimeOffset Now => new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    void WriteLedger(params string[] rows)
    {
        var text = "---\nrepo: huddle\n---\n# Ledger — huddle\n\n" +
                   "| ID | Type | Parent | Title | State | Pri | Owner | Accepts | Refs |\n" +
                   "|----|------|--------|-------|-------|-----|-------|---------|------|\n" +
                   string.Join("\n", rows) + "\n";
        File.WriteAllText(Path.Combine(LedgerDir, "ledger.md"), text);
    }

    LedgerRepoSnapshot Snap() => LedgerView.Load("huddle", _dir);
    LedgerRow Row(string id) => Snap().Rows.Single(r => r.Id.ToString() == id);

    // ---- the overlay ----

    [Fact]
    public void With_no_events_the_state_is_whatever_ledger_md_says()
    {
        WriteLedger("| F-001 | feature | | Ledger phase 2 | planned | P0 | me | tests | |");
        Assert.Equal("planned", Row("F-001").State);
    }

    [Fact]
    public void A_state_event_wins_over_the_ledger_md_column()
    {
        WriteLedger("| F-001 | feature | | Ledger phase 2 | planned | P0 | me | tests | |");
        Writer().Append(new LedgerEvent(Now, "state", "F-001", Actor: "operator", From: "planned", To: "dispatched"));
        Assert.Equal("dispatched", Row("F-001").State);
    }

    [Fact]
    public void The_latest_state_event_wins_not_the_first()
    {
        WriteLedger("| F-001 | feature | | Ledger phase 2 | planned | P0 | me | tests | |");
        var w = Writer();
        w.Append(new LedgerEvent(Now, "state", "F-001", From: "planned", To: "dispatched"));
        w.Append(new LedgerEvent(Now.AddHours(1), "state", "F-001", From: "dispatched", To: "delivered"));
        Assert.Equal("delivered", Row("F-001").State);
    }

    [Fact]
    public void Events_are_applied_by_timestamp_not_by_file_order()
    {
        WriteLedger("| F-001 | feature | | Ledger phase 2 | planned | P0 | me | tests | |");
        var w = Writer();
        w.Append(new LedgerEvent(Now.AddHours(1), "state", "F-001", To: "delivered"));
        w.Append(new LedgerEvent(Now, "state", "F-001", To: "dispatched"));   // backdated
        Assert.Equal("delivered", Row("F-001").State);
    }

    [Fact]
    public void A_state_event_for_a_row_that_is_not_in_ledger_md_is_reported_not_dropped()
    {
        WriteLedger("| F-001 | feature | | Ledger phase 2 | planned | P0 | me | tests | |");
        Writer().Append(new LedgerEvent(Now, "state", "F-099", To: "delivered"));
        // Silent loss is the failure mode this whole design exists to prevent.
        Assert.Contains(Snap().Problems, p => p.Contains("F-099"));
    }

    [Fact]
    public void The_overlay_does_not_touch_tasks()
    {
        // Tasks are materialized by replaying task-* events; a `state` event is for
        // hierarchy rows only and must not fork a second source of truth for a task.
        var w = Writer();
        w.Append(new LedgerEvent(Now, "task-assigned", "T-001", Owner: "huddle:backenddev", Title: "x"));
        w.Append(new LedgerEvent(Now.AddMinutes(1), "state", "T-001", To: "accepted"));
        WriteLedger("| F-001 | feature | | Ledger phase 2 | planned | P0 | me | tests | |");
        Assert.Equal("assigned", Snap().Tasks.Single().State);
    }

    // ---- accept ----

    [Fact]
    public void Accept_is_refused_when_the_item_is_not_delivered()
    {
        WriteLedger("| F-001 | feature | | Ledger phase 2 | planned | P0 | me | tests | |");
        Assert.False(LedgerCommandsWrite.TryAccept(Snap(), Row("F-001").Id, "operator", Now, out var ev, out var why));
        Assert.Null(ev);
        Assert.Contains("planned", why);
    }

    [Fact]
    public void Accept_is_allowed_on_a_delivered_feature()
    {
        WriteLedger("| F-001 | feature | | Ledger phase 2 | delivered | P0 | me | | |");
        Assert.True(LedgerCommandsWrite.TryAccept(Snap(), Row("F-001").Id, "operator", Now, out var ev, out _));
        Assert.Equal("state", ev!.Event);
        Assert.Equal("F-001", ev.Id);
        Assert.Equal("delivered", ev.From);
        Assert.Equal("accepted", ev.To);
        Assert.Equal("operator", ev.Actor);
    }

    [Fact]
    public void A_deliverable_without_an_accepts_gate_cannot_be_accepted()
    {
        // §1.3 / acceptance criterion 4. Huddle does not RUN the gate; it refuses to
        // record acceptance when nobody has said what would prove the thing works.
        WriteLedger("| D-001 | deliverable | | Writer appends | delivered | P0 | me | | |");
        Assert.False(LedgerCommandsWrite.TryAccept(Snap(), Row("D-001").Id, "operator", Now, out var ev, out var why));
        Assert.Null(ev);
        Assert.Contains("accepts", why);
    }

    [Fact]
    public void A_deliverable_with_a_named_gate_can_be_accepted()
    {
        WriteLedger("| D-001 | deliverable | | Writer appends | delivered | P0 | me | LedgerWriterTests | |");
        Assert.True(LedgerCommandsWrite.TryAccept(Snap(), Row("D-001").Id, "operator", Now, out _, out _));
    }

    [Fact]
    public void Accept_reads_the_overlaid_state_not_the_stale_column()
    {
        // The row still says planned in ledger.md; events have carried it to delivered.
        // Accept must agree with what `ledger` renders, or the operator is told the item
        // is in a state they cannot see.
        WriteLedger("| F-001 | feature | | Ledger phase 2 | planned | P0 | me | | |");
        var w = Writer();
        w.Append(new LedgerEvent(Now, "state", "F-001", To: "dispatched"));
        w.Append(new LedgerEvent(Now.AddMinutes(1), "state", "F-001", To: "delivered"));
        Assert.True(LedgerCommandsWrite.TryAccept(Snap(), Row("F-001").Id, "operator", Now.AddHours(1), out var ev, out var why));
        Assert.Equal("delivered", ev!.From);
    }

    [Fact]
    public void Accepting_something_that_is_in_no_ledger_says_so()
    {
        WriteLedger("| F-001 | feature | | Ledger phase 2 | delivered | P0 | me | | |");
        Assert.False(LedgerCommandsWrite.TryAccept(Snap(), new LedgerId(LedgerType.Feature, 99, null), "operator", Now, out _, out var why));
        Assert.Contains("F-099", why);
    }

    // ---- accepting a task ----

    [Fact]
    public void A_task_under_a_gated_deliverable_inherits_that_gate()
    {
        WriteLedger("| D-001 | deliverable | | Writer appends | planned | P0 | me | | |");
        var w = Writer();
        w.Append(new LedgerEvent(Now, "task-assigned", "T-001", Owner: "huddle:backenddev", Parent: "D-001", Title: "x"));
        w.Append(new LedgerEvent(Now.AddMinutes(1), "task-acked", "T-001"));
        w.Append(new LedgerEvent(Now.AddMinutes(2), "task-progress", "T-001"));
        w.Append(new LedgerEvent(Now.AddMinutes(3), "task-delivered", "T-001"));

        Assert.False(LedgerCommandsWrite.TryAccept(Snap(), new LedgerId(LedgerType.Task, 1, null), "operator", Now, out _, out var why));
        Assert.Contains("D-001", why);
        Assert.Contains("accepts", why);
    }

    [Fact]
    public void An_orphan_task_is_accepted_and_the_event_records_that_it_was_ungated()
    {
        // §5.3: there is no Deliverable to gate against, so acceptance is allowed — but
        // the count of ungated acceptances is itself a reading on how much delegation is
        // running ahead of the plan.
        WriteLedger("| F-001 | feature | | unrelated | planned | P0 | me | | |");
        var w = Writer();
        w.Append(new LedgerEvent(Now, "task-assigned", "T-001", Owner: "huddle:backenddev", Title: "x"));
        w.Append(new LedgerEvent(Now.AddMinutes(1), "task-acked", "T-001"));
        w.Append(new LedgerEvent(Now.AddMinutes(2), "task-progress", "T-001"));
        w.Append(new LedgerEvent(Now.AddMinutes(3), "task-delivered", "T-001"));

        Assert.True(LedgerCommandsWrite.TryAccept(Snap(), new LedgerId(LedgerType.Task, 1, null), "operator", Now, out var ev, out _));
        Assert.Equal("task-accepted", ev!.Event);
        Assert.True(ev.Ungated);
    }

    [Fact]
    public void A_task_that_has_not_been_delivered_cannot_be_accepted()
    {
        WriteLedger("| F-001 | feature | | unrelated | planned | P0 | me | | |");
        Writer().Append(new LedgerEvent(Now, "task-assigned", "T-001", Owner: "huddle:backenddev", Title: "x"));
        Assert.False(LedgerCommandsWrite.TryAccept(Snap(), new LedgerId(LedgerType.Task, 1, null), "operator", Now, out _, out var why));
        Assert.Contains("assigned", why);
    }

    // ---- drop ----

    [Fact]
    public void Drop_requires_a_reason()
    {
        WriteLedger("| F-001 | feature | | Ledger phase 2 | planned | P0 | me | | |");
        foreach (var reason in new[] { "", "   ", null })
        {
            Assert.False(LedgerCommandsWrite.TryDrop(Snap(), Row("F-001").Id, reason!, "operator", Now, out _, out var why));
            // Dropping is how work stops existing; an unexplained drop is the audit trail
            // this design exists to keep.
            Assert.Contains("reason", why);
        }
    }

    [Fact]
    public void Drop_records_the_reason_as_the_note()
    {
        WriteLedger("| F-001 | feature | | Ledger phase 2 | planned | P0 | me | | |");
        Assert.True(LedgerCommandsWrite.TryDrop(Snap(), Row("F-001").Id, "superseded by phase 3", "operator", Now, out var ev, out _));
        Assert.Equal("state", ev!.Event);
        Assert.Equal("dropped", ev.To);
        Assert.Equal("planned", ev.From);
        Assert.Equal("superseded by phase 3", ev.Note);
    }

    [Fact]
    public void Something_already_accepted_cannot_be_dropped()
    {
        WriteLedger("| F-001 | feature | | Ledger phase 2 | accepted | P0 | me | | |");
        Assert.False(LedgerCommandsWrite.TryDrop(Snap(), Row("F-001").Id, "changed my mind", "operator", Now, out _, out var why));
        Assert.Contains("accepted", why);
    }

    [Fact]
    public void A_task_is_declined_not_dropped()
    {
        WriteLedger("| F-001 | feature | | unrelated | planned | P0 | me | | |");
        Writer().Append(new LedgerEvent(Now, "task-assigned", "T-001", Owner: "huddle:backenddev", Title: "x"));
        Assert.False(LedgerCommandsWrite.TryDrop(Snap(), new LedgerId(LedgerType.Task, 1, null), "no", "operator", Now, out _, out var why));
        Assert.Contains("decline", why);
    }

    // ---- decline ----

    [Fact]
    public void Decline_is_allowed_from_assigned_and_from_acked()
    {
        foreach (var extra in new[] { 0, 1 })
        {
            var d = new LedgerAcceptTests();
            try
            {
                d.WriteLedger("| F-001 | feature | | unrelated | planned | P0 | me | | |");
                var w = d.Writer();
                w.Append(new LedgerEvent(Now, "task-assigned", "T-001", Owner: "huddle:backenddev", Title: "x"));
                if (extra == 1) w.Append(new LedgerEvent(Now.AddMinutes(1), "task-acked", "T-001"));

                Assert.True(LedgerCommandsWrite.TryDecline(d.Snap(), new LedgerId(LedgerType.Task, 1, null),
                    "architect took the work", "huddle:backenddev", Now, out var ev, out _));
                // §6.2: declining is cheap and is recorded. A high decline rate is
                // information, not failure.
                Assert.Equal("task-declined", ev!.Event);
                Assert.Equal("architect took the work", ev.Note);
                Assert.Equal("huddle:backenddev", ev.Actor);
            }
            finally { d.Dispose(); }
        }
    }

    [Fact]
    public void Decline_without_a_note_is_allowed_because_the_point_is_that_it_is_cheap()
    {
        WriteLedger("| F-001 | feature | | unrelated | planned | P0 | me | | |");
        Writer().Append(new LedgerEvent(Now, "task-assigned", "T-001", Owner: "huddle:backenddev", Title: "x"));
        Assert.True(LedgerCommandsWrite.TryDecline(Snap(), new LedgerId(LedgerType.Task, 1, null), null, "huddle:backenddev", Now, out _, out _));
    }

    [Fact]
    public void Work_already_under_way_is_abandoned_rather_than_declined()
    {
        WriteLedger("| F-001 | feature | | unrelated | planned | P0 | me | | |");
        var w = Writer();
        w.Append(new LedgerEvent(Now, "task-assigned", "T-001", Owner: "huddle:backenddev", Title: "x"));
        w.Append(new LedgerEvent(Now.AddMinutes(1), "task-acked", "T-001"));
        w.Append(new LedgerEvent(Now.AddMinutes(2), "task-progress", "T-001"));
        Assert.False(LedgerCommandsWrite.TryDecline(Snap(), new LedgerId(LedgerType.Task, 1, null), null, "huddle:backenddev", Now, out _, out var why));
        Assert.Contains("in-progress", why);
    }

    [Fact]
    public void Only_a_task_can_be_declined()
    {
        WriteLedger("| F-001 | feature | | Ledger phase 2 | planned | P0 | me | | |");
        Assert.False(LedgerCommandsWrite.TryDecline(Snap(), Row("F-001").Id, null, "operator", Now, out _, out var why));
        Assert.Contains("drop", why);
    }

    // ---- nothing is deleted (§6.4) ----

    [Fact]
    public void A_dropped_row_is_still_in_the_ledger_with_its_reason()
    {
        WriteLedger("| F-001 | feature | | Ledger phase 2 | planned | P0 | me | | |");
        LedgerCommandsWrite.TryDrop(Snap(), Row("F-001").Id, "superseded", "operator", Now, out var ev, out _);
        Writer().Append(ev!);

        var s = Snap();
        Assert.Equal("dropped", s.Rows.Single().State);
        Assert.Contains(s.Events!, e => e.Note == "superseded");
        // and it renders, with its history, rather than vanishing
        var text = LedgerView.RenderOne(new[] { s }, new LedgerId(LedgerType.Feature, 1, null));
        Assert.Contains("dropped", text);
        Assert.Contains("superseded", text);
    }
}
