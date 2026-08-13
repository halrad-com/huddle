using System.Text.Json;

namespace Huddle;

/// <summary>
/// One discovered project (spec 2026-08-09-projects-artifacts-tasks-design.md).
/// Repo layer is standalone truth; the huddle map only annotates (or stands alone
/// as MapOnly when the project.md hasn't been written yet).
/// </summary>
public sealed record ProjectInfo(
    string Slug, string Title, string Goal, string Status,
    IReadOnlyList<string> Repos,
    string HomeRepo, string Dir,
    string? SprintId, string? SprintVersion,
    IReadOnlyList<string> TypedArtifacts,
    string? MapNotes, IReadOnlyList<string> MapLinks, bool MapOnly,
    string? Warning,
    DerivedSummary? Derived = null);

/// <summary>
/// Discovers docs/projects/&lt;slug&gt;/project.md across registered repos and merges
/// the projects-map.json overlay. Read-only; every failure degrades to a log line or
/// a Warning on the entry — never throws (a malformed project doc must not take the
/// listing down).
/// </summary>
public static class ProjectMap
{
    // The typed artifacts the spec names; presence is discovered, never enforced.
    private static readonly string[] TypedNames = { "ROADMAP.md", "BACKLOG.md", "SPRINT.md", "ISSUES.md" };

