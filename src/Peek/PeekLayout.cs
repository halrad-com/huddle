using System.Drawing;

namespace Huddle;

/// <summary>Geometry of one overlay: the grid, the tile size chosen for it, and how
/// many tiles could not be drawn.</summary>
public readonly record struct PeekGrid(
    int Columns, int Rows,
    int TileWidth, int TileHeight,
    int Shown, int Hidden,
    int WindowWidth, int WindowHeight);

/// <summary>
/// Turns a tile count and a work area into a grid.
///
/// <para>There is deliberately no scrolling. At the floor tile size a routine work
/// area holds far more tiles than any plausible session count, so a scroll model
/// would be machinery for a case that does not occur; past the floor the overflow is
/// left undrawn and counted, which is visible rather than silent.</para>
/// </summary>
public static class PeekLayout
{
    public const int PreferredTileWidth = 320;
    public const int PreferredTileHeight = 180;   // 16:9, the shape of a console window
    public const int FloorTileWidth = 160;
    public const int FloorTileHeight = 90;
    public const int Gap = 12;
    public const int Padding = 16;
    public const int HeaderHeight = 26;           // "6 sessions" plus the hint line
    // Three text lines plus breathing room. It was 44, which is almost exactly the sum of
    // the three fonts' heights, so the lines had zero leading and visibly collided: the
    // metadata line sat on the name above it and the signal line was pushed past the
    // bottom of the band. PeekWindow now advances by each font's measured height and this
    // is sized to hold that with a few pixels to spare.
    public const int LabelHeight = 56;

    // Discrete steps rather than a continuous solve: fewer states, all testable, and
    // the visual result is predictable across session counts.
    private static readonly double[] Scales = { 1.0, 0.8, 0.65, 0.5 };

    public static PeekGrid Compute(int tileCount, int workAreaWidth, int workAreaHeight)
    {
        var n = Math.Max(1, tileCount);

        foreach (var scale in Scales)
        {
            var tw = Math.Max(FloorTileWidth, (int)(PreferredTileWidth * scale));
            var th = Math.Max(FloorTileHeight, (int)(PreferredTileHeight * scale));

            var cols = Math.Max(1, Math.Min(MaxColumns(tw, workAreaWidth), (int)Math.Ceiling(Math.Sqrt(n))));
            var rows = (int)Math.Ceiling(n / (double)cols);

            var w = WindowWidth(cols, tw);
            var h = WindowHeight(rows, th);

            if (w <= workAreaWidth && h <= workAreaHeight)
                return new PeekGrid(cols, rows, tw, th, n, 0, w, h);
        }

        // Floor reached and still too big: fill what fits and report the remainder.
        var fCols = Math.Max(1, MaxColumns(FloorTileWidth, workAreaWidth));
        var fRows = Math.Max(1, MaxRows(FloorTileHeight, workAreaHeight));
        var capacity = fCols * fRows;
        var shown = Math.Min(n, capacity);

        // Do not draw empty columns or rows when the overflow is small. Without the column
        // clamp, a work area that fits five columns but only shows three tiles still claims
        // five, and the window is sized for two cells that hold nothing — breaking the
        // Columns * Rows == Shown invariant this layout's own tests assert. Only reachable
        // on a work area under roughly 192px tall, but the invariant should hold because it
        // is enforced, not because the case is rare.
        fCols = Math.Min(fCols, shown);
        fRows = (int)Math.Ceiling(shown / (double)fCols);

        return new PeekGrid(
            fCols, fRows, FloorTileWidth, FloorTileHeight,
            shown, n - shown,
            WindowWidth(fCols, FloorTileWidth), WindowHeight(fRows, FloorTileHeight));
    }

    /// <summary>Thumbnail rectangle of one tile, relative to the overlay client area.
    /// The label band sits directly beneath it and is not part of this rectangle.</summary>
    public static Rectangle TileBounds(PeekGrid grid, int index)
    {
        var col = index % grid.Columns;
        var row = index / grid.Columns;

        return new Rectangle(
            Padding + col * (grid.TileWidth + Gap),
            Padding + HeaderHeight + row * (grid.TileHeight + LabelHeight + Gap),
            grid.TileWidth,
            grid.TileHeight);
    }

    private static int MaxColumns(int tileWidth, int workAreaWidth) =>
        Math.Max(1, (workAreaWidth - 2 * Padding + Gap) / (tileWidth + Gap));

    private static int MaxRows(int tileHeight, int workAreaHeight) =>
        Math.Max(1, (workAreaHeight - 2 * Padding - HeaderHeight + Gap) / (tileHeight + LabelHeight + Gap));

    private static int WindowWidth(int cols, int tileWidth) =>
        2 * Padding + cols * tileWidth + (cols - 1) * Gap;

    private static int WindowHeight(int rows, int tileHeight) =>
        2 * Padding + HeaderHeight + rows * (tileHeight + LabelHeight) + (rows - 1) * Gap;
}
