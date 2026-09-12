using Huddle;
using Xunit;

namespace HuddleTests;

/// <summary>
/// LedgerCommands.RepoForPath: the most specific registered repo that contains a path. Real
/// configs nest — a workspace repo holds every repo, and an outer repo holds a registered project
/// directory — and the checkout commands and the edit gate must pick the SAME repo for the same
/// file, or a checkout filed under one name never meets a gate check filed under the other.
///
/// Neutral names on purpose: the public-release scrub renames private repo names, and a test
/// whose data holds one can quietly change meaning on the other side of the rename.
/// </summary>
public class RepoForPathTests : IDisposable
{
    private readonly string _configDir, _claimsDir, _workspace, _suite, _app, _suitecast;

    public RepoForPathTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "huddle-repoforpath-" + Guid.NewGuid().ToString("N"));
        _claimsDir = Path.Combine(_configDir, "ipc", "workledger", "claims");
        _workspace = Path.Combine(_configDir, "repos");
        _suite = Path.Combine(_workspace, "suite");
        _app = Path.Combine(_suite, "app");
        _suitecast = Path.Combine(_workspace, "suitecast");
        Directory.CreateDirectory(_claimsDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_configDir, recursive: true); } catch { }
    }

    private void WriteConfig(params (string Name, string Root)[] repos)
    {
        var sessions = string.Join(",", repos.Select(r =>
            $"{{\"name\":{System.Text.Json.JsonSerializer.Serialize(r.Name)},\"root\":{System.Text.Json.JsonSerializer.Serialize(r.Root)}}}"));
        File.WriteAllText(Path.Combine(_configDir, "huddle.json"), $"{{\"sessions\":[{sessions}]}}");
    }

    // Registered in the order that makes "first match wins" the WRONG answer, so the test proves
    // the longest root is chosen rather than the first one listed.
    private void WriteNestedConfig() =>
        WriteConfig(("workspace", _workspace), ("suite", _suite), ("app", _app), ("suitecast", _suitecast));

    [Fact]
    public void Most_specific_repo_wins_when_roots_nest()
    {
        WriteNestedConfig();

        var hit = LedgerCommands.RepoForPath(_claimsDir, Path.Combine(_app, "src", "Core", "X.cs"));

        Assert.Equal("app", hit!.Value.Name);
        Assert.Equal(_app, hit.Value.Root);
    }

    [Fact]
    public void File_in_the_outer_repo_but_outside_the_nested_one_resolves_to_the_outer()
    {
        WriteNestedConfig();

        Assert.Equal("suite", LedgerCommands.RepoForPath(_claimsDir, Path.Combine(_suite, "README.md"))!.Value.Name);
    }

    [Fact]
    public void File_in_no_specific_repo_falls_to_the_catch_all()
    {
        WriteNestedConfig();

        Assert.Equal("workspace", LedgerCommands.RepoForPath(_claimsDir, Path.Combine(_workspace, "loose", "x.md"))!.Value.Name);
    }

    [Fact]
    public void A_root_itself_resolves_to_its_own_repo()
    {
        WriteNestedConfig();

        Assert.Equal("app", LedgerCommands.RepoForPath(_claimsDir, _app)!.Value.Name);
    }

    // `suite` is a string prefix of `suitecast` but not a directory that contains it. Matching on
    // the raw prefix would file suitecast's files under suite.
    [Fact]
    public void A_sibling_sharing_a_name_prefix_is_not_contained()
    {
        WriteNestedConfig();

        Assert.Equal("suitecast", LedgerCommands.RepoForPath(_claimsDir, Path.Combine(_suitecast, "a.cs"))!.Value.Name);
    }

    [Fact]
    public void Case_and_a_trailing_separator_do_not_matter()
    {
        WriteConfig(("app", _app + Path.DirectorySeparatorChar));

        Assert.Equal("app", LedgerCommands.RepoForPath(_claimsDir, Path.Combine(_app, "x.cs").ToUpperInvariant())!.Value.Name);
    }

    [Fact]
    public void A_path_outside_every_registered_repo_is_null()
    {
        WriteNestedConfig();

        Assert.Null(LedgerCommands.RepoForPath(_claimsDir, Path.Combine(_configDir, "elsewhere", "x.cs")));
    }

    // A missing or malformed config must cost the caller nothing: the checkout commands fall back
    // rather than refusing to hand out a book.
    [Fact]
    public void No_config_is_null_not_an_error()
    {
        Assert.Null(LedgerCommands.RepoForPath(_claimsDir, Path.Combine(_app, "x.cs")));
    }

    [Fact]
    public void Malformed_config_is_null_not_an_error()
    {
        File.WriteAllText(Path.Combine(_configDir, "huddle.json"), "{ not json");

        Assert.Null(LedgerCommands.RepoForPath(_claimsDir, Path.Combine(_app, "x.cs")));
    }
}
