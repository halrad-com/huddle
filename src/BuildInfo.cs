using System.Reflection;

namespace Huddle;

/// <summary>
/// Surfaces the version/build identity baked into the assembly by the StampGitInfo
/// target in huddle.csproj (AssemblyInformationalVersion = "&lt;version&gt;+&lt;branch&gt;.&lt;commit&gt;").
/// Lets the operator tell at a glance which build is actually running — the recurring
/// "is this the new build or the old one?" question.
/// </summary>
public static class BuildInfo
{
    private static readonly string _informational =
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";

    /// <summary>Semantic version, e.g. "1.0.0" (the part before '+').</summary>
    public static string Version
    {
        get
        {
            var plus = _informational.IndexOf('+');
            return (plus > 0 ? _informational[..plus] : _informational).Trim();
        }
    }

    public static string Branch => Meta().branch;
    public static string Commit => Meta().commit;

    /// <summary>
    /// Raw `git describe --tags --long --always` baked at build time, e.g.
    /// "public-release-20260722-2-g12f0169" (tag, commits-since, g-hash) or a bare
    /// hash when the repo has no tags. "unknown" if git wasn't available at build.
    /// </summary>
    public static string Describe => Metadata("GitDescribe");

    /// <summary>Nearest tag reachable from the built commit, or null if none/unknown.</summary>
    public static string? NearestTag => ParseDescribe().tag;

    /// <summary>Commits between the nearest tag and the built commit, or null if unknown.</summary>
    public static int? CommitsSinceTag => ParseDescribe().count;
    public static string ShortCommit
    {
        get { var c = Commit; return c.Length >= 7 ? c[..7] : c; }
    }

    /// <summary>Last-write time of the running exe — a practical "built/deployed at".</summary>
    public static DateTime? BuildTime
    {
        get
        {
            try
            {
                var path = Environment.ProcessPath;
                return path != null && File.Exists(path) ? File.GetLastWriteTime(path) : null;
            }
            catch { return null; }
        }
    }

    /// <summary>One-line summary for the startup banner, e.g. "v1.0.0 (master @ 1a2b3c4)".</summary>
    public static string Short => $"v{Version} ({Branch} @ {ShortCommit})";

    /// <summary>Multi-line detail for the `ver` command.</summary>
    public static string Full
    {
        get
        {
            var bt = BuildTime;
            var n = CommitsSinceTag;
            var tagLine = NearestTag is { } tag
                ? $"  tag:    {tag} (+{n} commit{(n == 1 ? "" : "s")})\n"
                : "  tag:    (none)\n";
            return $"huddle v{Version}\n" +
                   $"  branch: {Branch}\n" +
                   $"  commit: {Commit}\n" +
                   tagLine +
                   $"  built:  {(bt.HasValue ? bt.Value.ToString("yyyy-MM-dd HH:mm:ss") : "unknown")}";
        }
    }

    // Split "<version>+<branch>.<commit>" → (branch, commit). The commit is a 40-char
    // hash with no '.', so the LAST '.' is the branch/commit separator even when the
    // branch name itself contains dots (e.g. "release-1.0").
    private static (string branch, string commit) Meta()
    {
        var plus = _informational.IndexOf('+');
        if (plus < 0 || plus + 1 >= _informational.Length) return ("unknown", "unknown");
        var meta = _informational[(plus + 1)..];
        var dot = meta.LastIndexOf('.');
        return dot < 0
            ? (meta.Trim(), "unknown")
            : (meta[..dot].Trim(), meta[(dot + 1)..].Trim());
    }

    // Read an AssemblyMetadata value baked by the StampGitInfo target, or "" if absent.
    private static string Metadata(string key)
    {
        foreach (var a in Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>())
            if (string.Equals(a.Key, key, StringComparison.Ordinal))
                return a.Value ?? "";
        return "";
    }

    // Parse "<tag>-<N>-g<hash>" (git describe --long). Tag names may contain hyphens,
    // so the last two '-'-separated fields are N and g<hash>; everything before is the
    // tag. A bare hash (no tags → --always fallback) or "unknown" yields (null, null).
    private static (string? tag, int? count) ParseDescribe()
    {
        var d = Describe;
        if (string.IsNullOrEmpty(d) || d == "unknown") return (null, null);
        var parts = d.Split('-');
        if (parts.Length < 3) return (null, null);          // bare hash, no tag
        var last = parts[^1];
        if (last.Length < 2 || last[0] != 'g') return (null, null);
        if (!int.TryParse(parts[^2], out var count)) return (null, null);
        return (string.Join('-', parts[..^2]), count);
    }
}
