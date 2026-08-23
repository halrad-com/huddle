namespace Huddle;

/// <summary>
/// Spec §1.2. Forward-only; `dropped` / `declined` / `abandoned` are terminal and are
/// never removals. `accepts` is load-bearing (§1.3): a Deliverable may not enter
/// `accepted` while its gate is unnamed. Huddle does not run the gate — it refuses to
/// record acceptance without one.
/// </summary>
public static partial class LedgerStateMachine
{
    public static readonly string[] HierarchyStates = { "ideated", "decided", "planned", "dispatched", "delivered", "accepted", "dropped" };
    public static readonly string[] TaskStates = { "assigned", "acked", "in-progress", "delivered", "accepted", "declined", "abandoned" };

    static readonly string[] HierarchyChain = { "ideated", "decided", "planned", "dispatched", "delivered", "accepted" };
    static readonly string[] TaskChain = { "assigned", "acked", "in-progress", "delivered", "accepted" };

    public static bool IsHierarchyState(string s) => HierarchyStates.Contains(s, StringComparer.OrdinalIgnoreCase);
    public static bool IsTaskState(string s) => TaskStates.Contains(s, StringComparer.OrdinalIgnoreCase);
    public static bool IsTerminal(string s) => s.ToLowerInvariant() is "accepted" or "dropped" or "declined" or "abandoned";

    public static bool CanTransitionHierarchy(string from, string to)
    {
        from = from.ToLowerInvariant(); to = to.ToLowerInvariant();
        if (!IsHierarchyState(from) || !IsHierarchyState(to) || from == to) return false;
        if (IsTerminal(from)) return false;
        if (to == "dropped") return true;
        var i = Array.IndexOf(HierarchyChain, from);
        var j = Array.IndexOf(HierarchyChain, to);
        return i >= 0 && j == i + 1;
    }

    /// <summary>
    /// Forward-only, but a forward JUMP is legal. An agent that does the work and reports
    /// `task-complete` never sent an ack or a progress line, and refusing that would
    /// recreate the "unknown task" nack for work that really happened — the exact failure
    /// §5.2 exists to remove. Skipping a state loses no information and invents none;
    /// synthesising the states it skipped would invent history, which is worse.
    ///
    /// <para><c>accepted</c> is the exception and must come from <c>delivered</c>.
    /// Acceptance is a deliberate act on work that has actually been handed over, and
    /// letting it jump the queue would hollow out the one gate this design is built
    /// around.</para>
    /// </summary>
    public static bool CanTransitionTask(string from, string to)
    {
        from = from.ToLowerInvariant(); to = to.ToLowerInvariant();
        if (!IsTaskState(from) || !IsTaskState(to) || from == to) return false;
        if (IsTerminal(from)) return false;
        // Never asked for and never started -> declined. Started and stopped -> abandoned.
        // Two different words for two different things; neither is a removal.
        if (to == "declined") return from is "assigned" or "acked";
        if (to == "abandoned") return from is "acked" or "in-progress";
        if (to == "accepted") return from == "delivered";
        var i = Array.IndexOf(TaskChain, from);
        var j = Array.IndexOf(TaskChain, to);
        return i >= 0 && j > i;
    }

    public static bool CanAccept(LedgerRow row, out string why)
    {
        why = "";
        if (!row.State.Equals("delivered", StringComparison.OrdinalIgnoreCase))
        { why = $"{row.Id} is {row.State}; only a delivered item can be accepted"; return false; }
        if (row.Type == LedgerType.Deliverable && string.IsNullOrWhiteSpace(row.Accepts))
        { why = $"{row.Id} has no accepts gate — name the test, capture suite, replay or commit that proves it"; return false; }
        return true;
    }
}
