using Huddle;
using Xunit;

namespace HuddleTests;

/// <summary>
/// ISSUES.md I013, last clause: the `conflicts` verb decided overlaps on raw file strings, so
/// the OPERATOR-facing view of the ledger could disagree with the arbiter — reporting "no
/// conflicts" on a pair the arbiter would refuse. These tests pin the decision half of the
/// verb (<see cref="ClaimConflictView"/>): the same collisions the arbiter finds, plus enough
/// explanation that a cross-spelling collision does not read as a bug in huddle.
///
/// Rendering (colours, layout) is not covered — ConsoleUI has no seam by existing convention.
/// </summary>
public class ClaimConflictViewTests
{
    // The live I013 topology: nested roots, one worktree, two disjoint repos.
    private const string Repos = @"C:\repos";
    private const string LIB = @"C:\repos\LIB";
    private const string myapp = @"C:\repos\LIB\myapp";
    private const string FEATURE = @"C:\repos\LIB-FEATURE";
    private const string HuddleRoot = @"C:\repos\myapp";
    private const string corelib = @"C:\repos\corelib";

    private static string? Resolve(string repo) => repo switch
    {
        "workspace" => Repos,
        "LIB-root" => LIB,
        "myapp" => myapp,
        "FEATURE" => FEATURE,
        "huddle" => HuddleRoot,
        "corelib" => corelib,
        _ => null,
    };

    private static WorkLedgerClaim Claim(string session, string repo, params string[] files) =>
        new(session, repo, "B-" + session.Replace(':', '-'), DateTime.UtcNow, "abc123", files);

    // ---- the gap being closed --------------------------------------------

    [Fact]
    public void NestedRoots_SameFileUnderDifferentRepoNames_IsReportedAsACollision()
    {
        // The incident verbatim: raw-string grouping saw two unrelated files and said nothing.
        var viaMbxRoot = Claim("workspace:architect", "LIB-root",
            "myapp/MBXS/corelib.Shell/ShellConfig.cs");
        var viamyapp = Claim("myapp:architect-2", "myapp",
            "MBXS/corelib.Shell/ShellConfig.cs");

        var collision = Assert.Single(ClaimConflictView.Find(new[] { viaMbxRoot, viamyapp }, Resolve));
        var file = Assert.Single(collision.Files);

        Assert.Equal("myapp/MBXS/corelib.Shell/ShellConfig.cs", file.SpellingA);
        Assert.Equal("MBXS/corelib.Shell/ShellConfig.cs", file.SpellingB);
        Assert.True(file.CrossSpelling);
        Assert.Equal(Path.GetFullPath(@"C:\repos\LIB\myapp\MBXS\corelib.Shell\ShellConfig.cs"),
            file.ResolvedPath);
    }

    [Fact]
    public void WorkspaceScopedClaim_CollidesAndNamesTheResolvedFile()
    {
        // `workspace` contains every repo — the widest form of the same hole.
        var viaWorkspace = Claim("workspace:architect", "workspace",
            "LIB/myapp/MBXS/corelib.Shell/Program.cs");
        var viamyapp = Claim("myapp:architect-2", "myapp",
            "MBXS/corelib.Shell/Program.cs");

        var collision = Assert.Single(ClaimConflictView.Find(new[] { viaWorkspace, viamyapp }, Resolve));
        var file = Assert.Single(collision.Files);

        Assert.True(file.CrossSpelling);
        Assert.Equal(Path.GetFullPath(@"C:\repos\LIB\myapp\MBXS\corelib.Shell\Program.cs"),
            file.ResolvedPath);
    }

    [Fact]
    public void SameRepoSameSpelling_IsACollisionButNotCrossSpelling()
    {
        // The ordinary case: nothing to explain, so nothing extra is claimed about it.
        var a = Claim("a:one", "myapp", "MBXS/x.cs");
        var b = Claim("b:two", "myapp", "MBXS/x.cs");

        var file = Assert.Single(Assert.Single(ClaimConflictView.Find(new[] { a, b }, Resolve)).Files);

        Assert.False(file.CrossSpelling);
        Assert.Equal(file.SpellingA, file.SpellingB);
        Assert.Equal(Path.GetFullPath(@"C:\repos\LIB\myapp\MBXS\x.cs"), file.ResolvedPath);
    }

    [Fact]
    public void SeparatorVariantsOfOnePath_AreFlaggedAsTwoSpellings()
    {
        var backslashes = Claim("a:one", "myapp", @"MBXS\corelib.Shell\ShellConfig.cs");
        var dotSlash = Claim("b:two", "myapp", "./MBXS/corelib.Shell/ShellConfig.cs");

        var file = Assert.Single(Assert.Single(
            ClaimConflictView.Find(new[] { backslashes, dotSlash }, Resolve)).Files);

        Assert.True(file.CrossSpelling);
        Assert.Equal(@"MBXS\corelib.Shell\ShellConfig.cs", file.SpellingA);
        Assert.Equal("./MBXS/corelib.Shell/ShellConfig.cs", file.SpellingB);
    }

