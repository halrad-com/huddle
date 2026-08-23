using Huddle;
using Xunit;

namespace HuddleTests;

/// <summary>
/// I015: `huddle --claim --repo <name>` lets a session claim a file in a repo that is not
/// its own. On 2026-08-22 two myapp sessions edited the same file in netlib (a
/// build dependency) with no claim, because the CLI took its repo from HUDDLE_REPO and a
/// netlib path had nowhere to land.
/// </summary>
public class LedgerCommandsCrossRepoTests : IDisposable
{
    private readonly string _configDir;
    private readonly string _claimsDir;
    private readonly string _netlibRoot;
    private readonly List<string> _out = new();

    public LedgerCommandsCrossRepoTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "huddle-xrepo-" + Guid.NewGuid().ToString("N"));
        _claimsDir = Path.Combine(_configDir, "ipc", "workledger", "claims");
        _netlibRoot = Path.Combine(_configDir, "src", "netlib");
        Directory.CreateDirectory(_claimsDir);
        Directory.CreateDirectory(_netlibRoot);
        var app = Path.Combine(_configDir, "src", "myapp").Replace("\\", "\\\\");
        var fa = _netlibRoot.Replace("\\", "\\\\");
        File.WriteAllText(Path.Combine(_configDir, "huddle.json"), $$"""
        { "sessions": [ { "name": "myapp", "root": "{{app}}" }, { "name": "netlib", "root": "{{fa}}" } ] }
        """);
    }

    public void Dispose() { try { Directory.Delete(_configDir, true); } catch { } }

    private string? Env(string key) => key switch
    {
        "HUDDLE_CLAIMS" => _claimsDir,
        "HUDDLE_INSTANCE" => "myapp:architect",
        "HUDDLE_REPO" => "myapp",
        "HUDDLE_GUID" => "22222222-2222-2222-2222-222222222222",
        _ => null
    };

    [Fact]
    public void SplitRepoFlag_extracts_the_flag_anywhere_and_keeps_the_rest()
    {
        var rest = LedgerCommands.SplitRepoFlag(new[] { "a.cs", "--repo", "netlib", "b.cs" }, out var repo);
        Assert.Equal("netlib", repo);
        Assert.Equal(new[] { "a.cs", "b.cs" }, rest);
        LedgerCommands.SplitRepoFlag(new[] { "a.cs" }, out var none);
        Assert.Null(none);
    }

    [Fact]
    public void Claim_with_repo_flag_records_under_that_repo_with_its_registered_root()
    {
        var code = LedgerCommands.RunClaim(new[] { "--repo", "netlib", "src/netcfg/netcfgManager.cs" }, Env, _out.Add);
        Assert.Equal(0, code);
        var claim = Assert.Single(new WorkLedgerClaims(_claimsDir, _ => { }).ReadAll());
        Assert.Equal("netlib", claim.Repo);
        Assert.Equal("myapp:architect", claim.SessionId);
        Assert.Equal(Path.GetFullPath(_netlibRoot), Path.GetFullPath(claim.Root));
        Assert.Contains("in netlib as myapp:architect", string.Join("\n", _out));
    }

    [Fact]
    public void Cross_repo_claim_sees_the_other_sessions_holder_on_the_same_file()
    {
        new WorkLedgerClaims(_claimsDir, _ => { }).Write(new WorkLedgerClaim(
            "myapp:backenddev", "netlib", "A-0", DateTime.UtcNow, "",
            new[] { "src/netcfg/netcfgManager.cs" }, Root: _netlibRoot));
        LedgerCommands.RunClaim(new[] { "--repo", "netlib", "src/netcfg/netcfgManager.cs" }, Env, _out.Add);
        Assert.Contains("ALSO HELD BY myapp:backenddev", string.Join("\n", _out));
    }

    [Fact]
    public void Unknown_repo_flag_is_refused_and_records_nothing()
    {
        var code = LedgerCommands.RunClaim(new[] { "--repo", "nope", "x.cs" }, Env, _out.Add);
        Assert.NotEqual(0, code);
        Assert.Empty(new WorkLedgerClaims(_claimsDir, _ => { }).ReadAll());
        Assert.Contains("not a registered repo", string.Join("\n", _out));
    }

    [Fact]
    public void Release_accepts_the_flag()
    {
        LedgerCommands.RunClaim(new[] { "--repo", "netlib", "src/netcfg/netcfgManager.cs" }, Env, _out.Add);
        var code = LedgerCommands.RunRelease(new[] { "--repo", "netlib", "src/netcfg/netcfgManager.cs" }, Env, _out.Add);
        Assert.Equal(0, code);
        Assert.Empty(new WorkLedgerClaims(_claimsDir, _ => { }).ReadAll());
    }
}
