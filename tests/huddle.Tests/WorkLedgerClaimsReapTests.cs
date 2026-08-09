using Huddle;
using Xunit;

namespace HuddleTests;

// Orphan reaping closes the recurring "undeletable claim" gap: a claim whose owning
// SESSION INSTANCE is no longer alive must be archived, even when a DIFFERENT instance
// has since reused the same name. Identity is the conversation GUID; a legacy claim with
// no GUID falls back to name + start-time (an instance that started after the claim is a
// different instance reusing the name).
public class WorkLedgerClaimsReapTests : IDisposable
{
    private readonly string _dir;
    private readonly WorkLedgerClaims _claims;

    public WorkLedgerClaimsReapTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "huddle-reap-" + Guid.NewGuid().ToString("N"));
        _claims = new WorkLedgerClaims(_dir, _ => { });
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static WorkLedgerClaim Make(string session, string id, DateTime claimedAt, string ownerGuid, params string[] files) =>
        new(session, "repo1", id, claimedAt, "abc123", files, ownerGuid);

    private static readonly DateTime T0 = new(2026, 8, 5, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void GuidMatchKeepsClaim()
    {
        var g = Guid.NewGuid();
        var claim = Make("repo1:architect", "R-1", T0, g.ToString(), "src/a.cs");
        var live = new List<WorkLedgerClaims.LiveInstance>
        {
            new("repo1:architect", g, T0.AddHours(-1))
        };

        Assert.False(WorkLedgerClaims.IsOrphan(claim, live));
    }

    [Fact]
    public void GuidNoMatchIsOrphanEvenWhenNameIsAliveAgain()
    {
        // Same name, different conversation GUID = a recycled name, not the owner.
        var owner = Guid.NewGuid();
        var recycled = Guid.NewGuid();
        var claim = Make("repo1:architect", "R-1", T0, owner.ToString(), "src/a.cs");
        var live = new List<WorkLedgerClaims.LiveInstance>
        {
            new("repo1:architect", recycled, T0.AddHours(10))
        };

        Assert.True(WorkLedgerClaims.IsOrphan(claim, live));
    }

    [Fact]
    public void LegacyClaimIsOrphanWhenLiveInstanceStartedAfterIt()
    {
        // No GUID (older claim). The live same-named instance started AFTER the claim,
        // so it cannot be the claim's owner. Underscore vs colon form must still match.
        var claim = Make("repo1_architect", "R-1", T0, "", "src/a.cs");
        var live = new List<WorkLedgerClaims.LiveInstance>
        {
            new("repo1:architect", Guid.NewGuid(), T0.AddHours(10))
        };

        Assert.True(WorkLedgerClaims.IsOrphan(claim, live));
    }

    [Fact]
    public void LegacyClaimIsKeptWhenLiveInstanceStartedBeforeIt()
    {
        var claim = Make("repo1:architect", "R-1", T0.AddHours(2), "", "src/a.cs");
        var live = new List<WorkLedgerClaims.LiveInstance>
        {
            new("repo1:architect", Guid.NewGuid(), T0.AddHours(1))
        };

        Assert.False(WorkLedgerClaims.IsOrphan(claim, live));
    }

    [Fact]
    public void ReapArchivesOrphansAndKeepsLiveClaims()
    {
        var liveGuid = Guid.NewGuid();
        _claims.Write(Make("repo1:architect", "R-live", T0.AddHours(3), liveGuid.ToString(), "src/live.cs"));
        _claims.Write(Make("repo1:architect", "R-orphan-guid", T0, Guid.NewGuid().ToString(), "src/dead1.cs"));
        _claims.Write(Make("repo1_architect", "R-orphan-legacy", T0, "", "src/dead2.cs"));

        Assert.Equal(3, _claims.ReadAll().Count);

        var live = new List<WorkLedgerClaims.LiveInstance>
        {
            new("repo1:architect", liveGuid, T0.AddHours(2))
        };

        var reaped = _claims.ReapOrphans(live);

        Assert.Equal(2, reaped.Count);
        var remaining = _claims.ReadAll();
        Assert.Single(remaining);
        Assert.Equal("R-live", remaining[0].BatchId);
    }

    [Fact]
    public void ReapedClaimsAreArchivedNotDeleted()
    {
        _claims.Write(Make("repo1_architect", "R-orphan", T0, "", "src/dead.cs"));

        var reaped = _claims.ReapOrphans(new List<WorkLedgerClaims.LiveInstance>());

        Assert.Single(reaped);
        Assert.Empty(_claims.ReadAll()); // gone from the active (top-level) set

        // Still present on disk somewhere under the claims dir (reversible archive).
        var archived = Directory.GetFiles(_dir, "*.md", SearchOption.AllDirectories);
        Assert.Single(archived);
        Assert.Contains("archived-orphan", archived[0]);
    }

    [Fact]
    public void OwnerGuidRoundTripsThroughFile()
    {
        var g = Guid.NewGuid();
        _claims.Write(Make("repo1:architect", "R-1", T0, g.ToString(), "src/a.cs"));

        var read = Assert.Single(_claims.ReadAll());
        Assert.Equal(g.ToString(), read.OwnerGuid);
    }
}
