using Huddle;
using Xunit;

namespace HuddleTests;

// Selection walks a grid whose last row is usually ragged, and it must never land on
// a tile Enter would do nothing with.
public class PeekNavigationTests
{
    private static List<PeekTile> Tiles(params bool[] selectable)
    {
        var list = new List<PeekTile>();
        for (var i = 0; i < selectable.Length; i++)
            list.Add(new PeekTile($"s{i}", selectable[i] ? new IntPtr(i + 1) : IntPtr.Zero,
                                  selectable[i], $"s{i}", "", null));
        return list;
    }

    private static List<PeekTile> AllSelectable(int n) =>
        Tiles(Enumerable.Repeat(true, n).ToArray());

    [Fact]
    public void Right_advances_and_wraps_at_the_end()
    {
        var t = AllSelectable(5);
        Assert.Equal(1, PeekNavigation.Move(t, 3, 0, PeekKey.Right));
        Assert.Equal(0, PeekNavigation.Move(t, 3, 4, PeekKey.Right));
    }

    [Fact]
    public void Left_retreats_and_wraps_at_the_start()
    {
        var t = AllSelectable(5);
        Assert.Equal(4, PeekNavigation.Move(t, 3, 0, PeekKey.Left));
    }

    [Fact]
    public void Down_moves_a_row_and_wraps_within_the_column_on_a_ragged_last_row()
    {
        // 5 tiles, 3 columns:  [0 1 2]
        //                      [3 4  ]
        var t = AllSelectable(5);

        Assert.Equal(3, PeekNavigation.Move(t, 3, 0, PeekKey.Down));

        // Column 2 has no tile in the last row, so Down wraps back to its top.
        Assert.Equal(2, PeekNavigation.Move(t, 3, 2, PeekKey.Down));
    }

    [Fact]
    public void Up_moves_a_row_and_wraps_to_the_lowest_tile_in_that_column()
    {
        var t = AllSelectable(5);
        Assert.Equal(0, PeekNavigation.Move(t, 3, 3, PeekKey.Up));
        Assert.Equal(4, PeekNavigation.Move(t, 3, 1, PeekKey.Up));
    }

    // The one line no test reached: a vertical move whose target is unselectable. Up
    // must keep going UP — a forward scan from the target would walk back down into
    // `current` and make the key inert, because `current` is itself selectable.
    [Fact]
    public void Up_past_an_unselectable_target_keeps_going_up()
    {
        var t = Tiles(true, false, false, false, false, false, true, false, false);
        Assert.Equal(0, PeekNavigation.Move(t, 3, 6, PeekKey.Up));
    }

    // The counterpart, so the fix above cannot be "corrected" by flipping both
    // directions: Down past an unselectable target keeps going DOWN.
    [Fact]
    public void Down_past_an_unselectable_target_keeps_going_down()
    {
        var t = Tiles(true, false, false, false, false, false, true, false, false);
        Assert.Equal(6, PeekNavigation.Move(t, 3, 0, PeekKey.Down));
    }

    [Fact]
    public void Tab_and_shift_tab_walk_the_flat_order()
    {
        var t = AllSelectable(4);
        Assert.Equal(1, PeekNavigation.Move(t, 2, 0, PeekKey.Next));
        Assert.Equal(3, PeekNavigation.Move(t, 2, 0, PeekKey.Previous));
    }

    [Fact]
    public void Movement_skips_tiles_that_cannot_be_committed()
    {
        var t = Tiles(true, false, true);
        Assert.Equal(2, PeekNavigation.Move(t, 3, 0, PeekKey.Right));
    }

    [Fact]
    public void First_and_last_land_on_selectable_tiles()
    {
        var t = Tiles(false, true, true, false);
        Assert.Equal(1, PeekNavigation.Move(t, 2, 3, PeekKey.First));
        Assert.Equal(2, PeekNavigation.Move(t, 2, 0, PeekKey.Last));
    }

    // The empty-state placeholder is the whole list. Every key must be inert rather
    // than throwing or selecting something Enter cannot act on.
    [Fact]
    public void With_nothing_selectable_every_key_stays_put()
    {
        var t = Tiles(false, false);
        foreach (PeekKey k in Enum.GetValues<PeekKey>())
            Assert.Equal(0, PeekNavigation.Move(t, 2, 0, k));
    }

    [Fact]
    public void An_empty_list_never_throws()
    {
        Assert.Equal(0, PeekNavigation.Move(new List<PeekTile>(), 3, 0, PeekKey.Right));
    }
}
