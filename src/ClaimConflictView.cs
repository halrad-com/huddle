namespace Huddle;

/// <summary>
/// One physical file that two claims both hold, recorded in each holder's OWN spelling.
/// <paramref name="ResolvedPath"/> is the absolute path both spellings land on, or null when
/// the pair was decided by the pre-I013 name comparison (no resolver, or a root the registry
/// cannot place). <paramref name="CrossSpelling"/> is true when the two holders wrote the file
/// down differently — different repo name, different relative path text, or both — which is the
/// case the operator cannot otherwise explain to themselves.
/// </summary>
public sealed record CollidingFile(
    string SpellingA,
    string SpellingB,
    string? ResolvedPath,
    bool CrossSpelling);

/// <summary>
/// Two claims that collide, and every file they collide on.
/// </summary>
public sealed record ClaimCollision(
    WorkLedgerClaim A,
    WorkLedgerClaim B,
    IReadOnlyList<CollidingFile> Files);

/// <summary>
/// Two claims on the same repo-relative path in two SIBLING CHECKOUTS of one repo (I014).
/// Not a collision — different files on disk, nobody is blocked — but a guaranteed merge
/// conflict, which the ledger used to report as nothing at all. Kept as its own type so the
/// operator view cannot render it with the vocabulary of a real overlap.
/// </summary>
public sealed record ClaimMergeRisk(
    WorkLedgerClaim A,
    WorkLedgerClaim B,
    IReadOnlyList<string> Files);

/// <summary>
/// The decision half of the `conflicts` verb: WHICH active claims collide, and WHY.
/// Pure — no console, no filesystem — so the operator-facing view can be unit-tested
/// against the same topology the arbiter is tested against.
///
/// ISSUES.md I013: the verb used to group claims on raw file strings, which meant the
/// operator's own view of the ledger could report "no conflicts" on a pair the arbiter
/// would refuse (nested repo roots give one physical file several legitimate repo-relative
/// spellings). Collision is therefore NOT decided here — it is delegated wholesale to
/// <see cref="WorkLedgerClaims.FindOverlaps"/>, the arbiter's own comparison, so there
/// stays exactly one definition of "these two claims collide". This class only explains
/// the answer.
/// </summary>
public static class ClaimConflictView
{
    /// <summary>
    /// Every colliding pair among the active claims, annotated with the resolved path and
    /// both spellings. <paramref name="resolveRoot"/> is the orchestrator's repo-name → root
    /// resolver; null degrades to the pre-I013 name comparison exactly as the arbiter does.
    /// </summary>
    public static List<ClaimCollision> Find(
        IReadOnlyList<WorkLedgerClaim> claims,
        Func<string, string?>? resolveRoot = null)
    {
        var result = new List<ClaimCollision>();
        foreach (var overlap in WorkLedgerClaims.FindOverlaps(claims, resolveRoot))
        {
            var files = new List<CollidingFile>();
            foreach (var spellingB in overlap.SharedFiles)
            {
                foreach (var spellingA in SpellingsIn(overlap.A, overlap.B, spellingB, resolveRoot))
                    files.Add(Describe(overlap.A, spellingA, overlap.B, spellingB, resolveRoot));
            }
            if (files.Count > 0)
                result.Add(new ClaimCollision(overlap.A, overlap.B, files));
        }
        return result;
    }

    /// <summary>
    /// Every pair of active claims that is NOT a collision but holds the same path in a
    /// sibling checkout (I014). Same delegation rule as <see cref="Find"/>: the decision is
    /// <see cref="WorkLedgerClaims.FindMergeWarnings"/>'s, this only enumerates pairs once
    /// (the arbiter's entry point is proposed-vs-active, which over one list would report
    /// each pair in both directions).
    /// </summary>
    public static List<ClaimMergeRisk> FindMergeRisks(
        IReadOnlyList<WorkLedgerClaim> claims,
        Func<string, string?>? resolveRoot = null,
        Func<string, CheckoutInfo?>? identifyCheckout = null)
    {
        var result = new List<ClaimMergeRisk>();
        for (int i = 0; i < claims.Count; i++)
        {
            for (int j = i + 1; j < claims.Count; j++)
            {
                foreach (var w in WorkLedgerClaims.FindMergeWarnings(
                             new[] { claims[i] }, new[] { claims[j] }, resolveRoot, identifyCheckout))
                    result.Add(new ClaimMergeRisk(w.A, w.B, w.SharedFiles));
            }
        }
        return result;
    }

    /// <summary>
    /// Which of A's recorded paths name the same physical file as ONE of B's. Answered by
    /// running the arbiter's comparison with the pair flipped — <see cref="WorkLedgerClaims.FindOverlaps"/>
    /// reports the SECOND claim's spellings in the first claim's key space — rather than by
    /// re-deriving path equality here. Slower than a hand-rolled match and deliberately so:
    /// a ledger holds a handful of claims, and a second definition of "same file" is exactly
    /// the defect this change exists to remove.
    /// </summary>
    private static List<string> SpellingsIn(
        WorkLedgerClaim a, WorkLedgerClaim b, string oneFileOfB, Func<string, string?>? resolveRoot)
    {
        var probe = WorkLedgerClaims.FindOverlaps(
            new[] { b with { Files = new[] { oneFileOfB } }, a }, resolveRoot);
        var spellings = probe
            .SelectMany(p => p.SharedFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        // Unreachable while the two directions agree; if they ever disagree, report the
        // collision with B's spelling rather than dropping it silently.
        if (spellings.Count == 0) spellings.Add(oneFileOfB);
        return spellings;
    }

    private static CollidingFile Describe(
        WorkLedgerClaim a, string spellingA,
        WorkLedgerClaim b, string spellingB,
        Func<string, string?>? resolveRoot)
    {
        // Either side resolves to the same absolute path by construction; try both so a
        // legacy claim with no repo does not blank out a path its partner can supply.
        // Claim-aware overload: a claim's own recorded checkout root outranks its declared
        // repo name (I014), so the path shown is the one the holder is really editing.
        var resolved = WorkLedgerClaims.TryAbsolutePath(b, spellingB, resolveRoot)
                    ?? WorkLedgerClaims.TryAbsolutePath(a, spellingA, resolveRoot);

        var cross = !string.Equals(a.Repo, b.Repo, StringComparison.OrdinalIgnoreCase)
                 || !string.Equals(spellingA, spellingB, StringComparison.OrdinalIgnoreCase);

        return new CollidingFile(spellingA, spellingB, resolved, cross);
    }
}
