namespace Huddle;

class Program
{
    // Held for the process lifetime; the OS releases it however huddle dies.
    private static Mutex? _singleton;

    // Whether console output is UTF-8. Set on the first line of Main because EVERY
    // mode prints - the CLI verbs return long before the interactive path is reached,
    // and putting this later left `--settings` still flattening em-dashes to "-".
    private static bool _utf8Console;

    static int Main(string[] args)
    {
        // Before any output, in every mode. The console defaults to the system ANSI
        // codepage, which turns the status row's warning sign into a bare "?" that
        // reads as part of the message. No BOM, so redirecting stdout stays clean.
        _utf8Console = ConsoleEncoding.TryEnableUtf8();

        // Before ANY window exists in this process, including the hotkey listener's
        // message-only one: DPI awareness is latched at the first window and cannot be
        // changed afterwards. Without it the peek overlay is a DPI-unaware window, so
        // Windows bitmap-stretches it on any display scaled differently from the primary
        // and the live thumbnails come out visibly soft. PerMonitorV2 because the
        // overlay opens on whichever monitor the cursor is on, and those can differ.
        try { System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2); }
        catch { /* older shell or already latched: the overlay still works, just softer */ }

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

        // Direct ledger access. These modes run the binary only - no running huddle,
        // no orchestrator round-trip - so an agent can claim, release and read the
        // ledger whether or not the console is up.
        // Windows shell entry (spec 2026-08-31-shell-registration-design.md): Start-menu
        // shortcut + AUMID + App Paths, per-user, idempotent. Runs the binary only.
        if (args.Length >= 1 && args[0] == "--register")
            return ShellRegistration.RunRegister(args, Console.WriteLine);

        if (args.Length >= 1 && args[0] == "--unregister")
            return ShellRegistration.RunUnregister(Console.WriteLine);

        if (args.Length >= 1 && args[0] == "--claim")
            return LedgerCommands.RunClaim(args[1..], Environment.GetEnvironmentVariable, Console.WriteLine);

        if (args.Length >= 1 && args[0] == "--release")
            return LedgerCommands.RunRelease(args[1..], Environment.GetEnvironmentVariable, Console.WriteLine);

        if (args.Length >= 1 && args[0] == "--ledger")
            return LedgerCommands.RunLedger(args[1..], Environment.GetEnvironmentVariable, Console.WriteLine);

        // PreToolUse guard (see LedgerCommands.RunClaimCheck). Claude Code hands the tool
        // call as JSON on stdin; exit 2 blocks the tool and stderr goes back to the model.
        if (args.Length >= 1 && args[0] == "--claim-check")
            return LedgerCommands.RunClaimCheck(Console.In.ReadToEnd(), Environment.GetEnvironmentVariable, Console.Error.WriteLine);

        // The pinned "Huddle Sessions" taskbar button. Dispatched here, before config
        // load and before the console starts, because it is a launcher and not a mode:
        // it either nudges the huddle already running for this root or starts one, then
        // exits. Falling through would boot a second orchestrator, which the singleton
        // mutex would then have to refuse. Position-independent for the same reason the
        // settings dispatch below is: `huddle --config x.json --peek` is a documented
        // form, and matching args[0] alone let it fall through (S3).
        if (PeekLauncher.IsPeek(args))
            return PeekLauncher.Run(args, Console.WriteLine);

        // Settings access, same dispatch position and for the same reason: changing a
        // knob must not require launching the orchestrator, and must work from a script
        // or a second window while huddle is running. Position-independent: the
        // documented `huddle --config <path> --set k v` used to fall through here and
        // silently boot a second orchestrator (S3).
        if (SettingsCli.FindVerb(args) != null)
            return SettingsCli.Run(args, Console.WriteLine);

        // Enable VT processing so OSC 8 hyperlinks (docs/history listings) work when
        // huddle runs under legacy conhost. When the console can't do VT, fall back
        // to plain-text titles instead of spewing raw escape sequences.
        ConsoleUI.HyperlinksEnabled = VtConsole.TryEnable();
        // Say it once rather than leaving the operator to decode "?" on a status row.
        if (!_utf8Console)
            ConsoleUI.Log("Note: console is not UTF-8 — non-ASCII glyphs will render as '?'");

        // Put huddle's own icon on the console window + taskbar — the embedded
        // ApplicationIcon covers Explorer only; the live window needs WM_SETICON.
        ConsoleIcon.TrySet();

