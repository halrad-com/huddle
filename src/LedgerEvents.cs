using System.Text.Json;
using System.Text.Json.Serialization;

namespace Huddle;

/// <summary>One line of docs/ledger/events*.jsonl (spec §2.2). Append-only, never rewritten.</summary>
public sealed record LedgerEvent(
    [property: JsonPropertyName("ts")] DateTimeOffset Ts,
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("actor")] string? Actor = null,
    [property: JsonPropertyName("owner")] string? Owner = null,
    [property: JsonPropertyName("parent")] string? Parent = null,
    [property: JsonPropertyName("pri")] string? Pri = null,
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("refs")] IReadOnlyList<string>? Refs = null,
    [property: JsonPropertyName("from")] string? From = null,
    [property: JsonPropertyName("to")] string? To = null,
    [property: JsonPropertyName("note")] string? Note = null,
    [property: JsonPropertyName("ungated")] bool Ungated = false);

public static class LedgerEventReader
{
    static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Every events*.jsonl in ordinal name order (rotation: events-YYYYMMDD.jsonl
    /// sort before events.jsonl, which is the live tail). Missing dir = empty.</summary>
    public static IReadOnlyList<LedgerEvent> ReadAll(string ledgerDir, List<string> problems)
    {
        if (!Directory.Exists(ledgerDir)) return Array.Empty<LedgerEvent>();
        var files = Directory.GetFiles(ledgerDir, "events*.jsonl").OrderBy(f => f, StringComparer.Ordinal).ToList();
        // ensure the live file is read last
        var live = files.FirstOrDefault(f => Path.GetFileName(f).Equals("events.jsonl", StringComparison.OrdinalIgnoreCase));
        if (live != null) { files.Remove(live); files.Add(live); }
        var all = new List<LedgerEvent>();
        foreach (var f in files)
            all.AddRange(ParseLines(File.ReadLines(f), Path.GetFileName(f), problems));
        return all;
    }

    public static IReadOnlyList<LedgerEvent> ParseLines(IEnumerable<string> lines, string fileLabel, List<string> problems)
    {
        var list = new List<LedgerEvent>();
        int n = 0;
        foreach (var line in lines)
        {
            n++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var ev = JsonSerializer.Deserialize<LedgerEvent>(line, Opts);
                if (ev is null || string.IsNullOrEmpty(ev.Event) || string.IsNullOrEmpty(ev.Id))
                { problems.Add($"{fileLabel}:{n}: missing event or id"); continue; }
                list.Add(ev);
            }
            catch (JsonException ex) { problems.Add($"{fileLabel}:{n}: {ex.Message}"); }
        }
        return list;
    }
}

public sealed record LedgerTask(
    LedgerId Id, string Title, string State, string? Owner, string? Actor, LedgerId? Parent, string? Pri,
    IReadOnlyList<string> Refs, DateTimeOffset AssignedAt, DateTimeOffset LastAt, string? LastNote, bool Ungated);

/// <summary>Tasks never appear in ledger.md; their current row is the replay of their events.</summary>
public static class TaskMaterializer
{
    static string? StateOf(string ev) => ev switch
    {
        "task-assigned" => "assigned", "task-acked" => "acked", "task-progress" => "in-progress",
        "task-delivered" => "delivered", "task-accepted" => "accepted", "task-declined" => "declined",
        "task-abandoned" => "abandoned", _ => null
    };

    /// <summary>
    /// Keyed on the PARSED, bare-normalised <see cref="LedgerId"/> — never the raw
    /// string. `T-7`, `T-007` and `huddle:T-007` are one task; keying on the text
    /// made them three, so an ack could silently open a second task and leave the
    /// first hanging in `assigned` forever (L3).
    /// </summary>
    public static IReadOnlyList<LedgerTask> Materialize(IEnumerable<LedgerEvent> events, List<string> problems)
    {
        var byId = new Dictionary<LedgerId, LedgerTask>();
        foreach (var ev in events.Where(e => e.Event.StartsWith("task-", StringComparison.Ordinal)).OrderBy(e => e.Ts))
        {
            var to = StateOf(ev.Event);
            if (to is null) { problems.Add($"{ev.Id}: unknown event {ev.Event}"); continue; }
            if (!LedgerId.TryParse(ev.Id, out var parsed) || parsed.Type != LedgerType.Task)
            { problems.Add($"{ev.Id}: not a task id"); continue; }
            // Events live in one repo's log, so a repo qualifier on a task id is
            // decoration: strip it before keying so it cannot fork the identity.
            var key = parsed with { Repo = null };

            if (ev.Event == "task-assigned")
            {
                if (byId.ContainsKey(key)) { problems.Add($"{key}: assigned twice"); continue; }
                LedgerId? parent = null;
                if (!string.IsNullOrEmpty(ev.Parent) && LedgerId.TryParse(ev.Parent, out var pid)) parent = pid;
                byId[key] = new LedgerTask(key, ev.Title ?? "", "assigned", ev.Owner, ev.Actor, parent, ev.Pri,
                    ev.Refs ?? Array.Empty<string>(), ev.Ts, ev.Ts, ev.Note, false);
                continue;
            }

            if (!byId.TryGetValue(key, out var t))
            { problems.Add($"{key}: {ev.Event} before any task-assigned"); continue; }
            if (!LedgerStateMachine.CanTransitionTask(t.State, to))
            { problems.Add($"{key}: illegal transition {t.State} -> {to} at {ev.Ts:O}"); continue; }
            byId[key] = t with { State = to, LastAt = ev.Ts, LastNote = ev.Note ?? t.LastNote, Ungated = t.Ungated || ev.Ungated };
        }
        return byId.Values.OrderBy(t => t.AssignedAt).ToList();
    }
}
