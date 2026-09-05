using Huddle;
using Xunit;

namespace HuddleTests;

// Tiles are built from a pure snapshot, never from live SessionInstance objects, so
// every rule here (labels, signals, the unselectable case, the empty case) is
// testable without a process, a window or a transcript on disk.
public class PeekModelTests
{
    private static PeekSource Src(
        string id = "repo1:architect",
        string? project = null,
        string uptime = "2h",
        long window = 42,
        string? apiError = null,
        int idleMinutes = 0,
        int unread = 0) =>
        new(id, project, uptime, new IntPtr(window), apiError, idleMinutes, unread);

    [Fact]
    public void A_session_with_a_window_is_selectable_and_labelled()
    {
        var tiles = PeekModel.Build(new[] { Src(project: "FEATURE") });

        var t = Assert.Single(tiles);
        Assert.True(t.Selectable);
        Assert.Equal("repo1:architect", t.Line1);
        Assert.Equal("[FEATURE]  2h", t.Line2);
        Assert.Null(t.Note);
    }

    [Fact]
    public void Without_a_project_the_second_line_is_just_uptime()
    {
        var t = Assert.Single(PeekModel.Build(new[] { Src(project: null, uptime: "15m") }));
        Assert.Equal("15m", t.Line2);
    }

    // Rule 4 from the spec: a tile is selectable only if Enter on it would do
    // something. A session huddle has no window handle for cannot be raised, so
    // making it selectable would reproduce corelib's first-run bug — press the
    // hotkey, press Enter, nothing happens.
    [Fact]
    public void A_session_with_no_window_is_shown_but_not_selectable()
    {
        var t = Assert.Single(PeekModel.Build(new[] { Src(window: 0) }));

        Assert.False(t.Selectable);
        Assert.Contains("Alt+Tab", t.Note);
    }

    [Fact]
    public void An_api_error_outranks_idle_and_unread()
    {
        var t = Assert.Single(PeekModel.Build(
            new[] { Src(apiError: "529 overloaded", idleMinutes: 40, unread: 3) }));

        Assert.Equal("[!] API: 529 overloaded", t.Note);
    }

    [Fact]
    public void Idle_is_reported_only_past_the_threshold()
    {
        Assert.Null(Assert.Single(PeekModel.Build(new[] { Src(idleMinutes: 2) })).Note);
        Assert.Equal("idle 3m", Assert.Single(PeekModel.Build(new[] { Src(idleMinutes: 3) })).Note);
    }

    [Fact]
    public void Unread_mail_shows_when_there_is_no_worse_news()
    {
        Assert.Equal("2 unread", Assert.Single(PeekModel.Build(new[] { Src(unread: 2) })).Note);
    }

    [Fact]
    public void No_sessions_yields_one_unselectable_placeholder()
    {
        var t = Assert.Single(PeekModel.Build(Array.Empty<PeekSource>()));

        Assert.False(t.Selectable);
        Assert.Equal("No running sessions", t.Line1);
    }

    // Spec section 9: "Huddle's own console is a tile. You need the way back." It is not
    // a session, so it carries none of the session signals, and when the console host
    // gives no window (Windows Terminal) it degrades to exactly the unselectable tile a
    // WT-hosted session gets. Handles are supplied here rather than read from
    // GetConsoleWindow, so the shape is asserted without a desktop.
    [Fact]
    public void Huddles_own_console_is_a_plain_tile_selectable_only_when_it_has_a_window()
    {
        var live = Assert.Single(PeekModel.Build(
            new[] { PeekController.SelfSource(new IntPtr(7), "3h 4m") }));

        Assert.True(live.Selectable);
        Assert.Equal("huddle", live.Line1);
        Assert.Equal("3h 4m", live.Line2);   // no project: uptime alone
        Assert.Null(live.Note);              // no API error, no idle, no unread

        var hosted = Assert.Single(PeekModel.Build(
            new[] { PeekController.SelfSource(IntPtr.Zero, "3h 4m") }));

        Assert.False(hosted.Selectable);
        Assert.Contains("Alt+Tab", hosted.Note);
    }

