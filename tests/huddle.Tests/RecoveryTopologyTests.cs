using Huddle;
using Xunit;

namespace HuddleTests;

// I010 F2 topology: the recover listing marks dispatched-by lineage and hubs so the
// operator recovers coordinators before workers. FindDispatcher is the pure core:
// match a dispatch-batch document against a dead session's repo:persona + spawn time.
public class RecoveryTopologyTests
{
    private const string HarmonicBatch = """
        {
          "from": "myapp:researcher",
          "to": "_huddle",
          "timestamp": "2026-08-09T03:05:00Z",
          "type": "command",
          "subject": "dispatch-batch",
          "body": {
            "batchId": "B-harmonic-term",
            "tasks": [
              { "repo": "otherapp", "persona": "architect", "prompt": "TASK A", "files": ["a.cs"] },
              { "repo": "myapp", "persona": "architect", "prompt": "TASK B", "files": ["b.cs"] }
            ]
          }
        }
        """;

    private static readonly DateTime BatchTime = DateTime.Parse("2026-08-09T03:05:00Z",
        null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

    [Fact]
    public void FindDispatcher_MatchingRepoPersonaAndWindow_ReturnsSender()
    {
        var started = BatchTime.AddMinutes(1); // spawned right after the batch
        var from = RecoveryTopology.FindDispatcher(HarmonicBatch, "otherapp", "architect", started);
        Assert.Equal("myapp:researcher", from);
    }

    [Fact]
    public void FindDispatcher_WrongPersona_ReturnsNull()
    {
        var started = BatchTime.AddMinutes(1);
        Assert.Null(RecoveryTopology.FindDispatcher(HarmonicBatch, "otherapp", "backenddev", started));
    }

    [Fact]
    public void FindDispatcher_StartedHoursLater_ReturnsNull()
    {
        // A same-named session started 2h after the batch was NOT spawned by it.
        var started = BatchTime.AddHours(2);
        Assert.Null(RecoveryTopology.FindDispatcher(HarmonicBatch, "otherapp", "architect", started));
    }

    [Fact]
    public void FindDispatcher_UnknownStartTime_ReturnsNull()
    {
        // No spawn time → no confident lineage claim.
        Assert.Null(RecoveryTopology.FindDispatcher(HarmonicBatch, "otherapp", "architect", null));
    }

    [Fact]
    public void FindDispatcher_NotADispatchBatch_ReturnsNull()
    {
        var mail = """{"from":"a:b","to":"c:d","timestamp":"2026-08-09T03:05:00Z","type":"info","subject":"hello","body":{}}""";
        Assert.Null(RecoveryTopology.FindDispatcher(mail, "otherapp", "architect", BatchTime));
    }

    [Fact]
    public void FindDispatcher_MalformedJson_ReturnsNull()
    {
        Assert.Null(RecoveryTopology.FindDispatcher("{not json", "otherapp", "architect", BatchTime));
    }

    [Fact]
    public void Analyze_CountsUnreadMail_AndMarksHub()
    {
        var root = Path.Combine(Path.GetTempPath(), $"topo-{Guid.NewGuid():N}");
        try
        {
            var inbox = Path.Combine(root, "ipc", "app_architect", "inbox");
            Directory.CreateDirectory(inbox);
            File.WriteAllText(Path.Combine(inbox, "001-mail.json"), "{}");
            File.WriteAllText(Path.Combine(inbox, "002-mail.json"), "{}");
            var processed = Path.Combine(root, "ipc", "_huddle", "processed");
            Directory.CreateDirectory(processed);

            var info = RecoveryTopology.Analyze("app:architect", DateTime.Now,
                processed, Path.Combine(root, "ipc"));

            Assert.Equal(2, info.UnreadMail);
            Assert.True(info.IsHub);
            Assert.Null(info.DispatchedBy);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Analyze_DispatcherIdentity_MarksHub()
    {
        var root = Path.Combine(Path.GetTempPath(), $"topo-{Guid.NewGuid():N}");
        try
        {
            var processed = Path.Combine(root, "ipc", "_huddle", "processed");
            Directory.CreateDirectory(processed);
            File.WriteAllText(Path.Combine(processed, "038-dispatch.json"), HarmonicBatch);

            // The researcher sent a batch → it is a hub even with an empty inbox.
            var info = RecoveryTopology.Analyze("myapp:researcher", DateTime.Now,
                processed, Path.Combine(root, "ipc"));

            Assert.True(info.IsHub);
            Assert.Equal(0, info.UnreadMail);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Analyze_DispatchedWorker_GetsLineage()
    {
        var root = Path.Combine(Path.GetTempPath(), $"topo-{Guid.NewGuid():N}");
        try
        {
            var processed = Path.Combine(root, "ipc", "_huddle", "processed");
            Directory.CreateDirectory(processed);
            File.WriteAllText(Path.Combine(processed, "038-dispatch.json"), HarmonicBatch);

            var info = RecoveryTopology.Analyze("otherapp:architect-3", BatchTime.AddMinutes(1),
                processed, Path.Combine(root, "ipc"));

            Assert.Equal("myapp:researcher", info.DispatchedBy);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
