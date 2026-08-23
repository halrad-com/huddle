using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Huddle;

public class SessionStateEntry
{
    [JsonPropertyName("instanceId")]
    public string InstanceId { get; set; } = "";

    [JsonPropertyName("repoName")]
    public string RepoName { get; set; } = "";

    [JsonPropertyName("persona")]
    public string? Persona { get; set; }

    [JsonPropertyName("pid")]
    public int Pid { get; set; }

    [JsonPropertyName("startedAt")]
    public DateTime StartedAt { get; set; }

    // Stored as a string (not raw Guid) so state.json is self-documenting: the
    // value pastes straight into `claude --resume <sessionId>`.
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    // Process identity (F1 / I009): Windows recycles PIDs, so a stale state.json
    // could bind a "recovered" session to an unrelated process that huddle would
    // later Kill(entireProcessTree). StartTime + image name pin the identity;
    // both are verified on recovery. Null on entries written by pre-fix builds
    // (IdentityMatches has a legacy fallback for those).
    [JsonPropertyName("procStartedAt")]
    public DateTime? ProcStartedAt { get; set; }

    [JsonPropertyName("procName")]
    public string? ProcName { get; set; }

    // I010: "live" (default) or "recoverable" — a dead-but-resumable session retained
    // as the crash-recovery roster. Absent in legacy state files → live.
    [JsonPropertyName("status")]
    public string Status { get; set; } = "live";

    // The task the session was started for (F3); null/empty for bare starts.
    [JsonPropertyName("declaredPurpose")]
    public string? DeclaredPurpose { get; set; }

    // Project slug the session serves (projects phase 1); null when unstamped.
    [JsonPropertyName("project")]
    public string? Project { get; set; }

    [JsonPropertyName("diedAt")]
    public DateTime? DiedAt { get; set; }
}

