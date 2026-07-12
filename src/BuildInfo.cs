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
            return $"huddle v{Version}\n" +
                   $"  branch: {Branch}\n" +
                   $"  commit: {Commit}\n" +
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
}
