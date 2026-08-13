using System;
using System.Collections.Generic;
using System.Linq;

namespace Huddle;

public enum EditAction { Continue, Submit, Cancel }

public readonly record struct EditState(
    string Buffer, int Cursor, string Ghost, int HistoryIndex, int CycleIndex, string TabPrefix,
    // The live buffer parked while the operator walks history (HistoryIndex >= 0);
    // Down past the newest entry puts it back. Deliberately its own field rather
    // than sharing TabPrefix, so Tab-cycling and history nav cannot corrupt each other.
    string StashedBuffer)
{
    public static EditState Empty { get; } = new("", 0, "", -1, -1, "", "");
}

public static class LineEditorLogic
{
    // Ghost = remainder of the best completion of the *whole current buffer*,
    // falling back to the completer's Hint when nothing is completable. The hint
    // rides the same Ghost channel (painted dim, after the buffer) but is never
    // acceptable: Tab consults Complete directly, which is empty whenever only a
    // hint is showing, so Tab stays a no-op on hints for free.
    private static string ComputeGhost(string buffer, ICompleter completer)
    {
        // An empty buffer has no ghost: Complete("") returns the whole verb list,
        // which would otherwise sprout a suggestion the moment the operator clears
        // the line — and disagree with EditState.Empty, whose Ghost is "".
        if (buffer.Length == 0) return "";
        var matches = completer.Complete(buffer);
        if (matches.Count > 0)
        {
            var top = matches[0];
            return top.Length > buffer.Length ? top.Substring(buffer.Length) : "";
        }
        return completer.Hint(buffer);
    }

    private static EditState WithBuffer(EditState s, string buffer, int cursor, ICompleter completer)
        => s with { Buffer = buffer, Cursor = cursor, Ghost = ComputeGhost(buffer, completer),
                    CycleIndex = -1, TabPrefix = "" }; // any edit cancels an in-progress Tab cycle

    // Start from EditState.Empty — it is the only safe entry point, since
    // default(EditState) leaves Buffer/Ghost/TabPrefix/StashedBuffer null.
    // `history` is ordered most-recent-first: index 0 is the newest command,
    // and HistoryIndex == -1 means "not in history, showing the live buffer".
    public static (EditState, EditAction) Step(
        EditState s, ConsoleKeyInfo key, ICompleter completer, IReadOnlyList<string> history)
    {
        switch (key.Key)
        {
            case ConsoleKey.Enter:
                return (s, EditAction.Submit);

            case ConsoleKey.Backspace:
                if (s.Cursor > 0)
                {
                    var b = s.Buffer.Remove(s.Cursor - 1, 1);
                    return (WithBuffer(s, b, s.Cursor - 1, completer), EditAction.Continue);
                }
                return (s, EditAction.Continue);

            case ConsoleKey.Delete:
                if (s.Cursor < s.Buffer.Length)
                {
                    var b = s.Buffer.Remove(s.Cursor, 1);
                    return (WithBuffer(s, b, s.Cursor, completer), EditAction.Continue);
                }
                return (s, EditAction.Continue);

            case ConsoleKey.LeftArrow:
                return (s with { Cursor = Math.Max(0, s.Cursor - 1) }, EditAction.Continue);

            case ConsoleKey.RightArrow:
                return (s with { Cursor = Math.Min(s.Buffer.Length, s.Cursor + 1) }, EditAction.Continue);

            case ConsoleKey.Home:
                return (s with { Cursor = 0 }, EditAction.Continue);

            case ConsoleKey.End:
                return (s with { Cursor = s.Buffer.Length }, EditAction.Continue);

            // A cycle is anchored on TabPrefix — the buffer as it stood when Tab was
            // first pressed — so repeated Tab walks the same candidate list. Only an
            // edit (via WithBuffer) or history nav cancels it; Home/End/Left/Right are
            // cursor-only and deliberately leave the cycle intact.
            //
            // Accepting a candidate also appends a trailing space: the operator is
            // done with the verb and ready to type arguments. The space also stops
            // the ghost of its own accord (VerbCompleter returns nothing once the
            // line contains a space), so no explicit Ghost suppression is needed.
            // Each cycle step replaces the whole buffer with the next candidate plus
            // that same trailing space.
            case ConsoleKey.Tab:
            {
                if (s.CycleIndex < 0)
                {
                    // An empty prompt stays inert — same rationale as ComputeGhost's
                    // empty-buffer guard: Complete("") returns the whole verb list, so
                    // without this Tab would type the alphabetically-first verb and open
                    // a cycle over the entire catalog.
                    if (s.Buffer.Length == 0) return (s, EditAction.Continue);
                    var cands = completer.Complete(s.Buffer);
                    if (cands.Count == 0) return (s, EditAction.Continue);
                    var accepted = cands[0] + " ";
                    return (s with { Buffer = accepted, Cursor = accepted.Length,
                                     Ghost = "", CycleIndex = 0, TabPrefix = s.Buffer },
                            EditAction.Continue);
                }
                else
                {
                    var cands = completer.Complete(s.TabPrefix);
                    if (cands.Count == 0) return (s, EditAction.Continue);
                    var next = (s.CycleIndex + 1) % cands.Count;
                    var cycled = cands[next] + " ";
                    return (s with { Buffer = cycled, Cursor = cycled.Length,
                                     Ghost = "", CycleIndex = next },
                            EditAction.Continue);
                }
            }

            case ConsoleKey.UpArrow:
            {
                if (history.Count == 0) return (s, EditAction.Continue);
                var idx = s.HistoryIndex;
                var stash = idx < 0 ? s.Buffer : s.StashedBuffer; // entering history: stash live buffer
                var newIdx = Math.Min(idx + 1, history.Count - 1);
                var recalled = history[newIdx];
                return (s with { Buffer = recalled, Cursor = recalled.Length, Ghost = "",
                                 HistoryIndex = newIdx, StashedBuffer = stash,
                                 CycleIndex = -1, TabPrefix = "" }, EditAction.Continue);
            }

            case ConsoleKey.DownArrow:
            {
                if (s.HistoryIndex < 0) return (s, EditAction.Continue);
                var newIdx = s.HistoryIndex - 1;
                if (newIdx < 0)
                {
                    // back to the live buffer
                    return (s with { Buffer = s.StashedBuffer, Cursor = s.StashedBuffer.Length,
                                     Ghost = ComputeGhost(s.StashedBuffer, completer),
                                     HistoryIndex = -1, StashedBuffer = "",
                                     CycleIndex = -1, TabPrefix = "" }, EditAction.Continue);
                }
                var recalled = history[newIdx];
                return (s with { Buffer = recalled, Cursor = recalled.Length, Ghost = "",
                                 HistoryIndex = newIdx, CycleIndex = -1, TabPrefix = "" },
                        EditAction.Continue);
            }

            default:
                if (!char.IsControl(key.KeyChar) && key.KeyChar != '\0')
                {
                    var b = s.Buffer.Insert(s.Cursor, key.KeyChar.ToString());
                    return (WithBuffer(s, b, s.Cursor + 1, completer), EditAction.Continue);
                }
                return (s, EditAction.Continue);
        }
    }
}

