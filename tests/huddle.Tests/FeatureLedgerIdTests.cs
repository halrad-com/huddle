using Huddle;

namespace Huddle.Tests;

public class FeatureLedgerIdTests
{
    [Theory]
    [InlineData("E-001", LedgerType.Epic, 1, null)]
    [InlineData("S-4", LedgerType.Scenario, 4, null)]
    [InlineData("U-011", LedgerType.Story, 11, null)]
    [InlineData("F-014", LedgerType.Feature, 14, null)]
    [InlineData("D-032", LedgerType.Deliverable, 32, null)]
    [InlineData("T-107", LedgerType.Task, 107, null)]
    [InlineData("myapp:F-014", LedgerType.Feature, 14, "myapp")]
    [InlineData("  huddle:E-2 ", LedgerType.Epic, 2, "huddle")]
    public void Parses_bare_and_qualified(string s, LedgerType t, int n, string? repo)
    {
        Assert.True(LedgerId.TryParse(s, out var id));
        Assert.Equal(t, id.Type); Assert.Equal(n, id.Number); Assert.Equal(repo, id.Repo);
    }

    [Theory]
    [InlineData("")] [InlineData("X-001")] [InlineData("F001")] [InlineData("F-")] [InlineData("F-abc")] [InlineData(":F-1")] [InlineData("F-0")]
    public void Rejects_malformed(string s) => Assert.False(LedgerId.TryParse(s, out _));

    [Fact]
    public void Renders_three_digit_padded()
    {
        Assert.Equal("F-014", new LedgerId(LedgerType.Feature, 14, null).ToString());
        Assert.Equal("myapp:T-1234", new LedgerId(LedgerType.Task, 1234, "myapp").ToString());
    }

    [Fact]
    public void Qualify_sets_repo_only_when_bare()
    {
        var bare = new LedgerId(LedgerType.Epic, 1, null);
        Assert.Equal("huddle", bare.Qualify("huddle").Repo);
        var q = new LedgerId(LedgerType.Epic, 1, "otherapp");
        Assert.Equal("otherapp", q.Qualify("huddle").Repo);
    }

    [Fact]
    public void Ids_compare_by_value_case_insensitively_on_repo()
    {
        Assert.True(LedgerId.TryParse("Huddle:F-1", out var a));
        Assert.True(LedgerId.TryParse("huddle:F-001", out var b));
        Assert.Equal(a, b);
    }
}
