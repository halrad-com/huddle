using System.Text.Json;

namespace Huddle;

/// <summary>
/// Spec §5.4. Any mail with <c>"type":"task"</c> opens a tracked row in the recipient's
/// repo ledger, whoever sent it and without anyone running a command. This is the change
/// that catches peer-to-peer dispatch: the audited session's four dropped assignments
/// were all <c>type:"task"</c>, and every one of them would have appeared in
/// <c>ledger open --by-age</c> on the day it was sent.
///
/// <para>Pure by design — decisions here are a function of the mail and nothing else, so
/// they are testable without a filesystem, an orchestrator or a live session. The two
/// writer-touching helpers take the writer explicitly.</para>
/// </summary>
public static class LedgerMailIngest
{
    /// <summary>The ledger is an index, not a store (§6.1) — the mail holds the prose.</summary>
    public const int MaxTitle = 100;

    /// <summary>
    /// The <c>task-assigned</c> event a mail should open, or null when the mail is not a
    /// task. <paramref name="mailRelPath"/> is both the evidence and the DEDUP KEY: a
    /// rescan, a retry or a restart re-finds the row by it instead of opening a second.
    /// </summary>
    /// <param name="recipientInstance"><c>repo:persona</c> — the agent that now owes the work.</param>
    public static LedgerEvent? Assigned(
        IpcMessage msg, string mailRelPath, string recipientInstance, LedgerId newId, DateTimeOffset now)
    {
        if (!IsTask(msg)) return null;

        var body = SafeBody(msg);
        return new LedgerEvent(
            now, "task-assigned", newId.ToString(),
            Actor: msg.From,                      // who asked
            Owner: recipientInstance,             // who owes
            Parent: ParentOf(body, recipientInstance),
            Pri: PriOf(body),
            Title: Title(msg.Subject),
            Refs: new[] { mailRelPath });
    }

    /// <summary>Mail moving inbox -> processed IS acknowledgement; it already has a
    /// filesystem meaning and this reuses it rather than inventing a second one.</summary>
    public static LedgerEvent Acked(LedgerId id, string recipientInstance, DateTimeOffset now) =>
        new(now, "task-acked", id.ToString(), Actor: recipientInstance);

    public static bool IsTask(IpcMessage msg) =>
        string.Equals(msg.Type, "task", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The dedup key for one piece of mail: its path relative to the huddle root, forward
    /// slashes, so it is short enough to read in a nudge and stable across machines.
    ///
    /// <para>Both ends of the obligation go through this ONE function on purpose. Delivery
    /// opens the row from the inbox file's full path; acknowledgement finds it again from
    /// a bare file name reassembled against the inbox directory. If those two ever
    /// produced different text the row would open and then never be acknowledged — and
    /// nothing would fail, it would just quietly stay "assigned" forever.</para>
    /// </summary>
    public static string MailRef(string huddleRoot, string mailFilePath) =>
        Path.GetRelativePath(huddleRoot, mailFilePath).Replace('\\', '/');

    /// <inheritdoc cref="MailRef(string,string)"/>
    public static string MailRef(string huddleRoot, string ipcDir, string safePathName, string mailFileName) =>
        MailRef(huddleRoot, Path.Combine(ipcDir, safePathName, "inbox", mailFileName));

    /// <summary>
    /// Record acknowledgement for the task opened by <paramref name="mailRelPath"/>, if
    /// there is one and it has not moved past <c>assigned</c> already. Idempotent on
    /// purpose: reap and forget can both name the same file on consecutive ticks, and an
    /// <c>acked -&gt; acked</c> would surface as a replay problem in every `ledger` render.
    /// Silent when the mail opened no task — most mail is not a task.
    /// </summary>
    public static void AckIfOpen(
        LedgerWriter writer, string mailRelPath, string recipientInstance, DateTimeOffset now, Action<string> log)
    {
        if (!writer.TryFindTaskByRef(mailRelPath, out var id)) return;

        var problems = new List<string>();
        var task = TaskMaterializer.Materialize(writer.ReadAll(problems), problems)
            .FirstOrDefault(t => t.Id == id);
        if (task is null) return;
        if (!LedgerStateMachine.CanTransitionTask(task.State, "acked")) return;

        writer.Append(Acked(id, recipientInstance, now));
        log($"ledger: {id} acknowledged by {recipientInstance}");
    }

    /// <summary>
    /// The one line an agent often sees before deciding whether to interrupt itself
    /// (spec §5.8), so a task must not look like an FYI. Always exactly one line — a
    /// subject spanning lines would otherwise forge a second wake.
    /// </summary>
    public static string NudgeLine(IpcMessage msg, string mailRelPath, LedgerId? taskId)
    {
        var subject = OneLine(msg.Subject ?? "");
        return taskId is { } id
            ? $"[huddle] TASK {id} assigned to you by {msg.From} — {subject} — read {mailRelPath}"
            : $"[huddle mail from {msg.From}] {subject} — read {mailRelPath}";
    }

    // ---- body reading; a hand-written body is never allowed to throw ----

    static JsonElement? SafeBody(IpcMessage msg)
    {
        try
        {
            var b = msg.BodyObject;
            return b.ValueKind == JsonValueKind.Object ? b : null;
        }
        catch (JsonException) { return null; }   // body was a string that is not JSON
    }

    static string? StringProp(JsonElement? body, string name) =>
        body is { } b && b.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    /// <summary>
    /// The optional <c>"ledger"</c> field. A bare id is qualified with the recipient's
    /// repo so the event still names something definite when read out of context — in a
    /// diff, or from another repo's view. Anything that does not parse, or that names a
    /// Task (tasks are leaves), leaves the row an ORPHAN rather than failing the ingest:
    /// §6.3, an orphan is the signal, and refusing the mail would drop the very
    /// obligation the row exists to keep.
    /// </summary>
    static string? ParentOf(JsonElement? body, string recipientInstance)
    {
        var raw = StringProp(body, "ledger");
        if (!LedgerId.TryParse(raw, out var id) || id.Type == LedgerType.Task) return null;
        return id.Repo != null ? id.ToString() : id.Qualify(RepoOf(recipientInstance)).ToString();
    }

    static string? PriOf(JsonElement? body)
    {
        var raw = (StringProp(body, "pri") ?? "").Trim().ToUpperInvariant();
        return raw is "P0" or "P1" or "P2" or "P3" ? raw : null;
    }

    /// <summary><c>repo:persona</c> -> <c>repo</c>.</summary>
    public static string RepoOf(string instanceId)
    {
        var colon = instanceId.IndexOf(':');
        return colon > 0 ? instanceId[..colon] : instanceId;
    }

    static string Title(string? subject)
    {
        var s = OneLine(subject ?? "");
        if (s.Length == 0) return "(no subject)";
        return s.Length <= MaxTitle ? s : s[..MaxTitle];
    }

    static string OneLine(string s) =>
        s.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
}
