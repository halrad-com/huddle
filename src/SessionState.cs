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
}

public static class SessionState
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static void Save(string stateFile, IReadOnlyDictionary<string, SessionInstance> instances)
    {
        var entries = instances.Values
            .Where(i => i.IsAlive && i.Process != null)
            .Select(i => new SessionStateEntry
            {
                InstanceId = i.InstanceId,
                RepoName = i.RepoName,
                Persona = i.ActivePersona,
                Pid = i.Process!.Id,
                StartedAt = i.StartedAt ?? DateTime.Now,
                SessionId = i.SessionId?.ToString()
            })
            .ToList();

        var json = JsonSerializer.Serialize(entries, JsonOptions);
        File.WriteAllText(stateFile, json);
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
            Process? proc = null;
            try
            {
                proc = Process.GetProcessById(entry.Pid);
                if (proc.HasExited)
                {
                    proc.Dispose();
                    continue;
                }
            }
            catch
            {
                // PID no longer exists
                proc?.Dispose();
                continue;
            }

            // Reconnect. Carry the session id back so the recovered session keeps
            // its Resume line in context.md and can be reopened via the `resume` verb.
            Guid? sessionId = Guid.TryParse(entry.SessionId, out var sid) ? sid : null;
            if (!manager.Recover(entry.InstanceId, entry.RepoName, entry.Persona, proc, entry.StartedAt, sessionId))
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
}
