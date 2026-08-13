namespace Huddle;

/// <summary>
/// One git worktree of a repo: its checkout root, the branch it has out (null when
/// detached), and whether it is the MAIN worktree (the one that owns the .git dir).
/// </summary>
public sealed record Worktree(string Root, string? Branch, bool IsMain);

/// <summary>
/// Expands a registered repo directory into the equivalent directory inside EACH of its
/// git worktrees, so a doc or project authored on a feature branch (which lives only in a
/// linked worktree until it merges) is visible pre-merge and stays visible after — shown
/// once, main-worktree canonical, while both copies exist.
///
/// The registered root can be a SUBDIR of the git repo (e.g. <c>LIB/myapp</c> inside
/// the LIB git repo); that subpath is preserved across worktrees, so
/// <c>LIB/myapp</c> expands to <c>LIB-rockalley/myapp</c>. The main worktree is
/// always first in the returned list, which is what lets callers dedupe main-canonical.
///
/// git is shelled read-only (via <see cref="GitHelper"/>); any failure degrades to a
/// single main entry that is just the registered root — a non-git directory still works,
/// it simply has no other worktrees. The parsing and path composition are pure so they
/// can be tested without a repo.
/// </summary>
public static class GitWorktrees
{
    /// <summary>
    /// The worktree-equivalent directories for a registered repo dir, main first.
    /// Never throws; a non-git or git-less dir yields just the input as the sole main.
    /// </summary>
    public static List<Worktree> ForRepo(string registeredRoot)
    {
        // One git call: `worktree list --porcelain` reports every worktree root, and its
        // FIRST record is the main worktree — which IS the git repo top. The registered
        // root may be a subdir of that top; preserve the subpath across all worktrees.
        var roots = ParsePorcelain(GitHelper.WorktreeListPorcelain(registeredRoot));
        if (roots.Count == 0)
            return new List<Worktree> { new(NormFull(registeredRoot), null, true) };

        var top = roots[0].Root;
        return ComposeDirs(top, SubPath(top, registeredRoot), roots);
    }

    /// <summary>
    /// Pure parse of <c>git worktree list --porcelain</c>. Records are blank-line
    /// separated; the FIRST record is the main worktree. Recognizes
    /// <c>worktree &lt;path&gt;</c> and <c>branch refs/heads/&lt;name&gt;</c> (a
    /// <c>detached</c>/missing branch line yields a null branch). Robust to a missing
    /// trailing blank line and to records with no blank separator.
    /// </summary>
    public static List<Worktree> ParsePorcelain(string porcelain)
    {
        var result = new List<Worktree>();
        if (string.IsNullOrWhiteSpace(porcelain)) return result;

        string? root = null;
        string? branch = null;
        void Flush()
        {
            if (root != null)
                result.Add(new Worktree(NormFull(root), branch, result.Count == 0));
            root = null;
            branch = null;
        }

        foreach (var raw in porcelain.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) { Flush(); continue; }
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                Flush();                                   // close the previous record
                root = line["worktree ".Length..].Trim();
            }
            else if (line.StartsWith("branch refs/heads/", StringComparison.Ordinal))
            {
                branch = line["branch refs/heads/".Length..].Trim();
            }
        }
        Flush();
        return result;
    }

    /// <summary>
    /// Pure: the subpath of <paramref name="registeredRoot"/> beneath its worktree
    /// <paramref name="top"/> (forward-slash agnostic), or "" when the registered root
    /// IS the worktree top or lies outside it.
    /// </summary>
    public static string SubPath(string top, string registeredRoot)
    {
        string rel;
        try { rel = Path.GetRelativePath(NormFull(top), NormFull(registeredRoot)); }
        catch { return ""; }
        if (rel == "." || rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
            return "";
        return rel;
    }

    /// <summary>
    /// Pure: apply the registered subpath to each worktree root, preserving order
    /// (main first) and each worktree's branch / main flag.
    /// </summary>
    public static List<Worktree> ComposeDirs(string mainTop, string sub, IReadOnlyList<Worktree> roots)
    {
        var list = new List<Worktree>(roots.Count);
        foreach (var wt in roots)
        {
            var dir = string.IsNullOrEmpty(sub) ? wt.Root : Path.Combine(wt.Root, sub);
            list.Add(new Worktree(NormFull(dir), wt.Branch, wt.IsMain));
        }
        return list;
    }

    private static string NormFull(string p)
    {
        try { return Path.GetFullPath(p); } catch { return p; }
    }
}
