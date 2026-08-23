using System.Text;

namespace Huddle;

/// <summary>
/// One repo's ledger as read. <c>Events</c> is that repo's OWN event log and travels with
/// the snapshot, so a renderer can never attribute one repo's history to another (L2).
/// </summary>
public sealed record LedgerRepoSnapshot(
    string Repo, string Dir, IReadOnlyList<LedgerRow> Rows, IReadOnlyList<LedgerTask> Tasks,
    IReadOnlyList<LedgerRowError> RowErrors, IReadOnlyList<string> Problems, bool Present,
    IReadOnlyList<LedgerEvent>? Events = null,
    string? DeclaredRepo = null);

public static class LedgerView
{
    public const string LedgerSubdir = "docs/ledger";

    public static LedgerRepoSnapshot Load(string repoName, string repoRoot)
    {
        var dir = Path.GetFullPath(Path.Combine(repoRoot, LedgerSubdir));
        if (!Directory.Exists(dir)) return new(repoName, dir, [], [], [], [], false);
        var problems = new List<string>();
        var md = Path.Combine(dir, "ledger.md");
        var parsed = File.Exists(md) ? FeatureLedgerParser.Parse(File.ReadAllText(md)) : new LedgerParseResult();
        var events = LedgerEventReader.ReadAll(dir, problems);
        var tasks = TaskMaterializer.Materialize(events, problems);
        var rows = ApplyStateEvents(parsed.Rows, events, problems);
        parsed.Frontmatter.TryGetValue("repo", out var declared);
        return new(repoName, dir, rows, tasks, parsed.Errors, problems, true, events,
            string.IsNullOrWhiteSpace(declared) ? null : declared.Trim());
    }

    /// <summary>An event's id as a bare, normalised <see cref="LedgerId"/>, or null when
    /// it does not parse. Ids are compared by value everywhere, never as text (L3).</summary>
    static LedgerId? IdOf(LedgerEvent e) =>
        LedgerId.TryParse(e.Id, out var id) ? id with { Repo = null } : null;

    /// <summary>
    /// Hierarchy state is an OVERLAY: ledger.md's State column is the baseline and
    /// <c>state</c> events win.
    ///
    /// <para>The hierarchy lives in ledger.md because it changes rarely and benefits from
    /// review in a diff — and huddle never rewrites that file, both because it is the
    /// operator's and because a machine rewriting a table humans also edit is how a file
    /// gets clobbered. So `ledger accept` and `ledger drop` cannot edit the row; they
    /// append an event, and every reader applies the latest one on top.</para>
    ///
    /// <para>Applied in TIMESTAMP order, not file order: a backdated event appended after
    /// a later one must not win just because it was written last. An event naming a row
    /// that is not in ledger.md is REPORTED, never dropped.</para>
    /// </summary>
    public static IReadOnlyList<LedgerRow> ApplyStateEvents(
        IReadOnlyList<LedgerRow> rows, IReadOnlyList<LedgerEvent> events, List<string> problems)
    {
        var latest = new Dictionary<LedgerId, LedgerEvent>();
        foreach (var e in events.Where(e => e.Event == "state").OrderBy(e => e.Ts))
        {
            if (IdOf(e) is not { } id) { problems.Add($"state event with unparseable id \"{e.Id}\""); continue; }
            if (id.Type == LedgerType.Task)
            { problems.Add($"{id}: `state` is for hierarchy rows; a task's state is replayed from its own events"); continue; }
            latest[id] = e;
        }
        if (latest.Count == 0) return rows;

        var byId = rows.ToDictionary(r => r.Id);
        var result = rows.Select(r =>
            latest.TryGetValue(r.Id, out var e) && !string.IsNullOrWhiteSpace(e.To)
                ? r with { State = e.To!.ToLowerInvariant() }
                : r).ToList();

        foreach (var id in latest.Keys.Where(k => !byId.ContainsKey(k)))
            problems.Add($"{id}: state event for a row that is not in ledger.md");

        return result;
    }

