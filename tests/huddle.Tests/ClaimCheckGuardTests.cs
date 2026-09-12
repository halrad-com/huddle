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

    private FileCatalog Catalog() => new(FileCatalog.DirBesideClaims(_claimsDir));

    // The protection the roster-based path could not give. A borrower huddle never spawned has
    // no live instance to match, so IsOrphan read its claim as dead and ReapOrphans archived it
    // — the next agent was waved through onto a file somebody was editing. A catalog checkout
    // has no roster to be absent from.
    [Fact]
    public void Checkout_by_an_agent_huddle_never_spawned_blocks_the_edit()
    {
        Catalog().TryCheckOut("myapp", "MBXH/Core/X.cs", "codex:refactor", out _, null, _rbRoot);

        var rc = LedgerCommands.RunClaimCheck(Stdin(Path.Combine(_rbRoot, "MBXH", "Core", "X.cs")), Env, _err.Add);

        Assert.Equal(LedgerCommands.Block, rc);
        var t = string.Join("\n", _err);
        Assert.Contains("CHECKED OUT to codex:refactor", t);
        Assert.Contains("huddle did not start", t);
    }

    // A checkout is a first-class way in: an agent should not have to learn two mechanisms.
    [Fact]
    public void My_own_checkout_allows_the_edit_without_any_claim()
    {
        Catalog().TryCheckOut("myapp", "MBXH/Core/X.cs", "myapp:architect", out _, null, _rbRoot);

        var rc = LedgerCommands.RunClaimCheck(Stdin(Path.Combine(_rbRoot, "MBXH", "Core", "X.cs")), Env, _err.Add);

        Assert.Equal(0, rc);
        Assert.Empty(_err);
    }

    [Fact]
    public void My_own_checkout_matches_the_underscore_spelling_of_my_name()
    {
        Catalog().TryCheckOut("myapp", "MBXH/Core/X.cs", "myapp_architect", out _, null, _rbRoot);

        Assert.Equal(0, LedgerCommands.RunClaimCheck(Stdin(Path.Combine(_rbRoot, "MBXH", "Core", "X.cs")), Env, _err.Add));
    }

    // An expired lease is not a holder — the borrower stopped renewing, so the book is back on
    // the shelf. The edit falls through to the ordinary claim rule rather than being blocked by
    // a ghost.
    [Fact]
    public void Overdue_checkout_does_not_block_and_falls_through_to_the_claim_rule()
    {
        var expired = new FileCatalog(FileCatalog.DirBesideClaims(_claimsDir),
                                      () => DateTime.UtcNow.AddHours(-3));
        expired.TryCheckOut("myapp", "MBXH/Core/X.cs", "codex:refactor", out _, TimeSpan.FromMinutes(10), _rbRoot);

        var rc = LedgerCommands.RunClaimCheck(Stdin(Path.Combine(_rbRoot, "MBXH", "Core", "X.cs")), Env, _err.Add);

        // Still blocked, but by the CLAIM rule (no claim held), not by the stale checkout.
        Assert.Equal(LedgerCommands.Block, rc);
        var t = string.Join("\n", _err);
        Assert.Contains("holds no claim", t);
        Assert.DoesNotContain("CHECKED OUT to codex:refactor", t);
    }

    [Fact]
    public void Checkout_in_a_different_repo_does_not_block_the_same_path_here()
    {
        Catalog().TryCheckOut("netlib", "MBXH/Core/X.cs", "codex:refactor", out _, null, _faRoot);

        var rc = LedgerCommands.RunClaimCheck(Stdin(Path.Combine(_rbRoot, "MBXH", "Core", "X.cs")), Env, _err.Add);

        Assert.Equal(LedgerCommands.Block, rc);
        Assert.Contains("holds no claim", string.Join("\n", _err));
    }

    // Without renewal a lease is a trap: check out for an hour, work for ninety minutes, and at
    // minute sixty-one another agent may legitimately take the file you are still editing.
    // Every allowed edit is therefore a heartbeat.
    [Fact]
    public void An_allowed_edit_renews_my_own_lease()
    {
        var stale = new FileCatalog(FileCatalog.DirBesideClaims(_claimsDir),
                                    () => DateTime.UtcNow.AddMinutes(-50));
        stale.TryCheckOut("myapp", "MBXH/Core/X.cs", "myapp:architect", out var before,
                          TimeSpan.FromMinutes(60), _rbRoot);

        Assert.Equal(0, LedgerCommands.RunClaimCheck(Stdin(Path.Combine(_rbRoot, "MBXH", "Core", "X.cs")), Env, _err.Add));

        var after = Catalog().Status("myapp", "MBXH/Core/X.cs")!;
        Assert.True(after.DueAt > before!.DueAt,
                    $"lease should have moved out: was {before.DueAt:HH:mm}, now {after.DueAt:HH:mm}");
        Assert.Equal("myapp:architect", after.Borrower);
    }

    // Entitled by a claim but holding no book: the gate has already judged the edit legal, so
    // recording it tells other agents more than silence does — and it is what makes a batch
    // dispatched through the claim path visible to someone reading the catalog.
    [Fact]
    public void An_allowed_edit_under_a_claim_takes_the_book()
    {
        Hold("myapp:architect", "myapp", _rbRoot, "MBXH/Core/X.cs");
        Assert.Null(Catalog().Status("myapp", "MBXH/Core/X.cs"));

        Assert.Equal(0, LedgerCommands.RunClaimCheck(Stdin(Path.Combine(_rbRoot, "MBXH", "Core", "X.cs")), Env, _err.Add));

        var entry = Catalog().Status("myapp", "MBXH/Core/X.cs");
        Assert.NotNull(entry);
        Assert.Equal("myapp:architect", entry!.Borrower);
    }

    // A blocked edit must NOT create an entry — that would hand the blocker's book to the
    // agent that was just refused.
    [Fact]
    public void A_blocked_edit_does_not_take_the_book()
    {
        Catalog().TryCheckOut("myapp", "MBXH/Core/X.cs", "codex:refactor", out _, null, _rbRoot);

        Assert.Equal(LedgerCommands.Block,
            LedgerCommands.RunClaimCheck(Stdin(Path.Combine(_rbRoot, "MBXH", "Core", "X.cs")), Env, _err.Add));

        Assert.Equal("codex:refactor", Catalog().Status("myapp", "MBXH/Core/X.cs")!.Borrower);
    }

    [Fact]
    public void An_edit_blocked_for_having_no_claim_does_not_take_the_book()
    {
        Assert.Equal(LedgerCommands.Block,
            LedgerCommands.RunClaimCheck(Stdin(Path.Combine(_rbRoot, "MBXH", "Core", "X.cs")), Env, _err.Add));

        Assert.Null(Catalog().Status("myapp", "MBXH/Core/X.cs"));
    }

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
