using System.Globalization;

namespace Huddle;

public enum LedgerType { Epic, Scenario, Story, Feature, Deliverable, Task }

/// <summary>
/// `&lt;TYPE&gt;-&lt;n&gt;`, optionally `repo:` qualified. Numbering is per type per repo and never
/// reused. Repo compares case-insensitively so "Huddle:F-1" and "huddle:F-001" are one id.
/// </summary>
public readonly record struct LedgerId(LedgerType Type, int Number, string? Repo)
{
    public string? Repo { get; init; } = Repo?.ToLowerInvariant();

    public static char Prefix(LedgerType t) => t switch
    {
        LedgerType.Epic => 'E', LedgerType.Scenario => 'S', LedgerType.Story => 'U',
        LedgerType.Feature => 'F', LedgerType.Deliverable => 'D', _ => 'T'
    };

    public static bool TryType(char c, out LedgerType t)
    {
        switch (char.ToUpperInvariant(c))
        {
            case 'E': t = LedgerType.Epic; return true;
            case 'S': t = LedgerType.Scenario; return true;
            case 'U': t = LedgerType.Story; return true;
            case 'F': t = LedgerType.Feature; return true;
            case 'D': t = LedgerType.Deliverable; return true;
            case 'T': t = LedgerType.Task; return true;
            default: t = default; return false;
        }
    }

    public static bool TryParse(string? s, out LedgerId id)
    {
        id = default;
        s = (s ?? "").Trim();
        if (s.Length == 0) return false;
        string? repo = null;
        var colon = s.IndexOf(':');
        if (colon >= 0)
        {
            repo = s[..colon].Trim();
            if (repo.Length == 0) return false;
            s = s[(colon + 1)..].Trim();
        }
        if (s.Length < 3 || s[1] != '-') return false;
        if (!TryType(s[0], out var t)) return false;
        if (!int.TryParse(s[2..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n <= 0) return false;
        id = new LedgerId(t, n, repo);
        return true;
    }

    public LedgerId Qualify(string repo) => Repo is null ? this with { Repo = repo } : this;

    public override string ToString() =>
        (Repo is null ? "" : Repo + ":") + Prefix(Type) + "-" + Number.ToString("000", CultureInfo.InvariantCulture);
}

public sealed record LedgerRow(
    LedgerId Id, LedgerType Type, LedgerId? Parent, string Title, string State,
    string? Pri, string? Owner, string? Accepts, IReadOnlyList<string> Refs, int Line);

public sealed record LedgerRowError(int Line, string Raw, string Reason);

public sealed class LedgerParseResult
{
    public Dictionary<string, string> Frontmatter { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<LedgerRow> Rows { get; } = new();
    public List<LedgerRowError> Errors { get; } = new();
}

/// <summary>
/// Parses docs/ledger/ledger.md. The first pipe table is the ledger; everything else is
/// prose and ignored. An unparseable row is REPORTED, never dropped — silent loss is the
/// failure this whole design exists to prevent.
/// </summary>
public static class FeatureLedgerParser
{
    static readonly string[] Required = { "ID", "Type", "Parent", "Title", "State", "Pri", "Owner", "Accepts", "Refs" };

    public static LedgerParseResult Parse(string text)
    {
        var r = new LedgerParseResult();
        foreach (var kv in ProjectMap.ParseFrontmatter(text)) r.Frontmatter[kv.Key] = kv.Value;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        int i = 0;
        // find header: first line starting with '|' whose next line is a separator row
        for (; i < lines.Length - 1; i++)
            if (lines[i].TrimStart().StartsWith('|') && IsSeparator(lines[i + 1])) break;
        if (i >= lines.Length - 1) return r;

        var header = Cells(lines[i]);
        var col = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int c = 0; c < header.Count; c++) col[header[c]] = c;
        var missing = Required.Where(h => !col.ContainsKey(h)).ToList();
        if (missing.Count > 0)
        {
            r.Errors.Add(new LedgerRowError(i + 1, lines[i], $"header missing column(s): {string.Join(", ", missing)}"));
            return r;
        }

        var seen = new HashSet<LedgerId>();
        for (int ln = i + 2; ln < lines.Length; ln++)
        {
            var raw = lines[ln];
            if (!raw.TrimStart().StartsWith('|')) break; // table ended
            var cells = Cells(raw);
            string Cell(string name) => col[name] < cells.Count ? cells[col[name]].Trim() : "";
            int lineNo = ln + 1;

            if (!LedgerId.TryParse(Cell("ID"), out var id) || id.Repo != null)
            { r.Errors.Add(new LedgerRowError(lineNo, raw, $"bad id \"{Cell("ID")}\" (expect E-/S-/U-/F-/D- and a number, unqualified)")); continue; }
            if (id.Type == LedgerType.Task)
            { r.Errors.Add(new LedgerRowError(lineNo, raw, "tasks live in events.jsonl, not ledger.md")); continue; }
            if (!TryTypeName(Cell("Type"), out var type) || type != id.Type)
            { r.Errors.Add(new LedgerRowError(lineNo, raw, $"type \"{Cell("Type")}\" does not match id {id}")); continue; }
            LedgerId? parent = null;
            var p = Cell("Parent");
            if (p.Length > 0)
            {
                if (!LedgerId.TryParse(p, out var pid))
                { r.Errors.Add(new LedgerRowError(lineNo, raw, $"bad parent \"{p}\"")); continue; }
                parent = pid;
            }
            var state = Cell("State").ToLowerInvariant();
            if (!LedgerStateMachine.IsHierarchyState(state))
            { r.Errors.Add(new LedgerRowError(lineNo, raw, $"bad state \"{Cell("State")}\"")); continue; }
            var title = Cell("Title");
            if (title.Length == 0)
            { r.Errors.Add(new LedgerRowError(lineNo, raw, "title is empty")); continue; }
            if (!seen.Add(id))
            { r.Errors.Add(new LedgerRowError(lineNo, raw, $"duplicate id {id}")); continue; }

            var refs = Cell("Refs").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            r.Rows.Add(new LedgerRow(id, type, parent, title, state,
                Null(Cell("Pri")), Null(Cell("Owner")), Null(Cell("Accepts")), refs, lineNo));
        }
        return r;
    }

    static string? Null(string s) => s.Length == 0 ? null : s;
    static bool IsSeparator(string line) => line.Trim().Length > 0 && line.Trim().All(c => c is '|' or '-' or ':' or ' ');

    static List<string> Cells(string line)
    {
        var t = line.Trim();
        if (t.StartsWith('|')) t = t[1..];
        if (t.EndsWith('|')) t = t[..^1];
        return t.Split('|').Select(c => c.Trim()).ToList();
    }

    public static bool TryTypeName(string s, out LedgerType t)
    {
        switch (s.Trim().ToLowerInvariant())
        {
            case "epic": t = LedgerType.Epic; return true;
            case "scenario": t = LedgerType.Scenario; return true;
            case "story": t = LedgerType.Story; return true;
            case "feature": t = LedgerType.Feature; return true;
            case "deliverable": t = LedgerType.Deliverable; return true;
            case "task": t = LedgerType.Task; return true;
            default: t = default; return false;
        }
    }
}
