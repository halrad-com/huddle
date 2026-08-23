using Huddle;

namespace Huddle.Tests;

public class LedgerStateMachineTests
{
    [Theory]
    [InlineData("ideated", "decided", true)]
    [InlineData("decided", "planned", true)]
    [InlineData("planned", "dispatched", true)]
    [InlineData("dispatched", "delivered", true)]
    [InlineData("delivered", "accepted", true)]
    [InlineData("ideated", "dropped", true)]
    [InlineData("delivered", "dropped", true)]
    [InlineData("accepted", "dropped", false)]
    [InlineData("dropped", "ideated", false)]
    [InlineData("planned", "decided", false)]
    [InlineData("ideated", "accepted", false)]
    [InlineData("ideated", "ideated", false)]
    public void Hierarchy_transitions(string from, string to, bool ok) =>
        Assert.Equal(ok, LedgerStateMachine.CanTransitionHierarchy(from, to));

    [Theory]
    [InlineData("assigned", "acked", true)]
    [InlineData("acked", "in-progress", true)]
    [InlineData("in-progress", "delivered", true)]
    [InlineData("delivered", "accepted", true)]
    [InlineData("assigned", "declined", true)]
    [InlineData("acked", "declined", true)]
    [InlineData("in-progress", "declined", false)]
    [InlineData("acked", "abandoned", true)]
    [InlineData("in-progress", "abandoned", true)]
    [InlineData("assigned", "abandoned", false)]
    [InlineData("delivered", "abandoned", false)]
    [InlineData("accepted", "delivered", false)]
    [InlineData("declined", "acked", false)]
    // A forward JUMP is legal. An agent that does the work and sends task-complete never
    // sent an ack or a progress line; refusing that would recreate the "unknown task"
    // nack for work that really happened.
    [InlineData("assigned", "in-progress", true)]
    [InlineData("assigned", "delivered", true)]
    [InlineData("acked", "delivered", true)]
    // ...except into `accepted`, which must come from `delivered`. Acceptance is a
    // deliberate act on work actually handed over; letting it jump the queue would
    // hollow out the one gate this design is built around.
    [InlineData("assigned", "accepted", false)]
    [InlineData("acked", "accepted", false)]
    [InlineData("in-progress", "accepted", false)]
    public void Task_transitions(string from, string to, bool ok) =>
        Assert.Equal(ok, LedgerStateMachine.CanTransitionTask(from, to));

    [Theory]
    [InlineData("accepted", true)] [InlineData("dropped", true)] [InlineData("declined", true)]
    [InlineData("abandoned", true)] [InlineData("delivered", false)] [InlineData("assigned", false)]
    public void Terminal(string s, bool t) => Assert.Equal(t, LedgerStateMachine.IsTerminal(s));

    [Fact]
    public void Deliverable_without_accepts_cannot_be_accepted()
    {
        var d = new LedgerRow(new LedgerId(LedgerType.Deliverable, 1, null), LedgerType.Deliverable, null, "x", "delivered", null, null, null, [], 1);
        Assert.False(LedgerStateMachine.CanAccept(d, out var why));
        Assert.Contains("accepts", why);
        var ok = d with { Accepts = "SomeTests" };
        Assert.True(LedgerStateMachine.CanAccept(ok, out _));
    }

    [Fact]
    public void Non_deliverables_are_not_gated_by_accepts()
    {
        var f = new LedgerRow(new LedgerId(LedgerType.Feature, 1, null), LedgerType.Feature, null, "x", "delivered", null, null, null, [], 1);
        Assert.True(LedgerStateMachine.CanAccept(f, out _));
    }

    [Fact]
    public void Accept_requires_delivered_first()
    {
        var f = new LedgerRow(new LedgerId(LedgerType.Feature, 1, null), LedgerType.Feature, null, "x", "planned", null, null, null, [], 1);
        Assert.False(LedgerStateMachine.CanAccept(f, out var why));
        Assert.Contains("delivered", why);
    }
}
