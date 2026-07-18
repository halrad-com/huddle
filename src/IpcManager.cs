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

public class IpcManager : IDisposable
{
    private readonly string _ipcDir;
    private readonly Action<string> _log;
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new();
    private readonly object _watchersLock = new();

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

    public string IpcDir => _ipcDir;
    public string WorkLedgerDir => Path.Combine(_ipcDir, "workledger");
    public string ClaimsDir => Path.Combine(_ipcDir, "workledger", "claims");
    public string QueueDir => Path.Combine(_ipcDir, "workledger", "queue");
    public string ResLedgerDir => Path.Combine(_ipcDir, "resledger");

    public IpcManager(string ipcDir, Action<string> log)
    {
        _ipcDir = ipcDir;
        _log = log;
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

    // Periodic re-drive of undelivered mail. Runs every RetryInterval. For each
    // watched inbox, re-processes pending files older than RetryMinAge (fresh
    // ones belong to the FSW). Delivered mail was moved to processed/, so only
    // genuinely-undelivered files remain — chiefly nudges held because the
    // operator was at the recipient's console. Quiet: no per-file log spam;
    // the visible signal is the mail leaving the inbox when it finally lands.
    private void RetryTick(object? state)
    {
        if (Interlocked.Exchange(ref _retryRunning, 1) == 1) return; // a tick is still running
        try
        {
            var cutoff = DateTime.UtcNow - RetryMinAge;
            foreach (var kv in _watchedInstances)
            {
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
            }
        }
        catch (Exception ex) { _log($"IPC: retry tick failed: {ex.Message}"); }
        finally { Interlocked.Exchange(ref _retryRunning, 0); }
    }

    // Read, log, and dispatch a single mail file. Shared by FSW events and by
    // Watch() catch-up scans. On a successful nudge the file is auto-archived to
    // processed/ (I003) and the wake signal points there, so even a Write-only
    // persona never has to clear its own inbox and a reload can't re-fire it. A
    // failed delivery leaves the file in inbox/ to retry on the next scan / start.
    private void ProcessInboxFile(string instanceId, string fullPath, string name, bool quiet = false)
    {
        if (!File.Exists(fullPath)) return;

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
        if (!quiet) _log($"IPC [{instanceId}] from {msg.From}: {msg.Subject}");

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

        // Auto-archive (I003): nudge the recipient with the mail's FUTURE processed/ path,
        // then move it there only on a successful nudge. A delivered message ends up in
        // processed/ (a Write-only persona never clears its own inbox; a reload can't
        // re-fire it). A failed delivery leaves the file untouched in inbox/ — retried on
        // the next scan / session start, and generating no spurious watcher event.
        var destPath = ComputeProcessedDest(fullPath, name);

        bool delivered = false;
        try
        {
            if (MessageReceived != null)
                delivered = MessageReceived.Invoke(instanceId, msg, destPath);
        }
        catch (Exception ex) { _log($"IPC: MessageReceived handler threw: {ex.Message}"); }

        if (delivered)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                File.Move(fullPath, destPath);
            }
            catch (Exception ex) { _log($"IPC: delivered but failed to archive {name}: {ex.Message}"); }
        }
        // Not delivered: leave the file in inbox/ — retried on the next scan / session start.
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
