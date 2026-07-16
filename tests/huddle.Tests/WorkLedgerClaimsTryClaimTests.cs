using Huddle;
using Xunit;

namespace HuddleTests;

// TryClaim is the arbiter that closes the same-plan-parallel-execution gap: check-and-write is atomic,
// so two sessions can never both win the same file (or the same plan doc).
public class WorkLedgerClaimsTryClaimTests : IDisposable
{
    private readonly string _dir;
    private readonly WorkLedgerClaims _claims;

    public WorkLedgerClaimsTryClaimTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "huddle-tryclaim-" + Guid.NewGuid().ToString("N"));
        _claims = new WorkLedgerClaims(_dir, _ => { });
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static WorkLedgerClaim Make(string session, string id, params string[] files) =>
        new(session, "repo1", id, DateTime.UtcNow, "abc123", files);

    [Fact]
    public void FirstClaimIsGranted()
    {
        var ok = _claims.TryClaim(Make("repo1:architect", "R-1", "src/a.cs"), out var conflicts);

        Assert.True(ok);
        Assert.Empty(conflicts);
        Assert.Single(_claims.ReadAll());
    }

    [Fact]
    public void SecondSessionSameFileIsRejectedAndNothingWritten()
    {
        Assert.True(_claims.TryClaim(Make("repo1:architect", "R-1", "src/a.cs", "docs/plan.md"), out _));

        var ok = _claims.TryClaim(Make("repo1:architect-2", "R-2", "docs/plan.md"), out var conflicts);

        Assert.False(ok);
        var overlap = Assert.Single(conflicts);
        Assert.Equal("repo1:architect", overlap.B.SessionId);
        Assert.Equal("docs/plan.md", Assert.Single(overlap.SharedFiles));
        Assert.Single(_claims.ReadAll()); // loser's claim was not written
    }

    [Fact]
    public void SameSessionMayExtendItsOwnScope()
    {
        Assert.True(_claims.TryClaim(Make("repo1:architect", "R-1", "src/a.cs"), out _));

        var ok = _claims.TryClaim(Make("repo1:architect", "R-2", "src/a.cs", "src/b.cs"), out var conflicts);

        Assert.True(ok);
        Assert.Empty(conflicts);
    }

    [Fact]
    public void ReleasedFileCanBeReclaimedByAnotherSession()
    {
        Assert.True(_claims.TryClaim(Make("repo1:architect", "R-1", "src/a.cs"), out _));
        Assert.Equal(1, _claims.Release("repo1:architect", new[] { "src/a.cs" }));

        var ok = _claims.TryClaim(Make("repo1:architect-2", "R-2", "src/a.cs"), out var conflicts);

        Assert.True(ok);
        Assert.Empty(conflicts);
    }

    [Fact]
    public void FileComparisonIsCaseInsensitive()
    {
        Assert.True(_claims.TryClaim(Make("repo1:architect", "R-1", "SRC/A.cs"), out _));

        var ok = _claims.TryClaim(Make("repo1:architect-2", "R-2", "src/a.cs"), out var conflicts);

        Assert.False(ok);
        Assert.Single(conflicts);
    }

    [Fact]
    public async Task ConcurrentClaimantsExactlyOneWins()
    {
        var wins = 0;
        var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            if (_claims.TryClaim(Make($"repo1:worker-{i}", $"R-{i}", "docs/plan.md"), out _))
                Interlocked.Increment(ref wins);
        })).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(1, wins);
        Assert.Single(_claims.ReadAll());
    }
}
