using Huddle;

namespace Huddle.Tests;

/// <summary>
/// Spec §5.5. A task still in <c>assigned</c> past <c>taskAckMinutes</c> escalates ONCE:
/// a mail to the dispatcher and a line to the operator's console. Once per task, never
/// repeated — this design adds a surface, not a nag.
///
/// <para>The agent whose dropped assignment prompted the work was explicit about why the
/// surface matters: <i>"I had the notification — it arrived and I did not act. What I
/// lacked was any surface that made the omission visible afterwards."</i></para>
/// </summary>
public class LedgerEscalationTests
{
    static readonly DateTimeOffset Now = new(2026, 8, 23, 14, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan After15 = TimeSpan.FromMinutes(15);

    static LedgerTask Task(int n, string state, int assignedMinutesAgo,
                           string owner = "myapp:backenddev", string actor = "myapp:architect") =>
        new(new LedgerId(LedgerType.Task, n, null), "WMA transcode server half", state, owner, actor,
            null, "P0", Array.Empty<string>(),
            Now.AddMinutes(-assignedMinutesAgo), Now.AddMinutes(-assignedMinutesAgo), null, false);

    static IReadOnlySet<LedgerId> None => new HashSet<LedgerId>();

    // ---- the clock ----

    [Fact]
    public void A_task_younger_than_the_threshold_is_not_due() =>
        Assert.Empty(LedgerEscalation.Due(new[] { Task(1, "assigned", 14) }, None, Now, After15));

    [Fact]
    public void A_task_exactly_at_the_threshold_is_due() =>
        Assert.Single(LedgerEscalation.Due(new[] { Task(1, "assigned", 15) }, None, Now, After15));

    [Fact]
    public void A_long_unacked_task_is_due() =>
        Assert.Single(LedgerEscalation.Due(new[] { Task(1, "assigned", 240) }, None, Now, After15));

    [Fact]
    public void The_threshold_is_whatever_was_configured()
    {
        var task = new[] { Task(1, "assigned", 30) };
        Assert.Empty(LedgerEscalation.Due(task, None, Now, TimeSpan.FromMinutes(60)));
        Assert.Single(LedgerEscalation.Due(task, None, Now, TimeSpan.FromMinutes(5)));
    }

    // ---- only unacknowledged work ----

    [Theory]
    [InlineData("acked")]
    [InlineData("in-progress")]
    [InlineData("delivered")]
    [InlineData("accepted")]
    [InlineData("declined")]
    [InlineData("abandoned")]
    public void A_task_that_has_been_acknowledged_or_finished_is_never_due(string state) =>
        Assert.Empty(LedgerEscalation.Due(new[] { Task(1, state, 240) }, None, Now, After15));

    [Fact]
    public void Acknowledgement_after_the_threshold_still_stops_the_escalation()
    {
        // The clock is on ACKNOWLEDGEMENT, not on completion: what is being surfaced is
        // an assignment nobody has even read.
        Assert.Empty(LedgerEscalation.Due(new[] { Task(1, "acked", 999) }, None, Now, After15));
    }

    // ---- once, never repeated ----

    [Fact]
    public void A_task_already_escalated_is_not_due_again()
    {
        var escalated = new HashSet<LedgerId> { new(LedgerType.Task, 1, null) };
        Assert.Empty(LedgerEscalation.Due(new[] { Task(1, "assigned", 240) }, escalated, Now, After15));
    }

    [Fact]
    public void Escalating_one_task_does_not_silence_another()
    {
        var escalated = new HashSet<LedgerId> { new(LedgerType.Task, 1, null) };
        var due = LedgerEscalation.Due(new[] { Task(1, "assigned", 240), Task(2, "assigned", 240) }, escalated, Now, After15);
        Assert.Equal(2, Assert.Single(due).Id.Number);
    }

    // ---- the escalated set is rebuilt from events, so it survives a restart ----

    [Fact]
    public void The_escalated_set_is_derived_from_the_event_log_not_from_memory()
    {
        // Held in memory, a restart would re-escalate every old assignment at once —
        // turning a surface into exactly the nag the spec rules out.
        var events = new[]
        {
            new LedgerEvent(Now.AddHours(-4), "task-assigned", "T-001"),
            LedgerEscalation.EscalationEvent(new LedgerId(LedgerType.Task, 1, null), Now.AddHours(-3)),
            new LedgerEvent(Now.AddHours(-4), "task-assigned", "T-002"),
        };
        var set = LedgerEscalation.AlreadyEscalated(events);
        Assert.Contains(new LedgerId(LedgerType.Task, 1, null), set);
        Assert.DoesNotContain(new LedgerId(LedgerType.Task, 2, null), set);
    }

    [Fact]
    public void An_escalation_marker_is_matched_by_value_however_the_id_was_spelled()
    {
        // L3 — ids are compared parsed, never as text. `T-7` and `T-007` are one task,
        // so a marker written either way must silence both.
        var events = new[] { LedgerEscalation.EscalationEvent(new LedgerId(LedgerType.Task, 7, null), Now) };
        var raw = events.Select(e => e with { Id = "huddle:T-7" }).ToArray();
        Assert.Contains(new LedgerId(LedgerType.Task, 7, null), LedgerEscalation.AlreadyEscalated(raw));
    }

    [Fact]
    public void An_ordinary_ref_added_is_not_an_escalation_marker()
    {
        var events = new[]
        {
            new LedgerEvent(Now, "ref-added", "T-001", Refs: new[] { "docs/plan.md" }),
            new LedgerEvent(Now, "ref-added", "T-002", Note: "see the spec"),
        };
        Assert.Empty(LedgerEscalation.AlreadyEscalated(events));
    }

    [Fact]
    public void The_marker_round_trips_through_the_writer()
    {
        var dir = Path.Combine(Path.GetTempPath(), "huddle-escalation-" + Guid.NewGuid().ToString("N"));
        try
        {
            var w = new LedgerWriter(Path.Combine(dir, "docs", "ledger"), _ => { }, createIfAbsent: true);
            var id = new LedgerId(LedgerType.Task, 3, null);
            w.Append(new LedgerEvent(Now.AddHours(-1), "task-assigned", "T-003"));
            w.Append(LedgerEscalation.EscalationEvent(id, Now));

            var problems = new List<string>();
            Assert.Contains(id, LedgerEscalation.AlreadyEscalated(w.ReadAll(problems)));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // The whole once-only guarantee, through real storage rather than through a set the
    // test hands itself: sweep, record, sweep again from a FRESH read of the log — which
    // is what a restarted orchestrator does.
    [Fact]
    public void A_second_sweep_after_a_restart_escalates_nothing_again()
    {
        var dir = Path.Combine(Path.GetTempPath(), "huddle-escalation-" + Guid.NewGuid().ToString("N"));
        try
        {
            var ledgerDir = Path.Combine(dir, "docs", "ledger");
            var w = new LedgerWriter(ledgerDir, _ => { }, createIfAbsent: true);
            w.Append(new LedgerEvent(Now.AddHours(-4), "task-assigned", "T-001",
                Actor: "myapp:architect", Owner: "myapp:backenddev", Title: "x"));
            w.Append(new LedgerEvent(Now.AddHours(-4), "task-assigned", "T-002",
                Actor: "myapp:architect", Owner: "myapp:backenddev", Title: "y"));

            IReadOnlyList<LedgerTask> Sweep(LedgerWriter writer)
            {
                var problems = new List<string>();
                var events = writer.ReadAll(problems);
                var due = LedgerEscalation.Due(
                    TaskMaterializer.Materialize(events, problems),
                    LedgerEscalation.AlreadyEscalated(events), Now, After15);
                foreach (var t in due) writer.Append(LedgerEscalation.EscalationEvent(t.Id, Now));
                return due;
            }

            Assert.Equal(2, Sweep(w).Count);
            Assert.Empty(Sweep(w));
            // A restart is a new writer over the same directory.
            Assert.Empty(Sweep(new LedgerWriter(ledgerDir, _ => { }, createIfAbsent: true)));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---- ordering ----

    [Fact]
    public void The_oldest_neglect_is_surfaced_first()
    {
        var due = LedgerEscalation.Due(
            new[] { Task(1, "assigned", 30), Task(2, "assigned", 300), Task(3, "assigned", 60) },
            None, Now, After15);
        Assert.Equal(new[] { 2, 3, 1 }, due.Select(t => t.Id.Number));
    }

    // ---- what the operator and the dispatcher actually see ----

    [Fact]
    public void The_console_line_names_the_task_both_agents_and_how_long_it_has_sat()
    {
        var line = LedgerEscalation.ConsoleLine("myapp", Task(107, "assigned", 32), Now);
        Assert.Contains("T-107", line);
        Assert.Contains("myapp:backenddev", line);
        Assert.Contains("myapp:architect", line);
        Assert.Contains("32m", line);
        Assert.Contains("unacked", line);
        Assert.DoesNotContain('\n', line);
    }

    [Fact]
    public void The_mail_goes_to_the_dispatcher_who_is_the_one_who_can_act()
    {
        var (to, subject, _) = LedgerEscalation.Mail("myapp", Task(107, "assigned", 32), Now);
        Assert.Equal("myapp:architect", to);
        Assert.Contains("T-107", subject);
        Assert.DoesNotContain('\n', subject);
    }

    [Fact]
    public void The_mail_says_what_was_asked_of_whom_and_for_how_long()
    {
        var (_, _, body) = LedgerEscalation.Mail("myapp", Task(107, "assigned", 32), Now);
        Assert.Contains("myapp:backenddev", body);
        Assert.Contains("WMA transcode server half", body);
        Assert.Contains("32m", body);
    }

    [Fact]
    public void A_task_with_no_dispatcher_recorded_is_surfaced_to_the_console_but_mails_nobody()
    {
        var orphaned = Task(9, "assigned", 60) with { Actor = null };
        Assert.Single(LedgerEscalation.Due(new[] { orphaned }, None, Now, After15));
        Assert.Null(LedgerEscalation.Mail("myapp", orphaned, Now).To);
    }

    [Fact]
    public void A_dispatcher_that_is_the_orchestrator_itself_is_not_mailed()
    {
        // Queue-dispatched units carry actor "_huddle"; mailing the orchestrator's own
        // command inbox would be read as a command, not a notice.
        var queued = Task(9, "assigned", 60) with { Actor = "_huddle" };
        Assert.Null(LedgerEscalation.Mail("myapp", queued, Now).To);
    }

    // ---- ages read the way a human would say them ----

    [Theory]
    [InlineData(32, "32m")]
    [InlineData(90, "1h")]
    [InlineData(60 * 26, "1d")]
    public void Age_is_rendered_coarsely(int minutesAgo, string expected) =>
        Assert.Contains(expected, LedgerEscalation.ConsoleLine("r", Task(1, "assigned", minutesAgo), Now));
}
