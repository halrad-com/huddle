using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Huddle;

public class IpcMessage
{
    [JsonPropertyName("from")]
    public string From { get; set; } = "";

    [JsonPropertyName("to")]
    public string To { get; set; } = "";

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "info";

    [JsonPropertyName("subject")]
    public string Subject { get; set; } = "";

    [JsonPropertyName("body")]
    public JsonElement Body { get; set; }

    [JsonIgnore]
    public string BodyText => Body.ValueKind == JsonValueKind.String
        ? Body.GetString() ?? ""
        : Body.ValueKind == JsonValueKind.Undefined ? ""
        : Body.GetRawText();

    /// <summary>
    /// Get body as a JSON object element, parsing from string if needed.
    /// Handles both {"body": {...}} and {"body": "{...}"} formats.
    /// </summary>
    [JsonIgnore]
    public JsonElement BodyObject
    {
        get
        {
            if (Body.ValueKind == JsonValueKind.Object)
                return Body;
            if (Body.ValueKind == JsonValueKind.String)
            {
                var text = Body.GetString();
                if (!string.IsNullOrEmpty(text))
                    return JsonDocument.Parse(text).RootElement;
            }
            return Body;
        }
    }
}

/// <summary>
/// Mail delivery appends a wake line to the recipient's pending.txt, which its Stop /
/// UserPromptSubmit hooks drain — but those fire on a TURN BOUNDARY, and an idle session
/// ends no turn and submits nothing. Mail to an idle agent was therefore a dead letter
/// until a human typed into that console (2026-08-22: two fix tasks sat 27 minutes with
/// both recipients idle; the operator injected by hand). This decides when huddle nudges
/// the console itself, so the hook has a submit to fold the pending context onto.
/// </summary>
public static class MailWake
{
    /// <summary>Deliberately content-free: the pending line the hook folds in carries the
    /// sender, subject and path. This is only the submit that makes the hook fire.</summary>
    public const string WakeLine = "[huddle] you have mail";

    /// <summary>Short enough that a session mid-thought is nudged promptly, long enough
    /// that an agent between tool calls is not mistaken for an idle one.</summary>
    public static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(20);

    /// <summary>~/.claude/projects — where Claude Code keeps session transcripts.</summary>
    public static string ProjectsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    /// <summary>
    /// Pure form. Unknown activity counts as idle: a session with no transcript yet has
    /// certainly not drained anything, and a spurious nudge costs one line whereas a
    /// missed one costs the obligation.
    /// </summary>
    public static bool ShouldWake(DateTime? lastActivity, DateTime now, TimeSpan idleAfter) =>
        lastActivity is null || now - lastActivity.Value >= idleAfter;

    /// <summary>
    /// The clock pairing, in one place because getting it wrong is silent.
    /// <see cref="SessionTrouble.LastActivity"/> returns the transcript mtime in LOCAL
    /// time; comparing it against <c>UtcNow</c> yields a negative age on any UTC+n
    /// machine, so nothing ever reads as idle and the wake never fires. Both halves live
    /// here so a test can exercise the pair rather than the arithmetic alone.
    /// </summary>
    public static bool ShouldWakeSession(string? transcriptPath, TimeSpan idleAfter)
    {
        var last = transcriptPath is { Length: > 0 } && File.Exists(transcriptPath)
            ? SessionTrouble.LastActivity(transcriptPath)
            : null;
        return ShouldWake(last, DateTime.Now, idleAfter);
    }

    /// <summary>True when a session has queued context worth waking it for. The retry
    /// tick walks every watched session, so this keeps it from injecting into consoles
    /// with an empty queue.</summary>
    public static bool HasPending(string pendingPath)
    {
        try
        {
            if (!File.Exists(pendingPath)) return false;
            foreach (var line in File.ReadLines(pendingPath))
                if (!string.IsNullOrWhiteSpace(line)) return true;
            return false;
        }
        catch { return false; }
    }
}

public static class PendingWake
{
    /// <summary>
    /// The text huddle types into a session's console to wake it. THE only producer of
    /// injected wake text — both the delivery path and the retry tick's re-drive call
    /// this, and neither picks a string of its own.
    ///
    /// <para>That single-producer property is the point, not a tidiness preference. The
    /// regression this fixes was a call site quietly swapping the real line for the
    /// contentless <see cref="MailWake.WakeLine"/> carrier, and nothing failing. There is
    /// now no call site that chooses: delivery appends the nudge to pending.txt and then
    /// asks this what to type, so the file is the single source of truth for what a
    /// session is being woken about, and dropping the sender means breaking the tests
    /// below rather than editing a lambda.</para>
    ///
    /// <para>The newest line leads because it is the arrival being announced; a count
    /// covers anything still queued behind it. The bare carrier survives only as the
    /// cannot-read fallback — a contentless wake still beats the dead letter that adding
    /// the wake fixed in the first place.</para>
    /// </summary>
    public static string LineFor(string pendingPath)
    {
        try
        {
            var lines = File.ReadAllLines(pendingPath)
                            .Select(Strip)
                            .Where(l => l.Length > 0)
                            .ToArray();
            if (lines.Length == 0) return MailWake.WakeLine;

            var last = lines[^1];
            return lines.Length == 1 ? last : $"{last}  (+{lines.Length - 1} more queued)";
        }
        catch { return MailWake.WakeLine; }
    }

    /// <summary>
    /// Drop the non-blocking marker AppendPending prefixes (<see
    /// cref="IpcManager.InfoPendingSentinel"/>). The hook strips it before display; this
    /// path types the line straight into a console, so it has to strip it too or the
    /// control character goes to the terminal.
    /// </summary>
    private static string Strip(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return "";
        var s = line.Trim();
        return s.Length > 0 && s[0] == IpcManager.InfoPendingSentinel ? s[1..].Trim() : s;
    }
}

