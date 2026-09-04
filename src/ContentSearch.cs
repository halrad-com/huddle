namespace Huddle;

/// <summary>One transcript hit for the find verb: the session, how often the keyword
/// appears, and (when the session is currently running) its live instance id.</summary>
public sealed record SessionHit(SessionSummary Summary, int MatchCount, string? LiveInstanceId);

/// <summary>One IPC-mail hit. State is the folder it was found in (inbox/processed/failed).</summary>
public sealed record MailHit(string From, string To, string Subject, DateTime? Timestamp, string Path, string State);

/// <summary>Grouped results of one find run. TranscriptsTruncated is true when the
/// transcript scan stopped at TranscriptStore.MaxScan — surfaced, never silent.</summary>
public sealed record FindResults(
    IReadOnlyList<DocumentEntry> Docs,
    IReadOnlyList<SessionHit> Sessions,
    IReadOnlyList<DocumentEntry> Notes,
    IReadOnlyList<MailHit> Mail,
    bool TranscriptsTruncated);

/// <summary>
/// Cross-corpus keyword search for the `find` verb (spec:
/// docs/superpowers/specs/2026-08-08-find-verb-design.md). Deliberately index-free:
/// every corpus is streamed on demand, bounded by the same caps/windows the docs and
/// history verbs already use. Matching is plain case-insensitive substring.
/// </summary>
public sealed class ContentSearch
{
    private readonly IDocumentSource _docs;
    private readonly TranscriptStore _transcripts;
    private readonly string _logsDir;   // logs/ — session dirs holding scratchpad.md
    private readonly string _ipcDir;    // ipc/  — session dirs holding inbox/processed
    private readonly Func<string, string?> _liveInstanceBySessionId;
    private readonly Action<string> _log;

    public ContentSearch(IDocumentSource docs, TranscriptStore transcripts, string logsDir,
                         string ipcDir, Func<string, string?> liveInstanceBySessionId,
                         Action<string> log)
    {
        _docs = docs;
        _transcripts = transcripts;
        _logsDir = logsDir;
        _ipcDir = ipcDir;
        _liveInstanceBySessionId = liveInstanceBySessionId;
        _log = log;
    }

    public FindResults Search(string keyword, string? repoFilter, DateTime? cutoff)
    {
        var sessions = SearchTranscripts(keyword, repoFilter, cutoff, out var truncated);
        return new FindResults(
            SearchDocs(keyword, repoFilter, cutoff),
            sessions,
            SearchNotes(keyword, repoFilter, cutoff),
            SearchMail(keyword, repoFilter, cutoff),
            truncated);
    }

    // ---- Docs: discovered doc set (Output+Plans), metadata OR body match --------

    private List<DocumentEntry> SearchDocs(string kw, string? repo, DateTime? cutoff)
    {
        var results = new List<DocumentEntry>();
        foreach (var e in _docs.GetDocuments(DocLevel.Plans))   // Churn excluded by level
        {
            if (repo != null && !string.Equals(e.Repo, repo, StringComparison.OrdinalIgnoreCase))
                continue;
            if (cutoff.HasValue && !(e.Timestamp.HasValue && e.Timestamp.Value >= cutoff.Value))
                continue;
            if (MatchesMeta(e, kw) || BodyContains(e.Path, kw))
                results.Add(e);
        }
        return results.OrderByDescending(e => e.Timestamp ?? DateTime.MinValue).ToList();
    }

