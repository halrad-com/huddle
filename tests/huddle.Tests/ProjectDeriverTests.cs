using System.Diagnostics;
using Huddle;
using Xunit;

namespace HuddleTests;

// The project-summary deriver (2026-08-10): the operator supplies a thin source pointer
// (slug -> "repo[/subpath][@branch]"), huddle derives the substance from that repo's own
// README + git. ParseSource is pure; Derive is proven against a real temp git repo.
public class ProjectDeriverTests
{
    [Theory]
    [InlineData("otherapp", "otherapp", "", null)]
    [InlineData("myapp@FEATURE", "myapp", "", "FEATURE")]
    [InlineData("refcode/mb_clouseau", "refcode", "mb_clouseau", null)]
    [InlineData("LIB/a/b@feat", "LIB", "a/b", "feat")]
    [InlineData("  spaced  ", "spaced", "", null)]
    [InlineData("repo@", "repo", "", null)]   // empty branch after @ = no branch
    public void ParseSource_SplitsRepoSubpathBranch(string src, string repo, string sub, string? branch)
    {
        var (r, s, b) = ProjectDeriver.ParseSource(src);
        Assert.Equal(repo, r);
        Assert.Equal(sub, s);
        Assert.Equal(branch, b);
    }

    [Fact]
    public void Derive_UnknownRepo_ReturnsNull()
    {
        Assert.Null(ProjectDeriver.Derive("nope", new Dictionary<string, string>(), _ => { }));
    }

    [Fact]
    public void Derive_ReadsReadmeAndGitFromRealRepo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "huddle-deriv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // First heading is the name; the first prose line is the "what".
            File.WriteAllText(Path.Combine(dir, "README.md"),
                "# Demo Tool\n\nA tool that does the demo thing.\n");
            Git(dir, "init");
            Git(dir, "config user.email t@t.local");
            Git(dir, "config user.name test");
            Git(dir, "add -A");
            Git(dir, "commit -m seed-commit");

            var got = ProjectDeriver.Derive("demo",
                new Dictionary<string, string> { { "demo", dir } }, _ => { });

            Assert.NotNull(got);
            Assert.Equal("demo", got!.Repo);
            Assert.Equal("A tool that does the demo thing.", got.What);
            Assert.Equal("seed-commit", got.LastCommit);
            Assert.NotNull(got.LastCommitAt);
            Assert.True(got.Commits30d >= 1);
            Assert.False(string.IsNullOrEmpty(got.Branch));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static void Git(string dir, string args)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit(5000);
    }
}
