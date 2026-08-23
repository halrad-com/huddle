using Huddle;
namespace Huddle.Tests;

public class GitActivityLogTests
{
    static string Tmp() => Path.Combine(Path.GetTempPath(), $"ga-{Guid.NewGuid():N}.jsonl");

    [Fact]
    public void Auth_drop_becomes_entry()
    {
        var e = GitActivityLog.ParseAuthDrop("myapp:architect\t5050d332-aaaa\thttps\tdev.azure.com", new DateTimeOffset(2026,8,21,22,7,41,TimeSpan.Zero))!;
        Assert.Equal("auth", e.Kind);
        Assert.Equal("myapp:architect", e.Instance);
        Assert.Equal("5050d332", e.Session);
        Assert.Equal("dev.azure.com", e.Host);
        Assert.Null(GitActivityLog.ParseAuthDrop("", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Movement_uses_reflog_timestamp_and_identity()
    {
        var raw = "bab3978e3fc34ceaa478670436992a70a9b825d8 97a3aa8747d4ff6158492d9277f859cfbe773763 you <s@x> 1787375264 -0700\tupdate by push";
        var e = GitActivityLog.ParseMovement("myapp", "origin/master", raw, "dev.azure.com/contoso/LIB")!;
        Assert.Equal("move", e.Kind);
        Assert.Equal("push", e.Verb);
        Assert.Equal("origin", e.Remote);
        Assert.Equal("master", e.Branch);
        Assert.Equal("97a3aa8", e.Sha);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1787375264), e.Ts);
        Assert.Equal("dev.azure.com/contoso/LIB", e.Identity);
    }

    [Fact]
    public void Append_then_read_since_filters_by_time()
    {
        var p = Tmp();
        var log = new GitActivityLog(p);
        var t0 = new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero);
        log.Append(new GitActivityEntry(t0, "auth", "a:b", "s", "h", "https", null, null, null, null, null, null));
        log.Append(new GitActivityEntry(t0.AddDays(1), "move", null, null, null, null, "r", "push", "origin", "id", "master", "abc1234"));
        var all = log.ReadSince(t0.AddHours(12));
        var e = Assert.Single(all);
        Assert.Equal("move", e.Kind);
        Assert.DoesNotContain("\"instance\"", File.ReadAllLines(p)[1]); // nulls omitted
        File.Delete(p);
    }

    [Fact]
    public void Bad_line_is_skipped_not_fatal()
    {
        var p = Tmp();
        File.WriteAllText(p, "{garbage\n" + """{"ts":"2026-08-21T00:00:00Z","kind":"auth","instance":"x"}""" + "\n");
        Assert.Single(new GitActivityLog(p).ReadSince(DateTimeOffset.MinValue));
        File.Delete(p);
    }

    [Fact]
    public void Missing_file_reads_empty()
    {
        Assert.Empty(new GitActivityLog(Tmp()).ReadSince(DateTimeOffset.MinValue));
    }
}
