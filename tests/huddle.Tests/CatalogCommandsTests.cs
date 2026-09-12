using Huddle;
using Xunit;

namespace HuddleTests;

/// <summary>
/// End to end through the checkout commands, run from a folder OUTSIDE the huddle tree — the
/// position an agent working in another repo is in. Two properties are pinned here:
///
///  * the shared ledger is found from the huddle binary's folder when nothing above the working
///    directory holds an ipc/workledger;
///  * a file is filed under the most specific registered repo that contains it, which is the name
///    the edit gate uses, so a checkout made from an outer folder really does stop a nested-repo
///    agent's edit of the same file.
///
/// Neutral repo names, for the reason given in RepoForPathTests: the public-release scrub renames
/// private names and can change what a fixture means.
/// </summary>
public class CatalogCommandsTests : IDisposable
{
    private readonly string _root, _huddle, _exeDir, _claimsDir, _catalogDir, _workspace, _suite, _app;
    private readonly List<string> _out = new();

    public CatalogCommandsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "huddle-catalogcmd-" + Guid.NewGuid().ToString("N"));
        _huddle = Path.Combine(_root, "huddle");
        _exeDir = Path.Combine(_huddle, "publish");
        _claimsDir = Path.Combine(_huddle, "ipc", "workledger", "claims");
        _catalogDir = Path.Combine(_huddle, "ipc", "workledger", "catalog");
        _workspace = Path.Combine(_root, "repos");
        _suite = Path.Combine(_workspace, "suite");
        _app = Path.Combine(_suite, "app");

        Directory.CreateDirectory(_exeDir);
        Directory.CreateDirectory(_claimsDir);
        Directory.CreateDirectory(Path.Combine(_app, "src"));
        File.WriteAllText(Path.Combine(_app, "src", "X.cs"), "class X {}");
        File.WriteAllText(Path.Combine(_suite, "README.md"), "# suite");

        // An outer repo holding a registered nested repo, both inside a catch-all workspace: the
        // shape that made "the repo I am standing in" the wrong question.
        var repos = new[] { ("workspace", _workspace), ("suite", _suite), ("app", _app), ("huddle", _huddle) };
        var sessions = string.Join(",", repos.Select(r =>
            $"{{\"name\":{System.Text.Json.JsonSerializer.Serialize(r.Item1)},\"root\":{System.Text.Json.JsonSerializer.Serialize(r.Item2)}}}"));
        File.WriteAllText(Path.Combine(_huddle, "huddle.json"), $"{{\"sessions\":[{sessions}]}}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static string? NoEnv(string _) => null;

    [Fact]
    public void Checkout_from_outside_the_huddle_tree_finds_the_ledger_through_the_binary()
    {
        var rc = CatalogCommands.RunCheckout(new[] { "--as", "codex:refactor", "README.md" }, NoEnv, _out.Add, _suite, _exeDir);

        Assert.Equal(0, rc);
        Assert.Equal("codex:refactor", new FileCatalog(_catalogDir).Status("suite", "README.md")!.Borrower);
    }

    [Fact]
    public void A_file_in_a_nested_repo_is_filed_under_that_repo_not_the_folder_run_from()
    {
        CatalogCommands.RunCheckout(new[] { "--as", "codex:refactor", "app/src/X.cs" }, NoEnv, _out.Add, _suite, _exeDir);

        var cat = new FileCatalog(_catalogDir);
        Assert.NotNull(cat.Status("app", "src/X.cs"));
        Assert.Null(cat.Status("suite", "app/src/X.cs"));
    }

    // The property the resolution change exists for. Filed under the folder it was run from, this
    // checkout would sit under "suite" while the gate looks under "app", and the edit would pass.
    [Fact]
    public void An_outer_folder_checkout_blocks_the_gate_for_the_nested_repo_agent()
    {
        CatalogCommands.RunCheckout(new[] { "--as", "codex:refactor", "app/src/X.cs" }, NoEnv, _out.Add, _suite, _exeDir);

        var err = new List<string>();
        string? GateEnv(string k) => k switch
        {
            "HUDDLE_CLAIMS" => _claimsDir,
            "HUDDLE_INSTANCE" => "app:architect",
            "HUDDLE_REPO" => "app",
            _ => null
        };
        var stdin = "{\"tool_name\":\"Edit\",\"tool_input\":{\"file_path\":" +
                    System.Text.Json.JsonSerializer.Serialize(Path.Combine(_app, "src", "X.cs")) + "}}";

        var rc = LedgerCommands.RunClaimCheck(stdin, GateEnv, err.Add);

        Assert.Equal(LedgerCommands.Block, rc);
        Assert.Contains("CHECKED OUT to codex:refactor", string.Join("\n", err));
    }

    [Fact]
    public void The_same_file_named_from_inside_the_nested_repo_is_the_same_book()
    {
        CatalogCommands.RunCheckout(new[] { "--as", "codex:refactor", "app/src/X.cs" }, NoEnv, _out.Add, _suite, _exeDir);

        var rc = CatalogCommands.RunCheckout(new[] { "--as", "cursor:dev", "src/X.cs" }, NoEnv, _out.Add, _app, _exeDir);

        Assert.Equal(1, rc);
        Assert.Contains(_out, l => l.Contains("CHECKED OUT to codex:refactor"));
    }

    [Fact]
    public void Paths_in_two_repos_are_refused_and_nothing_is_taken()
    {
        var rc = CatalogCommands.RunCheckout(new[] { "--as", "codex:refactor", "app/src/X.cs", "README.md" },
                                             NoEnv, _out.Add, _suite, _exeDir);

        Assert.Equal(2, rc);
        Assert.Contains(_out, l => l.Contains("different repos"));
        Assert.Empty(new FileCatalog(_catalogDir).ReadAll());
    }

    // A borrower's books are its books: returning them must not require knowing which repo each
    // was filed under, or where the agent was standing when it took them.
    [Fact]
    public void Checkin_all_returns_books_from_every_repo()
    {
        CatalogCommands.RunCheckout(new[] { "--as", "codex:refactor", "app/src/X.cs" }, NoEnv, _out.Add, _suite, _exeDir);
        CatalogCommands.RunCheckout(new[] { "--as", "codex:refactor", "README.md" }, NoEnv, _out.Add, _suite, _exeDir);
        Assert.Equal(2, new FileCatalog(_catalogDir).ReadAll().Count);

        var rc = CatalogCommands.RunCheckin(new[] { "--as", "codex:refactor", "--all" }, NoEnv, _out.Add, _workspace, _exeDir);

        Assert.Equal(0, rc);
        Assert.Empty(new FileCatalog(_catalogDir).ReadAll());
    }

    [Fact]
    public void Checkin_all_leaves_another_borrowers_books_alone()
    {
        CatalogCommands.RunCheckout(new[] { "--as", "codex:refactor", "README.md" }, NoEnv, _out.Add, _suite, _exeDir);
        CatalogCommands.RunCheckout(new[] { "--as", "cursor:dev", "app/src/X.cs" }, NoEnv, _out.Add, _suite, _exeDir);

        CatalogCommands.RunCheckin(new[] { "--as", "codex:refactor", "--all" }, NoEnv, _out.Add, _suite, _exeDir);

        var left = Assert.Single(new FileCatalog(_catalogDir).ReadAll());
        Assert.Equal("cursor:dev", left.Borrower);
    }

    [Fact]
    public void No_ledger_above_the_folder_or_the_binary_is_a_usage_error_not_a_crash()
    {
        var elsewhere = Path.Combine(_root, "nowhere");
        Directory.CreateDirectory(elsewhere);

        var rc = CatalogCommands.RunCatalog(Array.Empty<string>(), NoEnv, _out.Add, elsewhere, elsewhere);

        Assert.Equal(2, rc);
        Assert.Contains(_out, l => l.Contains("could not find the shared ledger"));
    }
}
