namespace Huddle;

/// <summary>Per-session mail backlog, as shown by the `backlog` verb.</summary>
public readonly record struct MailBacklog(string Session, int Queued, int Unread, DateTime? Oldest);

/// <summary>
/// Read-receipt bookkeeping for session inboxes.
///
/// <para>Mail used to be archived to processed/ the moment huddle queued a wake
/// line, which made "delivered" and "read" indistinguishable: a session that never
/// saw its mail looked exactly like one that had acted on it. Now mail stays in
/// inbox/ until the agent acknowledges it, so <c>inbox/</c> means unread.</para>
///
/// <para>Two things follow. Huddle must remember which files it has already
/// announced, or every rescan, retry tick and restart would re-announce unread
/// mail — that is the delivered index. And acknowledgement has to be something a
/// bare, Write-only persona can perform: writing a copy into processed/ counts, and
/// huddle then removes the inbox original. An agent with a shell can simply move the
/// file, which is the same end state.</para>
/// </summary>
public static class MailReceipts
{
    /// <summary>Name of the per-session file listing mail huddle has already announced.</summary>
    public const string DeliveredIndexName = "delivered.txt";

    /// <summary>
    /// Decide what to clean up after agents have acknowledged mail.
    ///
    /// <para><c>Reap</c> — inbox files the agent has copied into processed/; the
    /// inbox original is now redundant and huddle deletes it.</para>
    /// <para><c>Forget</c> — delivered-index entries whose mail has left the inbox,
    /// either reaped just now or moved by the agent itself. Dropping them keeps the
    /// index from growing without bound, and means a later message that reuses a
    /// name is announced rather than silently swallowed.</para>
    ///
    /// Pure: the caller does the directory listing and the deleting.
    /// </summary>
    public static (List<string> Reap, List<string> Forget) PlanCleanup(
        IEnumerable<string> inboxNames,
        IReadOnlySet<string> processedNames,
        IEnumerable<string> deliveredNames)
    {
        var inbox = inboxNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var reap = inbox
            .Where(processedNames.Contains)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var reaped = reap.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var forget = deliveredNames
            .Where(n => !inbox.Contains(n) || reaped.Contains(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return (reap, forget);
    }
}
