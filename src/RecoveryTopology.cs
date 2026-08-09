using System.Text.Json;

namespace Huddle;

/// <summary>
/// One recoverable session's coordination role (I010 F2): who dispatched it, how much
/// unread mail waits in its inbox, and whether it is a hub other sessions report to.
/// </summary>
public sealed record TopologyInfo(string? DispatchedBy, int UnreadMail, bool IsHub);

/// <summary>
/// Derives coordination topology for the `recover` listing from what is already on
/// disk: dispatch-batch command files in _huddle/processed name their sender and
/// tasks; per-session inbox dirs hold unread mail. Resume order matters — workers
/// report to hubs (dispatchers, lane leads); recovering workers before hubs stalls
/// the workflow (live lesson, 2026-08-09). Read-only over both directories; every
/// failure degrades to "no annotation", never throws.
/// </summary>
public static class RecoveryTopology
{
    // A dispatched session spawns within moments of its batch; anything beyond this
    // window is a same-named session from a different lifetime.
    private static readonly TimeSpan DispatchWindow = TimeSpan.FromMinutes(30);

    // Newest-first scan cap over _huddle/processed — bounds the cost on a machine
    // with months of processed commands.
    private const int MaxScan = 200;

    public static TopologyInfo Analyze(
        string instanceId, DateTime? startedAt,
        string processedDir, string ipcRoot)
    {
        var (repo, persona) = SplitInstanceId(instanceId);

        string? dispatchedBy = null;
        var dispatchedAny = false;

        foreach (var json in EnumerateProcessed(processedDir))
        {
            if (dispatchedBy == null && persona != null)
                dispatchedBy = FindDispatcher(json, repo, persona, startedAt);
            if (!dispatchedAny)
                dispatchedAny = SenderIs(json, instanceId);
            if (dispatchedBy != null && dispatchedAny)
                break;
        }

        var unread = CountUnread(ipcRoot, instanceId);
        return new TopologyInfo(dispatchedBy, unread, IsHub: unread > 0 || dispatchedAny);
    }

    /// <summary>
    /// Pure core: if this JSON document is a dispatch-batch containing a task for
    /// repo:persona, and the session started within the dispatch window after the
    /// batch, return the sender. Null on any mismatch or malformed input.
    /// </summary>
    public static string? FindDispatcher(string json, string repoName, string persona, DateTime? startedAt)
    {
        if (startedAt == null)
            return null;

        var msg = Parse(json);
        if (msg == null || !msg.Subject.Equals("dispatch-batch", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!DateTime.TryParse(msg.Timestamp, null,
                System.Globalization.DateTimeStyles.AdjustToUniversal |
                System.Globalization.DateTimeStyles.AssumeUniversal, out var batchTime))
            return null;

        var startedUtc = startedAt.Value.Kind == DateTimeKind.Utc
            ? startedAt.Value
            : startedAt.Value.ToUniversalTime();
        var delta = startedUtc - batchTime;
        if (delta < TimeSpan.Zero || delta > DispatchWindow)
            return null;

        try
        {
            if (msg.Body.ValueKind != JsonValueKind.Object ||
                !msg.Body.TryGetProperty("tasks", out var tasks) ||
                tasks.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var task in tasks.EnumerateArray())
            {
                var taskRepo = task.TryGetProperty("repo", out var r) ? r.GetString() : null;
                var taskPersona = task.TryGetProperty("persona", out var p) ? p.GetString() : null;
                if (string.Equals(taskRepo, repoName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(taskPersona, persona, StringComparison.OrdinalIgnoreCase))
                    return string.IsNullOrEmpty(msg.From) ? null : msg.From;
            }
        }
        catch (Exception)
        {
            // Body shaped unexpectedly — no lineage claim.
        }
        return null;
    }

    // ---- helpers -----------------------------------------------------------

    // instanceId is repo:persona[-N] (e.g. "otherapp:architect-3"). The -N suffix
    // distinguishes parallel sessions but the dispatch task names the bare persona.
    private static (string Repo, string? Persona) SplitInstanceId(string instanceId)
    {
        var colon = instanceId.IndexOf(':');
        if (colon < 0) return (instanceId, null);
        var repo = instanceId[..colon];
        var persona = instanceId[(colon + 1)..];
        var dash = persona.LastIndexOf('-');
        if (dash > 0 && int.TryParse(persona[(dash + 1)..], out _))
            persona = persona[..dash];
        return (repo, persona.Length == 0 ? null : persona);
    }

    private static bool SenderIs(string json, string instanceId)
    {
        var msg = Parse(json);
        return msg != null &&
               msg.Subject.Equals("dispatch-batch", StringComparison.OrdinalIgnoreCase) &&
               msg.From.Equals(instanceId, StringComparison.OrdinalIgnoreCase);
    }

    private static IpcMessage? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<IpcMessage>(json); }
        catch (Exception) { return null; }
    }

    private static IEnumerable<string> EnumerateProcessed(string processedDir)
    {
        List<string> files;
        try
        {
            if (!Directory.Exists(processedDir)) yield break;
            files = Directory.EnumerateFiles(processedDir, "*.json")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(MaxScan)
                .Select(f => f.FullName)
                .ToList();
        }
        catch (Exception) { yield break; }

        foreach (var f in files)
        {
            string? json = null;
            try { json = File.ReadAllText(f); }
            catch (Exception) { /* mid-move or locked — skip */ }
            if (json != null) yield return json;
        }
    }

    private static int CountUnread(string ipcRoot, string instanceId)
    {
        try
        {
            var inbox = Path.Combine(ipcRoot, instanceId.Replace(':', '_'), "inbox");
            return Directory.Exists(inbox)
                ? Directory.EnumerateFiles(inbox, "*.json").Count()
                : 0;
        }
        catch (Exception) { return 0; }
    }
}
