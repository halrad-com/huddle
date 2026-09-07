using Huddle;
using Xunit;

namespace HuddleTests;

// 2026-09-06: a hand-launched huddle inherited a stdin that was already at EOF. It
// recovered seven live sessions and terminated all seven one second later, having asked
// for confirmation on the same dead stdin and read the silence as "yes".
public class ShutdownPolicyTests
{
    [Fact]
    public void An_unreadable_answer_is_not_consent_to_terminate()
    {
        // The incident, in one assertion. Not being able to ask is not being told yes.
        Assert.False(ShutdownPolicy.IsConsent(null, liveSessions: 7));
    }

    [Fact]
    public void With_nothing_running_there_is_nothing_to_consent_to()
    {
        Assert.True(ShutdownPolicy.IsConsent(null, liveSessions: 0));
    }

    [Theory]
    [InlineData("y")]
    [InlineData("Y")]
    [InlineData("yes")]
    [InlineData("YES")]
    [InlineData("  y  ")]
    public void An_explicit_yes_is_consent(string answer)
    {
        Assert.True(ShutdownPolicy.IsConsent(answer, liveSessions: 7));
    }

    [Theory]
    [InlineData("")]
    [InlineData("n")]
    [InlineData("no")]
    [InlineData("   ")]
    [InlineData("ye")]
    [InlineData("yep")]
    public void Anything_else_is_not(string answer)
    {
        Assert.False(ShutdownPolicy.IsConsent(answer, liveSessions: 7));
    }

    [Fact]
    public void End_of_input_detaches_it_does_not_terminate()
    {
        // EOF is the absence of a keystroke: it says nobody is at the console. huddle
        // lets go of the fleet rather than taking it down with it.
        Assert.Equal(ShutdownAction.Detach, ShutdownPolicy.OnEndOfInput());
    }
}
