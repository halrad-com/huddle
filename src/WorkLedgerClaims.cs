using System.Text;

namespace Huddle;

/// <summary>
/// A single claim entry: one session holds a set of files in one repo for the duration of a batch task.
/// Claims are written to ipc/workledger/claims/ as structured markdown, read back for overlap detection,
/// and deleted on release or session stop.
/// </summary>
public sealed record WorkLedgerClaim(
    string SessionId,       // display form, e.g. "huddle:backenddev"
    string Repo,            // repo name, e.g. "myapp"
    string BatchId,         // e.g. "B-20260421-231500"
    DateTime ClaimedAt,     // UTC
    string BaseCommit,      // 40-char sha
    IReadOnlyList<string> Files,  // repo-relative paths
    string OwnerGuid = ""   // owning session's conversation GUID; "" for legacy/unknown.
                            // Distinguishes the claiming INSTANCE from a later session
                            // that reuses the same name, so orphan reaping is precise.
);

/// <summary>
/// Overlap finding result: the files shared by two claims.
/// </summary>
public sealed record ClaimOverlap(
    WorkLedgerClaim A,
    WorkLedgerClaim B,
    IReadOnlyList<string> SharedFiles
);

public class WorkLedgerClaims
{
    private readonly string _claimsDir;
    private readonly Action<string> _log;
    private readonly object _lock = new();

    public WorkLedgerClaims(string claimsDir, Action<string> log)
    {
        _claimsDir = claimsDir;
        _log = log;
    }

    /// <summary>
    /// Write a claim file. Path is {claimsDir}/{batchId}-{sessionSafeName}.md.
    /// </summary>
    public string Write(WorkLedgerClaim claim)
    {
        lock (_lock)
        {
            return WriteCore(claim);
        }
    }

    // Caller must hold _lock.
    private string WriteCore(WorkLedgerClaim claim)
    {
        Directory.CreateDirectory(_claimsDir);
        var sessionSafe = claim.SessionId.Replace(':', '_');
        var path = Path.Combine(_claimsDir, $"{claim.BatchId}-{sessionSafe}.md");

        var sb = new StringBuilder();
        sb.AppendLine($"# {claim.BatchId}-{sessionSafe}");
        sb.AppendLine();
        sb.AppendLine($"- **Session:** {claim.SessionId}");
        sb.AppendLine($"- **Repo:** {claim.Repo}");
        sb.AppendLine($"- **Batch:** {claim.BatchId}");
        sb.AppendLine($"- **Claimed at:** {claim.ClaimedAt:yyyy-MM-ddTHH:mm:ssZ}");
        sb.AppendLine($"- **Base commit:** {claim.BaseCommit}");
        // Owner GUID is written BEFORE the Files list so the parser reads it while
        // still outside the files section (the files loop swallows any "- " line).
        if (!string.IsNullOrEmpty(claim.OwnerGuid))
            sb.AppendLine($"- **Owner:** {claim.OwnerGuid}");
        sb.AppendLine("- **Files:**");
        foreach (var file in claim.Files)
            sb.AppendLine($"  - {file}");

        File.WriteAllText(path, sb.ToString());
        return path;
    }

    /// <summary>
    /// Atomically check a proposed claim against every active claim and write it
    /// only if no OTHER session holds any of its files (a session may always extend
    /// its own scope). Check-and-write happen under one lock so two concurrent
    /// claimants cannot both win the same file. On rejection nothing is written
    /// and the conflicts name each holder and the shared files.
    /// </summary>
    public bool TryClaim(WorkLedgerClaim claim, out List<ClaimOverlap> conflicts)
    {
        lock (_lock)
        {
            var active = ReadAll(); // Monitor is reentrant — safe under _lock
            conflicts = FindConflictsWithActive(new[] { claim }, active);
            if (conflicts.Count > 0) return false;
            WriteCore(claim);
            return true;
        }
    }

