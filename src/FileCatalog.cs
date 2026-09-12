using System.Security.Cryptography;
using System.Text;

namespace Huddle;

/// <summary>
/// One catalog entry: a file that is currently checked out, and by whom.
///
/// The borrower's IDENTITY IS THE NAME. There is deliberately no GUID, no roster, and no
/// registration step — a session huddle spawned and a Codex session someone opened by hand
/// are the same kind of borrower, holding the same kind of card. That symmetry is the point:
/// the old claim path decided liveness by asking whether the holder was in huddle's roster of
/// sessions it had spawned (<see cref="WorkLedgerClaims.IsOrphan"/>), so an outsider's claim
/// was indistinguishable from a dead session's and got archived — handing the next agent a
/// false all-clear on a file somebody was actively editing.
/// </summary>
public sealed record CatalogEntry(
    string Repo,             // repo name the path is relative to
    string RelPath,          // repo-relative path, forward slashes
    string Borrower,         // self-minted identity, e.g. "codex:refactor" or "huddle:architect"
    DateTime CheckedOutAt,   // UTC
    DateTime DueAt,          // UTC; past-due entries are reclaimable by anyone
    string Root = "",        // absolute checkout the path is relative to; informational
    string Hash = ""         // content hash when the book was taken off the shelf; "" when the
                             // file did not exist yet or could not be read. Katalog's idea:
                             // a checksum turns "who holds it" into "and what did they do to
                             // it", and unlike a version counter it is derived, so it cannot
                             // drift from the bytes on disk.
)
{
    public bool IsOverdue(DateTime nowUtc) => DueAt <= nowUtc;
}

/// <summary>What a checkout attempt did.</summary>
public enum CheckoutOutcome
{
    /// <summary>The caller now holds the file.</summary>
    CheckedOut,
    /// <summary>The caller already held it; the due date moved out.</summary>
    Renewed,
    /// <summary>Someone else holds it and their lease has not expired.</summary>
    HeldByOther,
    /// <summary>The catalog could not be written (permissions, disk).</summary>
    Failed
}

/// <summary>
/// The catalog: a directory of the project's files that are checked out, like a library's
/// card drawer. A file has exactly two states — <b>available</b> (no entry) or <b>checked
/// out</b> (an entry exists). The directory listing IS the "what's out" view, so there is no
/// separate index to drift out of agreement with the truth.
///
/// Why this exists when <see cref="WorkLedgerClaims"/> already tracks claims: the claim path
/// RECORDS and REPORTS. `huddle --claim` always succeeds and prints an overlap afterwards, and
/// <c>TryClaim</c>'s check-and-write is atomic only against callers in the SAME process (see
/// the scope note at Orchestrator.cs:1536) — so two agents in two processes can both believe
/// they hold a file. A catalog entry is created with <see cref="FileMode.CreateNew"/>, which
/// the filesystem makes atomic across processes, users and tools. The create either wins or
/// tells you who got there first. That is the difference between advice and exclusion.
///
/// Liveness is the DUE DATE, not a roster. A borrower that dies stops renewing, its lease
/// lapses, and the next agent reclaims the file — no arbiter has to recognise the borrower or
/// even be running. That is what makes the mechanism work for agents nobody configured.
/// </summary>
public sealed class FileCatalog
{
    /// <summary>How long a checkout is good for when the caller does not say.</summary>
    public static readonly TimeSpan DefaultLease = TimeSpan.FromMinutes(60);

    private readonly string _dir;
    private readonly Func<DateTime> _now;

