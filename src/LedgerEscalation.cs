namespace Huddle;

/// <summary>
/// Spec §5.5. A task still in <c>assigned</c> past <c>taskAckMinutes</c> escalates ONCE:
/// a mail to the dispatcher and a line to the operator's console. Once per task, never
/// repeated — this is a surface, not a nag.
///
/// <para>The agent whose dropped assignment prompted the whole design said exactly what
/// was missing: <i>"I had the notification — it arrived and I did not act. What I lacked
/// was any surface that made the omission visible afterwards."</i> Escalation is that
/// surface, and it points at the DISPATCHER as well as the operator, because the agent
/// who has not read its mail is by definition not the one who will notice.</para>
///
/// <para>Pure. The orchestrator supplies the tasks and the clock and does the sending, so
/// the rules are testable without a running huddle.</para>
/// </summary>
public static class LedgerEscalation
{
    /// <summary>
    /// Marker note on the <c>ref-added</c> event that records an escalation. Kept in the
    /// LOG rather than in memory so "already escalated" survives a restart — held in
    /// memory, a restart would re-escalate every old assignment at once and turn the
    /// surface into the nag the spec rules out.
    /// </summary>
    public const string Marker = "escalated";

    /// <summary>
    /// Tasks whose assignment has gone unacknowledged past <paramref name="ackAfter"/>
    /// and that have not been escalated already, oldest neglect first.
    ///
    /// <para>The clock is on ACKNOWLEDGEMENT, not on completion. What is being surfaced
    /// is an assignment nobody has even read; a task someone acked and is slow to finish
    /// is a different problem and not this one.</para>
    /// </summary>
    public static IReadOnlyList<LedgerTask> Due(
        IEnumerable<LedgerTask> tasks, IReadOnlySet<LedgerId> alreadyEscalated,
        DateTimeOffset now, TimeSpan ackAfter) =>
        tasks
            .Where(t => t.State.Equals("assigned", StringComparison.OrdinalIgnoreCase))
            .Where(t => now - t.AssignedAt >= ackAfter)
            .Where(t => !alreadyEscalated.Contains(t.Id with { Repo = null }))
            .OrderBy(t => t.AssignedAt)
            .ToList();

    /// <summary>
    /// The set of tasks already escalated, rebuilt from the event log. Ids are compared
    /// PARSED, never as text (L3): a marker written as <c>T-7</c> or <c>huddle:T-007</c>
    /// silences <c>T-007</c>, because they are one task.
    /// </summary>
    public static IReadOnlySet<LedgerId> AlreadyEscalated(IEnumerable<LedgerEvent> events)
    {
        var set = new HashSet<LedgerId>();
        foreach (var e in events)
        {
            if (e.Event != "ref-added" || e.Note != Marker) continue;
            if (LedgerId.TryParse(e.Id, out var id) && id.Type == LedgerType.Task)
                set.Add(id with { Repo = null });
        }
        return set;
    }

    /// <summary>The event that records an escalation so it is never repeated.</summary>
    public static LedgerEvent EscalationEvent(LedgerId id, DateTimeOffset now) =>
        new(now, "ref-added", (id with { Repo = null }).ToString(), Actor: "_huddle", Note: Marker);

    /// <summary>The operator's one line. Names the task, both agents, and the age.</summary>
    public static string ConsoleLine(string repo, LedgerTask t, DateTimeOffset now) =>
        $"[ledger] {repo}:{t.Id} assigned to {t.Owner ?? "?"} by {t.Actor ?? "?"} unacked for {LedgerView.Age(now - t.AssignedAt)}";

    /// <summary>
    /// The notice for the dispatcher — the one who asked for the work and can decide
    /// whether to chase it, reassign it or drop it.
    ///
    /// <para><c>To</c> is null when there is nobody to tell: an unrecorded dispatcher, or
    /// the orchestrator itself. A queue-dispatched unit carries actor <c>_huddle</c>, and
    /// mail to the orchestrator's own inbox is read as a COMMAND, not as a notice — it
    /// would be nacked as an unknown subject at best. Those still reach the operator
    /// through <see cref="ConsoleLine"/>.</para>
    /// </summary>
    public static (string? To, string Subject, string Body) Mail(string repo, LedgerTask t, DateTimeOffset now)
    {
        var age = LedgerView.Age(now - t.AssignedAt);
        var to = string.IsNullOrWhiteSpace(t.Actor) || t.Actor == "_huddle" ? null : t.Actor;
        var subject = $"{repo}:{t.Id} has been unacknowledged for {age} — {t.Title}";
        var body =
            $"You assigned {repo}:{t.Id} to {t.Owner ?? "(nobody recorded)"} and it has sat in `assigned` for {age}. " +
            $"That means the assignment has not been READ, not merely that it is unfinished.\n\n" +
            $"Task: {t.Title}\n" +
            $"Assigned: {t.AssignedAt:yyyy-MM-dd HH:mm} UTC\n" +
            (t.Refs.Count > 0 ? $"Refs: {string.Join(" ", t.Refs)}\n" : "") +
            $"\nThis is sent once and never repeated. Chase it, reassign it, or drop it — " +
            $"`ledger decline {t.Id} <note>` if it should not have been asked for.";
        return (to, subject, body);
    }
}
