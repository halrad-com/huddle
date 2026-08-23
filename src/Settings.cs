using System.Globalization;
using System.Text.Json;

namespace Huddle;

public enum SettingKind { Bool, Int, Text }
public enum SettingApplies { Startup, Live }

/// <summary>One settable knob. The catalog is the single source of what is settable;
/// validation at load mirrors it so a value accepted here is never rejected downstream.</summary>
public sealed record SettingDef(
    string Key, SettingKind Kind, int Min, int Max, SettingApplies Applies, string Default, string Help);

public static class SettingsCatalog
{
    public static readonly IReadOnlyList<SettingDef> All = new[]
    {
        new SettingDef("contextFile",             SettingKind.Bool, 0, 0,     SettingApplies.Startup, "true",   "write logs/context.md"),
        new SettingDef("ipc",                     SettingKind.Bool, 0, 0,     SettingApplies.Startup, "true",   "run the orchestrator and mailboxes"),
        new SettingDef("crashLogRetention",       SettingKind.Int,  0, 1000,  SettingApplies.Live,    "10",     "crash logs kept per session"),
        new SettingDef("rescanIntervalSeconds",   SettingKind.Int,  0, 3600,  SettingApplies.Startup, "30",     "command-inbox rescan backstop; 0 disables"),
        new SettingDef("reclaimResourcesOnStop",  SettingKind.Bool, 0, 0,     SettingApplies.Live,    "false",  "also run recorded cleanup commands on leak"),
        new SettingDef("seedPermissions",         SettingKind.Bool, 0, 0,     SettingApplies.Startup, "true",   "seed each repo's .claude/settings.local.json"),
        new SettingDef("autoRestart",             SettingKind.Bool, 0, 0,     SettingApplies.Live,    "false",  "restart a session that dies"),
        new SettingDef("maxAutoRestarts",         SettingKind.Int,  0, 100,   SettingApplies.Live,    "3",      "restart attempts before giving up"),
        new SettingDef("backoffSeconds",          SettingKind.Text, 0, 0,     SettingApplies.Live,    "2,5,15", "restart backoff, comma-separated seconds"),
        new SettingDef("statsSinceDays",          SettingKind.Int,  1, 3650,  SettingApplies.Live,    "7",      "default window for stats"),
        new SettingDef("gitActivityLog",          SettingKind.Bool, 0, 0,     SettingApplies.Startup, "true",   "append cred requests + movements to logs/git-activity.jsonl"),
        new SettingDef("gitPollSeconds",          SettingKind.Int,  1, 300,   SettingApplies.Startup, "5",      "git activity poll interval"),
        new SettingDef("taskAckMinutes",          SettingKind.Int,  1, 1440,  SettingApplies.Live,    "15",     "unacked task escalates after this"),
        new SettingDef("transcriptMaxScan",       SettingKind.Int,  10, 1000, SettingApplies.Live,    "100",    "transcripts scanned by history / stats"),
    };

    private static readonly Dictionary<string, SettingDef> ByKey =
        All.ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);

    public static bool TryGet(string key, out SettingDef def) => ByKey.TryGetValue(key, out def!);

    /// <summary>A catalog key that matches after removing separators and case — the
    /// did-you-mean for a mistyped key. Null when nothing is close.</summary>
    public static string? Nearest(string key)
    {
        static string Fold(string s) => s.Replace("-", "").Replace("_", "").ToLowerInvariant();
        var f = Fold(key);
        return All.Select(s => s.Key).FirstOrDefault(k =>
            Fold(k) == f || Fold(k).StartsWith(f) || f.StartsWith(Fold(k)));
    }
}

public sealed class SettingsLoadResult
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Non-empty means REFUSE. Every entry is operator-facing and names the key.</summary>
    public List<string> Errors { get; } = new();
}

public static class SettingsLoader
{
    /// <summary>Validate the "settings" block. Null block = absent = no error.
    /// Reports EVERY problem, never only the first.</summary>
    public static SettingsLoadResult Load(JsonElement? block, string fileLabel)
    {
        var r = new SettingsLoadResult();
        if (block is null) return r;
        var el = block.Value;
        if (el.ValueKind != JsonValueKind.Object)
        {
            r.Errors.Add($"{fileLabel}: \"settings\" must be a JSON object");
            return r;
        }
        foreach (var prop in el.EnumerateObject())
        {
            if (!SettingsCatalog.TryGet(prop.Name, out var def))
            {
                var near = SettingsCatalog.Nearest(prop.Name);
                r.Errors.Add(near != null
                    ? $"{fileLabel}: unknown setting \"{prop.Name}\" — did you mean \"{near}\"?"
                    : $"{fileLabel}: unknown setting \"{prop.Name}\" (see: huddle --settings)");
                continue;
            }
            if (!TryReadJson(prop.Value, def, out var value, out var why))
            {
                r.Errors.Add($"{fileLabel}: {def.Key} — {why}");
                continue;
            }
            r.Values[def.Key] = value;
        }
        return r;
    }

