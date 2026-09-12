namespace Huddle;

/// <summary>
/// The circulation list, rendered once for every surface that shows it — `huddle --catalog` on
/// the command line and the `catalog` verb in the console. One renderer, so the two cannot
/// drift, for the same reason help is rendered from the verb catalog rather than from a second
/// hand-kept list.
///
/// Pure: entries and a clock in, lines out. Nothing here touches the disk, so the view is
/// testable without a catalog directory, and the console can colour lines without re-deriving
/// what they mean.
/// </summary>
public static class CatalogView
{
    /// <summary>
    /// Circulation statistics — Katalog's influence, applied to what is OUT rather than to what
    /// exists: how many files, held by whom, how many late. Derived from the entries on every
    /// call and never stored, so it cannot disagree with the list printed above it.
    /// </summary>
    public sealed record Summary(int Total, int Overdue, IReadOnlyList<BorrowerCount> ByBorrower);

    public sealed record BorrowerCount(string Borrower, int Count, int Overdue);

    /// <summary>
    /// Narrow and order the entries. Sorted by repo then path so the same catalog always prints
    /// the same way — a list whose order shuffles between calls reads as a list that changed.
    /// </summary>
    public static IReadOnlyList<CatalogEntry> Filter(
        IEnumerable<CatalogEntry> entries, DateTime nowUtc,
        string? repo = null, bool overdueOnly = false, string? borrower = null) =>
        entries
            .Where(e => string.IsNullOrWhiteSpace(repo) ||
                        e.Repo.Equals(repo.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(e => !overdueOnly || e.IsOverdue(nowUtc))
            .Where(e => string.IsNullOrWhiteSpace(borrower) || SameBorrower(e.Borrower, borrower))
            .OrderBy(e => e.Repo, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.RelPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// One entry as one line. This shape is what `huddle --catalog` has printed from the start,
    /// so it is held stable; the console adds colour, never different words.
    /// </summary>
    public static string Line(CatalogEntry e, DateTime nowUtc) =>
        $"{e.Repo}/{e.RelPath} - {e.Borrower}, due {e.DueAt:yyyy-MM-dd HH:mm}Z" +
        (e.IsOverdue(nowUtc) ? " (OVERDUE - reclaimable)" : "");

    public static Summary Summarize(IReadOnlyList<CatalogEntry> entries, DateTime nowUtc)
    {
        // Grouped on the form-agnostic spelling, because `huddle:architect` and
        // `huddle_architect` are one borrower everywhere else in the ledger; splitting them here
        // would report one agent as two.
        var byBorrower = entries
            .GroupBy(e => e.Borrower.Replace(':', '_'), StringComparer.OrdinalIgnoreCase)
            .Select(g => new BorrowerCount(g.First().Borrower, g.Count(), g.Count(e => e.IsOverdue(nowUtc))))
            .OrderByDescending(b => b.Count)
            .ThenBy(b => b.Borrower, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new Summary(entries.Count, entries.Count(e => e.IsOverdue(nowUtc)), byBorrower);
    }

    /// <summary>
    /// The footer: totals, then who holds what. "Everything else is available" is said outright,
    /// because the ABSENCE of a row is the available state, and a reader should not need to know
    /// that design to read the list correctly.
    /// </summary>
    public static IReadOnlyList<string> SummaryLines(Summary s)
    {
        if (s.Total == 0) return new[] { "Nothing is checked out - every file is available." };

        return new[]
        {
            $"{s.Total} file(s) checked out to {s.ByBorrower.Count} borrower(s)" +
            (s.Overdue > 0 ? $", {s.Overdue} overdue" : "") +
            ". Everything else is available.",
            "  " + string.Join("; ", s.ByBorrower.Select(b =>
                $"{b.Borrower} {b.Count}" + (b.Overdue > 0 ? $" ({b.Overdue} overdue)" : "")))
        };
    }

    private static bool SameBorrower(string a, string b) =>
        a.Replace(':', '_').Equals(b.Trim().Replace(':', '_'), StringComparison.OrdinalIgnoreCase);
}
