namespace Huddle;

/// <summary>
/// Everything claimed in one git repository, in a form a commit path can be tested
/// against. Claims are recorded relative to the CLAIMING SESSION's checkout root, while
/// a commit's paths are relative to the GIT root — for a checkout that sits in a
/// subdirectory those are two different spellings of one file, and comparing the raw
/// strings accuses correctly-claimed work. That is the I008 separator bug one level up:
/// same shape, same consequence, found the same way (in production, not in a test).
///
/// So claims are indexed as ABSOLUTE paths wherever the root is known, and matched
/// against the commit path resolved against the git top. Entries written before roots
/// were recorded keep a relative-tail fallback: over-matching costs silence, which is
/// the safe direction for a warning nobody can act on retroactively.
/// </summary>
public sealed class ClaimedIndex
{
    private readonly HashSet<string> _absolute = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _relative = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _absolute.Count + _relative.Count;

    /// <summary>Record one grant. <paramref name="root"/> may be empty (legacy entry),
    /// in which case only the relative-tail fallback is available for those files.</summary>
    public void AddClaim(string? root, IEnumerable<string> files)
    {
        var r = CommitAudit.Norm(root ?? "").TrimEnd('/');
        foreach (var f in files)
        {
            if (string.IsNullOrWhiteSpace(f)) continue;
            var rel = CommitAudit.Norm(f);
            _relative.Add(rel);
            if (r.Length > 0) _absolute.Add(r + "/" + rel);
        }
    }

    /// <summary>Is this commit path (relative to <paramref name="gitTop"/>) covered?</summary>
    public bool Covers(string gitTop, string changedRelative)
    {
        var rel = CommitAudit.Norm(changedRelative);
        var top = CommitAudit.Norm(gitTop).TrimEnd('/');

        if (_absolute.Contains(top + "/" + rel)) return true;
        if (_relative.Contains(rel)) return true;

        // Legacy tail match: a rootless grant of "docs/BACKLOG.md" covers
        // "myapp/docs/BACKLOG.md". Anchored on a separator so "BACKLOG.md" never
        // matches "OLD-BACKLOG.md".
        foreach (var candidate in _relative)
            if (rel.Length > candidate.Length
                && rel.EndsWith("/" + candidate, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }
}
