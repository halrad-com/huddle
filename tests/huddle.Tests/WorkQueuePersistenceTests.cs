using Huddle;
namespace Huddle.Tests;

public class WorkQueuePersistenceTests
{
    private static WorkUnit U(string id, string[] deps) =>
        new(id, "huddle", "backenddev", "p", ["f.cs"], deps);

    [Fact]
    public void State_survives_save_and_reload()
    {
        var dir = Path.Combine(Path.GetTempPath(), "huddle-queue-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var q1 = new WorkQueue(dir);
            q1.Enqueue([U("a", []), U("b", ["a"])]);
            q1.MarkActive("a"); q1.MarkDone("a");

            var q2 = new WorkQueue(dir);
            q2.Load();
            Assert.Equal(QueueState.Done, q2.StateOf("a"));
            Assert.Contains(q2.Dispatchable(), u => u.Id == "b"); // dep satisfied after reload
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
