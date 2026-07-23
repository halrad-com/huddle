using Huddle;
namespace Huddle.Tests;

public class MailReceiptTests
{
    private static IReadOnlySet<string> Names(params string[] names) =>
        names.ToHashSet(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Mail_the_agent_copied_to_processed_is_cleared_from_the_inbox()
    {
        // A Write-only persona cannot delete, so it copies. Huddle removes the original.
        var (reap, forget) = MailReceipts.PlanCleanup(
            inboxNames: new[] { "020-handoff.json", "021-review.json" },
            processedNames: Names("020-handoff.json"),
            deliveredNames: new[] { "020-handoff.json", "021-review.json" });

        Assert.Equal(new[] { "020-handoff.json" }, reap);
        Assert.Equal(new[] { "020-handoff.json" }, forget);
    }

    [Fact]
    public void Unread_mail_is_left_alone()
    {
        var (reap, forget) = MailReceipts.PlanCleanup(
            inboxNames: new[] { "023-gaps.json" },
            processedNames: Names(),
            deliveredNames: new[] { "023-gaps.json" });

        Assert.Empty(reap);
        Assert.Empty(forget);            // still unread — the index entry must stay
    }

    [Fact]
    public void Mail_the_agent_moved_itself_is_forgotten_without_being_reaped()
    {
        // A shell persona moves the file; nothing left to delete, but the index
        // entry has to go or the name can never be announced again.
        var (reap, forget) = MailReceipts.PlanCleanup(
            inboxNames: Array.Empty<string>(),
            processedNames: Names("022-speechlikely.json"),
            deliveredNames: new[] { "022-speechlikely.json" });

        Assert.Empty(reap);
        Assert.Equal(new[] { "022-speechlikely.json" }, forget);
    }

    [Fact]
    public void A_reused_filename_can_be_announced_again_after_the_first_is_cleared()
    {
        // Sender reuses a name for a new message. Once the old one is out of the
        // inbox the index is pruned, so the new file is not swallowed as a duplicate.
        var (_, forget) = MailReceipts.PlanCleanup(
            inboxNames: Array.Empty<string>(),
            processedNames: Names("001-status.json"),
            deliveredNames: new[] { "001-status.json" });

        Assert.Contains("001-status.json", forget);
    }

    [Fact]
    public void Nothing_outstanding_produces_no_work()
    {
        var (reap, forget) = MailReceipts.PlanCleanup(
            Array.Empty<string>(), Names(), Array.Empty<string>());

        Assert.Empty(reap);
        Assert.Empty(forget);
    }

    [Fact]
    public void Processed_mail_with_no_delivered_entry_is_still_reaped()
    {
        // Index lost (deleted, or written by an older huddle) — the copy in
        // processed/ is proof enough that the inbox original is redundant.
        var (reap, forget) = MailReceipts.PlanCleanup(
            inboxNames: new[] { "005-note.json" },
            processedNames: Names("005-note.json"),
            deliveredNames: Array.Empty<string>());

        Assert.Equal(new[] { "005-note.json" }, reap);
        Assert.Empty(forget);
    }

    [Fact]
    public void Names_are_matched_case_insensitively()
    {
        var (reap, _) = MailReceipts.PlanCleanup(
            inboxNames: new[] { "007-Note.json" },
            processedNames: Names("007-note.json"),
            deliveredNames: new[] { "007-NOTE.json" });

        Assert.Single(reap);
    }

    [Fact]
    public void Unrelated_processed_mail_does_not_touch_the_inbox()
    {
        var (reap, forget) = MailReceipts.PlanCleanup(
            inboxNames: new[] { "030-new.json" },
            processedNames: Names("001-old.json", "002-older.json"),
            deliveredNames: new[] { "030-new.json" });

        Assert.Empty(reap);
        Assert.Empty(forget);
    }
}