    // Same fields ConsoleUI's docs-verb matcher searches (title/path/note/repo/session).
    private static bool MatchesMeta(DocumentEntry e, string k) =>
        (e.Title?.Contains(k, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (e.Path?.Replace('\\', '/').Contains(k.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) ?? false) ||
        (e.Note?.Contains(k, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (e.Repo?.Contains(k, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (e.SourceSession?.Contains(k, StringComparison.OrdinalIgnoreCase) ?? false);

    // Streamed body scan, early-exit on first matching line. Unreadable file -> false
    // (metadata-only match still counts; logged, never fatal).
    private bool BodyContains(string path, string kw)
    {
        try
        {
            foreach (var line in File.ReadLines(path))
                if (line.Contains(kw, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch (Exception ex) { _log($"find: skip body of {path}: {ex.Message}"); }
        return false;
    }

    // ---- Sessions ----------------------------------------------------------------

    // Raw-line scan (no JSON parsing) newest-first under the MaxScan cap; a transcript
    // older than the cutoff ends the loop (mtime-sorted enumeration). ParsePath only on
    // hits. FileShare.ReadWrite via the streams below because live sessions append.
    private List<SessionHit> SearchTranscripts(string kw, string? repo, DateTime? cutoff, out bool truncated)
    {
        truncated = false;
        var hits = new List<SessionHit>();
        var scanned = 0;
        foreach (var path in _transcripts.TranscriptPaths())
        {
            // Conservative flag: the file that trips the cap might itself have been
            // cutoff-filtered a line later, so this can over-report truncation. It never
            // under-reports, which is the direction that matters — the footer must not
            // claim a complete scan when one was cut short.
            if (scanned >= _transcripts.MaxScan) { truncated = true; break; }
            DateTime mtime;
            try { mtime = File.GetLastWriteTime(path); }
            catch (Exception ex) { _log($"find: skip transcript {Path.GetFileName(path)}: {ex.Message}"); continue; }
            // A file deleted between enumeration and here does NOT throw — GetLastWriteTime
            // hands back the 1601 sentinel, which would read as "older than the cutoff" and
            // break the walk, dropping every in-window transcript behind it. Skip it loudly.
            if (mtime == default || mtime.Year < 2000)
            {
                _log($"find: skip transcript {Path.GetFileName(path)}: no mtime");
                continue;
            }
            if (cutoff.HasValue && mtime < cutoff.Value) break;   // everything after is older
            scanned++;

            var count = CountMatches(path, kw);
            if (count == 0) continue;

            var detail = _transcripts.ParsePath(path);
            if (detail == null) continue;
            var s = detail.Summary;
            if (repo != null && !string.Equals(s.Repo, repo, StringComparison.OrdinalIgnoreCase))
                continue;
            hits.Add(new SessionHit(s, count, _liveInstanceBySessionId(s.Id)));
        }
        return hits.OrderByDescending(h => h.Summary.LastActivity ?? DateTime.MinValue).ToList();
    }

    private int CountMatches(string path, string kw)
    {
        var count = 0;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            string? line;
            while ((line = reader.ReadLine()) != null)
                if (line.Contains(kw, StringComparison.OrdinalIgnoreCase)) count++;
        }
        catch (Exception ex) { _log($"find: skip transcript {Path.GetFileName(path)}: {ex.Message}"); }
        return count;
    }

    // ---- Notes -------------------------------------------------------------------

    // logs/<safe-name>/scratchpad.md — the session-notes record. Mention count in Note.
    private List<DocumentEntry> SearchNotes(string kw, string? repo, DateTime? cutoff)
    {
        var results = new List<DocumentEntry>();
        if (!Directory.Exists(_logsDir)) return results;
        foreach (var dir in SafeDirs(_logsDir))
        {
            var pad = Path.Combine(dir, "scratchpad.md");
            if (!File.Exists(pad)) continue;
            var safe = new DirectoryInfo(dir).Name;
            var (repoName, display) = ScratchpadDocumentSource.SplitSafeName(safe);
            if (repo != null && !string.Equals(repoName, repo, StringComparison.OrdinalIgnoreCase))
                continue;

            DateTime mtime;
            try { mtime = File.GetLastWriteTime(pad); }
            catch (Exception ex) { _log($"find: skip scratchpad {pad}: {ex.Message}"); continue; }
            // A scratchpad deleted between enumeration and here does NOT throw —
            // GetLastWriteTime hands back the 1601 sentinel, which reads as "older than the
            // cutoff" and drops the note with no trace. Same guard SearchTranscripts uses.
            if (mtime == default || mtime.Year < 2000)
            {
                _log($"find: skip scratchpad {pad}: no mtime");
                continue;
            }
            if (cutoff.HasValue && mtime < cutoff.Value) continue;

            var mentions = 0;
            try
            {
                foreach (var line in File.ReadLines(pad))
                    if (line.Contains(kw, StringComparison.OrdinalIgnoreCase)) mentions++;
            }
            catch (Exception ex) { _log($"find: skip scratchpad {pad}: {ex.Message}"); continue; }
            if (mentions == 0) continue;

            results.Add(new DocumentEntry(
                Title: $"{display} scratchpad",
                Path: pad,
                SourceSession: "",          // the Title already names the session — don't print it twice
                Repo: repoName,
                Timestamp: mtime,
                Level: DocLevel.Output,
                Note: $"{mentions} mention(s)"));
        }
        return results.OrderByDescending(e => e.Timestamp ?? DateTime.MinValue).ToList();
    }

    // ---- Mail ---------------------------------------------------------------------

    // ipc/<safe-name>/{inbox,processed,failed}/*.json — the coordination trail.
    // Raw-content match first; parse (tolerant, escape-repairing) only on hits.
    private List<MailHit> SearchMail(string kw, string? repo, DateTime? cutoff)
    {
        var results = new List<MailHit>();
        if (!Directory.Exists(_ipcDir)) return results;
        foreach (var sessionDir in SafeDirs(_ipcDir))
        {
            var owner = new DirectoryInfo(sessionDir).Name;               // safe-name or _huddle
            var (ownerRepo, ownerDisplay) = ScratchpadDocumentSource.SplitSafeName(owner);
            foreach (var state in new[] { "inbox", "processed", "failed" })
            {
                var stateDir = Path.Combine(sessionDir, state);
                if (!Directory.Exists(stateDir)) continue;
                string[] files;
                try { files = Directory.GetFiles(stateDir, "*.json", SearchOption.TopDirectoryOnly); }
                catch (Exception ex) { _log($"find: skip {stateDir}: {ex.Message}"); continue; }
                foreach (var file in files)
                {
                    DateTime mtime;
                    try { mtime = File.GetLastWriteTime(file); }
                    catch (Exception ex) { _log($"find: skip mail {file}: {ex.Message}"); continue; }
                    // A file deleted between GetFiles and here does NOT throw — GetLastWriteTime
                    // hands back the 1601 sentinel, which reads as "older than the cutoff" and
                    // drops the mail with no trace. Same guard SearchTranscripts uses.
                    if (mtime == default || mtime.Year < 2000)
                    {
                        _log($"find: skip mail {file}: no mtime");
                        continue;
                    }
                    if (cutoff.HasValue && mtime < cutoff.Value) continue;

                    string content;
                    try { content = File.ReadAllText(file); }
                    catch (Exception ex) { _log($"find: skip mail {file}: {ex.Message}"); continue; }
                    if (!content.Contains(kw, StringComparison.OrdinalIgnoreCase)) continue;

                    // TryParse: strict first, escape-repair only on failure, and it logs what
                    // it could not read. TryParseRepaired direct would rewrite escapes in every
                    // valid message (doubling \n \t in subjects) and swallow the failure.
                    var msg = IpcManager.TryParse(content, Path.GetFileName(file), _log);
                    var from = string.IsNullOrEmpty(msg?.From) ? "?" : msg!.From;
                    var to = string.IsNullOrEmpty(msg?.To) ? ownerDisplay : msg!.To;
                    var subject = string.IsNullOrEmpty(msg?.Subject) ? Path.GetFileName(file) : msg!.Subject;
                    DateTime? ts = DateTime.TryParse(msg?.Timestamp, null,
                        System.Globalization.DateTimeStyles.AssumeUniversal |
                        System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var parsed) ? parsed.ToLocalTime() : mtime;

                    if (repo != null &&
                        !string.Equals(ownerRepo, repo, StringComparison.OrdinalIgnoreCase) &&
                        !from.StartsWith(repo + ":", StringComparison.OrdinalIgnoreCase))
                        continue;

                    results.Add(new MailHit(from, to, subject, ts, file, state));
                }
            }
        }
        return results.OrderByDescending(m => m.Timestamp ?? DateTime.MinValue).ToList();
    }

    // Shared: fault-isolated directory listing.
    private string[] SafeDirs(string root)
    {
        try { return Directory.GetDirectories(root); }
        catch (Exception ex) { _log($"find: skip {root}: {ex.Message}"); return Array.Empty<string>(); }
    }
}

/// <summary>
/// Shared numbering for a find listing. Display numbers run contiguously across the
/// Docs/Sessions/Notes/Mail groups; each slot records which backing list the number
/// points into (ConsoleUI keeps docs+notes+mail rows in _lastDocs, sessions in
/// _lastHistory). open/resume/history translate through Resolve.
/// </summary>
public sealed class FindMap
{
    public enum Kind { Doc, Session }

    private readonly List<(Kind kind, int index)> _slots = new();

    public int Count => _slots.Count;

    /// <summary>Register the next display slot; returns its 1-based display number.</summary>
    public int Add(Kind kind, int index)
    {
        _slots.Add((kind, index));
        return _slots.Count;
    }

    public (Kind kind, int index)? Resolve(int displayNumber) =>
        displayNumber >= 1 && displayNumber <= _slots.Count ? _slots[displayNumber - 1] : null;
}
