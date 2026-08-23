using Huddle;
using Xunit;

namespace HuddleTests;

/// <summary>
/// I016's second half. The spawn guard stops NEW twins; these cover the ledger's behaviour
/// when two sessions already share one `repo:persona` — the state the operator was in on
/// 2026-08-23. On SessionId alone each twin reads the other's claim as its own: overlaps
/// vanish, the PreToolUse guard waves both through, and either can release the other's
/// protection. OwnerGuid (the conversation id) is the discriminator.
/// </summary>
public class SameIdentityTwinTests : IDisposable
{
    private readonly string _dir, _claimsDir, _repoRoot;
    private readonly List<string> _out = new();
    private const string GuidA = "557976b8-9320-49ab-a72a-30589a4b3964";
    private const string GuidB = "54e19155-2478-4a08-bc99-b9adafc8b0fd";

    public SameIdentityTwinTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "huddle-twin-" + Guid.NewGuid().ToString("N"));
        _claimsDir = Path.Combine(_dir, "ipc", "workledger", "claims");
        _repoRoot = Path.Combine(_dir, "src", "otherapp");
        Directory.CreateDirectory(_claimsDir);
        Directory.CreateDirectory(_repoRoot);
        File.WriteAllText(Path.Combine(_dir, "huddle.json"),
            "{ \"sessions\": [ { \"name\": \"otherapp\", \"root\": \"" + _repoRoot.Replace("\\", "\\\\") + "\" } ] }");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static WorkLedgerClaim Claim(string session, string guid, params string[] files) =>
        new(session, "otherapp", "A-" + guid[..4], DateTime.UtcNow, "", files, OwnerGuid: guid);

    private string? EnvFor(string guid) => guid;

    private Func<string, string?> Env(string guid) => key => key switch
    {
        "HUDDLE_CLAIMS" => _claimsDir,
        "HUDDLE_INSTANCE" => "otherapp:architect",
        "HUDDLE_REPO" => "otherapp",
        "HUDDLE_GUID" => guid,
        _ => null
    };

    private static string Stdin(string path) =>
        "{\"tool_name\":\"Edit\",\"tool_input\":{\"file_path\":\"" + path.Replace("\\", "\\\\") + "\"}}";

    [Fact]
    public void Two_sessions_sharing_a_name_are_not_the_same_session()
    {
        Assert.False(WorkLedgerClaims.IsSameSession(
            Claim("otherapp:architect", GuidA, "x.cs"), Claim("otherapp:architect", GuidB, "x.cs")));
        Assert.True(WorkLedgerClaims.IsSameSession(
            Claim("otherapp:architect", GuidA, "x.cs"), Claim("otherapp:architect", GuidA, "y.cs")));
    }

    [Fact]
    public void A_legacy_claim_without_a_guid_still_matches_by_name()
    {
        var legacy = new WorkLedgerClaim("otherapp:architect", "otherapp", "A-1", DateTime.UtcNow, "", new[] { "x.cs" });
        Assert.True(WorkLedgerClaims.IsSameSession(legacy, Claim("otherapp:architect", GuidA, "x.cs")));
    }

    [Fact]
    public void A_twins_overlap_is_reported_as_a_conflict_not_as_self_extension()
    {
        var held = Claim("otherapp:architect", GuidB, "otherapp/Program.cs");
        var mine = Claim("otherapp:architect", GuidA, "otherapp/Program.cs");
        var conflicts = WorkLedgerClaims.FindConflictsWithActive(new[] { mine }, new[] { held });
        var c = Assert.Single(conflicts);
        Assert.Equal("otherapp/Program.cs", Assert.Single(c.SharedFiles));
    }

    [Fact]
    public void The_edit_guard_blocks_a_twins_file_and_says_which_session_holds_it()
    {
        new WorkLedgerClaims(_claimsDir, _ => { }).Write(
            Claim("otherapp:architect", GuidB, "otherapp/Program.cs") with { Root = _repoRoot });
        var rc = LedgerCommands.RunClaimCheck(
            Stdin(Path.Combine(_repoRoot, "otherapp", "Program.cs")), Env(GuidA), _out.Add);
        Assert.Equal(LedgerCommands.Block, rc);
        var text = string.Join("\n", _out);
        Assert.Contains("ANOTHER SESSION ALSO CALLED otherapp:architect", text);
        Assert.Contains(GuidB[..8], text);
        Assert.Contains(GuidA[..8], text);
    }

    [Fact]
    public void My_own_claim_still_allows_my_edit()
    {
        new WorkLedgerClaims(_claimsDir, _ => { }).Write(
            Claim("otherapp:architect", GuidA, "otherapp/Program.cs") with { Root = _repoRoot });
        Assert.Equal(0, LedgerCommands.RunClaimCheck(
            Stdin(Path.Combine(_repoRoot, "otherapp", "Program.cs")), Env(GuidA), _out.Add));
    }

    [Fact]
    public void A_session_cannot_release_its_twins_claim()
    {
        var claims = new WorkLedgerClaims(_claimsDir, _ => { });
        claims.Write(Claim("otherapp:architect", GuidB, "otherapp/Program.cs"));
        Assert.Equal(0, claims.Release("otherapp:architect", new[] { "otherapp/Program.cs" }, GuidA));
        Assert.Single(claims.ReadAll());
        Assert.Equal(1, claims.Release("otherapp:architect", new[] { "otherapp/Program.cs" }, GuidB));
        Assert.Empty(claims.ReadAll());
    }

    [Fact]
    public void Release_without_a_guid_keeps_the_old_by_name_behaviour()
    {
        var claims = new WorkLedgerClaims(_claimsDir, _ => { });
        claims.Write(Claim("otherapp:architect", GuidB, "otherapp/Program.cs"));
        Assert.Equal(1, claims.Release("otherapp:architect", new[] { "otherapp/Program.cs" }));
        Assert.Empty(claims.ReadAll());
    }
}
