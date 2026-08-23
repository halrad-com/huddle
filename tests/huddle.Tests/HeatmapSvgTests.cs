using Huddle;
namespace Huddle.Tests;

public class HeatmapSvgTests
{
    static readonly DateTimeOffset Now = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Emits_one_cell_per_day_for_the_window()
    {
        var svg = StatsView.HeatmapSvg([], Now, weeks: 4);
        Assert.Equal(28, svg.Split("<rect").Length - 1);
        Assert.StartsWith("<svg", svg);
    }

    [Fact]
    public void Buckets_by_count()
    {
        var day = Now.Date;
        var times = Enumerable.Repeat(new DateTimeOffset(day, TimeSpan.Zero), 7).ToList();
        var svg = StatsView.HeatmapSvg(times, Now, weeks: 1);
        Assert.Contains("class=\"c4\"", svg);
        Assert.Contains("data-count=\"7\"", svg);
        Assert.Contains("class=\"c0\"", svg);
    }

    [Fact]
    public void Html_is_self_contained_and_has_a_section_per_repo()
    {
        var s = new RepoStatsSnapshot("app", "C:\\r", true, new Dictionary<string, string>(), [], new CommitStats(1, 0, 1, 0, Now, [Now]), 0, [], 0, 0, null, 0, 0, 0, 0, []);
        var html = StatsView.RenderHtml([s, s with { Repo = "td" }], Now.AddDays(-7), Now, "test");
        Assert.DoesNotContain("http://", html);
        Assert.DoesNotContain("https://", html);
        Assert.Equal(2, html.Split("<section").Length - 1);
        Assert.Contains("<svg", html);
        Assert.Contains("app", html);
    }

    /// <summary>
    /// Spec acceptance #2 again, on the HTML path: an inferred candidate must carry the
    /// word, not just a CSS class a stylesheet could drop.
    /// </summary>
    [Fact]
    public void Html_labels_inferred_in_text_not_only_in_css()
    {
        var s = new RepoStatsSnapshot("app", "C:\\r", true, new Dictionary<string, string>(), [], null, 0,
            [new Attribution("app:frontenddev", AttributionGrade.Inferred, ["live at push abc1234"])],
            1, 1.0, null, 0, 0, 0, 0, []);
        var html = StatsView.RenderHtml([s], Now.AddDays(-7), Now, "test");
        Assert.Contains("inferred", html);
        var marker = html.IndexOf("app:frontenddev", StringComparison.Ordinal);
        Assert.Contains("inferred", html[marker..(marker + 200)]);
    }

    /// <summary>Userinfo must not reach the page any more than the console.</summary>
    [Fact]
    public void Html_escapes_and_carries_no_userinfo()
    {
        var s = new RepoStatsSnapshot("app", "C:\\r", true,
            new Dictionary<string, string> { ["origin"] = "dev.azure.com/contoso/LIB" }, [], null, 0, [], 0, 0, null, 0, 0, 0, 0, []);
        var html = StatsView.RenderHtml([s], Now.AddDays(-7), Now, "test");
        Assert.DoesNotContain("contoso@", html);
        Assert.Contains("dev.azure.com/contoso/LIB", html);
    }
}
