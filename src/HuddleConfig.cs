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

    /// <summary>
    /// Resolve effective auto-restart config for a session (session overrides ?? global defaults).
    /// </summary>
    public (bool Enabled, int Max, int[] Backoff) GetAutoRestartConfig(SessionDefinition session)
    {
        var enabled = session.AutoRestart ?? AutoRestart;
        var max = session.MaxAutoRestarts ?? MaxAutoRestarts;
        var backoff = session.BackoffSeconds ?? BackoffSeconds;
        if (backoff.Length == 0) backoff = [2];
        return (enabled, max, backoff);
    }

    public static HuddleConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Config not found: {path}");

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<HuddleConfig>(json)
            ?? throw new InvalidOperationException("Failed to parse config");
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
