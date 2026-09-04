using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Huddle;

public class SessionManager
{
    private readonly Dictionary<string, SessionDefinition> _repos = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SessionInstance> _instances = new(StringComparer.OrdinalIgnoreCase);
    private readonly HuddleConfig _config;
    private readonly string _claudePath;
    private readonly string _dataDir;
    private readonly string _personasDir;
    private readonly string? _contextPath;
    private readonly Action<string> _log;

    public IReadOnlyDictionary<string, SessionDefinition> Repos => _repos;
    public IReadOnlyDictionary<string, SessionInstance> Instances => _instances;

    // I010: dead sessions retained from state.json at recovery — the crash-recovery
    // roster the `recover` verb lists. Populated by SessionState.Recover; entries
    // leave only via `recover <n>` / `recover dismiss` (archived, never deleted).
    public List<SessionStateEntry> Recoverable { get; } = new();

    // I010 F5: prepended to every ORCHESTRATOR-dispatched task prompt (dispatch-batch,
    // start-session, delegate-task). Operator-typed starts get these rules via the
    // persona prompt (_shared.md Shell Discipline); dispatched contexts historically
    // arrived without them and were the permission-prompt offenders (2026-08-09).
    public const string ShellDisciplinePreamble =
        "SHELL RULES (mandatory): one command per Bash call — no ';', '&&', pipes, or " +
        "shell variables/$-expansion; write literal absolute paths; prefer Read/Grep/Glob " +
        "tools over cat/grep/find/ls; never 'cd' — use git -C / absolute paths. " +
        "If you dispatch subagents, repeat these rules verbatim in their prompts.\n\n";
    public HuddleConfig Config => _config;
    public string DataDir => _dataDir;
    public IpcManager? Ipc { get; set; }

    public event Action<SessionInstance, SessionStatus>? SessionStateChanged;

    public SessionManager(HuddleConfig config, string claudePath, string dataDir, string personasDir, string? contextPath, Action<string> log)
    {
        _config = config;
        _claudePath = claudePath;
        _dataDir = dataDir;
        _personasDir = personasDir;
        _contextPath = contextPath;
        _log = log;
    }

