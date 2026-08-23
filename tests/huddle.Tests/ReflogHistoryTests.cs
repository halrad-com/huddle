using Huddle;
namespace Huddle.Tests;

public class ReflogHistoryTests
{
    const string Log = "a b s <e> 1787375264 -0700\tupdate by push\nb c s <e> 1787375715 -0700\tupdate by push\nc d s <e> 1787300000 -0700\tfetch\n";

    [Fact]
    public void Parses_movements_with_identity_and_window()
    {
        var ids = new Dictionary<string, string> { ["origin"] = "dev.azure.com/contoso/LIB" };
        var since = DateTimeOffset.FromUnixTimeSeconds(1787370000);
        var m = ReflogHistory.Parse("C:/r/logs/refs/remotes", "C:/r/logs/refs/remotes/origin/master", Log, since, ids);
        Assert.Equal(2, m.Count);
        Assert.All(m, x => Assert.Equal("push", x.Verb));
        Assert.Equal("dev.azure.com/contoso/LIB", m[0].Identity);
        Assert.Equal("master", m[0].Branch);
        Assert.Equal("origin", m[0].Remote);
    }

    [Fact]
    public void Unknown_remote_has_null_identity()
    {
        var m = ReflogHistory.Parse("C:/r/logs/refs/remotes", "C:/r/logs/refs/remotes/github/main", Log, DateTimeOffset.MinValue, new Dictionary<string, string>());
        Assert.Equal(3, m.Count);
        Assert.Null(m[0].Identity);
        Assert.Equal("github", m[0].Remote);
    }

    [Fact]
    public void Numstat_parses_commits_lines_and_times()
    {
        var log = "1787375264\n\n10\t2\tsrc/a.cs\n5\t0\tsrc/b.cs\n\n1787300000\n\n1\t1\tx\n";
        var s = GitLogStats.ParseNumstat(log, "6");
        Assert.Equal(2, s.Commits);
        Assert.Equal(6, s.Unpushed);
        Assert.Equal(16, s.Added);
        Assert.Equal(3, s.Deleted);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1787375264), s.Last);
        Assert.Equal(2, s.CommitTimes.Count);
    }

    [Fact]
    public void Numstat_handles_binary_dashes_and_missing_revlist()
    {
        var s = GitLogStats.ParseNumstat("1787375264\n\n-\t-\timg.png\n", null);
        Assert.Equal(1, s.Commits); Assert.Equal(0, s.Added); Assert.Equal(0, s.Unpushed);
    }

    [Fact]
    public void Non_repo_collect_is_null()
    {
        var d = Path.Combine(Path.GetTempPath(), "notrepo-" + Guid.NewGuid());
        Directory.CreateDirectory(d);
        Assert.Null(GitLogStats.Collect(d, DateTimeOffset.UtcNow.AddDays(-7)));
        Assert.Empty(ReflogHistory.Read(d, DateTimeOffset.MinValue));
        Directory.Delete(d);
    }
}