public class IpcManager : IDisposable
{
    private readonly string _ipcDir;
    private readonly Action<string> _log;
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new();
    private readonly object _watchersLock = new();

    // Mail files already announced to a session, keyed by safe path name. Mail now
    // stays in inbox/ until the agent acknowledges it (inbox = unread), so without
    // this every rescan, retry tick and huddle restart would re-announce the same
    // unread mail. Persisted per session under ipc/<safe>/delivered.txt so a restart
    // does not undo it. See MailReceipts.
    private readonly ConcurrentDictionary<string, HashSet<string>> _delivered = new();
    private readonly object _deliveredLock = new();

    // Per-path dedupe window — same idea as Orchestrator. Atomic writes
    // (temp + rename) fire Renamed *and* Changed for the same destination;
    // without this we'd process the same mail twice.
    private static readonly TimeSpan FswDedupWindow = TimeSpan.FromSeconds(2);
    private readonly ConcurrentDictionary<string, DateTime> _recentEvents =
        new(StringComparer.OrdinalIgnoreCase);

    // Periodic inbox rescan. A nudge that couldn't be delivered — most often
    // because the operator was typing at the recipient's console and injection
    // was held (see PromptInjector.OperatorBusy) — leaves the mail in inbox/.
    // Nothing else re-drives it until the next session-start Watch() or FSW
    // event, so a held nudge could sit indefinitely after the operator freed
    // the console. This timer re-runs delivery for still-pending mail, so a held
    // nudge lands within one interval of the operator stepping away.
    private readonly ConcurrentDictionary<string, string> _watchedInstances =
        new(); // safePathName → instanceId
    private System.Threading.Timer? _retryTimer;
    private int _retryRunning; // 0/1 re-entrancy guard for the timer callback
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(4);
    // Only retry files older than this — fresh arrivals are the FSW's job, and
    // this keeps the timer from racing an in-flight FSW delivery of the same file.
    private static readonly TimeSpan RetryMinAge = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Fired after a new message is successfully read from a session inbox.
    /// Host (Program) wires this to PromptInjector to wake the recipient with
    /// a short signal ("you got mail at X") — the actual mail FILE stays in
    /// inbox/ so the recipient can read the real body, reply by writing mail,
    /// and move the file to processed/ when they're done. We are emulating
    /// email, not pasting bodies into prompts.
    ///
    /// The handler returns true if the wake signal was delivered (recipient
    /// alive + inject succeeded), false otherwise. We use this only to decide
    /// whether to record a "already-nudged" snapshot so a Watch() rescan
    /// doesn't re-fire the same mail. The file is NOT moved either way.
    /// </summary>
    public event Func<string /*instanceId*/, IpcMessage, string /*filePath*/, bool>? MessageReceived;

    /// <summary>
    /// Every parsed inbox file, on every pass — including mail that was announced long
    /// ago and is still sitting unread. <see cref="MessageReceived"/> fires ONCE per file
    /// and is the wrong signal for anything durable.
    ///
    /// <para>This exists because the feature ledger was wired to the nudge. Ingest sat
    /// behind the already-announced early return, so a <c>type:"task"</c> mail announced
    /// before the ledger shipped could never open a row — and nothing backfilled, because
    /// announcement happens once and never repeats. Two real assignments sat unread and
    /// untracked for eight and ten days while every ledger surface reported nothing
    /// open.</para>
    ///
    /// <para>So: the OBLIGATION comes from the mail being there; the NUDGE comes from
    /// announcement. Handlers must be idempotent — this fires on every scan for as long
    /// as the mail stays unread — and cheap, for the same reason.</para>
    /// </summary>
    public Action<string /*instanceId*/, IpcMessage, string /*filePath*/>? MailSeen;

    /// <summary>
    /// Nudge an idle session so its hook drains pending.txt. Set by the host (Program),
    /// which owns process handles and transcript paths — IpcManager stays free of both.
    /// Returns true when a wake was actually injected. Called from the retry tick for
    /// sessions that still have queued context: <see cref="MessageReceived"/> fires once
    /// per mail file and never again, so a session that idles AFTER delivery, or whose
    /// wake was held because the operator was typing, has no other route back.
    /// </summary>
    public Func<string /*safePathName*/, bool>? WakeIdle;

    /// <summary>
    /// Mail that has just LEFT a session's inbox — the agent moved it to processed/, or
    /// huddle cleared an original whose processed/ copy appeared. Moving mail out of the
    /// inbox already means "I have read this", so spec §5.4 reuses it as the
    /// acknowledgement signal rather than inventing a second one. Set by the host
    /// (Program), which knows which repo's ledger to append to; IpcManager knows only
    /// that a file moved. File NAMES, not paths — the host makes them relative.
    /// </summary>
    public Action<string /*safePathName*/, IReadOnlyList<string> /*mailFileNames*/>? MailAcknowledged;

    public string IpcDir => _ipcDir;
    public string WorkLedgerDir => Path.Combine(_ipcDir, "workledger");
    public string ClaimsDir => Path.Combine(_ipcDir, "workledger", "claims");
    public string QueueDir => Path.Combine(_ipcDir, "workledger", "queue");
    public string ResLedgerDir => Path.Combine(_ipcDir, "resledger");

    // Drop dir for git credential-request notices. The per-session credential
    // logger (`huddle --cred-log`) writes small files here; GitActivityMonitor
    // tails them so a session blocked on a GitHub auth prompt is surfaced.
    public string GitAuthDir => Path.Combine(_ipcDir, "gitauth");

