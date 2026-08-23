using System.Text.Json;

namespace Huddle;

/// <summary>
/// Declarative work scheduler. Holds work units + their state; only surfaces a unit as
/// Dispatchable when no Active unit overlaps its files and all its DependsOn are Done.
/// Pure scheduling logic (no I/O unless a persistDir is given). Thread-safe.
/// </summary>
public class WorkQueue
{
    private readonly object _lock = new();
    private readonly Dictionary<string, WorkUnit> _units = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, QueueState> _state = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _dir;
    private readonly Action<string> _log;

    public WorkQueue(string? persistDir = null, Action<string>? log = null)
    {
        _dir = persistDir;
        _log = log ?? (_ => { });
    }

    public void Enqueue(IReadOnlyList<WorkUnit> units)
    {
        lock (_lock)
        {
            foreach (var u in units)
            {
                if (_units.ContainsKey(u.Id) ||
                    units.Count(x => x.Id.Equals(u.Id, StringComparison.OrdinalIgnoreCase)) > 1)
                    throw new InvalidOperationException($"duplicate work-unit id '{u.Id}'");
            }

            var known = new HashSet<string>(_units.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var u in units) known.Add(u.Id);
            foreach (var u in units)
                foreach (var d in u.DependsOn)
                    if (!known.Contains(d))
                        throw new InvalidOperationException($"unit '{u.Id}' depends on unknown '{d}'");

            var combined = _units.Values.Concat(units).ToList();
            var cycle = FindCycle(combined);
            if (cycle != null)
                throw new InvalidOperationException($"dependency cycle: {string.Join(" -> ", cycle)}");

            foreach (var u in units) { _units[u.Id] = u; _state[u.Id] = QueueState.Queued; Persist(u.Id); }
            _log($"queue: enqueued {units.Count} unit(s): {string.Join(", ", units.Select(u => u.Id))}");
        }
    }

    public IReadOnlyList<WorkUnit> Dispatchable()
    {
        lock (_lock)
        {
            var activeFiles = _units.Values
                .Where(u => _state[u.Id] == QueueState.Active)
                .SelectMany(u => u.Files)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return _units.Values
                .Where(u => _state[u.Id] == QueueState.Queued)
                .Where(u => !u.Files.Any(activeFiles.Contains))
                .Where(u => u.DependsOn.All(d => _state.TryGetValue(d, out var s) && s == QueueState.Done))
                .ToList();
        }
    }

    public void MarkActive(string id) => SetState(id, QueueState.Active);
    public void MarkDone(string id)   => SetState(id, QueueState.Done);
    public void MarkFailed(string id) => SetState(id, QueueState.Failed);

    private void SetState(string id, QueueState s)
    {
        lock (_lock)
        {
            if (_state.ContainsKey(id)) { _state[id] = s; Persist(id); _log($"queue: {id} -> {s}"); }
        }
    }

    public QueueState StateOf(string id)
    {
        lock (_lock) { return _state.TryGetValue(id, out var s) ? s : QueueState.Done; }
    }

    public IReadOnlyList<(WorkUnit unit, QueueState state)> All()
    {
        lock (_lock) { return _units.Values.Select(u => (u, _state[u.Id])).ToList(); }
    }

    /// <summary>
    /// When this unit's persisted record was last written — which, for an Active unit, is
    /// the moment it was dispatched (MarkActive persists). Used as the "since" for the
    /// did-it-ship commit check at settle time. Null when the queue is in-memory only.
    /// </summary>
    public DateTime? PersistedAt(string id)
    {
        if (_dir == null) return null;
        var path = Path.Combine(_dir, id.Replace(':', '_').Replace('/', '_') + ".json");
        return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
    }

    private sealed record Persisted(WorkUnit Unit, QueueState State);

    private void Persist(string id)
    {
        if (_dir == null) return;
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, id.Replace(':', '_').Replace('/', '_') + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(new Persisted(_units[id], _state[id])));
    }

    public void Load()
    {
        if (_dir == null || !Directory.Exists(_dir)) return;
        lock (_lock)
        {
            foreach (var file in Directory.GetFiles(_dir, "*.json"))
            {
                try
                {
                    var p = JsonSerializer.Deserialize<Persisted>(File.ReadAllText(file));
                    if (p?.Unit == null) continue;
                    _units[p.Unit.Id] = p.Unit;
                    _state[p.Unit.Id] = p.State;
                }
                catch (Exception ex) { _log($"queue: skip malformed {Path.GetFileName(file)}: {ex.Message}"); }
            }
        }
    }

    // DFS over the DependsOn graph; returns a cycle path or null.
    private static List<string>? FindCycle(IReadOnlyList<WorkUnit> units)
    {
        var byId = units.ToDictionary(u => u.Id, StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new List<string>();

        List<string>? Dfs(string id)
        {
            if (done.Contains(id)) return null;
            if (!visiting.Add(id)) return new List<string>(stack) { id };
            stack.Add(id);
            if (byId.TryGetValue(id, out var u))
                foreach (var d in u.DependsOn)
                {
                    var c = Dfs(d);
                    if (c != null) return c;
                }
            stack.RemoveAt(stack.Count - 1);
            visiting.Remove(id);
            done.Add(id);
            return null;
        }

        foreach (var u in units)
        {
            var c = Dfs(u.Id);
            if (c != null) return c;
        }
        return null;
    }
}
