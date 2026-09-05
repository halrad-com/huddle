using Huddle;
using Xunit;

namespace HuddleTests;

// The commit audit closes the hole the claim-check hook cannot: the hook is a
// PreToolUse guard on Edit/Write, so a file written through Bash (sed, a script,
// a redirect) reaches the repo unchecked. This is the post-hoc half - it reads
// what actually landed in a commit and asks whether anyone ever claimed it.
public class CommitAuditUnclaimedTests
{
    private static ISet<string> Claimed(params string[] files) =>
        CommitAudit.ClaimedSet(files);

    [Fact]
    public void A_file_nobody_claimed_is_reported()
    {
        var u = CommitAudit.Unclaimed(new[] { "src/A.cs", "src/B.cs" }, Claimed("src/A.cs"));
        Assert.Equal(new[] { "src/B.cs" }, u);
    }

    [Fact]
    public void Every_file_claimed_reports_nothing()
        => Assert.Empty(CommitAudit.Unclaimed(new[] { "src/A.cs" }, Claimed("src/A.cs")));

    [Fact]
    public void Separator_and_case_differences_are_the_same_file()
    {
        // The claim ledger normalises paths for exactly this reason (backslash vs
        // forward slash never collided before the I008 fix). The audit must agree,
        // or it cries wolf on every Windows-shaped path.
        Assert.Empty(CommitAudit.Unclaimed(new[] { "src/A.cs" }, Claimed(@"src\A.cs")));
        Assert.Empty(CommitAudit.Unclaimed(new[] { "SRC/a.cs" }, Claimed("src/A.cs")));
        Assert.Empty(CommitAudit.Unclaimed(new[] { "./src/A.cs" }, Claimed("src/A.cs")));
    }

    [Fact]
    public void Nothing_claimed_reports_every_file()
    {
        var u = CommitAudit.Unclaimed(new[] { "a.md", "b.md" }, Claimed());
        Assert.Equal(new[] { "a.md", "b.md" }, u);
    }

    [Fact]
    public void Duplicates_in_one_commit_are_reported_once()
        => Assert.Single(CommitAudit.Unclaimed(new[] { "a.md", @".\a.md" }, Claimed()));

    [Fact]
    public void Empty_and_whitespace_entries_are_ignored()
        => Assert.Empty(CommitAudit.Unclaimed(new[] { "", "   " }, Claimed()));
}

public class ClaimJournalTests : IDisposable
{
    private readonly string _dir;
    public ClaimJournalTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "huddle-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Appended_claims_come_back_per_repo()
    {
        var j = new ClaimJournal(_dir, _ => { });
        j.Record("huddle:architect", "huddle", new[] { "src/A.cs", "src/B.cs" });
        j.Record("myapp:dev", "myapp", new[] { "src/C.cs" });

        var huddle = j.ClaimedIn("huddle");
        Assert.Contains("src/A.cs", huddle);
        Assert.Contains("src/B.cs", huddle);
        Assert.DoesNotContain("src/C.cs", huddle);
        Assert.Contains("src/C.cs", j.ClaimedIn("myapp"));
    }

    [Fact]
    public void It_is_append_only_and_survives_a_new_instance()
    {
        new ClaimJournal(_dir, _ => { }).Record("s", "r", new[] { "one.cs" });
        new ClaimJournal(_dir, _ => { }).Record("s", "r", new[] { "two.cs" });

        var read = new ClaimJournal(_dir, _ => { }).ClaimedIn("r");
        Assert.Equal(2, read.Count);
    }

    [Fact]
    public void An_unreadable_line_never_costs_the_rest_of_the_file()
    {
        // Same failure posture as the rest of the ledger: a corrupt line is skipped,
        // not fatal. An audit that throws on bad input silently stops auditing.
        var j = new ClaimJournal(_dir, _ => { });
        j.Record("s", "r", new[] { "good.cs" });
        File.AppendAllText(Path.Combine(_dir, ClaimJournal.FileName), "{ not json\n");
        j.Record("s", "r", new[] { "later.cs" });

        var read = j.ClaimedIn("r");
        Assert.Contains("good.cs", read);
        Assert.Contains("later.cs", read);
    }

