namespace Huddle;

/// <summary>
/// Turns a session snapshot into the ordered tiles the overlay draws.
///
/// <para>Carries rule 4 of the interaction contract borrowed from corelib's View
/// Switcher: a tile is selectable only if committing it would do something. Their
/// placeholder was selectable and therefore the default selection, so the first use
/// of the feature was press hotkey, press Enter, nothing happens. A session whose
/// window huddle cannot identify is in exactly that position here.</para>
/// </summary>
public static class PeekModel
{
    /// <summary>Idle is noise below this; the status verb uses the same threshold.</summary>
    public const int IdleThresholdMinutes = 3;

    /// <summary>Instance id of huddle's own console tile (spec section 9: "Huddle's own
    /// console is a tile. You need the way back."). It is not a session, so it carries
    /// no project and none of the session signals, and anything counting sessions has
    /// to be able to tell it apart from one.</summary>
    public const string SelfId = "huddle";

    /// <summary>A window title fit to sit on a tile: no control characters, no runaway
    /// length. Claude Code writes the conversation topic here, so it is usually short,
    /// but a pasted line can make it enormous and the tile is 320px wide.</summary>
    private static string Clean(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var t = title.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return t.Length > MaxTitle ? t[..MaxTitle].TrimEnd() + "…" : t;
    }

    private const int MaxTitle = 34;

    public static List<PeekTile> Build(IEnumerable<PeekSource> sources)
    {
        var tiles = new List<PeekTile>();

        foreach (var s in sources)
        {
            var selectable = s.WindowHandle != IntPtr.Zero;
            var line2 = string.IsNullOrWhiteSpace(s.Project) ? s.Uptime : $"[{s.Project}]  {s.Uptime}";

            // The console title is what Claude Code renames the window to: the topic the
            // session is actually on. Identity (repo:persona) tells you which agent, this
            // tells you what it is doing, which is the thing you are usually picking by.
            // Appended rather than given its own line because the tile has three and the
            // third belongs to the trouble signals.
            var title = Clean(s.Title);
            if (title.Length > 0) line2 = line2.Length > 0 ? $"{line2}  {title}" : title;

            tiles.Add(new PeekTile(
                s.InstanceId,
                s.WindowHandle,
                selectable,
                s.InstanceId,
                line2,
                Note(s, selectable)));
        }

        if (tiles.Count == 0)
            tiles.Add(new PeekTile("", IntPtr.Zero, false, "No running sessions", "", null));

        return tiles;
    }

    /// <summary>
    /// How many of the first <paramref name="limit"/> tiles are actual sessions, for the
    /// overlay header.
    ///
    /// <para>Neither the "No running sessions" placeholder (no instance id) nor huddle's
    /// own console counts. A header that counted either contradicted the tiles under it:
    /// the empty overlay drew a placeholder, PeekLayout clamps the grid to at least one
    /// cell, and the header read "1 sessions" above a tile saying there were none.</para>
    /// </summary>
    public static int SessionCount(IReadOnlyList<PeekTile> tiles, int limit)
    {
        var n = 0;
        for (var i = 0; i < limit && i < tiles.Count; i++)
            if (tiles[i].InstanceId.Length > 0 && tiles[i].InstanceId != SelfId) n++;
        return n;
    }

    /// <summary>The header's count phrase, pluralised, for a count from
    /// <see cref="SessionCount"/>.</summary>
    public static string CountPhrase(int sessions) => sessions switch
    {
        0 => "no sessions",
        1 => "1 session",
        _ => $"{sessions} sessions",
    };

    /// <summary>Index of the first tile Enter would act on, or -1 when there is none.</summary>
    public static int FirstSelectable(IReadOnlyList<PeekTile> tiles)
    {
        for (var i = 0; i < tiles.Count; i++)
            if (tiles[i].Selectable) return i;
        return -1;
    }

    /// <summary>
    /// The tile the overlay opens on: the first selectable SESSION, falling back to
    /// huddle's own tile when there is no session to switch to.
    ///
    /// <para>huddle's tile is first in the list (spec section 9: you need the way back),
    /// but it must not be the default. Summoned from the <c>peek</c> verb the operator is
    /// already standing in huddle's console, so opening on it means peek, Enter, nothing
    /// changed: rule 4's failure in a new guise. Alt+Tab defaults to the NEXT window for
    /// the same reason. huddle stays one Home, Shift+Tab or Left press away.</para>
    /// </summary>
    public static int DefaultSelection(IReadOnlyList<PeekTile> tiles)
    {
        for (var i = 0; i < tiles.Count; i++)
            if (tiles[i].Selectable && tiles[i].InstanceId != SelfId) return i;
        return FirstSelectable(tiles);
    }

    // Worst news wins, and only one line is drawn: an API error is an alarm, idle is
    // an observation that cannot tell "stuck" from "waiting at the prompt", and
    // unread mail is the mildest of the three.
    private static string? Note(PeekSource s, bool selectable)
    {
        if (!selectable) return "no window huddle can identify — Alt+Tab";
        if (!string.IsNullOrWhiteSpace(s.ApiError)) return $"[!] API: {s.ApiError}";
        if (s.IdleMinutes >= IdleThresholdMinutes) return $"idle {s.IdleMinutes}m";
        if (s.Unread > 0) return $"{s.Unread} unread";
        return null;
    }
}
