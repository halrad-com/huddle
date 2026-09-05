using System.Drawing;
using System.Windows.Forms;

namespace Huddle;

/// <summary>
/// The overlay: a borderless, top-most grid of live session thumbnails.
///
/// <para>Interaction contract, taken from corelib's View Switcher because its comments
/// record the bugs it already paid for. Selection is state, not a hover effect, so
/// the keyboard drives and the mouse agrees rather than two highlights disagreeing.
/// Esc cancels without acting. Enter is the only thing that changes the screen.
/// Deactivation dismisses, so clicking away is a cancel and never leaves the overlay
/// stranded on screen.</para>
///
/// <para><see cref="ShowAndPick"/> uses <see cref="Form.ShowDialog()"/>, which needs an
/// STA thread; huddle's console thread is MTA. Supplying that thread is deliberately the
/// caller's job (<c>PeekController</c>) rather than this window's, so the window has one
/// responsibility and the threading decision lives with the code that owns the summon.</para>
/// </summary>
public sealed class PeekWindow : Form
{
    private readonly IReadOnlyList<PeekTile> _tiles;
    private readonly PeekGrid _grid;
    private readonly Action<string> _log;
    private ThumbnailHost? _thumbs;
    private int _selected;
    private IntPtr? _picked;

    private static readonly Color Ground = Color.FromArgb(20, 24, 29);
    private static readonly Color Edge = Color.FromArgb(88, 166, 255);
    // "Ink" rather than "Text": Form.Text is the window caption, and a palette entry that
    // hides it would read as a caption everywhere it is used.
    private static readonly Color Ink = Color.FromArgb(230, 232, 234);
    private static readonly Color Muted = Color.FromArgb(139, 145, 152);
    private static readonly Color Alarm = Color.FromArgb(240, 110, 110);

    public PeekWindow(IReadOnlyList<PeekTile> tiles, Action<string>? log = null)
    {
        _tiles = tiles;
        // Without a logger the thumbnail host's diagnostics — a refused registration, a
        // failed release, which is the leak it exists to prevent — go nowhere, so the
        // overlay forwards the operator's log rather than letting them fall on the floor.
        _log = log ?? (_ => { });

        var work = Screen.FromPoint(Cursor.Position).WorkingArea;
        _grid = PeekLayout.Compute(tiles.Count, work.Width, work.Height);
        _selected = Math.Max(0, PeekModel.DefaultSelection(tiles));

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Ground;
        DoubleBuffered = true;
        KeyPreview = true;
        ClientSize = new Size(_grid.WindowWidth, _grid.WindowHeight);
        Location = new Point(
            work.Left + (work.Width - _grid.WindowWidth) / 2,
            work.Top + (work.Height - _grid.WindowHeight) / 2);
    }

    /// <summary>Show the overlay and block until the operator picks or dismisses.
    /// Returns the chosen session's window handle, or null on cancel.</summary>
    public IntPtr? ShowAndPick()
    {
        ShowDialog();
        return _picked;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // Registered after the handle exists, and torn down in OnFormClosed. A summon
        // must never leave a thumbnail registered.
        _thumbs = new ThumbnailHost(Handle, log: _log);
        for (var i = 0; i < _grid.Shown && i < _tiles.Count; i++)
            if (_tiles[i].WindowHandle != IntPtr.Zero)
                _thumbs.Add(_tiles[i].WindowHandle, PeekLayout.TileBounds(_grid, i));

        Activate();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _thumbs?.Dispose();
        _thumbs = null;
        base.OnFormClosed(e);
    }

