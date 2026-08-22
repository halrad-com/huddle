using Huddle;
using Xunit;

namespace HuddleTests;

// The CLI surface an agent actually types. Identity and ledger location come from
// spawn-time environment variables so the agent supplies only file paths.
public class LedgerCommandsTests : IDisposable
{
    private readonly string _dir;
    private readonly List<string> _out = new();

    public LedgerCommandsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "huddle-ledgercmd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string? Env(string key) => key switch
    {
        "HUDDLE_CLAIMS" => _dir,
        "HUDDLE_INSTANCE" => "myapp:frontenddev",
        "HUDDLE_REPO" => "myapp",
        "HUDDLE_GUID" => "11111111-1111-1111-1111-111111111111",
        _ => null
    };

    private string Text => string.Join("\n", _out);

    [Fact]
    public void RunClaim_WritesTheClaimAndSucceeds()
    {
        var code = LedgerCommands.RunClaim(new[] { "deploy/docs.html" }, Env, _out.Add);

        Assert.Equal(0, code);
        var claims = new WorkLedgerClaims(_dir, _ => { }).ReadAll();
        var claim = Assert.Single(claims);
        Assert.Equal("myapp:frontenddev", claim.SessionId);
        Assert.Equal("myapp", claim.Repo);
        Assert.Equal(new[] { "deploy/docs.html" }, claim.Files);
    }

    [Fact]
    public void RunClaim_RecordsTheOwnerGuidSoReapingStaysPrecise()
    {
        LedgerCommands.RunClaim(new[] { "deploy/docs.html" }, Env, _out.Add);

        var claim = Assert.Single(new WorkLedgerClaims(_dir, _ => { }).ReadAll());
        Assert.Equal("11111111-1111-1111-1111-111111111111", claim.OwnerGuid);
    }

    [Fact]
    public void RunClaim_SucceedsButNamesTheOtherHolderOnOverlap()
    {
        new WorkLedgerClaims(_dir, _ => { }).Write(
            new WorkLedgerClaim("myapp:architect", "myapp", "A-0",
                new DateTime(2026, 8, 16, 8, 0, 0, DateTimeKind.Utc), "", new[] { "deploy/docs.html" }));

        var code = LedgerCommands.RunClaim(new[] { "deploy/docs.html" }, Env, _out.Add);

        Assert.Equal(0, code); // recorded, not refused
        Assert.Contains("myapp:architect", Text);
        Assert.Contains("deploy/docs.html", Text);
    }

    [Fact]
    public void RunClaim_ShowsWhenTheOtherHolderClaimedIt()
    {
        new WorkLedgerClaims(_dir, _ => { }).Write(
            new WorkLedgerClaim("myapp:architect", "myapp", "A-0",
                new DateTime(2026, 8, 14, 22, 41, 0, DateTimeKind.Utc), "", new[] { "deploy/docs.html" }));

        LedgerCommands.RunClaim(new[] { "deploy/docs.html" }, Env, _out.Add);

        // Without a live roster the holder may already be dead; the claim time is
        // what lets the claimant judge whether waiting on them is worth it.
        Assert.Contains("2026-08-14 22:41", Text);
    }

