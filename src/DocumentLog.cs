using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Huddle;

/// <summary>
/// Document-log levels. The name IS the filter token and the badge — no opaque codes.
///   Output — human deliverables (specs, designs, reports, READMEs); declared by agents.
///            This is what plain `docs` shows by default.
///   Plans  — planning docs; declared by agents.
///   Churn  — git working-tree changes (source included); derived on demand, the noisy tier.
/// Order matters: a filter shows its level and everything below it.
/// </summary>
public enum DocLevel
{
    Output = 0,
    Plans = 1,
    Churn = 2
}

/// <summary>
/// One entry in the document log. Path is resolved and directly openable.
/// Timestamp is the newest-first sort key (null sorts last).
/// </summary>
public record DocumentEntry(
    string Title,
    string Path,
    string SourceSession,
    string Repo,
    DateTime? Timestamp,
    DocLevel Level,
    string? Note);

/// <summary>
/// Seam between the `docs` verb and the data. Scratchpad/git-backed today;
/// a virtual-storage implementation can replace it without touching the verb.
/// maxLevel bounds what is returned: asking for Output returns only Output;
/// asking for Churn returns Output+Plans+Churn.
/// </summary>
public interface IDocumentSource
{
    IReadOnlyList<DocumentEntry> GetDocuments(DocLevel maxLevel);
}

/// <summary>
/// Seam between the `open` verb and the act of opening. OS-shell today;
/// swappable for a virtual-storage opener later. Returns false on failure
/// (already logged via the supplied logger).
/// </summary>
public interface IDocumentOpener
{
    bool Open(string path, Action<string> log);
}

/// <summary>OS file-handler opener — same pattern as ConsoleUI.HandleShell.</summary>
public sealed class ShellDocumentOpener : IDocumentOpener
{
    public bool Open(string path, Action<string> log)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            log($"open: failed — {ex.Message}");
            return false;
        }
    }
}

/// <summary>
/// Parses the `## Documents` section of every logs/&lt;session&gt;/scratchpad.md and
/// returns Output / Plans entries. Reads scratchpad files DIRECTLY
/// (not via git), so gitignored artifacts still appear as long as the agent declared
/// them. This is the file-backed implementation, so filesystem access here is expected;
/// the seam is what keeps the VERB filesystem-free.
///
/// Line grammar inside the section:  - [Title](path) — optional note #output|#plans
///   * [Title](path) required; path is repo-relative or absolute.
///   * trailing "— note" optional (em dash or hyphen).
///   * #output / #plans level tag optional; if absent, level is inferred from the path
///     (under a /plans/ directory -> Plans, else Output). Legacy #docs/#p0/#p1 still recognized.
/// </summary>
public sealed class ScratchpadDocumentSource : IDocumentSource
{
    private readonly string _dataDir;
    private readonly IReadOnlyDictionary<string, SessionDefinition> _repos;
    private readonly Action<string> _log;

