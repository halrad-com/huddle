using Huddle;
using Xunit;

namespace HuddleTests;

/// <summary>
/// ISSUES.md I013: registered repo roots are NESTED (`myapp` lives inside `LIB-root`
/// lives inside `workspace`), so one physical file has several legitimate repo-relative
/// spellings. Comparing repo NAMES made the arbiter treat those spellings as unrelated
/// files and hand a false all-clear to two agents editing the same file. Collision is
/// therefore decided on RESOLVED ABSOLUTE PATHS.
///
/// The same tests pin the two things this must not break:
/// I008 — disjoint repos must NOT collide (huddle's README.md never blocks corelib's), and
/// worktrees — the same repo-relative path under a different root is a different file.
/// And the fail-safe: anything the resolver cannot answer for falls back to the old
/// name comparison, where an empty repo still collides with everything (I005).
/// </summary>
public class WorkLedgerClaimsRepoScopeTests : IDisposable
{
    private readonly string _dir;

    // The live I013 topology, rooted under a temp dir so nothing depends on this machine.
    private readonly string _reposRoot;   // workspace
    private readonly string _mbxRoot;     // LIB-root      = workspace/LIB
    private readonly string _myapp;  // myapp    = workspace/LIB/myapp
    private readonly string _FEATURE;   // LIB-FEATURE = workspace/LIB-FEATURE (worktree)
    private readonly string _huddleRoot;  // huddle        = workspace/myapp
    private readonly string _corelibRoot;  // corelib        = workspace/corelib

    public WorkLedgerClaimsRepoScopeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "huddle-reposcope-" + Guid.NewGuid().ToString("N"));
        _reposRoot = Path.Combine(_dir, "repos");
        _mbxRoot = Path.Combine(_reposRoot, "LIB");
        _myapp = Path.Combine(_mbxRoot, "myapp");
        _FEATURE = Path.Combine(_reposRoot, "LIB-FEATURE");
        _huddleRoot = Path.Combine(_reposRoot, "myapp");
        _corelibRoot = Path.Combine(_reposRoot, "corelib");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>The orchestrator's registry, standing in for SessionManager.Repos.</summary>
    private string? Resolve(string repo) => repo switch
    {
        "workspace" => _reposRoot,
        "LIB-root" => _mbxRoot,
        "myapp" => _myapp,
        "FEATURE" => _FEATURE,
        "huddle" => _huddleRoot,
        "corelib" => _corelibRoot,
        _ => null, // unknown name — the resolver says so rather than guessing
    };

    // The batch id reaches the claim FILENAME, so it must stay filename-legal: a raw
    // "repo:persona" would make Windows treat the colon as an alternate data stream and
    // the claim would vanish from ReadAll.
    private static WorkLedgerClaim Claim(string session, string repo, params string[] files) =>
        new(session, repo, "B-" + session.Replace(':', '-'), DateTime.UtcNow, "abc123", files);

    private List<ClaimOverlap> Conflicts(WorkLedgerClaim proposed, params WorkLedgerClaim[] active) =>
        WorkLedgerClaims.FindConflictsWithActive(new[] { proposed }, active, Resolve);

    // ---- the live defect -------------------------------------------------

    [Fact]
    public void NestedRoots_SameFileUnderDifferentRepoNames_Collides()
    {
        // Exactly the incident: workspace:architect held it via LIB-root, myapp:architect-2
        // via myapp. Same physical file; both were told it was free.
        var viaMbxRoot = Claim("workspace:architect", "LIB-root",
            "myapp/MBXS/corelib.Shell/ShellConfig.cs");
        var viamyapp = Claim("myapp:architect-2", "myapp",
            "MBXS/corelib.Shell/ShellConfig.cs");

        var conflicts = Conflicts(viamyapp, viaMbxRoot);

        var overlap = Assert.Single(conflicts);
        Assert.Equal("workspace:architect", overlap.B.SessionId);
        Assert.Equal("myapp/MBXS/corelib.Shell/ShellConfig.cs", Assert.Single(overlap.SharedFiles));
    }

    [Fact]
    public void NestedRoots_CollideInEitherDirection()
    {
        var viaMbxRoot = Claim("a:one", "LIB-root", "myapp/MBXS/corelib.Shell/Program.cs");
        var viamyapp = Claim("b:two", "myapp", "MBXS/corelib.Shell/Program.cs");

        Assert.Single(Conflicts(viaMbxRoot, viamyapp));
        Assert.Single(Conflicts(viamyapp, viaMbxRoot));
    }

    [Fact]
    public void WorkspaceRootContainingAnotherRepo_Collides()
    {
        // `workspace` contains EVERY repo, so a workspace-scoped claim was invisible to
        // every other session and vice versa — the widest form of the same hole.
        var viaWorkspace = Claim("workspace:architect", "workspace",
            "LIB/myapp/MBXS/corelib.Shell/ShellConfig.cs");
        var viamyapp = Claim("myapp:architect-2", "myapp",
            "MBXS/corelib.Shell/ShellConfig.cs");

        Assert.Single(Conflicts(viamyapp, viaWorkspace));
    }

    [Fact]
    public void FindOverlaps_IsPathAware_ForNestedRoots()
    {
        // The self-overlap check inside a proposed batch (Orchestrator step 1) must see it too.
        var overlaps = WorkLedgerClaims.FindOverlaps(new[]
        {
            Claim("a:one", "LIB-root", "myapp/MBXS/corelib.Shell/ShellConfig.cs"),
            Claim("b:two", "myapp", "MBXS/corelib.Shell/ShellConfig.cs"),
        }, Resolve);

        Assert.Single(overlaps);
    }

    // ---- I008 regression guard: disjoint repos must NOT collide ----------

    [Fact]
    public void DisjointRepos_SameRelativePath_DoNotCollide()
    {
        // I008 verbatim: huddle's README.md must not block corelib's README.md.
        var huddle = Claim("huddle:documenter", "huddle", "README.md");
        var corelib = Claim("corelib:documenter", "corelib", "README.md");

        Assert.Empty(Conflicts(corelib, huddle));
        Assert.Empty(WorkLedgerClaims.FindOverlaps(new[] { huddle, corelib }, Resolve));
    }

    [Fact]
    public void NestedRoots_DifferentFiles_DoNotCollide()
    {
        // Nesting alone is not a collision — only the same resolved file is.
        var viaMbxRoot = Claim("a:one", "LIB-root", "myapp/MBXS/corelib.Shell/ShellConfig.cs");
        var viamyapp = Claim("b:two", "myapp", "MBXS/corelib.Shell/Program.cs");

        Assert.Empty(Conflicts(viamyapp, viaMbxRoot));
    }

    // ---- worktrees -------------------------------------------------------

    [Fact]
    public void SeparateWorktrees_SameRelativePath_DoNotCollide()
    {
        // LIB-FEATURE is a separate worktree with its own root: the same repo-relative
        // path is a genuinely different file on disk and must stay independently claimable.
        var trunk = Claim("LIB:backenddev", "LIB-root", "MBXS/corelib.Shell/ShellConfig.cs");
        var worktree = Claim("FEATURE:backenddev", "FEATURE", "MBXS/corelib.Shell/ShellConfig.cs");

        Assert.Empty(Conflicts(worktree, trunk));
    }

    // ---- fail-safe fallback ---------------------------------------------

    [Fact]
    public void UnresolvableRepoName_StillCollidesWithEverything()
    {
        // The resolver cannot place `mystery`, so the pair drops to the pre-I013 name
        // comparison. It must NOT quietly decide "different repo, no conflict".
        var unknown = Claim("x:one", "", "src/a.cs");
        var known = Claim("y:two", "myapp", "src/a.cs");

        Assert.Single(Conflicts(known, unknown));
        Assert.Single(Conflicts(unknown, known));
    }

    [Fact]
    public void EmptyRepo_StillCollidesWithEverything()
    {
        // A legacy claim (written before Repo was recorded) must never lose its I005
        // protection: it is the wildcard and collides with every repo.
        var legacy = Claim("legacy:one", "", "docs/plan.md");

        Assert.Single(Conflicts(Claim("y:two", "huddle", "docs/plan.md"), legacy));
        Assert.Single(Conflicts(Claim("y:two", "corelib", "docs/plan.md"), legacy));
        Assert.Single(Conflicts(Claim("y:two", "", "docs/plan.md"), legacy));
    }

    [Fact]
    public void UnknownButEqualRepoNames_StillCollide()
    {
        // Two claims in a repo the resolver has never heard of (a synthetic name, or a
        // repo unregistered since the claim was written) keep the old name-equality rule.
        var a = Claim("x:one", "mystery", "src/a.cs");
        var b = Claim("y:two", "mystery", "src/a.cs");

        Assert.Single(Conflicts(b, a));
    }

    [Fact]
    public void UnknownDifferentRepoNames_DoNotCollide()
    {
        // Fallback is the OLD behaviour, not "collide with everything": two unresolvable
        // but differently-named repos stay scoped, so the CLI path (which has no resolver)
        // does not regress I008.
        var a = Claim("x:one", "mystery", "README.md");
        var b = Claim("y:two", "enigma", "README.md");

        Assert.Empty(Conflicts(b, a));
    }

    [Fact]
    public void NoResolverInjected_BehavesExactlyAsBefore()
    {
        // Every existing construction site (and the `huddle --claim` CLI) passes no
        // resolver; those callers must see the pre-I013 semantics untouched.
        var viaMbxRoot = Claim("a:one", "LIB-root", "myapp/MBXS/corelib.Shell/ShellConfig.cs");
        var viamyapp = Claim("b:two", "myapp", "MBXS/corelib.Shell/ShellConfig.cs");
        var huddle = Claim("huddle:documenter", "huddle", "README.md");
        var corelib = Claim("corelib:documenter", "corelib", "README.md");
        var legacy = Claim("legacy:one", "", "README.md");

        Assert.Empty(WorkLedgerClaims.FindConflictsWithActive(new[] { viamyapp }, new[] { viaMbxRoot }));
        Assert.Empty(WorkLedgerClaims.FindConflictsWithActive(new[] { corelib }, new[] { huddle }));
        Assert.Single(WorkLedgerClaims.FindConflictsWithActive(new[] { corelib }, new[] { legacy }));
    }

    // ---- spelling of the same absolute path ------------------------------

    [Fact]
    public void SeparatorAndCaseVariants_OfTheSameAbsolutePath_Collide()
    {
        // Windows: `MBXS\corelib.Shell\ShellConfig.cs`, `./MBXS/corelib.Shell/ShellConfig.cs`
        // and a differently-cased spelling are one file and must not be split apart.
        var backslashes = Claim("a:one", "myapp", @"MBXS\corelib.Shell\ShellConfig.cs");
        var dotSlash = Claim("b:two", "myapp", "./MBXS/corelib.Shell/ShellConfig.cs");
        var mixedCase = Claim("c:three", "myapp", "mbxs/corelib.shell/shellconfig.cs");

        Assert.Single(Conflicts(dotSlash, backslashes));
        Assert.Single(Conflicts(mixedCase, backslashes));
    }

    [Fact]
    public void RootSpellingVariants_ResolveToTheSameRepo()
    {
        // A registry entry spelled with a trailing separator, a forward slash, or a
        // redundant "." segment is the same root and must not fork the comparison.
        string? Wobbly(string repo) => repo switch
        {
            "myapp" => _myapp,
            "myapp-alt" => Path.Combine(_mbxRoot, ".", "myapp") + Path.DirectorySeparatorChar,
            _ => null,
        };

        var a = Claim("a:one", "myapp", "src/a.cs");
        var b = Claim("b:two", "myapp-alt", "src/a.cs");

        Assert.Single(WorkLedgerClaims.FindConflictsWithActive(new[] { b }, new[] { a }, Wobbly));
    }

    // ---- end to end through the arbiter ----------------------------------

    [Fact]
    public void TryClaim_RefusesTheSecondHolderOfANestedPath()
    {
        var claims = new WorkLedgerClaims(_dir, _ => { }, Resolve);

        Assert.True(claims.TryClaim(
            Claim("workspace:architect", "LIB-root", "myapp/MBXS/corelib.Shell/ShellConfig.cs"),
            out _));

        var ok = claims.TryClaim(
            Claim("myapp:architect-2", "myapp", "MBXS/corelib.Shell/ShellConfig.cs"),
            out var conflicts);

        Assert.False(ok);
        Assert.Equal("workspace:architect", Assert.Single(conflicts).B.SessionId);
        Assert.Single(claims.ReadAll()); // loser's claim was not written
    }

    [Fact]
    public void RecordWithOverlaps_ReportsTheNestedHolder()
    {
        var claims = new WorkLedgerClaims(_dir, _ => { }, Resolve);
        claims.Write(Claim("workspace:architect", "workspace",
            "LIB/myapp/MBXS/corelib.Shell/Program.cs"));

        claims.RecordWithOverlaps(
            Claim("myapp:architect-2", "myapp", "MBXS/corelib.Shell/Program.cs"),
            out var overlaps);

        // Always recorded, never refused — but the claimant is now told who else holds it.
        Assert.Equal(2, claims.ReadAll().Count);
        Assert.Equal("workspace:architect", Assert.Single(overlaps).B.SessionId);
    }
}
