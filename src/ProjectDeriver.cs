namespace Huddle;

/// <summary>
/// A project's status DERIVED from the corpora huddle already holds — no hand-authoring.
/// Null fields mean "couldn't derive that signal" (no README, not a git repo, …); the
/// card simply omits them.
/// </summary>
public sealed record DerivedSummary(
    string Repo,             // registered repo name the summary was read from
    string RepoRoot,         // resolved absolute dir
    string? Branch,          // branch read from (source @branch, else current)
    string? What,            // first meaningful README line
    string? LastCommit,      // subject of the most recent commit touching the source
    DateTime? LastCommitAt,
    int Commits30d,          // commits touching the source in the last 30 days
    int DocCount,            // total docs found under the source (README + docs/**)
    string? DocsDir,         // the docs/ folder, for a "browse all" link (null if none)
    IReadOnlyList<string> RecentDocs);  // newest docs (absolute paths), capped

/// <summary>
/// Turns a thin source pointer ("repo[/subpath][@branch]") into a <see cref="DerivedSummary"/>
/// by reading git + the README of the pointed-at location. The operator supplies the MAP
/// (slug → where), huddle supplies the SUBSTANCE. Parsing is pure and unit-tested; the
/// derivation shells git read-only via <see cref="GitHelper"/> and never throws — any
/// missing signal is just a null field.
/// </summary>
public static class ProjectDeriver
{
    /// <summary>
    /// Pure parse of a source pointer. Grammar: <c>repo[/sub/path][@branch]</c> — the
    /// first segment is a registered repo NAME, an optional subpath follows a slash, and
    /// an optional <c>@branch</c> selects that branch's worktree.
    /// </summary>
    public static (string Repo, string SubPath, string? Branch) ParseSource(string source)
    {
        var s = (source ?? "").Trim();
        string? branch = null;
        var at = s.IndexOf('@');
        if (at >= 0)
        {
            branch = s[(at + 1)..].Trim();
            s = s[..at];
            if (branch.Length == 0) branch = null;
        }
        var slash = s.IndexOf('/');
        if (slash < 0) return (s.Trim(), "", branch);
        return (s[..slash].Trim(), s[(slash + 1)..].Trim().Trim('/'), branch);
    }

    /// <summary>
    /// Resolve a source pointer against the registered repos and derive its summary, or
    /// null if the repo name is unknown. <paramref name="repoRoots"/> maps registered
    /// name → root (case-insensitive).
    /// </summary>
    public static DerivedSummary? Derive(
        string source, IReadOnlyDictionary<string, string> repoRoots, Action<string> log)
    {
        var (repo, sub, branch) = ParseSource(source);
        if (string.IsNullOrEmpty(repo) || !repoRoots.TryGetValue(repo, out var root))
        {
            log($"projects: source '{source}' names no registered repo — no summary derived.");
            return null;
        }

        // @branch selects that branch's worktree (e.g. rockalley -> the ROCKALLEY checkout).
        var baseDir = root;
        var resolvedBranch = branch;
        if (branch != null)
        {
            var wt = GitWorktrees.ForRepo(root)
                .FirstOrDefault(w => string.Equals(w.Branch, branch, StringComparison.OrdinalIgnoreCase));
            if (wt != null) baseDir = wt.Root;
            else log($"projects: source '{source}' — no worktree on branch '{branch}', using main.");
        }

        var dir = string.IsNullOrEmpty(sub) ? baseDir : Path.Combine(baseDir, sub);
        try { dir = Path.GetFullPath(dir); } catch { }

        var (subject, when) = GitHelper.LastCommitTouching(dir);
        var (docCount, docsDir, recentDocs) = CollectDocs(dir);
        return new DerivedSummary(
            Repo: repo,
            RepoRoot: dir,
            Branch: resolvedBranch ?? GitHelper.CurrentBranch(dir),
            What: ReadWhat(dir),
            LastCommit: subject,
            LastCommitAt: when,
            Commits30d: GitHelper.CommitsSince(dir, "30.days"),
            DocCount: docCount,
            DocsDir: docsDir,
            RecentDocs: recentDocs);
    }

    // The project's docs, for auto-linking: total count, the docs/ folder (browse-all
    // link), and the newest few (absolute paths, capped). "A lot of docs" is exactly why
    // we surface the folder + recent slice rather than hand-listing every file.
    private static (int count, string? docsDir, List<string> recent) CollectDocs(string dir)
    {
        var files = new List<string>();
        try
        {
            var readme = Path.Combine(dir, "README.md");
            if (File.Exists(readme)) files.Add(readme);
        }
        catch { }

        string? docsDir = null;
        try
        {
            var docs = Path.Combine(dir, "docs");
            if (Directory.Exists(docs))
            {
                docsDir = docs;
                try { files.AddRange(Directory.GetFiles(docs, "*.md", SearchOption.AllDirectories)); }
                catch { /* a subdir vanished mid-walk — keep whatever we collected */ }
            }
        }
        catch { }

        var recent = files
            .Select(f => (f, t: SafeMtime(f)))
            .OrderByDescending(x => x.t)
            .Select(x => x.f)
            .Take(8)
            .ToList();
        return (files.Count, docsDir, recent);
    }

    private static DateTime SafeMtime(string f)
    {
        try { return File.GetLastWriteTime(f); } catch { return DateTime.MinValue; }
    }

    // First line of the README worth showing: skip blanks, frontmatter, HTML comments,
    // setext underlines, and badge lines; prefer the first non-heading prose line but fall
    // back to the first heading's text (often the project's own name).
    private static string? ReadWhat(string dir)
    {
        foreach (var name in new[] { "README.md", "README.MD", "Readme.md", "readme.md", "README.txt", "README" })
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) continue;
            try
            {
                string? headingFallback = null;
                foreach (var raw in File.ReadLines(path).Take(60))
                {
                    var l = raw.Trim();
                    if (l.Length == 0) continue;
                    if (l is "---" || l.StartsWith("<!--", StringComparison.Ordinal)
                        || l.StartsWith("===", StringComparison.Ordinal)
                        || l.StartsWith("[![", StringComparison.Ordinal)) continue;
                    if (l.StartsWith("#", StringComparison.Ordinal))
                    {
                        headingFallback ??= l.TrimStart('#').Trim();
                        continue;
                    }
                    return Clip(l);
                }
                if (headingFallback != null) return Clip(headingFallback);
            }
            catch { /* unreadable — try the next candidate */ }
            break;   // found a README but nothing usable; don't keep probing other names
        }
        return null;
    }

    private static string Clip(string s) => s.Length > 200 ? s[..200].TrimEnd() + "…" : s;
}