        // Taskbar identity: mirror the prototype's process AUMID (the shortcut's
        // embedded AUMID from --register is what actually shapes pinning).
        ShellRegistration.TrySetProcessAumid();

        // Find config path — the shared resolver, so the console and `huddle --settings`
        // can never disagree about which file exists (S6). It applies the myapp.json
        // fallback itself, plus the registered-root fallback (a Start-menu/Win+R launch
        // from a config-less cwd boots the registered repo); the note below is the only
        // thing this caller adds.
        var configPath = ConfigPathResolver.Resolve(args, Directory.GetCurrentDirectory(), ShellRegistration.RegisteredRoot);
        if (Path.IsPathRooted(configPath)
            && Path.GetFileName(configPath) == ConfigPathResolver.Default   // legacy fallback returns rooted myapp.json — not this
            && !args.Contains("--config") && !args.Contains("-c"))
            ConsoleUI.Log($"Config: registered root {Path.GetDirectoryName(configPath)}");
        if (Path.GetFileName(configPath) == ConfigPathResolver.Legacy)
            ConsoleUI.Log("Note: rename myapp.json to huddle.json");

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
        catch (SettingsException ex)
        {
            // Starting with settings the operator does not have is worse than not
            // starting. Print EVERY problem, not just the first.
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("huddle.json settings refused — not starting:");
            foreach (var e in ex.Errors) Console.WriteLine($"  {e}");
            Console.ResetColor();
            Console.WriteLine("Fix with: huddle --set <key> <value>   or   huddle --unset <key>");
            return 1;
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

        // A key set both top-level and in "settings" is resolved to "settings" and said
        // out loud here — reported, never silently resolved.
        foreach (var warning in config.Settings.Warnings)
            ConsoleUI.Log(warning);

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
        // context.md ping-ponging between two registries (2026-07-16 incident,
        // ISSUES.md I006). Keyed to the root directory so separate huddle roots
        // can still run side-by-side. An abandoned mutex (previous instance
        // crashed while holding it) still counts as acquired.
        // The hash is shared with the peek signal event (ConfigPathResolver.RootHash), not
        // reproduced here: the two names must agree about what "this root" means, or --peek
        // signals a name nobody is listening on and starts an instance this mutex refuses.
        var rootKey = Path.GetFullPath(configDir).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
        var mutexName = "Local\\huddle-" + ConfigPathResolver.RootHash(configDir);
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

        // Shell entry, self-healing. --register is a command nobody discovers, so an
        // orchestrator that is never in the Start menu is the normal outcome; make
        // running it the registration. Writes only when absent or broken, never
        // hijacks another clone's entry, never fails startup.
        if (config.Settings.Bool("shellRegistration"))
            ShellRegistration.EnsureRegistered(configDir, ConsoleUI.Log);

        // Create components
        var contextWriter = config.Settings.Bool("contextFile") ? new ContextWriter(dataDir, ConsoleUI.Log) : null;
        var contextPath = contextWriter?.ContextPath;
        var manager = new SessionManager(config, claudePath, dataDir, personasDir, contextPath, ConsoleUI.Log);

        // The orchestrator is the ONLY process that appends to a repo's events.jsonl
        // (spec §2.2), so one registry of per-repo writers is shared by everything that
        // records an obligation: mail ingestion, TaskTracker, the work queue, escalation.
        var ledgerWriters = new LedgerWriters(
            repo => manager.Repos.TryGetValue(repo, out var def) ? def.Root : null, ConsoleUI.Log);

        // IPC
        IpcManager? ipcManager = null;
        if (config.Settings.Bool("ipc"))
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
                var relPath = LedgerMailIngest.MailRef(configDir, filePath);

                // Spec §5.4: a task mail opens a tracked row in the RECIPIENT's repo
                // ledger, whoever sent it and without anyone running a command. Keyed on
                // the mail file, so the FSW delivery, the retry tick and a restart's
                // rescan all re-find the same row rather than opening three. This is the
                // change that catches peer-to-peer dispatch — the audit's four dropped
                // assignments were all type:"task".
                // The row itself is opened by the MailSeen handler below, which runs on
                // every pass and does not need the recipient to be alive. Here we only
                // LOOK IT UP, so the wake line can say TASK. Opening it here as well
                // would put the obligation back behind a live session and a one-shot
                // announcement — which is exactly how two assignments went untracked.
                LedgerId? taskId = null;
                if (LedgerMailIngest.IsTask(msg)
                    && ledgerWriters.ForInstance(instanceId) is { } writer
                    && writer.TryFindTaskByRef(relPath, out var existing))
                    taskId = existing;

                // A task must not read like an FYI in the one line an agent often sees
                // before deciding whether to interrupt itself (§5.8). A ledger that could
                // not be written still gets the ordinary line — never no line at all.
                var nudge = LedgerMailIngest.NudgeLine(msg, relPath, taskId);

                // Deliver the wake line as pulled context via the session's
                // pending-context file (drained by its Stop/UserPromptSubmit hook)
                // instead of synthesized keystrokes — an operator typing in the
                // console is never stomped. Returning true records the announcement
                // so it is never repeated; clearing the inbox is the agent's job.
                ipcManager.AppendPending(inst.SafePathName, nudge);

                // pending.txt is DRAINED BY A HOOK, and the hooks that drain it (Stop,
                // UserPromptSubmit) fire on a turn boundary. An idle session ends no turn
                // and submits nothing, so without this the line sits unread until a human
                // types into that console — on 2026-08-22 two fix tasks sat 27 minutes
                // exactly this way. Nudge the console with a one-line submit; the
                // UserPromptSubmit hook folds the pending context onto it.
                //
                // Inject keeps its foreground gate, so an operator typing at the recipient's
                // console is never stomped; a held wake is re-driven by IpcManager's retry
                // tick (WakeIdle below) once they step away.
                //
                // Type the QUEUE, not a carrier. The submit exists to give the hook
                // something to fold pending.txt onto, so for a while it was the contentless
                // MailWake.WakeLine — "[huddle] you have mail" and nothing else. That is
                // survivable only while the fold always happens. When it does not (hook
                // absent, or pending.txt already drained on an earlier turn boundary) the
                // agent gets a ping naming no sender, no subject and no path, and its only
                // move is to go hunting. On 2026-08-28 otherapp:architect took exactly that
                // ping mid-review, went looking, misread an outbox that is empty by design,
                // and told the operator a colleague had fabricated a request that colleague
                // had genuinely made four days earlier.
                //
                // The line was appended to pending.txt immediately above, so the file
                // already holds it — and everything else still queued. Asking PendingWake
                // for the text means this site does not CHOOSE a string at all, which is
                // what stops the swap-back: same producer as the re-drive below, and the
                // only one, so losing the sender means failing PendingWakeTests rather
                // than editing a lambda nothing asserts on.
                if (MailWake.ShouldWakeSession(TranscriptOf(inst), MailWake.IdleAfter))
                    PromptInjector.Inject(
                        pid, PendingWake.LineFor(ipcManager.PendingPath(inst.SafePathName)), ConsoleUI.Log);
                return true;
            };