    // [Title](path)
    private static readonly Regex LinkRx = new(@"\[(?<title>.+?)\]\((?<path>.+?)\)", RegexOptions.Compiled);
    // Level tag: #output / #plans / #churn (the level names) or legacy #docs / #p0 / #p1 / #p2.
    private static readonly Regex TagRx =
        new(@"#(?<tok>output|docs|plans|churn|p[0-2])\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public ScratchpadDocumentSource(string dataDir, IReadOnlyDictionary<string, SessionDefinition> repos, Action<string> log)
    {
        _dataDir = dataDir;
        _repos = repos;
        _log = log;
    }

    public IReadOnlyList<DocumentEntry> GetDocuments(DocLevel maxLevel)
    {
        var results = new List<DocumentEntry>();
        if (!Directory.Exists(_dataDir)) return results;

        var scratchpads = Directory.GetFiles(_dataDir, "scratchpad.md", SearchOption.AllDirectories);
        var parsed = 0;
        foreach (var scratchpad in scratchpads)
        {
            string[] lines;
            try { lines = File.ReadAllLines(scratchpad); }
            catch { continue; }

            // Session safe-name is the immediate parent directory name (e.g. "huddle_architect-2").
            var safeName = new DirectoryInfo(Path.GetDirectoryName(scratchpad)!).Name;
            var (repoName, sessionDisplay) = SplitSafeName(safeName);
            var repoRoot = _repos.TryGetValue(repoName, out var def) ? def.Root : null;

            DateTime scratchMtime;
            try { scratchMtime = File.GetLastWriteTime(scratchpad); } catch { scratchMtime = DateTime.MinValue; }

            foreach (var (title, rawPath, note, level) in ParseDocumentsSection(lines))
            {
                if (level > maxLevel) continue;          // e.g. maxLevel==Output drops Plans
                if (level == DocLevel.Churn) continue;    // scratchpad source never yields Churn

                var resolved = ResolvePath(rawPath, repoRoot);
                DateTime? ts;
                try { ts = File.Exists(resolved) ? File.GetLastWriteTime(resolved) : scratchMtime; }
                catch { ts = scratchMtime; }

                results.Add(new DocumentEntry(title, resolved, sessionDisplay, repoName, ts, level, note));
            }
            parsed++;
        }

        _log($"docs: parsed {parsed} scratchpad(s), {results.Count} declared entr(y/ies) at <= {maxLevel}");
        return results;
    }

    /// <summary>Yields (title, path, note, level) for each bullet in the `## Documents` section.</summary>
    private static IEnumerable<(string title, string path, string? note, DocLevel level)> ParseDocumentsSection(string[] lines)
    {
        var inSection = false;
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("## Documents", StringComparison.OrdinalIgnoreCase))
            {
                inSection = true;
                continue;
            }
            if (!inSection) continue;
            if (trimmed.StartsWith("##")) yield break;          // next heading ends the section
            if (!trimmed.StartsWith("- ")) continue;

            var link = LinkRx.Match(trimmed);
            if (!link.Success) continue;                         // malformed bullet -> skip

            var title = link.Groups["title"].Value.Trim();
            var path = link.Groups["path"].Value.Trim();
            var remainder = trimmed[(link.Index + link.Length)..];

            // Level: explicit tag wins, else infer from path.
            DocLevel level;
            var tag = TagRx.Match(remainder);
            if (tag.Success)
                level = TokenToLevel(tag.Groups["tok"].Value);
            else
                level = InferLevel(path);

            // Note: remainder with tags stripped, leading em dash / hyphen / space removed.
            var note = TagRx.Replace(remainder, "").Trim().TrimStart('—', '-', ' ').Trim();
            yield return (title, path, string.IsNullOrWhiteSpace(note) ? null : note, level);
        }
    }

    private static DocLevel InferLevel(string path)
    {
        var p = path.Replace('\\', '/');
        return p.Contains("/plans/", StringComparison.OrdinalIgnoreCase) ? DocLevel.Plans : DocLevel.Output;
    }

    // Map a level tag token (#output/#plans/#churn or legacy #docs/#p0/#p1/#p2) to its level.
    private static DocLevel TokenToLevel(string tok) => tok.ToLowerInvariant() switch
    {
        "plans" or "p1" => DocLevel.Plans,
        "churn" or "p2" => DocLevel.Churn,
        _ => DocLevel.Output,   // "output" / "docs" / "p0" / anything else
    };

    private static string ResolvePath(string rawPath, string? repoRoot)
    {
        if (Path.IsPathRooted(rawPath)) return rawPath;
        if (repoRoot != null) return Path.GetFullPath(Path.Combine(repoRoot, rawPath));
        return rawPath;
    }

    /// <summary>
    /// safe-name is InstanceId.Replace(':','_') i.e. "repo_persona". Repo names in
    /// huddle config contain no underscores, so split on the first underscore: left is
    /// the repo, the rejoined remainder (with ':' restored) is the display id.
    /// </summary>
    private static (string repo, string display) SplitSafeName(string safeName)
    {
        var i = safeName.IndexOf('_');
        if (i <= 0) return (safeName, safeName);
        var repo = safeName[..i];
        var persona = safeName[(i + 1)..];
        return (repo, $"{repo}:{persona}");
    }
}

/// <summary>
/// Churn: files dirty in each registered repo's working tree (source code included).
/// Queried only when maxLevel >= Churn, so the default `docs` path never shells git. Uses
/// the existing GitHelper (git CLI) — no new dependency. Gitignored files are not reported
/// by git, which is correct for this tier. Binaries and build output are filtered out so
/// the list stays human-readable.
/// </summary>
public sealed class GitChurnSource : IDocumentSource
{
    private readonly IReadOnlyDictionary<string, SessionDefinition> _repos;
    private readonly Action<string> _log;

