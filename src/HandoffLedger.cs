using System.Text.Json;
using System.Text.Json.Serialization;

namespace Huddle;

/// <summary>One recorded agent-to-agent handoff.</summary>
public sealed record HandoffEntry(
    [property: JsonPropertyName("at")]     DateTime At,
    [property: JsonPropertyName("from")]   string From,
    [property: JsonPropertyName("to")]     string To,
    [property: JsonPropertyName("task")]   string Task,
    [property: JsonPropertyName("state")]  string? State,
    [property: JsonPropertyName("source")] string Source);   // mail filename — the idempotency key

/// <summary>
/// Durable, append-only record of handoffs (logs/handoffs.jsonl) so the operator can
/// trace who handed what to whom without asking. Idempotent by source mail filename: a
/// re-processed inbox file (e.g. handoff to a session that isn't live yet) never
/// double-records or double-announces. Single writer (IpcManager); the `handoffs` verb
/// only reads.
/// </summary>
public sealed class HandoffLedger
{
    private readonly string _path;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };

    public HandoffLedger(string path) => _path = path;

    /// <summary>Append unless this source is already recorded. Returns true iff newly written.</summary>
    public bool Record(HandoffEntry e)
    {
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(e.Source) && HasSource(e.Source)) return false;
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(_path, JsonSerializer.Serialize(e, Opts) + "\n");
                return true;
            }
            catch { return false; }   // ledger is best-effort; never break mail delivery
        }
    }

    /// <summary>Every recorded handoff, file order (append order). Bad lines are skipped.</summary>
    public IReadOnlyList<HandoffEntry> ReadAll()
    {
        var list = new List<HandoffEntry>();
        if (!File.Exists(_path)) return list;
        try
        {
            foreach (var line in File.ReadLines(_path))
            {
                var l = line.Trim();
                if (l.Length == 0) continue;
                try { if (JsonSerializer.Deserialize<HandoffEntry>(l) is { } e) list.Add(e); }
                catch { /* skip a corrupt line, keep the rest */ }
            }
        }
        catch { /* unreadable ledger -> empty */ }
        return list;
    }

    private bool HasSource(string source)
    {
        foreach (var e in ReadAll())
            if (string.Equals(e.Source, source, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// Extract (to, task, state) from a handoff mail's body object, falling back to the
    /// mail's own `to` and `subject`. Tolerant of missing fields; the caller passes
    /// <c>IpcMessage.BodyObject</c>, which already unwraps a string-encoded body.
    /// </summary>
    public static (string To, string Task, string? State) ParseBody(
        JsonElement body, string? fallbackTo, string? fallbackSubject)
    {
        string? to = null, task = null, state = null;
        if (body.ValueKind == JsonValueKind.Object)
        {
            if (body.TryGetProperty("to", out var t) && t.ValueKind == JsonValueKind.String) to = t.GetString();
            if (body.TryGetProperty("task", out var k) && k.ValueKind == JsonValueKind.String) task = k.GetString();
            if (body.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String) state = s.GetString();
        }
        return (Nz(to) ?? Nz(fallbackTo) ?? "?",
                Nz(task) ?? Nz(fallbackSubject) ?? "(unspecified)",
                Nz(state));
    }

    private static string? Nz(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
