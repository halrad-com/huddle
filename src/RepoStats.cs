using System.Globalization;

namespace Huddle;

public sealed record Movement(DateTimeOffset Ts, string Remote, string Branch, string Verb, string Sha, string? Identity);

/// <summary>Remote-tracking reflogs are git's own record and survive huddle being down —
/// this is what lets `stats` answer for the past week on day one.</summary>
public static class ReflogHistory
{
    public static IReadOnlyList<Movement> Read(string repoRoot, DateTimeOffset since)
    {
        var common = GitHelper.GitCommonDir(repoRoot);
        if (common == null) return Array.Empty<Movement>();
        var remotesDir = Path.Combine(common, "logs", "refs", "remotes");
        if (!Directory.Exists(remotesDir)) return Array.Empty<Movement>();
        var ids = RemoteIdentity.ForRepo(repoRoot);
        var all = new List<Movement>();
        foreach (var f in Directory.GetFiles(remotesDir, "*", SearchOption.AllDirectories))
        {
            // origin/HEAD is a symbolic pointer, not a transfer.
            if (Path.GetFileName(f) == "HEAD") continue;
            string text;
            try { text = File.ReadAllText(f); } catch { continue; }
            all.AddRange(Parse(remotesDir, f, text, since, ids));
        }
        return all.OrderBy(m => m.Ts).ToList();
    }

    public static IReadOnlyList<Movement> Parse(string remotesDir, string file, string text, DateTimeOffset since, IReadOnlyDictionary<string, string> identities)
    {
        var reference = Path.GetRelativePath(remotesDir, file).Replace('\\', '/');
        var (remote, branch) = GitActivityMonitor.SplitReference(reference);
        identities.TryGetValue(remote, out var identity);
        var list = new List<Movement>();
        foreach (var raw in text.Split('\n'))
        {
            var e = GitActivityLog.ParseMovement("", reference, raw, identity);
            if (e == null || e.Ts < since) continue;
            list.Add(new Movement(e.Ts, remote, branch, e.Verb!, e.Sha!, identity));
        }
        return list;
    }
}

public sealed record CommitStats(int Commits, int Unpushed, int Added, int Deleted, DateTimeOffset? Last, IReadOnlyList<DateTimeOffset> CommitTimes);

public static class GitLogStats
{
    /// <summary>Null when the root is not a git repo — `matrixapp` and `ledapp` are
    /// registered repos that are not git checkouts, and that is a note, never an error.</summary>
    public static CommitStats? Collect(string repoRoot, DateTimeOffset since)
    {
        if (GitHelper.GitCommonDir(repoRoot) == null) return null;
        var (ok, log, _) = GitHelper.RunRaw(repoRoot, $"log --since=\"{since.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}\" --pretty=%at --numstat");
        if (!ok) return null;
        // No upstream configured is normal, not a failure — it just means nothing is unpushed.
        var (rok, count, _) = GitHelper.RunRaw(repoRoot, "rev-list --count @{upstream}..HEAD");
        return ParseNumstat(log, rok ? count.Trim() : null);
    }

    /// <summary>
    /// Commit times only, for the heatmap's year-wide pass. Deliberately NOT
    /// <see cref="Collect"/>: `--numstat` walks every diff and costs ~39s on a repo the
    /// size of ReferenceCode, against ~0.03s without it, and the graph needs nothing but
    /// the timestamps. Empty for a non-repo.
    /// </summary>
    public static IReadOnlyList<DateTimeOffset> CommitTimesSince(string repoRoot, DateTimeOffset since)
    {
        if (GitHelper.GitCommonDir(repoRoot) == null) return Array.Empty<DateTimeOffset>();
        var (ok, log, _) = GitHelper.RunRaw(repoRoot, $"log --since=\"{since.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}\" --pretty=%at");
        if (!ok) return Array.Empty<DateTimeOffset>();
        var times = new List<DateTimeOffset>();
        foreach (var raw in log.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0 && long.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out var unix))
                times.Add(DateTimeOffset.FromUnixTimeSeconds(unix));
        }
        return times;
    }

    public static int DirtyFiles(string repoRoot) => GitHelper.StatusDirty(repoRoot).Count;

    /// <summary>`git log --pretty=%at --numstat` output: a bare unix timestamp starts each
    /// commit, then tab-separated "added deleted path" rows ("-" for binary). Pure — unit-tested.</summary>
    public static CommitStats ParseNumstat(string log, string? revListCount)
    {
        int commits = 0, added = 0, deleted = 0;
        var times = new List<DateTimeOffset>();
        foreach (var raw in log.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (long.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out var unix) && !line.Contains('\t'))
            { commits++; times.Add(DateTimeOffset.FromUnixTimeSeconds(unix)); continue; }
            var p = line.Split('\t');
            if (p.Length >= 2)
            {
                if (int.TryParse(p[0], out var a)) added += a;
                if (int.TryParse(p[1], out var d)) deleted += d;
            }
        }
        int.TryParse(revListCount ?? "", out var unpushed);
        return new CommitStats(commits, unpushed, added, deleted, times.Count > 0 ? times.Max() : null, times);
    }
}

public enum AttributionGrade { Exact, Inferred }
public sealed record Attribution(string Instance, AttributionGrade Grade, IReadOnlyList<string> Evidence);

public sealed record RosterWindow(string Instance, string Repo, DateTimeOffset Start, DateTimeOffset? End)
{
    public bool Covers(DateTimeOffset t) => t >= Start && (End is null || t <= End);
    public static RosterWindow From(SessionStateEntry e) =>
        new(e.InstanceId, e.RepoName, new DateTimeOffset(e.StartedAt), e.DiedAt is { } d ? new DateTimeOffset(d) : null);
}

