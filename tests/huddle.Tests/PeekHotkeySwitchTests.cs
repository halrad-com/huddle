using Huddle;
using Xunit;

namespace HuddleTests;

// The swap policy is the whole point of PeekHotkeySwitch: an operator hunting for a free
// chord must never end up with no hotkey because a guess was taken. These tests drive the
// injected listener factory, so nothing here asks Windows for a real hotkey.
public class PeekHotkeySwitchTests
{
    private sealed class FakeHotkey : IPeekHotkey
    {
        public FakeHotkey(string chord, bool registered) { Chord = chord; Registered = registered; }

        public string Chord { get; }
        public bool Registered { get; }
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    // Hands out a listener per chord and remembers every one it made, so a test can assert
    // which of them was disposed and which is still alive.
    private sealed class FakeFactory
    {
        private readonly Func<string, bool> _grants;
        public readonly List<FakeHotkey> Made = new();
        public int Calls;

        /// <summary>One entry per call: was ANY listener made earlier already disposed at the
        /// moment this one was asked for? The swap ordering is the whole safety property of
        /// the class, and "the incumbent survived a failed swap" does not pin it: an
        /// implementation that disposed first and rebuilt on failure passes every such
        /// assertion while leaving the operator chordless for the length of the attempt.
        /// This is the observation that catches it.</summary>
        public readonly List<bool> AnyEarlierAlreadyDisposed = new();

        public FakeFactory(Func<string, bool> grants) { _grants = grants; }

        public IPeekHotkey? Start(string chord, Action onPressed, Action<string> log)
        {
            Calls++;
            AnyEarlierAlreadyDisposed.Add(Made.Any(m => m.Disposed));
            if (!_grants(chord))
                log($"peek hotkey: '{chord}' is already taken by another application; peek verb still works");
            var made = new FakeHotkey(chord, _grants(chord));
            Made.Add(made);
            return made;
        }
    }

    private static SettingDef PeekDef =>
        SettingsCatalog.All.First(d => d.Key.Equals("peekHotkey", StringComparison.Ordinal));

    private static PeekHotkeySwitch Switch(FakeFactory f, string chord = "Ctrl+Alt+H") =>
        new(chord, () => { }, _ => { }, f.Start);

    [Fact]
    public void A_chord_that_registers_goes_live_and_the_old_one_is_released()
    {
        var f = new FakeFactory(_ => true);
        using var sw = Switch(f);
        var old = f.Made[0];

        Assert.True(sw.TrySet("Ctrl+Alt+J", out var message));

        Assert.Equal("peek hotkey: 'Ctrl+Alt+J' is live now; 'Ctrl+Alt+H' is released", message);
        Assert.True(old.Disposed);
        Assert.False(f.Made[1].Disposed);
        Assert.Equal("Ctrl+Alt+J", sw.Chord);
        Assert.True(sw.Active);
    }

    [Fact]
    public void A_chord_another_application_owns_is_discarded_and_the_old_one_keeps_working()
    {
        var f = new FakeFactory(c => c == "Ctrl+Alt+H");
        using var sw = Switch(f);
        var old = f.Made[0];

        Assert.False(sw.TrySet("Ctrl+Alt+J", out var message));

        Assert.Equal(
            "peek hotkey: 'Ctrl+Alt+J' is already taken by another application; 'Ctrl+Alt+H' is still the peek chord",
            message);
        Assert.True(f.Made[1].Disposed);      // the candidate is thrown away
        Assert.False(old.Disposed);           // and the working chord survives
        Assert.Equal("Ctrl+Alt+H", sw.Chord);
        Assert.True(sw.Active);
    }

    [Fact]
    public void A_chord_that_does_not_parse_constructs_nothing_and_changes_nothing()
    {
        var f = new FakeFactory(_ => true);
        using var sw = Switch(f);
        var old = f.Made[0];

        Assert.False(sw.TrySet("J", out var message));   // no modifier

        Assert.Equal(
            "peek hotkey: 'J' is not a usable chord (need at least one modifier and one key); 'Ctrl+Alt+H' is still the peek chord",
            message);
        Assert.Equal(1, f.Calls);             // the startup registration, and nothing since
        Assert.False(old.Disposed);
        Assert.Equal("Ctrl+Alt+H", sw.Chord);
        Assert.True(sw.Active);
    }

