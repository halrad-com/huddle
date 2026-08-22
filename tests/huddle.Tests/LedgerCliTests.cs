using Huddle;
using Xunit;

namespace HuddleTests;

// The ledger records; it does not adjudicate. A claim is ALWAYS written, and any
// other session already holding one of its files is reported so the two agents can
// take turns by talking. This is the behaviour that survives huddle being down.
public class LedgerCliTests : IDisposable
{
    private readonly string _dir;
    private readonly WorkLedgerClaims _claims;

    public LedgerCliTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "huddle-ledgercli-" + Guid.NewGuid().ToString("N"));
        _claims = new WorkLedgerClaims(_dir, _ => { });
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static readonly DateTime T0 = new(2026, 8, 16, 8, 0, 0, DateTimeKind.Utc);

    private static WorkLedgerClaim Make(string session, string batch, params string[] files) =>
        new(session, "myapp", batch, T0, "", files);

    [Fact]
    public void Claim_IsWritten_EvenWhenAnotherSessionHoldsTheFile()
    {
        _claims.Write(Make("myapp:architect", "A-1", "deploy/docs.html"));

        var result = LedgerCli.Claim(_claims, Make("myapp:frontenddev", "A-2", "deploy/docs.html"));

        Assert.True(File.Exists(result.ClaimPath));
        Assert.Equal(2, _claims.ReadAll().Count);
    }

    [Fact]
    public void Claim_ReportsTheOtherHolderAndTheSharedFile()
    {
        _claims.Write(Make("myapp:architect", "A-1", "deploy/docs.html", "src/other.cs"));

        var result = LedgerCli.Claim(_claims, Make("myapp:frontenddev", "A-2", "deploy/docs.html"));

        var overlap = Assert.Single(result.Overlaps);
        Assert.Equal("myapp:architect", overlap.B.SessionId);
        Assert.Equal(new[] { "deploy/docs.html" }, overlap.SharedFiles);
    }

    [Fact]
    public void Claim_WithNoOverlap_ReportsNothing()
    {
        _claims.Write(Make("myapp:architect", "A-1", "src/a.cs"));

        var result = LedgerCli.Claim(_claims, Make("myapp:frontenddev", "A-2", "src/b.cs"));

        Assert.Empty(result.Overlaps);
    }

    [Fact]
    public void Claim_DoesNotReportTheClaimantAsItsOwnConflict()
    {
        _claims.Write(Make("myapp:architect", "A-1", "src/a.cs"));

        var result = LedgerCli.Claim(_claims, Make("myapp:architect", "A-2", "src/a.cs"));

        Assert.Empty(result.Overlaps);
    }

    [Fact]
    public void Release_RemovesOnlyTheCallersFiles()
    {
        _claims.Write(Make("myapp:architect", "A-1", "src/a.cs"));
        _claims.Write(Make("myapp:frontenddev", "A-2", "src/a.cs"));

        var released = LedgerCli.Release(_claims, "myapp:frontenddev", new[] { "src/a.cs" });

        Assert.Equal(1, released);
        var remaining = Assert.Single(_claims.ReadAll());
        Assert.Equal("myapp:architect", remaining.SessionId);
    }

    [Fact]
    public void Describe_NamesTheHolderOfEachFile()
    {
        _claims.Write(Make("myapp:architect", "A-1", "deploy/docs.html"));

        var text = LedgerCli.Describe(_claims.ReadAll(), repoFilter: null);

        Assert.Contains("deploy/docs.html", text);
        Assert.Contains("myapp:architect", text);
    }

    [Fact]
    public void Describe_FiltersByRepo()
    {
        _claims.Write(new WorkLedgerClaim("huddle:architect", "huddle", "A-1", T0, "", new[] { "README.md" }));
        _claims.Write(Make("myapp:architect", "A-2", "deploy/docs.html"));

        var text = LedgerCli.Describe(_claims.ReadAll(), repoFilter: "huddle");

        Assert.Contains("README.md", text);
        Assert.DoesNotContain("deploy/docs.html", text);
    }

    [Fact]
    public void Describe_ShowsUnstampedRepoClaims_WhateverTheFilter()
    {
        // A claim with no repo recorded collides with EVERY repo (ReposCollide), so Claim
        // WOULD report it as an overlap. The read-before-you-work view must not hide the
        // very claim the conflict engine says will bite.
        _claims.Write(new WorkLedgerClaim("old:session", "", "A-legacy", T0, "", new[] { "src/a.cs" }));

        var text = LedgerCli.Describe(_claims.ReadAll(), repoFilter: "huddle");

        Assert.Contains("src/a.cs", text);
        // ...and with no repo to qualify it, the path is rendered bare, not as "/src/a.cs",
        // which would read as an absolute path.
        Assert.DoesNotContain("/src/a.cs", text);
    }

    [Fact]
    public void Describe_RendersTheClaimTimeAsUtc()
    {
        // The format appends a literal "Z". An in-memory claim can carry a Local-kind
        // DateTime (only claims round-tripped through disk are normalized to UTC), and
        // printing local time labelled UTC would misreport how long a file has been held.
        var claim = new WorkLedgerClaim("myapp:architect", "myapp", "A-1",
            T0.ToLocalTime(), "", new[] { "src/a.cs" });

        var text = LedgerCli.Describe(new[] { claim }, repoFilter: null);

        Assert.Contains("2026-08-16 08:00Z", text);
    }

    // ---- Record-and-report is one critical section ----
    // ReadAll and Write each take the ledger lock separately, so composing them in the
    // caller lets two claimants both read a clean ledger before either writes — and both
    // get told there is no conflict, which is the exact outcome this feature exists to
    // prevent. RecordWithOverlaps does the read, the compare and the write as one unit.

    [Fact]
    public void RecordWithOverlaps_WritesTheClaimAndReportsTheOtherHolder()
    {
        _claims.Write(Make("myapp:architect", "A-1", "deploy/docs.html"));

        var path = _claims.RecordWithOverlaps(
            Make("myapp:frontenddev", "A-2", "deploy/docs.html"), out var overlaps);

        Assert.True(File.Exists(path));
        Assert.Equal(2, _claims.ReadAll().Count);
        Assert.Equal("myapp:architect", Assert.Single(overlaps).B.SessionId);
    }

    [Fact]
    public async Task ConcurrentRecords_AllLand_AndOnlyTheFirstSeesACleanLedger()
    {
        var clean = 0;
        var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            _claims.RecordWithOverlaps(Make($"myapp:worker-{i}", $"A-{i}", "docs/plan.md"), out var overlaps);
            if (overlaps.Count == 0) Interlocked.Increment(ref clean);
        })).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(8, _claims.ReadAll().Count); // nothing was refused
        Assert.Equal(1, clean);                   // exactly one claimant found the ledger empty
    }
}
