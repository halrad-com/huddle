using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Huddle;

/// <summary>
/// The ONE process that appends to a repo's events.jsonl (spec §2.2). Everything that
/// records an obligation — mail ingestion, TaskTracker, the work queue, the operator's
/// accept/drop/decline — goes through here, so id allocation and canonical formatting
/// have a single owner and there is no allocation race.
///
/// <para>Ids are written canonically (<c>T-007</c>, never <c>T-7</c>) and read back
/// PARSED, never as text: an event hand-written as <c>T-7</c> or <c>huddle:T-007</c>
/// names the same task as <c>T-007</c>, and keying on the string made those three
/// separate obligations (review finding L3).</para>
///
/// <para>Nothing is ever rewritten or deleted. Rotation renames the live file and starts
/// a fresh one; the reader loads every <c>events*.jsonl</c>.</para>
/// </summary>
public sealed class LedgerWriter
{
    /// <summary>Spec §2.2. Past this the live file is renamed and a fresh one started.</summary>
    public const long RotateAtBytes = 5 * 1024 * 1024;

    public const string LiveFileName = "events.jsonl";

    static readonly JsonSerializerOptions WriteOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Converters = { new UtcZTimestampConverter() },
        // The log is read by humans in a diff as often as by huddle; escaping only what
        // JSON requires keeps a title with an apostrophe or a plus sign legible.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    readonly string _dir;
    readonly Action<string> _log;
    readonly bool _createIfAbsent;
    readonly long _rotateAt;
    readonly object _lock = new();
    readonly string _mutexName;

    /// <summary>
    /// Highest task number this writer has HANDED OUT, whether or not it has been
    /// appended yet. Without it two threads that both call <see cref="NextTaskId"/>
    /// before either appends would be issued the same number.
    /// </summary>
    int _issued;

