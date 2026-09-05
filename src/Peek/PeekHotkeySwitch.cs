namespace Huddle;

/// <summary>A registered global chord that can be told whether it actually got the chord
/// and can be released. <see cref="HotkeyListener"/> is the real one; the interface exists
/// so <see cref="PeekHotkeySwitch"/>'s swap policy can be tested without asking Windows for
/// a real hotkey.</summary>
public interface IPeekHotkey : IDisposable
{
    /// <summary>True when the chord was actually granted. False means it parsed but
    /// another application already owns it, so no summon will ever arrive.</summary>
    bool Registered { get; }
}

/// <summary>
/// Owns the live peek hotkey and can swap it for another chord while huddle is running.
///
/// <para>Finding a free chord is trial and error, and every attempt used to cost a full
/// <c>reload</c>. This is the one setting that re-applies itself: <c>settings peekHotkey
/// &lt;chord&gt;</c> writes huddle.json as usual and then calls <see cref="TrySet"/> on the
/// running process.</para>
///
/// <para><b>The swap policy is the point of this class.</b> The new chord is registered
/// BEFORE the old one is released, and a candidate that fails is thrown away with the old
/// registration left intact. An operator hunting for a free chord must never be left with
/// no hotkey at all because their third guess was also taken; the worst outcome of a failed
/// attempt is that nothing changed.</para>
///
/// <para>Two listeners therefore coexist for the length of a swap, both using
/// <c>HotkeyListener</c>'s single hotkey id constant. That is safe only for the IDS:
/// <c>RegisterHotKey</c> scopes an id to a window handle and every listener builds its own
/// message-only window, so the two ids live in different namespaces and cannot collide.
/// It says nothing about the CHORD. A chord is owned by the whole desktop, so a second
/// registration of the same modifiers plus key fails with
/// <c>ERROR_HOTKEY_ALREADY_REGISTERED</c> even when the current owner is huddle itself,
/// which is why <see cref="TrySet"/> answers an unchanged chord before constructing
/// anything rather than reporting a competitor that does not exist.</para>
/// </summary>
public sealed class PeekHotkeySwitch : IDisposable
{
    private readonly Func<string, Action, Action<string>, IPeekHotkey?> _start;
    private readonly Action _onPressed;
    private readonly object _gate = new();

    private IPeekHotkey? _current;
    private string _chord;

    /// <summary>Set by <see cref="Dispose"/>. Shutdown disposes this explicitly, BEFORE the
    /// orchestrator it summons into is torn down, so a <see cref="TrySet"/> arriving after
    /// that point must not quietly register a fresh chord whose callback would reach into a
    /// half-dismantled huddle. Nulling <c>_current</c> alone arranged the guard without
    /// enforcing it; this flag enforces it.</summary>
    private bool _disposed;

    /// <summary>
    /// Acquire the peek hotkey from the resolved <c>peekHotkey</c> setting.
    ///
    /// <para>The explicit-versus-default decision lives HERE, not in <c>Program.cs</c>. This
    /// class owns the chord from end to end: acquiring it, resolving a conflict, disposing
    /// anything that did not register, reporting what is actually bound, and swapping it
    /// later. A candidate loop written at the call site would leave a manager that does not
    /// manage and two places deciding about hotkeys.</para>
    ///
    /// <para><see cref="ResolvedSetting.Source"/> is the discriminator, never the value.
    /// <see cref="SettingSource.Settings"/> or <see cref="SettingSource.TopLevelLegacy"/>
    /// means the operator wrote the chord down, so it is registered alone and a failure is
    /// reported: an explicit choice is honoured or reported, never silently replaced by one
    /// they did not ask for. <see cref="SettingSource.Default"/> means nobody chose, so
    /// <see cref="PeekChord.Candidates"/> is walked and the first chord Windows grants wins.
    /// Comparing the VALUE against the catalog default would get this backwards the moment
    /// an operator deliberately sets the chord that happens to be the default.</para>
    /// </summary>
    public PeekHotkeySwitch(
        ResolvedSetting setting,
        Action onPressed,
        Action<string> log,
        Func<string, Action, Action<string>, IPeekHotkey?>? start = null)
        : this(Plan(setting), onPressed, log, start) { }

    /// <summary>What an acquisition will try, in order: the whole candidate list when the
    /// value is only the built-in default, or the operator's one chord and nothing else.</summary>
    private static IReadOnlyList<string> Plan(ResolvedSetting setting) =>
        setting.Source == SettingSource.Default ? PeekChord.Candidates : new[] { setting.Value };

    /// <param name="chord">The chord from settings, registered immediately. One chord and no
    /// fallback: this overload is the EXPLICIT case, where the operator named a chord and a
    /// failure has to be reported rather than papered over with a substitute.</param>
    /// <param name="onPressed">What a summon does.</param>
    /// <param name="log">Where the startup registration reports what it bound, or why it
    /// bound nothing. Used only for that first attempt: a later <see cref="TrySet"/> returns
    /// its own message so the caller can print one line instead of two saying nearly the
    /// same thing.</param>
    /// <param name="start">The listener factory, injected by tests the way
    /// <see cref="ThumbnailHost"/> injects its DWM calls.</param>
    public PeekHotkeySwitch(
        string chord,
        Action onPressed,
        Action<string> log,
        Func<string, Action, Action<string>, IPeekHotkey?>? start = null)
        : this(new[] { chord }, onPressed, log, start) { }

