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
    string? Warning);

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
        var overlay = ParseOverlay(mapJson, log);
        var bySlug = new Dictionary<string, ProjectInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var (repoName, root) in repos)
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
                    // Conflict: first repo (registration order) wins, loudly.
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

        // Overlay merge: annotate discovered projects; unknown slugs become MapOnly
        // entries (the doc hasn't been written yet — visible, not an error).
        foreach (var (slug, (notes, links)) in overlay)
        {
            if (bySlug.TryGetValue(slug, out var p))
                bySlug[slug] = p with { MapNotes = notes, MapLinks = links };
            else
                bySlug[slug] = new ProjectInfo(slug, slug, "", "",
                    Array.Empty<string>(), "", "", null, null,
                    Array.Empty<string>(), notes, links, MapOnly: true, Warning: null);
        }

        return bySlug.Values.OrderBy(p => p.Slug, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Dictionary<string, (string? Notes, IReadOnlyList<string> Links)> ParseOverlay(
        string? mapJson, Action<string> log)
    {
        var result = new Dictionary<string, (string?, IReadOnlyList<string>)>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(mapJson))
            return result;
        try
        {
            using var doc = JsonDocument.Parse(mapJson);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                string? notes = null;
                var links = new List<string>();
                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    if (prop.Value.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String)
                        notes = n.GetString();
                    if (prop.Value.TryGetProperty("links", out var l) && l.ValueKind == JsonValueKind.Array)
                        foreach (var item in l.EnumerateArray())
                            if (item.ValueKind == JsonValueKind.String)
                                links.Add(item.GetString()!);
                }
                result[prop.Name] = (notes, links);
            }
        }
        catch (Exception ex)
        {
            log($"projects: map overlay unparseable ({ex.Message}) — ignored; repo layer stands alone.");
        }
        return result;
    }
}
