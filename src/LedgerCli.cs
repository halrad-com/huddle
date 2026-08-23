using System.Text;

namespace Huddle;

/// <summary>
/// The outcome of recording a claim: where it was written, every OTHER session already
/// holding one of its files, and — separately — every session holding the same path in a
/// SIBLING CHECKOUT (ISSUES.md I014). The two lists are kept apart on purpose: an overlap
/// is "you can overwrite each other, go talk now"; a merge warning is "you will conflict at
/// merge time", which is real but not urgent, and collapsing them would make the urgent one
/// mean less.
/// </summary>
public sealed record LedgerClaimResult(
    string ClaimPath,
    IReadOnlyList<ClaimOverlap> Overlaps,
    IReadOnlyList<ClaimOverlap> MergeWarnings
);

/// <summary>
/// Ledger operations for agents claiming directly, with no orchestrator in the write
/// path. The difference from <see cref="WorkLedgerClaims.TryClaim(WorkLedgerClaim, out List{ClaimOverlap})"/>
/// is deliberate and is the whole point of the ledger design: a claim is ALWAYS
/// recorded, and an overlap is REPORTED rather than refused. Refusing requires an
/// arbiter to be alive; reporting does not. Two agents that overlap can both see it
/// in the ledger and take turns by talking to each other.
/// </summary>
public static class LedgerCli
{
    /// <summary>
    /// Record a claim and report who else holds any of its files. Overlaps are computed
    /// against the ledger as it stood BEFORE the write, so the new claim never appears as
    /// its own conflict. A session extending its own scope is not an overlap.
    /// </summary>
    public static LedgerClaimResult Claim(WorkLedgerClaims claims, WorkLedgerClaim claim)
    {
        var path = claims.RecordWithOverlaps(claim, out var overlaps, out var mergeWarnings);
        return new LedgerClaimResult(path, overlaps, mergeWarnings);
    }

    /// <summary>
    /// Release files from the calling session's own claims. Returns the count released.
    /// Another session's claim on the same file is untouched.
    /// </summary>
    public static int Release(WorkLedgerClaims claims, string sessionId, IEnumerable<string> files, string? ownerGuid = null)
        => claims.Release(sessionId, files, ownerGuid);

    /// <summary>
    /// Render the ledger as one line per claimed file: what is claimed, by whom, since when.
    /// This is the "read before you work" view; it must be legible with no huddle running.
    /// A repo-less claim survives every filter, because the conflict engine treats it as
    /// colliding with every repo (see <see cref="WorkLedgerClaims.FindConflictsWithActive"/>):
    /// hiding a claim that WOULD be reported as an overlap inverts the point of the view.
    /// </summary>
    public static string Describe(IReadOnlyList<WorkLedgerClaim> active, string? repoFilter)
    {
        var sb = new StringBuilder();
        var rows = active
            .Where(c => string.IsNullOrEmpty(repoFilter) ||
                        string.IsNullOrEmpty(c.Repo) ||
                        c.Repo.Equals(repoFilter, StringComparison.OrdinalIgnoreCase))
            .SelectMany(c => c.Files.Select(f => (Repo: c.Repo, File: f, Session: c.SessionId, At: c.ClaimedAt)))
            .OrderBy(r => r.Repo, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.File, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (rows.Count == 0)
        {
            // "Nothing matched your filter" is NOT "nothing is claimed". A mistyped repo
            // would otherwise print an all-clear at the exact read-before-you-work step, to
            // an agent that is about to collide — the 2026-08-16 failure with a different
            // cause. Name the repo asked for and the count sitting elsewhere, so a typo is
            // obvious from the output alone.
            if (!string.IsNullOrEmpty(repoFilter) && active.Count > 0)
                sb.AppendLine($"No claims in '{repoFilter}' - but {active.Count} claim(s) are recorded in " +
                              "other repos. Check the repo name (run `huddle --ledger` with no repo to see everything).");
            else
                sb.AppendLine("Ledger is empty - nothing is claimed.");
            return sb.ToString();
        }

        foreach (var r in rows)
        {
            // No repo recorded — print the path bare; "/src/a.cs" would read as absolute.
            var what = string.IsNullOrEmpty(r.Repo) ? r.File : $"{r.Repo}/{r.File}";
            // The literal Z is a promise: an in-memory claim may carry a non-UTC DateTime.
            sb.AppendLine($"{what} - held by {r.Session} since {r.At.ToUniversalTime():yyyy-MM-dd HH:mm}Z");
        }

        return sb.ToString();
    }
}