    /// <param name="ledgerDir">The repo's <c>docs/ledger</c> directory.</param>
    /// <param name="createIfAbsent">
    /// Create <c>docs/ledger/</c> when it does not exist. Off by default: spec §3 —
    /// huddle indexes a repo's ledger, it does not decide that a repo has one. Mail
    /// ingestion turns it on, because a task mail to a repo with no ledger is exactly
    /// the obligation that must not vanish; even then only <c>events.jsonl</c> is
    /// created. <c>ledger.md</c> is the operator's file and huddle never authors it.
    /// </param>
    public LedgerWriter(string ledgerDir, Action<string> log, bool createIfAbsent = false, long rotateAtBytes = RotateAtBytes)
    {
        _dir = Path.GetFullPath(ledgerDir);
        _log = log;
        _createIfAbsent = createIfAbsent;
        _rotateAt = rotateAtBytes;
        // Two huddle instances (different roots, overlapping repos) must not interleave
        // half a line. A path cannot be a mutex name — '\' is the namespace separator —
        // so the directory is hashed.
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(_dir.ToLowerInvariant())))[..16];
        _mutexName = "huddle-ledger-" + hash;
    }

    public string Dir => _dir;
    public string LivePath => Path.Combine(_dir, LiveFileName);

    /// <summary>Every event in this repo's log, rotated files included.</summary>
    public IReadOnlyList<LedgerEvent> ReadAll(List<string> problems) => LedgerEventReader.ReadAll(_dir, problems);

    /// <summary>
    /// Append one event. A no-op (logged, never thrown) when the ledger directory is
    /// absent and <c>createIfAbsent</c> is off — a repo without a ledger is a
    /// configuration fact, not an error to crash the orchestrator over.
    /// </summary>
    public void Append(LedgerEvent e) => WithLedger(() => AppendLocked(e));

    /// <summary>
    /// Allocate a task id and append its <c>task-assigned</c> event as ONE atomic act.
    /// Prefer this over <see cref="NextTaskId"/> + <see cref="Append"/>: it holds the
    /// cross-process mutex across both halves, so a second orchestrator cannot read the
    /// log between the allocation and the write and hand out the same number. Returns
    /// the id, or null when the ledger directory is absent and uncreatable.
    /// </summary>
    public LedgerId? AppendNewTask(Func<LedgerId, LedgerEvent> makeEvent)
    {
        LedgerId? issued = null;
        WithLedger(() =>
        {
            var id = NextTaskIdLocked();
            AppendLocked(makeEvent(id));
            issued = id;
        });
        return issued;
    }

    /// <summary>
    /// The next unused task number for this repo: one past the highest that appears in
    /// any <c>events*.jsonl</c>, and never lower than one this writer has already handed
    /// out. Numbers are never reused, so a restart continues the sequence rather than
    /// re-issuing <c>T-001</c> — which the in-memory counter it replaces did 23 times.
    /// </summary>
    public LedgerId NextTaskId()
    {
        LedgerId id = default;
        WithLedger(() => id = NextTaskIdLocked(), requireDir: false);
        return id;
    }

    /// <summary>
    /// The task carrying <paramref name="reference"/> in its refs, if any. This is the
    /// dedup key: mail ingestion keys on the mail file's path and the work queue on
    /// <c>unit:&lt;id&gt;</c>, so a rescan, a retry or a restart re-finds the existing
    /// row instead of opening a second one. Matching is exact — a prefix is a different
    /// file. The id comes back PARSED, so a loosely-spelled event still answers with the
    /// canonical id.
    /// </summary>
    public bool TryFindTaskByRef(string reference, out LedgerId id)
    {
        id = default;
        if (string.IsNullOrEmpty(reference)) return false;
        var problems = new List<string>();
        foreach (var e in ReadAll(problems))
        {
            if (e.Refs is null || !e.Refs.Contains(reference, StringComparer.Ordinal)) continue;
            if (!LedgerId.TryParse(e.Id, out var parsed) || parsed.Type != LedgerType.Task) continue;
            id = parsed with { Repo = null };
            return true;
        }
        return false;
    }

    /// <summary>
    /// Rename the live file when it passes the threshold and start a fresh one. The
    /// archive name carries a sequence so a second rotation on the same day cannot
    /// overwrite the first, and so ordinal name order stays chronological — which is the
    /// order <see cref="LedgerEventReader"/> replays events in.
    /// </summary>
    public void RotateIfNeeded() => WithLedger(RotateIfNeededLocked);

    // ---- internals; every one of these runs under both locks ----

    void AppendLocked(LedgerEvent e)
    {
        RotateIfNeededLocked();
        var line = JsonSerializer.Serialize(e, WriteOpts);
        // Serialize escapes any newline inside a value, so one call is always one record.
        File.AppendAllText(LivePath, line + "\n");
        if (LedgerId.TryParse(e.Id, out var id) && id.Type == LedgerType.Task && id.Number > _issued)
            _issued = id.Number;
    }

    LedgerId NextTaskIdLocked()
    {
        var problems = new List<string>();
        var highest = 0;
        foreach (var e in LedgerEventReader.ReadAll(_dir, problems))
            if (LedgerId.TryParse(e.Id, out var id) && id.Type == LedgerType.Task && id.Number > highest)
                highest = id.Number;
        _issued = Math.Max(_issued, highest) + 1;
        return new LedgerId(LedgerType.Task, _issued, null);
    }

    void RotateIfNeededLocked()
    {
        FileInfo fi;
        try { fi = new FileInfo(LivePath); if (!fi.Exists || fi.Length < _rotateAt) return; }
        catch { return; }

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd");
        for (int seq = 1; seq < 1000; seq++)
        {
            var archive = Path.Combine(_dir, $"events-{stamp}-{seq:000}.jsonl");
            if (File.Exists(archive)) continue;
            try
            {
                File.Move(LivePath, archive);
                _log($"ledger: rotated {LivePath} -> {Path.GetFileName(archive)} ({fi.Length / 1024}k)");
            }
            catch (IOException ex) { _log($"ledger: could not rotate {LivePath}: {ex.Message}"); }
            return;
        }
    }

    /// <summary>
    /// Run <paramref name="body"/> holding the in-process lock and the cross-process
    /// mutex, after making sure the ledger directory exists. Never throws: a ledger that
    /// cannot be written is logged and the orchestrator carries on.
    /// </summary>
    void WithLedger(Action body, bool requireDir = true)
    {
        lock (_lock)
        {
            if (requireDir && !EnsureDir()) return;
            using var mutex = new Mutex(false, _mutexName);
            var held = false;
            try
            {
                try { held = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
                catch (AbandonedMutexException) { held = true; }  // previous holder died mid-write
                if (!held) { _log($"ledger: timed out waiting for {_mutexName}; skipping write to {_dir}"); return; }
                body();
            }
            catch (Exception ex) { _log($"ledger: write to {_dir} failed: {ex.Message}"); }
            finally { if (held) mutex.ReleaseMutex(); }
        }
    }

    bool EnsureDir()
    {
        if (Directory.Exists(_dir)) return true;
        if (!_createIfAbsent) { _log($"ledger: no docs/ledger in {Path.GetDirectoryName(Path.GetDirectoryName(_dir))} — not creating one"); return false; }
        try { Directory.CreateDirectory(_dir); return true; }
        catch (Exception ex) { _log($"ledger: could not create {_dir}: {ex.Message}"); return false; }
    }

    /// <summary>
    /// Writes <c>ts</c> as UTC with a literal Z, matching the spec's examples and every
    /// event already on disk. The framework default carries a numeric offset
    /// (<c>+01:00</c>), which reads as a local timestamp in a log whose whole point is
    /// that two agents on different clocks agree on when something was assigned.
    /// Full tick precision is kept because replay order IS the task's history: two events
    /// written in the same millisecond — three delegate-task calls in a loop, a dispatch
    /// batch — would otherwise tie, and a tie orders arbitrarily.
    /// </summary>
    sealed class UtcZTimestampConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o) =>
            reader.GetDateTimeOffset();

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions o) =>
            writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ",
                System.Globalization.CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// The decision half of <c>ledger accept</c> / <c>drop</c> / <c>decline</c>. Pure: each