    // The case the operator is actually in: the configured chord was taken at startup, so
    // saying it is "still the peek chord" after a failed attempt would be the one lie that
    // matters.
    [Fact]
    public void A_failed_attempt_admits_when_no_chord_is_bound_at_all()
    {
        var f = new FakeFactory(_ => false);
        using var sw = Switch(f);

        Assert.False(sw.Active);
        Assert.False(sw.TrySet("Ctrl+Alt+J", out var message));

        Assert.Equal(
            "peek hotkey: 'Ctrl+Alt+J' is already taken by another application; 'Ctrl+Alt+H' is still configured "
            + "but was never registered, so no chord is bound; the peek verb still works",
            message);
        Assert.Equal("Ctrl+Alt+H", sw.Chord);
    }

    // A chord that was dead at startup can still be replaced by one that is free.
    [Fact]
    public void A_free_chord_rescues_a_startup_registration_that_lost()
    {
        var f = new FakeFactory(c => c == "Ctrl+Alt+J");
        using var sw = Switch(f);

        Assert.False(sw.Active);
        Assert.True(sw.TrySet("Ctrl+Alt+J", out var message));

        // Not "is released": nothing was ever registered to release, and the operator this
        // path exists for is precisely the one whose startup chord was taken.
        Assert.Equal(
            "peek hotkey: 'Ctrl+Alt+J' is live now; 'Ctrl+Alt+H' was never registered, so nothing was released",
            message);
        Assert.True(sw.Active);
    }

    [Fact]
    public void A_listener_that_will_not_start_carries_its_reason_and_keeps_the_old_chord()
    {
        var f = new FakeFactory(_ => true);
        var refuses = false;
        IPeekHotkey? Start(string chord, Action onPressed, Action<string> log)
        {
            if (!refuses) return f.Start(chord, onPressed, log);
            log("peek hotkey: the message loop thread could not be created");
            return null;
        }

        using var sw = new PeekHotkeySwitch("Ctrl+Alt+H", () => { }, _ => { }, Start);
        refuses = true;

        Assert.False(sw.TrySet("Ctrl+Alt+J", out var message));

        Assert.Equal(
            "peek hotkey: 'Ctrl+Alt+J' could not be registered (peek hotkey: the message loop thread could not "
            + "be created); 'Ctrl+Alt+H' is still the peek chord",
            message);
        Assert.False(f.Made[0].Disposed);
        Assert.Equal("Ctrl+Alt+H", sw.Chord);
    }

    [Fact]
    public void Disposing_releases_the_live_chord_and_is_idempotent()
    {
        var f = new FakeFactory(_ => true);
        var sw = Switch(f);

        sw.Dispose();
        sw.Dispose();

        Assert.True(f.Made[0].Disposed);
        Assert.False(sw.Active);
    }

    // --- acquisition: the candidate walk ---------------------------------
    //
    // The defect these cover: a single default chord shipped the hotkey dead on a machine
    // where something else already owned it, every start logged that it was taken, and the
    // only recovery was the operator guessing chords by hand.

    [Fact]
    public void An_unset_hotkey_walks_the_candidates_and_binds_the_first_one_that_registers()
    {
        var free = PeekChord.Candidates[2];
        var f = new FakeFactory(c => c == free);
        var logs = new List<string>();

        using var sw = new PeekHotkeySwitch(
            ResolvedSettings.Defaults().Get("peekHotkey"), () => { }, logs.Add, f.Start);

        Assert.True(sw.Active);
        Assert.Equal(free, sw.Chord);
        Assert.Equal(3, f.Calls);                       // stopped at the first that worked
        Assert.Contains($"peek hotkey: '{free}' is live (2 earlier candidates were already taken)", logs);
        Assert.True(f.Made[0].Disposed);                // nothing dead is left parked
        Assert.True(f.Made[1].Disposed);
        Assert.False(f.Made[2].Disposed);
    }

