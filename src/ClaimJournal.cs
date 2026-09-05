using System.Text;
using System.Text.Json;

namespace Huddle;

/// <summary>
/// Append-only record of every claim ever GRANTED, one JSON line per grant.
///
/// It exists because the claims directory is a record of what is held NOW: a claim is
/// deleted on release, and the protocol tells agents to release as they finish. So by
/// the time a commit can be audited, the claim that authorised it is usually gone —
/// and the session-stop audit, which only inspects claims still held, sees nothing at
/// all for a session that followed the rules. The journal is the "was this ever
/// claimed by anyone" half, and nothing more: it is not a second ledger, it holds no
/// state anyone reads for coordination, and losing it costs only audit precision.
///
/// Deliberately NOT session-attributed at read time. Sessions share a worktree, so
/// huddle cannot know which session authored a commit; claiming otherwise would
/// produce confident false accusations, and a ledger that cries wolf gets ignored.
/// The question it answers is "did ANYONE claim this file", which is answerable.
/// </summary>
public sealed class ClaimJournal
{
    public const string FileName = "journal.jsonl";

    private readonly string _path;
    private readonly Action<string> _log;
    private readonly object _lock = new();

    /// <summary>No-BOM UTF-8. Encoding.UTF8 emits a preamble on the FIRST append, which
    /// .NET's own reader strips but `jq` and every other line-oriented tool chokes on —
    /// a .jsonl nobody outside huddle can read is half a format.</summary>
    private static readonly Encoding NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public ClaimJournal(string workLedgerDir, Action<string> log)
    {
        _path = Path.Combine(workLedgerDir, FileName);
        _log = log;
    }

    // Root is the ABSOLUTE checkout the Files are relative to. Added 2026-09-04 after
    // the audit's first live run accused a correctly-claimed file: without it a claim
    // from a subdirectory checkout cannot be lined up with a repo-relative commit path.
    // Optional so lines written before it still parse - they lose only root precision.
    private sealed record Entry(string Ts, string Session, string Repo, List<string> Files, string Root = "");

    /// <summary>Append one grant. Never throws — a failed journal write must not fail
    /// the claim it is recording; the claim is the thing that prevents collisions.</summary>
    public void Record(string sessionId, string repo, IEnumerable<string> files, string root = "")
    {
        try
        {
            var list = files.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
            if (list.Count == 0) return;

            var entry = new Entry(
                DateTime.UtcNow.ToString("o"), sessionId ?? "", repo ?? "", list, root ?? "");
            var line = JsonSerializer.Serialize(entry) + "\n";

            lock (_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.AppendAllText(_path, line, NoBom);
            }
        }
        catch (Exception ex)
        {
            _log($"claim journal: could not record grant ({ex.Message})");
        }
    }

    /// <summary>Every path ever claimed in one repo, normalised for comparison.
    /// A corrupt line is skipped, never fatal — an audit that throws stops auditing.</summary>
    public HashSet<string> ClaimedIn(string repo)
    {
        var set = CommitAudit.ClaimedSet(Array.Empty<string>());
        Read(new[] { repo }, (files, _) =>
        {
            foreach (var f in files) set.Add(CommitAudit.Norm(f));
        });
        return set;
    }

    /// <summary>
    /// Root-aware index across every repo NAME that maps to one git repository. The
    /// union matters: a claim can be recorded under one registered name while the commit
    /// is observed under another that points into the same checkout.
    /// </summary>
    public ClaimedIndex IndexFor(IEnumerable<string> repoNames)
    {
        var idx = new ClaimedIndex();
        Read(repoNames, (files, root) => idx.AddClaim(root, files));
        return idx;
    }

    private void Read(IEnumerable<string> repoNames, Action<List<string>, string> onEntry)
    {
        var wanted = new HashSet<string>(repoNames, StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(_path)) return;
            foreach (var line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                Entry? e;
                try { e = JsonSerializer.Deserialize<Entry>(line); }
                catch { continue; }
                if (e?.Files == null) continue;
                if (!wanted.Contains(e.Repo)) continue;
                onEntry(e.Files, e.Root ?? "");
            }
        }
        catch (Exception ex)
        {
            _log($"claim journal: could not read ({ex.Message})");
        }
    }
}
