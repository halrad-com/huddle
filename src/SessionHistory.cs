using System.Text.Json;

namespace Huddle;

/// <summary>
/// One row of the `history` listing: a past (or current) Claude session derived
/// entirely from its transcript — no manifest, no agent discipline required.
/// </summary>
public sealed record SessionSummary(
    string Id,              // session GUID (resume token)
    string Title,           // aiTitle > opening prompt > "(untitled)"
    string Repo,            // registered repo name, or shortened raw cwd if unmatched
    string Cwd,             // session working directory
    DateTime? StartedAt,    // first timestamped line (local)
    DateTime? LastActivity, // last timestamped line (local; falls back to file mtime)
    int FileCount,          // distinct files touched via Write/Edit/NotebookEdit
    string OpeningPrompt,   // first user message, one line, truncated
    string TranscriptPath);

/// <summary>Detail view: the summary plus where it left off and what it wrote.</summary>
public sealed record SessionDetail(
    SessionSummary Summary,
    string LastPrompt,                  // most recent last-prompt (or last user text)
    IReadOnlyList<string> Files);       // absolute paths; still-existing files first

public sealed record HistoryFilter(string? Repo, string? Keyword, DateTime? Cutoff);

/// <summary>
/// Reads Claude Code transcripts (~/.claude/projects/&lt;encoded-cwd&gt;/&lt;sessionId&gt;.jsonl)
/// and derives session summaries/details. Top-level sessions only (subagent
/// transcripts live in subdirectories and are deliberately not enumerated).
/// Malformed lines and unreadable files are skipped — never fatal.
/// </summary>
public class TranscriptStore
{
    // Newest-first parse cap: bounds a `history` call on a machine with years of
    // transcripts. Filters apply after parse, so a very narrow filter over a very
    // old session can miss — the listing footer makes the cap visible.
    public const int MaxScan = 100;

    private readonly string _projectsRoot;
    private readonly IReadOnlyDictionary<string, string> _repoRoots; // name -> root
    private readonly Action<string> _log;

    public TranscriptStore(string projectsRoot, IReadOnlyDictionary<string, string> repoRoots, Action<string>? log = null)
    {
        _projectsRoot = projectsRoot;
        _repoRoots = repoRoots;
        _log = log ?? (_ => { });
    }

    /// <summary>True when the last ListSessions hit the MaxScan cap (older transcripts unscanned).</summary>
    public bool LastListTruncated { get; private set; }

    public IReadOnlyList<SessionSummary> ListSessions(HistoryFilter filter)
    {
        LastListTruncated = false;
        var results = new List<SessionSummary>();
        foreach (var path in EnumerateTranscriptsNewestFirst())
        {
            if (results.Count >= MaxScan) { LastListTruncated = true; break; }
            var detail = ParseTranscript(path);
            if (detail == null) continue;
            var s = detail.Summary;

            if (filter.Repo != null && !string.Equals(s.Repo, filter.Repo, StringComparison.OrdinalIgnoreCase))
                continue;
            if (filter.Cutoff.HasValue && (s.LastActivity ?? DateTime.MinValue) < filter.Cutoff.Value)
                continue;
            if (!string.IsNullOrEmpty(filter.Keyword) && !Matches(s, filter.Keyword))
                continue;

            results.Add(s);
        }
        return results
            .OrderByDescending(s => s.LastActivity ?? DateTime.MinValue)
            .ToList();
    }

    public SessionDetail? GetDetail(string sessionId)
    {
        foreach (var path in EnumerateTranscriptsNewestFirst())
        {
            if (!Path.GetFileNameWithoutExtension(path).Equals(sessionId, StringComparison.OrdinalIgnoreCase))
                continue;
            return ParseTranscript(path);
        }
        return null;
    }

    /// <summary>Transcript file paths, newest mtime first — for content scans (find verb).</summary>
    public IEnumerable<string> TranscriptPaths() => EnumerateTranscriptsNewestFirst();

    /// <summary>Parse one transcript by path. Find verb: parse cost is paid per HIT, not per scan.</summary>
    public SessionDetail? ParsePath(string path) => ParseTranscript(path);

    private static bool Matches(SessionSummary s, string k) =>
        s.Title.Contains(k, StringComparison.OrdinalIgnoreCase) ||
        s.OpeningPrompt.Contains(k, StringComparison.OrdinalIgnoreCase) ||
        s.Repo.Contains(k, StringComparison.OrdinalIgnoreCase) ||
        s.Id.Contains(k, StringComparison.OrdinalIgnoreCase);

    // Top-level *.jsonl per project dir, newest mtime first across all projects.
    private IEnumerable<string> EnumerateTranscriptsNewestFirst()
    {
        if (!Directory.Exists(_projectsRoot)) yield break;
        var files = new List<(string path, DateTime mtime)>();
        foreach (var dir in SafeGetDirectories(_projectsRoot))
            foreach (var f in SafeGetFiles(dir))
                files.Add((f, SafeMtime(f)));
        foreach (var (path, _) in files.OrderByDescending(f => f.mtime))
            yield return path;
    }

    private string[] SafeGetDirectories(string root)
    {
        try { return Directory.GetDirectories(root); }
        catch (Exception ex) { _log($"history: skip {root}: {ex.Message}"); return Array.Empty<string>(); }
    }