    // The mail-delivery hook script. Runs on Stop (session finished a turn) and
    // UserPromptSubmit (operator submitted). It atomically claims the session's
    // pending-context file and emits its lines back to Claude: on Stop as a
    // decision:block reason (waking a new turn to process the mail), on submit as
    // additionalContext (folded onto the operator's own prompt). Draining the
    // file each time makes it self-terminating — no stop_hook_active loop.
    // ASCII-only (PowerShell 5.1 chokes on non-ASCII in .ps1).
    private const string MailHookScript = @"# huddle mail-delivery hook - written by huddle SessionManager; do not edit.
$ErrorActionPreference = 'SilentlyContinue'
# Read the pending file as UTF-8 and answer in UTF-8: PS 5.1 defaults both to the
# console codepage, which mangles the non-ASCII characters in wake lines.
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$raw = [Console]::In.ReadToEnd()
$in = $null
try { $in = $raw | ConvertFrom-Json } catch { }
$evt = ''
if ($in) { $evt = [string]$in.hook_event_name }

$pending = $env:HUDDLE_PENDING
if ($pending) { $pending = $pending.Trim() }
if (-not $pending) { exit 0 }
$claim = ""$pending.draining""

# Claim atomically: absorb any orphaned claim from a crashed prior drain, then
# rename the live file aside so a concurrent huddle append lands in a fresh one.
$content = ''
if (Test-Path -LiteralPath $claim) {
    $content += (Get-Content -LiteralPath $claim -Raw -Encoding UTF8)
    Remove-Item -LiteralPath $claim -Force
}
if (Test-Path -LiteralPath $pending) {
    try {
        Move-Item -LiteralPath $pending -Destination $claim -Force
        $content += (Get-Content -LiteralPath $claim -Raw -Encoding UTF8)
        Remove-Item -LiteralPath $claim -Force
    } catch { }
}

if (-not $content) { exit 0 }
$content = $content.TrimEnd()
if ($content.Length -eq 0) { exit 0 }

# Split drained lines into two lanes. Info lines carry a leading SOH (0x01)
# sentinel written by huddle for ack/nack replies: they must NOT block the stop,
# because a blocked stop renders as a red 'Stop hook error' in the CLI and an ack
# is not an error. Actionable lines (real mail nudges) still block, to wake the
# session into a fresh turn.
$actionable = @()
$info = @()
foreach ($line in ($content -split ""`n"")) {
    $t = $line.TrimEnd([char]13)
    if ($t.Length -eq 0) { continue }
    if ($t[0] -eq [char]1) { $info += $t.Substring(1) } else { $actionable += $t }
}
$actionText = ($actionable -join ""`n"")
$infoText = ($info -join ""`n"")

if ($evt -eq 'Stop') {
    if ($actionText.Length -gt 0) {
        # A wake is warranted anyway, so fold any info in with the block reason.
        $reason = $actionText
        if ($infoText.Length -gt 0) { $reason = $reason + ""`n"" + $infoText }
        $out = @{ decision = 'block'; reason = $reason }
    } elseif ($infoText.Length -gt 0) {
        # Info only: let the stop proceed and surface it quietly, not as an error.
        $out = @{ hookSpecificOutput = @{ hookEventName = $evt; additionalContext = $infoText } }
    } else {
        exit 0
    }
} else {
    $all = $actionText
    if ($infoText.Length -gt 0) {
        if ($all.Length -gt 0) { $all = $all + ""`n"" + $infoText } else { $all = $infoText }
    }
    $out = @{ hookSpecificOutput = @{ hookEventName = $evt; additionalContext = $all } }
}
$out | ConvertTo-Json -Compress -Depth 5
exit 0
";

    // Write the mail hook script under <configDir>\hooks (idempotent overwrite,
    // so it always matches this huddle build). Returns the absolute script path.
    private string WriteMailHookScript(string configDir)
    {
        var hooksDir = Path.Combine(configDir, "hooks");
        Directory.CreateDirectory(hooksDir);
        var scriptPath = Path.Combine(hooksDir, "huddle-mail-hook.ps1");
        File.WriteAllText(scriptPath, MailHookScript);
        return scriptPath;
    }

    // Write a per-session settings file wiring both hooks to the script. Scoped
    // via `--settings <file>`; nothing global is touched. JsonSerializer handles
    // escaping the Windows path inside the command string.
    private static void WriteMailHookSettings(string settingsFile, string scriptPath)
    {
        var command = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"";
        var hookEntry = new[]
        {
            new { matcher = "", hooks = new[] { new { type = "command", command } } }
        };
        // The claim guard: every Edit/Write in a registered repo must be covered by a
        // claim this session holds, or the tool is refused with the claim command to run.
        // Enforcement, not prose (I015 / 2026-08-22 netlib collision). The guard fails
        // OPEN on its own errors, and never gates ipc/, logs/, .claude/, hooks/.
        var huddleExe = Environment.ProcessPath ?? "huddle";
        var guardEntry = new[]
        {
            new { matcher = "Edit|Write|MultiEdit|NotebookEdit",
                  hooks = new[] { new { type = "command", command = $"\"{huddleExe}\" --claim-check" } } }
        };
        var settings = new
        {
            hooks = new Dictionary<string, object>
            {
                ["Stop"] = hookEntry,
                ["UserPromptSubmit"] = hookEntry,
                ["PreToolUse"] = guardEntry,
            }
        };
        File.WriteAllText(settingsFile,
            JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }

    // Escape a string for use as a double-quoted argument on a cmd.exe /c line.
    // Two parsers run: cmd.exe handles the outer line, then CommandLineToArgvW
    // (Microsoft's standard runtime parser, which claude uses) unquotes the
    // argument for the child process. Inside double quotes, cmd treats the
    // shell metachars `& | < > ( )` as literal, so they need no escaping. We
    // do need:
    //   - backslashes preceding a `"` doubled, per msvcrt rules
    //   - internal `"` escaped as `\"`
    //   - trailing backslashes doubled so the closing quote isn't escaped
    /// <summary>
    /// A task prompt rides to claude as ONE quoted positional argument through cmd.exe,
    /// and cmd ends the command line at the first newline — everything after it is
    /// silently dropped. ShellDisciplinePreamble ends in "\n\n", so from 5dea3fb
    /// (2026-08-09) every orchestrator-dispatched session received the preamble and
    /// nothing else: the task never arrived. Paragraph breaks become " | " so the
    /// structure survives; single line breaks become a space.
    /// </summary>
    public static string FlattenForCommandLine(string s)
    {
        var t = s.Replace("\r\n", "\n").Replace('\r', '\n');
        while (t.Contains("\n\n\n")) t = t.Replace("\n\n\n", "\n\n");
        return t.Replace("\n\n", " | ").Replace('\n', ' ').Trim();
    }

    public static string EscapeForCmdQuoted(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 8);
        int backslashes = 0;
        foreach (var ch in s)
        {
            if (ch == '\\')
            {
                backslashes++;
                continue;
            }
            if (ch == '"')
            {
                // Double all preceding backslashes, then add one more before the escaped quote.
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
                backslashes = 0;
                continue;
            }
            // Non-special char: flush backslashes literally.
            if (backslashes > 0)
            {
                sb.Append('\\', backslashes);
                backslashes = 0;
            }
            sb.Append(ch);
        }
        // Trailing backslashes must be doubled so the closing quote stays a closing quote.
        if (backslashes > 0)
            sb.Append('\\', backslashes * 2);
        return sb.ToString();
    }

    public string[] GetAvailablePersonas()
    {
        if (!Directory.Exists(_personasDir))
            return [];
        return Directory.GetFiles(_personasDir, "*.md")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n != null && !n.StartsWith("_"))
            .Select(n => n!)
            .OrderBy(n => n)
            .ToArray();
    }

    public PersonaConfig GetPersonaTuning(string persona)
        => PersonaConfigLoader.LoadAndMerge(_personasDir, persona);

    public string BuildBasePrompt(SessionInstance instance)
    {
        var parts = new List<string>();

        // Session context
        parts.Add($"Your current session is '{instance.InstanceId}' (repo: {instance.RepoName}). Working directory: {instance.Root}");
        if (!string.IsNullOrEmpty(instance.Purpose))
            parts.Add($"Project context: {instance.Purpose}");

        // Project paths
        var paths = instance.Definition.Paths;
        if (paths != null && paths.Count > 0)
        {
            var pathLines = new List<string> { "Project paths:" };
            foreach (var (key, value) in paths)
                pathLines.Add($"- {key}: {value}");
            parts.Add(string.Join("\n", pathLines));
        }

        // Project notes — free-form context about how things work
        if (!string.IsNullOrEmpty(instance.Definition.Notes))
            parts.Add($"Project notes:\n{instance.Definition.Notes}");

        // Cross-session awareness
        if (_contextPath != null)
            parts.Add($"Cross-session awareness: Other huddle sessions are tracked in {_contextPath}.\nRead this file to see what other sessions are running and their current state.");

        // IPC mailbox paths
        if (Ipc != null)
        {
            var (inbox, outbox) = Ipc.GetMailboxPaths(instance.SafePathName);
            var ipcLines = new List<string>
            {
                "## Inter-Session Communication (IPC)",
                "",
                $"Your inbox: {inbox}",
                $"Your outbox: {outbox}",
                $"IPC root: {Ipc.IpcDir}",
                "",
                $"To message another session, write a JSON file to {Ipc.IpcDir}/<target-safe-name>/inbox/:",
                $"Filename: NNN-from-{instance.SafePathName}-<timestamp>.json",
                "{\"from\":\"your-id\",\"to\":\"target-id\",\"timestamp\":\"ISO8601\",\"type\":\"request|info|status|task|command\",\"subject\":\"...\",\"body\":{...}}",
                "",
                "Check your inbox for incoming messages. Other active sessions are listed in the context file above."
            };
            parts.Add(string.Join("\n", ipcLines));

            // Orchestration commands
            var huddleInboxPath = Path.Combine(Ipc.IpcDir, Orchestrator.HuddleMailbox, "inbox");
            var orchLines = new List<string>
            {
                "## Orchestration Commands",
                "",
                $"To control the session manager, write command files to: {huddleInboxPath}",
                "Use the same JSON format as IPC messages, but set type: \"command\".",
                "",
                "Available commands (set as \"subject\"):",
                "  start-session — body: {\"repo\":\"repo-name-or-alias\",\"persona\":\"name\",\"prompt\":\"task\"}",
                "  stop-session — body: {\"instanceId\":\"repo:persona\"} (use repo name or alias, e.g. \"myapp:architect\")",
                "  delegate-task — body: {\"description\":\"what\",\"assignTo\":\"repo:persona\",\"startIfNeeded\":true}",
                "  task-complete — body: {\"taskId\":\"T001\",\"notes\":\"what was done\"}",
                "  task-failed — body: {\"taskId\":\"T001\",\"notes\":\"why\"}",
                "  task-progress — body: {\"taskId\":\"T001\",\"notes\":\"update\"}",
                "  broadcast — body: {\"subject\":\"...\",\"body\":\"...\",\"type\":\"info\",\"targets\":\"all\"|[...],\"exclude\":[...],\"repo\":\"name-or-alias[,more]\" (optional — only that repo's agents)}",
                "  dispatch-batch — body: {\"batchId\":\"B-...\",\"tasks\":[{\"repo\":\"...\",\"persona\":\"...\",\"prompt\":\"...\",\"files\":[\"...\"]}]}",
                "  claim — body: {\"repo\":\"name-or-alias\",\"files\":[\"path/to/file\"]} (MANDATORY before substantive edits unless your work arrived via dispatch-batch; include the plan doc's path to lock a whole plan; ack:claim = yours, nack:claim = someone else holds it — do NOT edit, coordinate by mail)",
                "  release — body: {\"files\":[\"path/to/file\"]}",
                "",
                "Responses arrive in your inbox as type: \"info\". Subject \"ack:<command>\" = accepted; subject \"nack:<command>\" = rejected (body is the reason). Always check the prefix before assuming a command succeeded."
            };
            parts.Add(string.Join("\n", orchLines));

            // Work ledger path
            parts.Add($"Work ledger directory: {Ipc.WorkLedgerDir}\nWrite your claim file here as <your-safe-name>.md when starting work. Read other files here to check for conflicts.");
        }

        // §5.7: what this session owes, rendered FRESH at every spawn and resume. A
        // notification is a moment and can be missed; a standing fact is present every
        // time the session looks. The two features dropped on 2026-08-21 could not have
        // survived a day of being told this at every wake.
        var owed = Obligations.ContextSection(
            Obligations.For(instance.InstanceId, _config.Sessions.Select(s => (s.Name, s.Root)), DateTimeOffset.Now),
            DateTimeOffset.Now);
        if (owed.Length > 0) parts.Add(owed);

        // Scratchpad
        var scratchpadPath = GetScratchpadPath(instance);
        parts.Add($"Scratchpad: {scratchpadPath}\nWrite checkpoint notes here as you work — decisions made, things found, issues hit, state of progress. Include the git commit hash with each checkpoint.");

        // If scratchpad has existing content (crash recovery), include it
        if (File.Exists(scratchpadPath))
        {
            var existing = File.ReadAllText(scratchpadPath).Trim();
            if (existing.Length > 0)
                parts.Add($"Previous scratchpad content (from prior session):\n{existing}");
        }

        return string.Join("\n\n", parts);
    }

    public string? BuildPersonaPrompt(string? persona, SessionInstance instance)
    {
        if (string.IsNullOrEmpty(persona))
            return null;

        var personaFile = Path.Combine(_personasDir, $"{persona}.md");
        if (!File.Exists(personaFile))
        {
            _log($"Unknown persona: {persona}. Available: {string.Join(", ", GetAvailablePersonas())}");
            return null;
        }

        var parts = new List<string>();

        // Shared rules first
        var sharedFile = Path.Combine(_personasDir, "_shared.md");
        if (File.Exists(sharedFile))
            parts.Add(File.ReadAllText(sharedFile).Trim());

        // Persona
        parts.Add(File.ReadAllText(personaFile).Trim());

        // Base prompt (session context, paths, IPC, scratchpad)
        parts.Add(BuildBasePrompt(instance));

        return string.Join("\n\n", parts);
    }

    public string GetScratchpadPath(SessionInstance instance)
    {
        var logDir = Path.Combine(_dataDir, instance.SafePathName);
        return Path.Combine(logDir, "scratchpad.md");
    }

    public void Register(SessionDefinition def)
    {
        if (_repos.ContainsKey(def.Name))
        {
            _log($"Repo '{def.Name}' already registered, skipping.");
            return;
        }
        _repos[def.Name] = def;

        if (def.Aliases != null)
        {
            foreach (var alias in def.Aliases)
            {
                if (string.IsNullOrWhiteSpace(alias)) continue;
                if (_repos.ContainsKey(alias) || _aliases.ContainsKey(alias))
                {
                    _log($"Alias '{alias}' conflicts with an existing repo or alias, skipping.");
                    continue;
                }
                _aliases[alias] = def.Name;
            }
        }
    }

    /// <summary>
    /// Resolve a name that might be an alias to the canonical repo name.
    /// Returns the canonical name, or the input unchanged if no alias match.
    /// </summary>
    public string ResolveRepoName(string name)
    {
        if (_repos.ContainsKey(name)) return name;
        if (_aliases.TryGetValue(name, out var canonical)) return canonical;
        return name;
    }

    /// <summary>True if the (canonical) name is a registered repo.</summary>
    public bool IsKnownRepo(string name) => _repos.ContainsKey(name);

    private string GenerateInstanceId(string repoName, string? persona)
    {
        var baseId = persona != null ? $"{repoName}:{persona}" : repoName;
        if (!_instances.ContainsKey(baseId)) return baseId;

        // If existing instance with this ID is stopped/crashed, reuse the slot
        if (_instances.TryGetValue(baseId, out var existing) && !existing.IsAlive)
            return baseId;

        for (int i = 2; ; i++)
        {
            var id = $"{baseId}-{i}";
            if (!_instances.ContainsKey(id)) return id;
            if (_instances.TryGetValue(id, out var ex) && !ex.IsAlive)
                return id;
        }
    }

    /// <summary>
    /// False until <see cref="SessionState.Recover"/> has run. While false the roster is
    /// KNOWN INCOMPLETE — sessions that survived a reload are alive but not yet re-adopted
    /// — so nothing may persist it and nothing may conclude from its absences.
    /// </summary>
    public bool RecoveryComplete { get; set; }

    /// <summary>Absolute path to state.json; set by the host so spawn guards can consult
    /// the on-disk roster rather than trusting only what is in memory.</summary>
    public string? StateFile { get; set; }

    /// <summary>
    /// A session with this identity is ALIVE on disk but absent from the in-memory roster.
    /// That combination is what produced two `otherapp:architect` sessions on 2026-08-23:
    /// a reload left the roster empty, `startIfNeeded` saw no such instance, and a twin was
    /// started over a working session — same id, so the ledger saw one holder and the two
    /// shared a mailbox. Identity is verified the same way recovery verifies it (I009:
    /// Windows recycles PIDs), so a stale entry can never block a legitimate start.
    /// </summary>
    public bool IsLiveButUntracked(string instanceId, out int pid)
    {
        pid = 0;
        if (string.IsNullOrEmpty(StateFile) || !File.Exists(StateFile)) return false;
        if (_instances.TryGetValue(instanceId, out var tracked) && tracked.IsAlive) return false;
        try
        {
            var entries = JsonSerializer.Deserialize<List<SessionStateEntry>>(File.ReadAllText(StateFile)) ?? [];
            foreach (var e in entries)
            {
                if (!string.Equals(e.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase)) continue;
                if (e.Status != "live") continue;
                using var proc = System.Diagnostics.Process.GetProcessById(e.Pid);
                if (proc.HasExited) continue;
                if (!SessionState.IdentityMatches(e, proc.StartTime, proc.ProcessName)) continue;
                pid = e.Pid;
                return true;
            }
        }
        catch { /* unreadable / dead PID / identity unavailable: never block a start */ }
        return false;
    }

    public bool Start(string repoName, string? persona = null, bool continueSession = false, string? prompt = null, string? project = null)
    {
        repoName = ResolveRepoName(repoName);
        if (!_repos.TryGetValue(repoName, out var def))
        {
            _log($"Unknown repo: {repoName}");
            return false;
        }

        if (!Directory.Exists(def.Root))
        {
            _log($"Root directory does not exist: {def.Root}");
            return false;
        }

        var instanceId = GenerateInstanceId(repoName, persona);

        // Refuse to start a twin. GenerateInstanceId only knows the in-memory roster, so
        // after a reload it will happily hand back an id whose session is still running.
        // Two agents on one `repo:persona` are invisible to each other in the claims ledger
        // and share one mailbox — the 2026-07-16 duplicate-work failure, recreated.
        if (IsLiveButUntracked(instanceId, out var livePid))
        {
            _log($"REFUSED to start '{instanceId}': a live session with that identity is already running (PID {livePid}) " +
                 "but is not in this huddle's roster — most likely it survived a reload. Nothing was started. " +
                 "Use `recover` to re-adopt it, or `resume` to reopen it; mail it rather than starting a second one.");
            return false;
        }

        // Reuse or create instance
        SessionInstance instance;
        var isNewInstance = false;
        if (_instances.TryGetValue(instanceId, out var existing))
        {
            instance = existing;
            lock (instance.Lock)
            {
                if (instance.IsAlive)
                {
                    _log($"Instance '{instanceId}' is already running.");
                    return false;
                }
                // Dispose previous process handle
                instance.Process?.Dispose();
                instance.Process = null;
                instance.SessionId = null;
                instance.PersonaTempFiles.Clear();
                instance.PersonaConfig = null;
                // New run, new task (I010 F3): don't let a stale purpose linger.
                instance.DeclaredPurpose = null;
                instance.Project = null;
            }
        }
        else
        {
            instance = new SessionInstance(instanceId, def);
            _instances[instanceId] = instance;
            isNewInstance = true;
        }

        // A newly-created instance is registered before the persona/config is
        // validated. Any failure below (unknown persona, bad config, process
        // launch error) returns early — without this guard the dead instance
        // lingers in _instances as a phantom "Stopped" entry with no persona,
        // which then surfaces in context.md. Reused instances are left alone:
        // they represent a real prior session and their Stopped state is valid.
        var started = false;
        try
        {
        lock (instance.Lock)
        {
            // I010 F3: remember what this session is FOR. The crash-recovery roster
            // shows this instead of requiring transcript forensics. Dispatched prompts
            // carry the shell-rules preamble (F5) — strip it so the purpose reads as
            // the task, not the boilerplate.
            if (!string.IsNullOrWhiteSpace(prompt))
                instance.DeclaredPurpose = prompt.StartsWith(ShellDisciplinePreamble, StringComparison.Ordinal)
                    ? prompt[ShellDisciplinePreamble.Length..]
                    : prompt;
            if (!string.IsNullOrWhiteSpace(project))
                instance.Project = project;

            // Build system prompt — persona sessions get persona + base, others get base only.
            // The task `prompt` is NOT part of the system prompt; it goes in as the first
            // user turn so claude actually starts working without waiting for keystrokes.
            string systemPrompt;
            if (!string.IsNullOrEmpty(persona))
            {
                _log($"Loading persona '{persona}' for '{instanceId}'...");
                var personaPrompt = BuildPersonaPrompt(persona, instance);
                if (personaPrompt == null)
                    return false; // Unknown persona, error already logged
                systemPrompt = personaPrompt;
                _log($"Persona '{persona}' loaded ({systemPrompt.Length} chars)");
            }
            else
            {
                systemPrompt = BuildBasePrompt(instance);
                _log($"Base prompt loaded ({systemPrompt.Length} chars)");
            }

            var sessionLogDir = Path.Combine(_dataDir, instance.SafePathName);
            Directory.CreateDirectory(sessionLogDir);

            // Ensure IPC mailbox and watcher
            Ipc?.EnsureMailbox(instance.SafePathName);
            Ipc?.Watch(instance.SafePathName, instance.InstanceId);

            // Build the claude command — always write system prompt file
            // Pre-assign the Claude session id so we can locate the JSONL log file
            // (~/.claude/projects/<encoded-cwd>/<session-id>.jsonl) without scanning.
            if (!continueSession)
                instance.SessionId = Guid.NewGuid();
            var claudeArgs = "";
            if (!continueSession)
                claudeArgs += $" --session-id {instance.SessionId!.Value}";
            if (!continueSession)
                _log($"[{instance.InstanceId}] Resume with: {instance.ResumeCommand}  (run in {instance.Root})");
            if (continueSession)
                claudeArgs += " --continue";
            var promptTempFile = Path.Combine(sessionLogDir, $"{instance.SafePathName}-persona.tmp");
            File.WriteAllText(promptTempFile, systemPrompt);
            claudeArgs += $" --append-system-prompt-file \"{promptTempFile}\"";

            // Load + materialize persona tuning (sidecar JSON). Missing files = no flags.
            var personasDir = _personasDir;
            try
            {
                var personaCfg = PersonaConfigLoader.LoadAndMerge(personasDir, persona);
                instance.PersonaConfig = personaCfg;
                var built = PersonaFlagBuilder.Build(personaCfg, sessionLogDir, instance.SafePathName);
                claudeArgs += built.Args;
                instance.PersonaTempFiles.AddRange(built.TempFiles);
                _log($"Persona tuning applied: model={personaCfg.Model ?? "default"} effort={personaCfg.Effort ?? "default"}");
            }
            catch (InvalidOperationException ex)
            {
                _log($"Persona config error: {ex.Message}");
                instance.Status = SessionStatus.Stopped;
                return false;
            }

            // Append the task prompt as a positional argument so claude treats it
            // as the first user turn. Without this, claude opens the prompt and
            // waits — system-prompt content alone never fires a turn, so the
            // session sits idle until someone types. Skip when prompt is empty
            // to preserve the interactive "open and wait" path.
            if (!string.IsNullOrEmpty(prompt))
                claudeArgs += $" \"{EscapeForCmdQuoted(FlattenForCommandLine(prompt))}\"";

            // Ledger context (see BuildLedgerEnvSet), so an agent can run
            // `huddle --claim <path>` with no arguments beyond the paths themselves.
            //
            // Deliberately OUTSIDE the mail-hook try/catch below. The ledger vars
            // need nothing from the hook setup, and folding them in there would make
            // a hook-file write failure silently strip a session's ledger identity —
            // every `huddle --claim` in it would then fail while the log talked only
            // about mail. Two unrelated features must not share one failure path.
            // Do not "tidy" this back into the block below.
            var ledgerEnvSet = BuildLedgerEnvSet(
                instance.InstanceId, instance.RepoName, instance.SessionId?.ToString() ?? "", instance.Root);

            // Mail-delivery hooks. Instead of typing wake lines into this console
            // (which stomped an operator's in-progress prompt), huddle appends them
            // to a per-session pending-context file; a Stop hook drains it into a
            // fresh turn when the session goes idle, and a UserPromptSubmit hook
            // folds any pending lines onto the operator's own next submit. Both are
            // scoped to this session via `--settings <file>` — nothing global is
            // touched. HUDDLE_PENDING tells the hook which file to drain.
            var hookPendingSet = "";
            if (Ipc != null)
            {
                try
                {
                    var configDir = Directory.GetParent(Ipc.IpcDir)!.FullName;
                    var scriptPath = WriteMailHookScript(configDir);
                    var settingsFile = Path.Combine(sessionLogDir, $"{instance.SafePathName}-hooks.json");
                    WriteMailHookSettings(settingsFile, scriptPath);
                    claudeArgs += $" --settings \"{settingsFile}\"";
                    // Quote the whole assignment: `set VAR=value && ...` captures
                    // everything up to the `&&`, trailing space included, and the
                    // hook's -LiteralPath lookups do not trim. The quotes are not
                    // part of the value.
                    hookPendingSet = $"set \"HUDDLE_PENDING={Ipc.PendingPath(instance.SafePathName)}\" && ";
                }
                catch (Exception ex)
                {
                    // Non-fatal: without hooks the session simply won't auto-receive
                    // mail as context (it can still read its inbox on demand).
                    _log($"[{instance.InstanceId}] mail hook setup failed (continuing without): {ex.Message}");
                }
            }

            // Git credential-request logging. Point this session's git at a
            // per-session system config that runs a logging helper
            // (huddle --cred-log) BEFORE GCM, so when an agent's git blocks on a
            // GitHub/Azure credential prompt — a pop-under the operator never sees
            // — huddle announces which session+repo is asking. The helper logs the
            // request and falls through to the real GCM for the actual auth; it
            // never sees the credential itself. Scoped to this session via
            // GIT_CONFIG_SYSTEM (an [include] preserves the real system config);
            // nothing global is touched. Non-fatal — without it the session just
            // won't surface its auth requests.
            var gitAuthSet = "";
            if (Ipc != null)
            {
                try
                {
                    var huddleExe = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(huddleExe))
                    {
                        var sessionGitConfig = Path.Combine(sessionLogDir, $"{instance.SafePathName}-gitconfig");
                        // Include the Claude session GUID so the auth line names the
                        // specific agent, not just its repo (several sessions can share
                        // a repo, and auto-started ones have no persona in instanceId).
                        var sessionId = instance.SessionId?.ToString() ?? "";
                        GitActivityMonitor.WriteCredentialLoggerConfig(
                            sessionGitConfig, huddleExe, GitHelper.SystemConfigPath(), instanceId, sessionId, Ipc.GitAuthDir);
                        // A file path is a non-empty value, so cmd's `set` holds it
                        // fine (unlike the empty-reset, which is why the reset lives
                        // inside the config file, not here).
                        gitAuthSet = $"set \"GIT_CONFIG_SYSTEM={sessionGitConfig}\" && ";
                    }
                }
                catch (Exception ex)
                {
                    _log($"[{instance.InstanceId}] git-auth logging setup failed (continuing without): {ex.Message}");
                }
            }

            // Env vars exported to the child Claude Code process:
            //   BUN_CRASH_REPORTER_URL — silenced so Bun crash dialogs stay inside this session.
            //   NOTE: this one assignment is deliberately NOT quoted, unlike every other `set`
            //   below. `set "VAR="` DELETES a variable in cmd — there is no way to define one as
            //   empty — and a deleted BUN_CRASH_REPORTER_URL means Bun falls back to its DEFAULT
            //   crash-reporter endpoint, i.e. an offline-first tool starts phoning home. The
            //   unquoted form assigns a single space, which is defined and non-empty, so the
            //   default never applies. Quoting it to "match the others" would restore the very
            //   behaviour it exists to prevent. Do not tidy it.
            //   CLAUDE_SESSION_LABEL  — literal statusline label (used by ~/.claude/statusline.ps1)
            //   CLAUDE_PERSONA        — persona name, for scripts/tools inside the session
            // The leading `title` makes the console window identifiable in Alt+Tab
            // and Task Manager, and lets the spawn-time window capture tell this
            // session's window from those of sessions started alongside it. Claude
            // Code replaces the title with the conversation topic once it is up, so
            // it is a launch-time discriminator only — see SessionWindow.
            var envPrefix = $"title huddle: {instanceId} && " +
                            "set BUN_CRASH_REPORTER_URL= && " +
                            $"set CLAUDE_SESSION_LABEL={instanceId} && " +
                            ledgerEnvSet +
                            hookPendingSet +
                            gitAuthSet;
            if (!string.IsNullOrEmpty(persona))
                envPrefix += $"set CLAUDE_PERSONA={persona} && ";

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {envPrefix}\"{_claudePath}\"{claudeArgs}",
                WorkingDirectory = def.Root,
                UseShellExecute = true,
            };

            try
            {
                instance.Status = SessionStatus.Starting;
                instance.ActivePersona = persona;

                // Snapshot the desktop before launching so the session's own console
                // window can be told apart from the ones already open.
                var windowsBefore = SessionWindow.Snapshot();

                var process = Process.Start(psi);
                if (process == null)
                {
                    _log($"Failed to start process for '{instanceId}'.");
                    instance.Status = SessionStatus.Stopped;
                    return false;
                }

                instance.Process = process;
                instance.StartedAt = DateTime.Now;
                instance.StoppedAt = null;
                instance.LastExitCode = null;
                instance.Status = SessionStatus.Running;

                var personaLabel = persona != null ? $" [{persona}]" : "";
                _log($"Started '{instanceId}'{personaLabel} (PID {process.Id}) in {def.Root}");
                SessionStateChanged?.Invoke(instance, SessionStatus.Running);

                // Monitor in background
                var proc = process;
                _ = Task.Run(() => MonitorProcess(instance, proc, sessionLogDir));

                // Find this session's console window in the background: the host takes
                // a moment to create it, and nothing here should block the spawn.
                _ = Task.Run(() => CaptureWindow(instance, windowsBefore));

                started = true;
                return true;
            }
            catch (Exception ex)
            {
                _log($"Error starting '{instanceId}': {ex.Message}");
                instance.Status = SessionStatus.Stopped;
                return false;
            }
        }
        }
        finally
        {
            if (!started && isNewInstance)
                _instances.Remove(instanceId);
        }
    }

    /// <summary>
    /// The ledger context a session needs to claim files, as a cmd `set` prefix. Used by
    /// every launch path — spawn AND both resumes — because resume is the recovery path
    /// taken after exactly the kind of outage the ledger exists to survive, and a session
    /// that cannot run `huddle --claim` is invisible to its peers in the same silent way
    /// the 2026-08-16 incident was (ISSUES.md I011).
    ///
    /// Exports, in order:
    /// <list type="bullet">
    /// <item>huddle's own directory PREPENDED to PATH, so bare `huddle --claim` resolves;</item>
    /// <item><c>HUDDLE_EXE</c>, the full executable path — the guaranteed form. The agent's
    /// Bash tool runs Git Bash initialised from the user's profile, and a login shell's
    /// /etc/profile can rebuild PATH from scratch and drop the prepended entry;</item>
    /// <item>ledger location + identity: <c>HUDDLE_CLAIMS</c> (absolute — the agent's cwd is
    /// its repo, not huddle's), <c>HUDDLE_INSTANCE</c>, <c>HUDDLE_REPO</c>, <c>HUDDLE_GUID</c>;</item>
    /// <item><c>HUDDLE_REPO_ROOT</c>, the session's ACTUAL checkout directory. A repo NAME
    /// cannot name a git worktree — `LIB-FEATURE` is a worktree of `LIB`, not a registered
    /// repo — so a session working in one had no way to say where it was, and its claims
    /// resolved to the wrong checkout for every reader (ISSUES.md I014). The root is recorded
    /// on the claim itself; the name stays for display and for the fallback.</item>
    /// </list>
    ///
    /// <see cref="Environment.ProcessPath"/>, not <c>AppContext.BaseDirectory</c>:
    /// PublishSingleFile is enabled (src/huddle.csproj), where BaseDirectory is not
    /// reliably the executable's directory. Same precedent as the credential logger below.
    ///
    /// With no IPC there is no ledger, so the four vars are explicitly CLEARED rather than
    /// left alone: the child inherits huddle's OWN environment, and an inherited
    /// HUDDLE_CLAIMS/HUDDLE_INSTANCE would file this session's claims under the parent
    /// instance's name — a claim that misnames its holder is worse than no claim. An empty
    /// assignment deletes the variable in cmd.
    ///
    /// Quote the whole assignment: `set VAR=value &amp;&amp; ...` captures everything up to the
    /// `&amp;&amp;`, trailing space included. The quotes are not part of the value. Quoting does
    /// NOT save a directory containing `%`, `&amp;` or `^` — `%` would expand mid-parse and the
    /// others are cmd metacharacters — so a huddle installed under such a path would break
    /// this prefix. `%PATH%` is left for cmd to expand at launch (it costs 6 characters of
    /// the command line rather than the whole expanded PATH).
    /// </summary>
    private string BuildLedgerEnvSet(string instanceId, string repoName, string sessionGuid, string repoRoot)
    {
        var set = "";
        var huddleExe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(huddleExe))
        {
            var huddleDir = Path.GetDirectoryName(huddleExe);
            if (!string.IsNullOrEmpty(huddleDir))
                set += $"set \"PATH={huddleDir};%PATH%\" && ";
            set += $"set \"HUDDLE_EXE={huddleExe}\" && ";
        }

        if (Ipc == null)
            return set +
                   "set \"HUDDLE_CLAIMS=\" && " +
                   "set \"HUDDLE_INSTANCE=\" && " +
                   "set \"HUDDLE_REPO=\" && " +
                   "set \"HUDDLE_REPO_ROOT=\" && " +
                   "set \"HUDDLE_GUID=\" && ";

        return set +
               $"set \"HUDDLE_CLAIMS={Ipc.ClaimsDir}\" && " +
               $"set \"HUDDLE_INSTANCE={instanceId}\" && " +
               $"set \"HUDDLE_REPO={repoName}\" && " +
               $"set \"HUDDLE_REPO_ROOT={repoRoot}\" && " +
               $"set \"HUDDLE_GUID={sessionGuid}\" && ";
    }

    /// <summary>
    /// Resolve an instance ID that may use a repo alias (e.g. "app:architect" → "myapp:architect").
    /// Returns null if no match found. Caller should typically try a direct
    /// <c>Instances.TryGetValue(id, ...)</c> first and fall back to this.
    /// </summary>
    public SessionInstance? ResolveInstance(string id)
    {
        var colonIdx = id.IndexOf(':');
        if (colonIdx <= 0) return null;

        var repoPart = id[..colonIdx];
        var personaPart = id[(colonIdx + 1)..];
        var resolved = ResolveRepoName(repoPart);
        var resolvedId = $"{resolved}:{personaPart}";
        if (resolvedId != id && _instances.TryGetValue(resolvedId, out var instance))
            return instance;
        return null;
    }

    public bool Stop(string id)
    {
        // Direct instance match
        if (_instances.TryGetValue(id, out var instance))
            return StopInstance(instance);

        // Try resolving repo alias in repo:persona format
        instance = ResolveInstance(id);
        if (instance != null)
            return StopInstance(instance);

        // No direct instance match — treat as repo name (resolve aliases), stop all instances of that repo
        id = ResolveRepoName(id);
        if (!_repos.ContainsKey(id))
        {
            _log($"Unknown instance or repo: {id}");
            return false;
        }

        var repoInstances = _instances.Values
            .Where(i => i.RepoName.Equals(id, StringComparison.OrdinalIgnoreCase) && i.IsAlive)
            .ToList();

        if (repoInstances.Count == 0)
        {
            _log($"No running instances for repo '{id}'.");
            return false;
        }

        _log($"Stopping {repoInstances.Count} instance(s) of '{id}'...");
        var allStopped = true;
        foreach (var inst in repoInstances)
            allStopped &= StopInstance(inst);
        return allStopped;
    }

    private bool StopInstance(SessionInstance instance)
    {
        Process? proc;
        lock (instance.Lock)
        {
            // Cancel any pending auto-restart first
            if (instance.AutoRestartCts != null)
            {
                instance.AutoRestartCts.Cancel();
                instance.AutoRestartCts.Dispose();
                instance.AutoRestartCts = null;
                instance.AutoRestartAt = null;
            }

            // If waiting for auto-restart (no live process), just mark stopped
            if (instance.Status == SessionStatus.AutoRestarting)
            {
                instance.Status = SessionStatus.Stopped;
                _log($"Cancelled auto-restart for '{instance.InstanceId}'.");
                SessionStateChanged?.Invoke(instance, SessionStatus.Stopped);
                return true;
            }

            if (!instance.IsAlive)
            {
                _log($"Instance '{instance.InstanceId}' is not running.");
                return false;
            }

            instance.Status = SessionStatus.Stopping;
            proc = instance.Process;
            var uptime = instance.FormatUptime();
            var personaLabel = instance.ActivePersona != null ? $" [{instance.ActivePersona}]" : "";
            _log($"Stopping '{instance.InstanceId}'{personaLabel} (PID {proc?.Id}, uptime {uptime})...");
        }

        // Stop the process outside the lock. The session is only marked Stopped
        // when the process has VERIFIABLY exited — a stop that fails to kill must
        // not report success, or the agent keeps working untracked and unclaimed
        // while huddle believes it's gone (2026-07-16 incident, ISSUES.md I007).
        var exited = false;
        if (proc != null)
        {
            try
            {
                proc.CloseMainWindow();
                if (!proc.WaitForExit(5000))
                {
                    _log($"Force-killing '{instance.InstanceId}'...");
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(3000);
                }
                exited = proc.HasExited;
            }
            catch (InvalidOperationException)
            {
                // Process already exited / handle no longer associated — that IS dead.
                exited = true;
            }
            catch (Exception ex)
            {
                _log($"Error stopping '{instance.InstanceId}': {ex.Message}");
                try { exited = proc.HasExited; } catch { exited = false; }
            }
        }

        if (!exited)
        {
            lock (instance.Lock)
            {
                instance.Status = SessionStatus.Running;
            }
            _log($"STOP FAILED: '{instance.InstanceId}' (PID {proc?.Id}) is still running — session stays tracked with its claims. Retry, or kill PID {proc?.Id} manually.");
            return false;
        }

        lock (instance.Lock)
        {
            instance.Status = SessionStatus.Stopped;
            instance.StoppedAt = DateTime.Now;
            try { instance.LastExitCode = proc?.ExitCode; } catch { }
            _log($"Stopped '{instance.InstanceId}' (exit code {instance.LastExitCode}).");

            // Release the window so a concurrent spawn can claim it if Windows
            // recycles the handle.
            instance.WindowHandle = IntPtr.Zero;
            instance.Process = null;
            proc?.Dispose();

            foreach (var t in instance.PersonaTempFiles)
            {
                try { if (File.Exists(t)) File.Delete(t); } catch { /* best effort */ }
            }
            instance.PersonaTempFiles.Clear();

            Ipc?.Unwatch(instance.SafePathName);

            // Clean up work ledger entry
            if (Ipc != null)
            {
                var ledgerFile = Path.Combine(Ipc.WorkLedgerDir, $"{instance.SafePathName}.md");
                if (File.Exists(ledgerFile))
                {
                    try { File.Delete(ledgerFile); }
                    catch (Exception ex) { _log($"Failed to clean up ledger file: {ex.Message}"); }
                }
            }

            SessionStateChanged?.Invoke(instance, SessionStatus.Stopped);
        }
        return true;
    }

    public bool Restart(string id)
    {
        if (!_instances.TryGetValue(id, out var instance))
        {
            // Try resolving repo alias in repo:persona format
            instance = ResolveInstance(id);
            if (instance == null)
            {
                _log($"Unknown instance: {id}. Use 'start <repo> [persona]' to create new instances.");
                return false;
            }
            id = instance.InstanceId;
        }

        var persona = instance.ActivePersona;
        var repoName = instance.RepoName;
        var wasCrashed = instance.Status == SessionStatus.Crashed || instance.Status == SessionStatus.AutoRestarting;
        var personaLabel = persona != null ? $" with persona '{persona}'" : "";
        _log($"Restarting '{id}'{personaLabel}{(wasCrashed ? " (crash recovery, --continue)" : "")}...");

        StopInstance(instance);
        instance.ConsecutiveAutoRestarts = 0;

        // Remove the old instance so Start can recreate with the same ID
        _instances.Remove(id);
        return Start(repoName, persona, continueSession: wasCrashed);
    }

    /// <summary>
    /// Recover a session from persisted state — attach to an existing process by PID.
    /// </summary>
    public bool Recover(string instanceId, string repoName, string? persona, Process proc, DateTime startedAt, Guid? sessionId = null, string? declaredPurpose = null, string? project = null)
    {
        repoName = ResolveRepoName(repoName);
        if (!_repos.TryGetValue(repoName, out var def))
            return false;

        if (_instances.ContainsKey(instanceId))
            return false; // Already tracked

        var instance = new SessionInstance(instanceId, def)
        {
            Process = proc,
            Status = SessionStatus.Running,
            StartedAt = startedAt,
            ActivePersona = persona,
            SessionId = sessionId,
            DeclaredPurpose = declaredPurpose,
            Project = project
        };

        _instances[instanceId] = instance;

        // Monitor in background
        var logDir = Path.Combine(_dataDir, instance.SafePathName);
        Directory.CreateDirectory(logDir);
        _ = Task.Run(() => MonitorProcess(instance, proc, logDir));

        // A recovered session has no spawn snapshot, but its console window still
        // carries its PID — capture it so `focus` works across a huddle restart.
        _ = Task.Run(() => TryCaptureWindowByPid(instance));

        SessionStateChanged?.Invoke(instance, SessionStatus.Running);
        return true;
    }

    /// <summary>
    /// Open `claude --resume &lt;session-id&gt;` for a tracked session in a fresh console,
    /// with the working directory set to the session's repo root (Claude keys session
    /// storage by cwd), and ADOPT the launched console back into the roster as that
    /// instance's live process (<see cref="AdoptResumed"/>).
    /// <para>Adoption is not a convenience: a resumed session does real work and claims
    /// files, and a claim whose owner is missing from the live roster is classified as an
    /// orphan and archived while the agent is still working. Consequences the operator
    /// approved: a resumed session appears in `status`, counts as live, and `stop` will
    /// kill it.</para>
    /// Returns false if the instance is unknown or carries no session id.
    /// </summary>
    public bool Resume(string id)
    {
        var instance = _instances.TryGetValue(id, out var direct) ? direct : ResolveInstance(id);
        if (instance == null)
        {
            _log($"resume: unknown instance '{id}'.");
            return false;
        }
        if (!instance.SessionId.HasValue)
        {
            _log($"resume: {instance.InstanceId} has no session id (started before session-id assignment, or a --continue session).");
            return false;
        }
        // Refuse to resume a session that's still alive: a second `claude` on the same
        // session-id + cwd means two live writers on one transcript JSONL, which forks
        // and can corrupt the conversation. Resume is for stopped/crashed sessions.
        // Adoption makes this guard STRONGER: a previously resumed session is now a live
        // instance, so a second resume of it is refused here instead of silently forking.
        if (instance.IsAlive)
        {
            _log($"resume: {instance.InstanceId} is still running — 'focus {instance.InstanceId}' to jump to it, or stop it first.");
            return false;
        }

        // Mirror the launch shape of Start: cmd.exe wrapper, identifiable title,
        // Bun crash reporter silenced, own console via UseShellExecute — and the same
        // ledger context, because a resumed session does real work and must be able to
        // claim it. The launched console is then adopted as this instance's process, so
        // the claim arbiter's live roster includes it: unadopted, its claims looked like
        // orphans and the reaper archived them mid-session — and because HandleClaim
        // reaps before computing overlaps, the next claimant on the same file archived
        // the resumed agent's claim and was told there was no overlap (a false all-clear).
        var envPrefix = $"title huddle-resume: {instance.InstanceId} && set BUN_CRASH_REPORTER_URL= && " +
                        BuildLedgerEnvSet(instance.InstanceId, instance.RepoName, instance.SessionId.Value.ToString(), instance.Root);
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c {envPrefix}\"{_claudePath}\" --resume {instance.SessionId.Value}",
            WorkingDirectory = instance.Root,
            UseShellExecute = true,
        };

        try
        {
            var proc = Process.Start(psi);
            if (proc == null)
            {
                // UseShellExecute can hand back null (the shell satisfied the request
                // without starting a new process we own). The console DID launch — only
                // adoption failed — so this is still a successful resume, with exactly
                // the tracking huddle had before adoption existed. Say so, loudly.
                _log($"resume: launched {instance.ResumeCommand}  (in {instance.Root}) — " +
                     "no process handle returned, so it is NOT tracked: it will not show in `status`, " +
                     "and its claims will look like orphans to the reaper.");
                return true;
            }

            // Read the PID before adopting: adoption hands `proc` to MonitorProcess, which
            // disposes it the moment the session exits, and a disposed Process throws on Id.
            var pid = proc.Id;
            AdoptResumed(instance, proc);
            _log($"resume: launched {instance.ResumeCommand}  (in {instance.Root}) — " +
                 $"adopted as live instance '{instance.InstanceId}' (PID {pid}).");
            return true;
        }
        catch (Exception ex)
        {
            _log($"resume: failed to launch — {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Adopt a just-launched resume console as an existing instance's live process, so the
    /// resumed session is a tracked, live member of the roster the claim arbiter reads.
    /// Both resume paths use this.
    /// <para>The sequence is the registration tail of <see cref="Start"/> — dispose the
    /// stale handle from the previous run, take the new process, reset the run stamps, go
    /// Running — with <see cref="Recover"/>'s log-directory derivation (<c>_dataDir</c> +
    /// <c>SafePathName</c>) and <see cref="Recover"/>'s monitor-then-announce ordering.</para>
    /// <para>Unlike <see cref="Recover"/>, which builds a brand-new instance nobody can see
    /// yet, this instance is already in <c>_instances</c> and <see cref="Poll"/> may be
    /// reading it on the timer thread — hence the lock, which is the same lock
    /// <see cref="Start"/> holds across its own registration tail.</para>
    /// <para>On exit <see cref="MonitorProcess"/> flips the instance to Stopped (or Crashed)
    /// and raises <c>SessionStateChanged</c>, which is what auto-releases the session's
    /// claims — the resumed session gets the same end-of-life handling as a spawned one.</para>
    /// </summary>
    private void AdoptResumed(SessionInstance instance, Process proc)
    {
        var logDir = Path.Combine(_dataDir, instance.SafePathName);
        // Best effort, and deliberately not allowed to throw: the console is ALREADY
        // running by the time we get here, so a failure to make the log directory must
        // not abort adoption and leave a live session untracked. MonitorProcess only
        // uses logDir to write a crash log, and guards that write itself.
        try { Directory.CreateDirectory(logDir); }
        catch (Exception ex)
        {
            _log($"[{instance.InstanceId}] resume: could not create log dir {logDir} ({ex.Message}) — crash logs for this run may be lost.");
        }

        lock (instance.Lock)
        {
            instance.Process?.Dispose();
            instance.Process = proc;
            instance.StartedAt = DateTime.Now;
            instance.StoppedAt = null;
            instance.LastExitCode = null;
            instance.Status = SessionStatus.Running;

            // Monitor in background FIRST, then announce — Recover's ordering (:984-986),
            // and here it is load-bearing rather than stylistic. SessionStateChanged
            // handlers do real work: Program.cs's handler calls SessionState.Save, which
            // dereferences i.Process!.Id for every live instance outside the try that
            // guards the other process reads (SessionState.cs:75 vs :85). A throw there
            // would skip the Task.Run and unwind into Resume's catch, which would log
            // "resume: failed to launch" and return false about a console that is adopted
            // and running. Dispatching the monitor first makes the announce unable to cost
            // us the monitor.
            _ = Task.Run(() => MonitorProcess(instance, proc, logDir));

            SessionStateChanged?.Invoke(instance, SessionStatus.Running);
        }

        // The resumed console reports the adopted cmd.exe as its window's owner, so
        // capture it by PID — polling briefly, because the window is being created
        // right now. Restores `focus` for resumed sessions (the old gap: adopted
        // sessions were tracked but had no captured window).
        _ = Task.Run(() =>
        {
            var deadline = DateTime.UtcNow + WindowCaptureTimeout;
            while (DateTime.UtcNow < deadline)
            {
                if (TryCaptureWindowByPid(instance)) return;
                // IsAlive dereferences Process.HasExited, which throws once
                // MonitorProcess disposes the exited proc — guard it, or a session
                // that quits mid-poll faults this task.
                try { if (!instance.IsAlive) return; }
                catch { return; }
                Thread.Sleep(WindowCapturePoll);
            }
            _log($"[{instance.InstanceId}] no console window found to focus after resume (session is unaffected).");
        });
    }

    /// <summary>
    /// Resume a session known only by its transcript (the `history` verb) — it may
    /// never have been one of this huddle's instances. Same live-writer guard as
    /// the instance-based resume: if any tracked instance is alive on this session
    /// id, refuse (two writers fork/corrupt the transcript JSONL).
    /// <para>When the transcript DOES belong to a tracked instance, the launched console
    /// is adopted as that instance's live process (<see cref="AdoptResumed"/>), so its
    /// claims are held by a session the live roster can see. When it belongs to none,
    /// nothing is adopted — there is no identity to adopt it as.</para>
    /// </summary>
    public bool ResumeTranscript(string sessionId, string cwd)
    {
        if (!Guid.TryParse(sessionId, out var guid))
        {
            _log($"resume: '{sessionId}' is not a session id.");
            return false;
        }

        var live = _instances.Values.FirstOrDefault(i =>
            i.IsAlive && i.SessionId.HasValue && i.SessionId.Value == guid);
        if (live != null)
        {
            _log($"resume: session {sessionId} is still running as {live.InstanceId} — stop it first.");
            return false;
        }

        var workDir = Directory.Exists(cwd) ? cwd : ".";
        // Same ledger context as every other launch path. A transcript resume may not
        // correspond to any tracked instance; when it does, borrow that instance's
        // identity so its claims are attributed to the name everything else knows it by
        // — and adopt the console back into that instance below, so the roster agrees
        // with the name the claims carry. When it does not, HUDDLE_INSTANCE is empty and
        // `huddle --claim` refuses loudly rather than writing an ownerless claim — the
        // fail-loud direction, and the reason there is nothing to adopt in that case.
        var known = _instances.Values.FirstOrDefault(i => i.SessionId.HasValue && i.SessionId.Value == guid);
        var envPrefix2 = $"title huddle-resume: {guid} && set BUN_CRASH_REPORTER_URL= && " +
                         BuildLedgerEnvSet(known?.InstanceId ?? "", known?.RepoName ?? "", guid.ToString(), known?.Root ?? "");
        var psi2 = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c {envPrefix2}\"{_claudePath}\" --resume {guid}",
            WorkingDirectory = workDir,
            UseShellExecute = true,
        };
        try
        {
            var proc = Process.Start(psi2);
            if (proc == null)
            {
                // See Resume: the console launched, only adoption failed.
                _log($"resume: launched claude --resume {guid}  (in {workDir}) — " +
                     "no process handle returned, so it is NOT tracked.");
                return true;
            }

            if (known != null)
            {
                // PID first — see Resume: MonitorProcess disposes `proc` on exit.
                var pid = proc.Id;
                AdoptResumed(known, proc);
                _log($"resume: launched claude --resume {guid}  (in {workDir}) — " +
                     $"adopted as live instance '{known.InstanceId}' (PID {pid}).");
            }
            else
            {
                // No tracked instance owns this transcript, so there is nothing to adopt
                // it AS. Deliberately unchanged: HUDDLE_INSTANCE is empty, so
                // `huddle --claim` refuses loudly rather than writing an ownerless claim.
                proc.Dispose();
                _log($"resume: launched claude --resume {guid}  (in {workDir}) — " +
                     "no tracked instance owns this transcript, so it is not adopted " +
                     "(it cannot claim files: `huddle --claim` will refuse).");
            }
            return true;
        }
        catch (Exception ex)
        {
            _log($"resume: failed to launch — {ex.Message}");
            return false;
        }
    }

    public void StopAll()
    {
        var running = _instances
            .Where(i => i.Value.IsAlive || i.Value.Status == SessionStatus.AutoRestarting)
            .Select(i => i.Key).ToList();
        if (running.Count == 0)
        {
            _log("No instances running.");
            return;
        }
        _log($"Stopping {running.Count} instance(s): {string.Join(", ", running)}");
        foreach (var id in running)
        {
            if (_instances.TryGetValue(id, out var instance))
                StopInstance(instance);
        }
    }

    public void Poll()
    {
        foreach (var (id, instance) in _instances)
        {
            lock (instance.Lock)
            {
                if (instance.Status == SessionStatus.Running && !instance.IsAlive)
                {
                    instance.StoppedAt = DateTime.Now;

                    int? code = null;
                    try { code = instance.Process?.ExitCode; } catch { }
                    if (code == null && instance.Process != null)
                    {
                        try { code = TryGetKernelExitCode(instance.Process.Id); } catch { }
                    }
                    instance.LastExitCode = code;
                    instance.Process?.Dispose();
                    instance.Process = null;

                    // Unknown or zero is a clean stop; nonzero is a crash.
                    var newStatus = (code is null or 0)
                        ? SessionStatus.Stopped
                        : SessionStatus.Crashed;
                    instance.Status = newStatus;
                    SessionStateChanged?.Invoke(instance, newStatus);
                }
            }
        }
    }

    /// <summary>
    /// Get all instances for a given repo name.
    /// </summary>
    public IEnumerable<SessionInstance> GetRepoInstances(string repoName)
    {
        return _instances.Values
            .Where(i => i.RepoName.Equals(repoName, StringComparison.OrdinalIgnoreCase));
    }

    // How long to wait for a spawned session's console window to appear. Windows
    // Terminal is typically up well inside a second; the ceiling only matters on a
    // loaded box. Claude Code replaces the title with the conversation topic after
    // it starts, so a slow capture loses the title match and falls back to the
    // new-window rule.
    private static readonly TimeSpan WindowCaptureTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WindowCapturePoll = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// Locate and remember the console window of a session that has just started,
    /// so `focus` can raise it. Best-effort: a session without a captured window is
    /// fully functional, it just cannot be brought to the front.
    /// </summary>
    private void CaptureWindow(SessionInstance instance, IReadOnlySet<IntPtr> windowsBefore)
    {
        try
        {
            var hWnd = SessionWindow.WaitForWindow(
                windowsBefore,
                SessionWindow.TitleMarker(instance.InstanceId),
                ClaimedWindowHandles,
                WindowCaptureTimeout,
                WindowCapturePoll);

            if (hWnd == IntPtr.Zero)
            {
                _log($"[{instance.InstanceId}] no console window found to focus (session is unaffected).");
                return;
            }

            lock (instance.Lock) instance.WindowHandle = hWnd;
        }
        catch (Exception ex)
        {
            _log($"[{instance.InstanceId}] window capture failed (continuing without): {ex.Message}");
        }
    }

    /// <summary>
    /// Locate the console window of an ALREADY-RUNNING session by its tracked PID —
    /// the path for sessions huddle did not spawn this run (recovered after a huddle
    /// restart, or adopted from a resume), which have no spawn-time snapshot to diff.
    /// A classic console window reports the console app (the session's cmd.exe) as
    /// its owner, so the PID huddle persists identifies it directly; when Windows
    /// Terminal owns the windows nothing matches and this returns false (best-effort, same
    /// contract as spawn-time capture). Safe to call any time — also used as the
    /// lazy retry when `focus` finds no live handle on record.
    /// </summary>
    public bool TryCaptureWindowByPid(SessionInstance instance)
    {
        try
        {
            var proc = instance.Process;
            if (proc == null || proc.HasExited) return false;

            var hWnd = SessionWindow.PickWindowByPid(
                SessionWindow.Enumerate(), (uint)proc.Id, ClaimedWindowHandles());
            if (hWnd == IntPtr.Zero) return false;

            lock (instance.Lock) instance.WindowHandle = hWnd;
            return true;
        }
        catch (Exception ex)
        {
            _log($"[{instance.InstanceId}] window lookup by pid failed (continuing without): {ex.Message}");
            return false;
        }
    }

    /// <summary>Window handles already owned by a session, so two concurrent spawns cannot claim one window.</summary>
    private IReadOnlySet<IntPtr> ClaimedWindowHandles()
    {
        var claimed = new HashSet<IntPtr>();
        foreach (var (_, other) in Instances)
        {
            var handle = other.WindowHandle;
            if (handle != IntPtr.Zero) claimed.Add(handle);
        }
        return claimed;
    }

    /// <summary>
    /// H2 (wiring-gap): keep at most <paramref name="keep"/> newest crash-*.log files
    /// in a session's log dir; delete the rest oldest-first. keep &lt;= 0 removes all.
    /// Best-effort — a locked or vanished file is logged and skipped, never fatal.
    /// </summary>
    public static void PruneCrashLogs(string logDir, int keep, Action<string> log)
    {
        try
        {
            if (!Directory.Exists(logDir)) return;
            var files = Directory.GetFiles(logDir, "crash-*.log")
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
            foreach (var stale in files.Skip(Math.Max(0, keep)))
            {
                try { stale.Delete(); }
                catch (Exception ex) { log($"Crash-log prune: could not delete {stale.Name}: {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            log($"Crash-log prune failed for {logDir}: {ex.Message}");
        }
    }

    private void MonitorProcess(SessionInstance instance, Process proc, string logDir)
    {
        // Diagnostics — capture every detail of the exit so we can tell
        // "claude returned nonzero" from "console window was closed externally".
        string? waitException = null;
        try
        {
            proc.WaitForExit();
        }
        catch (Exception ex)
        {
            waitException = $"{ex.GetType().Name}: {ex.Message}";
        }

        string? exitCodeException = null;
        int? rawExitCode = null;
        bool exitCodeFromKernel = false;
        try { rawExitCode = proc.ExitCode; }
        catch (Exception ex) { exitCodeException = $"{ex.GetType().Name}: {ex.Message}"; }

        // Fallback: UseShellExecute=true gives us a Process whose ExitCode
        // always throws. Read the kernel directly while `proc` still holds
        // the underlying handle (guaranteed because we haven't Disposed yet).
        if (rawExitCode == null)
        {
            try
            {
                var pid = proc.Id;
                rawExitCode = TryGetKernelExitCode(pid);
                if (rawExitCode != null) exitCodeFromKernel = true;
            }
            catch { /* Id may also throw on a detached handle — leave null */ }
        }

        bool hasExited = false;
        try { hasExited = proc.HasExited; } catch { }

        DateTime? exitTime = null;
        try { exitTime = proc.ExitTime; } catch { }

        DateTime? startTime = null;
        try { startTime = proc.StartTime; } catch { }

        var statusAtEntry = instance.Status;

        // Will be set inside the lock if auto-restart should fire
        bool shouldAutoRestart = false;
        int delaySeconds = 0;
        CancellationTokenSource? cts = null;

        lock (instance.Lock)
        {
            if (instance.Status == SessionStatus.Stopping || instance.Status == SessionStatus.Stopped)
                return;

            // No signal at all (both .NET and kernel reads failed): treat as
            // a clean stop, not a crash. We literally don't know.
            var exitCode = rawExitCode ?? 0;
            var codeKnown = rawExitCode.HasValue;

            instance.LastExitCode = exitCode;
            instance.StoppedAt = DateTime.Now;

            // Calculate uptime before clearing the process
            var uptime = instance.StartedAt.HasValue ? DateTime.Now - instance.StartedAt.Value : TimeSpan.Zero;

            instance.Process = null;
            proc.Dispose();

            var codeSource = exitCodeFromKernel ? "kernel" : (codeKnown ? "process" : "unknown");

            if (exitCode == 0)
            {
                instance.Status = SessionStatus.Stopped;
                instance.ConsecutiveAutoRestarts = 0;
                _log($"Instance '{instance.InstanceId}' exited cleanly. " +
                     $"[code={(codeKnown ? "0" : "unknown")}, source={codeSource}, " +
                     $"HasExited={hasExited}, statusAtEntry={statusAtEntry}, " +
                     $"waitEx={waitException ?? "none"}]");
                SessionStateChanged?.Invoke(instance, SessionStatus.Stopped);
            }
            else
            {
                instance.CrashCount++;
                var hexCode = $"0x{unchecked((uint)exitCode):X8}";
                _log($"*** CRASH *** Instance '{instance.InstanceId}' exited with code {exitCode} ({hexCode}) " +
                     $"[source={codeSource}, HasExited={hasExited}, statusAtEntry={statusAtEntry}, " +
                     $"waitEx={waitException ?? "none"}, exitCodeEx={exitCodeException ?? "none"}]");

                // H2 (wiring-gap): crashLogRetention governs how many crash logs a
                // session keeps. 0 = keep none (skip the write); otherwise write then
                // prune oldest-first down to the cap. The setting was settable and
                // validated for two shipped builds while nothing pruned anything.
                var crashLogKeep = _config.Settings.Int("crashLogRetention");
                if (crashLogKeep > 0)
                try
                {
                    var crashFile = Path.Combine(logDir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                    var content = $"""
                        Instance: {instance.InstanceId}
                        Repo: {instance.RepoName}
                        Root: {instance.Root}
                        Purpose: {instance.Purpose}
                        Persona: {instance.ActivePersona ?? "(none)"}
                        Crashed: {DateTime.Now:O}
                        Started: {instance.StartedAt:O}
                        Uptime: {instance.FormatUptime()}
                        Exit Code: {exitCode} ({hexCode})
                        Crash Count: {instance.CrashCount}

                        --- Diagnostics ---
                        Exit Code Source: {codeSource}
                        ExitCode Exception: {exitCodeException ?? "none"}
                        WaitForExit Exception: {waitException ?? "none"}
                        HasExited (post-wait): {hasExited}
                        Process StartTime: {startTime?.ToString("O") ?? "<unavailable>"}
                        Process ExitTime: {exitTime?.ToString("O") ?? "<unavailable>"}
                        Status At Monitor Entry: {statusAtEntry}
                        """;
                    File.WriteAllText(crashFile, content);
                    _log($"Crash log written: {crashFile}");
                }
                catch (Exception ex)
                {
                    _log($"Failed to write crash log: {ex.Message}");
                }
                else
                    _log($"Crash log skipped (crashLogRetention = 0) for '{instance.InstanceId}'.");
                PruneCrashLogs(logDir, crashLogKeep, _log);

                // Reset consecutive counter if uptime was >60s (sustained run)
                if (uptime.TotalSeconds > 60)
                    instance.ConsecutiveAutoRestarts = 0;

                // Check auto-restart config
                var (enabled, max, backoff) = _config.GetAutoRestartConfig(instance.Definition);
                if (enabled && instance.ConsecutiveAutoRestarts < max)
                {
                    instance.ConsecutiveAutoRestarts++;
                    var attempt = instance.ConsecutiveAutoRestarts;
                    delaySeconds = backoff[Math.Min(attempt - 1, backoff.Length - 1)];
                    cts = new CancellationTokenSource();
                    instance.AutoRestartCts = cts;
                    instance.AutoRestartAt = DateTime.Now.AddSeconds(delaySeconds);
                    instance.Status = SessionStatus.AutoRestarting;
                    shouldAutoRestart = true;
                    _log($"Auto-restart {attempt}/{max} for '{instance.InstanceId}' in {delaySeconds}s...");
                }
                else
                {
                    instance.Status = SessionStatus.Crashed;
                    if (enabled)
                        _log($"Auto-restart limit reached for '{instance.InstanceId}' ({max} attempts).");
                }

                SessionStateChanged?.Invoke(instance, instance.Status);
            }
        }

        // Schedule the delayed restart outside the lock to avoid deadlock
        if (shouldAutoRestart && cts != null)
        {
            var repoName = instance.RepoName;
            var persona = instance.ActivePersona;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cts.Token);
                }
                catch (TaskCanceledException)
                {
                    _log($"Auto-restart cancelled for '{instance.InstanceId}'.");
                    return;
                }

                // Verify the instance is still in AutoRestarting state (user may have intervened)
                lock (instance.Lock)
                {
                    if (instance.Status != SessionStatus.AutoRestarting)
                    {
                        _log($"Auto-restart skipped for '{instance.InstanceId}' (status changed to {instance.Status}).");
                        return;
                    }
                    instance.AutoRestartCts = null;
                    instance.AutoRestartAt = null;
                }

                _log($"Auto-restarting '{instance.InstanceId}'...");
                Start(repoName, persona, continueSession: true);
            });
        }
    }

    // Win32 fallback for reading exit code when Process.ExitCode throws.
    // Sessions launch with UseShellExecute=true so the .NET Process object
    // doesn't carry a query-rights handle — every read of ExitCode throws
    // "Process was not started by this object". We re-open the kernel object
    // by PID (still alive because `proc` holds an internal handle) and ask
    // the kernel directly. STILL_ACTIVE (259) means the read raced the exit;
    // treat that as unavailable.
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint STILL_ACTIVE = 259;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private static int? TryGetKernelExitCode(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            if (!GetExitCodeProcess(h, out uint code)) return null;
            if (code == STILL_ACTIVE) return null;
            return unchecked((int)code);
        }
        finally
        {
            CloseHandle(h);
        }
    }
}