    [Fact]
    public void No_journal_file_yet_is_an_empty_set_not_a_throw()
        => Assert.Empty(new ClaimJournal(_dir, _ => { }).ClaimedIn("anything"));

    [Fact]
    public void Repo_matching_is_case_insensitive()
    {
        var j = new ClaimJournal(_dir, _ => { });
        j.Record("s", "Huddle", new[] { "x.cs" });
        Assert.Contains("x.cs", j.ClaimedIn("huddle"));
    }
}

// Wiring, not logic: a claim written through the REAL ledger must land in the
// journal beside it. The unit tests above prove the decision; this proves the
// choke point is actually plumbed, which is the half that silently rots.
public class ClaimJournalWiringTests : IDisposable
{
    private readonly string _root;
    public ClaimJournalWiringTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "huddle-jw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "claims"));
    }
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void Writing_a_claim_journals_the_grant()
    {
        var claims = new WorkLedgerClaims(Path.Combine(_root, "claims"), _ => { });
        claims.Write(new WorkLedgerClaim(
            "huddle:architect", "huddle", "B-1", DateTime.UtcNow, "abc123",
            new[] { "src/One.cs", "src/Two.cs" }));

        // Beside claims/, not inside it - ReadAll globs that directory.
        var journalPath = Path.Combine(_root, ClaimJournal.FileName);
        Assert.True(File.Exists(journalPath), "journal.jsonl should sit beside claims/");

        var claimed = new ClaimJournal(_root, _ => { }).ClaimedIn("huddle");
        Assert.Contains("src/One.cs", claimed);
        Assert.Contains("src/Two.cs", claimed);

        // And the audit therefore stays quiet about exactly those files, while still
        // reporting one that was never claimed.
        var unclaimed = CommitAudit.Unclaimed(
            new[] { "src/One.cs", "src/Two.cs", "src/Sneaky.cs" }, claimed);
        Assert.Equal(new[] { "src/Sneaky.cs" }, unclaimed);
    }

    [Fact]
    public void A_released_claim_still_leaves_its_grant_on_record()
    {
        // The whole reason the journal exists: the protocol says release when done,
        // so the claim file is gone by the time a commit could be audited.
        var claimsDir = Path.Combine(_root, "claims");
        var claims = new WorkLedgerClaims(claimsDir, _ => { });
        claims.Write(new WorkLedgerClaim(
            "huddle:architect", "huddle", "B-2", DateTime.UtcNow, "abc123",
            new[] { "src/Gone.cs" }));
        claims.Release("huddle:architect", new[] { "src/Gone.cs" });

        Assert.Empty(Directory.GetFiles(claimsDir, "*.md"));
        Assert.Contains("src/Gone.cs", new ClaimJournal(_root, _ => { }).ClaimedIn("huddle"));
    }
}

public class ClaimJournalEncodingTests : IDisposable
{
    private readonly string _dir;
    public ClaimJournalEncodingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "huddle-je-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void The_file_starts_with_json_not_a_byte_order_mark()
    {
        // Encoding.UTF8 writes a preamble on the first append. .NET's own reader strips
        // it, so huddle never noticed - but a .jsonl is a format other tools read, and
        // jq fails on a leading BOM. Assert the BYTES, not the round-trip.
        new ClaimJournal(_dir, _ => { }).Record("s", "r", new[] { "a.cs" });

        var bytes = File.ReadAllBytes(Path.Combine(_dir, ClaimJournal.FileName));
        Assert.NotEmpty(bytes);
        Assert.Equal((byte)'{', bytes[0]);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "journal.jsonl must not carry a UTF-8 BOM");
    }
}

// Both of these are REGRESSIONS from the feature's first live firing (2026-09-04):
// it reported myapp/docs/BACKLOG.md as unclaimed 3 minutes after that exact file
// was claimed, and reported the same commit twice.
public class CommitAuditNestedRepoTests
{
    private const string Top = @"C:\repos\LIB";
    private const string Sub = @"C:\repos\LIB\myapp";

