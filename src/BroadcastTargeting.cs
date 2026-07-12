namespace Huddle;

/// <summary>
/// Pure filter logic for repo-scoped broadcasts
/// (spec: docs/superpowers/specs/2026-07-10-repo-scoped-broadcast-design.md).
/// Kept free of SessionManager/JSON dependencies so it is unit-testable.
/// </summary>
public static class BroadcastTargeting
{
    /// <summary>
    /// Parse a comma-delimited repo filter ("app, webshop") into canonical repo
    /// names. Returns null with an error when any token is unknown or when no
    /// usable token remains — a scoped broadcast that can't scope must fail
    /// loud, not fan out wide.
    /// </summary>
    public static HashSet<string>? ResolveRepoFilter(
        string csv, Func<string, string> resolveName, Func<string, bool> isKnownRepo, out string? error)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var canonical = resolveName(raw);
            if (!isKnownRepo(canonical))
            {
                error = $"unknown repo '{raw}'";
                return null;
            }
            set.Add(canonical);
        }
        if (set.Count == 0)
        {
            error = "repo filter contains no repo names";
            return null;
        }
        error = null;
        return set;
    }

    /// <summary>Instance IDs are "repo:persona" (or bare "repo"); match the repo part.</summary>
    public static bool MatchesRepo(string instanceId, HashSet<string> repos)
    {
        var idx = instanceId.IndexOf(':');
        var repo = idx < 0 ? instanceId : instanceId[..idx];
        return repos.Contains(repo);
    }
}
