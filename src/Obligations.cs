namespace Huddle;

/// <summary>
/// What a session owes, read from the feature ledger — spec §5.6 and §5.7.
///
/// The ledger records obligations; these two surfaces are what make an ignored one
/// impossible to miss. The session that prompted this work reported "standing down,
/// nothing in flight, inbox clear" several times while holding four unread task
/// assignments, the oldest four days old. Huddle knew both facts and said nothing.
///
/// Neither surface blocks anything. A status line that can be falsified is worth more
/// than one that only repeats what the session says about itself.
/// </summary>
public sealed record Obligation(LedgerId Id, string Repo, string Title, string State, DateTimeOffset Since)
{
    public TimeSpan Age(DateTimeOffset now) => now - Since;
}

public static class Obligations
{
    /// <summary>
    /// Every open (non-terminal) task owned by <paramref name="instanceId"/>, across every
    /// repo that has a ledger, oldest first. Repos are (name, root) pairs — normally every
    /// registered repo, because a session can be assigned work in a repo that is not its own.
    /// Never throws: a repo whose ledger is missing or malformed contributes nothing, since
    /// a broken ledger must not stop a status line from rendering.
    /// </summary>
    public static IReadOnlyList<Obligation> For(
        string instanceId, IEnumerable<(string Name, string Root)> repos, DateTimeOffset now)
    {
        var found = new List<Obligation>();
        foreach (var (name, root) in repos)
        {
            try
            {
                var dir = Path.Combine(root, "docs", "ledger");
                if (!Directory.Exists(dir)) continue;
                var problems = new List<string>();
                foreach (var t in TaskMaterializer.Materialize(LedgerEventReader.ReadAll(dir, problems), problems))
                {
                    if (LedgerStateMachine.IsTerminal(t.State)) continue;
                    if (!string.Equals(t.Owner, instanceId, StringComparison.OrdinalIgnoreCase)) continue;
                    found.Add(new Obligation(t.Id, name, t.Title, t.State, t.AssignedAt));
                }
            }
            catch { /* a repo that cannot be read owes nothing it can prove */ }
        }
        return found.OrderBy(o => o.Since).ToList();
    }

    /// <summary>
    /// The `status` annotation (§5.6): "⚠ 4 open (oldest 4d)", or "" when nothing is owed.
    /// A session cannot look clean while it owes work.
    /// </summary>
    public static string StatusNote(IReadOnlyList<Obligation> open, DateTimeOffset now) =>
        open.Count == 0 ? "" : $"⚠ {open.Count} open (oldest {LedgerView.Age(open[0].Age(now))})";

    /// <summary>
    /// Live sessions that share one `repo:persona`, as "id (n sessions)" lines. Two
    /// sessions on one identity are invisible to each other in mail and — before the
    /// OwnerGuid fix — in the ledger too (I016). The spawn guard stops new ones; this is
    /// how a pair that ALREADY exists becomes visible instead of being discovered by a
    /// failed edit. Pass the live roster and the on-disk entries; anything alive in either
    /// that is not the same process counts.
    /// </summary>
    public static IReadOnlyList<string> DuplicateIdentities(
        IEnumerable<(string InstanceId, int Pid)> live)
    {
        return live
            .GroupBy(s => s.InstanceId, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(x => x.Pid).Distinct().Count() > 1)
            .Select(g => $"{g.Key}: {g.Select(x => x.Pid).Distinct().Count()} live sessions share this identity " +
                         $"(PIDs {string.Join(", ", g.Select(x => x.Pid).Distinct().OrderBy(p => p))}) — " +
                         "they cannot see each other's mail; stop one by PID")
            .ToList();
    }

    /// <summary>
    /// The spawn/resume context section (§5.7). Rendered fresh at every wake, so a dropped
    /// obligation has to survive being read every time the session starts. Empty string when
    /// nothing is owed — an agent with a clean slate is told nothing.
    /// </summary>
    public static string ContextSection(IReadOnlyList<Obligation> open, DateTimeOffset now)
    {
        if (open.Count == 0) return "";
        var lines = open.Select(o =>
            $"  - {o.Repo}:{o.Id}  {o.State,-12} assigned {LedgerView.Age(o.Age(now))} ago  {o.Title}");
        return "YOU OWE (open tasks from the feature ledger — oldest first):\n" +
               string.Join("\n", lines) +
               "\n\nAcknowledge, decline, or deliver each one. Do not report yourself idle while any of these is open.";
    }
}