    /// <summary>True when a parent pointer names a row in a DIFFERENT repo (L1). A bare
    /// parent, or one qualified with this repo's own name, is local.</summary>
    static bool IsForeign(LedgerId? parent, string repo) =>
        parent is { } p && p.Repo != null && !p.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="parent"/> is a local reference to <paramref name="bare"/>.</summary>
    static bool PointsAt(LedgerId? parent, LedgerId bare, string repo) =>
        parent is { } p && !IsForeign(p, repo) && (p with { Repo = null }) == bare;

    /// <summary>What `ledger` prints when the working directory belongs to no configured repo.</summary>
    public const string NoCurrentLedger = "ledger: no ledger for the current directory; use --repo <name>";

    /// <summary>
    /// The configured repo whose root contains <paramref name="cwd"/>, or null. Matching is
    /// by path containment, not by a repo literally named "huddle" (L4), and a sibling whose
    /// name merely starts with a root's name (…\rbextra vs …\app) is not a match. The deepest
    /// root wins, so nested checkouts resolve to the inner one.
    /// </summary>
    public static string? RepoForDirectory(IEnumerable<(string Name, string Root)> repos, string cwd)
    {
        var here = Normalize(cwd);
        if (here.Length == 0) return null;
        return repos
            .Where(r => r.Root is { Length: > 0 })
            .Select(r => (r.Name, Root: Normalize(r.Root)))
            .Where(r => r.Root.Length > 0 && (
                here.Equals(r.Root, StringComparison.OrdinalIgnoreCase) ||
                here.StartsWith(r.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(r => r.Root.Length)
            .Select(r => r.Name)
            .FirstOrDefault();
    }

    static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return path.Trim().TrimEnd('\\', '/'); }
    }

    /// <summary>
    /// The snapshots `ledger` / `ledger all` should render: the explicit --repo scope when
    /// given, otherwise the repo the working directory is in. EMPTY when neither applies —
    /// the caller prints <see cref="NoCurrentLedger"/> rather than blank lines (L4).
    /// </summary>
    public static IReadOnlyList<LedgerRepoSnapshot> CurrentSnapshots(
        IEnumerable<LedgerRepoSnapshot> snaps, IEnumerable<(string Name, string Root)> repos,
        string cwd, string? repoFilter)
    {
        var all = snaps.ToList();
        if (repoFilter != null) return all;
        var name = RepoForDirectory(repos, cwd);
        if (name is null) return Array.Empty<LedgerRepoSnapshot>();
        return all.Where(s => s.Repo.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Non-null when a ledger.md's frontmatter `repo:` disagrees with the repo it was read
    /// from — the signature of a ledger.md copied between repos. Absent frontmatter is not
    /// an error: the key is optional (L4).
    /// </summary>
    public static string? DeclaredRepoWarning(LedgerRepoSnapshot s) =>
        s.DeclaredRepo is { Length: > 0 } d && !d.Equals(s.Repo, StringComparison.OrdinalIgnoreCase)
            ? $"ledger.md declares repo: {d} but was read from {s.Repo} — copied between repos?"
            : null;

    public sealed record OpenItem(string Repo, string Id, LedgerType Type, string Title, string State, string? Owner, DateTimeOffset? Since, TimeSpan? Age);

    public static IReadOnlyList<OpenItem> OpenByAge(IEnumerable<LedgerRepoSnapshot> snaps, DateTimeOffset now)
    {
        var items = new List<OpenItem>();
        foreach (var s in snaps)
        {
            foreach (var r in s.Rows.Where(r => !LedgerStateMachine.IsTerminal(r.State)))
                items.Add(new(s.Repo, r.Id.ToString(), r.Type, r.Title, r.State, r.Owner, null, null));
            foreach (var t in s.Tasks.Where(t => !LedgerStateMachine.IsTerminal(t.State)))
                items.Add(new(s.Repo, t.Id.ToString(), LedgerType.Task, t.Title, t.State, t.Owner, t.AssignedAt, now - t.AssignedAt));
        }
        return items
            .OrderBy(i => i.Since.HasValue ? 0 : 1)
            .ThenBy(i => i.Since)
            .ThenBy(i => i.Repo, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string Age(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d" : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h" : $"{(int)t.TotalMinutes}m";

    public static string RenderOpenByAge(IReadOnlyList<OpenItem> items)
    {
        var sb = new StringBuilder();
        if (items.Count == 0) { sb.AppendLine("  nothing open."); return sb.ToString(); }
        sb.AppendLine($"  {"age",-6}{"id",-22}{"state",-13}{"owner",-26}title");
        foreach (var i in items)
            sb.AppendLine($"  {(i.Age.HasValue ? Age(i.Age.Value) : "-"),-6}{i.Repo + ":" + i.Id,-22}{i.State,-13}{i.Owner ?? "-",-26}{i.Title}");
        return sb.ToString();
    }

    public static string RenderOrphans(IEnumerable<LedgerRepoSnapshot> snaps)
    {
        var sb = new StringBuilder();
        var any = false;
        foreach (var s in snaps)
            foreach (var t in s.Tasks.Where(t => t.Parent is null && !LedgerStateMachine.IsTerminal(t.State)))
            { any = true; sb.AppendLine($"  {s.Repo}:{t.Id}  {t.State,-12} {t.Owner ?? "-",-26} {t.Title}"); }
        if (!any) sb.AppendLine("  no orphan tasks — every open task has a parent.");
        return sb.ToString();
    }

    public static string RenderTree(LedgerRepoSnapshot s, bool includeClosed)
    {
        var sb = new StringBuilder();
        foreach (var e in s.RowErrors) sb.AppendLine($"  ! line {e.Line}: {e.Reason}");
        foreach (var p in s.Problems) sb.AppendLine($"  ! {p}");
        if (!s.Present) { sb.AppendLine($"  {s.Repo}: no docs/ledger/"); return sb.ToString(); }

        var rows = s.Rows.Where(r => includeClosed || !LedgerStateMachine.IsTerminal(r.State)).ToList();
        var tasks = s.Tasks.Where(t => includeClosed || !LedgerStateMachine.IsTerminal(t.State)).ToList();
        var known = rows.Select(r => r.Id).ToHashSet();

        // L1: a parent qualified with ANOTHER repo names a row in that repo's ledger.
        // Stripping the qualifier and matching it locally silently reparented
        // cross-repo work under an unrelated same-numbered local row. Foreign-parented
        // rows are pulled out here and rendered under a visible stub, so they read as
        // "child of something elsewhere" rather than as a root or an orphan.
        bool Foreign(LedgerId? parent) => IsForeign(parent, s.Repo);
        LedgerId? LocalKey(LedgerId? parent)
        {
            if (parent is not { } p || Foreign(p)) return null;
            var bare = p with { Repo = null };
            return known.Contains(bare) ? bare : (LedgerId?)null;
        }

        var childRows = rows.Where(r => !Foreign(r.Parent)).ToLookup(r => LocalKey(r.Parent));
        var childTasks = tasks.Where(t => !Foreign(t.Parent)).ToLookup(t => LocalKey(t.Parent));

        string RowLine(LedgerRow r, string pad) =>
            $"{pad}{r.Id}  {r.State,-11} {r.Pri ?? "  ",-3} {r.Title}{(r.Owner != null ? "  — " + r.Owner : "")}";
        string TaskLine(LedgerTask t, string pad) =>
            $"{pad}{t.Id}  {t.State,-11} {t.Pri ?? "  ",-3} {t.Title}{(t.Owner != null ? "  — " + t.Owner : "")}";

        void Emit(LedgerId? parent, int depth)
        {
            var pad = new string(' ', 2 + depth * 2);
            foreach (var r in childRows[parent].OrderBy(r => r.Id.Type).ThenBy(r => r.Id.Number))
            {
                sb.AppendLine(RowLine(r, pad));
                Emit(r.Id, depth + 1);
            }
            foreach (var t in childTasks[parent].OrderBy(t => t.Id.Number))
                sb.AppendLine(TaskLine(t, pad));
        }
        Emit(null, 0);

        var foreignParents = rows.Where(r => Foreign(r.Parent)).Select(r => r.Parent!.Value)
            .Concat(tasks.Where(t => Foreign(t.Parent)).Select(t => t.Parent!.Value))
            .Distinct()
            .OrderBy(p => p.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var fp in foreignParents)
        {
            sb.AppendLine($"  ^ {fp}  (cross-repo parent)");
            foreach (var r in rows.Where(r => r.Parent is { } p && p == fp).OrderBy(r => r.Id.Type).ThenBy(r => r.Id.Number))
            {
                sb.AppendLine(RowLine(r, "    "));
                Emit(r.Id, 2);
            }
            foreach (var t in tasks.Where(t => t.Parent is { } p && p == fp).OrderBy(t => t.Id.Number))
                sb.AppendLine(TaskLine(t, "    "));
        }

        if (rows.Count == 0 && tasks.Count == 0) sb.AppendLine("  (empty)");
        return sb.ToString();
    }

    /// <summary>
    /// One item per repo that holds it. Each section's event history comes from THAT
    /// repo's snapshot — never a union across repos filtered by bare id, which printed
    /// one repo's transitions under another's same-numbered row (L2).
    /// </summary>
    public static string RenderOne(IEnumerable<LedgerRepoSnapshot> snaps, LedgerId id)
    {
        var sb = new StringBuilder();
        foreach (var s in snaps)
        {
            if (id.Repo != null && !id.Repo.Equals(s.Repo, StringComparison.OrdinalIgnoreCase)) continue;
            var bare = id with { Repo = null };
            var row = s.Rows.FirstOrDefault(r => r.Id == bare);
            var task = s.Tasks.FirstOrDefault(t => t.Id == bare);
            if (row is null && task is null) continue;

            sb.AppendLine($"  {s.Repo}:{bare}");
            if (row != null)
            {
                sb.AppendLine($"    {row.Type}  {row.State}  pri={row.Pri ?? "-"}  owner={row.Owner ?? "-"}  accepts={row.Accepts ?? "-"}");
                sb.AppendLine($"    {row.Title}");
                foreach (var r in row.Refs) sb.AppendLine($"    ref  {r}");
                // Ancestry. A qualified parent belongs to another repo's ledger: name it
                // and stop, rather than resolving it against a same-numbered local row
                // and inventing an ancestry that does not exist (L1).
                var p = row.Parent; int guard = 0;
                while (p is { } pid && guard++ < 10)
                {
                    if (IsForeign(pid, s.Repo)) { sb.AppendLine($"    ^ {pid}  (not in this repo)"); break; }
                    var pr = s.Rows.FirstOrDefault(r => r.Id == (pid with { Repo = null }));
                    sb.AppendLine($"    ^ {pid}  {(pr != null ? pr.Title : "(not in this ledger)")}");
                    p = pr?.Parent;
                }
                foreach (var c in s.Rows.Where(r => PointsAt(r.Parent, bare, s.Repo)))
                    sb.AppendLine($"    v {c.Id}  {c.State,-11} {c.Title}");
                foreach (var t in s.Tasks.Where(t => PointsAt(t.Parent, bare, s.Repo)))
                    sb.AppendLine($"    v {t.Id}  {t.State,-11} {t.Title}  — {t.Owner ?? "-"}");
            }
            if (task != null)
            {
                sb.AppendLine($"    task  {task.State}  owner={task.Owner ?? "-"}  by={task.Actor ?? "-"}  parent={(task.Parent?.ToString() ?? "(orphan)")}  assigned {task.AssignedAt:yyyy-MM-dd HH:mm}");
                sb.AppendLine($"    {task.Title}");
                foreach (var r in task.Refs) sb.AppendLine($"    ref  {r}");
            }
            // Match the event's id by VALUE, not by text (L3) — an event written as
            // `T-7` belongs to `T-007` and must appear in its history.
            foreach (var ev in (s.Events ?? Array.Empty<LedgerEvent>()).Where(e => IdOf(e) == bare).OrderBy(e => e.Ts))
                sb.AppendLine($"    {ev.Ts:yyyy-MM-dd HH:mm}  {ev.Event,-16} {ev.Actor ?? "-"}{(ev.Note != null ? "  " + ev.Note : "")}{(ev.From != null ? $"  {ev.From} -> {ev.To}" : "")}");
        }
        if (sb.Length == 0) sb.AppendLine($"  {id}: not found in any ledger");
        return sb.ToString();
    }
}
