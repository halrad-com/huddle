using Huddle;
namespace Huddle.Tests;

public class WorkQueueScheduleTests
{
    private static WorkUnit U(string id, string[] files, string[] deps) =>
        new(id, "huddle", "backenddev", "p", files, deps);

    [Fact]
    public void Independent_units_are_all_dispatchable()
    {
        var q = new WorkQueue();
        q.Enqueue([U("a", ["x.cs"], []), U("b", ["y.cs"], [])]);
        Assert.Equal(2, q.Dispatchable().Count);
    }

    [Fact]
    public void File_overlap_with_an_active_unit_blocks_dispatch()
    {
        var q = new WorkQueue();
        q.Enqueue([U("a", ["x.cs"], []), U("b", ["X.CS"], [])]); // case-insensitive overlap
        q.MarkActive("a");
        var d = q.Dispatchable();
        Assert.DoesNotContain(d, u => u.Id == "b");
    }

    [Fact]
    public void Pending_dependency_blocks_dispatch_then_unblocks_when_done()
    {
        var q = new WorkQueue();
        q.Enqueue([U("a", ["x.cs"], []), U("b", ["y.cs"], ["a"])]);
        Assert.DoesNotContain(q.Dispatchable(), u => u.Id == "b"); // a not done
        q.MarkActive("a"); q.MarkDone("a");
        Assert.Contains(q.Dispatchable(), u => u.Id == "b");
    }

    [Fact]
    public void Failed_dependency_keeps_dependent_blocked()
    {
        var q = new WorkQueue();
        q.Enqueue([U("a", ["x.cs"], []), U("b", ["y.cs"], ["a"])]);
        q.MarkActive("a"); q.MarkFailed("a");
        Assert.DoesNotContain(q.Dispatchable(), u => u.Id == "b");
    }
}
