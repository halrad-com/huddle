namespace Huddle;

/// <summary>
/// The one place that decides which huddle.json a given invocation means.
///
/// S6 (review 2026-08-22): there were three copies of this scan — Program.Main,
/// RunProjectsHtml and SettingsCli — and the SettingsCli copy lacked the myapp.json
/// fallback, so `huddle --settings` and the console could disagree about which config file
/// exists. A settings surface that reads a different file from the one huddle runs is the
/// invisible-until-wrong failure the settings feature was built to remove, so the scan is
/// shared rather than repeated.
/// </summary>
public static class ConfigPathResolver
{
    public const string Default = "huddle.json";
    public const string Legacy = "myapp.json";

    /// <summary>
    /// An explicit <c>--config</c>/<c>-c</c> anywhere in <paramref name="args"/> wins and is
    /// returned verbatim — the operator naming a file is never second-guessed, even if it
    /// does not exist (the caller reports that far more usefully than a silent substitution).
    /// Otherwise <c>huddle.json</c>, falling back to the legacy <c>myapp.json</c> only
    /// when it exists in <paramref name="baseDir"/> and <c>huddle.json</c> does not.
    /// </summary>
    public static string Resolve(string[] args, string baseDir)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] is "--config" or "-c") return args[i + 1];

        if (!File.Exists(Path.Combine(baseDir, Default)) && File.Exists(Path.Combine(baseDir, Legacy)))
            return Path.Combine(baseDir, Legacy);

        return Default;
    }

    /// <summary>
    /// Resolve with a last-resort registered-root fallback (shell registration): when
    /// <paramref name="baseDir"/> holds neither huddle.json nor myapp.json and no
    /// --config was given, a root recorded by `huddle --register` whose huddle.json
    /// exists wins over returning a relative path — so a Win+R launch boots the
    /// registered huddle instead of first-run-templating a config into a random cwd.
    /// The lookup is injected for tests; pass <c>ShellRegistration.RegisteredRoot</c> live.
    /// </summary>
    public static string Resolve(string[] args, string baseDir, Func<string?> registeredRoot)
    {
        var resolved = Resolve(args, baseDir);
        if (resolved != Default) return resolved;                       // --config or legacy won
        if (File.Exists(Path.Combine(baseDir, Default))) return resolved; // cwd has a config

        var root = registeredRoot();
        if (!string.IsNullOrEmpty(root))
        {
            var candidate = Path.Combine(root, Default);
            if (File.Exists(candidate)) return candidate;
        }
        return resolved;
    }

    /// <summary>Resolve against the current working directory — the normal case.</summary>
    public static string Resolve(string[] args) => Resolve(args, Directory.GetCurrentDirectory());

    /// <summary>
    /// The identity of a huddle root as a short stable hash: full path, trailing separator
    /// removed, lowercased, SHA-256, first 16 hex characters.
    ///
    /// <para>Every per-root kernel object name is built from this — the singleton mutex
    /// (<c>Local\huddle-</c>, Program.Main) and the peek signal event
    /// (<c>Local\huddle-peek-</c>, <see cref="PeekSignal.NameFor"/>). The recipe used to be
    /// duplicated as source text in those two files with nothing pinning them together, and
    /// a drift makes <c>huddle --peek</c> signal a name nobody is listening on and then
    /// start a second huddle the mutex refuses. Same reason the config scan itself lives
    /// here (S6): two copies of one rule is one copy too many.</para>
    /// </summary>
    public static string RootHash(string configDir)
    {
        var rootKey = Path.GetFullPath(configDir).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rootKey)))[..16];
    }

    /// <summary>True when <paramref name="arg"/> is the flag that consumes the next argument
    /// as a path. Callers skipping over the pair when collecting positionals use this so the
    /// rule lives in one place.</summary>
    public static bool IsConfigFlag(string arg) => arg is "--config" or "-c";
}