    // Durable handoff ledger (logs/handoffs.jsonl, sibling of the ipc dir). A `handoff`
    // mail is recorded here and announced live; the `handoffs` verb reads it back.
    private readonly HandoffLedger _handoffs;
    public HandoffLedger Handoffs => _handoffs;

    public IpcManager(string ipcDir, Action<string> log)
    {
        _ipcDir = ipcDir;
        _log = log;
        _handoffs = new HandoffLedger(Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(ipcDir)) ?? ".", "logs", "handoffs.jsonl"));
        Directory.CreateDirectory(ipcDir);
        Directory.CreateDirectory(WorkLedgerDir);
        Directory.CreateDirectory(ClaimsDir);
        _log($"IPC directory: {ipcDir}");
        _retryTimer = new System.Threading.Timer(RetryTick, null, RetryInterval, RetryInterval);
    }

    /// <summary>
    /// Create inbox/outbox directories for an instance.
    /// </summary>
    public (string inbox, string outbox) EnsureMailbox(string safePathName)
    {
        var inbox = Path.Combine(_ipcDir, safePathName, "inbox");
        var outbox = Path.Combine(_ipcDir, safePathName, "outbox");
        Directory.CreateDirectory(inbox);
        Directory.CreateDirectory(outbox);
        return (inbox, outbox);
    }

    /// <summary>
    /// Absolute path to a session's pending-context file — the queue of short
    /// wake lines its Stop / UserPromptSubmit hook drains into the session as
    /// context. This replaces synthesized-keystroke injection, so an operator
    /// typing in the console is never stomped mid-prompt.
    /// </summary>
    public const string PendingFileName = "pending.txt";

    public string PendingPath(string safePathName)
        => Path.Combine(_ipcDir, safePathName, PendingFileName);

    /// <summary>
    /// Append one wake line to a session's pending-context file. The session's
    /// hook claims and clears this file on its own turn boundary (Stop) or on
    /// the operator's next submit (UserPromptSubmit), so mail arrives as pulled
    /// context instead of pushed keystrokes. Newlines are collapsed so one call
    /// is always exactly one line. Retries briefly if the hook is mid-drain.
    /// </summary>
    /// <summary>
    /// Non-blocking pending lines are written with this leading sentinel (SOH,
    /// 0x01). The session's Stop hook routes sentinel-prefixed lines to
    /// additionalContext — quiet, Claude-visible — instead of a decision:block,
    /// which the CLI renders as a red "Stop hook error". Info replies (ack/nack)
    /// are notifications, not interruptions, so they must never wake a stop as an
    /// error. The sentinel is stripped by the hook before display and is not
    /// counted as backlog.
    /// </summary>
    public const char InfoPendingSentinel = '';

    public void AppendPending(string safePathName, string line, bool blocking = true)
    {
        if (string.IsNullOrEmpty(safePathName) || string.IsNullOrEmpty(line)) return;
        line = line.Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ");
        if (!blocking) line = InfoPendingSentinel + line;
        var path = PendingPath(safePathName);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, line + "\n");
                return;
            }
            catch (IOException)
            {
                // Hook may be mid-drain (renaming the file). Brief backoff + retry.
                Thread.Sleep(20);
            }
        }
        _log($"IPC: could not append pending wake line for {safePathName} (file busy)");
    }

    /// <summary>
    /// Start watching an instance's inbox for new messages.
    /// </summary>
    public void Watch(string safePathName, string instanceId)
    {
        var inboxPath = Path.Combine(_ipcDir, safePathName, "inbox");
        Directory.CreateDirectory(inboxPath);

        bool fresh = false;
        lock (_watchersLock)
        {
            if (!_watchers.ContainsKey(safePathName))
            {
                try
                {
                    var watcher = new FileSystemWatcher(inboxPath, "*.json")
                    {
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite,
                        EnableRaisingEvents = false
                    };

                    // Direct File.WriteAllText fires Created. Atomic temp-file +
                    // rename (Claude Code's Write tool and most safe-write idioms)
                    // fires Renamed into this directory, not Created. Touching an
                    // existing file fires Changed. Subscribing to all three then
                    // deduping by path is the only way to catch every writer.
                    watcher.Created += (_, e) => OnInboxEvent(instanceId, e.FullPath, e.Name ?? "<unknown>");
                    watcher.Renamed += (_, e) => OnInboxEvent(instanceId, e.FullPath, e.Name ?? "<unknown>");
                    watcher.Changed += (_, e) => OnInboxEvent(instanceId, e.FullPath, e.Name ?? "<unknown>");

                    watcher.Error += (_, e) =>
                    {
                        _log($"IPC: FileSystemWatcher error for '{instanceId}': {e.GetException().Message}");
                    };

                    _watchers[safePathName] = watcher;
                    watcher.EnableRaisingEvents = true;
                    _log($"IPC: Watching inbox for '{instanceId}'");
                    fresh = true;
                }
                catch (Exception ex)
                {
                    _log($"IPC: Failed to create watcher for '{instanceId}': {ex.Message}");
                }
            }
        }

        // Remember which instance owns this inbox so the periodic retry timer
        // can re-drive delivery for still-pending mail.
        _watchedInstances[safePathName] = instanceId;

        // Catch-up scan runs unconditionally — outside the lock and outside the
        // already-watching guard. A leaked watcher from a prior session means
        // the live FSW fires for new mail but nothing drained queued mail on
        // session restart; running this every Watch() call fixes that. Sessions
        // own dedupe on their side (they re-read their inbox dir, processed
        // files get moved/skipped).
        int scanned = 0;
        try
        {
            foreach (var file in Directory.GetFiles(inboxPath, "*.json").OrderBy(f => f))
            {
                ProcessInboxFile(instanceId, file, Path.GetFileName(file));
                scanned++;
            }
        }
        catch (Exception ex) { _log($"IPC: scan-on-watch failed for '{instanceId}': {ex.Message}"); }
        if (scanned > 0)
        {
            var qualifier = fresh ? "pre-existing" : "queued";
            _log($"IPC: scanned {scanned} {qualifier} message(s) for '{instanceId}'");
        }
    }

    /// <summary>
    /// Offer EVERY inbox on disk to <see cref="MailSeen"/> once, including inboxes
    /// belonging to sessions huddle no longer tracks.
    ///
    /// <para>The per-session scan only reaches inboxes that something called
    /// <see cref="Watch"/> for, which means a session that has since been stopped keeps
    /// its unread mail entirely to itself. That is not a lesser case: an assignment does
    /// not stop being owed because the agent that was going to do it exited. A
    /// twenty-day-old task mail sat in a stopped session's inbox, unseen for exactly this
    /// reason, after the announced-once bug above had already been fixed.</para>
    ///
    /// <para>Announcement is deliberately NOT part of this. There is nobody to wake and
    /// no wake line is written, so the delivered index is untouched and a session that
    /// later starts still gets its own first-run scan. Handlers that cannot place the
    /// mail — an unregistered repo, say — are expected to ignore it.</para>
    /// </summary>
    public void SweepAllInboxes()
    {
        if (MailSeen == null) return;

        string[] dirs;
        try { dirs = Directory.GetDirectories(_ipcDir); }
        catch (Exception ex) { _log($"IPC: inbox sweep could not list {_ipcDir}: {ex.Message}"); return; }

        foreach (var dir in dirs)
        {
            var safe = Path.GetFileName(dir);
            // _huddle is the orchestrator's own command drop, not a session mailbox.
            if (safe.Length == 0 || safe[0] == '_') continue;

            var inbox = Path.Combine(dir, "inbox");
            string[] files;
            try { files = Directory.GetFiles(inbox, "*.json"); }
            catch { continue; }   // no inbox here (workledger, resledger, gitauth…)

            // repo_persona -> repo:persona. Only the FIRST underscore separates them;
            // persona names carry their own (architect-2, feature-dev).
            var us = safe.IndexOf('_');
            if (us <= 0) continue;
            var instanceId = safe[..us] + ":" + safe[(us + 1)..];
            foreach (var file in files.OrderBy(f => f))
            {
                IpcMessage? msg;
                try { msg = TryParse(File.ReadAllText(file), Path.GetFileName(file), _log); }
                catch { continue; }   // mid-write or unreadable — the owning scan retries
                if (msg == null) continue;

                try { MailSeen.Invoke(instanceId, msg, file); }
                catch (Exception ex) { _log($"IPC: MailSeen handler threw during sweep: {ex.Message}"); }
            }
        }
    }

    private void OnInboxEvent(string instanceId, string fullPath, string name)
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

            Thread.Sleep(100); // let the file finish writing (rename can fire before content is flushed)
            ProcessInboxFile(instanceId, fullPath, name);
        }
        catch (Exception ex)
        {
            _log($"IPC: Error processing inbox event {name}: {ex.Message}");
        }
    }

    // Periodic re-drive of undelivered mail, and cleanup of mail the agent has
    // acknowledged. Runs every RetryInterval. The delivered index suppresses the NUDGE
    // for announced mail — chiefly nudges held because the operator was at the
    // recipient's console — but every unread file is still parsed and offered to
    // MailSeen, which is what keeps a durable obligation from depending on a one-shot
    // signal. Quiet: no per-file log spam.
    private void RetryTick(object? state)
    {
        if (Interlocked.Exchange(ref _retryRunning, 1) == 1) return; // a tick is still running
        try
        {
            var cutoff = DateTime.UtcNow - RetryMinAge;
            foreach (var kv in _watchedInstances)
            {
                // Clear anything the agent has acknowledged since the last tick, so
                // the inbox reflects what is genuinely still unread.
                ReapAcknowledged(kv.Key);

                var inbox = Path.Combine(_ipcDir, kv.Key, "inbox");
                string[] files;
                try { files = Directory.GetFiles(inbox, "*.json"); }
                catch { continue; }
                foreach (var file in files.OrderBy(f => f))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) > cutoff) continue; // fresh — FSW owns it
                        ProcessInboxFile(kv.Value, file, Path.GetFileName(file), quiet: true);
                    }
                    catch { /* file may have been moved mid-scan; ignore */ }
                }

                // Queued context that nothing is going to drain. Announced mail never
                // re-enters ProcessInboxFile, so this is the only path that reaches a
                // session which idled after delivery or whose wake was held.
                try
                {
                    if (WakeIdle is { } wake && MailWake.HasPending(PendingPath(kv.Key)))
                        wake(kv.Key);
                }
                catch { /* a wake is best-effort; never let it kill the tick */ }
            }
        }
        catch (Exception ex) { _log($"IPC: retry tick failed: {ex.Message}"); }
        finally { Interlocked.Exchange(ref _retryRunning, 0); }
    }

    // Read, log, and dispatch a single mail file. Shared by FSW events and by
    // Watch() catch-up scans. The mail STAYS in inbox/ once announced, so inbox/
    // means "not read yet" — the agent moves it to processed/ when done, or a
    // Write-only persona copies it there and huddle clears the original (see
    // ReapAcknowledged). Announcing is recorded in the delivered index so rescans,
    // retry ticks and restarts never re-fire a wake line for the same message. A
    // failed delivery records nothing and is retried on the next scan / start.
    private void ProcessInboxFile(string instanceId, string fullPath, string name, bool quiet = false)
    {
        if (!File.Exists(fullPath)) return;

        var safe = SafeNameFor(fullPath);
        // NOT an early return any more. Announced-once used to end the pass here, which
        // put every durable consequence of a piece of mail behind a one-shot signal —
        // see MailSeen. Announcement now only suppresses the NUDGE, below.
        var announced = safe.Length > 0 && AlreadyAnnounced(safe, name);

        string json;
        try { json = File.ReadAllText(fullPath); }
        catch (Exception ex)
        {
            // Writer may still hold the file — leave it in inbox/ for the next
            // event/scan to retry rather than treating an I/O race as bad mail.
            _log($"IPC: could not read {name} (retrying later): {ex.Message}");
            return;
        }

        var msg = TryParse(json, name, _log);
        if (msg == null)
        {
            // Unparseable even after repair. Huddle only needs From/Subject to
            // format the wake line — the BODY is for the recipient agent, which
            // reads the raw file fine. Deliver anyway with a synthesized envelope
            // instead of letting the mail rot undelivered — but only once the
            // file looks complete and the writer has gone quiet; otherwise leave
            // it in inbox/ for the next event/scan to retry.
            if (!LooksComplete(fullPath, json)) return;
            msg = new IpcMessage
            {
                From = "unknown sender",
                Subject = $"mail file is not valid JSON ({name}); open it and read the raw text"
            };
        }
        // Fires for announced mail too — this is the pass that backfills a row for mail
        // that has been sitting unread since before the ledger existed. Kept ahead of the
        // announced return so it cannot drift back behind it.
        try { MailSeen?.Invoke(instanceId, msg, fullPath); }
        catch (Exception ex) { _log($"IPC: MailSeen handler threw: {ex.Message}"); }

        // Everything from here is the ANNOUNCEMENT: the console line, the handoff record
        // and the wake. Those are one-shot by design — re-announcing unread mail on every
        // scan is the nag this index exists to prevent.
        if (announced) return;

        if (!quiet) _log($"IPC [{instanceId}] from {msg.From}: {msg.Subject}");

        // A handoff mail is recorded + announced the moment it lands, regardless of
        // whether the recipient is live — the push that makes handoffs visible without
        // the operator asking. Idempotent by mail filename, so a re-processed inbox file
        // (handoff to a not-yet-live session) never double-announces.
        if (string.Equals(msg.Type, "handoff", StringComparison.OrdinalIgnoreCase))
            RecordHandoff(msg, name);

        // Give the SENDER a record of what it sent. Sits with the announcement because it
        // is one-shot for the same reason, and after RecordHandoff so a failure here can
        // never cost the handoff ledger its entry.
        RecordOutbound(msg, name);

        // Internal sender already handled the nudge (broadcast fan-out,
        // orchestrator ack/nack reply) and the body content is structured
        // for the orchestrator's own bookkeeping, not for an agent to read
        // as mail. Move straight to processed/ — recipient was nudged via
        // the sender's own injector.
        if (_suppressNudgeFor.TryRemove(fullPath, out _))
        {
            MoveToProcessed(fullPath, name);
            return;
        }

        // Point the wake line at the mail where it actually is — inbox/ — and leave
        // it there. The agent clears it when it has read it, which is what makes
        // inbox/ a truthful unread list rather than a delivery artefact.
        bool delivered = false;
        try
        {
            if (MessageReceived != null)
                delivered = MessageReceived.Invoke(instanceId, msg, fullPath);
        }
        catch (Exception ex) { _log($"IPC: MessageReceived handler threw: {ex.Message}"); }

        if (delivered && safe.Length > 0)
            MarkAnnounced(safe, name);
        // Not delivered: nothing recorded — retried on the next scan / session start.
    }

    // Record a handoff mail to the ledger and, if it's new, announce it in the console:
    //   [handoff] <from> -> <to>: <task> (<state>)
    /// <summary>
    /// Write a receipt into the SENDER's outbox for a mail huddle has just delivered.
    ///
    /// <para>Every session has had an outbox/ since the first IPC commit and nothing has
    /// ever written to it. Agents mail each other by writing straight into the recipient's
    /// inbox, so a sender leaves no trace of its own correspondence anywhere — and an
    /// empty outbox is indistinguishable from having sent nothing. On 2026-08-28
    /// otherapp:architect checked its outbox to see whether it had made an offer that
    /// myapp:architect said it was accepting, found nothing, and reported to the
    /// operator that the offer had been invented. It had not: otherapp had made it four
    /// days earlier, and the proof was sitting in the RECIPIENT's processed/ where only
    /// the other party could see it. An agent must be able to substantiate its own history
    /// from its own mailbox.</para>
    ///
    /// <para>The receipt is huddle's OBSERVATION, not the sender's claim. observedAt is
    /// huddle's clock, and the sender's own timestamp is recorded beside it as
    /// claimedTimestamp precisely so the two can disagree — the mail that triggered this
    /// was stamped three hours in the future by the agent that wrote it. From is still
    /// self-declared and huddle still cannot authenticate it; what this establishes is
    /// that a mail bearing that name was really delivered, when, and to whom.</para>
    /// </summary>
    private void RecordOutbound(IpcMessage msg, string sourceName)
    {
        try
        {
            var safeFrom = SafeName(msg.From);
            if (safeFrom.Length == 0) return;

            var outbox = Path.Combine(_ipcDir, safeFrom, "outbox");
            Directory.CreateDirectory(outbox);

            var receipt = Path.Combine(outbox, sourceName);
            // Idempotent: the receipt is named for the mail file, so a re-processed inbox
            // never writes a second copy of the same send.
            if (File.Exists(receipt)) return;

            var record = new
            {
                from = msg.From,
                to = msg.To,
                subject = msg.Subject,
                type = msg.Type,
                claimedTimestamp = msg.Timestamp,
                observedAt = DateTime.UtcNow.ToString("o"),
                mailFile = sourceName
            };
            File.WriteAllText(receipt,
                JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            // A missing receipt must never cost the delivery it is describing.
            _log($"IPC: could not record outbound receipt for {sourceName}: {ex.Message}");
        }
    }

    /// <summary>Safe-name form of an instance id ("repo:persona" -> "repo_persona"), which
    /// is how mailbox directories are named. Returns empty for an id huddle cannot place —
    /// the synthesized "unknown sender" envelope, notably, which owns no mailbox.</summary>
    private static string SafeName(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return "";
        var s = instanceId.Trim();
        if (s.Contains(' ')) return "";           // "unknown sender" and friends
        return s.Replace(':', '_');
    }

    private void RecordHandoff(IpcMessage msg, string sourceName)
    {
        try
        {
            JsonElement body;
            try { body = msg.BodyObject; } catch { body = default; }
            var (to, task, state) = HandoffLedger.ParseBody(body, msg.To, msg.Subject);
            var from = string.IsNullOrWhiteSpace(msg.From) ? "?" : msg.From;
            if (_handoffs.Record(new HandoffEntry(DateTime.Now, from, to, task, state, sourceName)))
            {
                var tail = string.IsNullOrWhiteSpace(state)
                    ? "" : $" ({(state!.Length > 80 ? state[..80] + "…" : state)})";
                _log($"[handoff] {from} -> {to}: {task}{tail}");
            }
        }
        catch (Exception ex) { _log($"IPC: handoff record failed: {ex.Message}"); }
    }

    // One shared entry point for reading agent-authored IPC JSON: strict parse,
    // then repair. Owns the logging so the three consumers (session-mail
    // delivery, ReadInbox, orchestrator commands) can't drift apart.
    public static IpcMessage? TryParse(string json, string name, Action<string> log)
    {
        try { return JsonSerializer.Deserialize<IpcMessage>(json); }
        catch (Exception ex)
        {
            var repaired = TryParseRepaired(json);
            if (repaired != null)
            {
                log($"IPC: repaired invalid escapes in {name}");
                return repaired;
            }
            log($"IPC: unparseable JSON in {name}: {ex.Message}");
            return null;
        }
    }

    // Deserialize after repairing invalid backslash escapes. Two passes: the
    // first keeps \" as an escape; if that still doesn't parse, the second
    // treats \" as a literal backslash + closing quote — the trailing-backslash
    // path case ("dir": "X:\Library\") where keeping \" as an escape would eat
    // the string's real closing quote. Returns null if neither pass parses.
    public static IpcMessage? TryParseRepaired(string json)
    {
        try { return JsonSerializer.Deserialize<IpcMessage>(RepairInvalidEscapes(json)); }
        catch { /* fall through to the quote-literal pass */ }
        try { return JsonSerializer.Deserialize<IpcMessage>(RepairInvalidEscapes(json, quoteEscapeIsLiteral: true)); }
        catch { return null; }
    }

    // Double backslashes that don't begin a trusted escape inside JSON strings.
    // Only ever called on text that already FAILED strict parsing — in that
    // domain (agent-authored mail/commands) backslashes are overwhelmingly
    // Windows paths, so \b \f \n \r \t are deliberately NOT trusted: preserving
    // them decodes "C:\temp\build" into control characters that downstream code
    // would execute (wrong directory, mangled wake lines). Doubling them instead
    // turns paths right and an intentional \n into visible literal "\n" text —
    // degraded, never corrupted. Trusted: \\ , \/ , \uXXXX (4-hex is
    // unambiguous), and \" unless quoteEscapeIsLiteral. Known accepted loss: an
    // unescaped UNC prefix ("\\server\share") keeps its \\ as an escape and
    // decodes one backslash short.
    public static string RepairInvalidEscapes(string json, bool quoteEscapeIsLiteral = false)
    {
        var sb = new System.Text.StringBuilder(json.Length + 16);
        bool inString = false;
        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];
            if (!inString)
            {
                if (c == '"') inString = true;
                sb.Append(c);
                continue;
            }
            if (c == '"') { inString = false; sb.Append(c); continue; }
            if (c != '\\') { sb.Append(c); continue; }

            char next = i + 1 < json.Length ? json[i + 1] : '\0';
            bool valid = next is '\\' or '/' || (next == '"' && !quoteEscapeIsLiteral);
            if (next == 'u')
            {
                valid = i + 5 < json.Length
                    && Uri.IsHexDigit(json[i + 2]) && Uri.IsHexDigit(json[i + 3])
                    && Uri.IsHexDigit(json[i + 4]) && Uri.IsHexDigit(json[i + 5]);
            }

            if (valid)
            {
                // Emit the escape intro plus its escaped char so the escaped char
                // (e.g. \" or \\) isn't re-inspected as a string delimiter/backslash.
                sb.Append(c).Append(next);
                i++;
            }
            else
            {
                sb.Append('\\').Append('\\');
            }
        }
        return sb.ToString();
    }

    // Mid-write guard for fallback delivery paths: only treat a file as a
    // complete-but-broken document when it structurally looks finished AND the
    // writer has been quiet for a couple of seconds. An intermediate flush can
    // end at a nested '}' — the brace check alone isn't enough.
    public static bool LooksComplete(string fullPath, string json)
    {
        if (!json.TrimEnd().EndsWith("}")) return false;
        try { return (DateTime.UtcNow - File.GetLastWriteTimeUtc(fullPath)) >= TimeSpan.FromSeconds(2); }
        catch { return false; }
    }

    // ---- read receipts -------------------------------------------------------
    // Mail stays in inbox/ until the recipient acknowledges it, so inbox/ answers
    // "what has this agent not read yet". Huddle tracks what it has announced
    // (delivered index) and clears inbox originals once the agent has copied them
    // into processed/. See MailReceipts for the rules.

    private string DeliveredIndexPath(string safe) =>
        Path.Combine(_ipcDir, safe, MailReceipts.DeliveredIndexName);

    // The safe path name owning a mail file: ipc/<safe>/inbox/<file>.
    private static string SafeNameFor(string inboxFilePath)
    {
        var inboxDir = Path.GetDirectoryName(inboxFilePath);
        var sessionDir = inboxDir == null ? null : Path.GetDirectoryName(inboxDir);
        return sessionDir == null ? "" : Path.GetFileName(sessionDir);
    }

    private HashSet<string> DeliveredFor(string safe)
    {
        return _delivered.GetOrAdd(safe, s =>
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var path = DeliveredIndexPath(s);
                if (File.Exists(path))
                    foreach (var line in File.ReadAllLines(path))
                        if (!string.IsNullOrWhiteSpace(line)) set.Add(line.Trim());
            }
            catch (Exception ex) { _log($"IPC: could not read delivered index for '{s}': {ex.Message}"); }
            return set;
        });
    }

    private bool AlreadyAnnounced(string safe, string name)
    {
        var set = DeliveredFor(safe);
        lock (_deliveredLock) return set.Contains(name);
    }

    private void MarkAnnounced(string safe, string name)
    {
        var set = DeliveredFor(safe);
        lock (_deliveredLock)
        {
            if (!set.Add(name)) return;
            try { File.AppendAllText(DeliveredIndexPath(safe), name + Environment.NewLine); }
            catch (Exception ex) { _log($"IPC: could not record delivery of {name}: {ex.Message}"); }
        }
    }

    private void ForgetAnnounced(string safe, IEnumerable<string> names)
    {
        var set = DeliveredFor(safe);
        lock (_deliveredLock)
        {
            var changed = false;
            foreach (var n in names) changed |= set.Remove(n);
            if (!changed) return;
            try { File.WriteAllLines(DeliveredIndexPath(safe), set.OrderBy(n => n)); }
            catch (Exception ex) { _log($"IPC: could not rewrite delivered index for '{safe}': {ex.Message}"); }
        }
    }

    /// <summary>
    /// Clear inbox originals the agent has acknowledged by copying into processed/,
    /// and prune index entries for mail that has left the inbox. An agent that moves
    /// its own mail (shell personas) needs nothing here; this covers the Write-only
    /// ones that can create the processed/ copy but not delete the original.
    ///
    /// Driven by the retry tick, and by <see cref="GetBacklog"/> so the operator's
    /// unread counts are current rather than up to one tick stale.
    /// </summary>
    public void ReapAcknowledged(string safe)
    {
        try
        {
            var sessionDir = Path.Combine(_ipcDir, safe);
            var inboxDir = Path.Combine(sessionDir, "inbox");
            var processedDir = Path.Combine(sessionDir, "processed");
            if (!Directory.Exists(inboxDir)) return;

            var inboxNames = Directory.GetFiles(inboxDir, "*.json").Select(Path.GetFileName).OfType<string>();
            var processedNames = Directory.Exists(processedDir)
                ? Directory.GetFiles(processedDir, "*.json").Select(Path.GetFileName).OfType<string>()
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            List<string> delivered;
            var set = DeliveredFor(safe);
            lock (_deliveredLock) delivered = set.ToList();

            var (reap, forget) = MailReceipts.PlanCleanup(inboxNames, processedNames, delivered);

            foreach (var name in reap)
            {
                try { File.Delete(Path.Combine(inboxDir, name)); }
                catch (Exception ex) { _log($"IPC: could not clear acknowledged {name}: {ex.Message}"); }
            }
            if (reap.Count > 0)
                _log($"IPC: {reap.Count} message(s) acknowledged by '{safe}' — cleared from inbox");

            // Mail leaving the inbox is acknowledgement (§5.4). `reap` and `forget`
            // together are exactly what left it since the last pass, so this fires once
            // per message in the normal case; a file can appear in reap on one tick and
            // forget on the next, so the handler is required to be idempotent anyway.
            var acknowledged = reap.Concat(forget).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (acknowledged.Count > 0 && MailAcknowledged is { } ack)
            {
                try { ack(safe, acknowledged); }
                catch (Exception ex) { _log($"IPC: ledger acknowledgement for '{safe}' failed: {ex.Message}"); }
            }

            if (forget.Count > 0) ForgetAnnounced(safe, forget);
        }
        catch (Exception ex) { _log($"IPC: receipt cleanup failed for '{safe}': {ex.Message}"); }
    }

    /// <summary>
    /// Per-session mail backlog: wake lines queued but not yet shown, and mail
    /// delivered but not yet acknowledged. Sessions with nothing outstanding are
    /// omitted. Ordered by unread, then queued, descending.
    /// </summary>
    public List<MailBacklog> GetBacklog()
    {
        var rows = new List<MailBacklog>();
        if (!Directory.Exists(_ipcDir)) return rows;

        foreach (var sessionDir in Directory.GetDirectories(_ipcDir))
        {
            var name = Path.GetFileName(sessionDir);
            if (name.StartsWith('_')) continue;          // orchestrator dirs, not a session inbox

            // Settle acknowledgements first so the counts below are current.
            ReapAcknowledged(name);

            var queued = 0;
            DateTime? oldest = null;
            try
            {
                var pending = Path.Combine(sessionDir, PendingFileName);
                if (File.Exists(pending))
                {
                    // Info replies (sentinel-prefixed) are notifications, not
                    // queued work — they never block a stop, so they aren't backlog.
                    queued = File.ReadAllLines(pending)
                        .Count(l => !string.IsNullOrWhiteSpace(l) && l[0] != InfoPendingSentinel);
                    if (queued > 0) oldest = File.GetLastWriteTime(pending);
                }
            }
            catch { /* a drain may be in flight; report what we can */ }

            var unread = 0;
            try
            {
                var inbox = Path.Combine(sessionDir, "inbox");
                if (Directory.Exists(inbox))
                {
                    var files = Directory.GetFiles(inbox, "*.json");
                    unread = files.Length;
                    foreach (var f in files)
                    {
                        var written = File.GetLastWriteTime(f);
                        if (oldest == null || written < oldest) oldest = written;
                    }
                }
            }
            catch { /* ditto */ }

            if (queued > 0 || unread > 0)
                rows.Add(new MailBacklog(name, queued, unread, oldest));
        }

        return rows
            .OrderByDescending(r => r.Unread)
            .ThenByDescending(r => r.Queued)
            .ToList();
    }

    // Compute where a mail file will live once archived (sibling processed/ dir), giving a
    // colliding name a guid suffix. Pure — does not touch the filesystem.
    private static string ComputeProcessedDest(string fullPath, string name)
    {
        var inboxDir = Path.GetDirectoryName(fullPath)!;
        var sessionDir = Path.GetDirectoryName(inboxDir)!;
        var processedDir = Path.Combine(sessionDir, "processed");
        var dest = Path.Combine(processedDir, name);
        if (File.Exists(dest))
        {
            var stem = Path.GetFileNameWithoutExtension(name);
            var ext = Path.GetExtension(name);
            dest = Path.Combine(processedDir, $"{stem}-{Guid.NewGuid():N}{ext}");
        }
        return dest;
    }

    // Move a mail file from inbox/ to the sibling processed/. Returns the destination path
    // on success, or null on failure. Used by the internal-send path (already nudged).
    private string? MoveToProcessed(string fullPath, string name)
    {
        try
        {
            var dest = ComputeProcessedDest(fullPath, name);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Move(fullPath, dest);
            return dest;
        }
        catch (Exception ex) { _log($"IPC: failed to move {name} to processed/: {ex.Message}"); return null; }
    }

    private void CleanupRecentEvents(DateTime now)
    {
        foreach (var kv in _recentEvents)
        {
            if ((now - kv.Value) > FswDedupWindow)
                _recentEvents.TryRemove(kv.Key, out _);
        }
    }

    /// <summary>
    /// Stop watching an instance's inbox.
    /// </summary>
    public void Unwatch(string safePathName)
    {
        lock (_watchersLock)
        {
            if (_watchers.Remove(safePathName, out var watcher))
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
        }
    }

    // Paths that in-process callers wrote with suppressAutoNudge=true.
    // The inbox watcher checks this set before firing MessageReceived so
    // we don't double-nudge when the caller (broadcast, ack/nack) has
    // already injected to the target's console itself.
    private readonly ConcurrentDictionary<string, byte> _suppressNudgeFor =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Send a message to a target session's inbox.
    /// Set <paramref name="suppressAutoNudge"/> when the caller will inject
    /// to the target's console itself (broadcast fan-out, orchestrator
    /// ack/nack replies) — prevents a duplicate MessageReceived nudge.
    /// </summary>
    public void Send(string fromId, string toSafePathName, string subject, string body, string type = "info",
        bool suppressAutoNudge = false)
    {
        var inboxPath = Path.Combine(_ipcDir, toSafePathName, "inbox");
        Directory.CreateDirectory(inboxPath);

        var now = DateTime.UtcNow;
        var timestamp = now.ToString("o");
        var safeTimestamp = now.ToString("yyyyMMddTHHmmss");
        var safeFrom = fromId.Replace(':', '_');
        var guid = Guid.NewGuid().ToString("N")[..8];

        var filename = $"from-{safeFrom}-{safeTimestamp}-{guid}.json";
        var msg = new IpcMessage
        {
            From = fromId,
            To = toSafePathName.Replace('_', ':'),
            Timestamp = timestamp,
            Type = type,
            Subject = subject,
            Body = JsonSerializer.SerializeToElement(body)
        };

        var fullPath = Path.Combine(inboxPath, filename);
        var json = JsonSerializer.Serialize(msg, new JsonSerializerOptions { WriteIndented = true });
        if (suppressAutoNudge) _suppressNudgeFor[fullPath] = 1;
        File.WriteAllText(fullPath, json);
        _log($"IPC: Sent message to {toSafePathName}: {subject}");
    }

    /// <summary>
    /// Read all inbox messages for an instance.
    /// </summary>
    public IpcMessage[] ReadInbox(string safePathName)
    {
        var inboxPath = Path.Combine(_ipcDir, safePathName, "inbox");
        if (!Directory.Exists(inboxPath))
            return [];

        var messages = new List<IpcMessage>();
        foreach (var file in Directory.GetFiles(inboxPath, "*.json").OrderBy(f => f))
        {
            try
            {
                var json = File.ReadAllText(file);
                var msg = TryParse(json, Path.GetFileName(file), _log);
                if (msg != null)
                    messages.Add(msg);
            }
            catch (Exception ex)
            {
                _log($"IPC: Error reading {Path.GetFileName(file)}: {ex.Message}");
            }
        }
        return messages.ToArray();
    }

    /// <summary>
    /// Get inbox/outbox paths for prompt injection.
    /// </summary>
    public (string inbox, string outbox) GetMailboxPaths(string safePathName)
    {
        return (
            Path.Combine(_ipcDir, safePathName, "inbox"),
            Path.Combine(_ipcDir, safePathName, "outbox")
        );
    }

    public void Dispose()
    {
        _retryTimer?.Dispose();
        _retryTimer = null;
        lock (_watchersLock)
        {
            foreach (var (_, watcher) in _watchers)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
                catch { }
            }
            _watchers.Clear();
        }
    }
}