    /// <summary>
    /// Register the first of <paramref name="candidates"/> that Windows grants.
    ///
    /// <para>This is the UNSET case: no <c>peekHotkey</c> in huddle.json, so huddle picks.
    /// A single default shipped the feature dead on any machine already running something
    /// that owns that chord, and the operator's only recovery was guessing chords by hand.
    /// See <see cref="PeekChord.Candidates"/> for the list and the order.</para>
    ///
    /// <para>Whatever happens is one line in the log, naming the chord that actually bound
    /// and how many earlier candidates were skipped. The operator has to be able to learn
    /// which key to press without reading source or config.</para>
    /// </summary>
    public PeekHotkeySwitch(
        IReadOnlyList<string> candidates,
        Action onPressed,
        Action<string> log,
        Func<string, Action, Action<string>, IPeekHotkey?>? start = null)
    {
        _onPressed = onPressed;
        _start = start ?? ((c, pressed, l) => HotkeyListener.Start(c, pressed, l));

        var list = Normalise(candidates);
        // Whatever ends up bound, the configured chord is what the operator sees reported
        // back when nothing registers at all.
        _chord = list.Count > 0 ? list[0] : "";

        for (int i = 0; i < list.Count; i++)
        {
            // One candidate means the operator named it, so the listener's own failure line
            // is exactly the report they need and goes straight to the log. Several means
            // huddle is choosing, and a line per rejected guess would bury the answer: the
            // detail is collected and one summary is written below instead.
            var detail = new List<string>();
            var candidate = _start(list[i], onPressed, list.Count == 1 ? log : detail.Add);
            if (candidate is { Registered: true })
            {
                _current = candidate;
                _chord = list[i];
                log(i == 0
                    ? $"peek hotkey: '{_chord}' is live"
                    : $"peek hotkey: '{_chord}' is live ({Skipped(i)})");
                return;
            }
            // A candidate that did not get the chord still owns a message-only window and a
            // thread. Nothing will ever be delivered to it, so let it go.
            candidate?.Dispose();
        }

        if (list.Count == 0)
            log("peek hotkey: no chord is configured, so no chord is bound; the peek verb and the pinned shortcut still work");
        else if (list.Count > 1)
            // Named verb and all: the operator otherwise has to deduce that NO chord bound,
            // and then deduce how to name one. That deduction is the defect this whole
            // fallback exists to remove, so the exit is spelled out on the same line.
            log($"peek hotkey: no chord is bound - all {list.Count} candidates were taken "
                + $"({string.Join(", ", list)}); set one with `settings peekHotkey <chord>`, "
                + "and the peek verb and the pinned shortcut work meanwhile");
        // A single candidate has already reported itself, in the listener's own words.
    }

    /// <summary>Trim the caller's chords without dropping any: an empty entry is still put
    /// to the parser, so an empty <c>peekHotkey</c> is refused by name rather than silently
    /// becoming "no candidates".</summary>
    private static List<string> Normalise(IReadOnlyList<string>? candidates) =>
        (candidates ?? Array.Empty<string>()).Select(c => (c ?? "").Trim()).ToList();

    /// <summary>How a fallback reports what it walked past. Singular matters: "1 earlier
    /// candidates" reads as a bug in the message rather than a fact about the desktop.</summary>
    private static string Skipped(int count) => count == 1
        ? "1 earlier candidate was already taken"
        : $"{count} earlier candidates were already taken";

    /// <summary>The chord currently in force. Unchanged by a failed <see cref="TrySet"/>.</summary>
    public string Chord { get { lock (_gate) return _chord; } }

    /// <summary>True when a summon will actually arrive. False when the configured chord
    /// was taken or unusable, which is a normal state huddle runs in happily.</summary>
    public bool Active { get { lock (_gate) return _current is { Registered: true }; } }

    /// <summary>Try to move the peek hotkey to <paramref name="chord"/>. Returns false and
    /// leaves the previous registration alone when the chord cannot be taken.
    /// <paramref name="message"/> is always set and is the operator's only feedback, so it
    /// names the chord in every outcome.</summary>
    public bool TrySet(string chord, out string message)
    {
        lock (_gate) return TrySetLocked(chord, out message);
    }

