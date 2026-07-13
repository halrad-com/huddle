using System.Diagnostics;
using System.Runtime.InteropServices;

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

    // Escape a string for use as a double-quoted argument on a cmd.exe /c line.
    // Two parsers run: cmd.exe handles the outer line, then CommandLineToArgvW
    // (Microsoft's standard runtime parser, which claude uses) unquotes the
    // argument for the child process. Inside double quotes, cmd treats the
    // shell metachars `& | < > ( )` as literal, so they need no escaping. We
    // do need:
    //   - backslashes preceding a `"` doubled, per msvcrt rules
    //   - internal `"` escaped as `\"`
    //   - trailing backslashes doubled so the closing quote isn't escaped
    internal static string EscapeForCmdQuoted(string s)
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
                "  release — body: {\"files\":[\"path/to/file\"]}",
                "",
                "Responses arrive in your inbox as type: \"info\". Subject \"ack:<command>\" = accepted; subject \"nack:<command>\" = rejected (body is the reason). Always check the prefix before assuming a command succeeded."
            };
            parts.Add(string.Join("\n", orchLines));

            // Work ledger path
            parts.Add($"Work ledger directory: {Ipc.WorkLedgerDir}\nWrite your claim file here as <your-safe-name>.md when starting work. Read other files here to check for conflicts.");
        }

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

    public bool Start(string repoName, string? persona = null, bool continueSession = false, string? prompt = null)
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
                claudeArgs += $" \"{EscapeForCmdQuoted(prompt)}\"";

            // Env vars exported to the child Claude Code process:
            //   BUN_CRASH_REPORTER_URL — silenced so Bun crash dialogs stay inside this session
            //   CLAUDE_SESSION_LABEL  — literal statusline label (used by ~/.claude/statusline.ps1)
            //   CLAUDE_PERSONA        — persona name, for scripts/tools inside the session
            // The leading `title` makes the console window identifiable in Alt+Tab
            // and Task Manager, and is what the `focus` verb matches on.
            var envPrefix = $"title huddle: {instanceId} && " +
                            "set BUN_CRASH_REPORTER_URL= && " +
                            $"set CLAUDE_SESSION_LABEL={instanceId} && ";
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

        // Stop the process outside the lock
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
            }
            catch (Exception ex)
            {
                _log($"Error stopping '{instance.InstanceId}': {ex.Message}");
            }
        }

        lock (instance.Lock)
        {
            instance.Status = SessionStatus.Stopped;
            instance.StoppedAt = DateTime.Now;
            try { instance.LastExitCode = proc?.ExitCode; } catch { }
            _log($"Stopped '{instance.InstanceId}' (exit code {instance.LastExitCode}).");

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
    public bool Recover(string instanceId, string repoName, string? persona, Process proc, DateTime startedAt, Guid? sessionId = null)
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
            SessionId = sessionId
        };

        _instances[instanceId] = instance;

        // Monitor in background
        var logDir = Path.Combine(_dataDir, instance.SafePathName);
        Directory.CreateDirectory(logDir);
        _ = Task.Run(() => MonitorProcess(instance, proc, logDir));

        SessionStateChanged?.Invoke(instance, SessionStatus.Running);
        return true;
    }

    /// <summary>
    /// Open `claude --resume &lt;session-id&gt;` for a tracked session in a fresh console,
    /// with the working directory set to the session's repo root (Claude keys session
    /// storage by cwd). This is a convenience launcher — the resumed CLI is NOT adopted
    /// as a managed instance; it's the operator picking a prior conversation back up.
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
        if (instance.IsAlive)
        {
            _log($"resume: {instance.InstanceId} is still running — 'focus {instance.InstanceId}' to jump to it, or stop it first.");
            return false;
        }

        // Mirror the launch shape of Start: cmd.exe wrapper, identifiable title,
        // Bun crash reporter silenced, own console via UseShellExecute.
        var envPrefix = $"title huddle-resume: {instance.InstanceId} && set BUN_CRASH_REPORTER_URL= && ";
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c {envPrefix}\"{_claudePath}\" --resume {instance.SessionId.Value}",
            WorkingDirectory = instance.Root,
            UseShellExecute = true,
        };

        try
        {
            Process.Start(psi);
            _log($"resume: launched {instance.ResumeCommand}  (in {instance.Root})");
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
