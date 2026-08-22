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
    string OwnerGuid = "",  // owning session's conversation GUID; "" for legacy/unknown.
                            // Distinguishes the claiming INSTANCE from a later session
                            // that reuses the same name, so orphan reaping is precise.
    string Project = "",    // project slug this claim serves; "" for legacy/unstamped.
    string Root = "",       // ABSOLUTE directory the Files are relative to — the session's
                            // real checkout, recorded rather than inferred from Repo (I014).
                            // "" for legacy/unstamped claims, which fall back to resolving
                            // the repo NAME through the registry exactly as before.
    string Branch = ""      // branch that checkout had out when the claim was made.
                            // INFORMATIONAL ONLY — never part of a collision decision;
                            // "" means detached, unreadable, or simply not recorded.
);

/// <summary>
/// What identifies a checkout to the merge-risk test: the git object store it shares with
/// every other worktree of the same repo (<c>--git-common-dir</c>), and this worktree's own
/// top (<c>--show-toplevel</c>). Two roots are SIBLING checkouts when the stores match and
/// the tops differ; matching tops mean two directories inside ONE working copy, which is a
/// path question, not a merge question. Passed in as data so the ledger shells out to git
/// nowhere and the discrimination stays unit-testable.
/// </summary>
public readonly record struct CheckoutInfo(string Store, string Top);

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
    private readonly Func<string, string?>? _resolveRoot;
    private readonly Func<string, CheckoutInfo?>? _identifyCheckout;
    private readonly object _lock = new();

    /// <param name="resolveRoot">
    /// Repo name (or alias) → its absolute root on disk; null for an unknown name.
    /// Optional: when it is null, or returns null, collision detection falls back to
    /// comparing repo NAMES exactly as it did before I013 (see <see cref="ReposCollide"/>).
    /// Injected rather than looked up so the ledger keeps no repo registry of its own —
    /// the orchestrator's <c>SessionManager</c> stays the single source of truth for
    /// what a repo name means.
    /// </param>
    /// <param name="identifyCheckout">
    /// Absolute checkout root → its <see cref="CheckoutInfo"/>, or null when the directory is
    /// not a git checkout (or git cannot be asked). Used ONLY for the non-blocking merge-risk
    /// report (I014); null here simply narrows that report, it never changes what collides.
    /// </param>
    public WorkLedgerClaims(
        string claimsDir,
        Action<string> log,
        Func<string, string?>? resolveRoot = null,
        Func<string, CheckoutInfo?>? identifyCheckout = null)
    {
        _claimsDir = claimsDir;
        _log = log;
        _resolveRoot = resolveRoot;
        _identifyCheckout = identifyCheckout;
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
        // Project too must precede Files (the files loop swallows any "- " line).
        if (!string.IsNullOrEmpty(claim.Project))
            sb.AppendLine($"- **Project:** {claim.Project}");
        // Root/Branch likewise precede Files. Both are omitted when empty so a claim from a
        // session that cannot say where it is looks exactly like every claim written before
        // I014 — the parser defaults them to "" and every comparison degrades to the repo
        // registry, so an old claims directory keeps working untouched.
        if (!string.IsNullOrEmpty(claim.Root))
            sb.AppendLine($"- **Root:** {claim.Root}");
        if (!string.IsNullOrEmpty(claim.Branch))
            sb.AppendLine($"- **Branch:** {claim.Branch}");
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
            conflicts = FindConflictsWithActive(new[] { claim }, active, _resolveRoot);
            if (conflicts.Count > 0) return false;
            WriteCore(claim);
            return true;
        }
    }

    /// <summary>
    /// As <see cref="TryClaim(WorkLedgerClaim, out List{ClaimOverlap})"/>, but a conflict against a
    /// DEAD session's claim does not block the claimant: orphan holders are reaped inline (archived,
    /// reversible) and the claim is re-checked against what remains. Closes the window where a stale
    /// claim from an exited session forced a manual `conflicts` sweep before a re-claim could succeed.
    /// The <paramref name="live"/> roster is the reap authority; an EMPTY roster disables reaping
    /// entirely (during incomplete recovery every claim looks orphaned) — mirrors
    /// <c>Orchestrator.ReapOrphanClaims</c>' empty-set guard.
    /// </summary>
    public bool TryClaim(WorkLedgerClaim claim, IReadOnlyList<LiveInstance> live, out List<ClaimOverlap> conflicts)
    {
        lock (_lock)
        {
            var active = ReadAll();
            conflicts = FindConflictsWithActive(new[] { claim }, active, _resolveRoot);
            if (conflicts.Count == 0) { WriteCore(claim); return true; }

            // A live claimant blocked only by dead sessions' claims should win. With no live
            // roster we cannot tell dead from live, so leave the conflict standing.
            if (live.Count == 0) return false;
            if (ReapOrphans(live).Count == 0) return false; // holder(s) are live — genuine conflict

            active = ReadAll();
            conflicts = FindConflictsWithActive(new[] { claim }, active, _resolveRoot);
            if (conflicts.Count > 0) return false;
            WriteCore(claim);
            return true;
        }
    }

    /// <summary>
    /// Record a claim and report every OTHER session already holding one of its files.
    /// Unlike <see cref="TryClaim(WorkLedgerClaim, out List{ClaimOverlap})"/> this never refuses:
    /// the claim is ALWAYS written, so it lands whether or not an arbiter is alive, and the
    /// overlaps are the claimant's cue to go talk to the other holder.
    ///
    /// Read-compare-write is one critical section, guarded by a MACHINE-scoped named mutex as
    /// well as the in-process lock. The claimants here are separate huddle processes, so an
    /// in-process lock alone would close the race in tests and leave it open in production:
    /// two processes could both read a clean ledger before either wrote, and both be told
    /// there is no conflict — the one outcome the ledger exists to prevent.
    ///
    /// EVERY mutex failure degrades to recording unguarded, never to refusing. Constructing
    /// the Mutex is itself inside the try because opening an existing one can throw
    /// UnauthorizedAccessException — an elevated huddle creates a high-integrity kernel
    /// object that a medium-integrity opener may not touch, even as the same user — and a
    /// throw there would REFUSE the claim, contradicting the whole design in the one place
    /// that must not.
    /// </summary>
    public string RecordWithOverlaps(WorkLedgerClaim claim, out List<ClaimOverlap> overlaps)
        => RecordWithOverlaps(claim, out overlaps, out _);

    /// <summary>
    /// As <see cref="RecordWithOverlaps(WorkLedgerClaim, out List{ClaimOverlap})"/>, and additionally
    /// reports <paramref name="mergeWarnings"/>: holders of the SAME repo-relative path in a
    /// sibling checkout of the same repo (I014). Those are not overlaps — nobody can overwrite
    /// anybody — but they are a guaranteed merge conflict, which the ledger used to report as
    /// silence. Warnings never affect whether the claim is written; nothing here can refuse.
    /// </summary>
    public string RecordWithOverlaps(
        WorkLedgerClaim claim, out List<ClaimOverlap> overlaps, out List<ClaimOverlap> mergeWarnings)
    {
        Mutex? mutex = null;
        var held = false;
        try
        {
            try
            {
                mutex = new Mutex(initiallyOwned: false, LedgerMutexName(_claimsDir));
                held = mutex.WaitOne(TimeSpan.FromSeconds(30));
                if (!held)
                    _log($"WorkLedgerClaims: timed out waiting for the ledger mutex; recording {claim.BatchId} unguarded");
            }
            catch (AbandonedMutexException)
            {
                // A previous holder died mid-section. Each claim is its own file, so there is
                // no half-written ledger to roll back — we now own the mutex; carry on.
                held = true;
                _log("WorkLedgerClaims: ledger mutex was abandoned by a dead holder; continuing");
            }
            catch (Exception ex)
            {
                // Unopenable mutex (integrity level, name collision with a non-mutex object,
                // resource exhaustion). Same outcome as the timeout above: say so and record
                // anyway. Losing the cross-process guard costs a rare interleaved read; losing
                // the claim costs the invisibility that caused the incident.
                held = false;
                _log($"WorkLedgerClaims: cannot open the ledger mutex ({ex.GetType().Name}: {ex.Message}); " +
                     $"recording {claim.BatchId} unguarded");
            }

            lock (_lock)
            {
                var active = ReadAll(); // Monitor is reentrant — safe under _lock
                overlaps = FindConflictsWithActive(new[] { claim }, active, _resolveRoot);
                mergeWarnings = FindMergeWarnings(new[] { claim }, active, _resolveRoot, _identifyCheckout);
                return WriteCore(claim);
            }
        }
        finally
        {
            if (held) mutex!.ReleaseMutex();
            mutex?.Dispose();
        }
    }

    /// <summary>
    /// Machine-scoped mutex name for one claims directory. Keyed by a hash of the normalized
    /// directory path so that every process pointed at the SAME ledger contends, while two
    /// huddle roots on one machine do not. Local\ (this session), not Global\ — the ledger is
    /// per-user. Hex because a mutex name may not contain a backslash past the namespace prefix.
    /// </summary>
    private static string LedgerMutexName(string claimsDir)
    {
        string key;
        try { key = Path.GetFullPath(claimsDir); }
        catch { key = claimsDir; } // unresolvable path: hash it as given rather than fail the claim
        key = key.TrimEnd('/', '\\').ToLowerInvariant();
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return @"Local\huddle-ledger-" + Convert.ToHexString(hash, 0, 8);
    }

    /// <summary>
    /// Read and parse a single claim file. Returns null on malformed input (and logs).
    /// </summary>
    public WorkLedgerClaim? ReadFile(string path)
    {
        try
        {
            var lines = File.ReadAllLines(path);
            string session = "", repo = "", batch = "", baseCommit = "", ownerGuid = "", project = "";
            string root = "", branch = "";
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
                else if (line.StartsWith("- **Project:**")) project = AfterColon(line);
                else if (line.StartsWith("- **Root:**")) root = AfterColon(line);
                else if (line.StartsWith("- **Branch:**")) branch = AfterColon(line);
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

            return new WorkLedgerClaim(session, repo, batch, claimedAt, baseCommit, files, ownerGuid, project, root, branch);
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
            // The ledger directory may not exist yet: huddle may never have started, which is
            // exactly the state direct claim access exists to survive. Releasing before anything
            // was ever claimed releases nothing — a no-op, not an exception. (Same guard as ReadAll.)
            if (!Directory.Exists(_claimsDir)) return 0;

            // Path-normalized (same rule as conflict matching) so a claim written with
            // one separator style can be released with the other.
            var toRelease = new HashSet<string>(files.Select(NormPath), StringComparer.OrdinalIgnoreCase);
            int released = 0;
            foreach (var path in Directory.GetFiles(_claimsDir, "*.md"))
            {
                var claim = ReadFile(path);
                if (claim == null) continue;
                if (!claim.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase)) continue;

                var remaining = claim.Files.Where(f => !toRelease.Contains(NormPath(f))).ToList();
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
            // Same absent-ledger guard as Release/ReadAll: a session stopping before it ever
            // claimed anything has nothing to delete and must not throw on the way out.
            if (!Directory.Exists(_claimsDir)) return removed;

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
    /// Name-only repo comparison — the pre-I013 rule, kept as the FALLBACK for any pair
    /// whose roots cannot both be resolved. Claim file paths are repo-relative, so two
    /// claims only collide when they name the SAME repo (I008: huddle README.md must not
    /// block corelib README.md). Fail-safe: a claim with no repo recorded (legacy/malformed
    /// file) collides with every repo — an old claim never silently loses its I005
    /// protection.
    /// </summary>
    private static bool ReposCollide(string a, string b) =>
        string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b) ||
        a.Equals(b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Normalize a repo-relative path for comparison: backslashes to forward slashes,
    /// trimmed, leading "./" stripped — `src\a.cs`, `./src/a.cs`, and `src/a.cs` are
    /// the same file and must collide (guardrail against separator-defeated conflicts).
    /// </summary>
    private static string NormPath(string f)
    {
        var p = f.Trim().Replace('\\', '/');
        while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        return p;
    }

    /// <summary>
    /// A claim's repo root as an absolute, normalized path — or null when it cannot be
    /// established (no resolver injected, empty repo on a legacy claim, a name the registry
    /// does not know, or an unusable root). Null is the signal to fall back to
    /// <see cref="ReposCollide"/>; it never means "no conflict".
    /// </summary>
    private static string? TryResolveRoot(string repo, Func<string, string?>? resolveRoot)
    {
        if (resolveRoot == null || string.IsNullOrWhiteSpace(repo)) return null;
        try
        {
            var root = resolveRoot(repo);
            if (string.IsNullOrWhiteSpace(root)) return null;
            return Path.GetFullPath(root);
        }
        catch
        {
            // A resolver that throws, or a root that is not a legal path, must not take the
            // ledger down mid-claim. Degrade to the name comparison — over-report, never
            // under-protect.
            return null;
        }
    }

    /// <summary>
    /// The directory a claim's files are relative to, most trustworthy source first (I014):
    /// the checkout root the session RECORDED, else the root the repo registry resolves from
    /// the declared name (I013), else null — which is the signal to fall back to
    /// <see cref="ReposCollide"/> and never means "no conflict".
    ///
    /// The recorded root wins because it is observed rather than inferred. A session working
    /// in a worktree has no correct repo name to declare; the name it is forced to use points
    /// at a checkout it is not in, and trusting the name over the session's own working
    /// directory is precisely the defect.
    /// </summary>
    private static string? EffectiveRoot(WorkLedgerClaim claim, Func<string, string?>? resolveRoot)
    {
        if (!string.IsNullOrWhiteSpace(claim.Root))
        {
            try { return Path.GetFullPath(claim.Root); }
            catch { /* an unusable recorded root falls through to the registry */ }
        }
        return TryResolveRoot(claim.Repo, resolveRoot);
    }

    /// <summary>
    /// Absolute comparison key for one claimed file under a resolved root. This is the
    /// I013 fix: `LIB-root` + `myapp/MBXS/x.cs` and `myapp` + `MBXS/x.cs` are the
    /// same physical file and produce the same key, while two worktrees produce different
    /// ones. Case-insensitive comparison is the caller's job (Windows).
    /// </summary>
    private static string AbsKey(string root, string file)
    {
        var rel = NormPath(file);
        try { return Path.GetFullPath(Path.Combine(root, rel)); }
        catch { return root.TrimEnd('/', '\\') + Path.DirectorySeparatorChar + rel; }
    }

    /// <summary>
    /// The absolute path one claimed file lands on under its claim's repo root, or null when
    /// the root cannot be established (no resolver, empty/unknown repo, unusable root — the
    /// same conditions that put a pair back on the name comparison).
    ///
    /// REPORTING ONLY. The collision decision stays in <see cref="FindOverlaps"/>; this exists
    /// so the `conflicts` view can SHOW the operator which physical file two differently-spelled
    /// claims share, without a second copy of the root+relative mapping (I013).
    /// </summary>
    public static string? TryAbsolutePath(string repo, string file, Func<string, string?>? resolveRoot)
    {
        var root = TryResolveRoot(repo, resolveRoot);
        return root == null ? null : AbsKey(root, file);
    }

    /// <summary>
    /// As <see cref="TryAbsolutePath(string, string, Func{string, string?})"/>, but honouring the
    /// claim's own recorded checkout root ahead of its declared repo name (I014). Prefer this
    /// overload wherever a whole claim is in hand: the name-only form places a worktree
    /// session's file in the wrong checkout, which is the thing being fixed.
    /// </summary>
    public static string? TryAbsolutePath(WorkLedgerClaim claim, string file, Func<string, string?>? resolveRoot)
    {
        var root = EffectiveRoot(claim, resolveRoot);
        return root == null ? null : AbsKey(root, file);
    }

    /// <summary>
    /// The comparison basis for one PAIR of claims: the set of A's file keys plus the
    /// function that maps one of B's files to the same key space. Null means the pair can
    /// never collide and may be skipped.
    ///
    /// Absolute keys are used only when BOTH roots resolve; otherwise the pair drops to the
    /// pre-I013 name gate + repo-relative paths. That asymmetry is deliberate — half a
    /// resolution is not enough to prove two files are distinct, and the fallback is the
    /// direction that over-reports.
    /// </summary>
    private static (HashSet<string> AKeys, Func<string, string> BKey)? PairBasis(
        WorkLedgerClaim a, WorkLedgerClaim b, Func<string, string?>? resolveRoot)
    {
        var rootA = EffectiveRoot(a, resolveRoot);
        var rootB = EffectiveRoot(b, resolveRoot);
        if (rootA != null && rootB != null)
        {
            var abs = new HashSet<string>(a.Files.Select(f => AbsKey(rootA, f)), StringComparer.OrdinalIgnoreCase);
            return (abs, f => AbsKey(rootB, f));
        }

        if (!ReposCollide(a.Repo, b.Repo)) return null;
        var rel = new HashSet<string>(a.Files.Select(NormPath), StringComparer.OrdinalIgnoreCase);
        return (rel, NormPath);
    }

    /// <summary>
    /// Find overlaps: every pair of claims that share at least one file, compared on
    /// RESOLVED ABSOLUTE PATHS when <paramref name="resolveRoot"/> can place both repos
    /// (I013) and on repo name + repo-relative path otherwise. Used both for self-check
    /// within a proposed batch and for check-against-active.
    /// </summary>
    public static List<ClaimOverlap> FindOverlaps(
        IReadOnlyList<WorkLedgerClaim> claims,
        Func<string, string?>? resolveRoot = null)
    {
        var overlaps = new List<ClaimOverlap>();
        for (int i = 0; i < claims.Count; i++)
        {
            var a = claims[i];
            for (int j = i + 1; j < claims.Count; j++)
            {
                var b = claims[j];
                // Keys are per-PAIR: the same file has a different key depending on which
                // root the other claim resolved under, so they cannot be hoisted out of
                // the inner loop. Ledgers hold a handful of claims; correctness wins.
                var basis = PairBasis(a, b, resolveRoot);
                if (basis == null) continue;
                var shared = b.Files.Where(f => basis.Value.AKeys.Contains(basis.Value.BKey(f))).ToList();
                if (shared.Count > 0)
                    overlaps.Add(new ClaimOverlap(a, b, shared));
            }
        }
        return overlaps;
    }

    /// <summary>
    /// Find where any of the proposed claims overlap any of the existing active claims
    /// (held by a different session, on the same physical file — see <see cref="PairBasis"/>).
    /// </summary>
    public static List<ClaimOverlap> FindConflictsWithActive(
        IReadOnlyList<WorkLedgerClaim> proposed,
        IReadOnlyList<WorkLedgerClaim> active,
        Func<string, string?>? resolveRoot = null)
    {
        var conflicts = new List<ClaimOverlap>();
        foreach (var p in proposed)
        {
            foreach (var a in active)
            {
                if (a.SessionId.Equals(p.SessionId, StringComparison.OrdinalIgnoreCase))
                    continue; // same session can extend its own claim
                var basis = PairBasis(p, a, resolveRoot);
                if (basis == null) continue;
                var shared = a.Files.Where(f => basis.Value.AKeys.Contains(basis.Value.BKey(f))).ToList();
                if (shared.Count > 0)
                    conflicts.Add(new ClaimOverlap(p, a, shared));
            }
        }
        return conflicts;
    }

    /// <summary>
    /// The THIRD outcome (I014): pairs that do NOT collide — different absolute paths, so
    /// neither can overwrite the other — but hold the same path in two SIBLING checkouts of
    /// one repo, which is a guaranteed merge conflict later. Never blocking; a report.
    ///
    /// Deciding "sibling checkout" is the whole difficulty, because two unrelated repos share
    /// relative paths constantly (`src/Program.cs`, `README.md`) and warning about those would
    /// rebuild I008 as a warning — the same lost signal by a slower route. Two real signals,
    /// in order:
    ///
    /// 1. <paramref name="identifyCheckout"/>: worktrees of one repo share a git object store
    ///    (<c>--git-common-dir</c>) and have DIFFERENT worktree tops. Same store + same top is
    ///    two directories inside one working copy (`LIB-root` and `myapp`) — different
    ///    files on one branch, no merge risk. Paths are then compared relative to each
    ///    worktree TOP, so a registration that sits below the top lines up with its sibling.
    /// 2. No identity available (git absent, or neither root is a checkout): the narrow rule —
    ///    both claims recorded a Root, the roots differ, and the declared repo NAMES match.
    ///    Narrow and right beats broad and noisy.
    ///
    /// A pair reported here can never also be reported as a collision: identical absolute
    /// paths are excluded by construction (different roots, equal top-relative paths).
    /// </summary>
    public static List<ClaimOverlap> FindMergeWarnings(
        IReadOnlyList<WorkLedgerClaim> proposed,
        IReadOnlyList<WorkLedgerClaim> active,
        Func<string, string?>? resolveRoot = null,
        Func<string, CheckoutInfo?>? identifyCheckout = null)
    {
        var warnings = new List<ClaimOverlap>();
        // One identity lookup per distinct root: it may shell out to git, and a ledger pair
        // loop would otherwise ask about the same checkout repeatedly.
        var cache = new Dictionary<string, CheckoutInfo?>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in proposed)
        {
            foreach (var a in active)
            {
                if (a.SessionId.Equals(p.SessionId, StringComparison.OrdinalIgnoreCase))
                    continue; // a session in two checkouts is still one session's own business
                var basis = WarnBasis(p, a, resolveRoot, identifyCheckout, cache);
                if (basis == null) continue;
                var shared = a.Files.Where(f => basis.Value.AKeys.Contains(basis.Value.BKey(f))).ToList();
                if (shared.Count > 0)
                    warnings.Add(new ClaimOverlap(p, a, shared));
            }
        }
        return warnings;
    }

    /// <summary>
    /// The merge-risk comparison basis for one PAIR, or null when the pair cannot be a merge
    /// risk at all. Mirrors <see cref="PairBasis"/> in shape so the two comparisons read the
    /// same way, but keys on the path relative to each claim's WORKTREE TOP rather than on the
    /// absolute path — that is exactly the equality "the same file on two branches" means.
    /// </summary>
    private static (HashSet<string> AKeys, Func<string, string> BKey)? WarnBasis(
        WorkLedgerClaim a, WorkLedgerClaim b,
        Func<string, string?>? resolveRoot,
        Func<string, CheckoutInfo?>? identifyCheckout,
        Dictionary<string, CheckoutInfo?> cache)
    {
        var rootA = EffectiveRoot(a, resolveRoot);
        var rootB = EffectiveRoot(b, resolveRoot);
        // No root on either side means the pair is already compared by NAME, where the same
        // relative path IS a collision — it is reported by the louder channel, not this one.
        if (rootA == null || rootB == null) return null;
        // Same directory: any shared file is a collision, not a merge risk.
        if (rootA.Equals(rootB, StringComparison.OrdinalIgnoreCase)) return null;

        string subA = "", subB = "";
        var idA = Identify(identifyCheckout, rootA, cache);
        var idB = Identify(identifyCheckout, rootB, cache);
        if (idA != null && idB != null)
        {
            // Different repos entirely — the I008 guard, and the reason this is not a
            // "same relative filename" test.
            if (!idA.Value.Store.Equals(idB.Value.Store, StringComparison.OrdinalIgnoreCase)) return null;
            // One working copy, two directories inside it: different files, one branch.
            if (idA.Value.Top.Equals(idB.Value.Top, StringComparison.OrdinalIgnoreCase)) return null;
            subA = GitWorktrees.SubPath(idA.Value.Top, rootA);
            subB = GitWorktrees.SubPath(idB.Value.Top, rootB);
        }
        else
        {
            // Narrow fallback: only claims that positively recorded where they are, under the
            // same declared repo name. Anything less would warn about unrelated repos.
            if (string.IsNullOrWhiteSpace(a.Root) || string.IsNullOrWhiteSpace(b.Root)) return null;
            if (string.IsNullOrWhiteSpace(a.Repo) ||
                !a.Repo.Equals(b.Repo, StringComparison.OrdinalIgnoreCase)) return null;
        }

        var keys = new HashSet<string>(a.Files.Select(f => WarnKey(subA, f)), StringComparer.OrdinalIgnoreCase);
        return (keys, f => WarnKey(subB, f));
    }

    /// <summary>One claimed file as its worktree-top-relative path — the merge-risk key.</summary>
    private static string WarnKey(string sub, string file)
    {
        var rel = NormPath(file);
        return string.IsNullOrEmpty(sub) ? rel : NormPath(sub).TrimEnd('/') + "/" + rel;
    }

    private static CheckoutInfo? Identify(
        Func<string, CheckoutInfo?>? identifyCheckout, string root, Dictionary<string, CheckoutInfo?> cache)
    {
        if (identifyCheckout == null) return null;
        if (cache.TryGetValue(root, out var cached)) return cached;
        CheckoutInfo? info;
        // A throwing or unavailable identity costs the warning, never the claim.
        try { info = identifyCheckout(root); }
        catch { info = null; }
        if (info != null &&
            (string.IsNullOrWhiteSpace(info.Value.Store) || string.IsNullOrWhiteSpace(info.Value.Top)))
            info = null;
        cache[root] = info;
        return info;
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