    /// <summary>
    /// Walk <paramref name="candidates"/> and keep the first that registers. The runtime
    /// twin of the fallback constructor: it is what <c>settings unset peekHotkey</c> does,
    /// because unsetting means "go back to letting huddle choose" and the operator should
    /// not have to reload to get that.
    /// </summary>
    public bool TrySetFirstAvailable(IReadOnlyList<string> candidates, out string message)
    {
        lock (_gate)
        {
            var list = Normalise(candidates);
            if (list.Count == 0)
            {
                message = $"peek hotkey: no candidate chords to try; {Unchanged()}";
                return false;
            }

            for (int i = 0; i < list.Count; i++)
            {
                if (TrySetLocked(list[i], out var attempt))
                {
                    message = i == 0 ? attempt : $"{attempt} ({Skipped(i)})";
                    return true;
                }
            }

            // Unchanged() rather than a flat "nothing is bound": a failed walk leaves any
            // live incumbent registered, and claiming otherwise would be the same kind of
            // lie the failure path was built to avoid. The verb is named either way, since
            // the operator who lands here has to choose a chord themselves.
            message = $"peek hotkey: none of the {list.Count} candidate chords could be registered "
                + $"({string.Join(", ", list)}); {Unchanged()}; name one with `settings peekHotkey <chord>`";
            return false;
        }
    }

    /// <summary>The body of <see cref="TrySet"/>, callable from inside <c>_gate</c> so a
    /// candidate walk holds the lock across the whole search instead of racing itself
    /// between guesses.</summary>
    private bool TrySetLocked(string chord, out string message)
    {
        var wanted = (chord ?? "").Trim();

        // Shutdown disposes this switch on purpose, ahead of the orchestrator a summon
        // reaches into. Registering after that would hand Windows a chord whose callback
        // walks a fleet being torn down.
        if (_disposed)
        {
            message = $"peek hotkey: huddle is shutting down; '{wanted}' was not registered";
            return false;
        }

        // Parse first, so nothing is constructed for a chord that could never work and
        // the "did not parse" case cannot be confused with "the OS said no".
        if (!PeekChord.TryParse(wanted, out var wantedMods, out var wantedKey))
        {
            message = $"peek hotkey: '{wanted}' is not a usable chord (need at least one modifier and one key); {Unchanged()}";
            return false;
        }

        // A chord belongs to the desktop, not to a window, so registering one huddle is
        // ALREADY holding fails with ERROR_HOTKEY_ALREADY_REGISTERED and looks exactly
        // like a competitor owning it. Without this, re-setting the live chord accused an
        // application that does not exist. Compared PARSED, so 'ctrl+alt+j' and
        // 'Ctrl + Alt + J' are recognised as the one chord they are.
        if (_current is { Registered: true }
            && PeekChord.TryParse(_chord, out var liveMods, out var liveKey)
            && liveMods == wantedMods && liveKey == wantedKey)
        {
            message = $"peek hotkey: '{wanted}' is already the peek chord; nothing changed";
            return true;
        }

        // The candidate's own log lines are captured rather than printed: this method
        // reports the outcome itself, and the listener's wording would duplicate it.
        var detail = new List<string>();
        var candidate = _start(wanted, _onPressed, detail.Add);

        if (candidate == null)
        {
            // Start returns null on a parse failure (ruled out above) or after an
            // exception, so this is the listener refusing to start at all. Carry its
            // reason through: it is the only trace of what went wrong.
            var why = detail.Count > 0 ? detail[^1] : "the listener did not start";
            message = $"peek hotkey: '{wanted}' could not be registered ({why}); {Unchanged()}";
            return false;
        }

        if (!candidate.Registered)
        {
            // Registered NEW first, and it lost. Throw the candidate away and keep the
            // old registration: a failed attempt must cost nothing.
            candidate.Dispose();
            message = $"peek hotkey: '{wanted}' is already taken by another application; {Unchanged()}";
            return false;
        }

        var previous = _chord;
        // Read BEFORE the dispose below. "is released" is only true of a chord Windows
        // actually granted, and the operator arriving here from a startup that bound
        // nothing would otherwise be told huddle just gave up a chord it never held -
        // the same over-claim the failure path already takes care to avoid.
        var previousWasBound = _current is { Registered: true };
        _current?.Dispose();
        _current = candidate;
        _chord = wanted;
        message = previousWasBound
            ? $"peek hotkey: '{wanted}' is live now; '{previous}' is released"
            : $"peek hotkey: '{wanted}' is live now; '{previous}' was never registered, so nothing was released";
        return true;
    }

    /// <summary>How a failed attempt describes what the operator is left with. A chord that
    /// was never granted is said so plainly: claiming it is "still the peek chord" would be
    /// the exact lie that matters, because a taken chord is why the operator is here.</summary>
    private string Unchanged() => _current is { Registered: true }
        ? $"'{_chord}' is still the peek chord"
        : $"'{_chord}' is still configured but was never registered, so no chord is bound; the peek verb still works";

    /// <summary>Release the chord. Idempotent, like the listener it owns: shutdown disposes
    /// this explicitly, before the orchestrator is torn down, and the <c>using var</c> at
    /// the same call site disposes it again on the way out. Also latches
    /// <see cref="_disposed"/>, so a later <see cref="TrySet"/> refuses instead of binding a
    /// chord that would summon into a half-dismantled huddle.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _current?.Dispose();
            _current = null;
        }
    }
}
