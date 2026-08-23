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

    /// <summary>Resolve against the current working directory — the normal case.</summary>
    public static string Resolve(string[] args) => Resolve(args, Directory.GetCurrentDirectory());

    /// <summary>True when <paramref name="arg"/> is the flag that consumes the next argument
    /// as a path. Callers skipping over the pair when collecting positionals use this so the
    /// rule lives in one place.</summary>
    public static bool IsConfigFlag(string arg) => arg is "--config" or "-c";
}