    public FileCatalog(string catalogDir, Func<DateTime>? nowUtc = null)
    {
        _dir = catalogDir;
        _now = nowUtc ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// The catalog sits beside the claims drawer rather than inside it: everything in
    /// <c>claims/</c> is read by the existing ledger readers, which would choke on a file
    /// shaped differently, and <c>ReadAll</c> there is non-recursive by design.
    /// </summary>
    public static string DirBesideClaims(string claimsDir) =>
        Path.Combine(Path.GetDirectoryName(claimsDir.TrimEnd('/', '\\')) ?? ".", "catalog");

    /// <summary>
    /// Check out one file. Atomic against every other writer on the machine.
    ///
    /// Reclaim semantics: an entry whose lease has lapsed is taken over, because the
    /// alternative is a book lost forever to a borrower that no longer exists. The delete and
    /// the create are separate operations, so two reclaimers can race — but only one
    /// <see cref="FileMode.CreateNew"/> can win, and the loser is told who holds it. The race
    /// is therefore safe, not merely unlikely.
    /// </summary>
    public CheckoutOutcome TryCheckOut(
        string repo, string relPath, string borrower,
        out CatalogEntry? holder, TimeSpan? lease = null, string root = "")
    {
        holder = null;
        if (string.IsNullOrWhiteSpace(borrower)) return CheckoutOutcome.Failed;

        var norm = Normalize(relPath);
        var path = EntryPath(repo, norm);
        var now = _now();
        var entry = new CatalogEntry(repo, norm, borrower, now, now + (lease ?? DefaultLease),
                                     root, HashOf(root, norm));

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var w = new StreamWriter(fs, new UTF8Encoding(false));
                w.Write(Render(entry));
                holder = entry;
                return CheckoutOutcome.CheckedOut;
            }
            catch (IOException)
            {
                // Somebody has this file. Three cases: it is us (renew), their lease has
                // lapsed (reclaim and retry the create), or they hold it (refuse).
                var existing = TryRead(path);
                if (existing == null)
                {
                    // Unreadable or half-written entry. Treat as held rather than steal it:
                    // a corrupt card is not evidence the book is on the shelf.
                    return CheckoutOutcome.Failed;
                }
                if (SameBorrower(existing.Borrower, borrower))
                {
                    var renewed = existing with { DueAt = now + (lease ?? DefaultLease) };
                    if (!TryOverwrite(path, renewed)) return CheckoutOutcome.Failed;
                    holder = renewed;
                    return CheckoutOutcome.Renewed;
                }
                if (!existing.IsOverdue(now))
                {
                    holder = existing;
                    return CheckoutOutcome.HeldByOther;
                }
                try { File.Delete(path); } catch { /* lost the reclaim race; the retry reports the winner */ }
            }
            catch (Exception)
            {
                return CheckoutOutcome.Failed;
            }
        }

