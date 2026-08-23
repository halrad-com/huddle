using System.Collections.Concurrent;
using System.Text.Json;

namespace Huddle;

public class Orchestrator : IDisposable
{
    public const string HuddleMailbox = "_huddle";

    private readonly SessionManager _manager;
    private readonly IpcManager _ipc;
    private readonly TaskTracker _tasks;
    private readonly LedgerWriters _writers;
    private readonly Action<string> _log;
    private FileSystemWatcher? _watcher;
    private readonly WorkLedgerClaims _claims;
    private readonly WorkQueue _queue;
    private readonly ResourceLedger _resLedger;

    // Dedup window for FSW events. Writes that go through temp-file + rename
    // fire Created (sometimes), Renamed, and Changed for the same destination
    // path. The dedup window collapses those into a single ProcessCommandFile
    // call. Keyed on full path; TTL ~2s.
    private static readonly TimeSpan FswDedupWindow = TimeSpan.FromSeconds(2);
    private readonly ConcurrentDictionary<string, DateTime> _recentEvents =
        new(StringComparer.OrdinalIgnoreCase);

    // Periodic rescan backstop (B012). Windows FileSystemWatcher silently drops
    // events under load, on network paths, or for some rename-into-place
    // patterns — a command file can land in the inbox while huddle is live and
    // never fire an event, leaving the sender waiting forever for an ack. A
    // timer re-runs Scan() on an interval to recover anything the watcher
    // missed. The `scan` console verb does the same on demand. The interval is
    // configurable via huddle.json "rescanIntervalSeconds" (<=0 disables).
    private Timer? _rescanTimer;
    private int _rescanning; // 0/1 re-entrancy guard so a slow scan can't overlap itself

    // In-flight guard. A file can be handed to ProcessCommandFile by an FSW
    // event and by a periodic/manual Scan at the same time. Without this, both
    // would read and execute the same command (e.g. start-session twice).
    // First caller to claim the path processes it; the other skips.
    private readonly ConcurrentDictionary<string, byte> _inFlight =
        new(StringComparer.OrdinalIgnoreCase);

    // Parse-failure retry budget per command file. An unparseable file that
    // still looks mid-write stays in the inbox for the next scan; this cap
    // stops a permanently truncated file from retrying (and logging) forever.
    private const int MaxParseAttempts = 5;
    private readonly ConcurrentDictionary<string, int> _parseAttempts =
        new(StringComparer.OrdinalIgnoreCase);

    public TaskTracker Tasks => _tasks;

    public Orchestrator(SessionManager manager, IpcManager ipc, Action<string> log, LedgerWriters? writers = null)
    {
        _manager = manager;
        _ipc = ipc;
        _log = log;
        // One writer per repo, resolved through the SAME registry every other verb uses,
        // so the ledger can never disagree with what `start` or `shell` think a repo is.
        // The host passes its registry when it has one — mail ingestion needs the same
        // instances — and we build one otherwise.
        _writers = writers ?? new LedgerWriters(ResolveRepoRoot, log);
        // Tasks are rows in the assignee repo's event log now, not a dictionary that
        // emptied on restart. That is what stops T001 being issued 23 times and stops
        // task-complete nacking "unknown task" for work that really happened.
        _tasks = new TaskTracker(_writers, () => _manager.Repos.Keys, log);
        // GitWorktrees.Identify is what lets the ledger tell a SIBLING worktree of one repo
        // from an unrelated repo that merely shares a relative filename (I014 vs I008). It is
        // consulted only for the non-blocking merge-risk report; when git cannot answer, the
        // report narrows and nothing else changes.
        _claims = new WorkLedgerClaims(ipc.ClaimsDir, log, ResolveRepoRoot, GitWorktrees.Identify);
        _queue = new WorkQueue(ipc.QueueDir, log);
        _queue.Load();
        _resLedger = new ResourceLedger(ipc.ResLedgerDir, log);
    }

    /// <summary>
    /// Repo name (or alias) → its absolute root, read from the SessionManager registry that
    /// every other verb resolves against, so the ledger can never disagree with what `start`
    /// or `shell` think a repo is. Returns null for a name the registry does not know, which
    /// puts that comparison back on the pre-I013 name matching rather than guessing a root.
    ///
    /// I013: registered roots are nested (`myapp` inside `LIB-root` inside `workspace`),
    /// so one physical file has several repo-relative spellings. Handing the resolver to
    /// WorkLedgerClaims is what lets it compare files instead of names.
    /// </summary>
    /// <summary>
    /// The repo-name → root resolver, exposed so operator-facing views (the `conflicts` verb)
    /// can compare claims exactly as the arbiter does instead of building a second resolver
    /// or re-reading huddle.json. One registry, one answer.
    /// </summary>
    public Func<string, string?> RepoRootResolver => ResolveRepoRoot;

    private string? ResolveRepoRoot(string repo)
    {
        if (string.IsNullOrWhiteSpace(repo)) return null;
        return _manager.Repos.TryGetValue(_manager.ResolveRepoName(repo), out var def)
            ? def.Root
            : null;
    }

    public ResourceLedger ResLedger => _resLedger;

    public WorkQueue Queue => _queue;

    public void Start()
    {
        var inboxPath = Path.Combine(_ipc.IpcDir, HuddleMailbox, "inbox");
        var processedPath = Path.Combine(_ipc.IpcDir, HuddleMailbox, "processed");
        var failedPath = Path.Combine(_ipc.IpcDir, HuddleMailbox, "failed");
        Directory.CreateDirectory(inboxPath);
        Directory.CreateDirectory(processedPath);
        Directory.CreateDirectory(failedPath);

        // Subscribe to Created, Renamed, and Changed. Different writers
        // produce different events: direct File.WriteAllText fires Created;
        // temp-file + rename (Claude Code's Write tool, atomic-write libraries,
        // editors) fires Renamed into this directory; some flows also touch
        // an existing file and fire Changed. Missing any of these caused
        // commands to sit unread until a manual `scan`.
        _watcher = new FileSystemWatcher(inboxPath, "*.json")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite,
            EnableRaisingEvents = false
        };

        _watcher.Created += (_, e) => OnInboxEvent(e.FullPath, e.Name ?? "<unknown>");
        _watcher.Renamed += (_, e) => OnInboxEvent(e.FullPath, e.Name ?? "<unknown>");
        _watcher.Changed += (_, e) => OnInboxEvent(e.FullPath, e.Name ?? "<unknown>");

        _watcher.Error += (_, e) =>
        {
            _log($"Orchestrator: FileSystemWatcher error: {e.GetException().Message}");
        };

        _watcher.EnableRaisingEvents = true;
        _log($"Orchestrator: Watching {inboxPath}");

        _manager.SessionStateChanged += OnSessionStateChanged;

        // Process any commands that arrived before we started watching
        Scan();

        // Belt-and-braces: recover anything the watcher drops at runtime.
        var rescanSeconds = _manager.Config.Settings.Int("rescanIntervalSeconds");
        if (rescanSeconds > 0)
        {
            var interval = TimeSpan.FromSeconds(rescanSeconds);
            _rescanTimer = new Timer(_ => PeriodicRescan(), null, interval, interval);
            _log($"Orchestrator: periodic inbox rescan every {rescanSeconds}s");
        }
        else
        {
            _log("Orchestrator: periodic inbox rescan disabled (rescanIntervalSeconds <= 0)");
        }

