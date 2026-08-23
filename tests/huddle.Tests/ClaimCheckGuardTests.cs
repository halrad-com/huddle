using Huddle;
using Xunit;

namespace HuddleTests;

/// <summary>
/// The PreToolUse claim guard (`huddle --claim-check`). Exit 0 = edit allowed,
/// LedgerCommands.Block (2) = refused with the reason on stderr.
/// </summary>
public class ClaimCheckGuardTests : IDisposable
{
    private readonly string _configDir, _claimsDir, _rbRoot, _faRoot;
    private readonly List<string> _err = new();

    public ClaimCheckGuardTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "huddle-guard-" + Guid.NewGuid().ToString("N"));
        _claimsDir = Path.Combine(_configDir, "ipc", "workledger", "claims");
        _rbRoot = Path.Combine(_configDir, "src", "myapp");
        _faRoot = Path.Combine(_configDir, "src", "netlib");
        Directory.CreateDirectory(_claimsDir);
        Directory.CreateDirectory(_rbRoot);
        Directory.CreateDirectory(_faRoot);
        File.WriteAllText(Path.Combine(_configDir, "huddle.json"), $$"""
        { "sessions": [ { "name": "myapp", "root": "{{_rbRoot.Replace("\\", "\\\\")}}" }, { "name": "netlib", "root": "{{_faRoot.Replace("\\", "\\\\")}}" } ] }
        """);
    }

    public void Dispose() { try { Directory.Delete(_configDir, true); } catch { } }

    private string? Env(string key) => key switch
    {
        "HUDDLE_CLAIMS" => _claimsDir,
        "HUDDLE_INSTANCE" => "myapp:architect",
        "HUDDLE_REPO" => "myapp",
        _ => null
    };

    private static string Stdin(string path) =>
        "{\"tool_name\":\"Edit\",\"tool_input\":{\"file_path\":\"" + path.Replace("\\", "\\\\") + "\"}}";

    private void Hold(string session, string repo, string root, string file) =>
        new WorkLedgerClaims(_claimsDir, _ => { }).Write(new WorkLedgerClaim(
            session, repo, "A-" + Guid.NewGuid().ToString("N")[..6], DateTime.UtcNow, "", new[] { file }, Root: root));

    [Fact]
    public void Unclaimed_file_in_own_repo_is_blocked_with_the_claim_command()
    {
        var rc = LedgerCommands.RunClaimCheck(Stdin(Path.Combine(_rbRoot, "MBXH", "Core", "X.cs")), Env, _err.Add);
        Assert.Equal(LedgerCommands.Block, rc);
        var t = string.Join("\n", _err);
        Assert.Contains("EDIT BLOCKED", t);
        Assert.Contains("huddle --claim MBXH/Core/X.cs", t);
    }

    [Fact]
    public void Unclaimed_file_in_other_repo_is_blocked_with_repo_flag_and_names_holder()
    {
        Hold("myapp:backenddev", "netlib", _faRoot, "src/netcfg/netcfgManager.cs");
        var rc = LedgerCommands.RunClaimCheck(Stdin(Path.Combine(_faRoot, "src", "netcfg", "netcfgManager.cs")), Env, _err.Add);
        Assert.Equal(LedgerCommands.Block, rc);
        var t = string.Join("\n", _err);
        Assert.Contains("huddle --claim --repo netlib src/netcfg/netcfgManager.cs", t);
        Assert.Contains("HELD BY myapp:backenddev", t);
    }

    [Fact]
    public void Own_claim_allows_the_edit()
    {
        Hold("myapp:architect", "netlib", _faRoot, "src/netcfg/netcfgManager.cs");
        var rc = LedgerCommands.RunClaimCheck(Stdin(Path.Combine(_faRoot, "src", "netcfg", "netcfgManager.cs")), Env, _err.Add);
        Assert.Equal(0, rc);
        Assert.Empty(_err);
    }

    [Fact]
    public void Own_claim_matches_path_spelling_variants()
    {
        Hold("myapp:architect", "myapp", _rbRoot, @".\MBXH\Core\X.cs");
        Assert.Equal(0, LedgerCommands.RunClaimCheck(Stdin(Path.Combine(_rbRoot, "MBXH", "Core", "x.cs")), Env, _err.Add));
    }

    [Theory]
    [InlineData("ipc")] [InlineData("logs")] [InlineData(".claude")] [InlineData("hooks")]
    public void Huddle_traffic_dirs_are_never_gated(string dir)
    {
        var rc = LedgerCommands.RunClaimCheck(Stdin(Path.Combine(_rbRoot, dir, "x.json")), Env, _err.Add);
        Assert.Equal(0, rc);
    }

    [Fact]
    public void File_outside_every_repo_is_allowed()
    {
        Assert.Equal(0, LedgerCommands.RunClaimCheck(Stdin(Path.Combine(Path.GetTempPath(), "elsewhere", "x.cs")), Env, _err.Add));
    }

    [Fact]
    public void Missing_ledger_context_fails_open()
    {
        Assert.Equal(0, LedgerCommands.RunClaimCheck(Stdin(Path.Combine(_rbRoot, "a.cs")), _ => null, _err.Add));
    }

    [Fact]
    public void Garbage_stdin_fails_open_with_a_note()
    {
        Assert.Equal(0, LedgerCommands.RunClaimCheck("{not json", Env, _err.Add));
        Assert.Contains("guard degraded", string.Join("\n", _err));
    }

    [Fact]
    public void Non_file_tool_input_is_allowed()
    {
        Assert.Equal(0, LedgerCommands.RunClaimCheck("""{"tool_name":"Bash","tool_input":{"command":"ls"}}""", Env, _err.Add));
    }
}
