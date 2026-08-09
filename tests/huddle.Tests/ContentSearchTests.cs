using Huddle;
namespace Huddle.Tests;

public class ContentSearchTests : IDisposable
{
    private readonly string _root;

    public ContentSearchTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "huddle-find-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    // --- helpers -----------------------------------------------------------

    private string Dir(params string[] parts)
    {
        var p = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(p);
        return p;
    }

    private string WriteFile(string dir, string name, string content)
    {
        var p = Path.Combine(dir, name);
        File.WriteAllText(p, content);
        return p;
    }

    private ContentSearch Make(IDocumentSource? docs = null,
                               Func<string, string?>? live = null,
                               Action<string>? log = null)
    {
        var projects = Dir("projects");
        var store = new TranscriptStore(projects,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), _ => { });
        return new ContentSearch(
            docs ?? new FakeDocs(),
            store,
            Dir("logs"),
            Dir("ipc"),
            live ?? (_ => null),
            log ?? (_ => { }));
    }

    private sealed class FakeDocs : IDocumentSource
    {
        private readonly List<DocumentEntry> _entries;
        public FakeDocs(params DocumentEntry[] entries) { _entries = entries.ToList(); }
        public IReadOnlyList<DocumentEntry> GetDocuments(DocLevel maxLevel) => _entries;
    }

    private static DocumentEntry Doc(string title, string path, string repo = "alpha",
                                     DateTime? ts = null) =>
        new(title, path, "alpha:architect", repo, ts ?? DateTime.Now, DocLevel.Output, null);

    // --- Docs corpus -------------------------------------------------------

    [Fact]
    public void Docs_MatchesByBodyContent()
    {
        var d = Dir("docs");
        var hit = WriteFile(d, "spec.md", "# Overlay spec\nthe rockalley overlay design\n");
        var miss = WriteFile(d, "other.md", "# Unrelated\nnothing here\n");
        var cs = Make(new FakeDocs(Doc("Overlay spec", hit), Doc("Unrelated", miss)));

        var r = cs.Search("rockalley", null, null);

        Assert.Single(r.Docs);
        Assert.Equal(hit, r.Docs[0].Path);
    }

    [Fact]
    public void Docs_MetadataMatchStillHitsWhenBodyUnreadable()
    {
        var gone = Path.Combine(_root, "does-not-exist.md");
        var cs = Make(new FakeDocs(Doc("rockalley plan", gone)));

        var r = cs.Search("rockalley", null, null);

        Assert.Single(r.Docs);   // title matched; missing body is not fatal
    }

    [Fact]
    public void Docs_RepoAndCutoffFiltersApply()
    {
        var d = Dir("docs");
        var a = WriteFile(d, "a.md", "rockalley\n");
        var b = WriteFile(d, "b.md", "rockalley\n");
        var old = DateTime.Now.AddDays(-30);
        var cs = Make(new FakeDocs(
            Doc("A", a, repo: "alpha"),
            Doc("B", b, repo: "beta", ts: old)));

        Assert.Single(cs.Search("rockalley", "alpha", null).Docs);
        Assert.Empty(cs.Search("rockalley", "beta", DateTime.Now.AddDays(-1)).Docs);
    }

    [Fact]
    public void Search_EmptyCorporaReturnEmptyGroups()
    {
        var r = Make().Search("anything", null, null);
        Assert.Empty(r.Docs);
        Assert.Empty(r.Sessions);
        Assert.Empty(r.Notes);
        Assert.Empty(r.Mail);
        Assert.False(r.TranscriptsTruncated);
    }

    // --- Sessions corpus ---------------------------------------------------

    // Minimal valid transcript lines: cwd + timestamp on a user message.
    private static string TranscriptLine(string text, string ts = "2026-08-08T12:00:00Z") =>
        "{\"type\":\"user\",\"cwd\":\"C:/w\",\"timestamp\":\"" + ts + "\"," +
        "\"message\":{\"role\":\"user\",\"content\":\"" + text + "\"}}";

    private string WriteTranscript(string name, params string[] lines)
    {
        var projDir = Dir("projects", "proj-a");
        return WriteFile(projDir, name + ".jsonl", string.Join("\n", lines) + "\n");
    }

    [Fact]
    public void Sessions_HitCountsMatchingLines_AndParsesSummary()
    {
        WriteTranscript("aaa",
            TranscriptLine("working on rockalley overlay"),
            TranscriptLine("more rockalley work"),
            TranscriptLine("unrelated"));
        WriteTranscript("bbb", TranscriptLine("nothing relevant"));

        var r = Make().Search("rockalley", null, null);

        Assert.Single(r.Sessions);
        Assert.Equal(2, r.Sessions[0].MatchCount);
        Assert.Equal("aaa", r.Sessions[0].Summary.Id);
        Assert.Null(r.Sessions[0].LiveInstanceId);
    }

    [Fact]
    public void Sessions_LiveLookupIsThreadedThrough()
    {
        WriteTranscript("ccc", TranscriptLine("rockalley"));
        var r = Make(live: sid => sid == "ccc" ? "alpha:architect" : null)
            .Search("rockalley", null, null);

        Assert.Single(r.Sessions);
        Assert.Equal("alpha:architect", r.Sessions[0].LiveInstanceId);
    }

    [Fact]
    public void Sessions_CutoffSkipsOldTranscriptsByMtime()
    {
        // Two matching transcripts; only the recent one may survive the cutoff. The
        // recent hit is what makes this a real guard rather than an assert-nothing test:
        // a broken sessions search would fail it, not pass it.
        var old = WriteTranscript("ddd", TranscriptLine("rockalley"));
        File.SetLastWriteTime(old, DateTime.Now.AddDays(-30));
        WriteTranscript("eee", TranscriptLine("rockalley"));   // mtime = now, enumerated first

        var r = Make().Search("rockalley", null, DateTime.Now.AddDays(-1));

        Assert.Single(r.Sessions);
        Assert.Equal("eee", r.Sessions[0].Summary.Id);
        Assert.DoesNotContain(r.Sessions, h => h.Summary.Id == "ddd");
        Assert.False(r.TranscriptsTruncated);
    }

    // A transcript deleted between enumeration and scan has no mtime — File.GetLastWriteTime
    // returns the 1601 sentinel instead of throwing, which would read as "older than the
    // cutoff" and break the walk, silently dropping every in-window transcript behind it.
    // The live callback fires inside the scan loop, so it can stage the deletion for real.
    [Fact]
    public void Sessions_TranscriptDeletedMidScanDoesNotKillTheRestOfTheCorpus()
    {
        var newest = WriteTranscript("n1", TranscriptLine("rockalley"));
        var doomed = WriteTranscript("n2", TranscriptLine("rockalley"));
        var behind = WriteTranscript("n3", TranscriptLine("rockalley"));
        File.SetLastWriteTime(newest, DateTime.Now);
        File.SetLastWriteTime(doomed, DateTime.Now.AddHours(-1));
        File.SetLastWriteTime(behind, DateTime.Now.AddHours(-2));

        var logs = new List<string>();
        var cs = Make(live: sid =>
        {
            if (sid == "n1") File.Delete(doomed);   // vanishes before the loop reaches it
            return null;
        }, log: logs.Add);

        // Cutoff must be set for the early-break path to be live at all.
        var r = cs.Search("rockalley", null, DateTime.Now.AddDays(-1));

        Assert.Equal(new[] { "n1", "n3" }, r.Sessions.Select(h => h.Summary.Id).ToArray());
        Assert.Contains(logs, m => m.Contains("n2"));   // skipped loudly, never silently
    }

    // --- Notes corpus ------------------------------------------------------

    [Fact]
    public void Notes_CountsMentionsAndNamesSession()
    {
        var pad = Dir("logs", "alpha_architect");
        WriteFile(pad, "scratchpad.md", "## Checkpoint\nrockalley started\nrockalley done\n");
        var other = Dir("logs", "beta_reviewer");
        WriteFile(other, "scratchpad.md", "nothing\n");

        var r = Make().Search("rockalley", null, null);

        Assert.Single(r.Notes);
        Assert.Equal("alpha:architect scratchpad", r.Notes[0].Title);
        Assert.Equal("alpha", r.Notes[0].Repo);
        Assert.Equal("2 mention(s)", r.Notes[0].Note);
        Assert.Equal("", r.Notes[0].SourceSession);   // title already names the session
    }

    [Fact]
    public void Notes_RepoFilterUsesSafeNamePrefix()
    {
        var pad = Dir("logs", "alpha_architect");
        WriteFile(pad, "scratchpad.md", "rockalley\n");

        Assert.Single(Make().Search("rockalley", "alpha", null).Notes);
        Assert.Empty(Make().Search("rockalley", "beta", null).Notes);
    }

    // A scratchpad deleted between the directory walk and the mtime read has no mtime —
    // File.GetLastWriteTime returns the 1601 sentinel instead of throwing, and with a
    // cutoff active that reads as "older than the cutoff": the note disappears with no
    // trace. Reproducible directly via SetLastWriteTimeUtc, so no mid-scan seam is needed.
    // The surviving good scratchpad keeps this from asserting nothing.
    [Fact]
    public void Notes_FileWithNoMtimeIsSkippedLoudlyNotSilently()
    {
        var goodDir = Dir("logs", "alpha_architect");
        var good = WriteFile(goodDir, "scratchpad.md", "rockalley started\n");
        var ghostDir = Dir("logs", "alpha_reviewer");
        var ghost = WriteFile(ghostDir, "scratchpad.md", "rockalley ghost\n");
        File.SetLastWriteTime(good, DateTime.Now);
        // 1601-01-02, not 1601-01-01: a FILETIME of exactly 0 means "leave unchanged" to
        // the Win32 API, so setting the literal epoch is a silent no-op. One day past it
        // is the closest reproducible stand-in for the sentinel a vanished file reports.
        File.SetLastWriteTimeUtc(ghost, new DateTime(1601, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        var logs = new List<string>();
        var r = Make(log: logs.Add).Search("rockalley", null, DateTime.Now.AddDays(-1));

        Assert.Single(r.Notes);
        Assert.Equal("alpha:architect scratchpad", r.Notes[0].Title);
        Assert.Contains(logs, m => m.Contains("alpha_reviewer"));   // never silent
    }

    // --- Mail corpus -------------------------------------------------------

    [Fact]
    public void Mail_ParsesHitAndCarriesState()
    {
        var inbox = Dir("ipc", "alpha_architect", "inbox");
        WriteFile(inbox, "001-msg.json",
            "{\"from\":\"beta:reviewer\",\"to\":\"alpha:architect\"," +
            "\"timestamp\":\"2026-08-08T10:00:00Z\",\"type\":\"info\"," +
            "\"subject\":\"rockalley handoff\",\"body\":{}}");
        var processed = Dir("ipc", "alpha_architect", "processed");
        WriteFile(processed, "002-old.json",
            "{\"from\":\"x\",\"to\":\"y\",\"timestamp\":\"2026-08-01T00:00:00Z\"," +
            "\"type\":\"info\",\"subject\":\"unrelated\",\"body\":{}}");

        var r = Make().Search("rockalley", null, null);

        Assert.Single(r.Mail);
        Assert.Equal("beta:reviewer", r.Mail[0].From);
        Assert.Equal("rockalley handoff", r.Mail[0].Subject);
        Assert.Equal("inbox", r.Mail[0].State);
    }

    [Fact]
    public void Mail_UnparseableHitFallsBackToFilename()
    {
        var inbox = Dir("ipc", "alpha_architect", "inbox");
        WriteFile(inbox, "003-broken.json", "{\"subject\":\"rockalley\", not json at all");

        var r = Make().Search("rockalley", null, null);

        Assert.Single(r.Mail);
        Assert.Equal("003-broken.json", r.Mail[0].Subject);
        Assert.Equal("?", r.Mail[0].From);
        Assert.Equal("alpha:architect", r.Mail[0].To);   // owner display form, not the safe-name
    }

    // Mail has two ways to belong to a repo: it sits in that repo's session directory
    // (ownerRepo), or it was sent by one of that repo's agents (From "repo:persona").
    // Both accept paths matter — a filter that only checked the owner dir would drop
    // every message an alpha agent sent out to another repo's inbox.
    [Fact]
    public void Mail_RepoFilterAcceptsOwnerDirOrFromPrefix()
    {
        // Owned by alpha, sent by beta -> owner-dir match.
        WriteFile(Dir("ipc", "alpha_architect", "inbox"), "020-owned.json",
            "{\"from\":\"beta:reviewer\",\"to\":\"alpha:architect\"," +
            "\"timestamp\":\"2026-08-08T12:00:00Z\",\"type\":\"info\"," +
            "\"subject\":\"rockalley owned\",\"body\":{}}");
        // Owned by gamma, sent by alpha -> From-prefix match.
        WriteFile(Dir("ipc", "gamma_reviewer", "inbox"), "021-sent.json",
            "{\"from\":\"alpha:architect\",\"to\":\"gamma:reviewer\"," +
            "\"timestamp\":\"2026-08-08T11:00:00Z\",\"type\":\"info\"," +
            "\"subject\":\"rockalley sent\",\"body\":{}}");
        // Neither owned by nor sent from alpha -> rejected.
        WriteFile(Dir("ipc", "gamma_reviewer", "inbox"), "022-foreign.json",
            "{\"from\":\"beta:reviewer\",\"to\":\"gamma:reviewer\"," +
            "\"timestamp\":\"2026-08-08T10:00:00Z\",\"type\":\"info\"," +
            "\"subject\":\"rockalley foreign\",\"body\":{}}");

        Assert.Equal(3, Make().Search("rockalley", null, null).Mail.Count);

        var filtered = Make().Search("rockalley", "alpha", null).Mail;

        Assert.Equal(new[] { "rockalley owned", "rockalley sent" },
                     filtered.Select(m => m.Subject).OrderBy(s => s, StringComparer.Ordinal).ToArray());
    }

    // Mail is filtered by file mtime, not by the timestamp inside the JSON — a message
    // whose body claims a recent date but whose file is old must still fall out of the
    // window. The surviving in-window mail keeps this from asserting nothing.
    [Fact]
    public void Mail_CutoffSkipsOlderMailByMtime()
    {
        var inbox = Dir("ipc", "alpha_architect", "inbox");
        var recent = WriteFile(inbox, "030-recent.json",
            "{\"from\":\"beta:reviewer\",\"to\":\"alpha:architect\"," +
            "\"timestamp\":\"2026-08-08T10:00:00Z\",\"type\":\"info\"," +
            "\"subject\":\"rockalley recent\",\"body\":{}}");
        var stale = WriteFile(inbox, "031-stale.json",
            "{\"from\":\"beta:reviewer\",\"to\":\"alpha:architect\"," +
            "\"timestamp\":\"2026-08-08T10:00:00Z\",\"type\":\"info\"," +
            "\"subject\":\"rockalley stale\",\"body\":{}}");
        File.SetLastWriteTime(recent, DateTime.Now);
        File.SetLastWriteTime(stale, DateTime.Now.AddDays(-30));

        Assert.Equal(2, Make().Search("rockalley", null, null).Mail.Count);

        var r = Make().Search("rockalley", null, DateTime.Now.AddDays(-1));

        Assert.Single(r.Mail);
        Assert.Equal("rockalley recent", r.Mail[0].Subject);
    }

    // A mail deleted between Directory.GetFiles and the mtime read has no mtime —
    // File.GetLastWriteTime returns the 1601 sentinel instead of throwing, and with a
    // cutoff active that reads as "older than the cutoff": the mail disappears with no
    // trace. The sentinel is reproducible directly via SetLastWriteTimeUtc, so no
    // mid-scan seam is needed. The surviving good mail keeps this from asserting nothing.
    [Fact]
    public void Mail_FileWithNoMtimeIsSkippedLoudlyNotSilently()
    {
        var inbox = Dir("ipc", "alpha_architect", "inbox");
        var good = WriteFile(inbox, "010-good.json",
            "{\"from\":\"beta:reviewer\",\"to\":\"alpha:architect\"," +
            "\"timestamp\":\"2026-08-08T10:00:00Z\",\"type\":\"info\"," +
            "\"subject\":\"rockalley handoff\",\"body\":{}}");
        var noMtime = WriteFile(inbox, "011-vanished.json",
            "{\"from\":\"beta:reviewer\",\"to\":\"alpha:architect\"," +
            "\"timestamp\":\"2026-08-08T10:00:00Z\",\"type\":\"info\"," +
            "\"subject\":\"rockalley ghost\",\"body\":{}}");
        File.SetLastWriteTime(good, DateTime.Now);
        // 1601-01-02, not 1601-01-01: a FILETIME of exactly 0 means "leave unchanged" to
        // the Win32 API, so setting the literal epoch is a silent no-op. One day past it
        // is the closest reproducible stand-in for the sentinel a vanished file reports.
        File.SetLastWriteTimeUtc(noMtime, new DateTime(1601, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        var logs = new List<string>();
        var r = Make(log: logs.Add).Search("rockalley", null, DateTime.Now.AddDays(-1));

        Assert.Single(r.Mail);
        Assert.Equal("rockalley handoff", r.Mail[0].Subject);
        Assert.Contains(logs, m => m.Contains("011-vanished.json"));   // never silent
    }
}