        // Units whose dependencies completed while huddle was down (or whose state was
        // corrected on disk) are dispatchable now; nothing else re-examines the queue
        // until some session event does. Do it once at start.
        AdvanceQueue();
    }

    // Returns the number of command files actually processed (read + routed, or
    // moved to processed/failed). Files skipped because another caller is
    // already processing them are not counted.
    public int Scan()
    {
        var inboxPath = Path.Combine(_ipc.IpcDir, HuddleMailbox, "inbox");
        string[] files;
        try
        {
            files = Directory.GetFiles(inboxPath, "*.json").OrderBy(f => f).ToArray();
        }
        catch (Exception ex)
        {
            _log($"Orchestrator: scan failed to enumerate inbox: {ex.Message}");
            return 0;
        }

        int processed = 0;
        foreach (var file in files)
        {
            if (ProcessCommandFile(file)) processed++;
        }
        return processed;
    }

    // Timer callback. Skips if a scan is already running (a slow handler must
    // not let scans pile up). Only logs when it actually recovers something, so
    // an idle huddle stays quiet on its 30s heartbeat.
    private void PeriodicRescan()
    {
        if (Interlocked.Exchange(ref _rescanning, 1) == 1) return;
        try
        {
            var recovered = Scan();
            if (recovered > 0)
                _log($"Orchestrator: periodic rescan recovered {recovered} command(s) the watcher missed");
            EscalateUnackedTasks();
        }
        catch (Exception ex)
        {
            _log($"Orchestrator: periodic rescan error: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _rescanning, 0);
        }
    }

    /// <summary>
    /// Spec §5.5. Surface every task that has sat in <c>assigned</c> past
    /// <c>taskAckMinutes</c> — once each, to the dispatcher by mail and to the operator
    /// on the console.
    ///
    /// <para>Deliberately hung off the EXISTING rescan tick rather than given a timer of
    /// its own: this is a backstop, and its useful resolution is minutes. It also means
    /// escalation cannot outlive the loop that recovers dropped commands, so there is one
    /// heartbeat to reason about rather than two.</para>
    ///
    /// <para>"Already escalated" is read back out of the event log, so a restart does not
    /// re-announce every old assignment at once — which would turn the surface into
    /// exactly the nag the spec rules out.</para>
    /// </summary>
    private void EscalateUnackedTasks()
    {
        var ackAfter = TimeSpan.FromMinutes(_manager.Config.Settings.Int("taskAckMinutes"));
        if (ackAfter <= TimeSpan.Zero) return;
        var now = DateTimeOffset.UtcNow;

        foreach (var repo in _manager.Repos.Keys)
        {
            var writer = _writers.For(repo);
            if (writer is null) continue;
            try
            {
                var problems = new List<string>();
                var events = writer.ReadAll(problems);
                if (events.Count == 0) continue;

                var due = LedgerEscalation.Due(
                    TaskMaterializer.Materialize(events, problems),
                    LedgerEscalation.AlreadyEscalated(events), now, ackAfter);

                foreach (var t in due)
                {
                    _log(LedgerEscalation.ConsoleLine(repo, t, now));
                    var (to, subject, body) = LedgerEscalation.Mail(repo, t, now);
                    if (to != null)
                        _ipc.Send(HuddleMailbox, to.Replace(':', '_'), subject, body, "info");
                    // Recorded BEFORE anything can throw on the next task, so a failure
                    // mid-sweep cannot cause this one to be announced twice.
                    writer.Append(LedgerEscalation.EscalationEvent(t.Id, now));
                }
            }
            catch (Exception ex) { _log($"Orchestrator: escalation sweep failed for {repo}: {ex.Message}"); }
        }
    }

    // Single entry point from all three FSW event handlers (Created, Renamed,
    // Changed). Collapses duplicate signals for the same path within
    // FswDedupWindow so we don't process the same command twice. Opportunistic
    // cleanup of stale entries on each call keeps the dictionary bounded.
    private void OnInboxEvent(string fullPath, string name)
    {
        try
        {
            var now = DateTime.UtcNow;
            CleanupRecentEvents(now);

            var added = _recentEvents.TryAdd(fullPath, now);
            if (!added)
            {
                if (_recentEvents.TryGetValue(fullPath, out var prev) && (now - prev) < FswDedupWindow)
                    return; // duplicate event within window — skip
                _recentEvents[fullPath] = now;
            }

            Thread.Sleep(100); // Let file finish writing (rename can fire before content is flushed)
            ProcessCommandFile(fullPath);
        }
        catch (Exception ex)
        {
            _log($"Orchestrator: Error processing command {name}: {ex.Message}");
        }
    }

    private void CleanupRecentEvents(DateTime now)
    {
        foreach (var kv in _recentEvents)
        {
            if ((now - kv.Value) > FswDedupWindow)
                _recentEvents.TryRemove(kv.Key, out _);
        }
    }

    // Returns true if the file was acted on (read + routed, or moved out of the
    // inbox); false if it was skipped (already gone, or already being processed
    // by another caller). The in-flight claim makes FSW events and Scan() safe
    // to race on the same path — only the first claimant runs the command.
    private bool ProcessCommandFile(string filePath)
    {
        if (!File.Exists(filePath)) return false;

        // Claim the path. A concurrent FSW event + periodic/manual scan could
        // both reach the same file; whoever loses the TryAdd backs off.
        if (!_inFlight.TryAdd(filePath, 1)) return false;
        try
        {
            return ProcessCommandFileCore(filePath);
        }
        finally
        {
            _inFlight.TryRemove(filePath, out _);
        }
    }

    private bool ProcessCommandFileCore(string filePath)
    {
        // Re-check after claiming: a racing caller may have moved it out already.
        if (!File.Exists(filePath)) return false;

        var fileName = Path.GetFileName(filePath);
        var huddlePath = Path.Combine(_ipc.IpcDir, HuddleMailbox);

        string json;
        try { json = File.ReadAllText(filePath); }
        catch (Exception ex)
        {
            // Writer may still hold the file — an I/O race is not a malformed
            // command. Leave it in the inbox for the next event/scan to retry.
            _log($"Orchestrator: could not read {fileName} (retrying later): {ex.Message}");
            return false;
        }

        // Agent-authored commands share the mail defect: unescaped backslashes
        // (Windows paths, regex) in string values. TryParse repairs before
        // giving up — a dropped dispatch-batch costs real work, not just a
        // missed nudge. A file that fails BOTH parse and repair but looks
        // mid-write (no closing brace yet, or the writer touched it moments
        // ago) is left for the next scan; the attempt cap stops a permanently
        // truncated file from retrying forever.
        var msg = IpcManager.TryParse(json, fileName, _log);
        if (msg == null)
        {
            var attempts = _parseAttempts.AddOrUpdate(filePath, 1, (_, n) => n + 1);
            if (!IpcManager.LooksComplete(filePath, json) && attempts < MaxParseAttempts)
            {
                _log($"Orchestrator: {fileName} may still be mid-write — will retry (attempt {attempts}/{MaxParseAttempts})");
                return false;
            }
            _parseAttempts.TryRemove(filePath, out _);
            _log($"Orchestrator: Malformed command file {fileName} — moving to failed/");
            MoveFile(filePath, Path.Combine(huddlePath, "failed", fileName));
            return true;
        }
        _parseAttempts.TryRemove(filePath, out _);

        if (msg == null || msg.Type != "command")
        {
            // Not a command — move to processed (don't reprocess on next startup)
            MoveFile(filePath, Path.Combine(huddlePath, "processed", fileName));
            return true;
        }

        _log($"[CONSUMED] {msg.Subject} from={msg.From}");

        switch (msg.Subject)
        {
            case "start-session":
                HandleStartSession(msg);
                break;
            case "stop-session":
                HandleStopSession(msg);
                break;
            case "delegate-task":
                HandleDelegateTask(msg);
                break;
            case "task-complete":
                HandleTaskUpdate(msg, TaskState.Completed);
                break;
            case "task-failed":
                HandleTaskUpdate(msg, TaskState.Failed);
                break;
            case "task-progress":
                HandleTaskUpdate(msg, TaskState.InProgress);
                break;
            case "broadcast":
                HandleBroadcast(msg);
                break;
            case "dispatch-batch":
                HandleDispatchBatch(msg);
                break;
            case "claim":
                HandleClaim(msg);
                break;
            case "release":
                HandleRelease(msg);
                break;
            default:
                _log($"Orchestrator: Unknown command '{msg.Subject}'");
                SendNack(msg.From, msg.Subject, "unknown command");
                break;
        }

        MoveFile(filePath, Path.Combine(huddlePath, "processed", fileName));
        return true;
    }

    private void MoveFile(string source, string destination)
    {
        try
        {
            File.Move(source, destination, overwrite: true);
        }
        catch (Exception ex)
        {
            _log($"Orchestrator: Failed to move {Path.GetFileName(source)}: {ex.Message}");
        }
    }

    // Guard-rail: a persona named in a command must exist in personas/*.md.
    // The persona set changes over time (cut/renamed) — without this check an
    // unknown persona passes every gate, BuildPersonaPrompt fails late inside
    // Start, and the session silently never spawns. For dispatch-batch it is
    // worse: the claimed files are left locked behind a session that never came
    // up. A null/empty persona is allowed (base session, no persona file).
    // Returns true + a reason when the persona is named but not in the registry.
    private bool IsUnknownPersona(string? persona, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(persona)) return false;
        var available = _manager.GetAvailablePersonas();
        if (available.Contains(persona, StringComparer.OrdinalIgnoreCase)) return false;
        reason = $"unknown persona '{persona}' — valid: {string.Join(", ", available)}";
        return true;
    }

    private void HandleStartSession(IpcMessage msg)
    {
        try
        {
            var body = msg.BodyObject;
            var repo = body.GetProperty("repo").GetString() ?? "";
            var persona = body.TryGetProperty("persona", out var p) ? p.GetString() : null;
            var prompt = body.TryGetProperty("prompt", out var pr) ? pr.GetString() : null;
            var project = body.TryGetProperty("project", out var pj) ? pj.GetString() : null;

            if (IsUnknownPersona(persona, out var personaErr))
            {
                SendNack(msg.From, msg.Subject, personaErr);
                return;
            }

            var ok = _manager.Start(repo, persona, prompt: WithShellRules(prompt), project: project);
            if (ok)
            {
                // Attributed spawn announcement (2026-08-09 operator feedback: an
                // agent-spawned window must never surprise the operator — say who,
                // for which project, to do what).
                _log($"Orchestrator: {msg.From} spawned {repo}:{persona ?? "(bare)"} " +
                     $"[{(string.IsNullOrEmpty(project) ? "no-project" : project)}]" +
                     $"{(Snippet(prompt) is { } t ? $" — task: {t}" : "")}");
                SendAck(msg.From, msg.Subject, $"started {repo}");
            }
            else SendNack(msg.From, msg.Subject, $"failed to start {repo}");
        }
        catch (Exception ex)
        {
            _log($"Orchestrator: Error in start-session: {ex.Message}");
            SendNack(msg.From, msg.Subject, ex.Message);
        }
    }

    private void HandleStopSession(IpcMessage msg)
    {
        try
        {
            var instanceId = msg.BodyObject.GetProperty("instanceId").GetString() ?? "";

            var ok = _manager.Stop(instanceId);
            if (ok) SendAck(msg.From, msg.Subject, $"stopped {instanceId}");
            else SendNack(msg.From, msg.Subject, $"failed to stop {instanceId}");

            if (ok) ReportResourceLeaks(instanceId);
        }
        catch (Exception ex)
        {
            _log($"Orchestrator: Error in stop-session: {ex.Message}");
            SendNack(msg.From, msg.Subject, ex.Message);
        }
    }

    // B016: after a session stops, check its resource ledger for entries it
    // never marked cleaned. Always report; execute the recorded cleanup command
    // only when the operator opted in via huddle.json reclaimResourcesOnStop.
    private void ReportResourceLeaks(string instanceId)
    {
        try
        {
            var safeName = instanceId.Replace(':', '_');
            foreach (var (safe, entry) in _resLedger.FindLeaks().Where(l =>
                         l.SafeName.Equals(safeName, StringComparison.OrdinalIgnoreCase)))
            {
                _log(ResourceLedger.FormatLeak(safe, entry));
                if (_manager.Config.Settings.Bool("reclaimResourcesOnStop") && !string.IsNullOrWhiteSpace(entry.Cleanup))
                    RunReclaim(entry.Cleanup!);
            }
        }
        catch (Exception ex)
        {
            _log($"Orchestrator: resource-leak check failed for {instanceId}: {ex.Message}");
        }
    }

    // How long an opt-in reclaim command may run before huddle kills it. Cleanup is
    // "kill a process / delete a temp profile" shaped — anything longer is hung.
    private const int ReclaimTimeoutMs = 30_000;

    // F2 hardening (2026-07-12 review): the cleanup string is agent-authored, so the
    // execution must be observable and bounded — full command logged, output captured,
    // hard timeout with kill, process disposed, and one bad entry never aborts the
    // rest of the leak sweep. Still strictly opt-in via reclaimResourcesOnStop.
    private void RunReclaim(string cleanup)
    {
        _log($"Orchestrator: reclaim (opt-in): cmd.exe /c {cleanup}");
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c " + cleanup)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) { _log("Orchestrator: reclaim failed to start"); return; }

            // Read both streams to EOF (also prevents pipe-full deadlock), then wait.
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(ReclaimTimeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                _log($"Orchestrator: reclaim TIMED OUT after {ReclaimTimeoutMs / 1000}s and was killed: {cleanup}");
                return;
            }
            var outText = stdout.Result.Trim();
            var errText = stderr.Result.Trim();
            _log($"Orchestrator: reclaim exit {proc.ExitCode}" +
                 (outText.Length > 0 ? $" — {Truncate(outText, 200)}" : "") +
                 (errText.Length > 0 ? $" — stderr: {Truncate(errText, 200)}" : ""));
        }
        catch (Exception ex)
        {
            _log($"Orchestrator: reclaim failed: {ex.Message}");
        }

        static string Truncate(string s, int max) =>
            s.Length <= max ? s : s[..max] + "…";
    }

    private void HandleDelegateTask(IpcMessage msg)
    {
        try
        {
            var body = msg.BodyObject;
            var description = body.GetProperty("description").GetString() ?? "";
            var assignTo = body.GetProperty("assignTo").GetString() ?? "";
            var startIfNeeded = body.TryGetProperty("startIfNeeded", out var s) && s.GetBoolean();

            // Validate the persona named in assignTo ("repo:persona") before
            // creating the task — fail loud rather than create a task pointed at
            // a session that can never spawn.
            var parts = assignTo.Split(':', 2);
            var repo = parts[0];
            var persona = parts.Length > 1 ? parts[1] : null;
            if (IsUnknownPersona(persona, out var personaErr))
            {
                SendNack(msg.From, msg.Subject, personaErr);
                return;
            }

            // Create tracked task. Null means the assignee's repo has no ledger that can
            // be written, so there is nowhere to record the obligation — nack rather than
            // hand back a task that looks tracked and is gone at the next restart.
            var ledger = body.TryGetProperty("ledger", out var lg) ? lg.GetString() : null;
            var task = _tasks.Create(description, assignTo, msg.From, ledger);
            if (task is null)
            {
                SendNack(msg.From, msg.Subject, $"cannot track a task for {assignTo} — no writable ledger for its repo");
                return;
            }

            // Start target if needed. "Needed" means no session with that identity is
            // RUNNING — not merely that this huddle's roster has forgotten it. On
            // 2026-08-23 a delegate-task with startIfNeeded arrived after a reload had
            // emptied the roster and started a second otherapp:architect over a working
            // one; the task below then went to a mailbox two sessions were reading.
            // The mail is sent either way, so a live-but-untracked session still gets it.
            if (startIfNeeded && !_manager.Instances.ContainsKey(assignTo))
            {
                if (_manager.IsLiveButUntracked(assignTo, out var livePid))
                    _log($"Orchestrator: delegate-task to {assignTo} did NOT start a session — one is already running (PID {livePid}) " +
                         "but missing from the roster (reload?). The task mail is delivered to it. Run `recover` to re-adopt it.");
                else
                    _manager.Start(repo, persona, prompt: WithShellRules(description));
            }

            // Send task to target session
            var targetSafe = assignTo.Replace(':', '_');
            _ipc.Send(HuddleMailbox, targetSafe, $"task:{task.TaskId}", description, "task");

            SendAck(msg.From, msg.Subject, $"delegated {task.TaskId} to {assignTo}");
        }
        catch (Exception ex)
        {
            _log($"Orchestrator: Error in delegate-task: {ex.Message}");
            SendNack(msg.From, msg.Subject, ex.Message);
        }
    }

    private void HandleTaskUpdate(IpcMessage msg, TaskState state)
    {
        try
        {
            var body = msg.BodyObject;
            var taskId = body.GetProperty("taskId").GetString() ?? "";
            var notes = body.TryGetProperty("notes", out var n) ? n.GetString() : null;

            var ok = _tasks.UpdateState(taskId, state, notes);
            if (ok) SendAck(msg.From, msg.Subject, $"{taskId} -> {state}");
            else SendNack(msg.From, msg.Subject, $"unknown task {taskId}");
        }
        catch (Exception ex)
        {
            _log($"Orchestrator: Error in task update: {ex.Message}");
            SendNack(msg.From, msg.Subject, ex.Message);
        }
    }

    private const int BroadcastBodyCapBytes = 64 * 1024;

    private void HandleBroadcast(IpcMessage msg)
    {
        try
        {
            var body = msg.BodyObject;

            if (body.ValueKind != JsonValueKind.Object)
            {
                SendNack(msg.From, msg.Subject, "body must be an object");
                return;
            }

            if (!body.TryGetProperty("subject", out var subjElem) || subjElem.ValueKind != JsonValueKind.String)
            {
                SendNack(msg.From, msg.Subject, "subject required");
                return;
            }
            var subject = subjElem.GetString() ?? "";
            if (string.IsNullOrEmpty(subject))
            {
                SendNack(msg.From, msg.Subject, "subject required");
                return;
            }

            if (!body.TryGetProperty("body", out var fwdBody))
            {
                SendNack(msg.From, msg.Subject, "body field required");
                return;
            }

            var fwdType = body.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                ? (t.GetString() ?? "info")
                : "info";

            // Guardrail: no command-type broadcasts
            if (fwdType.Equals("command", StringComparison.OrdinalIgnoreCase))
            {
                SendNack(msg.From, msg.Subject, "type=command cannot be broadcast");
                return;
            }

            // Build forwarded body string: unwrap JSON strings, stringify objects/arrays/etc.
            string bodyText = fwdBody.ValueKind == JsonValueKind.String
                ? fwdBody.GetString() ?? ""
                : fwdBody.GetRawText();

            // Guardrail: 64 KB body cap (trigger channel, not file transfer)
            if (System.Text.Encoding.UTF8.GetByteCount(bodyText) > BroadcastBodyCapBytes)
            {
                SendNack(msg.From, msg.Subject, $"body exceeds {BroadcastBodyCapBytes} byte cap");
                return;
            }

            // Resolve exclude set (case-insensitive on instance IDs)
            var excludeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (body.TryGetProperty("exclude", out var exElem) && exElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in exElem.EnumerateArray())
                {
                    if (e.ValueKind == JsonValueKind.String)
                    {
                        var s = e.GetString();
                        if (!string.IsNullOrEmpty(s)) excludeSet.Add(s);
                    }
                }
            }

            // Resolve targets
            List<string> targets;
            var liveIds = _manager.Instances
                .Where(kv => kv.Value.IsAlive)
                .Select(kv => kv.Key)
                .ToList();

            if (!body.TryGetProperty("targets", out var tgtElem))
            {
                targets = liveIds;
            }
            else if (tgtElem.ValueKind == JsonValueKind.String)
            {
                var tgtStr = tgtElem.GetString() ?? "";
                if (!tgtStr.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    SendNack(msg.From, msg.Subject, "targets string must be \"all\"");
                    return;
                }
                targets = liveIds;
            }
            else if (tgtElem.ValueKind == JsonValueKind.Array)
            {
                targets = tgtElem.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString() ?? "")
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();
            }
            else
            {
                SendNack(msg.From, msg.Subject, "targets must be \"all\" or array");
                return;
            }

            // Optional repo scope: comma-delimited names/aliases. Unknown repo nacks —
            // a scoped broadcast that can't scope must not fan out wide.
            if (body.TryGetProperty("repo", out var repoElem))
            {
                if (repoElem.ValueKind != JsonValueKind.String)
                {
                    SendNack(msg.From, msg.Subject, "repo must be a comma-delimited string of repo names/aliases");
                    return;
                }
                var repoSet = BroadcastTargeting.ResolveRepoFilter(
                    repoElem.GetString() ?? "", _manager.ResolveRepoName, _manager.IsKnownRepo, out var repoErr);
                if (repoSet is null)
                {
                    SendNack(msg.From, msg.Subject, repoErr ?? "invalid repo filter");
                    return;
                }
                targets = targets.Where(id => BroadcastTargeting.MatchesRepo(id, repoSet)).ToList();
            }

            // Apply exclude
            targets = targets.Where(id => !excludeSet.Contains(id)).ToList();

            // One-line nudge typed into each target's console. Inject the
            // actual body (that's what the operator typed) — truncate if
            // it's huge so we stay a single-turn-sized nudge. Full mail is
            // still in inbox regardless. Newlines collapsed to keep this
            // one typed line; ASCII-only separator because the em-dash was
            // getting dropped by ConPTY translation.
            const int NudgeBodyCap = 500;
            var nudgeBody = bodyText.Length > NudgeBodyCap
                ? bodyText.Substring(0, NudgeBodyCap) + "... (truncated; full in inbox)"
                : bodyText;
            nudgeBody = nudgeBody.Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ");
            // Console broadcasts derive their subject from the message itself, so
            // "{subject}: {body}" would stutter ("hello: hello"). Drop the subject
            // segment whenever it is just a prefix of the body — it adds nothing.
            var nudge = nudgeBody.StartsWith(subject, StringComparison.Ordinal)
                ? $"[huddle broadcast from {msg.From}] {nudgeBody}"
                : $"[huddle broadcast from {msg.From}] {subject}: {nudgeBody}";

            // Fan out — fire-and-forget per target. `delivered` counts mail
            // files written (audit trail); `injected` counts consoles actually
            // poked. Normally equal, but injection can fail independently
            // (e.g. AttachConsole races with a session exiting).
            int delivered = 0, injected = 0, skipped = 0;
            foreach (var id in targets)
            {
                if (!_manager.Instances.TryGetValue(id, out var inst) || !inst.IsAlive)
                {
                    skipped++;
                    continue;
                }
                try
                {
                    _ipc.Send(msg.From, inst.SafePathName, subject, bodyText, fwdType, suppressAutoNudge: true);
                    delivered++;
                }
                catch (Exception ex)
                {
                    skipped++;
                    _log($"Orchestrator: broadcast to {id} failed: {ex.Message}");
                    continue;
                }

                // Pending-context delivery (drained by the session's hook) rather
                // than keystroke injection — never stomps an operator's prompt.
                _ipc.AppendPending(inst.SafePathName, nudge);
                injected++;
            }

            _log($"Orchestrator: broadcast '{subject}' from {msg.From} — delivered={delivered} injected={injected} skipped={skipped}");
            SendAck(msg.From, msg.Subject, $"broadcast delivered={delivered} injected={injected} skipped={skipped}");
        }
        catch (Exception ex)
        {
            _log($"Orchestrator: Error in broadcast: {ex.Message}");
            SendNack(msg.From, msg.Subject, ex.Message);
        }
    }

    private void HandleDispatchBatch(IpcMessage msg)
    {
        try
        {
            var body = msg.BodyObject;

            if (body.ValueKind != JsonValueKind.Object)
            {
                SendNack(msg.From, msg.Subject, "body must be an object");
                return;
            }

            if (!body.TryGetProperty("batchId", out var batchEl) || batchEl.ValueKind != JsonValueKind.String)
            {
                SendNack(msg.From, msg.Subject, "batchId required");
                return;
            }
            var batchId = batchEl.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(batchId))
            {
                SendNack(msg.From, msg.Subject, "batchId required");
                return;
            }

            if (!body.TryGetProperty("tasks", out var tasksEl) || tasksEl.ValueKind != JsonValueKind.Array)
            {
                SendNack(msg.From, msg.Subject, "tasks array required");
                return;
            }

            // Parse every task into a proposed claim. Fail-fast on schema errors.
            var proposed = new List<(WorkLedgerClaim claim, string persona, string prompt)>();
            var units = new List<WorkUnit>();
            int idx = 0;
            foreach (var t in tasksEl.EnumerateArray())
            {
                if (t.ValueKind != JsonValueKind.Object)
                {
                    SendNack(msg.From, msg.Subject, $"task[{idx}] must be an object");
                    return;
                }

                var repo = StringProp(t, "repo");
                var persona = StringProp(t, "persona");
                var prompt = StringProp(t, "prompt");
                if (string.IsNullOrWhiteSpace(repo) || string.IsNullOrWhiteSpace(persona))
                {
                    SendNack(msg.From, msg.Subject, $"task[{idx}] needs repo + persona");
                    return;
                }
                if (string.IsNullOrWhiteSpace(prompt))
                {
                    _log($"Orchestrator: dispatch-batch task[{idx}] has empty prompt — session will spawn without instruction");
                }

                var files = new List<string>();
                if (t.TryGetProperty("files", out var fEl) && fEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var fe in fEl.EnumerateArray())
                        if (fe.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(fe.GetString()))
                            files.Add(fe.GetString()!.Trim());
                }
                if (files.Count == 0)
                {
                    SendNack(msg.From, msg.Subject, $"task[{idx}] files required (declare scope)");
                    return;
                }

                // Resolve repo root for base sha (may be null if repo unknown — that's a hard error)
                var resolvedRepo = _manager.ResolveRepoName(repo);
                if (!_manager.Repos.TryGetValue(resolvedRepo, out var repoDef))
                {
                    SendNack(msg.From, msg.Subject, $"task[{idx}] unknown repo '{repo}'");
                    return;
                }

                if (IsUnknownPersona(persona, out var personaErr))
                {
                    SendNack(msg.From, msg.Subject, $"task[{idx}] {personaErr}");
                    return;
                }

                var baseSha = GitHelper.GetHeadSha(repoDef.Root) ?? "";
                var sessionId = $"{resolvedRepo}:{persona}";
                // Projects phase 1: optional project slug stamps the claim + unit +
                // spawned session, so the lens can bind live work to its project.
                var project = StringProp(t, "project");
                var claim = new WorkLedgerClaim(
                    SessionId: sessionId,
                    Repo: resolvedRepo,
                    BatchId: batchId,
                    ClaimedAt: DateTime.UtcNow,
                    BaseCommit: baseSha,
                    Files: files,
                    Project: project ?? ""
                );

                // Work-unit id: explicit `id` if the caller supplied one (needed so
                // siblings can name it in dependsOn), else a batch-unique fallback.
                // The id is carried as the claim's BatchId so a finishing session maps
                // back to its unit on release/auto-release.
                var unitId = StringProp(t, "id");
                if (string.IsNullOrWhiteSpace(unitId)) unitId = $"{batchId}#{idx}";
                var dependsOn = StringArrayProp(t, "dependsOn");

                proposed.Add((claim, persona, prompt));
                // Optional feature-ledger parent. `project` is a SLUG, not a ledger id, so
                // it is deliberately not reused here: a unit that names no ledger row
                // becomes an orphan task, which is the honest reading of dispatched work
                // nobody ideated — and counting those is half the point of `ledger orphans`.
                var ledgerParent = StringProp(t, "ledger");
                units.Add(new WorkUnit(unitId, resolvedRepo, persona, prompt, files, dependsOn,
                    Project: project, Ledger: string.IsNullOrWhiteSpace(ledgerParent) ? null : ledgerParent));
                idx++;
            }

            if (proposed.Count == 0)
            {
                SendNack(msg.From, msg.Subject, "batch contains no tasks");
                return;
            }

            // Step 0: duplicate-session check. Two tasks with the same
            // repo:persona resolve to the same claim file ({batchId}-{session}.md)
            // and the same un-suffixed sessionId — the second Write would silently
            // overwrite the first's claimed files, and a per-session rollback could
            // not tell them apart. Reject up front; split into separate batches or
            // use distinct personas.
            var dupSession = proposed
                .GroupBy(p => p.claim.SessionId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(g => g.Count() > 1);
            if (dupSession != null)
            {
                SendNack(msg.From, msg.Subject, $"batch names {dupSession.Key} twice — split into separate batches or use distinct personas");
                return;
            }

            // Step 1: self-overlap check
            var proposedClaims = proposed.Select(p => p.claim).ToList();
            var selfOverlaps = WorkLedgerClaims.FindOverlaps(proposedClaims, ResolveRepoRoot);
            if (selfOverlaps.Count > 0)
            {
                var detail = string.Join("; ", selfOverlaps.Select(o =>
                    $"{o.A.SessionId} and {o.B.SessionId} both claim {string.Join(", ", o.SharedFiles)}"));
                SendNack(msg.From, msg.Subject, $"self-overlap in batch: {detail}");
                return;
            }

            // Step 2: enqueue. The queue serializes rather than rejects: a unit
            // whose files overlap an active unit, or whose dependsOn isn't Done,
            // stays Queued and dispatches later via AdvanceQueue. Enqueue still
            // rejects structural errors (duplicate id, unknown dep, cycle).
            try
            {
                _queue.Enqueue(units);
            }
            catch (InvalidOperationException ex)
            {
                SendNack(msg.From, msg.Subject, ex.Message);
                return;
            }

            // Step 3: dispatch everything immediately dispatchable; the rest waits.
            AdvanceQueue();

            int dispatched = units.Count(u => _queue.StateOf(u.Id) == QueueState.Active);
            int queued = units.Count(u => _queue.StateOf(u.Id) == QueueState.Queued);
            int failed = units.Count(u => _queue.StateOf(u.Id) == QueueState.Failed);
            var failedNote = failed > 0 ? $" failed={failed}" : "";
            _log($"Orchestrator: dispatch-batch {batchId} — dispatched {dispatched}/{units.Count}, queued {queued}{failedNote}");
            SendAck(msg.From, msg.Subject, $"dispatched batch {batchId} dispatched={dispatched} queued={queued} total={units.Count}{failedNote}");
        }
        catch (Exception ex)
        {
            _log($"Orchestrator: Error in dispatch-batch: {ex.Message}");
            SendNack(msg.From, msg.Subject, ex.Message);
        }
    }

    // Runtime claim: the ledger entry point for sessions whose work did NOT arrive
    // via dispatch-batch (console-started, operator-typed, mail-triggered). Before
    // substantive edits a session claims its file scope — include the plan doc
    // itself in the list to lock a whole plan. The claim is recorded unconditionally
    // and any other holder is named back to the claimant; the two sessions settle it
    // between themselves. Recorded claims live in the same claims dir the queue
    // checks, so dispatch-batch still refuses to dispatch a unit over a runtime
    // claimant's files. Two agents executing one plan in parallel while unaware of
    // each other is the 2026-07-16 incident (ISSUES.md I005).
    private static int _runtimeClaimSeq;

    /// <summary>
    /// The merge-risk sentence appended to a claim ack, or "" when there is none (I014).
    /// Worded so it can never be read as a conflict: nobody is blocked, the files are not the
    /// same file, and the cost lands at merge time. Before this, the ledger reported that
    /// situation as silence and the two agents found out by talking to each other.
    /// </summary>
    private static string MergeRiskNote(IReadOnlyList<ClaimOverlap> warnings)
    {
        if (warnings.Count == 0) return "";
        var parts = warnings.Select(w =>
            $"{w.B.SessionId} holds {string.Join(", ", w.SharedFiles)} in " +
            (string.IsNullOrEmpty(w.B.Branch) ? "another checkout" : $"branch {w.B.Branch}"));
        return $" | MERGE RISK (not a conflict, you are not blocked): {string.Join("; ", parts)} — " +
               "different files on disk, but you will conflict at merge; tell them what you are changing";
    }

    private void HandleClaim(IpcMessage msg)
    {
        try
        {
            var body = msg.BodyObject;
            if (body.ValueKind != JsonValueKind.Object)
            {
                SendNack(msg.From, msg.Subject, "body must be an object");
                return;
            }

            var repo = StringProp(body, "repo");
            if (string.IsNullOrWhiteSpace(repo))
            {
                SendNack(msg.From, msg.Subject, "repo required");
                return;
            }
            var resolvedRepo = _manager.ResolveRepoName(repo);
            if (!_manager.Repos.TryGetValue(resolvedRepo, out var repoDef))
            {
                SendNack(msg.From, msg.Subject, $"unknown repo '{repo}'");
                return;
            }

            var files = new List<string>();
            if (body.TryGetProperty("files", out var fEl) && fEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var fe in fEl.EnumerateArray())
                    if (fe.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(fe.GetString()))
                        files.Add(fe.GetString()!.Trim());
            }
            if (files.Count == 0)
            {
                SendNack(msg.From, msg.Subject, "files array required (declare your edit scope; include the plan doc to lock a plan)");
                return;
            }

            var baseSha = GitHelper.GetHeadSha(repoDef.Root) ?? "";
            var claimId = $"R-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Interlocked.Increment(ref _runtimeClaimSeq)}";
            // Normalize owner identity: store the canonical InstanceId (the exact form
            // auto-release matches on) plus the owning conversation GUID, so a recycled
            // name can't shield the claim and DeleteAllForSession never silently misses it.
            var ownerInstance = ResolveOwner(msg.From);
            var ownerId = ownerInstance?.InstanceId ?? msg.From;
            var ownerGuid = ownerInstance?.SessionId?.ToString() ?? "";
            // Projects phase 1: caller may stamp the claim with its project slug;
            // fall back to the owning session's stamp so dispatched workers inherit.
            var claimProject = StringProp(body, "project") ?? ownerInstance?.Project ?? "";
            // I014: record the checkout the paths are relative to instead of leaving every
            // reader to infer it from the repo NAME. The owning session's own Root is the
            // truth, but only when it is claiming in its OWN repo — a session claiming
            // somewhere else spelled its paths from THAT repo's root, so that is the base.
            var claimRoot = ownerInstance != null &&
                            ownerInstance.RepoName.Equals(resolvedRepo, StringComparison.OrdinalIgnoreCase)
                ? ownerInstance.Root
                : repoDef.Root;
            // Informational only, never part of a collision decision; null on detached HEAD
            // or any git failure, which is recorded as "unknown" rather than treated as an error.
            var claimBranch = GitHelper.CurrentBranch(claimRoot) ?? "";
            var claim = new WorkLedgerClaim(ownerId, resolvedRepo, claimId, DateTime.UtcNow, baseSha, files, ownerGuid, claimProject,
                Root: claimRoot, Branch: claimBranch);

            // Reap orphans FIRST, so the overlaps reported below are overlaps with sessions
            // that actually exist. Recording never refuses any more, so a stale claim from an
            // exited session no longer blocks anyone — but it would still be named back to the
            // claimant as a holder to go negotiate with, and telling an agent to mail a session
            // that died two days ago is its own failure. This is the orchestrator's advantage
            // over `huddle --claim`: the CLI has no live roster and must report a stale holder
            // with its claim age and let the agent judge; here we can just resolve it.
            // ReapOrphanClaims (not the raw _claims.ReapOrphans) because it carries the
            // empty-live-roster guard: during an incomplete recovery every claim looks
            // orphaned, and reaping against an empty roster would archive every live claim.
            ReapOrphanClaims();

            // Record, never refuse. A nack needs an arbiter alive to send it, and on
            // 2026-08-16 there wasn't one: the claim mail sat unread and two sessions
            // edited the same files believing they were alone. The same claim through
            // `huddle --claim` is always written, so the mail path must behave
            // identically or the ledger means two different things depending on route.
            // (dispatch-batch still uses TryClaim — that is a planner pre-flight check
            // made before any work starts, not a runtime lock.)
            var result = LedgerCli.Claim(_claims, claim);
            // Merge risks are appended to whichever ack goes out, never substituted for a
            // conflict report: they are non-blocking by construction (different files on
            // disk) and must not dilute the "stop and talk" of a real overlap.
            var mergeNote = MergeRiskNote(result.MergeWarnings);
            if (result.Overlaps.Count == 0)
            {
                _log($"Orchestrator: claim recorded — {msg.From} holds {files.Count} file(s) in {resolvedRepo} ({claimId}){(mergeNote.Length > 0 ? " [merge risk]" : "")}");
                SendAck(msg.From, msg.Subject, $"claimed {files.Count} file(s) in {resolvedRepo} — release when done{mergeNote}");
                return;
            }

            // Name the repo on BOTH sides: a wildcard match against a legacy
            // no-repo claim is otherwise indistinguishable from a same-repo
            // conflict, and I008 taught us an unexplained holder wastes everyone's
            // time. (Post-I008 a cross-repo pair can only be a legacy claim.)
            // The holder's claim time is included for the cases reaping cannot decide: a
            // holder that is genuinely live but has sat on a scope for days is a different
            // negotiation from one that claimed it a minute ago, and only the claimant can
            // judge that. (A dead holder was already archived above.)
            var detail = string.Join("; ", result.Overlaps.Select(o =>
                $"{o.B.SessionId} also holds {string.Join(", ", o.SharedFiles)} in " +
                $"{(string.IsNullOrEmpty(o.B.Repo) ? "(unrecorded repo — legacy claim)" : o.B.Repo)} " +
                $"since {o.B.ClaimedAt.ToUniversalTime():yyyy-MM-dd HH:mm}Z"));
            _log($"Orchestrator: claim recorded WITH OVERLAP — {msg.From} (repo {resolvedRepo}): {detail}");
            SendAck(msg.From, msg.Subject,
                $"claimed {files.Count} file(s) in {resolvedRepo}; {detail} — mail them and agree who goes first before editing those files{mergeNote}");
        }
        catch (Exception ex)
        {
            _log($"Orchestrator: Error in claim: {ex.Message}");
            SendNack(msg.From, msg.Subject, ex.Message);
        }
    }

    private void HandleRelease(IpcMessage msg)
    {
        try
        {
            var body = msg.BodyObject;

            if (body.ValueKind != JsonValueKind.Object ||
                !body.TryGetProperty("files", out var fEl) ||
                fEl.ValueKind != JsonValueKind.Array)
            {
                SendNack(msg.From, msg.Subject, "files array required");
                return;
            }

            var files = new List<string>();
            foreach (var fe in fEl.EnumerateArray())
                if (fe.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(fe.GetString()))
                    files.Add(fe.GetString()!.Trim());

            if (files.Count == 0)
            {
                SendNack(msg.From, msg.Subject, "files list is empty");
                return;
            }

            // Match claims by the SAME canonical identity HandleClaim stored under, so a
            // release still finds a claim written under the normalized InstanceId form.
            var owner = ResolveOwner(msg.From)?.InstanceId ?? msg.From;

            // Snapshot the session's claimed work-units before releasing so we can
            // tell which claim files fully disappear (= unit done) vs. shrink.
            var beforeUnits = _claims.ReadAll()
                .Where(c => c.SessionId.Equals(owner, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.BatchId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var released = _claims.Release(owner, files);
            if (released == 0)
            {
                _log($"Orchestrator: release from {msg.From} — no matching claim for {string.Join(", ", files)}");
                SendNack(msg.From, msg.Subject, "no matching claim");
            }
            else
            {
                _log($"Orchestrator: release from {msg.From} — {released} file(s)");

                // A unit whose claim file is now gone (all its files released) is done.
                var afterUnits = _claims.ReadAll()
                    .Where(c => c.SessionId.Equals(owner, StringComparison.OrdinalIgnoreCase))
                    .Select(c => c.BatchId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var doneUnits = beforeUnits.Where(b => !afterUnits.Contains(b)).ToList();
                foreach (var id in doneUnits) _queue.MarkDone(id); // no-op for runtime R-* claims

                // Any successful release may free a file a queued unit needs —
                // including a partial release (claim shrinks, no unit done) and
                // runtime-claim releases the queue can't see. Always advance.
                AdvanceQueue();

                SendAck(msg.From, msg.Subject, $"released {released}");
            }
        }
        catch (Exception ex)
        {
            _log($"Orchestrator: Error in release: {ex.Message}");
            SendNack(msg.From, msg.Subject, ex.Message);
        }
    }

    private void OnSessionStateChanged(SessionInstance instance, SessionStatus newStatus)
    {
        // Release claims when the session has finished. SessionManager fires
        // SessionStateChanged with Stopped (normal) or Crashed (detected in Poll).
        if (newStatus != SessionStatus.Stopped && newStatus != SessionStatus.Crashed)
            return;

        try
        {
            AuditAndReleaseClaims(instance, newStatus);
        }
        catch (Exception ex)
        {
            _log($"Orchestrator: auto-release error for {instance.InstanceId}: {ex.Message}");
        }

        try
        {
            SettleUnitsForSession(instance, newStatus);
        }
        catch (Exception ex)
        {
            _log($"Orchestrator: unit settle error for {instance.InstanceId}: {ex.Message}");
        }
    }

    /// <summary>
    /// A stopped session's Active units must be settled whether or not a claim file still
    /// exists. AuditAndReleaseClaims settles only through the claims it finds — and an
    /// agent that ran `huddle --release` before exiting (the documented commit-then-release
    /// idiom) leaves none, so its unit stayed Active forever, its dependents never
    /// dispatched, and the pipeline silently stalled (2026-08-22: settings and ledger-p1
    /// both finished, both released, both stopped, phase 2 and stats never started).
    /// Same rule as the claim path: Done needs a clean Stop AND commits in the unit's repo
    /// since the unit was dispatched; anything else is Failed and the operator re-dispatches.
    /// </summary>
    private void SettleUnitsForSession(SessionInstance instance, SessionStatus endStatus)
    {
        var sessionId = instance.InstanceId;
        foreach (var (unit, state) in _queue.All())
        {
            if (state != QueueState.Active) continue;
            if (!$"{unit.Repo}:{unit.Persona}".Equals(sessionId, StringComparison.OrdinalIgnoreCase)) continue;

            var root = _manager.Repos.TryGetValue(unit.Repo, out var def) ? def.Root : null;
            var since = _queue.PersistedAt(unit.Id);
            var commits = root != null && since != null
                ? GitHelper.CommitsSince(root, since.Value.ToString("yyyy-MM-ddTHH:mm:ssZ"))
                : 0;
            var finished = endStatus == SessionStatus.Stopped && commits > 0;
            // Done means DELIVERED, not accepted (§5.3). The queue's own states are
            // unchanged; what changes is that "someone stopped having committed" no longer
            // reads as "the work was any good" — that is `ledger accept`, and it is a
            // separate deliberate act with a gate.
            var writer = _writers.For(unit.Repo);
            if (finished)
            {
                _queue.MarkDone(unit.Id);
                var note = $"{sessionId} stopped with {commits} commit(s) since dispatch";
                _log($"Orchestrator: unit {unit.Id} -> Done — {note}");
                if (writer is not null)
                    WorkQueueLedger.OnSettled(writer, unit, QueueState.Done, note, DateTimeOffset.UtcNow, _log);
            }
            else
            {
                _queue.MarkFailed(unit.Id);
                var note = $"{sessionId} {endStatus.ToString().ToLowerInvariant()} with no commits since dispatch";
                _log($"Orchestrator: unit {unit.Id} -> Failed — {note}; dependents stay queued. Re-dispatch to retry.");
                if (writer is not null)
                    WorkQueueLedger.OnSettled(writer, unit, QueueState.Failed, note, DateTimeOffset.UtcNow, _log);
            }
        }
        AdvanceQueue();
    }

    /// <summary>
    /// Resolve an IPC sender id (as an agent typed it) to its tracked instance. Handles a
    /// direct InstanceId hit and the repo-alias colon form. Returns null when untracked.
    /// </summary>
    private SessionInstance? ResolveOwner(string from) =>
        _manager.Instances.TryGetValue(from, out var direct) ? direct : _manager.ResolveInstance(from);

    /// <summary>
    /// Archive claims whose owning session instance is no longer live. Called after session
    /// recovery at startup and on demand from the `conflicts` verb, so a stranded claim (dead
    /// instance, or a name since reused by a new one) can never block a live claimant forever.
    /// Guard: if recovery reports ZERO live instances we skip — an empty live set is
    /// indistinguishable from recovery not having populated instances, and reaping then would
    /// wrongly archive every live session's claims.
    /// </summary>
    // I010 F5: dispatched contexts don't inherit persona shell rules; carry them in
    // the task prompt itself. Empty prompts stay empty (nothing to instruct).
    private static string? WithShellRules(string? prompt) =>
        string.IsNullOrWhiteSpace(prompt) ? prompt : SessionManager.ShellDisciplinePreamble + prompt;

    // One-line task snippet for spawn announcements; null when there is no task.
    private static string? Snippet(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return null;
        var t = prompt.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return t.Length > 70 ? t[..70] + "…" : t;
    }

    // The current live roster as the claim arbiter sees it — canonical id, conversation
    // GUID, start time. Built fresh at each use (never cached): a session started moments
    // ago must appear here so its own just-written claim is never mistaken for an orphan.
    private List<WorkLedgerClaims.LiveInstance> LiveRoster() =>
        _manager.Instances.Values
            .Where(i => i.IsAlive)
            .Select(i => new WorkLedgerClaims.LiveInstance(i.InstanceId, i.SessionId, i.StartedAt))
            .ToList();

    public void ReapOrphanClaims()
    {
        try
        {
            var live = LiveRoster();

            if (live.Count == 0)
            {
                var present = _claims.ReadAll().Count;
                if (present > 0)
                    _log($"Orchestrator: {present} claim(s) present but 0 live instances — skipping orphan reap (run `conflicts` once sessions are up).");
                return;
            }

            var reaped = _claims.ReapOrphans(live);
            if (reaped.Count > 0)
            {
                _log($"Orchestrator: reaped {reaped.Count} orphan claim(s) — archived under claims/archived-orphan-*:");
                foreach (var c in reaped)
                    _log($"  - {c.SessionId} ({c.BatchId}): {string.Join(", ", c.Files)}");
            }
        }
        catch (Exception ex)
        {
            _log($"Orchestrator: orphan reap error: {ex.Message}");
        }
    }

    private void AuditAndReleaseClaims(SessionInstance instance, SessionStatus endStatus)
    {
        var sessionId = instance.InstanceId;
        var removed = _claims.DeleteAllForSession(sessionId);
        if (removed.Count == 0) return;

        foreach (var claim in removed)
        {
            if (!_manager.Repos.TryGetValue(claim.Repo, out var repoDef))
            {
                _log($"Orchestrator: audit skipped for {sessionId} (repo '{claim.Repo}' unknown)");
                continue;
            }

            var declared = new HashSet<string>(claim.Files, StringComparer.OrdinalIgnoreCase);

            // Committed since base
            var committed = !string.IsNullOrEmpty(claim.BaseCommit)
                ? GitHelper.DiffNames(repoDef.Root, claim.BaseCommit)
                : new List<string>();

            // Working-tree dirty right now
            var dirty = GitHelper.StatusDirty(repoDef.Root);

            var creep = committed
                .Concat(dirty)
                .Where(f => !declared.Contains(f))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var uncommittedClaimed = dirty
                .Where(f => declared.Contains(f))
                .ToList();

            if (creep.Count > 0)
                _log($"Orchestrator: scope creep — {sessionId} touched {string.Join(", ", creep)} outside declared scope (batch {claim.BatchId})");

            if (uncommittedClaimed.Count > 0)
                _log($"Orchestrator: uncommitted changes at stop — {sessionId} left {string.Join(", ", uncommittedClaimed)} dirty (batch {claim.BatchId})");

            _log($"Orchestrator: released {claim.Files.Count} file claim(s) for {sessionId} (batch {claim.BatchId})");

            // The claim's BatchId carries the work-unit id. A session going away is
            // NOT evidence its unit finished: a crash, or a clean stop with nothing
            // committed to the declared files, used to mark the unit Done — which
            // unblocked dependents and spawned the next session on a foundation that
            // never landed (2026-08-22: three huddle sessions killed, a fourth
            // auto-spawned). Done needs a clean stop AND a commit touching the
            // declared scope; anything else is Failed, dependents stay queued, and the
            // operator re-dispatches. No-op for claims not tracked by the queue.
            var committedDeclared = committed.Where(f => declared.Contains(f)).ToList();
            var finished = endStatus == SessionStatus.Stopped && committedDeclared.Count > 0;
            if (finished)
                _queue.MarkDone(claim.BatchId);
            else if (_queue.StateOf(claim.BatchId) == QueueState.Active)
            {
                _queue.MarkFailed(claim.BatchId);
                _log($"Orchestrator: unit {claim.BatchId} -> Failed — {sessionId} {endStatus.ToString().ToLowerInvariant()} with no commits to its declared files; dependents stay queued. Re-dispatch to retry.");
            }
        }

        // A finished unit may have freed files or satisfied a dependency — dispatch
        // anything that became dispatchable.
        AdvanceQueue();
    }

    // Dispatch every currently-dispatchable queued unit: claim its files, spawn it,
    // mark Active. A unit that overlaps an active unit's files or has an unfinished
    // dependsOn is not returned by Dispatchable() and stays queued until a later
    // advance (triggered when an active unit's session releases/stops).
    private void AdvanceQueue()
    {
        foreach (var u in _queue.Dispatchable())
        {
            var root = _manager.Repos.TryGetValue(u.Repo, out var def) ? def.Root : ".";
            var baseSha = GitHelper.GetHeadSha(root) ?? "";
            var sessionId = $"{u.Repo}:{u.Persona}";

            // The queue only knows about its own units; a runtime claim (claim
            // command, or `huddle --claim`) is invisible to Dispatchable(). So check
            // the LEDGER before dispatching: a unit whose files are already claimed
            // stays queued and retries on the next advance (a release/stop always
            // triggers one). Reap-on-nack: a block by a DEAD session's stale claim is
            // cleared inline rather than parking the unit until the next
            // startup/`conflicts` sweep. The roster is rebuilt per iteration so a unit
            // dispatched earlier in THIS advance is live and its own fresh claim is
            // never reaped.
            //
            // Scope of the guarantee, precisely: TryClaim's check-and-write is atomic
            // against other callers IN THIS PROCESS (the in-process `_lock`) — which
            // covers the mail `claim` path and auto-release, both of which run here.
            // It does NOT take the machine-scoped named mutex that RecordWithOverlaps
            // uses, so a `huddle --claim` running in a SEPARATE process could interleave
            // between this read and this write. The window is microseconds and the risk
            // is pre-existing; deliberately not closed, because dispatch-batch depends on
            // TryClaim's exact refuse-on-conflict behaviour and widening it is a bigger
            // change than the exposure warrants. Do not describe this as a general
            // cross-process critical section — it is not one.
            if (!_claims.TryClaim(new WorkLedgerClaim(sessionId, u.Repo, u.Id, DateTime.UtcNow, baseSha, u.Files), LiveRoster(), out var extConflicts))
            {
                var detail = string.Join("; ", extConflicts.Select(o =>
                    $"{o.B.SessionId} holds {string.Join(", ", o.SharedFiles)}"));
                _log($"queue: {u.Id} blocked by active claim — {detail}; stays queued");
                continue;
            }

            var ok = _manager.Start(u.Repo, u.Persona, prompt: WithShellRules(u.Prompt), project: u.Project);
            if (ok)
            {
                _queue.MarkActive(u.Id);
                // A dispatched unit IS an obligation, and until now it existed only in the
                // queue's own state file. Opening a task row makes it visible to `ledger`
                // and to the ack clock alongside every other kind of assignment.
                if (_writers.For(u.Repo) is { } w) WorkQueueLedger.OnDispatched(w, u, DateTimeOffset.UtcNow, _log);
                _log($"queue: dispatched {u.Id} -> {sessionId} " +
                     $"[{(string.IsNullOrEmpty(u.Project) ? "no-project" : u.Project)}]" +
                     $"{(Snippet(u.Prompt) is { } t ? $" — task: {t}" : "")}");
            }
            else
            {
                _claims.Release(sessionId, u.Files);
                _queue.MarkFailed(u.Id);
                _log($"queue: {u.Id} failed to start — released its claim");
                // Opened above only if the session actually started, so there is usually
                // no row here; OnSettled is a no-op when there is none.
                if (_writers.For(u.Repo) is { } fw)
                    WorkQueueLedger.OnSettled(fw, u, QueueState.Failed, "failed to start", DateTimeOffset.UtcNow, _log);
            }
        }
    }

    private static string StringProp(JsonElement obj, string name)
    {
        return obj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() ?? ""
            : "";
    }

    private static IReadOnlyList<string> StringArrayProp(JsonElement obj, string name)
    {
        var result = new List<string>();
        if (obj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Array)
            foreach (var item in e.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    result.Add(item.GetString()!.Trim());
        return result;
    }

    private void SendAck(string toFrom, string command, string result)
    {
        var senderSafe = toFrom.Replace(':', '_');
        _ipc.Send(HuddleMailbox, senderSafe, $"ack:{command}", result, "info", suppressAutoNudge: true);
        InjectReply(toFrom, $"[huddle ack:{command}] {result}");
    }

    // Command rejection — distinct subject prefix so senders can distinguish
    // success from failure without parsing body strings.
    private void SendNack(string toFrom, string command, string reason)
    {
        var senderSafe = toFrom.Replace(':', '_');
        _ipc.Send(HuddleMailbox, senderSafe, $"nack:{command}", reason, "info", suppressAutoNudge: true);
        InjectReply(toFrom, $"[huddle nack:{command}] {reason}");
    }

    // Fire a prompt-injection nudge into the sender's console if it resolves
    // to a live session. Mail is still written for the audit trail; this is
    // the extra signal so the sender's agent actually sees the reply as a
    // turn without waiting for a manual 'read your inbox' nudge. Non-session
    // callers (the UI console, the orchestrator mailbox, external scripts)
    // simply don't resolve and are skipped.
    private void InjectReply(string toFrom, string text)
    {
        if (string.IsNullOrEmpty(toFrom)) return;

        if (!_manager.Instances.TryGetValue(toFrom, out var inst))
            inst = _manager.ResolveInstance(toFrom);

        if (inst == null || !inst.IsAlive) return;

        // Pending-context delivery (drained by the session's hook) rather than
        // keystroke injection — never stomps an operator's in-progress prompt.
        // ack/nack replies are notifications, not interruptions: deliver them
        // non-blocking so the Stop hook surfaces them quietly (additionalContext)
        // instead of a decision:block the CLI renders as a "Stop hook error".
        _ipc.AppendPending(inst.SafePathName, text, blocking: false);
    }

    public void Dispose()
    {
        _manager.SessionStateChanged -= OnSessionStateChanged;

        _rescanTimer?.Dispose();
        _rescanTimer = null;

        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
    }
}
