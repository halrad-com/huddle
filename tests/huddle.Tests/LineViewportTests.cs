using Xunit;
using Huddle;

namespace HuddleTests;

// Pure viewport math for the single-row renderer: the painted slice must never
// exceed the row, and the caret must always be inside the visible window.
public class LineViewportTests
{
    [Fact]
    public void Short_line_shows_everything()
    {
        var v = LineViewport.Compute(promptLen: 2, width: 80, bufferLen: 10, ghostLen: 5, cursor: 10);
        Assert.Equal(0, v.Start);
        Assert.Equal(10, v.Take);
        Assert.Equal(5, v.GhostTake);
        Assert.Equal(12, v.CaretCol); // prompt + cursor
    }

    [Fact]
    public void Long_buffer_scrolls_window_to_keep_caret_visible()
    {
        // width 40, prompt 2 -> 37 usable cells (width-1 cap). Buffer 100, caret at end.
        var v = LineViewport.Compute(2, 40, 100, 0, 100);
        Assert.True(v.Start > 0);
        Assert.True(v.CaretCol < 40);             // caret inside the row (position-only, no write)
        Assert.Equal(100, v.Start + v.Take);      // window ends at the caret
        Assert.Equal(0, v.GhostTake);
    }

    [Fact]
    public void Caret_mid_buffer_stays_visible_after_home()
    {
        var v = LineViewport.Compute(2, 40, 100, 0, 0); // Home on a long line
        Assert.Equal(0, v.Start);                 // window snaps back to the front
        Assert.Equal(2, v.CaretCol);
    }

    [Fact]
    public void Ghost_is_clipped_to_the_remaining_row()
    {
        // 2 + 30 buffer leaves 5 cells of a 37-cell row for a 20-char ghost.
        var v = LineViewport.Compute(2, 40, 30, 20, 30);
        Assert.Equal(30, v.Take);
        Assert.Equal(37 - 30, v.GhostTake);
    }

    [Fact]
    public void Ghost_fully_hidden_when_buffer_fills_the_row()
    {
        var v = LineViewport.Compute(2, 40, 60, 10, 60);
        Assert.Equal(0, v.GhostTake);
    }

    [Fact]
    public void Total_paint_never_exceeds_width_minus_one()
    {
        foreach (var (w, b, g, c) in new[] { (20, 5, 30, 5), (40, 100, 10, 50), (30, 0, 100, 0), (10, 3, 3, 1) })
        {
            var v = LineViewport.Compute(2, w, b, g, c);
            Assert.True(2 + v.Take + v.GhostTake <= w - 1);
            Assert.InRange(v.CaretCol, 0, w - 1);
        }
    }

    [Fact]
    public void Tiny_window_degrades_without_negative_numbers()
    {
        var v = LineViewport.Compute(2, 3, 10, 5, 10);
        Assert.True(v.Take >= 0);
        Assert.True(v.GhostTake >= 0);
        Assert.InRange(v.CaretCol, 0, 2);
    }
}
