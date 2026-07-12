using Huddle;
namespace Huddle.Tests;

public class ResourceLedgerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "resledger-" + Guid.NewGuid().ToString("N"));
    public ResourceLedgerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private void WriteLedger(string safeName, string json) =>
        File.WriteAllText(Path.Combine(_dir, safeName + ".json"), json);

    private const string OneLive = """
    { "session": "repo:arch", "updated": "2026-07-10T19:00:00Z", "resources": [
      { "id": "edge", "type": "process", "pid": 4242, "port": 9333, "what": "headless edge",
        "artifacts": ["C:/tmp/prof"], "cleanup": "taskkill /PID 4242 /T /F",
        "spawnedAt": "2026-07-10T18:00:00Z", "cleanedAt": null } ] }
    """;

    [Fact]
    public void ReadSession_parses_entries()
    {
        WriteLedger("repo_arch", OneLive);
        var ledger = new ResourceLedger(_dir, _ => { });
        var s = ledger.ReadSession("repo_arch");
        Assert.NotNull(s);
        Assert.Equal("repo:arch", s!.Session);
        var e = Assert.Single(s.Resources);
        Assert.Equal("edge", e.Id);
        Assert.Equal(4242, e.Pid);
        Assert.Equal(9333, e.Port);
        Assert.Null(e.CleanedAt);
    }

    [Fact]
    public void ReadSession_returns_null_when_file_missing()
    {
        var ledger = new ResourceLedger(_dir, _ => { });
        Assert.Null(ledger.ReadSession("repo_none"));
    }

    [Fact]
    public void FindLeaks_reports_uncleaned_live_pid()
    {
        WriteLedger("repo_arch", OneLive);
        var ledger = new ResourceLedger(_dir, _ => { });
        var leaks = ledger.FindLeaks(pidAlive: pid => pid == 4242);
        var (safe, entry) = Assert.Single(leaks);
        Assert.Equal("repo_arch", safe);
        Assert.Equal("edge", entry.Id);
    }

    [Fact]
    public void FindLeaks_ignores_cleaned_entries()
    {
        WriteLedger("repo_arch", OneLive.Replace("\"cleanedAt\": null", "\"cleanedAt\": \"2026-07-10T19:30:00Z\""));
        var ledger = new ResourceLedger(_dir, _ => { });
        Assert.Empty(ledger.FindLeaks(pidAlive: _ => true));
    }

    [Fact]
    public void FindLeaks_ignores_dead_pids()
    {
        WriteLedger("repo_arch", OneLive);
        var ledger = new ResourceLedger(_dir, _ => { });
        Assert.Empty(ledger.FindLeaks(pidAlive: _ => false));
    }

    [Fact]
    public void ReadSession_returns_null_on_malformed_json_and_logs()
    {
        WriteLedger("repo_bad", "{ not json");
        var msgs = new List<string>();
        var ledger = new ResourceLedger(_dir, msgs.Add);
        Assert.Null(ledger.ReadSession("repo_bad"));
        Assert.Contains(msgs, m => m.Contains("repo_bad"));
    }

    [Fact]
    public void FormatLeak_is_actionable()
    {
        var e = new ResourceEntry("edge", "process", 4242, 9333, "headless edge",
            new List<string> { "C:/tmp/prof" }, "taskkill /PID 4242 /T /F",
            DateTime.UtcNow, null);
        var line = ResourceLedger.FormatLeak("repo_arch", e);
        Assert.Contains("repo_arch", line);
        Assert.Contains("4242", line);
        Assert.Contains("taskkill /PID 4242 /T /F", line);
    }
}