    // The overlay header counts sessions, and a cell is not always a session: the empty
    // overlay's placeholder is not one, and neither is huddle's own console. Counting
    // cells made the empty overlay read "1 sessions" above a tile saying there were none.
    [Fact]
    public void The_header_counts_sessions_not_cells()
    {
        var empty = PeekModel.Build(Array.Empty<PeekSource>());
        Assert.Equal("no sessions", PeekModel.CountPhrase(PeekModel.SessionCount(empty, empty.Count)));

        var selfOnly = PeekModel.Build(new[] { PeekController.SelfSource(new IntPtr(7), "1h 0m") });
        Assert.Equal("no sessions", PeekModel.CountPhrase(PeekModel.SessionCount(selfOnly, selfOnly.Count)));

        var one = PeekModel.Build(new[] { PeekController.SelfSource(new IntPtr(7), "1h 0m"), Src() });
        Assert.Equal("1 session", PeekModel.CountPhrase(PeekModel.SessionCount(one, one.Count)));

        var two = PeekModel.Build(new[] { Src(), Src(id: "repo1:reviewer") });
        Assert.Equal("2 sessions", PeekModel.CountPhrase(PeekModel.SessionCount(two, two.Count)));

        // Only the drawn prefix counts; the overflow is reported separately as "not shown".
        Assert.Equal("1 session", PeekModel.CountPhrase(PeekModel.SessionCount(two, 1)));
    }

    [Fact]
    public void FirstSelectable_skips_unselectable_tiles_and_reports_none()
    {
        var tiles = PeekModel.Build(new[] { Src(window: 0), Src(id: "repo1:reviewer") });
        Assert.Equal(1, PeekModel.FirstSelectable(tiles));

        Assert.Equal(-1, PeekModel.FirstSelectable(PeekModel.Build(Array.Empty<PeekSource>())));
    }

    // huddle's own tile is first in the list, but it must not be what the overlay opens
    // on: summoned from the peek verb the operator is already in huddle's console, so
    // defaulting to it means peek, Enter, nothing changed. Alt+Tab defaults to the next
    // window for the same reason.
    [Fact]
    public void The_default_selection_is_the_first_session_not_huddle_itself()
    {
        var tiles = PeekModel.Build(new[]
        {
            PeekController.SelfSource(new IntPtr(7), "3h"),
            Src(window: 0),                      // unselectable: skipped like any other
            Src(id: "repo1:reviewer"),
        });

        Assert.Equal(0, PeekModel.FirstSelectable(tiles));   // huddle is still first in the list
        Assert.Equal(2, PeekModel.DefaultSelection(tiles));
        Assert.Equal("repo1:reviewer", tiles[2].Line1);
    }

    [Fact]
    public void With_no_session_to_switch_to_the_default_falls_back_to_huddle()
    {
        var tiles = PeekModel.Build(new[] { PeekController.SelfSource(new IntPtr(7), "3h") });

        Assert.Equal(0, PeekModel.DefaultSelection(tiles));
        Assert.Equal(PeekModel.SelfId, tiles[0].InstanceId);
    }

    [Fact]
    public void With_nothing_selectable_the_default_matches_FirstSelectable()
    {
        // A WT-hosted huddle with a WT-hosted session: no handle anywhere, so there is
        // nothing Enter could raise and the overlay must not pretend otherwise.
        var tiles = PeekModel.Build(new[]
        {
            PeekController.SelfSource(IntPtr.Zero, "3h"),
            Src(window: 0),
        });

        Assert.Equal(-1, PeekModel.FirstSelectable(tiles));
        Assert.Equal(-1, PeekModel.DefaultSelection(tiles));

        var empty = PeekModel.Build(Array.Empty<PeekSource>());
        Assert.Equal(PeekModel.FirstSelectable(empty), PeekModel.DefaultSelection(empty));
    }
}