    static bool TryReadJson(JsonElement v, SettingDef def, out string value, out string why)
    {
        value = ""; why = "";
        switch (def.Kind)
        {
            case SettingKind.Bool:
                if (v.ValueKind == JsonValueKind.True) { value = "true"; return true; }
                if (v.ValueKind == JsonValueKind.False) { value = "false"; return true; }
                why = "must be true or false"; return false;
            case SettingKind.Int:
                if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n))
                { why = $"must be a whole number between {def.Min} and {def.Max}"; return false; }
                return CheckInt(def, n, out value, out why);
            default:
                if (v.ValueKind != JsonValueKind.String) { why = "must be a string"; return false; }
                return TryParseText(def, v.GetString() ?? "", out value, out why);
        }
    }

    /// <summary>Parse a value as typed on the command line. Shares every rule with the JSON
    /// reader so the two cannot disagree on what is legal.</summary>
    public static bool TryParseRaw(SettingDef def, string raw, out string value, out string why)
    {
        value = ""; why = "";
        raw = (raw ?? "").Trim();
        switch (def.Kind)
        {
            case SettingKind.Bool:
                if (raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw == "1" || raw.Equals("on", StringComparison.OrdinalIgnoreCase))
                { value = "true"; return true; }
                if (raw.Equals("false", StringComparison.OrdinalIgnoreCase) || raw == "0" || raw.Equals("off", StringComparison.OrdinalIgnoreCase))
                { value = "false"; return true; }
                why = "must be true/false (on/off accepted)"; return false;
            case SettingKind.Int:
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                { why = $"must be a whole number between {def.Min} and {def.Max}"; return false; }
                return CheckInt(def, n, out value, out why);
            default:
                return TryParseText(def, raw, out value, out why);
        }
    }

    static bool CheckInt(SettingDef def, int n, out string value, out string why)
    {
        value = ""; why = "";
        if (n < def.Min || n > def.Max) { why = $"{n} is out of range ({def.Min}..{def.Max})"; return false; }
        value = n.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    static bool TryParseText(SettingDef def, string s, out string value, out string why)
    {
        value = ""; why = "";
        if (s.Trim().Length == 0) { why = "must not be empty"; return false; }
        if (def.Key == "backoffSeconds")
        {
            var parsed = new List<int>();
            foreach (var tok in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!int.TryParse(tok, out var n) || n <= 0)
                { why = $"must be comma-separated positive whole seconds, got \"{tok}\""; return false; }
                parsed.Add(n);
            }
            // Store canonically. `settings backoffSeconds 2, 5, 15` is now legal (the verb
            // takes the rest of the line), so the spacing the operator typed must not end
            // up in the file and in every display of it.
            value = string.Join(",", parsed);
            return true;
        }
        value = s.Trim();
        return true;
    }
}

public enum SettingSource { Default, Settings, TopLevelLegacy }

public sealed record ResolvedSetting(SettingDef Def, string Value, SettingSource Source);

public sealed class ResolvedSettings
{
    private readonly Dictionary<string, ResolvedSetting> _by;
    public IReadOnlyList<ResolvedSetting> All { get; }
    public IReadOnlyList<string> Warnings { get; }

    public ResolvedSettings(IReadOnlyList<ResolvedSetting> all, IReadOnlyList<string> warnings)
    {
        All = all; Warnings = warnings;
        _by = all.ToDictionary(r => r.Def.Key, StringComparer.OrdinalIgnoreCase);
    }

    public ResolvedSetting Get(string key) => _by[key];
    public bool Bool(string key) => Get(key).Value.Equals("true", StringComparison.OrdinalIgnoreCase);
    public int Int(string key) => int.Parse(Get(key).Value, CultureInfo.InvariantCulture);
    public string Text(string key) => Get(key).Value;

    /// <summary>A <see cref="SettingKind.Text"/> setting read as the comma-separated list
    /// of whole numbers the loader already validated it to be (backoffSeconds). Empty when
    /// the value holds none — callers supply their own fallback, as the pre-settings code
    /// did for an empty array.</summary>
    public int[] IntList(string key) => Text(key)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(t => int.TryParse(t, System.Globalization.NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var n) ? n : (int?)null)
        .Where(n => n.HasValue)
        .Select(n => n!.Value)
        .ToArray();

    /// <summary>All built-in defaults — what a config with no settings anywhere resolves to.</summary>
    public static ResolvedSettings Defaults() => SettingsResolver.Resolve(
        new Dictionary<string, string>(), new Dictionary<string, string>());
}

