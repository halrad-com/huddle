using System.Text.Json;

namespace Huddle;

/// <summary>
/// One spawned OS resource declared by a session (spec: docs/resource-ledger-spec.md).
/// </summary>
public sealed record ResourceEntry(
    string Id, string Type, int? Pid, int? Port, string What,
    IReadOnlyList<string> Artifacts, string Cleanup,
    DateTime SpawnedAt, DateTime? CleanedAt);

public sealed record SessionResources(
    string Session, DateTime Updated, IReadOnlyList<ResourceEntry> Resources);

/// <summary>
/// Reads ipc/resledger/&lt;safe-name&gt;.json files and detects leaks: uncleaned
/// entries whose pid is still alive. Read-only — sessions own their ledger
/// files; huddle only inspects. Reclaim (killing) is a separate, opt-in step.
/// </summary>
public class ResourceLedger
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _ledgerDir;
    private readonly Action<string> _log;

    public ResourceLedger(string ledgerDir, Action<string> log)
    {
        _ledgerDir = ledgerDir;
        _log = log;
    }

    public SessionResources? ReadSession(string safeName)
    {
        var path = Path.Combine(_ledgerDir, safeName + ".json");
        if (!File.Exists(path)) return null;
        try
        {
            var doc = JsonSerializer.Deserialize<LedgerDoc>(File.ReadAllText(path), Opts);
            if (doc?.Session is null) return null;
            var entries = (doc.Resources ?? []).Select(r => new ResourceEntry(
                r.Id ?? "?", r.Type ?? "other", r.Pid, r.Port, r.What ?? "",
                r.Artifacts ?? [], r.Cleanup ?? "",
                r.SpawnedAt, r.CleanedAt)).ToList();
            return new SessionResources(doc.Session, doc.Updated, entries);
        }
        catch (Exception ex)
        {
            _log($"ResourceLedger: malformed ledger {safeName}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Uncleaned entries with a live pid, across all ledger files.</summary>
    public IReadOnlyList<(string SafeName, ResourceEntry Entry)> FindLeaks(Func<int, bool>? pidAlive = null)
    {
        pidAlive ??= DefaultPidAlive;
        var leaks = new List<(string, ResourceEntry)>();
        if (!Directory.Exists(_ledgerDir)) return leaks;
        foreach (var file in Directory.GetFiles(_ledgerDir, "*.json"))
        {
            var safeName = Path.GetFileNameWithoutExtension(file);
            var session = ReadSession(safeName);
            if (session is null) continue;
            foreach (var e in session.Resources)
                if (e.CleanedAt is null && e.Pid is int pid && pidAlive(pid))
                    leaks.Add((safeName, e));
        }
        return leaks;
    }

    public static string FormatLeak(string safeName, ResourceEntry e) =>
        $"RESOURCE LEAK {safeName}: {e.Id} pid={e.Pid?.ToString() ?? "?"} ({e.What}) — cleanup: {e.Cleanup}";

    private static bool DefaultPidAlive(int pid)
    {
        try { return !System.Diagnostics.Process.GetProcessById(pid).HasExited; }
        catch { return false; }
    }

    // JSON shapes for deserialization only — public surface is the records above.
    private sealed class LedgerDoc
    {
        public string? Session { get; set; }
        public DateTime Updated { get; set; }
        public List<LedgerEntry>? Resources { get; set; }
    }
    private sealed class LedgerEntry
    {
        public string? Id { get; set; }
        public string? Type { get; set; }
        public int? Pid { get; set; }
        public int? Port { get; set; }
        public string? What { get; set; }
        public List<string>? Artifacts { get; set; }
        public string? Cleanup { get; set; }
        public DateTime SpawnedAt { get; set; }
        public DateTime? CleanedAt { get; set; }
    }
}
