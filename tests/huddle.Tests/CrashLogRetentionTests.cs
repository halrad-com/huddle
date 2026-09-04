using Huddle;
using Xunit;

namespace HuddleTests;

// H2 (wiring-gap backlog): crashLogRetention must actually prune crash logs.
// The defect: the setting was settable and validated while nothing pruned anything.
public class CrashLogRetentionTests : IDisposable
{
    private readonly string _dir;

    public CrashLogRetentionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "huddle-crashlogs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string WriteCrashLog(string name, DateTime mtime)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "crash");
        File.SetLastWriteTimeUtc(path, mtime);
        return path;
    }

    [Fact]
    public void Prune_keeps_newest_n_and_removes_the_rest()
    {
        var t0 = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 5; i++)
            WriteCrashLog($"crash-2026080{i + 1}-000000.log", t0.AddDays(i));

        SessionManager.PruneCrashLogs(_dir, keep: 3, _ => { });

        var remaining = Directory.GetFiles(_dir, "crash-*.log").Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "crash-20260803-000000.log", "crash-20260804-000000.log", "crash-20260805-000000.log" }, remaining);
    }

    [Fact]
    public void Prune_zero_keeps_none()
    {
        WriteCrashLog("crash-20260801-000000.log", DateTime.UtcNow);
        SessionManager.PruneCrashLogs(_dir, keep: 0, _ => { });
        Assert.Empty(Directory.GetFiles(_dir, "crash-*.log"));
    }

    [Fact]
    public void Prune_leaves_other_files_alone_and_is_noop_when_under_cap()
    {
        WriteCrashLog("crash-20260801-000000.log", DateTime.UtcNow);
        File.WriteAllText(Path.Combine(_dir, "scratchpad.md"), "keep me");

        SessionManager.PruneCrashLogs(_dir, keep: 10, _ => { });

        Assert.Single(Directory.GetFiles(_dir, "crash-*.log"));
        Assert.True(File.Exists(Path.Combine(_dir, "scratchpad.md")));
    }

    [Fact]
    public void Prune_missing_dir_does_not_throw()
    {
        SessionManager.PruneCrashLogs(Path.Combine(_dir, "nope"), keep: 3, _ => { });
    }
}