    /// <summary>
    /// Read and parse a single claim file. Returns null on malformed input (and logs).
    /// </summary>
    public WorkLedgerClaim? ReadFile(string path)
    {
        try
        {
            var lines = File.ReadAllLines(path);
            string session = "", repo = "", batch = "", baseCommit = "", ownerGuid = "";
            DateTime claimedAt = default;
            var files = new List<string>();
            var inFiles = false;

            foreach (var raw in lines)
            {
                var line = raw.TrimStart();
                if (line.StartsWith("- **Session:**")) session = AfterColon(line);
                else if (line.StartsWith("- **Repo:**")) repo = AfterColon(line);
                else if (line.StartsWith("- **Batch:**")) batch = AfterColon(line);
                else if (line.StartsWith("- **Claimed at:**"))
                {
                    var txt = AfterColon(line);
                    DateTime.TryParse(txt, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out claimedAt);
                }
                else if (line.StartsWith("- **Base commit:**")) baseCommit = AfterColon(line);
                else if (line.StartsWith("- **Owner:**")) ownerGuid = AfterColon(line);
                else if (line.StartsWith("- **Files:**")) { inFiles = true; }
                else if (inFiles && line.StartsWith("- "))
                {
                    files.Add(line[2..].Trim());
                }
                else if (inFiles && !string.IsNullOrWhiteSpace(line) && !line.StartsWith("-"))
                {
                    inFiles = false;
                }
            }

            if (string.IsNullOrEmpty(session) || string.IsNullOrEmpty(batch))
            {
                _log($"WorkLedgerClaims: malformed claim file {Path.GetFileName(path)} (missing Session/Batch)");
                return null;
            }

            return new WorkLedgerClaim(session, repo, batch, claimedAt, baseCommit, files, ownerGuid);
        }
        catch (Exception ex)
        {
            _log($"WorkLedgerClaims: error reading {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Read all claim files in the claims directory. Malformed files are skipped (with a log).
    /// </summary>
    public List<WorkLedgerClaim> ReadAll()
    {
        lock (_lock)
        {
            var result = new List<WorkLedgerClaim>();
            if (!Directory.Exists(_claimsDir)) return result;
            foreach (var path in Directory.GetFiles(_claimsDir, "*.md"))
            {
                var claim = ReadFile(path);
                if (claim != null) result.Add(claim);
            }
            return result;
        }
    }

    /// <summary>
    /// Remove specified files from a session's claim(s). If a claim has no files remaining, delete it.
    /// Returns how many files were released (across possibly multiple claim files).
    /// </summary>
    public int Release(string sessionId, IEnumerable<string> files)
    {
        lock (_lock)
        {
            var toRelease = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
            int released = 0;
            foreach (var path in Directory.GetFiles(_claimsDir, "*.md"))
            {
                var claim = ReadFile(path);
                if (claim == null) continue;
                if (!claim.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase)) continue;

                var remaining = claim.Files.Where(f => !toRelease.Contains(f)).ToList();
                var matched = claim.Files.Count - remaining.Count;
                released += matched;

                if (remaining.Count == 0)
                {
                    File.Delete(path);
                }
                else if (matched > 0)
                {
                    WriteCore(claim with { Files = remaining });
                }
            }
            return released;
        }
    }

    /// <summary>
    /// Delete all claims held by a session. Returns the list of claims removed (so the caller can audit).
    /// </summary>
    public List<WorkLedgerClaim> DeleteAllForSession(string sessionId)
    {
        lock (_lock)
        {
            var removed = new List<WorkLedgerClaim>();
            foreach (var path in Directory.GetFiles(_claimsDir, "*.md"))
            {
                var claim = ReadFile(path);
                if (claim == null) continue;
                if (!claim.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase)) continue;
                File.Delete(path);
                removed.Add(claim);
            }
            return removed;
        }
    }

    /// <summary>
    /// A live session as the reaper sees it: canonical instance id, its conversation GUID
    /// (null for --continue sessions), and when it started. Passed in as plain data so the
    /// reap decision stays a pure, unit-testable function with no SessionManager dependency.
    /// </summary>
    public readonly record struct LiveInstance(string InstanceId, Guid? Guid, DateTime? StartedAt);

    /// <summary>
    /// True when no currently-live session owns this claim — i.e. the claim is stranded and
    /// safe to reap. Identity is the conversation GUID when the claim carries one: a match is
    /// definitive and a recycled name cannot shield a dead instance. A legacy claim (no GUID)
    /// falls back to name identity, but a live instance that started AFTER the claim is a
    /// different instance reusing the name and does not own it. Name comparison is form-agnostic
    /// (colon vs underscore) and case-insensitive.
    /// </summary>
    public static bool IsOrphan(WorkLedgerClaim claim, IReadOnlyList<LiveInstance> live)
    {
        if (Guid.TryParse(claim.OwnerGuid, out var g) && g != Guid.Empty)
            return !live.Any(l => l.Guid == g);

        // Legacy claim: match by name, but reject an instance that started after the claim.
        var claimName = Safe(claim.SessionId);
        foreach (var l in live)
        {
            if (!Safe(l.InstanceId).Equals(claimName, StringComparison.OrdinalIgnoreCase))
                continue;
            // Unknown start time → conservatively assume this live instance owns it (don't reap).
            if (l.StartedAt is null || l.StartedAt.Value <= claim.ClaimedAt)
                return false;
        }
        return true;

        static string Safe(string id) => id.Replace(':', '_');
    }

    /// <summary>
    /// Archive every claim whose owning instance is no longer live (see <see cref="IsOrphan"/>).
    /// Orphans are MOVED into an "archived-orphan-yyyyMMdd" subdirectory of the claims dir —
    /// reversible, auditable, and invisible to <see cref="ReadAll"/> (which is non-recursive) —
    /// never hard-deleted. Returns the claims that were archived.
    /// </summary>
    public List<WorkLedgerClaim> ReapOrphans(IReadOnlyList<LiveInstance> live)
    {
        lock (_lock)
        {
            var archived = new List<WorkLedgerClaim>();
            if (!Directory.Exists(_claimsDir)) return archived;

            var archiveDir = Path.Combine(_claimsDir, $"archived-orphan-{DateTime.UtcNow:yyyyMMdd}");
            foreach (var path in Directory.GetFiles(_claimsDir, "*.md"))
            {
                var claim = ReadFile(path);
                if (claim == null || !IsOrphan(claim, live)) continue;

                Directory.CreateDirectory(archiveDir);
                var dest = Path.Combine(archiveDir, Path.GetFileName(path));
                if (File.Exists(dest)) // near-impossible (unique claim ids); never overwrite/delete
                    dest = Path.Combine(archiveDir, $"{Path.GetFileNameWithoutExtension(path)}-{DateTime.UtcNow.Ticks}.md");
                File.Move(path, dest);
                archived.Add(claim);
            }
            return archived;
        }
    }

    /// <summary>
    /// Find overlaps: every pair of claims that share at least one file.
    /// Used both for self-check within a proposed batch and for check-against-active.
    /// </summary>
    public static List<ClaimOverlap> FindOverlaps(IReadOnlyList<WorkLedgerClaim> claims)
    {
        var overlaps = new List<ClaimOverlap>();
        for (int i = 0; i < claims.Count; i++)
        {
            var a = claims[i];
            var aFiles = new HashSet<string>(a.Files, StringComparer.OrdinalIgnoreCase);
            for (int j = i + 1; j < claims.Count; j++)
            {
                var b = claims[j];
                var shared = b.Files.Where(f => aFiles.Contains(f)).ToList();
                if (shared.Count > 0)
                    overlaps.Add(new ClaimOverlap(a, b, shared));
            }
        }
        return overlaps;
    }

    /// <summary>
    /// Find where any of the proposed claims overlap any of the existing active claims (held by a different session).
    /// </summary>
    public static List<ClaimOverlap> FindConflictsWithActive(
        IReadOnlyList<WorkLedgerClaim> proposed,
        IReadOnlyList<WorkLedgerClaim> active)
    {
        var conflicts = new List<ClaimOverlap>();
        foreach (var p in proposed)
        {
            var pFiles = new HashSet<string>(p.Files, StringComparer.OrdinalIgnoreCase);
            foreach (var a in active)
            {
                if (a.SessionId.Equals(p.SessionId, StringComparison.OrdinalIgnoreCase))
                    continue; // same session can extend its own claim
                var shared = a.Files.Where(f => pFiles.Contains(f)).ToList();
                if (shared.Count > 0)
                    conflicts.Add(new ClaimOverlap(p, a, shared));
            }
        }
        return conflicts;
    }

    private static string AfterColon(string line)
    {
        // Label format is always `- **Key:** value`. Find the ":**" that closes the label.
        var marker = line.IndexOf(":**", StringComparison.Ordinal);
        if (marker < 0) return "";
        var start = marker + 3; // skip past ":**"
        if (start >= line.Length) return "";
        return line[start..].Trim();
    }
}