    private string[] SafeGetFiles(string dir)
    {
        try { return Directory.GetFiles(dir, "*.jsonl", SearchOption.TopDirectoryOnly); }
        catch (Exception ex) { _log($"history: skip {dir}: {ex.Message}"); return Array.Empty<string>(); }
    }

    private static DateTime SafeMtime(string path)
    {
        try { return File.GetLastWriteTime(path); }
        catch { return DateTime.MinValue; }
    }

    // Single pass over the transcript: title, cwd, timestamps, opening prompt,
    // last prompt, and every Write/Edit/NotebookEdit file_path.
    private SessionDetail? ParseTranscript(string path)
    {
        string? title = null, cwd = null, opening = null, lastPrompt = null, lastUserText = null;
        DateTime? first = null, last = null;
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // FileShare.ReadWrite: live sessions keep the file open for append.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length < 2) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;

                    if (root.TryGetProperty("timestamp", out var tsEl) && tsEl.ValueKind == JsonValueKind.String &&
                        DateTime.TryParse(tsEl.GetString(), null,
                            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                            out var ts))
                    {
                        var local = ts.ToLocalTime();
                        first ??= local;
                        last = local;
                    }

                    if (cwd == null && root.TryGetProperty("cwd", out var cwdEl) && cwdEl.ValueKind == JsonValueKind.String)
                        cwd = cwdEl.GetString();

                    var type = root.TryGetProperty("type", out var tEl) && tEl.ValueKind == JsonValueKind.String
                        ? tEl.GetString() : null;

                    switch (type)
                    {
                        case "ai-title":
                            title = StringProp(root, "aiTitle") ?? StringProp(root, "title") ?? title;
                            break;
                        case "last-prompt":
                            lastPrompt = StringProp(root, "lastPrompt") ?? StringProp(root, "prompt") ?? lastPrompt;
                            break;
                        case "user":
                            var text = ExtractUserText(root);
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                opening ??= text;
                                lastUserText = text;
                            }
                            break;
                        case "assistant":
                            CollectWrites(root, files, seen);
                            break;
                    }
                }
                catch (JsonException) { /* malformed / partially-written line: skip */ }
            }
        }
        catch (Exception ex)
        {
            _log($"history: skip {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }

        var id = Path.GetFileNameWithoutExtension(path);
        var displayTitle = FirstNonEmpty(title, opening, "(untitled)");
        var summary = new SessionSummary(
            Id: id,
            Title: OneLine(displayTitle, 70),
            Repo: MatchRepo(cwd),
            Cwd: cwd ?? "",
            StartedAt: first,
            LastActivity: last ?? SafeMtime(path),
            FileCount: files.Count,
            OpeningPrompt: OneLine(opening ?? "", 160),
            TranscriptPath: path);

        // Existing-on-disk files first — those are the ones `open` can actually open.
        var ordered = files
            .OrderByDescending(File.Exists)
            .ToList();

        return new SessionDetail(summary, OneLine(FirstNonEmpty(lastPrompt, lastUserText, ""), 160), ordered);
    }

    private static void CollectWrites(JsonElement root, List<string> files, HashSet<string> seen)
    {
        if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) return;
        if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (StringProp(item, "type") != "tool_use") continue;
            var name = StringProp(item, "name");
            if (name is not ("Write" or "Edit" or "NotebookEdit")) continue;
            if (!item.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object) continue;
            var fp = StringProp(input, "file_path") ?? StringProp(input, "notebook_path");
            if (!string.IsNullOrWhiteSpace(fp) && seen.Add(fp))
                files.Add(fp);
        }
    }

    private static string? ExtractUserText(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) return null;
        if (!msg.TryGetProperty("content", out var content)) return null;
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (StringProp(item, "type") == "text")
                {
                    var t = StringProp(item, "text");
                    if (!string.IsNullOrWhiteSpace(t)) return t;
                }
            }
        }
        return null;
    }

    // Longest-root wins so nested repo roots (e.g. a repo inside a workspace repo)
    // attribute to the most specific registration.
    private string MatchRepo(string? cwd)
    {
        if (string.IsNullOrEmpty(cwd)) return "?";
        var norm = Norm(cwd);
        string? best = null;
        var bestLen = -1;
        foreach (var (name, root) in _repoRoots)
        {
            var r = Norm(root);
            if (r.Length > bestLen &&
                (norm.Equals(r, StringComparison.OrdinalIgnoreCase) ||
                 norm.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                best = name;
                bestLen = r.Length;
            }
        }
        if (best != null) return best;
        // Unregistered cwd still lists, labeled by its tail.
        var parts = norm.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? "…" + parts[^1] : norm;
    }

    private static string Norm(string p)
    {
        try { return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return p; }
    }

    private static string? StringProp(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static string FirstNonEmpty(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? "";

    private static string OneLine(string s, int max)
    {
        var flat = s.Replace("\r", " ").Replace("\n", " ").Trim();
        while (flat.Contains("  ")) flat = flat.Replace("  ", " ");
        return flat.Length <= max ? flat : flat[..(max - 1)] + "…";
    }
}
