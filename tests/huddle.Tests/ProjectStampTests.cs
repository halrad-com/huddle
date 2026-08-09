using System.Text.Json;
using Huddle;
using Xunit;

namespace HuddleTests;

// Projects phase 1: the optional project stamp flows dispatch -> session -> claims ->
// state.json/roster. Legacy files (no stamp) must read back as empty/null.
public class ProjectStampTests
{
    [Fact]
    public void Claim_WithProject_RoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"claims-{Guid.NewGuid():N}");
        try
        {
            var claims = new WorkLedgerClaims(dir, _ => { });
            var path = claims.Write(new WorkLedgerClaim(
                "app:architect", "myapp", "B-test", DateTime.UtcNow, new string('a', 40),
                new[] { "src/x.cs" }, OwnerGuid: "", Project: "casting"));

            var read = claims.ReadFile(path);
            Assert.NotNull(read);
            Assert.Equal("casting", read!.Project);
            Assert.Equal(new[] { "src/x.cs" }, read.Files);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Claim_Legacy_NoProjectLine_ReadsEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"claims-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "B-old-app_architect.md");
            File.WriteAllText(path, """
                # B-old-app_architect

                - **Session:** app:architect
                - **Repo:** myapp
                - **Batch:** B-old
                - **Claimed at:** 2026-08-01T00:00:00Z
                - **Base commit:** 0000000000000000000000000000000000000000
                - **Files:**
                  - src/x.cs
                """);

            var read = new WorkLedgerClaims(dir, _ => { }).ReadFile(path);
            Assert.NotNull(read);
            Assert.Equal("", read!.Project);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void StateEntry_Project_RoundTrips()
    {
        var json = JsonSerializer.Serialize(new SessionStateEntry
        {
            InstanceId = "app:architect", RepoName = "app", Pid = 1,
            StartedAt = DateTime.Now, Project = "casting"
        });
        var back = JsonSerializer.Deserialize<SessionStateEntry>(json)!;
        Assert.Equal("casting", back.Project);

        // Legacy entry without the field → null.
        var legacy = JsonSerializer.Deserialize<SessionStateEntry>(
            """{"instanceId":"a:b","repoName":"a","pid":1,"startedAt":"2026-08-09T10:00:00"}""")!;
        Assert.Null(legacy.Project);
    }
}
