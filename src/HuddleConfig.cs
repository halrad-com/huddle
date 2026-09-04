using System.Text.Json;
using System.Text.Json.Serialization;

namespace Huddle;

public class SessionDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("root")]
    public string Root { get; set; } = "";

    [JsonPropertyName("purpose")]
    public string Purpose { get; set; } = "";

    [JsonPropertyName("autoStart")]
    public bool AutoStart { get; set; }

    [JsonPropertyName("paths")]
    public Dictionary<string, string>? Paths { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("autoRestart")]
    public bool? AutoRestart { get; set; }

    [JsonPropertyName("maxAutoRestarts")]
    public int? MaxAutoRestarts { get; set; }

    [JsonPropertyName("backoffSeconds")]
    public int[]? BackoffSeconds { get; set; }

    [JsonPropertyName("aliases")]
    public string[]? Aliases { get; set; }

    // Capture-to-test replay target — the running instance the `replay` verb hits for this
    // repo. Host+port (mbxhval takes --host/--port, no --base-url). Use a literal IP like
    // 127.0.0.1, not "localhost" (mbxhval warns localhost can resolve to IPv6 ::1).
    [JsonPropertyName("replayHost")]
    public string? ReplayHost { get; set; }

    [JsonPropertyName("replayPort")]
    public int? ReplayPort { get; set; }

    // Generic replay runner — takes precedence over mbxhval host/port when set.
    // The literal token {output} is replaced with a temp summary path; the command
    // must write {"summary":{"total":N,"passed":N,"failed":N,"skipped":N}} there.
    [JsonPropertyName("replayCommand")]
    public string? ReplayCommand { get; set; }

    [JsonPropertyName("replayWorkingDir")]
    public string? ReplayWorkingDir { get; set; }

    /// <summary>
    /// G5 (wiring gate): command the `census <repo>` verb runs in this repo's root —
    /// typically its own wiring-census test filter. Read by ConsoleUI.HandleCensus.
    /// </summary>
    [JsonPropertyName("censusCommand")]
    public string? CensusCommand { get; set; }
}

public class GroupMember
{
    [JsonPropertyName("repo")]
    public string Repo { get; set; } = "";

    [JsonPropertyName("persona")]
    public string? Persona { get; set; }

    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }
}

// The nine settable properties below (contextFile, ipc, crashLogRetention,
// rescanIntervalSeconds, reclaimResourcesOnStop, seedPermissions, autoRestart,
// maxAutoRestarts, backoffSeconds) are the LEGACY TOP-LEVEL TIER. They exist so an older
// huddle.json keeps working, and they are read in exactly one place: LegacyTopLevelValues,
// which feeds them to SettingsResolver as the fallback below the "settings" block.
//
// Do not read them anywhere else. Runtime behaviour comes from config.Settings, or the
// "settings" block is silently ignored and every surface reports a value the code is not
// using (S1, review 2026-08-22).
public class HuddleConfig
{
    [JsonPropertyName("sessions")]
    public List<SessionDefinition> Sessions { get; set; } = new();

    [JsonPropertyName("claudePath")]
    public string? ClaudePath { get; set; }

    // Path to a built mbxhval (the capture-to-test runner) — an .exe, or the .dll
    // (run via `dotnet <dll>`). Used by the `replay` verb.
    [JsonPropertyName("mbxhvalPath")]
    public string? MbxhvalPath { get; set; }

    [JsonPropertyName("contextFile")]
    public bool ContextFile { get; set; } = true;

    [JsonPropertyName("ipc")]
    public bool Ipc { get; set; } = true;

    [JsonPropertyName("crashLogRetention")]
    public int CrashLogRetention { get; set; } = 10;

    /// <summary>
    /// Seconds between periodic rescans of the orchestrator command inbox — a
    /// backstop for FileSystemWatcher events Windows silently drops at runtime
    /// (B012). Set to 0 or negative to disable the timer and rely on the live
    /// watcher plus the manual `scan` verb only. Default 30.
    /// </summary>
    [JsonPropertyName("rescanIntervalSeconds")]
    public int RescanIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// When a session stops with uncleaned entries in its resource ledger
    /// (ipc/resledger/&lt;safe-name&gt;.json, see docs/resource-ledger-spec.md),
    /// huddle always reports the leak. If this is true it ALSO executes each
    /// entry's recorded cleanup command. Default false — report-only; the
    /// operator decides what dies (B016/I004).
    /// </summary>
    [JsonPropertyName("reclaimResourcesOnStop")]
    public bool ReclaimResourcesOnStop { get; set; }

