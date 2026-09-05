using System.Drawing;
using Huddle;
using Xunit;

namespace HuddleTests;

// Layout is pure arithmetic so the awkward cases (one tile, many tiles, a work area
// too small for any of them) are testable without a screen.
public class PeekLayoutTests
{
    private const int Wide = 2560, Tall = 1440;

    [Fact]
    public void One_tile_is_a_single_cell_at_the_preferred_size()
    {
        var g = PeekLayout.Compute(1, Wide, Tall);

        Assert.Equal(1, g.Columns);
        Assert.Equal(1, g.Rows);
        Assert.Equal(1, g.Shown);
        Assert.Equal(0, g.Hidden);
        Assert.Equal(PeekLayout.PreferredTileWidth, g.TileWidth);
    }

    [Fact]
    public void Seven_tiles_form_a_grid_that_holds_all_of_them()
    {
        var g = PeekLayout.Compute(7, Wide, Tall);

        Assert.Equal(7, g.Shown);
        Assert.Equal(0, g.Hidden);
        Assert.True(g.Columns * g.Rows >= 7);
        Assert.True(g.Rows <= g.Columns + 1);
    }

    [Fact]
    public void The_window_never_exceeds_the_usable_work_area()
    {
        foreach (var n in new[] { 1, 4, 7, 12, 30 })
        {
            var g = PeekLayout.Compute(n, Wide, Tall);
            Assert.True(g.WindowWidth <= Wide, $"{n} tiles too wide");
            Assert.True(g.WindowHeight <= Tall, $"{n} tiles too tall");
        }
    }

    [Fact]
    public void Thirty_tiles_fit_a_large_screen_without_overflow()
    {
        var g = PeekLayout.Compute(30, Wide, Tall);

        Assert.Equal(30, g.Shown);
        Assert.Equal(0, g.Hidden);
        Assert.True(g.WindowWidth <= Wide);
        Assert.True(g.WindowHeight <= Tall);
    }

    // A laptop screen is where shrinking actually happens: at 1366x768 the preferred
    // size overflows vertically (5 rows of 224px), and so does the 0.8 step, so the
    // 0.65 step is the one that fits — all 20 tiles, at a smaller size, none dropped.
    [Fact]
    public void A_laptop_screen_shrinks_tiles_rather_than_dropping_them()
    {
        var g = PeekLayout.Compute(20, 1366, 768);

        Assert.Equal(20, g.Shown);
        Assert.Equal(0, g.Hidden);
        Assert.True(g.TileWidth < PeekLayout.PreferredTileWidth);
        Assert.True(g.TileWidth >= PeekLayout.FloorTileWidth);
    }

    // There is deliberately no scrolling. Past the floor the overflow is simply not
    // drawn, and the count that is not drawn is reported rather than hidden.
    [Fact]
    public void Past_the_floor_the_overflow_is_reported_not_scrolled()
    {
        var g = PeekLayout.Compute(200, 640, 480);

        Assert.Equal(PeekLayout.FloorTileWidth, g.TileWidth);
        Assert.Equal(g.Columns * g.Rows, g.Shown);
        Assert.Equal(200 - g.Shown, g.Hidden);
        Assert.True(g.Hidden > 0);
    }

    // The fallback used to shrink rows to fit but leave the column count at the maximum,
    // so with fewer tiles than columns the grid claimed cells that hold nothing and the
    // window was sized for them — breaking Columns * Rows == Shown, which the overflow
    // test above asserts. Reachable only on a work area too short for even a floor tile.
    [Fact]
    public void The_overflow_fallback_never_claims_more_columns_than_there_are_tiles()
    {
        var g = PeekLayout.Compute(2, Wide, 180);

        Assert.Equal(2, g.Shown);
        Assert.Equal(0, g.Hidden);
        Assert.Equal(2, g.Columns);
        Assert.Equal(g.Columns * g.Rows, g.Shown);
    }

    [Fact]
    public void A_zero_count_still_lays_out_one_cell_for_the_placeholder()
    {
        var g = PeekLayout.Compute(0, Wide, Tall);
        Assert.Equal(1, g.Shown);
        Assert.Equal(1, g.Columns);
    }

    [Fact]
    public void Tile_bounds_advance_across_then_down_and_stay_inside_the_window()
    {
        var g = PeekLayout.Compute(4, Wide, Tall);

        var first = PeekLayout.TileBounds(g, 0);
        var second = PeekLayout.TileBounds(g, 1);
        var third = PeekLayout.TileBounds(g, g.Columns);

        Assert.True(second.Left > first.Left);
        Assert.Equal(first.Top, second.Top);
        Assert.True(third.Top > first.Top);

        for (var i = 0; i < g.Shown; i++)
        {
            var r = PeekLayout.TileBounds(g, i);
            Assert.True(r.Right <= g.WindowWidth, $"tile {i} overflows right");
            Assert.True(r.Bottom <= g.WindowHeight, $"tile {i} overflows bottom");
        }
    }
}