    // Build-output / dependency directories — never interesting as "changed documents".
    private static readonly string[] NoiseDirs = { "bin/", "obj/", "publish/", "node_modules/", ".vs/", ".git/" };

    // Binary / non-text extensions — noise in a changed-files view.
    private static readonly HashSet<string> BinaryExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".pdb", ".bin", ".obj", ".o", ".lib", ".so", ".dylib", ".a",
        ".class", ".jar", ".nupkg", ".snupkg", ".zip", ".7z", ".gz", ".tar", ".rar",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".icns",
        ".pdf", ".mp3", ".mp4", ".mov", ".wav", ".woff", ".woff2", ".ttf", ".otf", ".eot",
        ".wasm", ".msix", ".appx", ".cer", ".pfx", ".snk", ".dat", ".cache",
    };

    public GitChurnSource(IReadOnlyDictionary<string, SessionDefinition> repos, Action<string> log)
    {
        _repos = repos;
        _log = log;
    }

    public IReadOnlyList<DocumentEntry> GetDocuments(DocLevel maxLevel)
    {
        var results = new List<DocumentEntry>();
        if (maxLevel < DocLevel.Churn) return results;

        var skipped = 0;
        foreach (var (repoName, def) in _repos)
        {
            var dirty = GitHelper.StatusDirty(def.Root);   // repo-relative, forward slashes
            foreach (var rel in dirty)
            {
                if (IsNoise(rel)) { skipped++; continue; }

                var full = Path.GetFullPath(Path.Combine(def.Root, rel));
                DateTime? ts;
                try { ts = File.Exists(full) ? File.GetLastWriteTime(full) : (DateTime?)null; }
                catch { ts = null; }

                results.Add(new DocumentEntry(
                    Title: Path.GetFileName(rel),
                    Path: full,
                    SourceSession: "(working tree)",
                    Repo: repoName,
                    Timestamp: ts,
                    Level: DocLevel.Churn,
                    Note: rel));
            }
        }

        _log($"docs: churn contributed {results.Count} file(s) across {_repos.Count} repo(s) ({skipped} binary/build skipped)");
        return results;
    }

    // Build output, dependency dirs, and binary file types are noise in a changed-docs view.
    private static bool IsNoise(string rel)
    {
        var p = rel.Replace('\\', '/');
        foreach (var dir in NoiseDirs)
            if (p.StartsWith(dir, StringComparison.OrdinalIgnoreCase) ||
                p.Contains("/" + dir, StringComparison.OrdinalIgnoreCase))
                return true;
        return BinaryExts.Contains(Path.GetExtension(p));
    }
}

/// <summary>
/// Auto-discovery (B015): surfaces human docs even when no one declared them.
///
/// Discovery is broad; the bare-list curation lives in the VERB (HandleDocs):
///   * The huddle "home" repo gets a FULL scan — top-level `*.md` (README, DESIGN, …)
///     plus `docs/**/*.md`. Huddle's specs/plans ARE the orchestration record.
///   * Every OTHER registered repo's full `docs/**` tree is discovered too, so any
///     folder is filterable (`docs &lt;folder&gt;`, `docs -1w`). To keep the quiet default,
///     the verb shows cross-repo auto docs in the BARE listing only under the
///     `reference/` tier; any filter (folder or time window) searches the full set.
///
/// Yields Output / Plans (anything under a /plans/ directory). On-demand, like every
/// source; it only reads when the verb runs. Declared entries win on dedupe (this source
/// is listed AFTER ScratchpadDocumentSource in the composite), so a doc that is both
/// declared and on disk shows once, with the agent's richer title/level.
/// </summary>
public sealed class FilesystemDocSource : IDocumentSource
{
    private readonly string _huddleRoot;
    private readonly IReadOnlyDictionary<string, SessionDefinition> _repos;
    private readonly Action<string> _log;

    // First markdown H1 (# Title) — used as the display title when present.
    private static readonly Regex H1Rx = new(@"^#\s+(?<t>.+?)\s*$", RegexOptions.Compiled);

    public FilesystemDocSource(string huddleRoot, IReadOnlyDictionary<string, SessionDefinition> repos, Action<string> log)
    {
        _huddleRoot = huddleRoot;
        _repos = repos;
        _log = log;
    }

