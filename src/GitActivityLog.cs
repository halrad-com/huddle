using System.Text.Json;
using System.Text.Json.Serialization;

namespace Huddle;

public sealed record GitActivityEntry(
    [property: JsonPropertyName("ts")] DateTimeOffset Ts,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("instance")] string? Instance,
    [property: JsonPropertyName("session")] string? Session,
    [property: JsonPropertyName("host")] string? Host,
    [property: JsonPropertyName("protocol")] string? Protocol,
    [property: JsonPropertyName("repo")] string? Repo,
    [property: JsonPropertyName("verb")] string? Verb,
    [property: JsonPropertyName("remote")] string? Remote,
    [property: JsonPropertyName("identity")] string? Identity,
    [property: JsonPropertyName("branch")] string? Branch,
    [property: JsonPropertyName("sha")] string? Sha);

/// <summary>
/// Append-only logs/git-activity.jsonl. The cred-request drop is the one exact
/// who-signal huddle has (instance → host → time) and it used to be deleted the moment
/// it was logged; movements were only ever console lines. Same shape as HandoffLedger.
/// </summary>
public sealed class GitActivityLog
{
    private readonly string _path;
    private readonly object _lock = new();
    static readonly JsonSerializerOptions Opts = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public GitActivityLog(string path) => _path = path;

    public void Append(GitActivityEntry e)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
            File.AppendAllText(_path, JsonSerializer.Serialize(e, Opts) + "\n");
        }
    }

    public IReadOnlyList<GitActivityEntry> ReadSince(DateTimeOffset since)
    {
        if (!File.Exists(_path)) return Array.Empty<GitActivityEntry>();
        var list = new List<GitActivityEntry>();
        foreach (var line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var e = JsonSerializer.Deserialize<GitActivityEntry>(line, Opts);
                if (e != null && e.Ts >= since) list.Add(e);
            }
            catch (JsonException) { /* skip */ }
        }
        return list;
    }

    /// <summary>
    /// A drop file body is a single tab-separated line: instanceId, sessionId, protocol,
    /// host — the same shape <see cref="GitActivityMonitor.FormatAuthLine"/> reads. Null
    /// for an empty or malformed drop. Pure — unit-tested.
    /// </summary>
    public static GitActivityEntry? ParseAuthDrop(string dropText, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(dropText)) return null;
        var p = dropText.Trim().Split('\t');
        var instance = p.Length > 0 ? p[0].Trim() : "";
        var session = p.Length > 1 ? p[1].Trim() : "";
        var protocol = p.Length > 2 ? p[2].Trim() : "";
        var host = p.Length > 3 ? p[3].Trim() : "";
        if (instance.Length == 0 || host.Length == 0) return null;
        if (session.Length > 8) session = session[..8];
        return new(now, "auth", instance, session.Length > 0 ? session : null, host, protocol.Length > 0 ? protocol : null,
            null, null, null, null, null, null);
    }

    /// <summary>
    /// One raw remote-tracking reflog line → a move entry. The reflog line's OWN unix
    /// timestamp is used, not the clock: that is what makes the log exact when it is
    /// replayed later, and what lets a movement recorded while huddle was down still
    /// carry the time it actually happened. Pure — unit-tested.
    /// </summary>
    public static GitActivityEntry? ParseMovement(string repo, string reference, string rawLine, string? identity)
    {
        if (string.IsNullOrWhiteSpace(rawLine)) return null;
        var tab = rawLine.IndexOf('\t');
        if (tab < 0) return null;
        var tokens = rawLine[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2) return null;
        var sha = tokens[1].Length >= 7 ? tokens[1][..7] : tokens[1];
        // timestamp is the token before the tz offset (last token)
        DateTimeOffset ts = DateTimeOffset.UtcNow;
        if (tokens.Length >= 4 && long.TryParse(tokens[^2], out var unix)) ts = DateTimeOffset.FromUnixTimeSeconds(unix);
        var msg = rawLine[(tab + 1)..].ToLowerInvariant();
        var verb = msg.Contains("push") ? "push" : msg.Contains("fetch") ? "fetch" : msg.Contains("pull") ? "pull" : "update";
        var (remote, branch) = GitActivityMonitor.SplitReference(reference);
        return new(ts, "move", null, null, null, null, repo, verb, remote, identity, branch, sha);
    }
}
