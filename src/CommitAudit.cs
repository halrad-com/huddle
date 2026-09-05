namespace Huddle;

/// <summary>
/// Post-hoc half of the claim gate. The PreToolUse hook (`huddle --claim-check`) guards
/// the Edit and Write TOOLS, so a file written any other way — sed, a python one-liner,
/// a shell redirect, a script — reaches the repo unchecked. That is not a bug in the
/// hook; a pre-tool guard can only see tool calls. This watches what actually LANDED.
///
/// What it reports is deliberately narrow and literally true: files that appear in a
/// commit which no session ever claimed. It does NOT say who committed them — sessions
/// share a worktree and huddle cannot attribute authorship, so naming a session would
/// be a guess dressed as a finding.
///
/// It warns and nothing else. It cannot block a commit that already happened, and it
/// must never be wired to anything that does.
/// </summary>
public static class CommitAudit
{
    /// <summary>The claims ledger's own normalisation (I008): separators and a leading
    /// "./" must not make one file look like two. Comparison is case-insensitive, which
    /// is right on Windows and the safe direction elsewhere — it can merge two files
    /// that differ only by case (silence), never invent an accusation (noise).</summary>
    public static string Norm(string f)
    {
        var p = (f ?? "").Trim().Replace('\\', '/');
        while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        return p;
    }

    /// <summary>A comparison set with the ledger's matching rules baked in.</summary>
    public static HashSet<string> ClaimedSet(IEnumerable<string> files)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
            if (!string.IsNullOrWhiteSpace(f)) set.Add(Norm(f));
        return set;
    }

    /// <summary>
    /// Files in <paramref name="changed"/> that <paramref name="claimed"/> does not
    /// cover, in first-seen order, deduplicated. Pure — the git call and the journal
    /// read happen in the caller, so the decision itself is testable.
    /// </summary>
    public static List<string> Unclaimed(IEnumerable<string> changed, ISet<string> claimed)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outp = new List<string>();
        foreach (var raw in changed)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var f = Norm(raw);
            if (claimed.Contains(f)) continue;
            if (!seen.Add(f)) continue;
            outp.Add(f);
        }
        return outp;
    }

    /// <summary>
    /// Root-aware overload — the one the orchestrator uses. Commit paths are relative to
    /// <paramref name="gitTop"/>; the index resolves them against the roots claims were
    /// recorded under, so a session working in a subdirectory checkout is not accused of
    /// touching files it correctly claimed.
    /// </summary>
    public static List<string> Unclaimed(IEnumerable<string> changed, string gitTop, ClaimedIndex claimed)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outp = new List<string>();
        foreach (var raw in changed)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var f = Norm(raw);
            if (claimed.Covers(gitTop, f)) continue;
            if (!seen.Add(f)) continue;
            outp.Add(f);
        }
        return outp;
    }

    /// <summary>One git repository and every registered name that points into it.</summary>
    public sealed record RepoGroup(string Top, string DisplayName, List<string> RepoNames);

    /// <summary>
    /// Collapse registered repos onto the git repository they actually live in. Two
    /// registered names can be one repo (a project directory inside a larger checkout),
    /// and auditing each separately reported one commit twice on the first live run.
    /// Grouping also unions their claims, which matters more: the claim may have been
    /// recorded under one name and the commit observed under the other.
    ///
    /// The display name prefers the repo whose root IS the git top — the honest label
    /// for a diff that spans the whole repository — and otherwise the first name given.
    /// </summary>
    public static List<RepoGroup> GroupByTop(IEnumerable<(string Name, string Root, string Top)> repos)
    {
        var order = new List<string>();
        var names = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var display = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, root, top) in repos)
        {
            if (string.IsNullOrWhiteSpace(top)) continue;
            var key = Norm(top).TrimEnd('/');

            if (!names.TryGetValue(key, out var list))
            {
                list = new List<string>();
                names[key] = list;
                display[key] = name;      // first name wins unless a root-at-top appears
                order.Add(key);
            }
            list.Add(name);

            if (string.Equals(Norm(root).TrimEnd('/'), key, StringComparison.OrdinalIgnoreCase))
                display[key] = name;
        }

        return order.Select(k => new RepoGroup(k, display[k], names[k])).ToList();
    }

    /// <summary>One line for the console, or null when the commit is fully covered.
    /// Truncated: a bulk commit must not push everything else off the screen.</summary>
    public static string? Describe(string repo, string sha, List<string> unclaimed, int max = 8)
    {
        if (unclaimed.Count == 0) return null;
        var shown = string.Join(", ", unclaimed.Take(max));
        var more = unclaimed.Count > max ? $" (+{unclaimed.Count - max} more)" : "";
        var sha7 = sha.Length > 7 ? sha[..7] : sha;
        return $"Unclaimed commit: {repo} {sha7} touched {shown}{more} — no claim on record";
    }
}
