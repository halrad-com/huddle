using Huddle;
using Xunit;

namespace HuddleTests;

/// <summary>
/// The inverse resolver, used by the circulation desk to key a checkout on the REGISTERED repo
/// name rather than the folder it happens to sit in. This repo is the live example: the
/// directory is `myapp`, the registered name is `huddle`. Keying on the folder would let one
/// physical file be checked out twice under two spellings — I013 by a new route.
/// </summary>
public class RepoNameForRootTests : IDisposable
{
    private readonly string _configDir;
    private readonly string _claimsDir;

    public RepoNameForRootTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "huddle-reponame-" + Guid.NewGuid().ToString("N"));
        _claimsDir = Path.Combine(_configDir, "ipc", "workledger", "claims");
        Directory.CreateDirectory(_claimsDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_configDir, recursive: true); } catch { }
    }

    private void WriteConfig(string json) =>
        File.WriteAllText(Path.Combine(_configDir, "huddle.json"), json);

    [Fact]
    public void RegisteredNameWinsOverTheFolderName()
    {
        var root = Path.Combine(_configDir, "myapp");
        WriteConfig($$"""
        { "sessions": [ { "name": "huddle", "root": {{System.Text.Json.JsonSerializer.Serialize(root)}} } ] }
        """);

        Assert.Equal("huddle", LedgerCommands.RepoNameForRoot(_claimsDir, root));
    }

    [Fact]
    public void TrailingSeparatorAndCaseDoNotMatter()
    {
        var root = Path.Combine(_configDir, "myapp");
        WriteConfig($$"""
        { "sessions": [ { "name": "huddle", "root": {{System.Text.Json.JsonSerializer.Serialize(root + Path.DirectorySeparatorChar)}} } ] }
        """);

        Assert.Equal("huddle", LedgerCommands.RepoNameForRoot(_claimsDir, root.ToUpperInvariant()));
    }

    [Fact]
    public void UnregisteredRootIsNull()
    {
        WriteConfig($$"""
        { "sessions": [ { "name": "huddle", "root": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(_configDir, "myapp"))}} } ] }
        """);

        Assert.Null(LedgerCommands.RepoNameForRoot(_claimsDir, Path.Combine(_configDir, "elsewhere")));
    }

    // A missing, unreadable or malformed config must cost the caller nothing — the desk falls
    // back to the folder name rather than refusing to hand out a book.
    [Fact]
    public void NoConfigIsNullNotAnError()
    {
        Assert.Null(LedgerCommands.RepoNameForRoot(_claimsDir, Path.Combine(_configDir, "myapp")));
    }

    [Fact]
    public void MalformedConfigIsNullNotAnError()
    {
        WriteConfig("{ this is not json ");

        Assert.Null(LedgerCommands.RepoNameForRoot(_claimsDir, Path.Combine(_configDir, "myapp")));
    }

    [Fact]
    public void EmptyRootIsNull()
    {
        Assert.Null(LedgerCommands.RepoNameForRoot(_claimsDir, ""));
    }

    // Two sessions rooted in the same checkout (a repo and a persona-specific entry) must
    // resolve to ONE name, or two agents would key the same file differently.
    [Fact]
    public void FirstRegistrationWinsSoTheAnswerIsStable()
    {
        var root = Path.Combine(_configDir, "myapp");
        var json = System.Text.Json.JsonSerializer.Serialize(root);
        WriteConfig($$"""
        { "sessions": [
            { "name": "huddle", "root": {{json}} },
            { "name": "huddle-alt", "root": {{json}} }
        ] }
        """);

        Assert.Equal("huddle", LedgerCommands.RepoNameForRoot(_claimsDir, root));
    }
}