public static class SessionState
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static void Save(string stateFile, IReadOnlyDictionary<string, SessionInstance> instances,
        IReadOnlyList<SessionStateEntry>? recoverable = null)
    {
        var entries = new List<SessionStateEntry>();
        foreach (var i in instances.Values.Where(i => i.IsAlive && i.Process != null))
        {
            var entry = new SessionStateEntry
            {
                InstanceId = i.InstanceId,
                RepoName = i.RepoName,
                Persona = i.ActivePersona,
                Pid = i.Process!.Id,
                StartedAt = i.StartedAt ?? DateTime.Now,
                SessionId = i.SessionId?.ToString(),
                Status = "live",
                DeclaredPurpose = i.DeclaredPurpose,
                Project = i.Project
            };
            // Process identity for recovery verification. Reads can throw if the
            // process exits between the IsAlive check and here — the entry is
            // still written (legacy-shaped) and the fallback check covers it.
            try
            {
                entry.ProcStartedAt = i.Process.StartTime;
                entry.ProcName = i.Process.ProcessName;
            }
            catch { /* exited mid-save: identity fields stay null */ }
            entries.Add(entry);
        }

        // I010: carry the recoverable roster through every rewrite — a save must never
        // silently drop dead sessions. A roster entry whose conversation is now owned
        // by a live session is dropped (the live entry wins).
        if (recoverable != null)
            entries.AddRange(recoverable.Where(r =>
                r.SessionId == null ||
                !entries.Any(e => e.SessionId != null && e.SessionId == r.SessionId)));

        var json = JsonSerializer.Serialize(entries, JsonOptions);
        File.WriteAllText(stateFile, json);
    }

    /// <summary>
    /// The recorded entries, for read-only views (stats' session roster). Recovery has
    /// always deserialised this file inline; this is the same read exposed without the
    /// side effects, and it never throws — a missing or corrupt state file yields an
    /// empty roster rather than failing the verb that asked.
    /// </summary>
    public static List<SessionStateEntry> LoadEntries(string stateFile)
    {
        if (!File.Exists(stateFile)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<SessionStateEntry>>(File.ReadAllText(stateFile)) ?? [];
        }
        catch { return []; }
    }

    public static int Recover(
        string stateFile,
        SessionManager manager,
        IpcManager? ipc,
        Action<string> log)
    {
        if (!File.Exists(stateFile))
            return 0;

        List<SessionStateEntry> entries;
        try
        {
            var json = File.ReadAllText(stateFile);
            entries = JsonSerializer.Deserialize<List<SessionStateEntry>>(json) ?? [];
        }
        catch (Exception ex)
        {
            log($"State: Failed to read {stateFile}: {ex.Message}");
            return 0;
        }

        if (entries.Count == 0)
            return 0;

        var recovered = 0;
        foreach (var entry in entries)
        {
            // Entries already marked recoverable load straight into the roster —
            // they died before a previous run and keep their original DiedAt.
            if (entry.Status == "recoverable")
            {
                MarkRecoverable(entry, manager);
                continue;
            }

            Process? proc = null;
            try
            {
                proc = Process.GetProcessById(entry.Pid);
                if (proc.HasExited)
                {
                    proc.Dispose();
                    MarkRecoverable(entry, manager);
                    continue;
                }

                // F1 / I009: a live PID is NOT proof this is the original process —
                // Windows recycles PIDs. Verify identity before binding; everything
                // downstream (stop, restart, Kill entire tree) acts on this handle.
                if (!IdentityMatches(entry, proc.StartTime, proc.ProcessName))
                {
                    log($"State: NOT recovering '{entry.InstanceId}' — PID {entry.Pid} is a different process " +
                        $"({proc.ProcessName}, started {proc.StartTime:yyyy-MM-dd HH:mm:ss}); held as recoverable.");
                    proc.Dispose();
                    MarkRecoverable(entry, manager);
                    continue;
                }
            }
            catch
            {
                // PID no longer exists (or identity reads failed — treat as not ours).
                // I010: dead is not disposable — the entry becomes the recovery roster.
                proc?.Dispose();
                MarkRecoverable(entry, manager);
                continue;
            }

            // Reconnect. Carry the session id back so the recovered session keeps
            // its Resume line in context.md and can be reopened via the `resume` verb.
            Guid? sessionId = Guid.TryParse(entry.SessionId, out var sid) ? sid : null;
            if (!manager.Recover(entry.InstanceId, entry.RepoName, entry.Persona, proc, entry.StartedAt, sessionId, entry.DeclaredPurpose, entry.Project))
            {
                proc.Dispose();
                continue;
            }

            // Re-establish IPC watcher
            var safeName = entry.InstanceId.Replace(':', '_');
            ipc?.EnsureMailbox(safeName);
            ipc?.Watch(safeName, entry.InstanceId);

            recovered++;
            var personaLabel = entry.Persona != null ? $" [{entry.Persona}]" : "";
            log($"Recovered '{entry.InstanceId}'{personaLabel} (PID {entry.Pid})");
        }

        return recovered;
    }

    // I010: route a dead/unverifiable entry into the recoverable roster instead of
    // dropping it. First transition stamps DiedAt; an entry that was already
    // recoverable keeps its original timestamp. Dedup by session id (the resume token).
    private static void MarkRecoverable(SessionStateEntry entry, SessionManager manager)
    {
        if (entry.Status != "recoverable")
        {
            entry.Status = "recoverable";
            entry.DiedAt = DateTime.Now;
        }
        var dup = entry.SessionId != null &&
                  manager.Recoverable.Any(r => r.SessionId == entry.SessionId);
        if (!dup)
            manager.Recoverable.Add(entry);
    }

    // Tolerance for StartTime comparison on new-schema entries: JSON round-trips and
    // kernel-time reads can drift slightly; recycled PIDs differ by minutes-to-days.
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(5);

    // Legacy entries carry no process identity; the session's own StartedAt is the
    // best available anchor. The wrapper process spawns within moments of session
    // registration, so a live process born outside this window of StartedAt is a
    // recycled PID (always born LATER than the original) or a post-reboot stranger.
    private static readonly TimeSpan LegacyWindow = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Pure recovery-identity decision (F1 / I009): does the live process at the
    /// entry's PID look like the process the entry was written for?
    /// New-schema entries (ProcStartedAt present) require a StartTime match within
    /// tolerance AND, when recorded, a case-insensitive image-name match.
    /// Legacy entries fall back to the session-start window.
    /// </summary>
    public static bool IdentityMatches(SessionStateEntry entry, DateTime procStartTime, string procName)
    {
        if (entry.ProcStartedAt is { } recorded)
        {
            if ((procStartTime - recorded).Duration() > StartTimeTolerance)
                return false;
            if (!string.IsNullOrEmpty(entry.ProcName) &&
                !entry.ProcName.Equals(procName, StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        // Legacy (pre-identity state.json): window around the session's own start.
        return (procStartTime - entry.StartedAt).Duration() <= LegacyWindow;
    }
}
