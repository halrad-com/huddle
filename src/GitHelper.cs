using System.Diagnostics;

namespace Huddle;

/// <summary>
/// Thin wrappers around git commands used by dispatch-batch (base sha) and
/// auto-release (diff + status audit). All methods return null / empty list on
/// any failure — callers should treat that as "no info available" rather than
/// propagating exceptions into orchestration paths.
/// </summary>
public static class GitHelper
{
    /// <summary>
    /// Returns the current HEAD sha (40 chars) for the repo at repoRoot, or null on failure.
    /// </summary>
    public static string? GetHeadSha(string repoRoot)
    {
        var (ok, stdout, _) = Run(repoRoot, "rev-parse HEAD");
        if (!ok) return null;
        var sha = stdout.Trim();
        return sha.Length == 40 ? sha : null;
    }

    /// <summary>
    /// Returns the list of files changed between baseSha..HEAD. Empty on failure.
    /// Paths are repo-relative and use forward slashes (git normalizes).
    /// </summary>
    public static List<string> DiffNames(string repoRoot, string baseSha)
    {
        var (ok, stdout, _) = Run(repoRoot, $"diff --name-only {baseSha}..HEAD");
        if (!ok) return new List<string>();
        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Returns the list of files currently dirty in the working tree (modified, added, deleted, untracked).
    /// Empty on failure.
    /// </summary>
    public static List<string> StatusDirty(string repoRoot)
    {
        var (ok, stdout, _) = Run(repoRoot, "status --porcelain");
        if (!ok) return new List<string>();
        var results = new List<string>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // Porcelain format: "XY path" — two status chars, space, path
            if (line.Length < 4) continue;
            var path = line[3..].Trim();
            if (path.Length > 0) results.Add(path);
        }
        return results;
    }

    /// <summary>
    /// Returns the absolute git common dir for the repo at repoRoot (where
    /// logs/refs/remotes lives — shared across worktrees), or null on failure.
    /// Handles plain repos, linked worktrees, and .git-file cases via
    /// <c>rev-parse --git-common-dir</c>.
    /// </summary>
    public static string? GitCommonDir(string repoRoot)
    {
        var (ok, stdout, _) = Run(repoRoot, "rev-parse --git-common-dir");
        if (!ok) return null;
        var raw = stdout.Trim();
        if (raw.Length == 0) return null;
        // The result may be relative to repoRoot (commonly ".git").
        var full = Path.IsPathRooted(raw) ? raw : Path.Combine(repoRoot, raw);
        try { return Path.GetFullPath(full); } catch { return null; }
    }

    /// <summary>
    /// The two facts that tell one checkout from another (ISSUES.md I014): the shared git
    /// object store — identical for every worktree of a repo — and THIS worktree's top.
    /// Both null on any failure (not a checkout, no git, timeout); the caller treats that as
    /// "unknown", never as an answer.
    ///
    /// One process for both, in argument order, because the pair is always wanted together
    /// and this runs on the claim path. The common dir may come back relative to
    /// <paramref name="dir"/> (".git" in a main checkout, "../.git" from a subdirectory of
    /// one), so it is resolved before being returned.
    /// </summary>
    public static (string? Store, string? Top) CheckoutIdentity(string dir)
    {
        var (ok, stdout, _) = Run(dir, "rev-parse --git-common-dir --show-toplevel");
        if (!ok) return (null, null);
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
        if (lines.Count < 2) return (null, null);
        try
        {
            var store = Path.IsPathRooted(lines[0]) ? lines[0] : Path.Combine(dir, lines[0]);
            return (Path.GetFullPath(store), Path.GetFullPath(lines[1]));
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Returns the path to git's system-level config file (where Git for Windows
    /// puts credential.helper=manager), or null if none is reported. Discovered
    /// once at startup so a per-session config can [include] it faithfully.
    /// </summary>
    public static string? SystemConfigPath()
    {
        // --show-origin prefixes each line with "file:<path>\t...". Any system-scope
        // line names the system config file.
        var (ok, stdout, _) = Run(Directory.GetCurrentDirectory(), "config --system --list --show-origin");
        if (!ok) return null;
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("file:", StringComparison.Ordinal)) continue;
            var tab = line.IndexOf('\t');
            if (tab < 0) continue;
            var path = line["file:".Length..tab].Trim();
            if (path.Length > 0) return path;
        }
        return null;
    }

    /// <summary>
    /// Raw <c>git worktree list --porcelain</c> output for the repo containing
    /// <paramref name="dir"/>, or "" on failure. Parsed by
    /// <see cref="GitWorktrees.ParsePorcelain"/>.
    /// </summary>
    public static string WorktreeListPorcelain(string dir)
    {
        var (ok, stdout, _) = Run(dir, "worktree list --porcelain");
        return ok ? stdout : "";
    }

    /// <summary>
    /// Subject and committer-date of the most recent commit that touched
    /// <paramref name="dir"/> (scoped to that path). Either field is null when
    /// unavailable (not a repo, no commits, git failure).
    /// </summary>
    public static (string? subject, DateTime? when) LastCommitTouching(string dir)
    {
        // %x1f = unit separator between subject and strict-ISO committer date.
        var (ok, stdout, _) = Run(dir, "log -1 --format=%s%x1f%cI -- .");
        if (!ok) return (null, null);
        var line = stdout.Trim();
        if (line.Length == 0) return (null, null);
        var parts = line.Split('\x1f');
        DateTime? when = null;
        if (parts.Length > 1 && DateTimeOffset.TryParse(parts[1].Trim(), out var dto))
            when = dto.LocalDateTime;
        return (parts[0].Trim(), when);
    }

    /// <summary>
    /// Number of commits touching <paramref name="dir"/> since <paramref name="since"/>
    /// (a git approxidate, e.g. "30.days"). 0 on any failure.
    /// </summary>
    public static int CommitsSince(string dir, string since)
    {
        var (ok, stdout, _) = Run(dir, $"rev-list --count --since={since} HEAD -- .");
        if (!ok) return 0;
        return int.TryParse(stdout.Trim(), out var n) ? n : 0;
    }

    /// <summary>Current branch name for <paramref name="dir"/>, or null (detached / failure).</summary>
    public static string? CurrentBranch(string dir)
    {
        var (ok, stdout, _) = Run(dir, "rev-parse --abbrev-ref HEAD");
        if (!ok) return null;
        var b = stdout.Trim();
        return b.Length == 0 || b == "HEAD" ? null : b;
    }

    private static (bool ok, string stdout, string stderr) Run(string repoRoot, string args)
    {
        try
        {
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return (false, "", "");
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return (false, "", "");
            }
            return (p.ExitCode == 0, stdout, stderr);
        }
        catch
        {
            return (false, "", "");
        }
    }
}
