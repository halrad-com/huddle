using Huddle;
using Xunit;

namespace HuddleTests;

// The catalog is the exclusion primitive: a file is available (no entry) or checked out (an
// entry exists), and the create-if-not-exists is atomic across PROCESSES — which is exactly
// what WorkLedgerClaims.TryClaim cannot promise (see the scope note at Orchestrator.cs:1536).
// Identity is the borrower NAME, with no roster anywhere, so a session huddle spawned and one
// it never heard of are treated identically.
public class FileCatalogTests : IDisposable
{
    private readonly string _dir;
    private DateTime _now = new(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc);
    private readonly FileCatalog _cat;

    public FileCatalogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "huddle-catalog-" + Guid.NewGuid().ToString("N"));
        _cat = new FileCatalog(_dir, () => _now);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void AvailableFileHasNoEntry()
    {
        Assert.Null(_cat.Status("huddle", "src/One.cs"));
        Assert.Empty(_cat.ReadAll());
    }

    [Fact]
    public void CheckOutMakesTheFileHeld()
    {
        var outcome = _cat.TryCheckOut("huddle", "src/One.cs", "codex:refactor", out var holder);

        Assert.Equal(CheckoutOutcome.CheckedOut, outcome);
        Assert.Equal("codex:refactor", holder!.Borrower);
        Assert.Equal("codex:refactor", _cat.Status("huddle", "src/One.cs")!.Borrower);
    }

    [Fact]
    public void SecondBorrowerIsRefusedAndToldWhoHoldsIt()
    {
        _cat.TryCheckOut("huddle", "src/One.cs", "codex:refactor", out _);

        var outcome = _cat.TryCheckOut("huddle", "src/One.cs", "huddle:architect", out var holder);

        Assert.Equal(CheckoutOutcome.HeldByOther, outcome);
        Assert.Equal("codex:refactor", holder!.Borrower);
    }

    // The asymmetry that made the old path unusable for outsiders: liveness was decided
    // against huddle's roster, so a borrower huddle had not spawned looked dead on arrival.
    // Here a foreign name holds a file against a huddle-spawned name with no roster involved.
    [Fact]
    public void ForeignBorrowerBlocksAHuddleSessionAndViceVersa()
    {
        Assert.Equal(CheckoutOutcome.CheckedOut,
            _cat.TryCheckOut("huddle", "src/One.cs", "codex:refactor", out _));
        Assert.Equal(CheckoutOutcome.HeldByOther,
            _cat.TryCheckOut("huddle", "src/One.cs", "huddle:architect", out _));

        Assert.Equal(CheckoutOutcome.CheckedOut,
            _cat.TryCheckOut("huddle", "src/Two.cs", "huddle:architect", out _));
        Assert.Equal(CheckoutOutcome.HeldByOther,
            _cat.TryCheckOut("huddle", "src/Two.cs", "cursor:dev", out _));
    }

    [Fact]
    public void SameBorrowerRenewsRatherThanBlockingItself()
    {
        _cat.TryCheckOut("huddle", "src/One.cs", "codex:refactor", out var first);
        _now = _now.AddMinutes(30);

        var outcome = _cat.TryCheckOut("huddle", "src/One.cs", "codex:refactor", out var again);

        Assert.Equal(CheckoutOutcome.Renewed, outcome);
        Assert.True(again!.DueAt > first!.DueAt);
    }

    [Fact]
    public void BorrowerNameIsFormAgnostic()
    {
        _cat.TryCheckOut("huddle", "src/One.cs", "huddle:architect", out _);

        // The same identity spelled with an underscore is the same borrower, matching how
        // the rest of the ledger compares session names.
        Assert.Equal(CheckoutOutcome.Renewed,
            _cat.TryCheckOut("huddle", "src/One.cs", "huddle_architect", out _));
    }

    [Fact]
    public void OverdueEntryIsReclaimedByTheNextBorrower()
    {
        _cat.TryCheckOut("huddle", "src/One.cs", "codex:refactor", out _, TimeSpan.FromMinutes(10));
        _now = _now.AddMinutes(11);

        var outcome = _cat.TryCheckOut("huddle", "src/One.cs", "huddle:architect", out var holder);

        Assert.Equal(CheckoutOutcome.CheckedOut, outcome);
        Assert.Equal("huddle:architect", holder!.Borrower);
        Assert.Equal("huddle:architect", _cat.Status("huddle", "src/One.cs")!.Borrower);
    }

    [Fact]
    public void LeaseStillRunningIsNotReclaimed()
    {
        _cat.TryCheckOut("huddle", "src/One.cs", "codex:refactor", out _, TimeSpan.FromMinutes(10));
        _now = _now.AddMinutes(9);

        Assert.Equal(CheckoutOutcome.HeldByOther,
            _cat.TryCheckOut("huddle", "src/One.cs", "huddle:architect", out _));
    }

    [Fact]
    public void RenewExtendsEveryFileTheBorrowerHolds()
    {
        _cat.TryCheckOut("huddle", "src/One.cs", "codex:refactor", out _, TimeSpan.FromMinutes(10));
        _cat.TryCheckOut("huddle", "src/Two.cs", "codex:refactor", out _, TimeSpan.FromMinutes(10));
        _cat.TryCheckOut("huddle", "src/Three.cs", "cursor:dev", out _, TimeSpan.FromMinutes(10));
        _now = _now.AddMinutes(5);

        var renewed = _cat.Renew("codex:refactor", TimeSpan.FromMinutes(60));

        Assert.Equal(2, renewed);
        Assert.Equal(_now.AddMinutes(60), _cat.Status("huddle", "src/One.cs")!.DueAt);
        // Somebody else's lease is untouched.
        Assert.Equal(_now.AddMinutes(5), _cat.Status("huddle", "src/Three.cs")!.DueAt);
    }

    [Fact]
    public void CheckInReturnsTheFileToAvailable()
    {
        _cat.TryCheckOut("huddle", "src/One.cs", "codex:refactor", out _);

        Assert.True(_cat.CheckIn("huddle", "src/One.cs", "codex:refactor"));
        Assert.Null(_cat.Status("huddle", "src/One.cs"));
    }

    [Fact]
    public void CheckInCannotReturnSomebodyElsesBook()
    {
        _cat.TryCheckOut("huddle", "src/One.cs", "codex:refactor", out _);

        Assert.False(_cat.CheckIn("huddle", "src/One.cs", "huddle:architect"));
        Assert.Equal("codex:refactor", _cat.Status("huddle", "src/One.cs")!.Borrower);
    }

    [Fact]
    public void CheckInOfAnAvailableFileIsFalseNotAnError()
    {
        Assert.False(_cat.CheckIn("huddle", "src/One.cs", "codex:refactor"));
    }

    [Fact]
    public void MultiFileCheckoutTakesEverythingWhenFree()
    {
        var ok = _cat.TryCheckOutAll("huddle", new[] { "src/b.cs", "src/a.cs" }, "codex:refactor",
                                     out var taken, out _, out _);

        Assert.True(ok);
        Assert.Equal(2, taken.Count);
        Assert.NotNull(_cat.Status("huddle", "src/a.cs"));
        Assert.NotNull(_cat.Status("huddle", "src/b.cs"));
    }

    // All-or-nothing is the property that keeps two agents from holding half a set each and
    // waiting on the other: a refused set leaves the catalog exactly as it was.
    [Fact]
    public void MultiFileCheckoutRollsBackWhenAnyFileIsHeld()
    {
        _cat.TryCheckOut("huddle", "src/z.cs", "cursor:dev", out _);

        var ok = _cat.TryCheckOutAll("huddle", new[] { "src/a.cs", "src/z.cs" }, "codex:refactor",
                                     out var taken, out var blockedBy, out var blockedPath);

        Assert.False(ok);
        Assert.Empty(taken);
        Assert.Equal("cursor:dev", blockedBy!.Borrower);
        Assert.Equal("src/z.cs", blockedPath);
        // The file it managed to take first must be back on the shelf.
        Assert.Null(_cat.Status("huddle", "src/a.cs"));
    }

    [Fact]
    public void MultiFileCheckoutKeepsFilesTheBorrowerAlreadyHeld()
    {
        // Held from an earlier batch, well before this call.
        _cat.TryCheckOut("huddle", "src/a.cs", "codex:refactor", out _);
        _cat.TryCheckOut("huddle", "src/z.cs", "cursor:dev", out _);
        _now = _now.AddMinutes(5);

        var ok = _cat.TryCheckOutAll("huddle", new[] { "src/a.cs", "src/z.cs" }, "codex:refactor",
                                     out _, out _, out _);

        Assert.False(ok);
        // Rolling back a pre-existing checkout would hand away a book still being read.
        Assert.Equal("codex:refactor", _cat.Status("huddle", "src/a.cs")!.Borrower);
    }

    [Fact]
    public void ReadAllListsEveryCheckedOutFileAcrossRepos()
    {
        _cat.TryCheckOut("huddle", "src/One.cs", "codex:refactor", out _);
        _cat.TryCheckOut("netlib", "src/netcfg/netcfgManager.cs", "huddle:architect", out _);

        var all = _cat.ReadAll();

        Assert.Equal(2, all.Count);
        Assert.Contains(all, e => e.Repo == "netlib" && e.RelPath == "src/netcfg/netcfgManager.cs");
    }

    [Fact]
    public void SamePathInTwoReposAreSeparateBooks()
    {
        _cat.TryCheckOut("huddle", "src/One.cs", "codex:refactor", out _);

        Assert.Equal(CheckoutOutcome.CheckedOut,
            _cat.TryCheckOut("netlib", "src/One.cs", "huddle:architect", out _));
    }

    [Fact]
    public void BackslashesAndLeadingSlashesNormalizeToOneBook()
    {
        _cat.TryCheckOut("huddle", "src/One.cs", "codex:refactor", out _);

        Assert.Equal(CheckoutOutcome.Renewed,
            _cat.TryCheckOut("huddle", @"\src\One.cs", "codex:refactor", out _));
        Assert.Single(_cat.ReadAll());
    }

    [Fact]
    public void EntryNameIsReadableAndPathSafe()
    {
        var name = FileCatalog.EntryName("huddle", "src/sub/One.cs");

        Assert.Equal("huddle~src~sub~One.cs.md", name);
        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain('\\', name);
    }

    [Fact]
    public void VeryLongPathDegradesToAHashInsteadOfBreaking()
    {
        var deep = string.Join('/', Enumerable.Repeat("averyverylongdirectoryname", 12)) + "/File.cs";

        var name = FileCatalog.EntryName("huddle", deep);

        Assert.True(name.Length < 60, $"name was {name.Length} chars: {name}");
        // The real path is still recoverable, because it is recorded inside the entry.
        _cat.TryCheckOut("huddle", deep, "codex:refactor", out _);
        Assert.Equal(deep, _cat.Status("huddle", deep)!.RelPath);
    }

    [Fact]
    public void OverdueEntryIsStillReportedAsCheckedOutJustLate()
    {
        _cat.TryCheckOut("huddle", "src/One.cs", "codex:refactor", out _, TimeSpan.FromMinutes(10));
        _now = _now.AddMinutes(30);

        var status = _cat.Status("huddle", "src/One.cs");

        Assert.NotNull(status);
        Assert.True(status!.IsOverdue(_now));
    }

    [Fact]
    public void EmptyBorrowerIsRefused()
    {
        Assert.Equal(CheckoutOutcome.Failed, _cat.TryCheckOut("huddle", "src/One.cs", "  ", out _));
        Assert.Empty(_cat.ReadAll());
    }

    // Katalog's contribution: a checksum at checkout turns "who holds it" into "and what did
    // they do to it", and being derived it cannot drift from the bytes the way a hand-kept
    // version counter does.
    [Fact]
    public void CheckoutRecordsTheContentHashWhenTheFileExists()
    {
        var root = Path.Combine(_dir, "tree");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "One.cs"), "original");

        _cat.TryCheckOut("huddle", "src/One.cs", "codex:refactor", out var holder, null, root);

        Assert.NotEqual("", holder!.Hash);
        Assert.Equal(holder.Hash, _cat.Status("huddle", "src/One.cs")!.Hash);
    }

    [Fact]
    public void HashChangesWhenTheFileIsEdited()
    {
        var root = Path.Combine(_dir, "tree");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        var file = Path.Combine(root, "src", "One.cs");
        File.WriteAllText(file, "original");

        _cat.TryCheckOut("huddle", "src/One.cs", "codex:refactor", out var holder, null, root);
        File.WriteAllText(file, "edited");

        Assert.NotEqual(holder!.Hash, FileCatalog.HashOf(root, "src/One.cs"));
    }

    // A checkout must never fail because the librarian could not weigh the book: a file that
    // does not exist yet is a perfectly normal thing to reserve before creating it.
    [Fact]
    public void MissingFileOrNoRootStillCheckesOutWithAnEmptyHash()
    {
        Assert.Equal(CheckoutOutcome.CheckedOut,
            _cat.TryCheckOut("huddle", "src/New.cs", "codex:refactor", out var a, null, Path.Combine(_dir, "tree")));
        Assert.Equal("", a!.Hash);

        Assert.Equal(CheckoutOutcome.CheckedOut,
            _cat.TryCheckOut("huddle", "src/Other.cs", "codex:refactor", out var b));
        Assert.Equal("", b!.Hash);
    }

    [Fact]
    public void HashOfIsEmptyRatherThanThrowingForBadInput()
    {
        Assert.Equal("", FileCatalog.HashOf("", "src/One.cs"));
        Assert.Equal("", FileCatalog.HashOf(Path.Combine(_dir, "nope"), "src/One.cs"));
    }

    [Fact]
    public void CatalogDirSitsBesideTheClaimsDrawerNotInsideIt()
    {
        var dir = FileCatalog.DirBesideClaims(@"C:\repo\ipc\workledger\claims");

        Assert.Equal(Path.Combine(@"C:\repo\ipc\workledger", "catalog"), dir);
    }
}
