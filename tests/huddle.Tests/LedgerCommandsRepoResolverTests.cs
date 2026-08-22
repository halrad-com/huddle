using Huddle;
using Xunit;

namespace HuddleTests;

/// <summary>
/// I013, CLI half: `huddle --claim` runs the binary with no config loaded, so before this
/// it compared repo NAMES and could not see a holder of the same physical file registered
/// under a nested repo name. The CLI now reads huddle.json from disk — derived from
/// HUDDLE_CLAIMS, which IpcManager builds as &lt;configDir&gt;/ipc/workledger/claims — and
/// injects the same name → root resolver the orchestrator does.
///
/// Every test here also pins the other half of the contract: a config that is missing,
/// unreadable, or malformed must cost the agent NOTHING. The claim always lands.
/// </summary>
public class LedgerCommandsRepoResolverTests : IDisposable
{
    private readonly string _configDir;
    private readonly string _claimsDir;
    private readonly List<string> _out = new();

    public LedgerCommandsRepoResolverTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "huddle-cliresolver-" + Guid.NewGuid().ToString("N"));
        _claimsDir = Path.Combine(_configDir, "ipc", "workledger", "claims");
        Directory.CreateDirectory(_claimsDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_configDir, recursive: true); } catch { }
    }

    private string Text => string.Join("\n", _out);

    private string? Env(string key) => key switch
    {
        "HUDDLE_CLAIMS" => _claimsDir,
        "HUDDLE_INSTANCE" => "myapp:frontenddev",
        "HUDDLE_REPO" => "myapp",
        "HUDDLE_GUID" => "11111111-1111-1111-1111-111111111111",
        _ => null
    };

    private void WriteConfig(string json) =>
        File.WriteAllText(Path.Combine(_configDir, "huddle.json"), json);

    /// <summary>Nested roots, exactly as the live registry has them.</summary>
    private void WriteNestedConfig(string? aliasesForMbxRoot = null)
    {
        var LIB = Path.Combine(_configDir, "src", "LIB").Replace("\\", "\\\\");
        var app = Path.Combine(_configDir, "src", "LIB", "myapp").Replace("\\", "\\\\");
        var aliases = aliasesForMbxRoot == null ? "" : $", \"aliases\": [{aliasesForMbxRoot}]";
        WriteConfig($$"""
        {
          "sessions": [
            { "name": "myapp", "root": "{{app}}" },
            { "name": "LIB-root", "root": "{{LIB}}"{{aliases}} }
          ]
        }
        """);
    }

    private void HoldFile(string session, string repo, string file) =>
        new WorkLedgerClaims(_claimsDir, _ => { }).Write(
            new WorkLedgerClaim(session, repo, "A-0",
                new DateTime(2026, 8, 16, 8, 0, 0, DateTimeKind.Utc), "", new[] { file }));

    [Fact]
    public void RunClaim_WarnsAboutAHolderRegisteredUnderANestedRepoName()
    {
        // The 2026-08-16 incident, through the CLI door: one physical file
        // (<LIB>/myapp/MBXS/x.cs) spelled two legitimate ways.
        WriteNestedConfig();
        HoldFile("LIB-root:architect", "LIB-root", "myapp/MBXS/x.cs");

        var code = LedgerCommands.RunClaim(new[] { "MBXS/x.cs" }, Env, _out.Add);

        Assert.Equal(0, code);
        Assert.Contains("ALSO HELD BY", Text);
        Assert.Contains("LIB-root:architect", Text);
    }

    [Fact]
    public void RunClaim_FoldsAnAliasTheSameWayTheOrchestratorDoes()
    {
        // A holder that declared the alias must resolve to the canonical repo's root, or
        // the CLI and the orchestrator disagree about what a repo name means.
        WriteNestedConfig(aliasesForMbxRoot: "\"LIB\"");
        HoldFile("LIB-root:architect", "LIB", "myapp/MBXS/x.cs");

        LedgerCommands.RunClaim(new[] { "MBXS/x.cs" }, Env, _out.Add);

        Assert.Contains("ALSO HELD BY", Text);
    }

    [Fact]
    public void RunClaim_KeepsDisjointReposApart()
    {
        // I008 must not regress: huddle's README.md may not block corelib's.
        WriteConfig($$"""
        {
          "sessions": [
            { "name": "myapp", "root": "{{Path.Combine(_configDir, "a").Replace("\\", "\\\\")}}" },
            { "name": "corelib", "root": "{{Path.Combine(_configDir, "b").Replace("\\", "\\\\")}}" }
          ]
        }
        """);
        HoldFile("corelib:architect", "corelib", "README.md");

        var code = LedgerCommands.RunClaim(new[] { "README.md" }, Env, _out.Add);

        Assert.Equal(0, code);
        Assert.DoesNotContain("ALSO HELD BY", Text);
    }

    [Fact]
    public void RunClaim_WithNoConfigOnDisk_StillRecordsAndStillUsesTheNameGate()
    {
        // Absence of huddle.json is normal (the CLI exists for when huddle never started).
        HoldFile("myapp:architect", "myapp", "MBXS/x.cs");

        var code = LedgerCommands.RunClaim(new[] { "MBXS/x.cs" }, Env, _out.Add);

        Assert.Equal(0, code);
        Assert.DoesNotContain("FAILED", Text);
        Assert.Contains("ALSO HELD BY", Text); // same repo NAME: pre-I013 gate still holds
        Assert.Single(new WorkLedgerClaims(_claimsDir, _ => { }).ReadAll(),
            c => c.SessionId == "myapp:frontenddev");
    }

    [Fact]
    public void RunClaim_WithAMalformedConfig_NeverCostsTheAgentTheirClaim()
    {
        // A config problem is the operator's problem, never the claimant's: a throw here
        // would be a silent refusal in the one path that has no arbiter to fall back on.
        WriteConfig("{ this is not json");
        HoldFile("myapp:architect", "myapp", "MBXS/x.cs");

        var code = LedgerCommands.RunClaim(new[] { "MBXS/x.cs" }, Env, _out.Add);

        Assert.Equal(0, code);
        Assert.DoesNotContain("FAILED", Text);
        Assert.DoesNotContain("Exception", Text);
        Assert.Contains("ALSO HELD BY", Text); // degraded to the name gate, not to silence
    }

    [Fact]
    public void RunClaim_WithAConfigThatHasNoSessions_DegradesQuietly()
    {
        WriteConfig("{ \"sessions\": [] }");

        var code = LedgerCommands.RunClaim(new[] { "MBXS/x.cs" }, Env, _out.Add);

        Assert.Equal(0, code);
        Assert.DoesNotContain("FAILED", Text);
    }

    [Fact]
    public void BuildRepoResolver_MapsNamesAndAliasesToRootsAndUnknownsToNull()
    {
        WriteNestedConfig(aliasesForMbxRoot: "\"LIB\"");

        var resolve = LedgerCommands.BuildRepoResolver(_claimsDir);

        Assert.NotNull(resolve);
        Assert.Equal(Path.Combine(_configDir, "src", "LIB", "myapp"), resolve!("myapp"));
        Assert.Equal(Path.Combine(_configDir, "src", "LIB"), resolve("LIB-root"));
        Assert.Equal(Path.Combine(_configDir, "src", "LIB"), resolve("LIB"));      // alias
        Assert.Null(resolve("no-such-repo"));
        Assert.Null(resolve(""));
    }

    [Fact]
    public void BuildRepoResolver_IsCaseInsensitive_LikeTheSessionManagerRegistry()
    {
        WriteNestedConfig(aliasesForMbxRoot: "\"LIB\"");

        var resolve = LedgerCommands.BuildRepoResolver(_claimsDir)!;

        Assert.Equal(Path.Combine(_configDir, "src", "LIB"), resolve("LIB-ROOT"));
        Assert.Equal(Path.Combine(_configDir, "src", "LIB"), resolve("LIB"));
    }

    [Fact]
    public void BuildRepoResolver_PrefersARepoNameOverAnAliasOfTheSameSpelling()
    {
        // ResolveRepoName checks repos before aliases; the CLI must agree or one claim
        // resolves to a root the orchestrator would never pick.
        var a = Path.Combine(_configDir, "a").Replace("\\", "\\\\");
        var b = Path.Combine(_configDir, "b").Replace("\\", "\\\\");
        WriteConfig($$"""
        {
          "sessions": [
            { "name": "alpha", "root": "{{a}}", "aliases": ["beta"] },
            { "name": "beta", "root": "{{b}}" }
          ]
        }
        """);

        var resolve = LedgerCommands.BuildRepoResolver(_claimsDir)!;

        Assert.Equal(Path.Combine(_configDir, "b"), resolve("beta"));
    }

    [Fact]
    public void BuildRepoResolver_AlsoAcceptsTheLegacySeatbeltJsonName()
    {
        // Program.cs still falls back to myapp.json for un-renamed installs; the CLI
        // must find the same file the orchestrator would, or the two disagree about roots.
        var app = Path.Combine(_configDir, "src", "LIB", "myapp").Replace("\\", "\\\\");
        File.WriteAllText(Path.Combine(_configDir, "myapp.json"),
            $"{{ \"sessions\": [ {{ \"name\": \"myapp\", \"root\": \"{app}\" }} ] }}");

        var resolve = LedgerCommands.BuildRepoResolver(_claimsDir)!;

        Assert.Equal(Path.Combine(_configDir, "src", "LIB", "myapp"), resolve("myapp"));
    }

    [Fact]
    public void BuildRepoResolver_ReturnsNullWhenThereIsNoConfigToRead()
    {
        Assert.Null(LedgerCommands.BuildRepoResolver(_claimsDir));
    }

    [Fact]
    public void BuildRepoResolver_ReturnsNullForAClaimsDirThatIsNotUnderAConfigDir()
    {
        // No huddle.json three levels up: not an error, just no roots to resolve.
        Assert.Null(LedgerCommands.BuildRepoResolver(Path.Combine(_configDir, "loose")));
    }
}
