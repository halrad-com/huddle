using Xunit;
using Huddle;

namespace HuddleTests;

public class LineEditorTabHistoryTests
{
    private static readonly VerbCompleter C = new(new[] { "say", "send", "shell" });
    private static ConsoleKeyInfo Ch(char c) => new(c, ConsoleKey.NoName, false, false, false);
    private static ConsoleKeyInfo K(ConsoleKey k) => new('\0', k, false, false, false);

    private static EditState Type(string s, IReadOnlyList<string> hist)
    {
        var st = EditState.Empty;
        foreach (var c in s) (st, _) = LineEditorLogic.Step(st, Ch(c), C, hist);
        return st;
    }

    [Fact]
    public void Tab_accepts_top_match_with_trailing_space()
    {
        // Tab accepts AND appends a space: the verb is settled, the operator is
        // ready to type arguments (and the space kills the ghost by itself).
        var st = Type("se", System.Array.Empty<string>());
        (st, _) = LineEditorLogic.Step(st, K(ConsoleKey.Tab), C, System.Array.Empty<string>());
        Assert.Equal("send ", st.Buffer);
        Assert.Equal(5, st.Cursor);
    }

    [Fact]
    public void Tab_twice_cycles_to_next_match()
    {
        var st = Type("s", System.Array.Empty<string>()); // say, send, shell
        (st, _) = LineEditorLogic.Step(st, K(ConsoleKey.Tab), C, System.Array.Empty<string>());
        Assert.Equal("say ", st.Buffer);
        (st, _) = LineEditorLogic.Step(st, K(ConsoleKey.Tab), C, System.Array.Empty<string>());
        Assert.Equal("send ", st.Buffer);
        (st, _) = LineEditorLogic.Step(st, K(ConsoleKey.Tab), C, System.Array.Empty<string>());
        Assert.Equal("shell ", st.Buffer);
        (st, _) = LineEditorLogic.Step(st, K(ConsoleKey.Tab), C, System.Array.Empty<string>());
        Assert.Equal("say ", st.Buffer); // wraps
    }

    [Fact]
    public void Typing_after_tab_stops_cycling()
    {
        var st = Type("s", System.Array.Empty<string>());
        (st, _) = LineEditorLogic.Step(st, K(ConsoleKey.Tab), C, System.Array.Empty<string>()); // "say "
        (st, _) = LineEditorLogic.Step(st, Ch('x'), C, System.Array.Empty<string>());            // "say x"
        Assert.Equal("say x", st.Buffer);
        Assert.True(st.CycleIndex < 0);
    }

    [Fact]
    public void Tab_on_empty_buffer_is_noop()
    {
        var st = EditState.Empty;
        (st, _) = LineEditorLogic.Step(st, K(ConsoleKey.Tab), C, System.Array.Empty<string>());
        Assert.Equal("", st.Buffer);
        Assert.True(st.CycleIndex < 0);
    }

    [Fact]
    public void Tab_with_no_match_is_noop()
    {
        var st = Type("zzz", System.Array.Empty<string>());
        (st, _) = LineEditorLogic.Step(st, K(ConsoleKey.Tab), C, System.Array.Empty<string>());
        Assert.Equal("zzz", st.Buffer);
    }

    [Fact]
    public void Up_recalls_previous_command_down_restores()
    {
        var hist = new[] { "broadcast hi", "status" }; // index 0 = most recent
        var st = Type("sa", hist);
        (st, _) = LineEditorLogic.Step(st, K(ConsoleKey.UpArrow), C, hist);
        Assert.Equal("broadcast hi", st.Buffer);
        (st, _) = LineEditorLogic.Step(st, K(ConsoleKey.UpArrow), C, hist);
        Assert.Equal("status", st.Buffer);
        (st, _) = LineEditorLogic.Step(st, K(ConsoleKey.DownArrow), C, hist);
        Assert.Equal("broadcast hi", st.Buffer);
        (st, _) = LineEditorLogic.Step(st, K(ConsoleKey.DownArrow), C, hist);
        Assert.Equal("sa", st.Buffer); // back to the live buffer
    }

    [Fact]
    public void Up_at_oldest_entry_stays_put()
    {
        var hist = new[] { "status" };
        var st = Type("", hist);
        (st, _) = LineEditorLogic.Step(st, K(ConsoleKey.UpArrow), C, hist);
        (st, _) = LineEditorLogic.Step(st, K(ConsoleKey.UpArrow), C, hist);
        Assert.Equal("status", st.Buffer);
        Assert.Equal(0, st.HistoryIndex);
    }

    [Fact]
    public void Up_with_empty_history_is_noop()
    {
        var st = Type("se", System.Array.Empty<string>());
        (st, _) = LineEditorLogic.Step(st, K(ConsoleKey.UpArrow), C, System.Array.Empty<string>());
        Assert.Equal("se", st.Buffer);
        Assert.Equal(-1, st.HistoryIndex);
    }

    [Fact]
    public void Down_without_history_navigation_is_noop()
    {
        var hist = new[] { "status" };
        var st = Type("se", hist);
        (st, _) = LineEditorLogic.Step(st, K(ConsoleKey.DownArrow), C, hist);
        Assert.Equal("se", st.Buffer);
        Assert.Equal(-1, st.HistoryIndex);
    }

    [Fact]
    public void Down_to_live_buffer_restores_ghost()
    {
        var hist = new[] { "status" };
        var st = Type("se", hist); // ghost "nd"
        (st, _) = LineEditorLogic.Step(st, K(ConsoleKey.UpArrow), C, hist);
        (st, _) = LineEditorLogic.Step(st, K(ConsoleKey.DownArrow), C, hist);
        Assert.Equal("se", st.Buffer);
        Assert.Equal(2, st.Cursor);
        Assert.Equal("nd", st.Ghost);
        Assert.Equal(-1, st.HistoryIndex); // left history...
        Assert.Equal("", st.StashedBuffer); // ...and the stash was spent
    }

    [Fact]
    public void Cursor_move_preserves_tab_cycle()
    {
        // Deliberate: Home/End/Left/Right are cursor-only and do NOT cancel a cycle.
        var st = Type("s", System.Array.Empty<string>());
        (st, _) = LineEditorLogic.Step(st, K(ConsoleKey.Tab), C, System.Array.Empty<string>()); // "say "
        (st, _) = LineEditorLogic.Step(st, K(ConsoleKey.Home), C, System.Array.Empty<string>());
        (st, _) = LineEditorLogic.Step(st, K(ConsoleKey.RightArrow), C, System.Array.Empty<string>());
        (st, _) = LineEditorLogic.Step(st, K(ConsoleKey.LeftArrow), C, System.Array.Empty<string>());
        (st, _) = LineEditorLogic.Step(st, K(ConsoleKey.End), C, System.Array.Empty<string>());
        (st, _) = LineEditorLogic.Step(st, K(ConsoleKey.Tab), C, System.Array.Empty<string>());
        Assert.Equal("send ", st.Buffer);
    }

    [Fact]
    public void History_navigation_cancels_tab_cycle()
    {
        var hist = new[] { "status" };
        var st = Type("s", hist);
        (st, _) = LineEditorLogic.Step(st, K(ConsoleKey.Tab), C, hist); // "say ", cycling
        (st, _) = LineEditorLogic.Step(st, K(ConsoleKey.UpArrow), C, hist);
        Assert.Equal("status", st.Buffer);
        Assert.True(st.CycleIndex < 0);
        Assert.Equal("", st.TabPrefix);
    }
}
