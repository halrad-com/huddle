using Huddle;
using Xunit;

namespace HuddleTests;

// Worktree-aware doc/project discovery (2026-08-10). A doc authored on a feature
// branch lives in a LINKED worktree; huddle must surface it pre-merge (only in the
// worktree) and keep it once merged into the main checkout — identified by repo +
// repo-relative path, main worktree canonical. These exercise the pure pieces:
// porcelain parsing and path composition. The end-to-end proof (real git worktree ->
// projects report) is scripts/demo-project-status.ps1.
public class WorktreeDiscoveryTests
{
    private const string TwoWorktrees =
        "worktree C:/Users/you/source/repos/LIB\n" +
        "HEAD 0efdeea7f0000000000000000000000000000000\n" +
        "branch refs/heads/master\n" +
        "\n" +
        "worktree C:/Users/you/source/repos/LIB-rockalley\n" +
        "HEAD fce786df00000000000000000000000000000000\n" +
        "branch refs/heads/ROCKALLEY\n";

    [Fact]
    public void ParsePorcelain_YieldsMainFirst_WithBranches()
    {
        var wts = GitWorktrees.ParsePorcelain(TwoWorktrees);

        Assert.Equal(2, wts.Count);
        Assert.True(wts[0].IsMain);
        Assert.False(wts[1].IsMain);
        Assert.Equal("master", wts[0].Branch);
        Assert.Equal("ROCKALLEY", wts[1].Branch);
        // Roots normalized to full paths (platform separators).
        Assert.EndsWith("LIB", wts[0].Root.TrimEnd('\\', '/'));
        Assert.EndsWith("LIB-rockalley", wts[1].Root.TrimEnd('\\', '/'));
    }

    [Fact]
    public void ParsePorcelain_Detached_HasNullBranch()
    {
        var detached =
            "worktree C:/repo\nHEAD 1111111111111111111111111111111111111111\ndetached\n";
        var wts = GitWorktrees.ParsePorcelain(detached);

        Assert.Single(wts);
        Assert.Null(wts[0].Branch);
        Assert.True(wts[0].IsMain);
    }

    [Fact]
    public void ParsePorcelain_Empty_YieldsNothing()
    {
        Assert.Empty(GitWorktrees.ParsePorcelain(""));
        Assert.Empty(GitWorktrees.ParsePorcelain("   \n \n"));
    }

    [Fact]
    public void SubPath_RegisteredRootUnderTop_IsPreserved()
    {
        // LIB/myapp is a SUBDIR of the LIB git repo — the subpath must carry
        // across to each worktree (LIB-rockalley/myapp).
        Assert.Equal("myapp",
            GitWorktrees.SubPath(@"C:\a\LIB", @"C:\a\LIB\myapp").Replace('\\', '/'));
    }

    [Fact]
    public void SubPath_RegisteredRootIsTop_IsEmpty()
    {
        Assert.Equal("", GitWorktrees.SubPath(@"C:\a\LIB", @"C:\a\LIB"));
    }

    [Fact]
    public void ComposeDirs_AppliesSubpathToEachWorktree_MainFirst()
    {
        var roots = GitWorktrees.ParsePorcelain(TwoWorktrees);
        var dirs = GitWorktrees.ComposeDirs(roots[0].Root, "myapp", roots);

        Assert.Equal(2, dirs.Count);
        Assert.True(dirs[0].IsMain);
        Assert.EndsWith("LIB" + Path.DirectorySeparatorChar + "myapp", dirs[0].Root);
        Assert.EndsWith("LIB-rockalley" + Path.DirectorySeparatorChar + "myapp", dirs[1].Root);
        Assert.Equal("ROCKALLEY", dirs[1].Branch);
    }

    // The parser bug that made the ROCKALLEY spec invisible: a scratchpad accumulates
    // one `## Documents` section per checkpoint, but the parser stopped at the first
    // heading after the first section — so any doc declared in a LATER section was
    // never read. Every section's declarations must be returned.
    [Fact]
    public void ScratchpadSource_ReadsEveryDocumentsSection_NotJustTheFirst()
    {
        var dir = Path.Combine(Path.GetTempPath(), "huddle-wt-" + Guid.NewGuid().ToString("N"));
        var sessionDir = Path.Combine(dir, "myapp_frontenddev");
        Directory.CreateDirectory(sessionDir);
        try
        {
            var scratch = string.Join("\n", new[]
            {
                "## Checkpoint one",
                "did a thing",
                "",
                "## Documents",
                "- [First doc](C:/x/first.md) — a #output",
                "",
                "## Checkpoint two",
                "did more",
                "",
                "## Documents",
                "- [Second doc](C:/x/second.md) — b #output",
                "",
                "## Checkpoint three",
                "",
                "## Documents",
                "- [Third doc](C:/x/third.md) — c #output",
            });
            File.WriteAllText(Path.Combine(sessionDir, "scratchpad.md"), scratch);

            var src = new ScratchpadDocumentSource(
                dir, new Dictionary<string, SessionDefinition>(), _ => { });
            var docs = src.GetDocuments(DocLevel.Output);

            var titles = docs.Select(d => d.Title).ToList();
            Assert.Contains("First doc", titles);
            Assert.Contains("Second doc", titles);   // was dropped before the fix
            Assert.Contains("Third doc", titles);    // was dropped before the fix
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
