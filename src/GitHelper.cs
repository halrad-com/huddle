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