public static class SettingsResolver
{
    /// <summary>Precedence: settings block > legacy top-level key > built-in default.
    /// A key present in both is resolved to the block AND reported once as a warning —
    /// reported, never silently resolved.</summary>
    public static ResolvedSettings Resolve(
        IReadOnlyDictionary<string, string> block,
        IReadOnlyDictionary<string, string> legacyTopLevel)
    {
        var all = new List<ResolvedSetting>();
        var warnings = new List<string>();
        foreach (var def in SettingsCatalog.All)
        {
            var inBlock = block.TryGetValue(def.Key, out var bv);
            var inLegacy = legacyTopLevel.TryGetValue(def.Key, out var lv);
            if (inBlock && inLegacy)
                warnings.Add($"settings: \"{def.Key}\" is set both top-level and in \"settings\" — using settings ({bv})");
            if (inBlock) all.Add(new ResolvedSetting(def, bv!, SettingSource.Settings));
            else if (inLegacy) all.Add(new ResolvedSetting(def, lv!, SettingSource.TopLevelLegacy));
            else all.Add(new ResolvedSetting(def, def.Default, SettingSource.Default));
        }
        return new ResolvedSettings(all, warnings);
    }
}

public sealed class SettingsException : Exception
{
    public IReadOnlyList<string> Errors { get; }
    public SettingsException(IReadOnlyList<string> errors)
        : base(string.Join(Environment.NewLine, errors)) { Errors = errors; }
}

public static class SettingsWriter
{
    static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    public static bool TrySet(string configPath, string key, string raw, out string error, out SettingDef? def)
    {
        error = ""; def = null;
        if (!SettingsCatalog.TryGet(key, out var d))
        {
            var near = SettingsCatalog.Nearest(key);
            error = near != null
                ? $"unknown setting \"{key}\" — did you mean \"{near}\"?"
                : $"unknown setting \"{key}\" (see: huddle --settings)";
            return false;
        }
        def = d;
        if (!SettingsLoader.TryParseRaw(d, raw, out var value, out var why))
        {
            error = $"{d.Key} — {why}";
            return false;
        }
        return Rewrite(configPath, values => values[d.Key] = value, out error);
    }

    public static bool TryUnset(string configPath, string key, out string error)
    {
        error = "";
        if (!SettingsCatalog.TryGet(key, out var d))
        {
            error = $"unknown setting \"{key}\" (see: huddle --settings)";
            return false;
        }
        return Rewrite(configPath, values => values.Remove(d.Key), out error);
    }

    /// <summary>Read, validate, mutate the settings map, re-serialise with every other
    /// top-level property carried through untouched. A file that fails to load is
    /// REFUSED, not overwritten — rewriting now would discard whatever else the operator
    /// has in there along with the problem.</summary>
    static bool Rewrite(string configPath, Action<Dictionary<string, string>> mutate, out string error)
    {
        error = "";
        if (!File.Exists(configPath)) { error = $"config not found: {configPath}"; return false; }

        JsonDocument doc;
        try
        {
            // Same option set the loader uses, so the writer can never accept a file the
            // loader would reject (S4).
            doc = JsonDocument.Parse(File.ReadAllText(configPath), HuddleConfig.ReadOptions);
        }
        catch (JsonException ex) { error = $"{configPath}: not valid JSON ({ex.Message})"; return false; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = $"{configPath}: top level must be a JSON object"; return false; }

            JsonElement? block = root.TryGetProperty("settings", out var b) ? b : null;
            var probe = SettingsLoader.Load(block, Path.GetFileName(configPath));
            if (probe.Errors.Count > 0) { error = probe.Errors[0]; return false; }

            var values = new Dictionary<string, string>(probe.Values, StringComparer.OrdinalIgnoreCase);
            mutate(values);

            // Rebuild: every existing top-level property in original order, "settings" replaced
            // (or appended) with the validated map in catalog order.
            var ordered = new Dictionary<string, object?>();
            foreach (var prop in root.EnumerateObject())
                ordered[prop.Name] = prop.Name == "settings" ? null : JsonSerializer.Deserialize<object>(prop.Value.GetRawText());
            var settingsObj = new Dictionary<string, object>();
            foreach (var def in SettingsCatalog.All)
            {
                if (!values.TryGetValue(def.Key, out var v)) continue;
                settingsObj[def.Key] = def.Kind switch
                {
                    SettingKind.Bool => v.Equals("true", StringComparison.OrdinalIgnoreCase),
                    SettingKind.Int => int.Parse(v, CultureInfo.InvariantCulture),
                    _ => v
                };
            }
            if (settingsObj.Count > 0) ordered["settings"] = settingsObj;
            else ordered.Remove("settings");

            try
            {
                File.WriteAllText(configPath, JsonSerializer.Serialize(ordered, WriteOpts) + Environment.NewLine,
                    new System.Text.UTF8Encoding(false));
                return true;
            }
            catch (Exception ex) { error = $"cannot write {configPath}: {ex.Message}"; return false; }
        }
    }
}
