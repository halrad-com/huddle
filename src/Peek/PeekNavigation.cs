namespace Huddle;

public enum PeekKey { Left, Right, Up, Down, Next, Previous, First, Last }

/// <summary>
/// Selection movement over the tile grid.
///
/// <para>Two invariants, both borrowed from corelib's View Switcher. Selection never
/// rests on a tile that cannot be committed, so a walk skips over unselectable tiles
/// rather than treating them as stops. And movement is total: with nothing selectable
/// every key is inert rather than throwing, because the empty-state placeholder is
/// itself an unselectable tile and the operator will press keys at it.</para>
/// </summary>
public static class PeekNavigation
{
    public static int Move(IReadOnlyList<PeekTile> tiles, int columns, int current, PeekKey key)
    {
        if (tiles.Count == 0) return current;
        if (columns < 1) columns = 1;
        if (!tiles.Any(t => t.Selectable)) return current;

        return key switch
        {
            PeekKey.Right or PeekKey.Next => Scan(tiles, current, +1),
            PeekKey.Left or PeekKey.Previous => Scan(tiles, current, -1),
            PeekKey.Down => Vertical(tiles, columns, current, +1),
            PeekKey.Up => Vertical(tiles, columns, current, -1),
            PeekKey.First => Scan(tiles, tiles.Count - 1, +1),
            PeekKey.Last => Scan(tiles, 0, -1),
            _ => current,
        };
    }

    /// <summary>Step through the flat order, wrapping, until a selectable tile is
    /// found. Bounded by the list length, so an all-unselectable list terminates.</summary>
    private static int Scan(IReadOnlyList<PeekTile> tiles, int from, int delta)
    {
        var i = from;
        for (var guard = 0; guard < tiles.Count; guard++)
        {
            i += delta;
            if (i < 0) i = tiles.Count - 1;
            if (i >= tiles.Count) i = 0;
            if (tiles[i].Selectable) return i;
        }
        return from;
    }

    /// <summary>Move one row within the current column, wrapping top to bottom. The
    /// last row is usually ragged, so wrapping targets the lowest tile that actually
    /// exists in this column rather than a cell that is not there.
    ///
    /// <para>When the cell one row away is unselectable, the fallback scan continues in
    /// the direction of travel. It must: <paramref name="current"/> is normally itself
    /// selectable, so scanning forward from a target above it would walk straight back
    /// down into it and make Up a dead key whenever the tile above cannot be
    /// committed.</para></summary>
    private static int Vertical(IReadOnlyList<PeekTile> tiles, int columns, int current, int delta)
    {
        var column = current % columns;
        var target = current + delta * columns;

        if (target < 0 || target >= tiles.Count)
        {
            target = delta > 0
                ? column                                    // wrapped downward: top of the column
                : LowestInColumn(tiles.Count, columns, column);
        }

        if (target < 0 || target >= tiles.Count) return current;
        return tiles[target].Selectable ? target : Scan(tiles, target, delta > 0 ? +1 : -1);
    }

    private static int LowestInColumn(int count, int columns, int column)
    {
        var i = column;
        while (i + columns < count) i += columns;
        return i;
    }
}
