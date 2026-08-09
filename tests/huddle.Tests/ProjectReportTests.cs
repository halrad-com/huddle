using Huddle;
using Xunit;

namespace HuddleTests;

// The projects HTML report is the reproducible output demo: pure render over
// gathered data — same inputs, same page. Content must be escaped (agent/operator
// text can contain anything) and every section must actually appear.
public class ProjectReportTests
{
    private static ProjectInfo Info(string slug = "oracle") => new(
        Slug: slug, Title: "The Oracle", Goal: "Answers <always>", Status: "active",
        Repos: new[] { "huddle" }, HomeRepo: "huddle", Dir: @"C:\repo\docs\projects\oracle",
        SprintId: "2608-1", SprintVersion: null,
        TypedArtifacts: new[] { "ROADMAP.md", "SPRINT.md" },
        MapNotes: null, MapLinks: Array.Empty<string>(), MapOnly: false, Warning: null);

    [Fact]
    public void Render_ContainsProject_Sprint_Artifacts_AndSuspects()
    {
        var entry = new ProjectReportEntry(
            Info(),
            new[]
            {
                new ProjectAgent("huddle:architect", "architect", "recover verb build", "live", DateTime.Now),
                new ProjectAgent("app:architect-3", "architect", "TASK B", "past", DateTime.Now.AddHours(-3)),
            },
            new[]
            {
                new WorkLedgerClaim("huddle:architect", "huddle", "R-1", DateTime.UtcNow,
                    new string('a', 40), new[] { "src/x.cs" }, "", "oracle")
            });

        var html = ProjectReport.Render(new[] { entry }, "test");

        Assert.Contains("oracle", html);
        Assert.Contains("The Oracle", html);
        Assert.Contains("sprint 2608-1", html);
        Assert.Contains("ROADMAP.md", html);
        Assert.Contains("Usual suspects", html);
        Assert.Contains("huddle:architect", html);
        Assert.Contains("recover verb build", html);
        Assert.Contains("Open claims", html);
        // Escaping: the goal's angle brackets must not survive as markup.
        Assert.Contains("Answers &lt;always&gt;", html);
        Assert.DoesNotContain("<always>", html);
    }

    [Fact]
    public void Render_Empty_SaysSo()
    {
        var html = ProjectReport.Render(Array.Empty<ProjectReportEntry>(), "test");
        Assert.Contains("No projects discovered", html);
    }

    [Fact]
    public void Render_IsSelfContained_NoExternalRefs()
    {
        var html = ProjectReport.Render(new[] { new ProjectReportEntry(
            Info(), Array.Empty<ProjectAgent>(), Array.Empty<WorkLedgerClaim>()) }, "test");
        Assert.DoesNotContain("http://", html.Replace("file:///", ""));
        Assert.DoesNotContain("https://", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }
}