    /// <summary>
    /// Tolerant frontmatter parse: "---\nkey: value\n---" at the top of the text.
    /// No fences, unterminated fence, or no keys → empty dictionary. Never throws.
    /// </summary>
    public static Dictionary<string, string> ParseFrontmatter(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
            return result;

        var closed = false;
        var pending = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim() == "---") { closed = true; break; }
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (key.Length > 0)
                pending[key] = value;
        }
        return closed ? pending : result;
    }

    /// <summary>Split a "[a, b, c]" (or bare "a, b") frontmatter value into items.</summary>
    private static List<string> SplitList(string value) =>
        value.Trim().TrimStart('[').TrimEnd(']')
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    public static List<ProjectInfo> Discover(
        IEnumerable<(string Name, string Root)> repos, string? mapJson, Action<string> log)
    {
        var repoList = repos.ToList();
        var repoRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, root) in repoList) repoRoots[name] = root;

        var overlay = ParseOverlay(mapJson, log);
        var bySlug = new Dictionary<string, ProjectInfo>(StringComparer.OrdinalIgnoreCase);

        // Expand each registered repo to its git worktrees (main first) so a project
        // authored on a feature branch — which lives only in a linked worktree until it
        // merges — is discovered pre-merge and stays discovered after. Main-canonical:
        // the same slug found in this repo's OWN main + linked worktrees is deduped
        // silently (see the conflict branch below), only genuine cross-repo clashes warn.
        var expanded = repoList.SelectMany(
            r => GitWorktrees.ForRepo(r.Root).Select(wt => (r.Name, wt.Root)));

        foreach (var (repoName, root) in expanded)
        {
            var projectsDir = Path.Combine(root, "docs", "projects");
            IEnumerable<string> dirs;
            try
            {
                if (!Directory.Exists(projectsDir)) continue;
                dirs = Directory.EnumerateDirectories(projectsDir);
            }
            catch (Exception ex) { log($"projects: cannot scan {projectsDir}: {ex.Message}"); continue; }

            foreach (var dir in dirs)
            {
                var docPath = Path.Combine(dir, "project.md");
                if (!File.Exists(docPath)) continue;

                Dictionary<string, string> fm;
                try { fm = ParseFrontmatter(File.ReadAllText(docPath)); }
                catch (Exception ex) { log($"projects: cannot read {docPath}: {ex.Message}"); continue; }

                if (!fm.TryGetValue("slug", out var slug) || string.IsNullOrWhiteSpace(slug))
                {
                    log($"projects: {docPath} has no slug — skipped.");
                    continue;
                }

                if (bySlug.TryGetValue(slug, out var existing))
                {
                    // Same registered repo's own other worktree (same slug on main +
                    // a feature branch): main-canonical, deduped SILENTLY — not a clash.
                    if (string.Equals(existing.HomeRepo, repoName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    // Genuine cross-repo clash: first repo (registration order) wins, loudly.
                    bySlug[slug] = existing with
                    {
                        Warning = $"slug '{slug}' also declared by repo '{repoName}' ({docPath}) — using {existing.HomeRepo}'s"
                    };
                    continue;
                }

                var memberRepos = new List<string> { repoName };
                if (fm.TryGetValue("repos", out var reposVal))
                    foreach (var r in SplitList(reposVal))
                        if (!memberRepos.Contains(r, StringComparer.OrdinalIgnoreCase))
                            memberRepos.Add(r);

                var typed = TypedNames.Where(t => File.Exists(Path.Combine(dir, t))).ToList();

                string? sprintId = null, sprintVersion = null;
                if (typed.Contains("SPRINT.md"))
                {
                    try
                    {
                        var sfm = ParseFrontmatter(File.ReadAllText(Path.Combine(dir, "SPRINT.md")));
                        sfm.TryGetValue("sprint", out sprintId);
                        sfm.TryGetValue("version", out sprintVersion);
                    }
                    catch (Exception) { /* sprint id stays unknown */ }
                }

                bySlug[slug] = new ProjectInfo(
                    Slug: slug,
                    Title: fm.TryGetValue("title", out var t) ? t : slug,
                    Goal: fm.TryGetValue("goal", out var g) ? g : "",
                    Status: fm.TryGetValue("status", out var s) ? s : "",
                    Repos: memberRepos,
                    HomeRepo: repoName,
                    Dir: dir,
                    SprintId: string.IsNullOrWhiteSpace(sprintId) ? null : sprintId,
                    SprintVersion: string.IsNullOrWhiteSpace(sprintVersion) ? null : sprintVersion,
                    TypedArtifacts: typed,
                    MapNotes: null, MapLinks: Array.Empty<string>(), MapOnly: false,
                    Warning: null);
            }
        }

        // Overlay merge: annotate discovered projects and derive a summary from the
        // `source` pointer when given. A slug with a source but no project.md is a
        // DERIVED project (rich, not a bare map-only stub); a slug with neither stays
        // map-only (the doc hasn't been written yet — visible, not an error).
        foreach (var (slug, (notes, links, source, status)) in overlay)
        {
            var derived = string.IsNullOrWhiteSpace(source) ? null : ProjectDeriver.Derive(source!, repoRoots, log);

            if (bySlug.TryGetValue(slug, out var p))
                // Operator-set overlay status overrides where given; otherwise the repo doc's status stands.
                bySlug[slug] = p with
                {
                    MapNotes = notes, MapLinks = links, Derived = derived,
                    Status = string.IsNullOrWhiteSpace(status) ? p.Status : status!
                };
            else
                bySlug[slug] = new ProjectInfo(
                    Slug: slug, Title: slug, Goal: "", Status: status ?? "",
                    Repos: derived != null ? new[] { derived.Repo } : Array.Empty<string>(),
                    HomeRepo: derived?.Repo ?? "", Dir: "",
                    SprintId: null, SprintVersion: null,
                    TypedArtifacts: Array.Empty<string>(),
                    MapNotes: notes, MapLinks: links,
                    MapOnly: derived == null,       // a derived entry is not a bare stub
                    Warning: null, Derived: derived);
        }

        // Sort by status tier (operator's order): active on top, then research, then
        // released, then unknown, with EOL/legacy sinking to the bottom; slug breaks ties.
        return bySlug.Values
            .OrderBy(p => StatusRank(p.Status))
            .ThenBy(p => p.Slug, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Status-tier rank for sorting (lower = higher on the page). Matches on substrings so
    /// "EOL : Legacy", "Released 1.2", etc. all land in the right tier.
    /// </summary>
    public static int StatusRank(string? status)
    {
        var s = (status ?? "").Trim().ToLowerInvariant();
        if (s.Contains("eol") || s.Contains("legacy")) return 4;   // bottom
        if (s.StartsWith("active")) return 0;                       // top
        if (s.StartsWith("research")) return 1;
        if (s.StartsWith("release") || s.StartsWith("shipped")) return 2;
        return 3;                                                   // unknown/other: above EOL
    }

    private static Dictionary<string, (string? Notes, IReadOnlyList<string> Links, string? Source, string? Status)> ParseOverlay(
        string? mapJson, Action<string> log)
    {
        var result = new Dictionary<string, (string?, IReadOnlyList<string>, string?, string?)>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(mapJson))
            return result;
        try
        {
            using var doc = JsonDocument.Parse(mapJson);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                string? notes = null, source = null, status = null;
                var links = new List<string>();
                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    if (prop.Value.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String)
                        notes = n.GetString();
                    // `source` = "repo[/subpath][@branch]" — the pointer huddle derives from.
                    if (prop.Value.TryGetProperty("source", out var sc) && sc.ValueKind == JsonValueKind.String)
                        source = sc.GetString();
                    // `status` — operator-set project status (drives the pill + status-tier sort).
                    if (prop.Value.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String)
                        status = st.GetString();
                    if (prop.Value.TryGetProperty("links", out var l) && l.ValueKind == JsonValueKind.Array)
                        foreach (var item in l.EnumerateArray())
                            if (item.ValueKind == JsonValueKind.String)
                                links.Add(item.GetString()!);
                }
                result[prop.Name] = (notes, links, source, status);
            }
        }
        catch (Exception ex)
        {
            log($"projects: map overlay unparseable ({ex.Message}) — ignored; repo layer stands alone.");
        }
        return result;
    }
}
