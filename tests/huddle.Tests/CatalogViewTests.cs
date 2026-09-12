using Huddle;
using Xunit;

namespace HuddleTests;

// The circulation list is rendered once, for both `huddle --catalog` and the console's `catalog`
// verb. These tests pin that one renderer rather than two surfaces that could drift apart.
public class CatalogViewTests
{
    private static readonly DateTime Now = new(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc);

    private static CatalogEntry E(string repo, string path, string borrower, int dueInMinutes) =>
        new(repo, path, borrower, Now.AddMinutes(-10), Now.AddMinutes(dueInMinutes));

    [Fact]
    public void Filter_by_repo_is_case_insensitive()
    {
        var shown = CatalogView.Filter(new[]
        {
            E("huddle", "src/One.cs", "codex:refactor", 30),
            E("netlib", "src/Two.cs", "codex:refactor", 30),
        }, Now, repo: "HUDDLE");

        Assert.Equal("src/One.cs", Assert.Single(shown).RelPath);
    }

    [Fact]
    public void Filter_overdue_only_keeps_late_entries()
    {
        var shown = CatalogView.Filter(new[]
        {
            E("huddle", "src/Late.cs", "codex:refactor", -5),
            E("huddle", "src/OnTime.cs", "codex:refactor", 30),
        }, Now, overdueOnly: true);

        Assert.Equal("src/Late.cs", Assert.Single(shown).RelPath);
    }

    // IsOverdue is DueAt <= now: a lease that expires this instant is already reclaimable, so
    // the view must agree with the checkout logic about that boundary.
    [Fact]
    public void Due_exactly_now_counts_as_overdue()
    {
        var shown = CatalogView.Filter(new[] { E("huddle", "src/Edge.cs", "codex:refactor", 0) },
                                       Now, overdueOnly: true);

        Assert.Single(shown);
    }

    [Fact]
    public void Filter_by_borrower_matches_either_spelling()
    {
        var shown = CatalogView.Filter(new[]
        {
            E("huddle", "src/One.cs", "huddle:architect", 30),
            E("huddle", "src/Two.cs", "codex:refactor", 30),
        }, Now, borrower: "huddle_architect");

        Assert.Equal("src/One.cs", Assert.Single(shown).RelPath);
    }

    [Fact]
    public void Filter_orders_by_repo_then_path()
    {
        var shown = CatalogView.Filter(new[]
        {
            E("huddle", "src/b.cs", "x", 30),
            E("archive", "src/z.cs", "x", 30),
            E("huddle", "src/a.cs", "x", 30),
        }, Now);

        Assert.Equal(new[] { "archive/src/z.cs", "huddle/src/a.cs", "huddle/src/b.cs" },
                     shown.Select(e => $"{e.Repo}/{e.RelPath}"));
    }

    [Fact]
    public void Line_keeps_the_shape_the_cli_has_always_printed()
    {
        Assert.Equal("huddle/src/One.cs - codex:refactor, due 2026-09-12 12:30Z",
                     CatalogView.Line(E("huddle", "src/One.cs", "codex:refactor", 30), Now));
    }

    [Fact]
    public void Line_marks_an_overdue_entry_as_reclaimable()
    {
        Assert.EndsWith(" (OVERDUE - reclaimable)",
                        CatalogView.Line(E("huddle", "src/One.cs", "codex:refactor", -1), Now));
    }

    [Fact]
    public void Summary_counts_files_borrowers_and_overdue()
    {
        var s = CatalogView.Summarize(new[]
        {
            E("huddle", "src/a.cs", "codex:refactor", 30),
            E("huddle", "src/b.cs", "codex:refactor", -5),
            E("huddle", "src/c.cs", "cursor:dev", 30),
        }, Now);

        Assert.Equal(3, s.Total);
        Assert.Equal(1, s.Overdue);
        Assert.Equal(2, s.ByBorrower.Count);
        var codex = s.ByBorrower[0];
        Assert.Equal("codex:refactor", codex.Borrower);
        Assert.Equal(2, codex.Count);
        Assert.Equal(1, codex.Overdue);
    }

    // One agent, two spellings, must be one borrower in the statistics — the same rule the
    // checkout and the gate apply, or the footer would count a single agent twice.
    [Fact]
    public void Summary_treats_both_spellings_of_a_name_as_one_borrower()
    {
        var s = CatalogView.Summarize(new[]
        {
            E("huddle", "src/a.cs", "huddle:architect", 30),
            E("huddle", "src/b.cs", "huddle_architect", 30),
        }, Now);

        Assert.Equal(2, Assert.Single(s.ByBorrower).Count);
    }

    [Fact]
    public void Empty_summary_says_every_file_is_available()
    {
        var lines = CatalogView.SummaryLines(CatalogView.Summarize(Array.Empty<CatalogEntry>(), Now));

        Assert.Equal("Nothing is checked out - every file is available.", Assert.Single(lines));
    }

    [Fact]
    public void Summary_lines_name_each_borrower_with_counts_and_lateness()
    {
        var lines = CatalogView.SummaryLines(CatalogView.Summarize(new[]
        {
            E("huddle", "src/a.cs", "codex:refactor", 30),
            E("huddle", "src/b.cs", "codex:refactor", -5),
            E("huddle", "src/c.cs", "cursor:dev", 30),
        }, Now));

        Assert.Equal("3 file(s) checked out to 2 borrower(s), 1 overdue. Everything else is available.", lines[0]);
        Assert.Equal("  codex:refactor 2 (1 overdue); cursor:dev 1", lines[1]);
    }
}