// Pure single-row viewport math for the renderer: given the row width and the
// line's parts, decide which slice of the buffer is visible, how much ghost
// fits after it, and where the caret lands. The renderer never paints past
// width-1 (the final column would wrap to the next row, and wrapping is what
// desynced the row anchor — operator-reported "weird input line", 2026-08-13).
// Long input horizontally scrolls instead: the window slides to keep the caret
// visible, cmd.exe-style.
public readonly record struct LineViewport(int Start, int Take, int GhostTake, int CaretCol)
{
    public static LineViewport Compute(int promptLen, int width, int bufferLen, int ghostLen, int cursor)
    {
        var row = Math.Max(0, width - 1);            // usable cells on the row
        var avail = Math.Max(0, row - promptLen);    // cells after the prompt
        // Slide the window only when the caret would fall off the right edge.
        var start = cursor > avail ? cursor - avail : 0;
        var take = Math.Min(bufferLen - start, avail);
        var ghostTake = Math.Max(0, Math.Min(ghostLen, avail - take));
        var caretCol = Math.Min(promptLen + (cursor - start), Math.Max(0, row));
        return new LineViewport(start, Math.Max(0, take), ghostTake, Math.Max(0, caretCol));
    }
}

// The interactive half: owns the console (keys in, pixels out) and drives the
// pure LineEditorLogic.Step state machine. Everything decision-shaped lives in
// Step and is unit-tested; this class is deliberately dumb so that the only
// untestable code is "read a key, paint a line".
public sealed class LineEditor
{
    private readonly ICompleter _completer;
    private readonly int _cap;
    private readonly List<string> _history = new(); // index 0 = most recent, as Step expects

    public LineEditor(ICompleter completer, int historyCapacity = 200)
    {
        _completer = completer;
        _cap = historyCapacity;
    }

