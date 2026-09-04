using Huddle;
using Xunit;

namespace HuddleTests;

// G1/G3 (wiring-gap backlog): the census that found 22 shipped-but-unwired features,
// frozen into a test so a new orphan is a red build, not a forum post.
public class WiringCensusTests
{
    // ---- Pure rules --------------------------------------------------------------

    private static Dictionary<string, string> Files(params (string name, string body)[] files)
        => files.ToDictionary(f => f.name, f => f.body);

    [Fact]
    public void Key_with_no_reader_is_an_orphan()
    {
        var r = WiringCensus.Run(new[] { "myKnob" }, Files(("A.cs", "nothing here")), Array.Empty<string>());
        Assert.Contains("myKnob", r.Orphans);
    }

    [Fact]
    public void Key_with_a_reader_passes()
    {
        var r = WiringCensus.Run(new[] { "myKnob" },
            Files(("A.cs", "var x = config.Settings.Int(\"myKnob\");")), Array.Empty<string>());
        Assert.Empty(r.Orphans);
    }

    [Fact]
    public void Reader_match_is_case_insensitive()
    {
        // The C5-C7 lesson: consumers may hold the camelCase JSON spelling.
        var r = WiringCensus.Run(new[] { "MyKnob" },
            Files(("page.html", "cfg.myKnob = p.myKnob;")), Array.Empty<string>());
        Assert.Empty(r.Orphans);
    }

    [Fact]
    public void Exempted_key_is_not_an_orphan_but_needs_a_ledger_id()
    {
        var ok = WiringCensus.Run(new[] { "later" }, Files(("A.cs", "x")),
            new[] { "later | phase 2 wires it | T-042 | 2026-08-31" });
        Assert.Empty(ok.Orphans);
        Assert.Empty(ok.BadExemptions);

        var bad = WiringCensus.Run(new[] { "later" }, Files(("A.cs", "x")),
            new[] { "later | phase 2 wires it |  | 2026-08-31" });
        Assert.Empty(bad.Orphans);
        Assert.Contains(bad.BadExemptions, e => e.Contains("later"));
    }

    [Fact]
    public void Exemption_for_unknown_or_wired_key_is_stale()
    {
        var r = WiringCensus.Run(new[] { "wired" },
            Files(("A.cs", "Settings.Int(\"wired\")")),
            new[] { "wired | old reason | T-001 | 2026-01-01", "ghost | gone | T-002 | 2026-01-01" });
        Assert.Contains(r.StaleExemptions, e => e.Contains("wired"));
        Assert.Contains(r.StaleExemptions, e => e.Contains("ghost"));
    }

    [Fact]
    public void Comment_and_blank_exemption_lines_are_ignored()
    {
        var r = WiringCensus.Run(new[] { "k" }, Files(("A.cs", "\"k\"")),
            new[] { "# format: key | reason | ledger-id | date", "", "   " });
        Assert.Empty(r.BadExemptions);
        Assert.Empty(r.StaleExemptions);
    }

    [Fact]
    public void ExemptionLedgerIds_returns_key_id_pairs_skipping_comments()
    {
        var ids = WiringCensus.ExemptionLedgerIds(new[]
        {
            "# header",
            "later | phase 2 | huddle-T012 | 2026-08-31",
            "other | reason |  | 2026-08-31",
        });
        var pair = Assert.Single(ids);
        Assert.Equal(("later", "huddle-T012"), pair);
    }

    // ---- The live gate -----------------------------------------------------------
    // Every SettingsCatalog key must have a reader somewhere outside the settings
    // machinery itself, or an exemption carrying an open ledger id. THIS is the test
    // that fails when someone ships a knob nothing reads.

    [Fact]
    public void Every_setting_in_this_repo_has_a_consumer_or_a_ledgered_exemption()
    {
        var root = FindRepoRoot();
        var report = WiringCensus.RunLive(root);

        Assert.True(report.Orphans.Count == 0,
            "Settings with no reader outside the settings machinery (wire it, or exempt it " +
            "in wiring-exemptions.txt with an open ledger task): " + string.Join(", ", report.Orphans));
        Assert.True(report.BadExemptions.Count == 0,
            "Exemptions without a ledger id: " + string.Join("; ", report.BadExemptions));
        Assert.True(report.StaleExemptions.Count == 0,
            "Stale exemptions (key wired or unknown — delete the line): " + string.Join("; ", report.StaleExemptions));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "Settings.cs")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