    /// <summary>The other release path, and the only one on the throw.
    ///
    /// <para><see cref="OnFormClosed"/> covers Esc, Enter and click-away. It does NOT cover
    /// a throw out of the message loop: PeekController sets
    /// <c>UnhandledExceptionMode.ThrowException</c> so such a throw unwinds out of ShowDialog
    /// instead of landing in a hidden dialog, and FormClosed is raised from Close/WM_CLOSE,
    /// never from Dispose. Without this override the caller's `using` would dispose the form
    /// with DWM registrations still held, which is the one path that could falsify
    /// ThumbnailHost's "verified empty on hide". Double release is not a risk: Clear empties
    /// the handle list, and _thumbs is nulled either way.</para></summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _thumbs?.Dispose();
            _thumbs = null;
        }
        base.Dispose(disposing);
    }

    // Clicking away is a cancel. Without this the overlay can be left behind a window
    // it is meant to be raising.
    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (Visible) Close();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Arrows and Tab are dialog navigation keys and never reach OnKeyDown, so they
        // are handled here or they are silently swallowed.
        switch (keyData)
        {
            case Keys.Left: return MoveSelection(PeekKey.Left);
            case Keys.Right: return MoveSelection(PeekKey.Right);
            case Keys.Up: return MoveSelection(PeekKey.Up);
            case Keys.Down: return MoveSelection(PeekKey.Down);
            case Keys.Tab: return MoveSelection(PeekKey.Next);
            case Keys.Shift | Keys.Tab: return MoveSelection(PeekKey.Previous);
            case Keys.Home: return MoveSelection(PeekKey.First);
            case Keys.End: return MoveSelection(PeekKey.Last);
            case Keys.Enter: Commit(); return true;
            case Keys.Escape: Close(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // "MoveSelection" rather than "Move": Control.Move is an event, and a method hiding it
    // would silently shadow something every WinForms reader expects to be an event.
    private bool MoveSelection(PeekKey key)
    {
        var next = PeekNavigation.Move(_tiles, _grid.Columns, _selected, key);
        if (next != _selected) { _selected = next; Invalidate(); }
        return true;
    }

    // The mouse agrees with the keyboard rather than competing with it: hovering sets
    // the selection, so there is only ever one highlight on screen.
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitTest(e.Location);
        if (hit >= 0 && hit != _selected && _tiles[hit].Selectable)
        {
            _selected = hit;
            Invalidate();
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        // Left button only. Right-click is not an activation gesture and there is no context
        // menu here, so treating it as a commit would dismiss the overlay and switch windows
        // for an operator who asked for neither.
        if (e.Button != MouseButtons.Left) return;

        var hit = HitTest(e.Location);
        if (hit >= 0 && _tiles[hit].Selectable) { _selected = hit; Commit(); }
    }

    /// <summary>Index of the tile under <paramref name="p"/>, or -1.
    ///
    /// <para>The rectangle is the thumbnail plus the label band drawn beneath it — exactly
    /// the area that belongs to this tile — so the only dead space left is the real gap
    /// between rows. It deliberately grows downward only. A symmetric inflate of half the
    /// label band reached ten pixels into the row below, and those ten pixels are where the
    /// Note line is drawn: clicking a session's own "[!] API" alarm raised a different
    /// session, and hovering it moved the highlight away from the tile the cursor was over.
    /// Upward it reached into the header. Both broke the contract this window is built on.</para></summary>
    private int HitTest(Point p)
    {
        for (var i = 0; i < _grid.Shown && i < _tiles.Count; i++)
        {
            var r = PeekLayout.TileBounds(_grid, i);
            r.Height += PeekLayout.LabelHeight;
            if (r.Contains(p)) return i;
        }
        return -1;
    }

    private void Commit()
    {
        if (_selected < 0 || _selected >= _tiles.Count) return;
        if (!_tiles[_selected].Selectable) return;   // Enter must do something or do nothing at all

        _picked = _tiles[_selected].WindowHandle;
        Close();
    }

    /// <summary>One label line, confined to its tile's width and ellipsised rather than
    /// allowed to run into the neighbour. Single line: a wrapped label would push into
    /// the row below, which is the same collision in the other axis.</summary>
    private static void DrawClipped(
        Graphics g, string text, Font font, Brush brush, float x, float y, float width)
    {
        if (string.IsNullOrEmpty(text)) return;

        using var format = new StringFormat(StringFormatFlags.NoWrap)
        {
            Trimming = StringTrimming.EllipsisCharacter,
        };
        g.DrawString(text, font, brush, new RectangleF(x, y, width, font.Height + 2f), format);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;

        using var border = new Pen(Edge, 1);
        g.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);

        using var head = new Font(Font.FontFamily, 8.5f);
        using var name = new Font(Font.FontFamily, 9f, FontStyle.Bold);
        using var small = new Font(Font.FontFamily, 8f);
        using var textBrush = new SolidBrush(Ink);
        using var mutedBrush = new SolidBrush(Muted);
        using var alarmBrush = new SolidBrush(Alarm);

        // Counted rather than taken from _grid.Shown: the grid counts cells, and a cell is
        // not always a session. huddle's own tile is not one, and the empty overlay's
        // placeholder is not one either, which is how the header used to read "1 sessions"
        // above a tile saying there were none.
        var phrase = PeekModel.CountPhrase(PeekModel.SessionCount(_tiles, _grid.Shown));
        var header = _grid.Hidden > 0
            ? $"{phrase}  ({_grid.Hidden} not shown)   arrows or Tab, Enter to switch, Esc to cancel"
            : $"{phrase}   arrows or Tab, Enter to switch, Esc to cancel";
        DrawClipped(g, header, head, mutedBrush,
            PeekLayout.Padding, PeekLayout.Padding / 2f, ClientSize.Width - 2f * PeekLayout.Padding);

        for (var i = 0; i < _grid.Shown && i < _tiles.Count; i++)
        {
            var tile = _tiles[i];
            var r = PeekLayout.TileBounds(_grid, i);

            // No thumbnail arrives for a session with no window, so fill the area to
            // make the tile read as a tile rather than a hole in the overlay.
            if (tile.WindowHandle == IntPtr.Zero)
            {
                using var empty = new SolidBrush(Color.FromArgb(30, 35, 42));
                g.FillRectangle(empty, r);
            }

            if (i == _selected && tile.Selectable)
            {
                using var sel = new Pen(Edge, 2);
                var outline = r;
                outline.Inflate(3, 3);
                g.DrawRectangle(sel, outline);
            }

            // Every label is clipped to the tile it belongs to. Drawing at a point instead
            // let a long line run straight across the gap into the next tile's labels, so
            // two sessions' metadata appeared concatenated with no separator: unreadable,
            // and it read as the wrong data against the wrong thumbnail. The title made it
            // obvious, but any long instance id or project would have done it.
            // Advance by what the font actually measures, not by a guessed constant. The
            // old 15 and 13 were within a pixel of the fonts' own heights, so consecutive
            // lines had no leading at all and overlapped on screen.
            var y = r.Bottom + 3f;
            DrawClipped(g, tile.Line1, name, tile.Selectable ? textBrush : mutedBrush, r.Left, y, r.Width);
            y += name.Height + 1f;
            DrawClipped(g, tile.Line2, small, mutedBrush, r.Left, y, r.Width);
            y += small.Height + 1f;
            if (tile.Note != null)
                DrawClipped(g, tile.Note, small,
                    tile.Note.StartsWith("[!]", StringComparison.Ordinal) ? alarmBrush : mutedBrush,
                    r.Left, y, r.Width);
        }
    }
}
