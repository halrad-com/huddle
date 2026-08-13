using Xunit;
using Huddle;

namespace HuddleTests;

public class LineEditorStepTests
{
    private static readonly VerbCompleter C = new(new[] { "say", "send", "shell", "broadcast" });
    private static IReadOnlyList<string> NoHistory = System.Array.Empty<string>();

    private static ConsoleKeyInfo Ch(char c) => new(c, ConsoleKey.NoName, false, false, false);
    private static ConsoleKeyInfo K(ConsoleKey k) => new('\0', k, false, false, false);

    private static EditState Type(string s)
    {
        var st = EditState.Empty;
        foreach (var c in s) (st, _) = LineEditorLogic.Step(st, Ch(c), C, NoHistory);
        return st;
    }

    [Fact]
    public void Typing_builds_buffer_and_moves_cursor()
    {
        var st = Type("say");
        Assert.Equal("say", st.Buffer);
        Assert.Equal(3, st.Cursor);
    }

    [Fact]
    public void Ghost_shows_remainder_of_top_match()
    {
        var st = Type("se");
        Assert.Equal("nd", st.Ghost); // "send" - "se"
    }

    [Fact]
    public void Ghost_empty_when_no_match()
    {
        var st = Type("zzz");
        Assert.Equal("", st.Ghost);
    }

    [Fact]
    public void Ghost_empty_after_space()
    {
        var st = Type("say ");
        Assert.Equal("", st.Ghost);
    }

    [Fact]
    public void Backspace_removes_char_and_recomputes_ghost()
    {
        var st = Type("sh");           // ghost "ell"
        (st, _) = LineEditorLogic.Step(st, K(ConsoleKey.Backspace), C, NoHistory);
        Assert.Equal("s", st.Buffer);
        Assert.Equal(1, st.Cursor);
        // "s" matches say/send/shell/broadcast? broadcast excluded; top alphabetical = "say"
        Assert.Equal("ay", st.Ghost);
    }

    [Fact]
    public void Ghost_empty_when_buffer_emptied_by_backspace()
    {
        var st = Type("s");
        (st, _) = LineEditorLogic.Step(st, K(ConsoleKey.Backspace), C, NoHistory);
        Assert.Equal("", st.Buffer);
        Assert.Equal("", st.Ghost);
    }

    [Fact]
    public void Left_then_insert_places_char_at_cursor()
    {
        var st = Type("sy");
        (st, _) = LineEditorLogic.Step(st, K(ConsoleKey.LeftArrow), C, NoHistory);
        (st, _) = LineEditorLogic.Step(st, Ch('a'), C, NoHistory);
        Assert.Equal("say", st.Buffer);
        Assert.Equal(2, st.Cursor);
    }

    [Fact]
    public void Home_and_End_move_cursor_to_bounds()
    {
        var st = Type("say");
        (st, _) = LineEditorLogic.Step(st, K(ConsoleKey.Home), C, NoHistory);
        Assert.Equal(0, st.Cursor);
        (st, _) = LineEditorLogic.Step(st, K(ConsoleKey.End), C, NoHistory);
        Assert.Equal(3, st.Cursor);
    }

    [Fact]
    public void Enter_submits()
    {
        var st = Type("say");
        var (_, action) = LineEditorLogic.Step(st, K(ConsoleKey.Enter), C, NoHistory);
        Assert.Equal(EditAction.Submit, action);
    }
}
