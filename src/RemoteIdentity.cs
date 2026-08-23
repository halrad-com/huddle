namespace Huddle;

/// <summary>
/// A remote URL → stable identity "host/org/repo". Every repo has an `origin`, so the
/// remote NAME identifies nothing across a fleet; myapp carries a second remote
/// pointing at a repo a push must never reach. Userinfo is stripped — the `contoso@` in
/// an Azure URL must never reach the console or logs/huddle.log.
/// </summary>
public static class RemoteIdentity
{
    public static string? Parse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var s = url.Trim();

        string host, path;
        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme > 0)
        {
            var rest = s[(scheme + 3)..];
            var slash = rest.IndexOf('/');
            if (slash <= 0) return null;
            host = rest[..slash];
            path = rest[(slash + 1)..];
        }
        else
        {
            // scp-like: [user@]host:path
            var colon = s.IndexOf(':');
            if (colon <= 0 || s.Length > 1 && s[1] == ':' /* drive letter */) return null;
            host = s[..colon];
            path = s[(colon + 1)..];
        }

        var at = host.LastIndexOf('@');
        if (at >= 0) host = host[(at + 1)..];
        host = host.ToLowerInvariant();
        if (host.Length == 0 || !host.Contains('.')) return null;

        path = path.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        path = path.Trim('/');
        if (path.Length == 0) return null;

        // Azure: org/project/_git/repo → org/project when repo == project, else org/project/repo
        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        var gi = segs.IndexOf("_git");
        if (gi > 0 && gi == segs.Count - 2)
        {
            var repo = segs[gi + 1];
            segs.RemoveRange(gi, 2);
            if (!string.Equals(segs[^1], repo, StringComparison.OrdinalIgnoreCase)) segs.Add(repo);
        }
        return host + "/" + string.Join("/", segs);
    }

    /// <summary>remote name → identity, from `git remote -v`. Empty for a non-repo.</summary>
    public static IReadOnlyDictionary<string, string> ForRepo(string repoRoot)
    {
        var (ok, stdout, _) = GitHelper.RunRaw(repoRoot, "remote -v");
        return ok ? ParseRemoteList(stdout) : new Dictionary<string, string>();
    }

    public static IReadOnlyDictionary<string, string> ParseRemoteList(string remoteVOutput)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in remoteVOutput.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            var name = line[..tab];
            var rest = line[(tab + 1)..];
            var sp = rest.LastIndexOf(" (", StringComparison.Ordinal);
            var url = sp > 0 ? rest[..sp] : rest;
            var id = Parse(url);
            if (id != null && !d.ContainsKey(name)) d[name] = id;
        }
        return d;
    }
}
