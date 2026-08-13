using Xunit;
using Huddle;

namespace HuddleTests;

public class BroadcastParseTests
{
    [Fact]
    public void Whole_line_becomes_message_no_word_eaten()
    {
        var p = ConsoleUI.ParseBroadcast("finalize the spec now");
        Assert.NotNull(p);
        Assert.Null(p!.Value.RepoCsv);
        Assert.Equal("finalize the spec now", p.Value.Message);
    }

    [Fact]
    public void Repo_prefix_is_peeled_then_whole_rest_is_message()
    {
        var p = ConsoleUI.ParseBroadcast("@otherapp,myapp hold and report");
        Assert.NotNull(p);
        Assert.Equal("otherapp,myapp", p!.Value.RepoCsv);
        Assert.Equal("hold and report", p.Value.Message);
    }

    [Fact]
    public void Derived_subject_is_nonempty_label()
    {
        var p = ConsoleUI.ParseBroadcast("finalize the spec now");
        Assert.NotNull(p);
        Assert.False(string.IsNullOrWhiteSpace(p!.Value.Subject));
    }

    [Fact]
    public void Leading_dash_message_keeps_its_first_word()
    {
        // The production regression: "broadcast - yes the contract is..."
        // used to send subject "-" and drop it from the body.
        var p = ConsoleUI.ParseBroadcast("- yes the contract is signed");
        Assert.NotNull(p);
        Assert.Equal("- yes the contract is signed", p!.Value.Message);
    }

    [Fact]
    public void Empty_message_is_rejected()
    {
        Assert.Null(ConsoleUI.ParseBroadcast("@otherapp"));   // repo only, no message
        Assert.Null(ConsoleUI.ParseBroadcast(""));
        Assert.Null(ConsoleUI.ParseBroadcast("   "));
    }

    [Fact]
    public void Bare_at_token_stays_in_message()
    {
        var p = ConsoleUI.ParseBroadcast("@ everyone hold");
        Assert.NotNull(p);
        Assert.Null(p.Value.RepoCsv);
        Assert.Equal("@ everyone hold", p.Value.Message);
    }

    [Fact]
    public void Single_word_message_is_accepted()
    {
        var p = ConsoleUI.ParseBroadcast("standup");
        Assert.NotNull(p);
        Assert.Null(p!.Value.RepoCsv);
        Assert.Equal("standup", p.Value.Message);
        Assert.Equal("standup", p.Value.Subject);
    }
}