    [Fact]
    public void A_claim_from_a_subdirectory_checkout_matches_the_repo_relative_commit_path()
    {
        // The claim is relative to the SESSION's root (…/LIB/myapp), the commit path is
        // relative to the GIT root (…/LIB). Same file, two spellings - the I008 class,
        // one level up. Comparing the raw strings cries wolf on correctly-claimed work.
        var idx = new ClaimedIndex();
        idx.AddClaim(Sub, new[] { "docs/BACKLOG.md" });

        Assert.Empty(CommitAudit.Unclaimed(new[] { "myapp/docs/BACKLOG.md" }, Top, idx));
    }

    [Fact]
    public void A_file_outside_the_claim_is_still_reported()
    {
        var idx = new ClaimedIndex();
        idx.AddClaim(Sub, new[] { "docs/BACKLOG.md" });

        Assert.Equal(
            new[] { "myapp/src/Other.cs" },
            CommitAudit.Unclaimed(new[] { "myapp/docs/BACKLOG.md", "myapp/src/Other.cs" }, Top, idx));
    }

    [Fact]
    public void A_claim_recorded_at_the_git_root_still_matches()
    {
        var idx = new ClaimedIndex();
        idx.AddClaim(Top, new[] { "README.md" });
        Assert.Empty(CommitAudit.Unclaimed(new[] { "README.md" }, Top, idx));
    }

    [Fact]
    public void A_legacy_entry_with_no_root_falls_back_to_the_relative_path()
    {
        // Journal lines written before Root existed. Fall back to matching the tail so
        // an upgrade does not turn every old grant into a false accusation. Over-match
        // here means silence, which is the safe direction.
        var idx = new ClaimedIndex();
        idx.AddClaim("", new[] { "docs/BACKLOG.md" });

        Assert.Empty(CommitAudit.Unclaimed(new[] { "myapp/docs/BACKLOG.md" }, Top, idx));
        Assert.Empty(CommitAudit.Unclaimed(new[] { "docs/BACKLOG.md" }, Top, idx));
        Assert.Single(CommitAudit.Unclaimed(new[] { "other/BACKLOG.md.bak" }, Top, idx));
    }

    [Fact]
    public void Registered_roots_sharing_one_git_top_collapse_to_a_single_audit()
    {
        // myapp (…/LIB/myapp) and LIB-root (…/LIB) are two registered names for one
        // git repo, so one commit produced two identical warnings. Group by top.
        var groups = CommitAudit.GroupByTop(new[]
        {
            ("myapp", Sub, Top),
            ("lib-root", Top, Top),
            ("otherapp", @"C:\repos\other", @"C:\repos\other"),
        });

        Assert.Equal(2, groups.Count);
        // Tops come back normalised (forward slashes) - they are used as dictionary keys
        // and passed to git, which accepts either separator on Windows.
        var libGroup = groups.Single(g =>
            string.Equals(g.Top, CommitAudit.Norm(Top), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new[] { "myapp", "lib-root" }, libGroup.RepoNames);
        // The name reported is the one whose root IS the git top - the honest label for
        // a commit that spans the whole repo.
        Assert.Equal("lib-root", libGroup.DisplayName);
    }

    [Fact]
    public void A_group_with_no_root_at_the_top_reports_its_first_name()
    {
        var groups = CommitAudit.GroupByTop(new[] { ("myapp", Sub, Top) });
        Assert.Equal("myapp", groups.Single().DisplayName);
    }
}

// Third regression from the live run (2026-09-04): three files were accused while a
// live claim file covered them. The claim was written 90 minutes before the journal
// existed, and the audit consulted only the journal. History is not the present.
public class CommitAuditLiveClaimTests
{
    [Fact]
    public void A_claim_that_predates_the_journal_still_covers_its_files()
    {
        var top = @"C:/repos/LIB";
        var idx = new ClaimedIndex();          // journal empty, as it is on day one
        Assert.Single(CommitAudit.Unclaimed(new[] { "myapp/MBXH/Core/CharmRegistry.cs" }, top, idx));

        // Now fold in the live claim, exactly as the orchestrator does.
        idx.AddClaim(@"C:\repos\LIB\myapp", new[] { "MBXH/Core/CharmRegistry.cs" });
        Assert.Empty(CommitAudit.Unclaimed(new[] { "myapp/MBXH/Core/CharmRegistry.cs" }, top, idx));
    }
}
