namespace Huddle;

/// <summary>
/// Spec §5.3 — keep the queue's states, stop the lie.
///
/// <para><c>WorkQueue</c> sets <c>Done</c> when a session stops having committed. That is
/// a reasonable signal and it is NOT changed here; what changes is what it is called in
/// the ledger. A unit reaching Done appends <c>task-delivered</c> and never
/// <c>task-accepted</c>, so "delivered" and "accepted" become different words — which
/// they were not before, and which is why all 13 persisted units read Done including the
/// AutoCal unit the operator later found broken.</para>
///
/// <para>Acceptance stays a separate, deliberate act: <c>ledger accept &lt;id&gt;</c>,
/// which refuses when the parent Deliverable has no named gate.</para>
/// </summary>
public static class WorkQueueLedger
{
    /// <summary>The dedup key for a queue unit — the same idea as keying mail on its
    /// file, so a re-dispatch or a restart re-finds the row instead of opening a second.</summary>
    public static string RefFor(string unitId) => "unit:" + unitId;

    /// <summary>
    /// Open the task for a unit the queue has just started. Idempotent: a unit that is
    /// re-dispatched keeps its original row rather than acquiring a second obligation for
    /// the same work.
    /// </summary>
    public static void OnDispatched(LedgerWriter writer, WorkUnit u, DateTimeOffset now, Action<string> log)
    {
        var reference = RefFor(u.Id);
        if (writer.TryFindTaskByRef(reference, out _)) return;

        var owner = $"{u.Repo}:{u.Persona}";
        var id = writer.AppendNewTask(newId => new LedgerEvent(
            now, "task-assigned", newId.ToString(),
            Actor: "_huddle",                       // the orchestrator asked, not a person
            Owner: owner,
            Parent: ParentFor(u.Ledger, u.Repo),
            Title: Title(u.Prompt),
            Refs: new[] { reference }));

        if (id is { } opened) log($"ledger: {opened} assigned to {owner} for unit {u.Id}");
    }

    /// <summary>
    /// Record what became of a unit's task once the queue has settled it.
    ///
    /// <list type="bullet">
    /// <item><c>Done</c> → <c>task-delivered</c>. Never accepted.</item>
    /// <item><c>Failed</c> after the agent acknowledged → <c>task-abandoned</c>: someone
    /// tried and it stopped.</item>
    /// <item><c>Failed</c> before any acknowledgement → <c>task-declined</c>: never read,
    /// never started. Calling that "abandoned" would claim an attempt that did not happen.</item>
    /// </list>
    ///
    /// <para>A non-terminal queue state settles nothing, and a task already settled is
    /// left alone — the queue re-walks its units on every advance.</para>
    /// </summary>
    public static void OnSettled(
        LedgerWriter writer, WorkUnit u, QueueState state, string? note, DateTimeOffset now, Action<string> log)
    {
        if (state is not (QueueState.Done or QueueState.Failed)) return;
        if (!writer.TryFindTaskByRef(RefFor(u.Id), out var id)) return;

        var problems = new List<string>();
        var task = TaskMaterializer.Materialize(writer.ReadAll(problems), problems).FirstOrDefault(t => t.Id == id);
        if (task is null) return;

        var to = state == QueueState.Done
            ? "delivered"
            : task.State.Equals("assigned", StringComparison.OrdinalIgnoreCase) ? "declined" : "abandoned";

        if (!LedgerStateMachine.CanTransitionTask(task.State, to)) return;

        writer.Append(new LedgerEvent(now, "task-" + to, id.ToString(),
            Actor: "_huddle", Note: string.IsNullOrWhiteSpace(note) ? null : note.Trim()));
        log($"ledger: {id} {to} — unit {u.Id}");
    }

    static string? ParentFor(string? raw, string repo) =>
        LedgerId.TryParse(raw, out var p) && p.Type != LedgerType.Task ? p.Qualify(repo).ToString() : null;

    static string Title(string? prompt)
    {
        var s = (prompt ?? "").Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (s.Length == 0) return "(no prompt)";
        return s.Length <= LedgerMailIngest.MaxTitle ? s : s[..LedgerMailIngest.MaxTitle];
    }
}
