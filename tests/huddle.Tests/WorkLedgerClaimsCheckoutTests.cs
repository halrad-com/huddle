using Huddle;
using Xunit;

namespace HuddleTests;

/// <summary>
/// ISSUES.md I014: a claim recorded a repo NAME and repo-relative paths, and every reader
/// resolved the root from that name. A session working in a git WORKTREE has no correct
/// name to declare — `LIB-FEATURE` is a worktree of `LIB`, not a registered repo — so it
/// declared the closest available name and the ledger recorded a claim pointing at the
/// wrong checkout. Two failures, both live:
///
///   * false negative — two sessions holding the same repo-relative path in two worktrees
///     are a guaranteed merge conflict, and the ledger said nothing at all;
///   * false positive — after I013 resolved roots from the declared name, that mis-declared
///     claim collided with work that was genuinely elsewhere. A ledger that cries wolf gets
///     ignored, and then a real collision goes through.
///
/// The fix records the session's ACTUAL checkout root on the claim and compares those, with
/// a third outcome between "collision" and "clear": a non-blocking merge-conflict warning.
///
/// These tests also stand guard over the two properties this series has twice risked
/// undoing — I008 (different repos, same relative filename) must neither collide NOR warn,
/// and a legacy claim with no Root and no resolvable repo must still collide with
/// everything (I005).
/// </summary>
public class WorkLedgerClaimsCheckoutTests : IDisposable
{
    private readonly string _dir;

    // The live topology, rooted under a temp dir so nothing depends on this machine.
    private readonly string _reposRoot;    // workspace
    private readonly string _mbxRoot;      // LIB-root       = workspace/LIB          (git top)
    private readonly string _myapp;   // myapp     = workspace/LIB/myapp
    private readonly string _FEATURE;    // LIB-FEATURE  = workspace/LIB-FEATURE (git top, worktree)
    private readonly string _FEATURERb;  //                  workspace/LIB-FEATURE/myapp
    private readonly string _huddleRoot;   // huddle         = workspace/myapp      (git top)
    private readonly string _corelibRoot;   // corelib         = workspace/corelib        (git top)

    public WorkLedgerClaimsCheckoutTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "huddle-checkout-" + Guid.NewGuid().ToString("N"));
        _reposRoot = Path.Combine(_dir, "repos");
        _mbxRoot = Path.Combine(_reposRoot, "LIB");
        _myapp = Path.Combine(_mbxRoot, "myapp");
        _FEATURE = Path.Combine(_reposRoot, "LIB-FEATURE");
        _FEATURERb = Path.Combine(_FEATURE, "myapp");
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
        "huddle" => _huddleRoot,
        "corelib" => _corelibRoot,
        _ => null,
    };

    /// <summary>
    /// Stands in for git: worktrees of one repo share an object store, and each worktree has
    /// its own top. LIB and LIB-FEATURE share LIB/.git; myapp and corelib are unrelated.
    /// </summary>
    private CheckoutInfo? Identify(string root)
    {
        var full = Path.GetFullPath(root);
        if (Under(full, _FEATURE)) return new CheckoutInfo(Path.Combine(_mbxRoot, ".git"), _FEATURE);
        if (Under(full, _mbxRoot)) return new CheckoutInfo(Path.Combine(_mbxRoot, ".git"), _mbxRoot);
        if (Under(full, _huddleRoot)) return new CheckoutInfo(Path.Combine(_huddleRoot, ".git"), _huddleRoot);
        if (Under(full, _corelibRoot)) return new CheckoutInfo(Path.Combine(_corelibRoot, ".git"), _corelibRoot);
        return null; // the workspace dir itself is not a checkout
    }

    private static bool Under(string path, string top) =>
        path.Equals(top, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(top + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static WorkLedgerClaim Claim(
        string session, string repo, string root, string branch, params string[] files) =>
        new(session, repo, "B-" + session.Replace(':', '-'), DateTime.UtcNow, "abc123", files,
            OwnerGuid: "", Project: "", Root: root, Branch: branch);

    private List<ClaimOverlap> Conflicts(WorkLedgerClaim proposed, params WorkLedgerClaim[] active) =>
        WorkLedgerClaims.FindConflictsWithActive(new[] { proposed }, active, Resolve);

    private List<ClaimOverlap> Warnings(WorkLedgerClaim proposed, params WorkLedgerClaim[] active) =>
        WorkLedgerClaims.FindMergeWarnings(new[] { proposed }, active, Resolve, Identify);

    // ---- the live defect: a worktree session's claim ----------------------

    [Fact]
    public void Same_path_in_two_worktrees_is_not_a_collision()
    {
        // Both sessions can only spell the repo "myapp" — one of them is in the
        // FEATURE worktree. Before I014 the name resolved both to the master checkout
        // and the ledger reported a collision on files that are not the same file.
        var master = Claim("myapp:architect-2", "myapp", _myapp, "master",
            "MBXS/corelib.Shell/ShellConfig.cs");
        var worktree = Claim("workspace:architect", "myapp", _FEATURERb, "FEATURE",
            "MBXS/corelib.Shell/ShellConfig.cs");

        Assert.Empty(Conflicts(worktree, master));
    }

    [Fact]
    public void Same_path_in_two_worktrees_is_reported_as_a_merge_risk()
    {
        var master = Claim("myapp:architect-2", "myapp", _myapp, "master",
            "MBXS/corelib.Shell/ShellConfig.cs");
        var worktree = Claim("workspace:architect", "myapp", _FEATURERb, "FEATURE",
            "MBXS/corelib.Shell/ShellConfig.cs");

        var warned = Warnings(worktree, master);
        var w = Assert.Single(warned);
        Assert.Equal("workspace:architect", w.A.SessionId);
        Assert.Equal("myapp:architect-2", w.B.SessionId);
        Assert.Equal("MBXS/corelib.Shell/ShellConfig.cs", Assert.Single(w.SharedFiles));
    }

    [Fact]
    public void Mis_declared_repo_name_no_longer_produces_a_false_collision()
    {
        // The incident exactly: workspace:architect was in LIB-FEATURE but had to declare
        // `LIB-root`, so its paths are spelled from the LIB root. The recorded Root is the
        // truth and outranks the name.
        var master = Claim("myapp:architect-2", "myapp", _myapp, "master",
            "MBXS/corelib.Shell/ShellConfig.cs");
        var worktree = Claim("workspace:architect", "LIB-root", _FEATURE, "FEATURE",
            "myapp/MBXS/corelib.Shell/ShellConfig.cs");

        Assert.Empty(Conflicts(worktree, master));
        Assert.Single(Warnings(worktree, master));
    }

    [Fact]
    public void Recorded_root_outranks_the_registry_for_the_same_repo_name()
    {
        // Two sessions in the SAME checkout still collide, Root recorded or not.
        var a = Claim("myapp:architect", "myapp", _myapp, "master", "MBXS/x.cs");
        var b = Claim("myapp:backenddev", "myapp", _myapp, "master", "MBXS/x.cs");

        Assert.Single(Conflicts(a, b));
        Assert.Empty(Warnings(a, b));
    }

    // ---- discrimination: what must NOT warn ------------------------------

    [Fact]
    public void I008_different_repos_sharing_a_relative_path_neither_collide_nor_warn()
    {
        // The regression this series has twice risked: huddle's README.md must never block
        // corelib's, and it must not warn about it either — that would be I008 rebuilt as a
        // warning, which is the same lost signal by a slower route.
        var huddle = Claim("huddle:documenter", "huddle", _huddleRoot, "master", "README.md", "src/Program.cs");
        var corelib = Claim("corelib:documenter", "corelib", _corelibRoot, "main", "README.md", "src/Program.cs");

        Assert.Empty(Conflicts(huddle, corelib));
        Assert.Empty(Warnings(huddle, corelib));
    }

    [Fact]
    public void Two_dirs_inside_one_checkout_do_not_warn()
    {
        // LIB-root and myapp are nested registrations of ONE working copy. `src/Foo.cs`
        // under each is two genuinely different files on one branch — no overwrite, and no
        // merge conflict either. Same object store, same worktree top: not siblings.
        var outer = Claim("LIB:architect", "LIB-root", _mbxRoot, "master", "src/Foo.cs");
        var inner = Claim("myapp:architect", "myapp", _myapp, "master", "src/Foo.cs");

        Assert.Empty(Conflicts(outer, inner));
        Assert.Empty(Warnings(outer, inner));
    }

    [Fact]
    public void I013_nested_spellings_of_one_file_still_collide_with_roots_recorded()
    {
        var outer = Claim("LIB:architect", "LIB-root", _mbxRoot, "master", "myapp/MBXS/x.cs");
        var inner = Claim("myapp:architect", "myapp", _myapp, "master", "MBXS/x.cs");

        Assert.Single(Conflicts(outer, inner));
        Assert.Empty(Warnings(outer, inner));
    }

    // ---- degradation when the field is absent ----------------------------

    [Fact]
    public void Legacy_claim_with_no_root_and_no_resolvable_repo_still_collides_with_everything()
    {
        // I005 protection must never be silently lost: an unrecorded repo is the wildcard.
        var legacy = new WorkLedgerClaim(
            "old:session", "", "B-old", DateTime.UtcNow, "", new[] { "MBXS/x.cs" });
        var modern = Claim("myapp:architect", "myapp", _myapp, "master", "MBXS/x.cs");

        Assert.Single(Conflicts(modern, legacy));
        Assert.Single(Conflicts(legacy, modern));
        // A wildcard claim cannot be placed on disk, so it can never be a *merge* risk —
        // it is already reported as a collision, which is the louder of the two.
        Assert.Empty(Warnings(modern, legacy));
    }

    [Fact]
    public void One_side_missing_its_root_falls_back_to_the_registry()
    {
        // A claim written before I014 (no Root) still resolves through the repo name, so a
        // worktree session is told about it.
        var legacy = new WorkLedgerClaim(
            "myapp:backenddev", "myapp", "B-legacy", DateTime.UtcNow, "", new[] { "MBXS/x.cs" });
        var worktree = Claim("workspace:architect", "myapp", _FEATURERb, "FEATURE", "MBXS/x.cs");

        Assert.Empty(Conflicts(worktree, legacy));
        Assert.Single(Warnings(worktree, legacy));
    }

    [Fact]
    public void With_no_checkout_identity_the_narrow_rule_applies()
    {
        // git unavailable, or neither root is a checkout: warn only when BOTH claims recorded
        // a Root, the roots differ, and the repo NAMES match. Narrow and right beats broad
        // and noisy.
        var master = Claim("myapp:architect-2", "myapp", _myapp, "", "MBXS/x.cs");
        var worktree = Claim("workspace:architect", "myapp", _FEATURERb, "", "MBXS/x.cs");

        Assert.Single(WorkLedgerClaims.FindMergeWarnings(new[] { worktree }, new[] { master }, Resolve));
    }

    [Fact]
    public void With_no_checkout_identity_different_repo_names_never_warn()
    {
        var huddle = Claim("huddle:documenter", "huddle", _huddleRoot, "", "README.md");
        var corelib = Claim("corelib:documenter", "corelib", _corelibRoot, "", "README.md");

        Assert.Empty(WorkLedgerClaims.FindMergeWarnings(new[] { huddle }, new[] { corelib }, Resolve));
    }

    [Fact]
    public void Branch_is_informational_and_never_decides_a_collision()
    {
        // Same checkout, same file, different recorded branch (one claim stamped before a
        // switch): still a collision. A branch string must never talk anyone out of one.
        var a = Claim("myapp:architect", "myapp", _myapp, "master", "MBXS/x.cs");
        var b = Claim("myapp:backenddev", "myapp", _myapp, "feature/x", "MBXS/x.cs");

        Assert.Single(Conflicts(a, b));
        Assert.Empty(Warnings(a, b));
    }

    [Fact]
    public void A_session_never_warns_about_itself()
    {
        var a = Claim("myapp:architect", "myapp", _myapp, "master", "MBXS/x.cs");
        var same = Claim("myapp:architect", "myapp", _FEATURERb, "FEATURE", "MBXS/x.cs");

        Assert.Empty(Warnings(a, same));
    }

    // ---- persistence: the field must survive a round trip ----------------

    [Fact]
    public void Root_and_branch_round_trip_through_the_claim_file()
    {
        var claimsDir = Path.Combine(_dir, "claims");
        var claims = new WorkLedgerClaims(claimsDir, _ => { }, Resolve, Identify);
        var written = claims.Write(Claim(
            "workspace:architect", "myapp", _FEATURERb, "FEATURE", "MBXS/x.cs", "MBXS/y.cs"));

        var read = claims.ReadFile(written);
        Assert.NotNull(read);
        Assert.Equal(_FEATURERb, read!.Root);
        Assert.Equal("FEATURE", read.Branch);
        Assert.Equal(new[] { "MBXS/x.cs", "MBXS/y.cs" }, read.Files);
    }

    [Fact]
    public void A_pre_I014_claim_file_still_parses_with_its_files_intact()
    {
        var claimsDir = Path.Combine(_dir, "claims-legacy");
        Directory.CreateDirectory(claimsDir);
        var path = Path.Combine(claimsDir, "B-legacy-myapp_architect.md");
        File.WriteAllText(path, string.Join("\n", new[]
        {
            "# B-legacy-myapp_architect",
            "",
            "- **Session:** myapp:architect",
            "- **Repo:** myapp",
            "- **Batch:** B-legacy",
            "- **Claimed at:** 2026-08-15T10:00:00Z",
            "- **Base commit:** abc123",
            "- **Owner:** 11111111-1111-1111-1111-111111111111",
            "- **Files:**",
            "  - MBXS/x.cs",
            "  - MBXS/y.cs",
            "",
        }));

        var read = new WorkLedgerClaims(claimsDir, _ => { }).ReadFile(path);
        Assert.NotNull(read);
        Assert.Equal("", read!.Root);
        Assert.Equal("", read.Branch);
        Assert.Equal(new[] { "MBXS/x.cs", "MBXS/y.cs" }, read.Files);
    }

    // ---- a merge risk must never block -----------------------------------

    [Fact]
    public void A_merge_risk_never_refuses_a_claim()
    {
        var claimsDir = Path.Combine(_dir, "claims-grant");
        var claims = new WorkLedgerClaims(claimsDir, _ => { }, Resolve, Identify);

        Assert.True(claims.TryClaim(
            Claim("myapp:architect-2", "myapp", _myapp, "master", "MBXS/x.cs"),
            out _));
        Assert.True(claims.TryClaim(
            Claim("workspace:architect", "myapp", _FEATURERb, "FEATURE", "MBXS/x.cs"),
            out var conflicts));
        Assert.Empty(conflicts);
        Assert.Equal(2, claims.ReadAll().Count);
    }

    // ---- the operator view reports the same third state ------------------

    [Fact]
    public void The_conflicts_view_reports_a_merge_risk_once_per_pair()
    {
        var master = Claim("myapp:architect-2", "myapp", _myapp, "master", "MBXS/x.cs");
        var worktree = Claim("workspace:architect", "myapp", _FEATURERb, "FEATURE", "MBXS/x.cs");

        var risks = ClaimConflictView.FindMergeRisks(new[] { master, worktree }, Resolve, Identify);
        var risk = Assert.Single(risks);
        Assert.Equal("MBXS/x.cs", Assert.Single(risk.Files));
        // And it is NOT reported as a collision by the same view.
        Assert.Empty(ClaimConflictView.Find(new[] { master, worktree }, Resolve));
    }

    [Fact]
    public void The_conflicts_view_never_warns_about_unrelated_repos()
    {
        var huddle = Claim("huddle:documenter", "huddle", _huddleRoot, "master", "README.md");
        var corelib = Claim("corelib:documenter", "corelib", _corelibRoot, "main", "README.md");

        Assert.Empty(ClaimConflictView.FindMergeRisks(new[] { huddle, corelib }, Resolve, Identify));
    }

    // ---- the CLI's view of "where am I" ----------------------------------

    [Fact]
    public void The_cli_prefers_the_exported_root()
    {
        var root = LedgerCommands.ResolveClaimRoot(
            n => n == "HUDDLE_REPO_ROOT" ? _FEATURERb : null,
            () => _myapp);

        Assert.Equal(Path.GetFullPath(_FEATURERb), root);
    }

    [Fact]
    public void The_cli_falls_back_to_a_cwd_that_is_a_checkout_top()
    {
        // A linked worktree's `.git` is a FILE pointing at the real gitdir, not a directory.
        Directory.CreateDirectory(_FEATURE);
        File.WriteAllText(Path.Combine(_FEATURE, ".git"), "gitdir: ../LIB/.git/worktrees/FEATURE\n");

        Assert.Equal(_FEATURE, LedgerCommands.ResolveClaimRoot(_ => null, () => _FEATURE));
    }

    [Fact]
    public void The_cli_records_nothing_rather_than_guess_from_a_non_checkout_cwd()
    {
        // `LIB/myapp` is a registered root INSIDE the LIB checkout. Walking up to the
        // git top would rebase every claimed path onto the wrong directory, so absence is
        // recorded instead and the comparison degrades to the repo registry.
        Directory.CreateDirectory(_myapp);
        Assert.Equal("", LedgerCommands.ResolveClaimRoot(_ => null, () => _myapp));
    }

    [Fact]
    public void Recording_reports_the_merge_risk_to_the_claimant()
    {
        var claimsDir = Path.Combine(_dir, "claims-record");
        var claims = new WorkLedgerClaims(claimsDir, _ => { }, Resolve, Identify);
        claims.Write(Claim("myapp:architect-2", "myapp", _myapp, "master", "MBXS/x.cs"));

        claims.RecordWithOverlaps(
            Claim("workspace:architect", "myapp", _FEATURERb, "FEATURE", "MBXS/x.cs"),
            out var overlaps, out var warnings);

        Assert.Empty(overlaps);
        var w = Assert.Single(warnings);
        Assert.Equal("myapp:architect-2", w.B.SessionId);
        Assert.Equal("master", w.B.Branch);
    }
}