    /// <summary>
    /// Seed each registered repo's .claude/settings.local.json with the standing
    /// permission allow-set at startup (merge-only, backup before modify). Default
    /// true — operator decision 2026-08-09 after repeated permission-prompt pain;
    /// set false to manage allowlists by hand. (I010 F4)
    /// </summary>
    [JsonPropertyName("seedPermissions")]
    public bool SeedPermissions { get; set; } = true;

    [JsonPropertyName("autoRestart")]
    public bool AutoRestart { get; set; }

    [JsonPropertyName("maxAutoRestarts")]
    public int MaxAutoRestarts { get; set; } = 3;

    [JsonPropertyName("backoffSeconds")]
    public int[] BackoffSeconds { get; set; } = [2, 5, 15];

    [JsonPropertyName("groups")]
    public Dictionary<string, List<GroupMember>>? Groups { get; set; }

    /// <summary>Raw "settings" block; validated by SettingsLoader in Load. Never read
    /// directly — use <see cref="Settings"/>.</summary>
    [JsonPropertyName("settings")]
    public JsonElement? SettingsRaw { get; set; }

    [JsonIgnore]
    public ResolvedSettings Settings { get; private set; } = ResolvedSettings.Defaults();

    /// <summary>The nine pre-settings top-level keys, as strings, ONLY when the JSON
    /// actually carried them. Used as the legacy fallback tier.</summary>
    public Dictionary<string, string> LegacyTopLevelValues(JsonElement root)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in SettingsCatalog.All)
        {
            if (!root.TryGetProperty(def.Key, out var el)) continue;
            string? v = el.ValueKind switch
            {
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Number => el.GetRawText(),
                JsonValueKind.String => el.GetString(),
                JsonValueKind.Array when def.Key == "backoffSeconds" =>
                    string.Join(",", el.EnumerateArray().Select(x => x.GetRawText())),
                _ => null
            };
            if (v != null) d[def.Key] = v;
        }
        return d;
    }

    /// <summary>
    /// Resolve effective auto-restart config for a session (session overrides ?? global
    /// defaults). The global tier comes from <see cref="Settings"/>, never from the legacy
    /// POCO properties: those are the resolver's fallback INPUT, and reading them here
    /// would silently ignore the "settings" block (S1). Per-session overrides still win —
    /// they are out of scope for settings by design (spec section 10).
    /// </summary>
    public (bool Enabled, int Max, int[] Backoff) GetAutoRestartConfig(SessionDefinition session)
    {
        var enabled = session.AutoRestart ?? Settings.Bool("autoRestart");
        var max = session.MaxAutoRestarts ?? Settings.Int("maxAutoRestarts");
        var backoff = session.BackoffSeconds ?? Settings.IntList("backoffSeconds");
        if (backoff.Length == 0) backoff = [2];
        return (enabled, max, backoff);
    }

    /// <summary>
    /// One option set for every read of a huddle.json, used by the deserializer here and
    /// mirrored by <see cref="JsonDocumentOptions"/> in <see cref="ReadOptions"/> and in
    /// SettingsWriter. Comments and trailing commas are ACCEPTED — the writer already
    /// tolerated them, and a loader stricter than the writer means `--set` can rewrite a
    /// file the loader then refuses with a raw JsonException (S4).
    /// </summary>
    public static readonly JsonSerializerOptions ParseOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>The <see cref="ParseOptions"/> rules, for the raw-document readers.</summary>
    public static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static HuddleConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Config not found: {path}");

        var json = File.ReadAllText(path);
        var cfg = JsonSerializer.Deserialize<HuddleConfig>(json, ParseOptions)
            ?? throw new InvalidOperationException("Failed to parse config");

        using var doc = JsonDocument.Parse(json, ReadOptions);
        var label = Path.GetFileName(path);
        var loaded = SettingsLoader.Load(cfg.SettingsRaw, label);
        if (loaded.Errors.Count > 0) throw new SettingsException(loaded.Errors);
        cfg.Settings = SettingsResolver.Resolve(loaded.Values, cfg.LegacyTopLevelValues(doc.RootElement));
        return cfg;
    }

    public string ResolveClaudePath()
    {
        if (!string.IsNullOrEmpty(ClaudePath) && File.Exists(ClaudePath))
            return ClaudePath;

        // Search PATH for claude / claude.exe
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            var candidate = Path.Combine(dir, "claude.exe");
            if (File.Exists(candidate))
                return candidate;

            candidate = Path.Combine(dir, "claude");
            if (File.Exists(candidate))
                return candidate;
        }

        // Check common install locations
        var localBin = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "bin", "claude.exe");
        if (File.Exists(localBin))
            return localBin;

        throw new FileNotFoundException("Could not find claude.exe. Set claudePath in huddle.json.");
    }
}
