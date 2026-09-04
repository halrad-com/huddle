using System.Text.RegularExpressions;

namespace Huddle;

/// <summary>
/// G1/G3 (wiring gate): mechanical check that every declared setting has a consumer.
/// Born from the 2026-08-31 orphan-surface census — 22 features shipped modeled,
/// validated, documented and unread. A settable knob nothing reads is a promise the
/// product cannot keep; this makes that a red build instead of a user discovery.
///
/// Pure rules in <see cref="Run"/>; <see cref="RunLive"/> binds them to this repo's
/// sources and wiring-exemptions.txt. The census verb and the test suite share both.
/// </summary>
public static class WiringCensus
{
    public sealed record CensusReport(
        IReadOnlyList<string> Orphans,
        IReadOnlyList<string> BadExemptions,
        IReadOnlyList<string> StaleExemptions);

    private sealed record Exemption(string Key, string Reason, string LedgerId, string Raw);

    /// <summary>
    /// Files that ARE the settings machinery: a key's presence there is its
    /// declaration, not a consumer. Everything else in src/ counts as a reader.
    /// </summary>
    public static readonly string[] MachineryFiles = { "Settings.cs", "SettingsCli.cs", "HuddleConfig.cs" };

    public static CensusReport Run(
        IEnumerable<string> keys,
        IReadOnlyDictionary<string, string> fileContents,
        IEnumerable<string> exemptionLines)
    {
        var keyList = keys.ToList();
        var exemptions = new List<Exemption>();
        var badExemptions = new List<string>();

        foreach (var raw in exemptionLines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split('|').Select(p => p.Trim()).ToArray();
            var key = parts.Length > 0 ? parts[0] : "";
            var reason = parts.Length > 1 ? parts[1] : "";
            var ledgerId = parts.Length > 2 ? parts[2] : "";
            exemptions.Add(new Exemption(key, reason, ledgerId, line));
            // An exemption is a deferral, and a deferral without an owner is how
            // transcriptMaxScan rotted: the owning phase shipped and nothing fired.
            if (ledgerId.Length == 0)
                badExemptions.Add($"'{key}' has no ledger task id ({line})");
        }

        // Word-boundary, case-insensitive: catches the camelCase JSON spelling a web
        // client reads (the C5-C7 lesson) without letting 'ipc' match 'IpcManager'.
        bool Wired(string key)
        {
            var rx = new Regex($@"\b{Regex.Escape(key)}\b", RegexOptions.IgnoreCase);
            return fileContents.Values.Any(body => rx.IsMatch(body));
        }

        var wired = keyList.ToDictionary(k => k, Wired, StringComparer.OrdinalIgnoreCase);
        var exemptedKeys = new HashSet<string>(exemptions.Select(e => e.Key), StringComparer.OrdinalIgnoreCase);

        var orphans = keyList
            .Where(k => !wired[k] && !exemptedKeys.Contains(k))
            .ToList();

        var staleExemptions = exemptions
            .Where(e =>
                !keyList.Contains(e.Key, StringComparer.OrdinalIgnoreCase) ||
                wired.TryGetValue(keyList.First(k => string.Equals(k, e.Key, StringComparison.OrdinalIgnoreCase)), out var w) && w)
            .Select(e => keyList.Contains(e.Key, StringComparer.OrdinalIgnoreCase)
                ? $"'{e.Key}' is wired now — delete the exemption"
                : $"'{e.Key}' is not a known setting — delete the exemption")
            .ToList();

        return new CensusReport(orphans, badExemptions, staleExemptions);
    }

    /// <summary>
    /// (key, ledgerId) for every exemption that names a ledger task — the census verb
    /// cross-checks these against the feature ledger's OPEN items, so a deferral whose
    /// owning task closed without wiring the key is reported, not forgotten.
    /// </summary>
    public static IReadOnlyList<(string Key, string LedgerId)> ExemptionLedgerIds(IEnumerable<string> lines)
    {
        var result = new List<(string, string)>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split('|').Select(p => p.Trim()).ToArray();
            if (parts.Length > 2 && parts[2].Length > 0) result.Add((parts[0], parts[2]));
        }
        return result;
    }

    /// <summary>Run the census against this repo: catalog keys vs src/*.cs consumers.</summary>
    public static CensusReport RunLive(string repoRoot)
    {
        var keys = SettingsCatalog.All.Select(d => d.Key);

        var srcDir = Path.Combine(repoRoot, "src");
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.GetFiles(srcDir, "*.cs"))
        {
            var name = Path.GetFileName(path);
            if (MachineryFiles.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                // A machinery file holds DECLARATIONS (SettingDef rows, JsonPropertyName
                // tags) which must not count as consumers — but it may also hold real
                // reads of the resolved value (GetAutoRestartConfig reads
                // Settings.Bool("autoRestart") inside HuddleConfig.cs). Keep only its
                // consumer-shaped lines.
                var consumerLines = File.ReadLines(path)
                    .Where(l => Regex.IsMatch(l, @"Settings\s*\.\s*(Bool|Int|Text|Get)\s*\("));
                files[name] = string.Join('\n', consumerLines);
                continue;
            }
            files[name] = File.ReadAllText(path);
        }

        var exemptionsPath = Path.Combine(repoRoot, "wiring-exemptions.txt");
        var exemptionLines = File.Exists(exemptionsPath)
            ? File.ReadAllLines(exemptionsPath)
            : Array.Empty<string>();

        return Run(keys, files, exemptionLines);
    }
}