        holder = TryRead(path);
        return holder == null ? CheckoutOutcome.Failed : CheckoutOutcome.HeldByOther;
    }

    /// <summary>
    /// Check out several files as one unit: all of them, or none.
    ///
    /// Paths are taken in sorted order so two agents reaching for overlapping sets cannot
    /// deadlock holding half each — with a total order, one of them always gets the lower
    /// path first and the other backs off. On any refusal every file taken in THIS call is
    /// returned before reporting, so a failed checkout leaves the catalog exactly as it was.
    /// </summary>
    public bool TryCheckOutAll(
        string repo, IEnumerable<string> relPaths, string borrower,
        out IReadOnlyList<CatalogEntry> taken, out CatalogEntry? blockedBy, out string blockedPath,
        TimeSpan? lease = null, string root = "")
    {
        var ordered = relPaths.Select(Normalize)
                              .Distinct(StringComparer.OrdinalIgnoreCase)
                              .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                              .ToList();
        var got = new List<CatalogEntry>();
        blockedBy = null;
        blockedPath = "";

        foreach (var p in ordered)
        {
            var outcome = TryCheckOut(repo, p, borrower, out var holder, lease, root);
            if (outcome is CheckoutOutcome.CheckedOut or CheckoutOutcome.Renewed)
            {
                if (holder != null) got.Add(holder);
                continue;
            }
            // Roll back only what this call took, never a file we already held from before:
            // releasing a pre-existing checkout because a LATER file was busy would hand away
            // a book we were still reading.
            foreach (var g in got.Where(g => g.CheckedOutAt >= _now().AddSeconds(-30)))
                CheckIn(repo, g.RelPath, borrower);
            taken = Array.Empty<CatalogEntry>();
            blockedBy = holder;
            blockedPath = p;
            return false;
        }

        taken = got;
        return true;
    }

    /// <summary>
    /// Return a file. Only the borrower who holds it may check it in — returning someone
    /// else's book is how the catalog would start lying. Returns false when the entry is
    /// missing (already available) or held by somebody else.
    /// </summary>
    public bool CheckIn(string repo, string relPath, string borrower)
    {
        var path = EntryPath(repo, Normalize(relPath));
        var existing = TryRead(path);
        if (existing == null || !SameBorrower(existing.Borrower, borrower)) return false;
        try { File.Delete(path); return true; }
        catch { return false; }
    }

    /// <summary>
    /// Return a path this borrower holds in ANY repo, for callers that are themselves
    /// repo-agnostic — `huddle --release` matches claims on session plus path and never needs a
    /// repo, so requiring one here would make the two halves disagree about what a release
    /// means. Returns how many entries were returned; somebody else's are never touched.
    /// </summary>
    public int CheckInAnywhere(string relPath, string borrower)
    {
        var norm = Normalize(relPath);
        var n = 0;
        foreach (var e in ReadAll())
        {
            if (!e.RelPath.Equals(norm, StringComparison.OrdinalIgnoreCase)) continue;
            if (!SameBorrower(e.Borrower, borrower)) continue;
            if (CheckIn(e.Repo, e.RelPath, borrower)) n++;
        }
        return n;
    }

    /// <summary>Extend every checkout this borrower holds. This is the heartbeat.</summary>
    public int Renew(string borrower, TimeSpan? lease = null)
    {
        var due = _now() + (lease ?? DefaultLease);
        var n = 0;
        foreach (var e in ReadAll().Where(e => SameBorrower(e.Borrower, borrower)))
            if (TryOverwrite(EntryPath(e.Repo, e.RelPath), e with { DueAt = due })) n++;
        return n;
    }

    /// <summary>
    /// Is this file available, or checked out? Null means available. An overdue entry is
    /// returned as-is rather than hidden — the caller decides whether to reclaim, and a
    /// reader deserves to see that the book is out AND late.
    /// </summary>
    public CatalogEntry? Status(string repo, string relPath) =>
        TryRead(EntryPath(repo, Normalize(relPath)));

    /// <summary>Every checked-out file. Everything not listed here is available.</summary>
    public IReadOnlyList<CatalogEntry> ReadAll()
    {
        var list = new List<CatalogEntry>();
        if (!Directory.Exists(_dir)) return list;
        foreach (var f in Directory.EnumerateFiles(_dir, "*.md"))
        {
            var e = TryRead(f);
            if (e != null) list.Add(e);
        }
        return list;
    }

    // ---- storage -------------------------------------------------------------------

    /// <summary>
    /// Entry filename. Readable on purpose — the operator reads this directory directly, and
    /// a catalog you need a tool to inspect is a catalog you stop trusting. Separators become
    /// '~' after escaping, so no raw '~' survives from the path itself and the encoding stays
    /// unambiguous. A very long path degrades to a hash (with the real path recorded inside
    /// the file) rather than risking a filesystem name limit.
    /// </summary>
    public static string EntryName(string repo, string relPath)
    {
        var norm = Normalize(relPath);
        var name = Escape(repo) + "~" + Escape(norm).Replace('/', '~');
        if (name.Length <= 180) return name + ".md";
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(norm))).ToLowerInvariant();
        return Escape(repo) + "~" + sha[..16] + ".md";
    }

    private string EntryPath(string repo, string relPath) => Path.Combine(_dir, EntryName(repo, relPath));

    public static string Normalize(string relPath) => relPath.Replace('\\', '/').Trim('/');

    /// <summary>
    /// Content hash of a file in a checkout, or "" when there is no root, no file, or no read.
    /// Never throws and never blocks a checkout: a book is still lent when the librarian cannot
    /// weigh it. 16 hex digits is plenty to notice a change — this detects edits, it does not
    /// defend against someone crafting a collision.
    /// </summary>
    public static string HashOf(string root, string relPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(root)) return "";
            var full = Path.Combine(root, Normalize(relPath).Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) return "";
            using var s = File.OpenRead(full);
            return Convert.ToHexString(SHA256.HashData(s))[..16].ToLowerInvariant();
        }
        catch { return ""; }
    }

    private static string Escape(string s) =>
        s.Replace("%", "%25").Replace("~", "%7E").Replace(":", "%3A");

    private static bool SameBorrower(string a, string b) =>
        a.Replace(':', '_').Equals(b.Replace(':', '_'), StringComparison.OrdinalIgnoreCase);

    private static string Render(CatalogEntry e) =>
        $"""
        # {e.RelPath}

        - **Repo:** {e.Repo}
        - **Borrower:** {e.Borrower}
        - **Checked out:** {e.CheckedOutAt:yyyy-MM-ddTHH:mm:ssZ}
        - **Due:** {e.DueAt:yyyy-MM-ddTHH:mm:ssZ}
        - **Root:** {e.Root}
        - **Hash:** {e.Hash}

        """;

    private bool TryOverwrite(string path, CatalogEntry e)
    {
        try
        {
            File.WriteAllText(path, Render(e), new UTF8Encoding(false));
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Parse an entry. Returns null for anything unreadable — a card nobody can read must
    /// never be reported as "available", so callers treat null from an EXISTING file as
    /// "held, unknown borrower" rather than as an empty shelf.
    /// </summary>
    private static CatalogEntry? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            string repo = "", borrower = "", root = "", rel = "", hash = "";
            DateTime outAt = default, due = default;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith("# ")) { rel = line[2..].Trim(); continue; }
                var v = Value(line);
                if (v == null) continue;
                if (line.StartsWith("- **Repo:**")) repo = v;
                else if (line.StartsWith("- **Borrower:**")) borrower = v;
                else if (line.StartsWith("- **Checked out:**")) outAt = ParseUtc(v);
                else if (line.StartsWith("- **Due:**")) due = ParseUtc(v);
                else if (line.StartsWith("- **Root:**")) root = v;
                else if (line.StartsWith("- **Hash:**")) hash = v;
            }
            if (rel.Length == 0 || borrower.Length == 0 || due == default) return null;
            return new CatalogEntry(repo, rel, borrower, outAt, due, root, hash);
        }
        catch { return null; }
    }

    private static string? Value(string line)
    {
        var i = line.IndexOf(":**", StringComparison.Ordinal);
        return i < 0 ? null : line[(i + 3)..].Trim();
    }

    private static DateTime ParseUtc(string s) =>
        DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal |
                                   System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
            ? d : default;
}
