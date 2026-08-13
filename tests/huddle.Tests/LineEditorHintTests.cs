using Xunit;
using Huddle;

namespace HuddleTests;

// The hint rides the Ghost channel: when Complete() has nothing, ComputeGhost
// falls back to Hint(), and Tab stays a no-op because Tab consults Complete()
// directly. These tests drive the pure Step machine with a hinting completer.
public class LineEditorHintTests
{
    private sealed class HintingCompleter : ICompleter
    {
        public IReadOnlyList<string> Complete(string input) =>
            input == "sa" ? new[] { "say" } : System.Array.Empty<string>();
        public string Hint(string input) =>
            input == "say " ? "<instance> <text>" : "";
    }

    private static readonly HintingCompleter C = new();
    private static ConsoleKeyInfo Ch(char c) => new(c, ConsoleKey.NoName, false, false, false);
    private static ConsoleKeyInfo K(ConsoleKey k) => new('\0', k, false, false, false);

    private static EditState Type(string s)
    {
        var st = EditState.Empty;
        foreach (var c in s) (st, _) = LineEditorLogic.Step(st, Ch(c), C, System.Array.Empty<string>());
        return st;
    }

    [Fact]
    public void Hint_appears_as_ghost_when_no_completion()
    {
        var st = Type("say ");
        Assert.Equal("<instance> <text>", st.Ghost);
    }

    [Fact]
    public void Completion_wins_over_hint()
    {
        var st = Type("sa");
        Assert.Equal("y", st.Ghost); // completion remainder, not a hint
    }

    [Fact]
    public void Tab_on_a_hint_is_a_noop()
    {
        var st = Type("say ");
        var before = st.Buffer;
        (st, _) = LineEditorLogic.Step(st, K(ConsoleKey.Tab), C, System.Array.Empty<string>());
        Assert.Equal(before, st.Buffer);   // the hint was never acceptable
        Assert.True(st.CycleIndex < 0);
    }

    [Fact]
    public void Hint_clears_when_typing_past_it()
    {
        var st = Type("say x");
        Assert.Equal("", st.Ghost);
    }

    [Fact]
    public void Empty_buffer_never_hints()
    {
        Assert.Equal("", Type("").Ghost);
        var st = Type("s");
        (st, _) = LineEditorLogic.Step(st, K(ConsoleKey.Backspace), C, System.Array.Empty<string>());
        Assert.Equal("", st.Ghost);
    }
}