    // Returns the submitted line, or null if `cancelled` went true (Ctrl+C) while
    // waiting for a key. Callers must not use this when stdin is redirected —
    // Console.KeyAvailable/ReadKey have no meaning there; fall back to Console.ReadLine.
    public string? ReadLine(string prompt, Func<bool> cancelled)
    {
        var s = EditState.Empty;
        _prevPaint = 0; _prevRow = -1; // fresh row: nothing of ours on it yet
        Render(prompt, s);

        while (true)
        {
            // Poll instead of blocking in ReadKey: the existing Ctrl+C handler only
            // flips a flag (TreatControlCAsInput stays off), so a blocking ReadKey
            // would sit there until the operator also pressed a key.
            while (!Console.KeyAvailable)
            {
                if (cancelled()) { Console.WriteLine(); return null; }
                System.Threading.Thread.Sleep(15);
            }

            var key = Console.ReadKey(intercept: true);
            var (next, action) = LineEditorLogic.Step(s, key, _completer, _history);

            if (action == EditAction.Submit)
            {
                s = next;
                // Repaint without the ghost first: the suggestion is not part of the
                // committed line and must not be left on screen above the output.
                Render(prompt, s with { Ghost = "" });
                Console.WriteLine();
                Push(s.Buffer);
                return s.Buffer;
            }

            // Nothing produces Cancel today (cancellation arrives via the poll above),
            // but honouring it here keeps the contract honest if Step ever grows an
            // Esc/Ctrl+D branch.
            if (action == EditAction.Cancel) { Console.WriteLine(); return null; }

            s = next;
            Render(prompt, s);
        }
    }

    private void Push(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        _history.RemoveAll(h => h == line); // re-running a command promotes it, not duplicates it
        _history.Insert(0, line);
        while (_history.Count > _cap) _history.RemoveAt(_history.Count - 1);
    }

    // Cells this editor painted on the current row last time (prompt + buffer +
    // ghost). Lets Render erase only the shrunken tail instead of blanking the
    // whole row first — the blank-then-repaint made the line visibly flash and
    // the caret teleport on every keystroke (operator-reported, 2026-08-13).
    // _prevRow guards the arithmetic: an async log line or a scroll moves the
    // edit to a different row, where "what we painted last time" is meaningless —
    // detect the move and start the new row from zero.
    private int _prevPaint;
    private int _prevRow = -1;

    // Full repaint of the line's CONTENT each keystroke (Tab and history nav can
    // shorten the buffer), but no whole-row blank: overwrite in place, erase only
    // the tail the previous paint left behind, and hide the caret while painting
    // so it doesn't visibly teleport to column 0 and back.
    //
    // Single-row invariant: nothing is ever written past column width-2, so the
    // paint can never wrap and the row anchor (Console.CursorTop) stays valid.
    // Input longer than the row horizontally scrolls via LineViewport.
    private void Render(string prompt, EditState s)
    {
        try
        {
            Console.CursorVisible = false;

            int row = Console.CursorTop;
            int width = Console.WindowWidth;
            if (row != _prevRow) { _prevPaint = 0; _prevRow = row; }

            var v = LineViewport.Compute(prompt.Length, width, s.Buffer.Length, s.Ghost.Length, s.Cursor);

            Console.SetCursorPosition(0, row);
            Console.ForegroundColor = ConsoleColor.Cyan; // matches ConsoleUI.PrintPrompt
            Console.Write(prompt);
            Console.ResetColor();
            Console.Write(s.Buffer.Substring(v.Start, v.Take));

            if (v.GhostTake > 0)
            {
                // The ghost belongs at the END of the buffer, not at the caret: it is
                // only recomputed on edits, so on a cursor-only move (Left/Home/...) a
                // caret-anchored ghost would show a completion of text it no longer follows.
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write(s.Ghost.Substring(0, v.GhostTake));
                Console.ResetColor();
            }

            // Erase only what the previous paint wrote beyond this one.
            var painted = prompt.Length + v.Take + v.GhostTake;
            var stale = Math.Min(_prevPaint, Math.Max(0, width - 1)) - painted;
            if (stale > 0) Console.Write(new string(' ', stale));
            _prevPaint = painted;

            Console.SetCursorPosition(v.CaretCol, row);
        }
        catch (System.IO.IOException) { /* not a real console (redirected) */ }
        catch (ArgumentOutOfRangeException) { /* window too small / resized mid-draw */ }
        finally
        {
            // A throw between ForegroundColor and ResetColor would otherwise leave
            // the whole console cyan or grey — same guard ConsoleUI uses. Caret
            // visibility is restored the same way, or a mid-render throw would
            // leave it hidden.
            Console.ResetColor();
            try { Console.CursorVisible = true; } catch (System.IO.IOException) { }
        }
    }
}
