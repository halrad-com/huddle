using Huddle;

namespace Huddle.Tests;

public class LedgerAgeTests
{
    static LedgerRow Row(string id, string parent, string state, string title = "t") =>
        new(LedgerId.TryParse(id, out var i) ? i : default, i.Type,
            parent.Length > 0 && LedgerId.TryParse(parent, out var p) ? p : null, title, state, null, null, null, [], 1);

    static LedgerTask Task(string id, string owner, DateTimeOffset at, string state = "assigned", string? parent = null) =>
        new(LedgerId.TryParse(id, out var i) ? i : default, "task " + id, state, owner, null,
            parent != null && LedgerId.TryParse(parent, out var p) ? p : null, null, [], at, at, null, false);

    static LedgerRepoSnapshot Snap(string repo, IEnumerable<LedgerRow> rows, IEnumerable<LedgerTask> tasks,
        IEnumerable<LedgerEvent>? events = null) =>
        new(repo, "x", rows.ToList(), tasks.ToList(), [], [], true, events?.ToList());

    static readonly DateTimeOffset Now = new(2026, 8, 22, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Open_by_age_is_oldest_first_and_excludes_terminal()
    {
        var s = Snap("app", [Row("F-001", "", "planned"), Row("F-002", "", "accepted"), Row("F-003", "", "dropped")],
            [Task("T-001", "app:be", Now.AddDays(-4)), Task("T-002", "app:be", Now.AddHours(-4)), Task("T-003", "app:be", Now.AddDays(-1), "declined")]);
        var items = LedgerView.OpenByAge([s], Now);
        Assert.Equal(["T-001", "T-002", "F-001"], items.Select(i => i.Id).ToArray());
        Assert.Equal(TimeSpan.FromDays(4), items[0].Age);
        Assert.Null(items[2].Age);
    }

    [Fact]
    public void Render_open_shows_age_column()
    {
        var s = Snap("app", [], [Task("T-001", "app:backenddev", Now.AddDays(-4))]);
        var text = LedgerView.RenderOpenByAge(LedgerView.OpenByAge([s], Now));
        Assert.Contains("app:T-001", text);
        Assert.Contains("4d", text);
        Assert.Contains("app:backenddev", text);
    }

    [Fact]
    public void Orphans_are_tasks_with_no_parent()
    {
        var s = Snap("app", [], [Task("T-001", "a", Now), Task("T-002", "a", Now, parent: "D-001")]);
        var text = LedgerView.RenderOrphans([s]);
        Assert.Contains("T-001", text);
        Assert.DoesNotContain("T-002", text);
    }

    [Fact]
    public void Tree_nests_children_under_parents_and_lists_errors_first()
    {
        var s = new LedgerRepoSnapshot("app", "x",
            [Row("E-001", "", "planned", "Epic"), Row("F-001", "E-001", "planned", "Feat"), Row("D-001", "F-001", "ideated", "Deliv")],
            [Task("T-001", "a", Now, parent: "D-001")],
            [new LedgerRowError(9, "| junk |", "bad id")], [], true);
        var text = LedgerView.RenderTree(s, includeClosed: false);
        var lines = text.Split('\n');
        Assert.Contains("line 9", lines[0] + lines[1]);
        int e = Array.FindIndex(lines, l => l.Contains("E-001")), f = Array.FindIndex(lines, l => l.Contains("F-001")),
            d = Array.FindIndex(lines, l => l.Contains("D-001")), t = Array.FindIndex(lines, l => l.Contains("T-001"));
        Assert.True(e < f && f < d && d < t);
        Assert.True(lines[f].TakeWhile(char.IsWhiteSpace).Count() > lines[e].TakeWhile(char.IsWhiteSpace).Count());
    }

    [Fact]
    public void Tree_hides_closed_unless_asked()
    {
        var s = Snap("app", [Row("F-001", "", "accepted"), Row("F-002", "", "planned")], []);
        Assert.DoesNotContain("F-001", LedgerView.RenderTree(s, false));
        Assert.Contains("F-001", LedgerView.RenderTree(s, true));
    }

    [Theory]
    [InlineData(0, 0, 25, "25m")] [InlineData(0, 7, 0, "7h")] [InlineData(4, 3, 0, "4d")] [InlineData(0, 0, 0, "0m")]
    public void Age_format(int d, int h, int m, string s) => Assert.Equal(s, LedgerView.Age(new TimeSpan(d, h, m, 0)));

    // L1: a repo-qualified parent names a row in ANOTHER repo. It must never be
    // matched against a local bare row of the same number, and the row that carries
    // it must be visibly distinct from a root.
    [Fact]
    public void L1_qualified_parent_is_not_matched_against_a_local_bare_row()
    {
        var s = Snap("app", [Row("F-001", "", "planned", "Local feature"),
                            Row("D-003", "otherapp:F-001", "ideated", "Foreign child")], []);
        var text = LedgerView.RenderTree(s, false);
        var lines = text.Split('\n');
        int f = Array.FindIndex(lines, l => l.Contains("Local feature"));
        int stub = Array.FindIndex(lines, l => l.Contains("^ otherapp:F-001"));
        int d = Array.FindIndex(lines, l => l.Contains("D-003"));
        Assert.True(f >= 0 && stub >= 0 && d >= 0);
        Assert.True(f < stub && stub < d, "D-003 must hang off the cross-repo stub, not local F-001");
        Assert.Contains("cross-repo parent", text);
    }

    [Fact]
    public void L1_cross_repo_child_is_nested_under_its_stub_not_at_root()
    {
        var s = Snap("app", [Row("F-002", "", "planned", "Root feature"),
                            Row("D-004", "otherapp:E-009", "ideated", "Cross child")], []);
        var text = LedgerView.RenderTree(s, false);
        Assert.Contains("^ otherapp:E-009", text);
        var lines = text.Split('\n');
        int root = Array.FindIndex(lines, l => l.Contains("Root feature"));
        int child = Array.FindIndex(lines, l => l.Contains("D-004"));
        Assert.True(lines[child].TakeWhile(char.IsWhiteSpace).Count() >
                    lines[root].TakeWhile(char.IsWhiteSpace).Count());
    }

    [Fact]
    public void L1_render_one_does_not_resolve_a_qualified_parent_locally()
    {
        var s = Snap("app", [Row("F-001", "", "planned", "Local feature"),
                            Row("D-003", "otherapp:F-001", "ideated", "Foreign child")], []);
        Assert.True(LedgerId.TryParse("D-003", out var id));
        var one = LedgerView.RenderOne([s], id);
        Assert.Contains("^ otherapp:F-001", one);
        Assert.Contains("not in this repo", one);
        Assert.DoesNotContain("Local feature", one);

        Assert.True(LedgerId.TryParse("F-001", out var fid));
        Assert.DoesNotContain("D-003", LedgerView.RenderOne([s], fid));
    }

    // A parent qualified with THIS repo's own name is local, not foreign.
    [Fact]
    public void L1_parent_qualified_with_the_same_repo_still_resolves_locally()
    {
        var s = Snap("app", [Row("F-001", "", "planned", "Local feature"),
                            Row("D-005", "app:F-001", "ideated", "Same-repo child")], []);
        var text = LedgerView.RenderTree(s, false);
        Assert.DoesNotContain("cross-repo parent", text);
        var lines = text.Split('\n');
        int f = Array.FindIndex(lines, l => l.Contains("Local feature"));
        int d = Array.FindIndex(lines, l => l.Contains("D-005"));
        Assert.True(lines[d].TakeWhile(char.IsWhiteSpace).Count() >
                    lines[f].TakeWhile(char.IsWhiteSpace).Count());
    }

    // L3: RenderOne's event history matches on the PARSED id, so an item whose
    // events spell the id differently still shows its whole history.
    [Fact]
    public void L3_render_one_matches_events_by_parsed_id_not_string()
    {
        var problems = new List<string>();
        var lines = new[]
        {
            """{"ts":"2026-08-21T21:30:00Z","event":"task-assigned","id":"T-7","owner":"app:be","title":"x"}""",
            """{"ts":"2026-08-21T22:00:00Z","event":"task-acked","id":"T-007","actor":"app:be"}""",
        };
        var events = LedgerEventReader.ParseLines(lines, "f", problems);
        var tasks = TaskMaterializer.Materialize(events, problems);
        Assert.True(LedgerId.TryParse("T-7", out var id));
        var text = LedgerView.RenderOne([Snap("app", [], tasks, events)], id);
        Assert.Contains("task-assigned", text);
        Assert.Contains("task-acked", text);
    }

    // L2: `ledger <id>` must show each repo's OWN event history under that repo's
    // section. Unioning every repo's events and filtering by bare id printed
    // otherapp's F-001 transitions under huddle's F-001.
    [Fact]
    public void L2_render_one_scopes_events_to_the_repo_being_rendered()
    {
        var problems = new List<string>();
        var rbEvents = LedgerEventReader.ParseLines(
            ["""{"ts":"2026-08-21T21:00:00Z","event":"state","id":"F-001","actor":"app:architect","from":"planned","to":"dispatched"}"""],
            "app", problems);
        var tdEvents = LedgerEventReader.ParseLines(
            ["""{"ts":"2026-08-21T22:00:00Z","event":"state","id":"F-001","actor":"td:architect","from":"dispatched","to":"delivered"}"""],
            "td", problems);

        var app = Snap("app", [Row("F-001", "", "dispatched", "app feature")], [], rbEvents);
        var td = Snap("td", [Row("F-001", "", "delivered", "TD feature")], [], tdEvents);

        Assert.True(LedgerId.TryParse("F-001", out var id));
        var lines = LedgerView.RenderOne([app, td], id).Split('\n');

        int rbSection = Array.FindIndex(lines, l => l.Contains("app:F-001"));
        int tdSection = Array.FindIndex(lines, l => l.Contains("td:F-001"));
        Assert.True(rbSection >= 0 && tdSection > rbSection);

        Assert.Single(lines, l => l.Contains("app:architect"));
        Assert.Single(lines, l => l.Contains("td:architect"));
        int rbActor = Array.FindIndex(lines, l => l.Contains("app:architect"));
        int tdActor = Array.FindIndex(lines, l => l.Contains("td:architect"));
        Assert.True(rbActor > rbSection && rbActor < tdSection, "app's events must stay in app's section");
        Assert.True(tdActor > tdSection, "td's events must stay in td's section");
    }

    // L4: which repo is "current" comes from the working directory measured against
    // the configured roots — never from a repo literally named "huddle".
    [Theory]
    [InlineData(@"C:\repos\app", "app")]
    [InlineData(@"C:\repos\app\src\deep", "app")]
    [InlineData(@"C:\repos\app\", "app")]
    [InlineData(@"C:\repos\td", "td")]
    [InlineData(@"C:\elsewhere", null)]
    [InlineData(@"C:\repos\rbextra", null)]
    public void L4_repo_for_directory(string cwd, string? expected) =>
        Assert.Equal(expected, LedgerView.RepoForDirectory(
            [("app", @"C:\repos\app"), ("td", @"C:\repos\td")], cwd));

    static readonly (string Name, string Root)[] L4Repos = [("app", @"C:\repos\app"), ("td", @"C:\repos\td")];

    [Fact]
    public void L4_no_current_ledger_when_cwd_is_outside_every_configured_root()
    {
        var snaps = new[] { Snap("app", [], []), Snap("td", [], []) };
        Assert.Empty(LedgerView.CurrentSnapshots(snaps, L4Repos, @"C:\elsewhere", null));
    }

    [Fact]
    public void L4_current_ledger_is_resolved_from_cwd_not_a_repo_named_huddle()
    {
        var snaps = new[] { Snap("app", [], []), Snap("td", [], []) };
        var cur = LedgerView.CurrentSnapshots(snaps, L4Repos, @"C:\repos\td\src", null);
        Assert.Equal("td", Assert.Single(cur).Repo);
    }

    [Fact]
    public void L4_repo_filter_wins_over_the_working_directory()
    {
        var snaps = new[] { Snap("app", [], []) };
        var cur = LedgerView.CurrentSnapshots(snaps, L4Repos, @"C:\elsewhere", "app");
        Assert.Equal("app", Assert.Single(cur).Repo);
    }

    // L4: a ledger.md whose frontmatter names a different repo was copied between
    // repos. Warn — do not silently present it as this repo's ledger.
    [Fact]
    public void L4_declared_repo_mismatch_is_warned_and_absence_is_not()
    {
        Assert.Null(LedgerView.DeclaredRepoWarning(new("app", "x", [], [], [], [], true, null, "app")));
        Assert.Null(LedgerView.DeclaredRepoWarning(new("app", "x", [], [], [], [], true, null, null)));
        Assert.Null(LedgerView.DeclaredRepoWarning(new("app", "x", [], [], [], [], true, null, "app")));

        var w = LedgerView.DeclaredRepoWarning(new("app", "x", [], [], [], [], true, null, "otherapp"));
        Assert.NotNull(w);
        Assert.Contains("otherapp", w);
        Assert.Contains("app", w);
    }

    // Cleanup: LedgerSubdir is the one place the location is spelled; Load uses it.
    [Fact]
    public void Load_resolves_the_ledger_subdir_under_the_repo_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "ledger-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "docs", "ledger"));
        try
        {
            var s = LedgerView.Load("app", root);
            Assert.True(s.Present);
            Assert.Equal(Path.GetFullPath(Path.Combine(root, LedgerView.LedgerSubdir)), s.Dir);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Load_of_absent_dir_is_not_present()
    {
        var s = LedgerView.Load("app", Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid()));
        Assert.False(s.Present);
        Assert.Empty(s.Rows);
    }
}
