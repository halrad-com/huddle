namespace Huddle;

/// <summary>
/// `huddle --settings` / `--set k v` / `--unset k`. Dispatched in Program.cs BEFORE
/// config load and before the console starts (same position as --claim / --release /
/// --ledger), so settings can be changed without launching the orchestrator — from a
/// session, a script, or a second window while huddle is running. Exit 0 written /
/// listed, 1 refused; every refusal names the key and the reason.
/// </summary>
public static class SettingsCli
{
    /// <summary>The settings verbs, in the order they are searched for.</summary>
    private static readonly string[] VerbFlags = ["--settings", "--set", "--unset"];

    public static string ResolveConfigPath(string[] args) => ConfigPathResolver.Resolve(args);

    /// <summary>
    /// The settings verb present in <paramref name="args"/>, or null if there is none.
    /// Position-independent: `huddle --config x.json --set k v` is the form documented in
    /// docs/settings.md and it used to fall through the dispatch entirely and boot a second
    /// orchestrator (S3). A `--config` VALUE that happens to spell a verb is skipped, so a
    /// file really named `--settings` cannot hijack the dispatch.
    /// </summary>
    public static string? FindVerb(string[] args)
    {
        var i = FindVerbIndex(args);
        return i < 0 ? null : args[i];
    }

    private static int FindVerbIndex(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (ConfigPathResolver.IsConfigFlag(args[i])) { i++; continue; }   // skip its value
            if (Array.IndexOf(VerbFlags, args[i]) >= 0) return i;
        }
        return -1;
    }

    public static int Run(string[] args, Action<string> output)
    {
        var configPath = ResolveConfigPath(args);
        var verbIndex = FindVerbIndex(args);
        if (verbIndex < 0)
        {
            output("usage: huddle --settings | --set <key> <value> | --unset <key>  [--config <path>]");
            return 1;
        }
        var verb = args[verbIndex];

        // Everything that is not the verb itself, the --config flag, or its value.
        var positional = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (ConfigPathResolver.IsConfigFlag(args[i])) { i++; continue; }
            if (i == verbIndex) continue;
            positional.Add(args[i]);
        }

        switch (verb)
        {
            case "--settings":
                return List(configPath, output);

            case "--set":
                if (positional.Count < 2)
                {
                    output("usage: huddle --set <key> <value> [--config <path>]");
                    return 1;
                }
                if (!SettingsWriter.TrySet(configPath, positional[0], positional[1], out var err, out var def))
                {
                    output($"refused: {err}");
                    return 1;
                }
                output($"set — {def!.Key} = {positional[1]}{SetHint(def)}");
                return 0;

            case "--unset":
                if (positional.Count < 1)
                {
                    output("usage: huddle --unset <key> [--config <path>]");
                    return 1;
                }
                if (!SettingsWriter.TryUnset(configPath, positional[0], out var uerr))
                {
                    output($"refused: {uerr}");
                    return 1;
                }
                // Same out-of-process truth as SetHint: the file changed, the running
                // instance did not. peekHotkey's "built-in default" is a list of chords
                // tried in order, so saying "default" alone would misdescribe what the
                // operator just went back to.
                var revertsTo = SettingsCatalog.TryGet(positional[0], out var ud)
                    && ud.Key.Equals("peekHotkey", StringComparison.OrdinalIgnoreCase)
                        ? "reverts to the built-in candidate chords"
                        : "reverts to its built-in default";
                output($"unset — {positional[0]} {revertsTo} (takes effect on reload)");
                return 0;

            default:
                output($"unknown settings command {args[0]}");
                return 1;
        }
    }

    /// <summary>
    /// What a written value does next, from OUT HERE.
    ///
    /// <para><c>Applies == Live</c> says the running orchestrator re-reads the value without
    /// a restart. It does NOT say this process can reach that orchestrator, and it cannot:
    /// <c>--set</c> is dispatched before the console starts, holds no switch and knows no
    /// running instance. The old code printed the reload hint only for <c>Startup</c> keys,
    /// so when <c>peekHotkey</c> became <c>Live</c> this path started printing a bare
    /// confirmation for a chord that had not changed on the live instance, and the one line
    /// that used to say so was gone. Every out-of-process write lands on reload; peekHotkey
    /// adds the one shortcut that skips it, and names the verb that takes it.</para>
    /// </summary>
    static string SetHint(SettingDef def) =>
        def.Key.Equals("peekHotkey", StringComparison.OrdinalIgnoreCase)
            ? " (takes effect on reload, or immediately from `settings peekHotkey <chord>` inside a running huddle)"
            : " (takes effect on reload)";

    static int List(string configPath, Action<string> output)
    {
        HuddleConfig cfg;
        try { cfg = HuddleConfig.Load(configPath); }
        catch (SettingsException ex)
        {
            foreach (var e in ex.Errors) output($"refused: {e}");
            return 1;
        }
        catch (Exception ex)
        {
            output($"refused: {ex.Message}");
            return 1;
        }
        foreach (var line in Render(cfg.Settings, configPath).Split('\n'))
            output(line.TrimEnd('\r'));
        return 0;
    }

    /// <summary>Shared by the CLI and the `settings` console verb. Names the file and the
    /// source of every value — a setting that changes behaviour without appearing here
    /// recreates the invisible-until-wrong failure this surface exists to remove.</summary>
    public static string Render(ResolvedSettings s, string configPath)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"settings — {Path.GetFullPath(configPath)}");
        sb.AppendLine();
        foreach (var r in s.All)
        {
            var src = r.Source switch
            {
                SettingSource.Settings => "settings",
                SettingSource.TopLevelLegacy => "top-level (legacy)",
                _ => "default"
            };
            var when = r.Def.Applies == SettingApplies.Live ? "live" : "startup";
            sb.AppendLine($"  {r.Def.Key,-24}{r.Value,-10}{src,-20}{when,-9}{r.Def.Help}");
        }
        foreach (var w in s.Warnings) sb.AppendLine($"  ! {w}");
        return sb.ToString();
    }
}