/// verb answers with the event to append, or with a refusal the console prints verbatim.
/// Kept out of ConsoleUI so the rules — which are the load-bearing part — are testable
/// without a console.
///
/// <para>None of these WRITE. The caller appends the returned event, so a refusal cannot
/// leave a half-recorded transition behind.</para>
/// </summary>
public static class LedgerCommandsWrite
{
    /// <summary>
    /// Accept a hierarchy row or a task. Refused unless the item is <c>delivered</c>, and
    /// for a Deliverable, refused while its <c>accepts</c> gate is unnamed (§1.3,
    /// acceptance criterion 4) — huddle does not RUN the gate, it declines to record
    /// acceptance when nobody has said what would prove the thing works.
    ///
    /// <para>A task inherits its parent Deliverable's gate. An ORPHAN task has no
    /// Deliverable to gate against, so it is allowed and the event records
    /// <c>ungated:true</c> — the count of those is itself a reading on how much
    /// delegation is running ahead of the plan (§5.3).</para>
    /// </summary>
    public static bool TryAccept(
        LedgerRepoSnapshot snap, LedgerId id, string actor, DateTimeOffset now,
        out LedgerEvent? ev, out string why)
    {
        ev = null; why = "";
        var bare = id with { Repo = null };

        if (bare.Type == LedgerType.Task)
        {
            var task = snap.Tasks.FirstOrDefault(t => t.Id == bare);
            if (task is null) { why = $"{bare}: no such task in {snap.Repo}"; return false; }
            if (!LedgerStateMachine.CanTransitionTask(task.State, "accepted"))
            { why = $"{bare} is {task.State}; only a delivered task can be accepted"; return false; }

            // The gate is the parent Deliverable's, inherited.
            LedgerRow? parent = task.Parent is { } p
                ? snap.Rows.FirstOrDefault(r => r.Id == (p with { Repo = null }))
                : null;
            if (parent is { Type: LedgerType.Deliverable } d && string.IsNullOrWhiteSpace(d.Accepts))
            { why = $"{bare}'s parent {d.Id} has no accepts gate — name the test, capture suite, replay or commit that proves it"; return false; }

            ev = new LedgerEvent(now, "task-accepted", bare.ToString(), Actor: actor, Ungated: parent is null);
            return true;
        }

        var row = snap.Rows.FirstOrDefault(r => r.Id == bare);
        if (row is null) { why = $"{bare}: no such item in {snap.Repo}'s ledger.md"; return false; }
        if (!LedgerStateMachine.CanAccept(row, out why)) return false;

        ev = new LedgerEvent(now, "state", bare.ToString(), Actor: actor, From: row.State, To: "accepted");
        return true;
    }

