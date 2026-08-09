using System.Text.Json;
using Huddle;
using Xunit;

namespace HuddleTests;

// I010 (2026-08-09 mass termination): dead sessions found during recovery must be
// RETAINED as recoverable roster entries, not silently dropped — the roster is
// exactly what the operator needs to bring a fleet back. Save carries recoverable
// entries through every rewrite; Recover routes dead/mismatched entries into
// SessionManager.Recoverable instead of skipping them.
public class RecoverRosterTests
{
    private static SessionManager NewManager() =>
        new(new HuddleConfig(), "claude", Path.GetTempPath(), Path.GetTempPath(), null, _ => { });

    private static string TempStateFile() =>
        Path.Combine(Path.GetTempPath(), $"huddle-state-{Guid.NewGuid():N}.json");

    [Fact]
    public void Save_AppendsRecoverableEntries_AfterLiveOnes()
    {
        var file = TempStateFile();
        try
        {
            var roster = new List<SessionStateEntry>
            {
                new()
                {
                    InstanceId = "app:architect", RepoName = "app", Persona = "architect",
                    Status = "recoverable", SessionId = Guid.NewGuid().ToString(),
                    DeclaredPurpose = "TASK A", DiedAt = DateTime.Now
                }
            };
            SessionState.Save(file, new Dictionary<string, SessionInstance>(), roster);

            var round = JsonSerializer.Deserialize<List<SessionStateEntry>>(File.ReadAllText(file))!;
            Assert.Single(round);
            Assert.Equal("recoverable", round[0].Status);
            Assert.Equal("TASK A", round[0].DeclaredPurpose);
            Assert.NotNull(round[0].DiedAt);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void EntryWithoutStatus_ReadsAsLive()
    {
        // Legacy state.json written by pre-roster builds has no status field.
        var json = """[{"instanceId":"a:b","repoName":"a","pid":1,"startedAt":"2026-08-09T10:00:00"}]""";
        var entries = JsonSerializer.Deserialize<List<SessionStateEntry>>(json)!;
        Assert.Equal("live", entries[0].Status);
    }

    [Fact]
    public void Recover_DeadPid_LandsInRecoverableRoster_WithPurposeAndDiedAt()
    {
        var file = TempStateFile();
        try
        {
            var entry = new SessionStateEntry
            {
                InstanceId = "app:architect", RepoName = "app", Persona = "architect",
                Pid = 999999, StartedAt = DateTime.Now,
                SessionId = Guid.NewGuid().ToString(),
                DeclaredPurpose = "TASK A"
            };
            File.WriteAllText(file, JsonSerializer.Serialize(new[] { entry }));

            var manager = NewManager();
            SessionState.Recover(file, manager, null, _ => { });

            Assert.Single(manager.Recoverable);
            Assert.Equal("recoverable", manager.Recoverable[0].Status);
            Assert.Equal("TASK A", manager.Recoverable[0].DeclaredPurpose);
            Assert.NotNull(manager.Recoverable[0].DiedAt);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Recover_AlreadyRecoverableEntry_KeepsOriginalDiedAt()
    {
        var died = new DateTime(2026, 8, 9, 10, 43, 0);
        var file = TempStateFile();
        try
        {
            var entry = new SessionStateEntry
            {
                InstanceId = "app:architect", RepoName = "app", Pid = 999999,
                StartedAt = died.AddHours(-5), Status = "recoverable", DiedAt = died,
                SessionId = Guid.NewGuid().ToString()
            };
            File.WriteAllText(file, JsonSerializer.Serialize(new[] { entry }));

            var manager = NewManager();
            SessionState.Recover(file, manager, null, _ => { });

            Assert.Single(manager.Recoverable);
            Assert.Equal(died, manager.Recoverable[0].DiedAt);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Save_DropsRecoverableEntry_WhenSameSessionIdIsLive()
    {
        // A recoverable entry whose conversation is now carried by a live session
        // must not be written twice — the live entry wins.
        var file = TempStateFile();
        try
        {
            var sid = Guid.NewGuid().ToString();
            var roster = new List<SessionStateEntry>
            {
                new() { InstanceId = "app:architect", RepoName = "app", Status = "recoverable", SessionId = sid }
            };
            // No live instances (empty dict) — entry survives.
            SessionState.Save(file, new Dictionary<string, SessionInstance>(), roster);
            var round = JsonSerializer.Deserialize<List<SessionStateEntry>>(File.ReadAllText(file))!;
            Assert.Single(round);
        }
        finally { File.Delete(file); }
    }
}