    public IReadOnlyList<DocumentEntry> GetDocuments(DocLevel maxLevel)
    {
        var results = new List<DocumentEntry>();

        // Home repo: full top-level *.md + docs/** scan.
        if (!string.IsNullOrEmpty(_huddleRoot) && Directory.Exists(_huddleRoot))
        {
            var files = new List<string>();
            try
            {
                files.AddRange(Directory.GetFiles(_huddleRoot, "*.md", SearchOption.TopDirectoryOnly));
                var docsDir = Path.Combine(_huddleRoot, "docs");
                if (Directory.Exists(docsDir))
                    files.AddRange(Directory.GetFiles(docsDir, "*.md", SearchOption.AllDirectories));
            }
            catch (Exception ex) { _log($"docs: home-repo scan failed — {ex.Message}"); }
            foreach (var file in files)
                AddFile(file, "huddle", maxLevel, results);
        }

        // Every other registered repo: full docs/** discovered so any folder is filterable
        // (docs <folder>, docs -1w). The VERB keeps the BARE listing quiet — it shows
        // cross-repo auto docs only under the reference tier unless a filter is set.
        var huddleFull = NormDir(_huddleRoot);
        foreach (var (repoName, def) in _repos)
        {
            if (string.IsNullOrEmpty(def.Root) || NormDir(def.Root) == huddleFull) continue;  // home repo done above
            var docsDir = Path.Combine(def.Root, "docs");
            if (!Directory.Exists(docsDir)) continue;
            string[] files;
            try { files = Directory.GetFiles(docsDir, "*.md", SearchOption.AllDirectories); }
            catch (Exception ex) { _log($"docs: docs scan failed for {repoName} — {ex.Message}"); continue; }
            foreach (var file in files)
                AddFile(file, repoName, maxLevel, results);
        }

        _log($"docs: auto-discovered {results.Count} repo doc(s) at <= {maxLevel}");
        return results;
    }

    private void AddFile(string file, string repoName, DocLevel maxLevel, List<DocumentEntry> results)
    {
        var level = InferLevel(file);
        if (level > maxLevel) return;

        DateTime? ts;
        try { ts = File.GetLastWriteTime(file); } catch { ts = null; }

        results.Add(new DocumentEntry(
            Title: TitleOf(file),
            Path: Path.GetFullPath(file),
            SourceSession: $"({repoName})",
            Repo: repoName,
            Timestamp: ts,
            Level: level,
            Note: "auto"));
    }

    // Full path, trailing-slash- and case-normalized — for comparing repo roots.
    private static string NormDir(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return "";
        try { return Path.GetFullPath(dir).TrimEnd('\\', '/').ToLowerInvariant(); }
        catch { return dir.ToLowerInvariant(); }
    }

    private static DocLevel InferLevel(string path)
    {
        var p = path.Replace('\\', '/');
        return p.Contains("/plans/", StringComparison.OrdinalIgnoreCase) ? DocLevel.Plans : DocLevel.Output;
    }

    private static string TitleOf(string file)
    {
        try
        {
            foreach (var line in File.ReadLines(file).Take(20))
            {
                var m = H1Rx.Match(line.TrimStart());
                if (m.Success) return m.Groups["t"].Value.Trim();
            }
        }
        catch { /* fall through to filename */ }
        return Path.GetFileNameWithoutExtension(file);
    }
}

/// <summary>
/// Merges child sources, de-duplicates by resolved path (first source wins — so declared
/// entries beat auto-discovered ones), filters to entries at &lt;= maxLevel, and sorts
/// newest-first (entries with no timestamp sort last). The verb talks only to this.
/// </summary>
public sealed class CompositeDocumentSource : IDocumentSource
{
    private readonly IReadOnlyList<IDocumentSource> _sources;

    public CompositeDocumentSource(params IDocumentSource[] sources) => _sources = sources;

    public IReadOnlyList<DocumentEntry> GetDocuments(DocLevel maxLevel)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<DocumentEntry>();
        foreach (var source in _sources)
        {
            foreach (var e in source.GetDocuments(maxLevel))
            {
                if (e.Level > maxLevel) continue;
                if (!seen.Add(NormalizePath(e.Path))) continue;   // first source wins
                merged.Add(e);
            }
        }
        return merged
            .OrderByDescending(e => e.Timestamp ?? DateTime.MinValue)
            .ToList();
    }

    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }
}