    [Fact]
    public void The_first_candidate_binding_says_so_without_a_skip_count()
    {
        var f = new FakeFactory(_ => true);
        var logs = new List<string>();

        using var sw = new PeekHotkeySwitch(
            ResolvedSettings.Defaults().Get("peekHotkey"), () => { }, logs.Add, f.Start);

        Assert.Equal(PeekChord.Candidates[0], sw.Chord);
        Assert.Equal(1, f.Calls);
        Assert.Contains($"peek hotkey: '{PeekChord.Candidates[0]}' is live", logs);
        Assert.DoesNotContain(logs, l => l.Contains("earlier candidate"));
    }

    [Fact]
    public void Every_candidate_taken_is_one_line_that_names_the_verb_that_fixes_it()
    {
        var f = new FakeFactory(_ => false);
        var logs = new List<string>();

        using var sw = new PeekHotkeySwitch(
            ResolvedSettings.Defaults().Get("peekHotkey"), () => { }, logs.Add, f.Start);

        Assert.False(sw.Active);
        Assert.Equal(PeekChord.Candidates.Count, f.Calls);
        Assert.All(f.Made, m => Assert.True(m.Disposed));
        var line = Assert.Single(logs);
        Assert.Equal(
            $"peek hotkey: no chord is bound - all {PeekChord.Candidates.Count} candidates were taken "
            + $"({string.Join(", ", PeekChord.Candidates)}); set one with `settings peekHotkey <chord>`, "
            + "and the peek verb and the pinned shortcut work meanwhile",
            line);
    }

    // Source, not the value, is what says whether the operator chose. Comparing the text
    // against the catalog default would misread an operator who deliberately sets the chord
    // that happens to BE the default, and then silently move them off it.
    [Fact]
    public void An_explicit_hotkey_is_tried_alone_and_never_falls_back_to_a_candidate()
    {
        var f = new FakeFactory(c => c != "Ctrl+Alt+Q");   // every candidate would have worked
        var logs = new List<string>();

        using var sw = new PeekHotkeySwitch(
            new ResolvedSetting(PeekDef, "Ctrl+Alt+Q", SettingSource.Settings), () => { }, logs.Add, f.Start);

        Assert.False(sw.Active);
        Assert.Equal(1, f.Calls);
        Assert.Equal("Ctrl+Alt+Q", sw.Chord);
        Assert.DoesNotContain(f.Made, m => PeekChord.Candidates.Contains(m.Chord));
        Assert.Contains(
            "peek hotkey: 'Ctrl+Alt+Q' is already taken by another application; peek verb still works",
            logs);
    }

    [Fact]
    public void An_explicit_hotkey_that_is_the_catalog_default_is_still_treated_as_a_choice()
    {
        var f = new FakeFactory(_ => false);
        var chosen = PeekChord.Candidates[0];

        using var sw = new PeekHotkeySwitch(
            new ResolvedSetting(PeekDef, chosen, SettingSource.Settings), () => { }, _ => { }, f.Start);

        Assert.Equal(1, f.Calls);          // the one chord they named, not the list behind it
        Assert.Equal(chosen, sw.Chord);
    }

    [Fact]
    public void A_legacy_top_level_hotkey_counts_as_explicit_too()
    {
        var f = new FakeFactory(_ => false);

        using var sw = new PeekHotkeySwitch(
            new ResolvedSetting(PeekDef, "Ctrl+Alt+Q", SettingSource.TopLevelLegacy), () => { }, _ => { }, f.Start);

        Assert.Equal(1, f.Calls);
        Assert.Equal("Ctrl+Alt+Q", sw.Chord);
    }

    // --- unset: back to the candidate walk, live ------------------------

    [Fact]
    public void Going_back_to_the_candidates_at_runtime_takes_the_first_free_one()
    {
        var free = PeekChord.Candidates[1];
        var f = new FakeFactory(c => c == "Ctrl+Alt+Q" || c == free);

        using var sw = new PeekHotkeySwitch(
            new ResolvedSetting(PeekDef, "Ctrl+Alt+Q", SettingSource.Settings), () => { }, _ => { }, f.Start);

        Assert.True(sw.TrySetFirstAvailable(PeekChord.Candidates, out var message));

        Assert.Equal(
            $"peek hotkey: '{free}' is live now; 'Ctrl+Alt+Q' is released (1 earlier candidate was already taken)",
            message);
        Assert.Equal(free, sw.Chord);
        Assert.True(sw.Active);
    }