    /// <summary>
    /// Drop a hierarchy row. The reason is REQUIRED: dropping is how work stops existing,
    /// and an unexplained drop is exactly the audit gap this design was built to close
    /// (§6.4 — dropped is a terminal state, not a removal). Tasks are declined, not
    /// dropped.
    /// </summary>
    public static bool TryDrop(
        LedgerRepoSnapshot snap, LedgerId id, string reason, string actor, DateTimeOffset now,
        out LedgerEvent? ev, out string why)
    {
        ev = null; why = "";
        var bare = id with { Repo = null };

        if (bare.Type == LedgerType.Task)
        { why = $"{bare} is a task — use `ledger decline {bare} [note]`"; return false; }
        if (string.IsNullOrWhiteSpace(reason))
        { why = $"drop needs a reason: `ledger drop {bare} <why>`"; return false; }

        var row = snap.Rows.FirstOrDefault(r => r.Id == bare);
        if (row is null) { why = $"{bare}: no such item in {snap.Repo}'s ledger.md"; return false; }
        if (!LedgerStateMachine.CanTransitionHierarchy(row.State, "dropped"))
        { why = $"{bare} is {row.State}; that is terminal and cannot be dropped"; return false; }

        ev = new LedgerEvent(now, "state", bare.ToString(), Actor: actor,
            From: row.State, To: "dropped", Note: reason.Trim());
        return true;
    }

    /// <summary>
    /// Decline a task. Cheap on purpose (§6.2): auto-creating a row from a casual
    /// <c>type:"task"</c> mail makes it a tracked debt, and this is the release valve —
    /// one event, note optional. A high decline rate is information, not failure. Work
    /// already under way is <c>abandoned</c>, a different word for a different thing.
    /// </summary>
    public static bool TryDecline(
        LedgerRepoSnapshot snap, LedgerId id, string? note, string actor, DateTimeOffset now,
        out LedgerEvent? ev, out string why)
    {
        ev = null; why = "";
        var bare = id with { Repo = null };

        if (bare.Type != LedgerType.Task)
        { why = $"{bare} is not a task — use `ledger drop {bare} <why>`"; return false; }

        var task = snap.Tasks.FirstOrDefault(t => t.Id == bare);
        if (task is null) { why = $"{bare}: no such task in {snap.Repo}"; return false; }
        if (!LedgerStateMachine.CanTransitionTask(task.State, "declined"))
        { why = $"{bare} is {task.State}; only an unstarted task can be declined (started work is abandoned)"; return false; }

        ev = new LedgerEvent(now, "task-declined", bare.ToString(), Actor: actor,
            Note: string.IsNullOrWhiteSpace(note) ? null : note.Trim());
        return true;
    }
}

/// <summary>
/// One <see cref="LedgerWriter"/> per repo, shared by everything in the orchestrator that
/// records an obligation — mail ingestion, TaskTracker, the work queue, escalation. One
/// instance per repo matters: the writer's id reservation is per-instance, so two writers
/// over one directory would each have to fall back to the cross-process mutex for every
/// allocation.
/// </summary>
public sealed class LedgerWriters
{
    readonly Func<string, string?> _rootOfRepo;
    readonly Action<string> _log;
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, LedgerWriter> _byRepo =
        new(StringComparer.OrdinalIgnoreCase);

    public LedgerWriters(Func<string, string?> rootOfRepo, Action<string> log)
    { _rootOfRepo = rootOfRepo; _log = log; }

    /// <summary>The writer for a registered repo, or null when the repo is unknown.</summary>
    public LedgerWriter? For(string? repo)
    {
        if (string.IsNullOrWhiteSpace(repo)) return null;
        if (_byRepo.TryGetValue(repo, out var existing)) return existing;
        var root = _rootOfRepo(repo);
        if (string.IsNullOrWhiteSpace(root)) return null;
        // createIfAbsent: a task mail to a repo with no ledger is exactly the obligation
        // that must not vanish (spec §5.4), so the event log is created on first write.
        // ledger.md — the hierarchy — is still only ever written by a human.
        return _byRepo.GetOrAdd(repo, _ => new LedgerWriter(
            Path.Combine(root, LedgerView.LedgerSubdir), _log, createIfAbsent: true));
    }

    /// <summary>The writer for the repo half of a <c>repo:persona</c> instance id.</summary>
    public LedgerWriter? ForInstance(string? instanceId) =>
        string.IsNullOrWhiteSpace(instanceId) ? null : For(LedgerMailIngest.RepoOf(instanceId));
}
