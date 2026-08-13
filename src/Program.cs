namespace Huddle;

class Program
{
    // Held for the process lifetime; the OS releases it however huddle dies.
    private static Mutex? _singleton;

    static int Main(string[] args)
    {
        // Helper-process mode. The parent huddle's PromptInjector.Inject
        // spawns `huddle.exe --inject <pid> <b64utf8text>` as a throwaway
        // child so the parent never touches its own console. We do the
        // AttachConsole + WriteConsoleInput dance here and exit. Any log
        // output goes to stderr so the parent can capture it on failure.
        if (args.Length >= 3 && args[0] == "--inject")
        {
            if (!int.TryParse(args[1], out var injectPid))
            {
                Console.Error.WriteLine($"--inject: invalid PID '{args[1]}'");
                return 2;
            }
            string injectText;
            try
            {
                injectText = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(args[2]));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"--inject: bad base64 text: {ex.Message}");
                return 2;
            }
            var force = Array.IndexOf(args, "--force") >= 0;
            // Exit code is meaningful: 0 delivered, HELD_EXIT (3) declined
            // because the operator is at the console, 1 failure. The parent
            // maps these back in PromptInjector.Inject.
            return PromptInjector.InjectInProcess(injectPid, injectText, m => Console.Error.WriteLine(m), force);
        }

        // Credential-logger mode. Each spawned session's git is configured to run
        // `huddle --cred-log <instanceId> <dropDir>` as a credential helper BEFORE
        // GCM (see SessionManager / GitActivityMonitor.WriteCredentialLoggerConfig).
        // git appends the operation as the final arg. We record the requested host
        // to a drop file and output nothing, so GCM still performs the real auth —
        // this lets huddle announce which session is blocked on a credential prompt.
        if (args.Length >= 2 && args[0] == "--cred-log")
        {
            return GitActivityMonitor.RunCredLog(args);
        }

        // Headless projects-report mode: render the projects status page from registered
        // repo data (worktree-aware) and exit. No orchestrator, watchers, or singleton —
        // the reproducible output demo, made scriptable (scripts/demo-project-status.ps1,
        // CI). Usage: huddle --projects-html <out.html> [--config <path>]
        if (args.Length >= 2 && args[0] == "--projects-html")
        {
            return RunProjectsHtml(args);
        }

        // Enable VT processing so OSC 8 hyperlinks (docs/history listings) work when
        // huddle runs under legacy conhost. When the console can't do VT, fall back
        // to plain-text titles instead of spewing raw escape sequences.
        ConsoleUI.HyperlinksEnabled = VtConsole.TryEnable();

        // Put huddle's own icon on the console window + taskbar — the embedded
        // ApplicationIcon covers Explorer only; the live window needs WM_SETICON.
        ConsoleIcon.TrySet();

        // Find config path
        var configPath = "huddle.json";
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--config" or "-c")
            {
                configPath = args[i + 1];
                break;
            }
        }

        // Fallback to old config name
        if (!File.Exists(configPath) && configPath == "huddle.json" && File.Exists("myapp.json"))
        {
            configPath = "myapp.json";
            ConsoleUI.Log("Note: rename myapp.json to huddle.json");
        }

        // First-run bootstrap: copy template.json -> huddle.json so a fresh
        // clone has a starting config sitting next to its required siblings
        // (personas/, logs/, ipc/). Exit after creation so the user edits the
        // paths for their machine before huddle actually launches sessions.
        if (!File.Exists(configPath))
        {
            var bootstrapDir = Path.GetDirectoryName(Path.GetFullPath(configPath));
            if (string.IsNullOrEmpty(bootstrapDir)) bootstrapDir = ".";
            var templatePath = Path.Combine(bootstrapDir, "template.json");
            if (File.Exists(templatePath))
            {
                File.Copy(templatePath, configPath);
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Created {Path.GetFullPath(configPath)} from template.json.");
                Console.WriteLine("Edit it to match your machine's repo layout, then re-run huddle.");
                Console.ResetColor();
                return 1;
            }
        }

        // Load config
        HuddleConfig config;
        try
        {
            config = HuddleConfig.Load(configPath);
        }
        catch (FileNotFoundException)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Config not found: {Path.GetFullPath(configPath)}");
            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine("Create a huddle.json with your session definitions. Example:");
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("""
                {
                  "sessions": [
                    {
                      "name": "my-project",
                      "root": "C:\\path\\to\\project",
                      "purpose": "Main development session",
                      "autoStart": true
                    }
                  ]
                }
                """);
            Console.ResetColor();
            return 1;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error loading config: {ex.Message}");
            Console.ResetColor();
            return 1;
        }

        // Resolve claude path
        string claudePath;
        try
        {
            claudePath = config.ResolveClaudePath();
            ConsoleUI.Log($"Claude Code: {claudePath}");
        }
        catch (FileNotFoundException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(ex.Message);
            Console.ResetColor();
            return 1;
        }

        // Data and personas directories — next to huddle.json
        var configDir = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".";

        // Singleton guard. Two huddle instances sharing one root double-execute
        // every inbox command — two spawns per start, two workers per dispatch,
        // context.md ping-ponging between two registries (2026-07-16 incident).
        // Keyed to the root directory so separate huddle roots
        // can still run side-by-side. An abandoned mutex (previous instance
        // crashed while holding it) still counts as acquired.
        var rootKey = Path.GetFullPath(configDir).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
        var mutexName = "Local\\huddle-" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rootKey)))[..16];
        _singleton = new Mutex(initiallyOwned: false, mutexName);
        try
        {
            if (!_singleton.WaitOne(TimeSpan.Zero))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Another huddle instance is already running for {rootKey}.");
                Console.WriteLine("Two instances double-execute every command: duplicate sessions, double dispatches, clobbered state.");
                Console.WriteLine("Close the other huddle window first (find it: tasklist | findstr huddle).");
                Console.ResetColor();
                return 1;
            }
        }
        catch (AbandonedMutexException)
        {
            // Previous holder died without releasing — we now own it. Proceed.
        }

        var dataDir = Path.Combine(configDir, "logs");
        Directory.CreateDirectory(dataDir);
        var personasDir = Path.Combine(configDir, "personas");

        // Open the durable orchestrator log. From here on, every ConsoleUI.Log line
        // (commands, session lifecycle, shutdown decisions) is also appended to
        // logs\huddle.log so an abnormal teardown is reconstructable afterward.
        ConsoleUI.SetLogFile(Path.Combine(dataDir, "huddle.log"));

        // Create components
        var contextWriter = config.ContextFile ? new ContextWriter(dataDir, ConsoleUI.Log) : null;
        var contextPath = contextWriter?.ContextPath;
        var manager = new SessionManager(config, claudePath, dataDir, personasDir, contextPath, ConsoleUI.Log);

        // IPC
        IpcManager? ipcManager = null;
        if (config.Ipc)
        {
            ipcManager = new IpcManager(Path.Combine(configDir, "ipc"), ConsoleUI.Log);
            manager.Ipc = ipcManager;

            // Auto-nudge: when mail lands in a running session's inbox, type a
            // SHORT wake signal into its console so the agent processes it as
            // a turn. The signal points at the mail file — the agent reads
            // the real body itself and replies by writing mail back. We do
            // NOT paste the body into the prompt; long inline bodies (a) eat
            // the prompt's submit Enter when the console input buffer fills,
            // (b) turn agent-to-agent dialog into prompt-typed monologue
            // instead of real mail exchange.
            //
            // Return true on successful injection. IpcManager then leaves the mail in
            // processed/ (it auto-archives before nudging). Return false if the recipient
            // is dead or injection failed; IpcManager returns the file to inbox/ so
            // Watch() retries on the next scan / session start.
            //
            // Sends with suppressAutoNudge=true skip this path entirely
            // (caller already injected; IpcManager moves the file itself).
            ipcManager.MessageReceived += (instanceId, msg, filePath) =>
            {
                if (!manager.Instances.TryGetValue(instanceId, out var inst) || !inst.IsAlive)
                    return false;
                var pid = inst.Process?.Id ?? 0;
                if (pid <= 0) return false;

                // Make the path relative to the huddle root so the nudge stays short
                // and the agent can read it as-is. The mail stays in inbox/ until the
                // agent acknowledges it, so this points there.
                var relPath = Path.GetRelativePath(configDir, filePath).Replace('\\', '/');

                var subject = (msg.Subject ?? "").Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ");
                var nudge = $"[huddle mail from {msg.From}] {subject} — read {relPath}";

                // Deliver the wake line as pulled context via the session's
                // pending-context file (drained by its Stop/UserPromptSubmit hook)
                // instead of synthesized keystrokes — an operator typing in the
                // console is never stomped. Returning true records the announcement
                // so it is never repeated; clearing the inbox is the agent's job.
                ipcManager.AppendPending(inst.SafePathName, nudge);
                return true;
            };
        }

        // Orchestrator (started later — after repo registration — so its startup
        // inbox scan resolves repos correctly; see below)
        Orchestrator? orchestrator = null;
        if (ipcManager != null)
        {
            orchestrator = new Orchestrator(manager, ipcManager, ConsoleUI.Log);
        }

        var ui = new ConsoleUI(manager) { Ipc = ipcManager, Orchestrator = orchestrator };

        // State persistence
        var stateFile = Path.Combine(dataDir, "state.json");

        // Wire up state change notifications
        manager.SessionStateChanged += (instance, newStatus) =>
        {
            if (newStatus == SessionStatus.Crashed)
                ConsoleUI.LogCrash($"*** CRASH *** {instance.InstanceId} exited with code {instance.LastExitCode}");

            contextWriter?.Update(manager.Instances);
            SessionState.Save(stateFile, manager.Instances, manager.Recoverable);
        };

        // Register repo definitions
        ui.PrintBanner();

        foreach (var def in config.Sessions)
        {
            manager.Register(def);
            ConsoleUI.Log($"Registered: {def.Name} -> {def.Root}");
        }

        // I010 F4: keep every registered repo's permission allow-set seeded so the
        // prompt-spam class can't regress per-repo. Merge-only; silent when already
        // seeded; `"seedPermissions": false` in huddle.json disables.
        PermissionSeeder.SeedAll(
            config.Sessions.Select(s => (s.Name, s.Root)), config.SeedPermissions, ConsoleUI.Log);

        // Start the orchestrator only after repo definitions are registered.
        // Its startup inbox scan can process commands that resolve against the
        // repo registry (start-session, repo-scoped broadcast); starting it
        // earlier made those nack with "unknown repo" for stale inbox files.
        orchestrator?.Start();

        // Recover sessions from previous run
        var recovered = SessionState.Recover(stateFile, manager, ipcManager, ConsoleUI.Log);
        if (recovered > 0)
            ConsoleUI.Log($"Recovered {recovered} session(s) from previous run.");

        // Now that the live set is known, sweep claims stranded by dead/untracked instances
        // (this is what makes "bounce huddle to reap dead-session claims" actually true).
        orchestrator?.ReapOrphanClaims();

        // I010: dead sessions were held, not dropped — announce the roster.
        ui.StateFile = stateFile;
        // Projects phase 1: the huddle-map overlay lives beside huddle.json.
        ui.ProjectsMapPath = Path.Combine(configDir, "projects-map.json");
        if (manager.Recoverable.Count > 0)
            ConsoleUI.Log($"{manager.Recoverable.Count} session(s) recoverable from a previous run — 'recover' to list.");

        // Auto-start repos (no persona for auto-start)
        foreach (var def in config.Sessions.Where(s => s.AutoStart))
        {
            manager.Start(def.Name);
        }

        // Write initial context and state
        contextWriter?.Update(manager.Instances);
        SessionState.Save(stateFile, manager.Instances, manager.Recoverable);

        // Git activity monitor: surface pushes/fetches (remote-tracking reflog) and
        // credential-prompt requests (auth drop dir) in the console. Poll-based.
        var gitAuthDir = ipcManager?.GitAuthDir ?? Path.Combine(dataDir, "gitauth");
        var gitActivity = new GitActivityMonitor(
            config.Sessions.Select(s => (s.Name, s.Root)), gitAuthDir, ConsoleUI.Log);
        gitActivity.Start();

        // Print initial status
        ui.PrintStatus();
        ui.PrintPersonas(manager.GetAvailablePersonas());
        ui.PrintHelp();

        // Handle Ctrl+C — request shutdown, but confirm before killing live sessions.
        var ctrlCPressed = false;
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            ctrlCPressed = true;
        };

        // Gate every teardown path. Returns true if it is OK to proceed to StopAll().
        // No running sessions → nothing to protect, proceed silently. A null
        // (unreadable) answer means stdin is gone and huddle can no longer be
        // operated, so we proceed rather than spin forever.
        bool ConfirmShutdown()
        {
            var running = manager.Instances.Count(i => i.Value.IsAlive);
            if (running == 0) return true;
            Console.Write($"{running} huddle session(s) are running. Terminate them in progress? (y/N): ");
            var answer = Console.ReadLine();
            if (answer == null) return true;
            answer = answer.Trim();
            return answer.Equals("y", StringComparison.OrdinalIgnoreCase)
                || answer.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        // Command loop. One editor for the whole loop so history persists across
        // commands. The completer reads live state per keystroke: instance names
        // for say/stop/focus/..., repo names for start/replay/@-scopes, personas
        // for start's second argument — plus the verb's arg grammar as a dim hint
        // when nothing has been typed after the verb yet.
        var argCompleter = new ArgCompleter(new ArgProviders
        {
            LiveInstances = () => manager.Instances
                .Where(kv => kv.Value.IsAlive).Select(kv => kv.Key)
                .OrderBy(k => k, StringComparer.Ordinal).ToArray(),
            StoppedInstances = () => manager.Instances
                .Where(kv => !kv.Value.IsAlive).Select(kv => kv.Key)
                .OrderBy(k => k, StringComparer.Ordinal).ToArray(),
            Repos = () => manager.Repos.Keys
                .OrderBy(k => k, StringComparer.Ordinal).ToArray(),
            Personas = () => manager.GetAvailablePersonas(),
        });
        var lineEditor = new LineEditor(argCompleter);
        var stopAll = false;
        while (true)
        {
            // Redirected stdin (scripts, pipes, headless) has no interactive console:
            // Console.KeyAvailable throws there, so fall back to plain ReadLine and skip
            // the prompt entirely. The interactive branch paints its own "> " prompt.
            var line = Console.IsInputRedirected
                ? Console.ReadLine()
                : lineEditor.ReadLine("> ", () => ctrlCPressed);

            // Ctrl+C or EOF: both tear down every session. Record which trigger fired,
            // confirm, then log the decision — so an abnormal teardown is never a mystery.
            if (ctrlCPressed || line == null)
            {
                var trigger = ctrlCPressed ? "Ctrl+C" : "EOF/stdin-closed";
                ctrlCPressed = false;
                ConsoleUI.Log($"Shutdown requested via {trigger}.");
                if (ConfirmShutdown())
                {
                    ConsoleUI.Log($"SHUTDOWN CONFIRMED via {trigger} — stopping all sessions.");
                    stopAll = true;
                    break;
                }
                ConsoleUI.Log($"Shutdown via {trigger} CANCELLED by operator. Sessions still running.");
                continue;
            }

            ConsoleUI.LogInput(line);   // durable record of the exact command entered

            manager.Poll(); // Check for any unreported exits

            var result = ui.HandleCommand(line);
            if (result == CommandResult.Shutdown)
            {
                if (ConfirmShutdown())
                {
                    ConsoleUI.Log("SHUTDOWN CONFIRMED via 'shutdown' command — stopping all sessions.");
                    stopAll = true;
                    break;
                }
                ConsoleUI.Log("Shutdown via 'shutdown' command CANCELLED by operator. Sessions still running.");
                continue;
            }
            if (result == CommandResult.Quit)
            {
                ConsoleUI.Log("Exit via 'quit' — sessions left running.");
                break;
            }
        }

        // Exit
        if (stopAll)
        {
            ConsoleUI.Log("Shutting down all sessions...");
            manager.StopAll();
            contextWriter?.Update(manager.Instances);
            SessionState.Save(stateFile, manager.Instances, manager.Recoverable); // Clear — all stopped
        }
        else
        {
            var running = manager.Instances.Count(i => i.Value.IsAlive);
            if (running > 0)
            {
                SessionState.Save(stateFile, manager.Instances, manager.Recoverable); // Persist for recovery
                ConsoleUI.Log($"Detaching. {running} session(s) still running.");
            }
        }
        gitActivity.Dispose();
        orchestrator?.Dispose();
        ipcManager?.Dispose();
        ConsoleUI.Log("Goodbye.");

        return 0;
    }

    // Render docs/projects/<slug> across registered repos AND their git worktrees to a
    // self-contained HTML page, then exit. Agents/claims are live-orchestrator data, so
    // they are empty here — this path proves projects + worktree discovery, not the
    // running fleet. Any failure returns non-zero with a stderr line.
    private static int RunProjectsHtml(string[] args)
    {
        var outPath = Path.GetFullPath(args[1]);
        var configPath = "huddle.json";
        for (int i = 2; i < args.Length - 1; i++)
            if (args[i] is "--config" or "-c") { configPath = args[i + 1]; break; }

        HuddleConfig config;
        try { config = HuddleConfig.Load(configPath); }
        catch (Exception ex) { Console.Error.WriteLine($"--projects-html: config load failed: {ex.Message}"); return 1; }

        // projects-map.json overlay beside the config is optional.
        string? mapJson = null;
        var mapPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".", "projects-map.json");
        try { if (File.Exists(mapPath)) mapJson = File.ReadAllText(mapPath); } catch { /* overlay optional */ }

        var projects = ProjectMap.Discover(
            config.Sessions.Select(s => (s.Name, s.Root)), mapJson, m => Console.Error.WriteLine(m));
        var entries = projects
            .Select(p => new ProjectReportEntry(p, Array.Empty<ProjectAgent>(), Array.Empty<WorkLedgerClaim>()))
            .ToList();

        try
        {
            var dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(outPath, ProjectReport.Render(entries, $"huddle {BuildInfo.Short}"));
            Console.WriteLine($"projects: wrote {entries.Count} project(s) -> {outPath}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine($"--projects-html: write failed: {ex.Message}"); return 1; }
    }
}