/// <summary>
/// Every agent commits as the same git identity, so a reflog or a commit can never say
/// who. Exact = a signal that names the instance; inferred = roster overlap, rendered as a
/// list of candidates, never collapsed to one.
/// </summary>
public static class Attributor
{
    public static IReadOnlyList<Attribution> ForRepo(string repo,
        IReadOnlyList<RosterWindow> roster, IReadOnlyList<GitActivityEntry> activity,
        IReadOnlyList<Movement> movements, IReadOnlyList<WorkLedgerClaim> claims, IReadOnlyList<WorkUnit> units)
    {
        var exact = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var inferred = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        void Add(Dictionary<string, List<string>> d, string inst, string ev)
        { if (!d.TryGetValue(inst, out var l)) d[inst] = l = new(); l.Add(ev); }

        var prefix = repo + ":";
        var mine = roster.Where(w => w.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var a in activity.Where(a => a.Kind == "auth" && a.Instance != null && a.Instance.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            Add(exact, a.Instance!, $"cred {a.Host} {a.Ts:MM-dd HH:mm}");
        foreach (var c in claims.Where(c => c.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase)))
            Add(exact, c.SessionId, $"claim {c.BatchId}");
        foreach (var u in units.Where(u => u.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase)))
            Add(exact, $"{u.Repo}:{u.Persona}", $"unit {u.Id}");

        // A movement names nobody. One live session in that root at that instant is the
        // only case where the fleet roster narrows it to a single agent; two live
        // sessions stay two candidates and are labelled inferred, never collapsed.
        foreach (var m in movements)
        {
            var live = mine.Where(w => w.Covers(m.Ts)).ToList();
            if (live.Count == 1) Add(exact, live[0].Instance, $"{m.Verb} {m.Sha} sole live session");
            else foreach (var w in live) Add(inferred, w.Instance, $"live at {m.Verb} {m.Sha}");
        }

        var result = new List<Attribution>();
        foreach (var (inst, ev) in exact) result.Add(new(inst, AttributionGrade.Exact, ev));
        foreach (var (inst, ev) in inferred)
            if (!exact.ContainsKey(inst)) result.Add(new(inst, AttributionGrade.Inferred, ev));
        return result.OrderBy(r => r.Grade).ThenBy(r => r.Instance, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

public sealed record StatsSources(
    IReadOnlyList<RosterWindow> Roster, IReadOnlyList<GitActivityEntry> Activity,
    IReadOnlyList<WorkLedgerClaim> Claims, IReadOnlyList<WorkUnit> Units,
    Func<string, int> MailCountForRepo, IReadOnlyList<HandoffEntry> Handoffs);

public sealed record RepoStatsSnapshot(
    string Repo, string Root, bool IsGit, IReadOnlyDictionary<string, string> Remotes,
    IReadOnlyList<Movement> Movements, CommitStats? Commits, int Dirty,
    IReadOnlyList<Attribution> Who, int Sessions, double SessionHours, TimeSpan? IdleGap,
    int Units, int Mail, int Handoffs, int OpenClaims, IReadOnlyList<string> Health);

public static class RepoStatsCollector
{
    public static RepoStatsSnapshot Collect(string repo, string root, DateTimeOffset since, DateTimeOffset now, StatsSources src)
    {
        var isGit = GitHelper.GitCommonDir(root) != null;
        var remotes = isGit ? RemoteIdentity.ForRepo(root) : new Dictionary<string, string>();
        var moves = isGit ? ReflogHistory.Read(root, since) : Array.Empty<Movement>();
        var commits = isGit ? GitLogStats.Collect(root, since) : null;
        var dirty = isGit ? GitLogStats.DirtyFiles(root) : 0;

        var mine = src.Roster.Where(w => w.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase) && (w.End is null || w.End >= since)).ToList();
        // A session that started before the window contributes only the part inside it.
        var hours = mine.Sum(w => ((w.End ?? now) - (w.Start < since ? since : w.Start)).TotalHours);
        var lastActivity = new[] { moves.Select(m => (DateTimeOffset?)m.Ts).DefaultIfEmpty().Max(), commits?.Last }.Where(t => t.HasValue).Select(t => t!.Value).DefaultIfEmpty().Max();
        TimeSpan? idle = lastActivity == default ? null : now - lastActivity;

        var who = Attributor.ForRepo(repo, src.Roster, src.Activity.Where(a => a.Ts >= since).ToList(), moves, src.Claims, src.Units);
        var units = src.Units.Count(u => u.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase));
        var handoffs = src.Handoffs.Count(h => h.To.StartsWith(repo + ":", StringComparison.OrdinalIgnoreCase) || h.From.StartsWith(repo + ":", StringComparison.OrdinalIgnoreCase));
        var openClaims = src.Claims.Count(c => c.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase));

        // A long-running session with nothing attributable is the shape of a stuck agent.
        var health = new List<string>();
        foreach (var w in mine.Where(w => w.End is null && (now - w.Start).TotalHours > 48))
            if (!who.Any(a => a.Instance.Equals(w.Instance, StringComparison.OrdinalIgnoreCase) && a.Grade == AttributionGrade.Exact && a.Evidence.Any(e => e.Contains("push") || e.Contains("sole live"))))
                health.Add($"{w.Instance} running {(int)(now - w.Start).TotalHours}h with no attributable commit");

        return new(repo, root, isGit, remotes, moves, commits, dirty, who, mine.Count, Math.Round(hours, 1), idle,
            units, src.MailCountForRepo(repo), handoffs, openClaims, health);
    }
}
