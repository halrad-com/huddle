using Huddle;
namespace Huddle.Tests;

public class StatsRenderTests
{
    static readonly DateTimeOffset Now = new(2026, 8, 22, 0, 0, 0, TimeSpan.Zero);

    static RepoStatsSnapshot Snap(bool git = true) => new("myapp", "C:\\r", git,
        new Dictionary<string, string> { ["origin"] = "dev.azure.com/contoso/LIB", ["github"] = "github.com/halrad-com/otherapp" },
        git ? [new Movement(Now.AddHours(-2), "origin", "master", "push", "2bf09c9", "dev.azure.com/contoso/LIB")] : [],
        git ? new CommitStats(14, 6, 812, 203, Now.AddHours(-2), []) : null, 3,
        [new Attribution("myapp:architect", AttributionGrade.Exact, ["cred dev.azure.com 08-21 22:07"]),
         new Attribution("myapp:frontenddev", AttributionGrade.Inferred, ["live at push 2bf09c9"])],
        3, 285.0, TimeSpan.Zero, 2, 41, 3, 1, ["myapp:frontenddev running 96h with no attributable commit"]);

    [Fact]
    public void Repo_block_has_every_section_and_labels_inferred()
    {
        var t = StatsView.RenderRepo(Snap(), Now);
        foreach (var s in new[] { "remotes", "movement", "commits", "churn", "who", "time", "work", "health" }) Assert.Contains(s, t);
        Assert.Contains("dev.azure.com/contoso/LIB (origin)", t);
        Assert.Contains("github.com/halrad-com/otherapp (github)", t);
        Assert.Contains("6 unpushed", t);
        Assert.Contains("exact", t);
        var inferredLine = t.Split('\n').Single(l => l.Contains("myapp:frontenddev") && !l.Contains("running"));
        Assert.Contains("inferred", inferredLine);
    }

    [Fact]
    public void Non_git_repo_is_noted_not_errored()
    {
        var t = StatsView.RenderRepo(Snap(git: false), Now);
        Assert.Contains("not a git repo", t);
        Assert.Contains("who", t);
    }

    [Fact]
    public void Who_pivot_lists_by_session()
    {
        var t = StatsView.RenderWho([Snap()]);
        Assert.Contains("myapp:architect", t);
        Assert.Contains("myapp", t.Split('\n').First(l => l.Contains("myapp:architect")));
    }

    [Theory]
    [InlineData("30d", -30 * 24)] [InlineData("12h", -12)] [InlineData("7", -7 * 24)]
    public void Since_parses_days_and_hours(string tok, int hours)
    {
        Assert.True(StatsView.TryParseSince(tok, Now, out var s));
        Assert.Equal(Now.AddHours(hours), s);
        Assert.False(StatsView.TryParseSince("soon", Now, out _));
    }

    /// <summary>
    /// Spec acceptance #2: no rendered line ever shows an inferred attribution without
    /// the word "inferred". Guards both renderers against a future layout change that
    /// drops the grade column.
    /// </summary>
    [Fact]
    public void No_inferred_name_is_ever_rendered_unlabelled()
    {
        foreach (var text in new[] { StatsView.RenderRepo(Snap(), Now), StatsView.RenderWho([Snap()]) })
            foreach (var line in text.Split('\n').Where(l => l.Contains("myapp:frontenddev") && !l.Contains("running")))
                Assert.Contains("inferred", line);
    }
}
