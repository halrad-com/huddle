using Huddle;
namespace Huddle.Tests;

public class WorkQueueValidationTests
{
    private static WorkUnit U(string id, string[] files, string[] deps) =>
        new(id, "huddle", "backenddev", "p", files, deps);

    [Fact]
    public void Enqueue_accepts_a_valid_batch()
    {
        var q = new WorkQueue();
        q.Enqueue([U("a", ["x.cs"], []), U("b", ["y.cs"], ["a"])]);
    }

    [Fact]
    public void Enqueue_rejects_duplicate_id()
    {
        var q = new WorkQueue();
        Assert.Throws<InvalidOperationException>(() =>
            q.Enqueue([U("a", ["x.cs"], []), U("a", ["y.cs"], [])]));
    }

    [Fact]
    public void Enqueue_rejects_unknown_dependency()
    {
        var q = new WorkQueue();
        Assert.Throws<InvalidOperationException>(() =>
            q.Enqueue([U("a", ["x.cs"], ["nope"])]));
    }

    [Fact]
    public void Enqueue_rejects_cycle()
    {
        var q = new WorkQueue();
        Assert.Throws<InvalidOperationException>(() =>
            q.Enqueue([U("a", ["x.cs"], ["b"]), U("b", ["y.cs"], ["a"])]));
    }
}