    [Fact]
    public void EveryCollidingFileOfAPairIsReported()
    {
        var viaMbxRoot = Claim("workspace:architect", "LIB-root",
            "myapp/MBXS/A.cs", "myapp/MBXS/B.cs", "myapp/MBXS/Untouched.cs");
        var viamyapp = Claim("myapp:architect-2", "myapp",
            "MBXS/A.cs", "MBXS/B.cs");

        var collision = Assert.Single(ClaimConflictView.Find(new[] { viaMbxRoot, viamyapp }, Resolve));

        Assert.Equal(2, collision.Files.Count);
        Assert.All(collision.Files, f => Assert.True(f.CrossSpelling));
        Assert.Contains(collision.Files, f => f.SpellingB == "MBXS/A.cs");
        Assert.Contains(collision.Files, f => f.SpellingB == "MBXS/B.cs");
    }

    // ---- must not start crying wolf --------------------------------------

    [Fact]
    public void DisjointRepos_SameRelativePath_AreNotReported()
    {
        // I008: huddle's README.md must not be shown as blocking corelib's.
        Assert.Empty(ClaimConflictView.Find(new[]
        {
            Claim("huddle:documenter", "huddle", "README.md"),
            Claim("corelib:documenter", "corelib", "README.md"),
        }, Resolve));
    }

    [Fact]
    public void SeparateWorktrees_SameRelativePath_AreNotReported()
    {
        Assert.Empty(ClaimConflictView.Find(new[]
        {
            Claim("LIB:backenddev", "LIB-root", "MBXS/corelib.Shell/ShellConfig.cs"),
            Claim("FEATURE:backenddev", "FEATURE", "MBXS/corelib.Shell/ShellConfig.cs"),
        }, Resolve));
    }

    [Fact]
    public void NestedRoots_DifferentFiles_AreNotReported()
    {
        Assert.Empty(ClaimConflictView.Find(new[]
        {
            Claim("a:one", "LIB-root", "myapp/MBXS/ShellConfig.cs"),
            Claim("b:two", "myapp", "MBXS/Program.cs"),
        }, Resolve));
    }

    // ---- degradation: the verb must never fail the operator --------------

    [Fact]
    public void NoResolver_FallsBackToThePreI013NameComparison()
    {
        // Exactly what the verb did before: name-scoped, nothing resolved. Not a crash,
        // not a blank report — the old answer.
        var viaMbxRoot = Claim("a:one", "LIB-root", "myapp/MBXS/x.cs");
        var viamyapp = Claim("b:two", "myapp", "MBXS/x.cs");
        var sameRepo = Claim("c:three", "myapp", "MBXS/x.cs");

        Assert.Empty(ClaimConflictView.Find(new[] { viaMbxRoot, viamyapp }, resolveRoot: null));

        var file = Assert.Single(Assert.Single(
            ClaimConflictView.Find(new[] { viamyapp, sameRepo }, resolveRoot: null)).Files);
        Assert.Null(file.ResolvedPath);
        Assert.False(file.CrossSpelling);
    }

    [Fact]
    public void ThrowingResolver_DegradesInsteadOfFailing()
    {
        static string? Boom(string repo) => throw new InvalidOperationException("registry is gone");

        var a = Claim("a:one", "myapp", "MBXS/x.cs");
        var b = Claim("b:two", "myapp", "MBXS/x.cs");

        var file = Assert.Single(Assert.Single(ClaimConflictView.Find(new[] { a, b }, Boom)).Files);
        Assert.Null(file.ResolvedPath);
    }

    [Fact]
    public void LegacyClaimWithNoRepo_StillCollidesAndBorrowsThePartnersPath()
    {
        // I005 protection: an empty repo is the wildcard. It cannot resolve a path of its
        // own, so the report shows the path its partner resolves to rather than nothing.
        var legacy = Claim("legacy:one", "", "MBXS/x.cs");
        var known = Claim("y:two", "myapp", "MBXS/x.cs");

        var file = Assert.Single(Assert.Single(ClaimConflictView.Find(new[] { legacy, known }, Resolve)).Files);

        Assert.Equal(Path.GetFullPath(@"C:\repos\LIB\myapp\MBXS\x.cs"), file.ResolvedPath);
    }

    [Fact]
    public void NoClaims_AndSingleClaim_ReportNothing()
    {
        Assert.Empty(ClaimConflictView.Find(Array.Empty<WorkLedgerClaim>(), Resolve));
        Assert.Empty(ClaimConflictView.Find(new[] { Claim("a:one", "myapp", "MBXS/x.cs") }, Resolve));
    }

    // ---- agreement with the arbiter is the whole point -------------------

    [Fact]
    public void TheViewFindsExactlyThePairsTheArbiterWouldRefuse()
    {
        var claims = new[]
        {
            Claim("workspace:architect", "LIB-root", "myapp/MBXS/ShellConfig.cs"),
            Claim("myapp:architect-2", "myapp", "MBXS/ShellConfig.cs"),
            Claim("huddle:documenter", "huddle", "README.md"),
            Claim("corelib:documenter", "corelib", "README.md"),
            Claim("FEATURE:backenddev", "FEATURE", "MBXS/ShellConfig.cs"),
        };

        var view = ClaimConflictView.Find(claims, Resolve)
            .Select(c => (c.A.SessionId, c.B.SessionId)).ToList();
        var arbiter = WorkLedgerClaims.FindOverlaps(claims, Resolve)
            .Select(o => (o.A.SessionId, o.B.SessionId)).ToList();

        Assert.Equal(arbiter, view);
    }
}
