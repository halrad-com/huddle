using Huddle;
using Xunit;

namespace HuddleTests;

// H1 (wiring-gap backlog): transcriptMaxScan must actually govern the scan cap.
// The defect: the setting was documented and settable while MaxScan was a const.
public class TranscriptMaxScanTests : IDisposable
{
    private readonly string _root;
    private readonly string _repoRoot;

    public TranscriptMaxScanTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "huddle-maxscan-" + Guid.NewGuid().ToString("N"));
        _repoRoot = Path.Combine(_root, "repo-checkout");
        Directory.CreateDirectory(_repoRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private TranscriptStore MakeStore(int maxScan) => new(
        _root,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["exampleapp"] = _repoRoot },
        _ => { },
        maxScan);

    private void WriteTranscript(string sessionId)
    {
        var dir = Path.Combine(_root, "C--encoded-project");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, sessionId + ".jsonl");
        var cwd = System.Text.Json.JsonSerializer.Serialize(_repoRoot);
        File.WriteAllLines(path, new[]
        {
            """{"type":"user","timestamp":"2026-07-14T20:00:00Z","cwd":""" + cwd +
                ""","message":{"role":"user","content":"hello"}}""",
        });
    }

    [Fact]
    public void ListSessions_honors_configured_max_scan()
    {
        WriteTranscript("aaaa1111-0000-0000-0000-000000000001");
        WriteTranscript("aaaa1111-0000-0000-0000-000000000002");
        WriteTranscript("aaaa1111-0000-0000-0000-000000000003");

        var store = MakeStore(maxScan: 2);
        var sessions = store.ListSessions(new HistoryFilter(null, null, null));

        Assert.Equal(2, sessions.Count);
        Assert.True(store.LastListTruncated);
    }

    [Fact]
    public void ListSessions_scans_all_when_cap_above_count()
    {
        WriteTranscript("aaaa1111-0000-0000-0000-000000000001");
        WriteTranscript("aaaa1111-0000-0000-0000-000000000002");

        var store = MakeStore(maxScan: 50);
        var sessions = store.ListSessions(new HistoryFilter(null, null, null));

        Assert.Equal(2, sessions.Count);
        Assert.False(store.LastListTruncated);
    }

    [Fact]
    public void Default_max_scan_is_100_and_nonpositive_falls_back()
    {
        var byDefault = new TranscriptStore(_root,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), _ => { });
        Assert.Equal(100, byDefault.MaxScan);

        Assert.Equal(100, MakeStore(maxScan: 0).MaxScan);
    }
}
