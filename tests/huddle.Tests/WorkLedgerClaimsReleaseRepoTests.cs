using Huddle;
using Xunit;

namespace HuddleTests;

/// <summary>
/// I018: a release is scoped to one repo. Claim paths are repo-relative, so one path names a
/// different file in every repo, and releasing on session plus path alone dropped every one of
/// them: releasing AGENTS.md in one repo silently released the same session's claim on another
/// repo's AGENTS.md before it was committed. Release now uses the repo a claim would have used —
/// `--repo` when given, otherwise the session's own — exactly as `--claim` does.
///
/// Neutral repo names, because the public-release scrub renames private ones.
/// </summary>
public class WorkLedgerClaimsReleaseRepoTests : IDisposable
{
    private readonly string _configDir, _claimsDir, _alphaRoot, _betaRoot;
    private readonly List<string> _out = new();

    public WorkLedgerClaimsReleaseRepoTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "huddle-releaserepo-" + Guid.NewGuid().ToString("N"));
        _claimsDir = Path.Combine(_configDir, "ipc", "workledger", "claims");
        _alphaRoot = Path.Combine(_configDir, "repos", "alpha");
        _betaRoot = Path.Combine(_configDir, "repos", "beta");
        Directory.CreateDirectory(_claimsDir);
        Directory.CreateDirectory(_alphaRoot);
        Directory.CreateDirectory(_betaRoot);
        File.WriteAllText(Path.Combine(_configDir, "huddle.json"), $$"""
        { "sessions": [
            { "name": "alpha", "aliases": ["al"], "root": {{System.Text.Json.JsonSerializer.Serialize(_alphaRoot)}} },
            { "name": "beta", "root": {{System.Text.Json.JsonSerializer.Serialize(_betaRoot)}} }
        ] }
        """);
    }

    public void Dispose() { try { Directory.Delete(_configDir, true); } catch { } }

    private WorkLedgerClaims Ledger(Func<string, string?>? resolve = null) => new(_claimsDir, _ => { }, resolve);

    private void Hold(string repo, params string[] files) =>
        Ledger().Write(new WorkLedgerClaim("alpha:architect", repo, "A-" + Guid.NewGuid().ToString("N")[..6],
                                           DateTime.UtcNow, "", files));

    private string? Resolve(string name) => name.ToLowerInvariant() switch
    {
        "alpha" or "al" => _alphaRoot,
        "beta" => _betaRoot,
        _ => null
    };

    private IEnumerable<(string Repo, string File)> Held() =>
        Ledger().ReadAll().SelectMany(c => c.Files.Select(f => (c.Repo, f)));

    private string? Env(string key) => key switch
    {
        "HUDDLE_CLAIMS" => _claimsDir,
        "HUDDLE_INSTANCE" => "alpha:architect",
        "HUDDLE_REPO" => "alpha",
        "HUDDLE_GUID" => "33333333-3333-3333-3333-333333333333",
        _ => null
    };

    // ---- the ledger ---------------------------------------------------------------

    [Fact]
    public void A_scoped_release_leaves_the_same_path_claimed_in_another_repo()
    {
        Hold("alpha", "AGENTS.md");
        Hold("beta", "AGENTS.md");

        var released = Ledger(Resolve).Release("alpha:architect", new[] { "AGENTS.md" }, repo: "alpha");

        Assert.Equal(1, released);
        Assert.Equal(("beta", "AGENTS.md"), Assert.Single(Held()));
    }

    // A caller that genuinely holds no repo keeps the reach it always had.
    [Fact]
    public void An_unscoped_release_keeps_the_old_reach()
    {
        Hold("alpha", "AGENTS.md");
        Hold("beta", "AGENTS.md");

        Assert.Equal(2, Ledger().Release("alpha:architect", new[] { "AGENTS.md" }));
        Assert.Empty(Held());
    }

    // The collision fail-safe applied to release: a claim that recorded no repo must never become
    // impossible to release.
    [Fact]
    public void A_legacy_claim_with_no_repo_is_released_by_a_scoped_release()
    {
        Hold("", "AGENTS.md");

        Assert.Equal(1, Ledger(Resolve).Release("alpha:architect", new[] { "AGENTS.md" }, repo: "alpha"));
        Assert.Empty(Held());
    }

    [Fact]
    public void An_alias_spelling_releases_a_claim_made_under_the_canonical_name()
    {
        Hold("alpha", "AGENTS.md");

        Assert.Equal(1, Ledger(Resolve).Release("alpha:architect", new[] { "AGENTS.md" }, repo: "al"));
    }

    [Fact]
    public void A_scoped_release_still_refuses_a_same_named_twins_claim()
    {
        Ledger().Write(new WorkLedgerClaim("alpha:architect", "alpha", "A-twin", DateTime.UtcNow, "",
                                           new[] { "AGENTS.md" }, OwnerGuid: "11111111-1111-1111-1111-111111111111"));

        var released = Ledger(Resolve).Release("alpha:architect", new[] { "AGENTS.md" },
                                               ownerGuid: "22222222-2222-2222-2222-222222222222", repo: "alpha");

        Assert.Equal(0, released);
        Assert.Single(Held());
    }

    // ---- the CLI, end to end --------------------------------------------------------

    // The incident, reproduced: one file name claimed in two repos, one release, both gone.
    [Fact]
    public void Cli_release_defaults_to_the_sessions_own_repo()
    {
        LedgerCommands.RunClaim(new[] { "AGENTS.md" }, Env, _out.Add);
        LedgerCommands.RunClaim(new[] { "--repo", "beta", "AGENTS.md" }, Env, _out.Add);

        LedgerCommands.RunRelease(new[] { "AGENTS.md" }, Env, _out.Add);

        Assert.Equal(("beta", "AGENTS.md"), Assert.Single(Held()));
    }

    // The flag used to be parsed and discarded.
    [Fact]
    public void Cli_release_with_repo_releases_only_that_repo()
    {
        LedgerCommands.RunClaim(new[] { "AGENTS.md" }, Env, _out.Add);
        LedgerCommands.RunClaim(new[] { "--repo", "beta", "AGENTS.md" }, Env, _out.Add);

        LedgerCommands.RunRelease(new[] { "--repo", "beta", "AGENTS.md" }, Env, _out.Add);

        Assert.Equal(("alpha", "AGENTS.md"), Assert.Single(Held()));
    }

    [Fact]
    public void Cli_release_returns_only_the_scoped_repos_book()
    {
        LedgerCommands.RunClaim(new[] { "AGENTS.md" }, Env, _out.Add);
        LedgerCommands.RunClaim(new[] { "--repo", "beta", "AGENTS.md" }, Env, _out.Add);
        var catalog = new FileCatalog(FileCatalog.DirBesideClaims(_claimsDir));
        Assert.Equal(2, catalog.ReadAll().Count);

        LedgerCommands.RunRelease(new[] { "AGENTS.md" }, Env, _out.Add);

        Assert.Null(catalog.Status("alpha", "AGENTS.md"));
        Assert.NotNull(catalog.Status("beta", "AGENTS.md"));
    }
}