            // Re-drive of the same nudge for mail whose wake was held (operator was at
            // the console) or whose recipient went idle after delivery. The delivered
            // index stops MessageReceived re-firing for already-announced mail, so
            // without this a session that idled with a full pending.txt is never woken.
            ipcManager.WakeIdle = safePathName =>
            {
                var inst = manager.Instances.Values.FirstOrDefault(
                    i => i.SafePathName == safePathName && i.IsAlive);
                var pid = inst?.Process?.Id ?? 0;
                if (inst is null || pid <= 0) return false;
                if (!MailWake.ShouldWakeSession(TranscriptOf(inst), MailWake.IdleAfter)) return false;
                // Carry the sender here too. This path has no IpcMessage — it only knows
                // the queue is non-empty — but pending.txt holds the lines that were built
                // with one, so the newest of them says who and what.
                return PromptInjector.Inject(
                    pid, PendingWake.LineFor(ipcManager.PendingPath(safePathName)), ConsoleUI.Log);
            };

            // Spec §5.4, corrected: a task mail opens a tracked row because the mail
            // EXISTS, not because a nudge landed. Runs on every scan for as long as the
            // mail sits unread, needs no live recipient, and is idempotent on the same
            // MailRef the acknowledgement path uses — so a mail that already has a row is
            // never opened twice, and one that never got a row finally gets one.
            //
            // This is the backfill. Task mail delivered before the ledger shipped was
            // announced once, recorded in delivered.txt, and then invisible forever.
            ipcManager.MailSeen = (instanceId, msg, filePath) =>
            {
                if (!LedgerMailIngest.IsTask(msg)) return;
                if (ledgerWriters.ForInstance(instanceId) is not { } writer) return;

                var rel = LedgerMailIngest.MailRef(configDir, filePath);
                if (writer.TryFindTaskByRef(rel, out _)) return;

                var opened = writer.AppendNewTask(id =>
                    LedgerMailIngest.Assigned(msg, rel, instanceId, id, DateTimeOffset.UtcNow)!);
                if (opened is { } id2)
                    ConsoleUI.Log($"ledger: {id2} assigned to {instanceId} by {msg.From}");
            };

