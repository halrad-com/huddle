using Huddle;
using Xunit;

namespace HuddleTests;

public class SessionHistoryTests : IDisposable
{
    private readonly string _root;      // fake ~/.claude/projects
    private readonly string _repoRoot;  // fake registered repo

    public SessionHistoryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "huddle-history-" + Guid.NewGuid().ToString("N"));
        _repoRoot = Path.Combine(_root, "repo-checkout");
        Directory.CreateDirectory(_repoRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private TranscriptStore MakeStore() => new(
        _root,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["exampleapp"] = _repoRoot },
        _ => { });

    private string WriteTranscript(string sessionId, params string[] lines)
    {
        var dir = Path.Combine(_root, "C--encoded-project");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, sessionId + ".jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    private string[] FixtureLines(string cwd) => new[]
    {
        """{"type":"user","timestamp":"2026-07-14T20:00:00Z","cwd":""" +
            System.Text.Json.JsonSerializer.Serialize(cwd) +
            ""","message":{"role":"user","content":"design the ROI audit for the scoring library"}}""",
        """{"type":"assistant","timestamp":"2026-07-14T20:05:00Z","message":{"role":"assistant","content":[{"type":"tool_use","name":"Write","input":{"file_path":"C:\\out\\audit-design.md","content":"..."}}]}}""",
        """{"type":"ai-title","aiTitle":"ROI Audit design"}""",
        """{"type":"assistant","timestamp":"2026-07-14T21:00:00Z","message":{"role":"assistant","content":[{"type":"tool_use","name":"Edit","input":{"file_path":"C:\\out\\audit-design.md","old_string":"a","new_string":"b"}},{"type":"tool_use","name":"Write","input":{"file_path":"C:\\out\\coverage-report.md","content":"..."}}]}}""",
        """{"type":"last-prompt","lastPrompt":"does VA reproduce across boxes?"}""",
        """not json — must be skipped, not fatal""",
    };

    [Fact]
    public void SummaryDerivesTitleRepoTimesAndFileCount()
    {
        WriteTranscript("aaaa1111-0000-0000-0000-000000000001", FixtureLines(_repoRoot));

        var sessions = MakeStore().ListSessions(new HistoryFilter(null, null, null));

        var s = Assert.Single(sessions);
        Assert.Equal("ROI Audit design", s.Title);
        Assert.Equal("exampleapp", s.Repo);
        Assert.Equal(2, s.FileCount); // audit-design.md deduped across Write+Edit, plus coverage-report.md
        Assert.Contains("ROI audit", s.OpeningPrompt);
        Assert.NotNull(s.StartedAt);
        Assert.NotNull(s.LastActivity);
        Assert.True(s.StartedAt <= s.LastActivity);
    }

    [Fact]
    public void DetailCarriesLastPromptAndFiles()
    {
        WriteTranscript("aaaa1111-0000-0000-0000-000000000002", FixtureLines(_repoRoot));

        var detail = MakeStore().GetDetail("aaaa1111-0000-0000-0000-000000000002");

        Assert.NotNull(detail);
        Assert.Equal("does VA reproduce across boxes?", detail!.LastPrompt);
        Assert.Equal(2, detail.Files.Count);
        Assert.Contains(detail.Files, f => f.EndsWith("coverage-report.md"));
    }

    [Fact]
    public void UnregisteredCwdStillListsWithPathLabel()
    {
        var elsewhere = Path.Combine(_root, "somewhere-else");
        Directory.CreateDirectory(elsewhere);
        WriteTranscript("aaaa1111-0000-0000-0000-000000000003", FixtureLines(elsewhere));

        var s = Assert.Single(MakeStore().ListSessions(new HistoryFilter(null, null, null)));

        Assert.Equal("…somewhere-else", s.Repo);
    }

    [Fact]
    public void FiltersApplyRepoKeywordAndCutoff()
    {
        WriteTranscript("aaaa1111-0000-0000-0000-000000000004", FixtureLines(_repoRoot));
        var store = MakeStore();

        Assert.Single(store.ListSessions(new HistoryFilter("exampleapp", null, null)));
        Assert.Empty(store.ListSessions(new HistoryFilter("otherrepo", null, null)));
        Assert.Single(store.ListSessions(new HistoryFilter(null, "roi", null)));
        Assert.Empty(store.ListSessions(new HistoryFilter(null, "no-such-keyword", null)));
        Assert.Empty(store.ListSessions(new HistoryFilter(null, null, DateTime.Now)));      // activity is in the past
        Assert.Single(store.ListSessions(new HistoryFilter(null, null, new DateTime(2020, 1, 1))));
    }

    [Fact]
    public void MissingTitleFallsBackToOpeningPromptThenUntitled()
    {
        WriteTranscript("aaaa1111-0000-0000-0000-000000000005",
            """{"type":"user","timestamp":"2026-07-14T20:00:00Z","cwd":""" +
                System.Text.Json.JsonSerializer.Serialize(_repoRoot) +
                ""","message":{"role":"user","content":"short ask"}}""");
        WriteTranscript("aaaa1111-0000-0000-0000-000000000006",
            """{"type":"system","timestamp":"2026-07-14T20:00:00Z"}""");

        var sessions = MakeStore().ListSessions(new HistoryFilter(null, null, null));

        Assert.Contains(sessions, s => s.Title == "short ask");
        Assert.Contains(sessions, s => s.Title == "(untitled)");
    }

    [Fact]
    public void SubagentTranscriptsInSubdirectoriesAreNotListed()
    {
        WriteTranscript("aaaa1111-0000-0000-0000-000000000007", FixtureLines(_repoRoot));
        var subDir = Path.Combine(_root, "C--encoded-project", "subagents");
        Directory.CreateDirectory(subDir);
        File.WriteAllLines(Path.Combine(subDir, "agent-x.jsonl"), FixtureLines(_repoRoot));

        Assert.Single(MakeStore().ListSessions(new HistoryFilter(null, null, null)));
    }
}
