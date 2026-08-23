using Huddle;

namespace Huddle.Tests;

public class FeatureLedgerParseTests
{
    const string Good = """
        ---
        repo: huddle
        project: oracle
        ---
        # Ledger — huddle

        Some prose the parser must ignore.

        | ID | Type | Parent | Title | State | Pri | Owner | Accepts | Refs |
        |----|------|--------|-------|-------|-----|-------|---------|------|
        | E-001 | epic | | Work is traceable | planned | P0 | operator | | docs/a.md |
        | F-001 | feature | E-001 | Feature ledger | planned | P0 | huddle:architect | | docs/spec.md docs/plan.md |
        | D-001 | deliverable | F-001 | Parser | ideated | | | FeatureLedgerParseTests | |
        | F-002 | feature | otherapp:E-003 | Cross-repo child | decided | P1 | | | |

        Trailing prose, also ignored.
        """;

    [Fact]
    public void Parses_frontmatter_rows_and_ignores_prose()
    {
        var r = FeatureLedgerParser.Parse(Good);
        Assert.Empty(r.Errors);
        Assert.Equal("huddle", r.Frontmatter["repo"]);
        Assert.Equal(4, r.Rows.Count);
        var f = r.Rows[1];
        Assert.Equal("F-001", f.Id.ToString());
        Assert.Equal(LedgerType.Feature, f.Type);
        Assert.Equal("E-001", f.Parent!.Value.ToString());
        Assert.Equal("huddle:architect", f.Owner);
        Assert.Equal(new[] { "docs/spec.md", "docs/plan.md" }, f.Refs);
        Assert.Null(r.Rows[2].Pri);
        Assert.Equal("FeatureLedgerParseTests", r.Rows[2].Accepts);
        Assert.Equal("otherapp", r.Rows[3].Parent!.Value.Repo);
    }

    [Fact]
    public void Header_is_case_insensitive_and_extra_columns_ignored()
    {
        var text = "| id | TYPE | parent | title | state | pri | owner | accepts | refs | notes |\n|-|-|-|-|-|-|-|-|-|-|\n| E-001 | epic | | T | ideated | | | | | extra |\n";
        var r = FeatureLedgerParser.Parse(text);
        Assert.Empty(r.Errors);
        Assert.Single(r.Rows);
    }

    [Fact]
    public void Bad_rows_are_reported_with_line_numbers_never_dropped()
    {
        var text = """
            | ID | Type | Parent | Title | State | Pri | Owner | Accepts | Refs |
            |----|------|--------|-------|-------|-----|-------|---------|------|
            | E-001 | epic | | Good | ideated | | | | |
            | X-001 | epic | | Bad id | ideated | | | | |
            | F-001 | epic | | Type mismatch | ideated | | | | |
            | F-002 | feature | nope | Bad parent | ideated | | | | |
            | F-003 | feature | | Bad state | flying | | | | |
            | F-004 | feature | | | ideated | | | | |
            """;
        var r = FeatureLedgerParser.Parse(text);
        Assert.Single(r.Rows);
        Assert.Equal(5, r.Errors.Count);
        Assert.Equal(4, r.Errors[0].Line);
        Assert.Contains("id", r.Errors[0].Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("type", r.Errors[1].Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("parent", r.Errors[2].Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("state", r.Errors[3].Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("title", r.Errors[4].Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_required_column_is_one_error_and_no_rows()
    {
        var text = "| ID | Type | Title |\n|-|-|-|\n| E-001 | epic | T |\n";
        var r = FeatureLedgerParser.Parse(text);
        Assert.Empty(r.Rows);
        var e = Assert.Single(r.Errors);
        Assert.Contains("Parent", e.Reason);
    }

    [Fact]
    public void Duplicate_id_is_an_error_on_the_second_row()
    {
        var text = "| ID | Type | Parent | Title | State | Pri | Owner | Accepts | Refs |\n|-|-|-|-|-|-|-|-|-|\n| E-001 | epic | | A | ideated | | | | |\n| E-001 | epic | | B | ideated | | | | |\n";
        var r = FeatureLedgerParser.Parse(text);
        Assert.Single(r.Rows);
        Assert.Contains("duplicate", Assert.Single(r.Errors).Reason);
    }

    [Fact]
    public void Task_rows_are_refused_in_ledger_md()
    {
        var text = "| ID | Type | Parent | Title | State | Pri | Owner | Accepts | Refs |\n|-|-|-|-|-|-|-|-|-|\n| T-001 | task | | A | assigned | | | | |\n";
        var r = FeatureLedgerParser.Parse(text);
        Assert.Empty(r.Rows);
        Assert.Contains("events.jsonl", Assert.Single(r.Errors).Reason);
    }

    [Fact]
    public void No_table_means_no_rows_no_errors()
    {
        var r = FeatureLedgerParser.Parse("# just prose\n");
        Assert.Empty(r.Rows); Assert.Empty(r.Errors);
    }
}