            // Mail leaving the inbox is acknowledgement, and acknowledgement is the
            // timestamp the old task tracking never had — the audit could only
            // approximate age-at-read from archive mtimes. AckIfOpen is a no-op for the
            // mail that opened no task, which is most of it.
            ipcManager.MailAcknowledged = (safePathName, mailFileNames) =>
            {
                var inst = manager.Instances.Values.FirstOrDefault(i => i.SafePathName == safePathName);
                if (inst is null || ledgerWriters.ForInstance(inst.InstanceId) is not { } writer) return;
                foreach (var name in mailFileNames)
                {
                    var rel = LedgerMailIngest.MailRef(configDir, ipcManager.IpcDir, safePathName, name);
                    LedgerMailIngest.AckIfOpen(writer, rel, inst.InstanceId, DateTimeOffset.UtcNow, ConsoleUI.Log);
                }
            };
        }

        // Transcript mtime is huddle's only read on whether a session is mid-turn.
        static string? TranscriptOf(SessionInstance inst) =>
            inst.SessionId is { } sid
                ? SessionTrouble.TranscriptPath(MailWake.ProjectsRoot, inst.Root, sid)
                : null;

        // Orchestrator (started later — after repo registration — so its startup
        // inbox scan resolves repos correctly; see below)
        Orchestrator? orchestrator = null;
        if (ipcManager != null)
        {
            orchestrator = new Orchestrator(manager, ipcManager, ConsoleUI.Log);
        }

        var ui = new ConsoleUI(manager) { Ipc = ipcManager, Orchestrator = orchestrator, ConfigPath = configPath };

        // State persistence
        var stateFile = Path.Combine(dataDir, "state.json");
        manager.StateFile = stateFile;   // lets the spawn guard consult the on-disk roster

        // Wire up state change notifications
        manager.SessionStateChanged += (instance, newStatus) =>
        {
            if (newStatus == SessionStatus.Crashed)
                ConsoleUI.LogCrash($"*** CRASH *** {instance.InstanceId} exited with code {instance.LastExitCode}");

            contextWriter?.Update(manager.Instances);
            // Never persist a roster recovery has not finished filling: that write would
            // replace the record of every still-live session with whatever is in memory
            // so far, and the sessions it forgets become invisible — and then duplicable.
            if (manager.RecoveryComplete)
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
            config.Sessions.Select(s => (s.Name, s.Root)), config.Settings.Bool("seedPermissions"), ConsoleUI.Log);

        // Recover sessions from the previous run BEFORE the orchestrator runs anything.
        //
        // Order is load-bearing (2026-08-23): Start() scans the command inbox and advances
        // the work queue, and both can SPAWN. Run against a roster that recovery has not
        // filled yet, `startIfNeeded` and `GenerateInstanceId` see no otherapp:architect,
        // so a second one is started on top of the live session — two agents sharing one
        // identity, invisible to each other in the ledger and sharing one mailbox. Worse,
        // any spawn in that window fires SessionStateChanged -> SessionState.Save, which
        // overwrites state.json with the half-empty roster and destroys the very record
        // recovery was about to read. Recover first; then it is safe to act.
        var recovered = SessionState.Recover(stateFile, manager, ipcManager, ConsoleUI.Log);
        if (recovered > 0)
            ConsoleUI.Log($"Recovered {recovered} session(s) from previous run.");
        manager.RecoveryComplete = true;

        // Start the orchestrator only after repo definitions are registered AND recovery
        // has run. Its startup inbox scan can process commands that resolve against the
        // repo registry (start-session, repo-scoped broadcast); starting it earlier made
        // those nack with "unknown repo" for stale inbox files.
        orchestrator?.Start();

        // Now that the live set is known, sweep claims stranded by dead/untracked instances
        // (this is what makes "bounce huddle to reap dead-session claims" actually true).
        orchestrator?.ReapOrphanClaims();

        // Same moment, same reason, for obligations: a task mail sitting in the inbox of a
        // session nobody is watching is still work somebody was asked to do. Runs after
        // repo registration so ForInstance can resolve a writer; harmless when every
        // inbox is already tracked, because opening a row is idempotent on the mail path.
        ipcManager?.SweepAllInboxes();

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
        // Retention (gitActivityLog) makes both signals durable in git-activity.jsonl so
        // `stats` can answer for past days: the credential drop is the one exact who-signal
        // huddle has and was previously deleted on sight, and movements were console-only.
        var gitAuthDir = ipcManager?.GitAuthDir ?? Path.Combine(dataDir, "gitauth");
        var activityLog = config.Settings.Bool("gitActivityLog")
            ? new GitActivityLog(Path.Combine(dataDir, "git-activity.jsonl"))
            : null;
        var gitActivity = new GitActivityMonitor(
            config.Sessions.Select(s => (s.Name, s.Root)), gitAuthDir, ConsoleUI.Log,
            activityLog, TimeSpan.FromSeconds(config.Settings.Int("gitPollSeconds")));
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

        // Global summon for the peek overlay. A failure here is reported and ignored:
        // the `peek` verb and the pinned shortcut both still work without it.
        //
        // Owned by a switch rather than held directly, so `settings peekHotkey <chord>` can
        // re-register on the running process. Hunting for a free chord is trial and error
        // and used to cost a full `reload` per guess.
        //
        // The resolved SETTING goes in, not its text. Everything about hotkeys - whether an
        // explicit chord is honoured alone or a candidate list is walked, what happens when
        // one is taken, and which listener survives - belongs to the switch, so this file
        // asks for a hotkey and is told what it got. There is no candidate list, no retry
        // and no conflict handling here on purpose: two places deciding about chords is how
        // the feature came to ship dead in the first place.
        using var peekHotkey = new PeekHotkeySwitch(
            config.Settings.Get("peekHotkey"),
            () => PeekController.Show(manager, ipcManager, ConsoleUI.Log),
            ConsoleUI.Log);
        ui.PeekHotkeys = peekHotkey;

        // The pinned "Huddle Sessions" shortcut runs `huddle --peek`, which sets this
        // event rather than starting a second instance. Keyed to configDir by the same
        // hash as the singleton mutex above, so two huddle roots cannot summon each
        // other's overlay.
        using var peekSignalCts = new CancellationTokenSource();
        using var peekSignal = PeekSignal.Listen(
            configDir,
            () => PeekController.Show(manager, ipcManager, ConsoleUI.Log),
            peekSignalCts.Token,
            ConsoleUI.Log);

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

            // Never bare: a handler that throws must cost the operator a command, not the
            // console and the fleet attached to it (S2).
            var result = CommandGuard.Run(() => ui.HandleCommand(line), ConsoleUI.Log);
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
        // Stop the peek listener BEFORE the using-var disposals at the return below.
        // Disposing the event handle does not end that thread — WaitAny holds a ref on the
        // SafeWaitHandle, so a parked wait survives the dispose and would still deliver one
        // late summon into a half-torn-down huddle. Cancelling is the only thing that
        // actually reaches the wait, and without this call PeekSignal.Listen's cancellation
        // branch is unreachable in production: `using var` disposes a CancellationTokenSource,
        // it never cancels one.
        peekSignalCts.Cancel();
        // The hotkey has to stop HERE too, for the same reason and not at the `using var`
        // disposals at the return below: those run AFTER the three explicit disposals on
        // the next lines, so a chord pressed between the shutdown prompt and process exit
        // fires a summon that enumerates a session dictionary StopAll is tearing down and
        // then calls GetBacklog on a disposed IpcManager. Declaration order cannot fix
        // that — `using var` disposal happens at end of scope whatever the order is — so
        // this is an explicit dispose rather than a moved declaration. PeekHotkeySwitch.Dispose
        // is idempotent, so the using disposing it again on the way out is a no-op.
        peekHotkey.Dispose();
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
        var configPath = ConfigPathResolver.Resolve(args);   // shared scan (S6)

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