    [Fact]
    public void A_candidate_walk_that_finds_nothing_keeps_the_chord_the_operator_had()
    {
        var f = new FakeFactory(c => c == "Ctrl+Alt+Q");

        using var sw = new PeekHotkeySwitch(
            new ResolvedSetting(PeekDef, "Ctrl+Alt+Q", SettingSource.Settings), () => { }, _ => { }, f.Start);

        Assert.False(sw.TrySetFirstAvailable(PeekChord.Candidates, out var message));

        Assert.Equal(
            $"peek hotkey: none of the {PeekChord.Candidates.Count} candidate chords could be registered "
            + $"({string.Join(", ", PeekChord.Candidates)}); 'Ctrl+Alt+Q' is still the peek chord; "
            + "name one with `settings peekHotkey <chord>`",
            message);
        Assert.Equal("Ctrl+Alt+Q", sw.Chord);
        Assert.True(sw.Active);
    }

    // --- re-setting the chord that is already live -----------------------
    //
    // RegisterHotKey scopes IDS per window; a CHORD is global to the desktop, so asking for
    // one huddle already holds fails exactly like a competitor owning it. Without the
    // short-circuit the message blamed an application that does not exist.
    [Fact]
    public void Re_setting_the_live_chord_reports_it_rather_than_blaming_a_phantom()
    {
        var f = new FakeFactory(_ => true);
        using var sw = Switch(f, "Ctrl+Alt+J");

        Assert.True(sw.TrySet("ctrl + alt + j", out var message));

        Assert.Equal("peek hotkey: 'ctrl + alt + j' is already the peek chord; nothing changed", message);
        Assert.Equal(1, f.Calls);              // nothing was constructed to find that out
        Assert.Equal("Ctrl+Alt+J", sw.Chord);
        Assert.True(sw.Active);
    }

    // The short-circuit is about the LIVE registration, so a chord that is configured but
    // was never granted must still be retried rather than reported as already in force.
    [Fact]
    public void Re_setting_a_configured_chord_that_never_registered_tries_again()
    {
        var granted = false;
        var f = new FakeFactory(_ => granted);
        using var sw = Switch(f, "Ctrl+Alt+J");

        Assert.False(sw.Active);
        granted = true;
        Assert.True(sw.TrySet("Ctrl+Alt+J", out _));

        Assert.Equal(2, f.Calls);
        Assert.True(sw.Active);
    }

    // --- lifetime -------------------------------------------------------

    // Shutdown disposes the switch BEFORE the orchestrator a summon reaches into. Nulling
    // the current listener arranged that guard; the disposed flag enforces it.
    [Fact]
    public void A_disposed_switch_registers_nothing_further()
    {
        var f = new FakeFactory(_ => true);
        var sw = Switch(f);
        sw.Dispose();

        Assert.False(sw.TrySet("Ctrl+Alt+J", out var message));

        Assert.Equal("peek hotkey: huddle is shutting down; 'Ctrl+Alt+J' was not registered", message);
        Assert.Equal(1, f.Calls);
        Assert.False(sw.Active);
    }

    [Fact]
    public void A_disposed_switch_refuses_a_candidate_walk_too()
    {
        var f = new FakeFactory(_ => true);
        var sw = Switch(f);
        sw.Dispose();

        Assert.False(sw.TrySetFirstAvailable(PeekChord.Candidates, out _));
        Assert.Equal(1, f.Calls);
    }

    // The ordering, not just the outcome: the incumbent has to be undisposed at the moment
    // the candidate is CREATED. Disposing first and rebuilding on failure would satisfy
    // every "the old chord survived" assertion while leaving the desktop chordless for the
    // length of the attempt.
    [Fact]
    public void The_incumbent_is_still_registered_when_the_candidate_is_created()
    {
        var f = new FakeFactory(_ => true);
        using var sw = Switch(f);

        Assert.True(sw.TrySet("Ctrl+Alt+J", out _));

        Assert.Equal(2, f.Calls);
        Assert.All(f.AnyEarlierAlreadyDisposed, earlierDisposed => Assert.False(earlierDisposed));
        Assert.True(f.Made[0].Disposed);       // released only after the new one was granted
    }
}