    [Fact]
    public void RunClaim_WithoutFiles_FailsLoudly()
    {
        var code = LedgerCommands.RunClaim(Array.Empty<string>(), Env, _out.Add);

        Assert.Equal(2, code);
        Assert.Contains("usage", Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RunClaim_WithoutEnvironment_FailsLoudlyRatherThanWritingSomewhereWrong()
    {
        var code = LedgerCommands.RunClaim(new[] { "a.cs" }, _ => null, _out.Add);

        Assert.Equal(2, code);
        Assert.Contains("HUDDLE_CLAIMS", Text);
        // The half the name promises: with no ledger location it must write NOTHING,
        // not fall back to some default directory nobody else reads.
        Assert.Empty(Directory.GetFiles(_dir, "*.md"));
    }

    [Fact]
    public void RunClaim_RejectsAnAbsolutePath_BecauseItWouldProtectNobody()
    {
        // Claims match on repo-relative paths, so an absolute one can never collide with
        // another session's "src/a.cs" — it would print success and be invisible. Agents
        // are primed to type full paths, so this has to be rejected, not accepted quietly.
        var code = LedgerCommands.RunClaim(
            new[] { @"C:\Users\you\source\repos\myapp\src\a.cs" }, Env, _out.Add);

        Assert.Equal(2, code);
        Assert.Contains("REPO-RELATIVE", Text);
        Assert.Empty(Directory.GetFiles(_dir, "*.md"));
    }

    [Fact]
    public void RunClaim_RejectsAnAbsolutePathEvenAmongRelativeOnes()
    {
        var code = LedgerCommands.RunClaim(new[] { "src/a.cs", "/etc/hosts" }, Env, _out.Add);

        Assert.Equal(2, code);
        Assert.Empty(Directory.GetFiles(_dir, "*.md")); // all-or-nothing: no partial claim
    }

    [Fact]
    public void RunRelease_RejectsAnAbsolutePathToo()
    {
        var code = LedgerCommands.RunRelease(new[] { @"C:\repos\app\src\a.cs" }, Env, _out.Add);

        Assert.Equal(2, code);
        Assert.Contains("REPO-RELATIVE", Text);
    }

    [Fact]
    public void RunClaim_WhenTheLedgerCannotBeWritten_ReportsItInsteadOfThrowing()
    {
        // A claims dir that is really a FILE makes the write throw. Unhandled, that is a
        // stack trace, no claim on disk, and an agent with no arbiter to fall back on —
        // a silent refusal. It must be a distinct, non-usage exit code with a line saying
        // the claim did not land.
        var asFile = Path.Combine(_dir, "not-a-directory");
        File.WriteAllText(asFile, "x");
        string? BadEnv(string key) => key == "HUDDLE_CLAIMS" ? asFile : Env(key);

        var code = LedgerCommands.RunClaim(new[] { "a.cs" }, BadEnv, _out.Add);

        Assert.Equal(3, code); // not 2 — this is a failure, not a usage error
        Assert.Contains("FAILED", Text);
    }

    [Fact]
    public void RunClaim_WarnsWhenTheRepoIsUnknownRatherThanPrintingABlank()
    {
        string? NoRepo(string key) => key == "HUDDLE_REPO" ? "" : Env(key);

        var code = LedgerCommands.RunClaim(new[] { "a.cs" }, NoRepo, _out.Add);

        Assert.Equal(0, code); // recorded anyway: no repo collides with every repo (fail-safe)
        Assert.Contains("HUDDLE_REPO", Text);
    }

    [Fact]
    public void RunRelease_RemovesTheCallersClaim()
    {
        LedgerCommands.RunClaim(new[] { "deploy/docs.html" }, Env, _out.Add);

        var code = LedgerCommands.RunRelease(new[] { "deploy/docs.html" }, Env, _out.Add);

        Assert.Equal(0, code);
        Assert.Empty(new WorkLedgerClaims(_dir, _ => { }).ReadAll());
    }

    [Fact]
    public void RunRelease_AgainstAnAbsentLedgerIsANoOpRatherThanACrash()
    {
        // The feature exists to work when huddle has never started, and in that state the
        // claims directory may not exist at all. Releasing before anything was ever claimed
        // must report nothing released, not throw a stack trace at the agent.
        var missing = Path.Combine(_dir, "never-created");
        string? MissingEnv(string key) => key == "HUDDLE_CLAIMS" ? missing : Env(key);

        var code = LedgerCommands.RunRelease(new[] { "deploy/docs.html" }, MissingEnv, _out.Add);

        Assert.Equal(0, code);
        Assert.Contains("released 0 file(s)", Text);
    }

    [Fact]
    public void RunClaim_TwiceInTheSameSecondWritesTwoClaimsRatherThanOverwriting()
    {
        // The claim FILENAME is derived from the batch id, so two claims by one session
        // inside the same second collide unless the id is uniquified per call. This
        // exercises the real generated uniquifier, not one handed in by the test.
        LedgerCommands.RunClaim(new[] { "a.cs" }, Env, _out.Add);
        LedgerCommands.RunClaim(new[] { "b.cs" }, Env, _out.Add);

        Assert.Equal(2, Directory.GetFiles(_dir, "*.md").Length);
    }

    [Fact]
    public void RunLedger_ListsWhatIsClaimed()
    {
        LedgerCommands.RunClaim(new[] { "deploy/docs.html" }, Env, _out.Add);
        _out.Clear();

        var code = LedgerCommands.RunLedger(Array.Empty<string>(), Env, _out.Add);

        Assert.Equal(0, code);
        Assert.Contains("deploy/docs.html", Text);
        Assert.Contains("myapp:frontenddev", Text);
    }

    [Fact]
    public void RunLedger_DoesNotCallAMistypedRepoAnAllClear()
    {
        // "Nothing matched your filter" read as "nothing is claimed" is an all-clear
        // handed to an agent that is about to collide — at the read-before-you-work step.
        LedgerCommands.RunClaim(new[] { "deploy/docs.html" }, Env, _out.Add);
        _out.Clear();

        var code = LedgerCommands.RunLedger(new[] { "restfulbe" }, Env, _out.Add); // typo

        Assert.Equal(0, code);
        Assert.DoesNotContain("Ledger is empty", Text);
        Assert.Contains("restfulbe", Text);
        Assert.Contains("other repos", Text);
    }

    [Fact]
    public void RunLedger_StillReportsAGenuinelyEmptyLedger()
    {
        var code = LedgerCommands.RunLedger(new[] { "myapp" }, Env, _out.Add);

        Assert.Equal(0, code);
        Assert.Contains("Ledger is empty", Text);
    }

    [Fact]
    public void RunLedger_SurfacesAClaimFileItCouldNotParse()
    {
        // A claim file that fails to parse is silently skipped by ReadAll, so it
        // protects nobody while looking like a claim on disk. The agent has to be
        // told, or it is the 2026-08-16 failure in miniature.
        File.WriteAllText(Path.Combine(_dir, "broken.md"), "this is not a claim file\n");

        var code = LedgerCommands.RunLedger(Array.Empty<string>(), Env, _out.Add);

        Assert.Equal(0, code);
        Assert.Contains("broken.md", Text);
    }

    [Fact]
    public void NewBatchId_IsUniquePerCallWithinTheSameSecond()
    {
        var t = new DateTime(2026, 8, 16, 8, 0, 0, DateTimeKind.Utc);

        var a = LedgerCommands.NewBatchId(t, "aaaa");
        var b = LedgerCommands.NewBatchId(t, "bbbb");

        Assert.NotEqual(a, b);
        Assert.StartsWith("A-20260816-080000", a);
    }
}
