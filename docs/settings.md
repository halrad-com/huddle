# Settings

Huddle's tunable knobs live in a `"settings"` object inside `huddle.json`. Every key is
validated when the config loads: an unknown key, a wrong type, or an out-of-range value is
**refused by name**, not silently ignored.

Design: [`superpowers/specs/2026-08-21-settings-design.md`](superpowers/specs/2026-08-21-settings-design.md).

## Why validation, not tolerance

`System.Text.Json` ignores unknown properties by default. Before this existed, writing
`rescanIntervalSecond` — one missing `s` — started huddle, reported nothing, and ran the
built-in default forever. The setting looked applied and was not. A typo'd key must not be
indistinguishable from a setting that does nothing, so the loader refuses instead.

## Where settings live

One block in `huddle.json`. **Not a second file** — `--config` already names this one, and
a second config sitting beside the first costs a concept that has to be explained before it
can be used.

```json
{
  "sessions": [ /* ... */ ],
  "settings": {
    "rescanIntervalSeconds": 30,
    "taskAckMinutes": 15,
    "statsSinceDays": 7,
    "gitActivityLog": true
  }
}
```

**The nine pre-existing top-level keys keep working.** They are read as a fallback when the
same key is absent from `settings`, and every surface labels them `top-level (legacy)`.
Nothing was removed and no existing `huddle.json` breaks. Precedence is:

```
"settings" block  >  legacy top-level key  >  built-in default
```

A key present in *both* resolves to `settings` and is **reported once at startup** —
reported, never silently resolved.

## The settings

`Applies` matters because huddle is long-running. `live` means the next read picks the value
up; `startup` means it is captured when something is constructed and needs a `reload`.
Without that column an operator changes a value, sees no effect, and concludes the whole
settings system is broken.

| Key | Kind | Range | Default | Applies | What it does |
|---|---|---|---|---|---|
| `contextFile` | bool | | `true` | startup | write `logs/context.md` |
| `ipc` | bool | | `true` | startup | run the orchestrator and mailboxes |
| `crashLogRetention` | int | 0..1000 | `10` | live | crash logs kept per session |
| `rescanIntervalSeconds` | int | 0..3600 | `30` | startup | command-inbox rescan backstop; 0 disables |
| `reclaimResourcesOnStop` | bool | | `false` | live | also run recorded cleanup commands on leak |
| `seedPermissions` | bool | | `true` | startup | seed each repo's `.claude/settings.local.json` |
| `autoRestart` | bool | | `false` | live | restart a session that dies |
| `maxAutoRestarts` | int | 0..100 | `3` | live | restart attempts before giving up |
| `backoffSeconds` | text | | `2,5,15` | live | restart backoff, comma-separated seconds |
| `statsSinceDays` | int | 1..3650 | `7` | live | default window for `stats` |
| `gitActivityLog` | bool | | `true` | startup | append cred requests + movements to `logs/git-activity.jsonl` |
| `gitPollSeconds` | int | 1..300 | `5` | startup | git activity poll interval |
| `taskAckMinutes` | int | 1..1440 | `15` | live | unacked task escalates after this |
| `transcriptMaxScan` | int | 10..1000 | `100` | live | transcripts scanned by `history` / `find` |
| `shellRegistration` | bool | | `true` | startup | keep the Start-menu entry registered and healed |
| `commitAudit` | bool | | `true` | live | report commits touching files nobody claimed |
| `peekHotkey` | text | | `Win+Alt+H` * | live | peek switcher chord; unset tries `Win+Alt+H`, `Ctrl+Alt+F12`, `Ctrl+Alt+F9`, `Ctrl+Alt+0` in order |

`backoffSeconds` is text holding a comma-separated list because there are three kinds and an
int array is not one of them. Adding a fourth kind for a single setting costs more than
parsing one string, and that parse is validated at load like everything else.

\* `peekHotkey` is the one row whose Default column is not the whole story. Unset, huddle
tries a **list** of chords in order and binds the first Windows grants; the column shows the
first of them, because a column ten characters wide cannot hold four chords and a display
default the operator can actually press beats one that is a sentence. The full list is in
the help text beside it, and in the section below.

### `peekHotkey`

The global chord that opens the peek switcher: the same overlay the `peek` verb shows and
the pinned "Huddle Sessions" shortcut summons. It is registered at startup, and
`settings peekHotkey <chord>` re-registers it on the running process, so a change takes
effect immediately with no `reload`. It is the only setting that re-applies itself: finding
a chord no other application owns is trial and error, and charging a rebuild-and-relaunch
per guess made the search cost more than the feature.

#### Unset: huddle picks, from a list

With no `peekHotkey` in `huddle.json`, huddle tries these in order and keeps the first one
Windows grants:

1. `Win+Alt+H`
2. `Ctrl+Alt+F12`
3. `Ctrl+Alt+F9`
4. `Ctrl+Alt+0`

A single default is a hotkey that ships dead on any machine already running something that
owns that chord, and the old `Ctrl+Alt+H` was exactly that: every start logged that the key
was taken, and the only recovery was the operator guessing chords by hand. Every entry above
was probe-registered on a real desktop and measured free; a candidate is measured, never
reasoned about. `Ctrl+Alt+OemPlus` also measured free and is deliberately absent, because
the chord grammar below cannot express it.

The startup line names the chord that actually bound, and how many earlier candidates it
walked past, so the key to press is never something you have to look up:

```
peek hotkey: 'Win+Alt+H' is live
peek hotkey: 'Ctrl+Alt+F9' is live (2 earlier candidates were already taken)
```

If every candidate is taken, huddle says so in one line and names the way out. It still
starts, and both other routes to the overlay still work:

```
peek hotkey: no chord is bound - all 4 candidates were taken (Win+Alt+H, Ctrl+Alt+F12, Ctrl+Alt+F9, Ctrl+Alt+0); set one with `settings peekHotkey <chord>`, and the peek verb and the pinned shortcut work meanwhile
```

#### Set: your chord, and only your chord

A `peekHotkey` you wrote down is registered **alone**. There is no fallback, because an
explicit choice has to be honoured or reported, never quietly swapped for a chord you did
not ask for. What distinguishes the two cases is where the value came from, not what it
says: a chord you set that happens to be the same as the first candidate is still your
choice, and is still tried alone.

Unsetting it goes back to the candidate walk, live, with no `reload`:

```
settings unset peekHotkey
settings: unset peekHotkey — back to the built-in candidate chords
peek hotkey: 'Win+Alt+H' is live now; 'Ctrl+Alt+J' is released
```

A chord is at least one modifier and exactly one key. Modifiers are `Ctrl` (or `Control`),
`Alt`, `Shift` and `Win` (or `Windows` / `Super`), in any combination; the key is a single
letter, a single digit, or `F1` through `F24`. Case is ignored and spaces around the `+`
are trimmed, so `win + alt + h` and `Win+Alt+H` are one chord. A chord with **no modifier is
refused**, because a global hotkey with no modifier swallows that key in every application
on the desktop. A chord naming two keys is refused for a plainer reason: `RegisterHotKey`
takes exactly one virtual key.

Two failures are possible for a chord you set explicitly, both reported once at startup and
neither fatal:

- the text is not a usable chord: `peek hotkey: '<chord>' is not a usable chord (need at
  least one modifier and one key); peek verb still works`
- another application already owns it: `peek hotkey: '<chord>' is already taken by another
  application; peek verb still works`

In both cases huddle carries on without the hotkey. The `peek` verb and the pinned
"Huddle Sessions" shortcut still open the switcher. An unavailable convenience key must
never stop the orchestrator starting. A listener that lost its chord releases its window and
ends its thread instead of parking on a hotkey that can never fire, so a candidate walk
costs one thread and one window however many chords it had to step over.

Changing it while huddle runs reports what actually happened, because the new value is
written to `huddle.json` whether or not Windows grants the chord:

```
settings peekHotkey Ctrl+Alt+J
settings: set peekHotkey = Ctrl+Alt+J
peek hotkey: 'Ctrl+Alt+J' is live now; 'Win+Alt+H' is released
```

When the chord being replaced was never granted, the message says that instead of claiming
huddle released something it never held:

```
peek hotkey: 'Ctrl+Alt+J' is live now; 'Ctrl+Alt+H' was never registered, so nothing was released
```

Setting the chord that is **already live** is answered, not refused. A chord belongs to the
whole desktop rather than to a window, so asking Windows for one huddle already holds fails
exactly like a competitor owning it; without this, re-setting the live chord named an
application that does not exist. Spacing and case do not matter, because the comparison is
between parsed chords:

```
settings peekHotkey ctrl + alt + j
settings: set peekHotkey = ctrl + alt + j
peek hotkey: 'ctrl + alt + j' is already the peek chord; nothing changed
```

A chord someone else owns leaves the running hotkey exactly as it was. The new chord is
registered *before* the old one is released, so a guess that loses costs nothing:

```
peek hotkey: 'Ctrl+Alt+J' is already taken by another application; 'Ctrl+Alt+H' is still the peek chord
```

When the previously configured chord was never granted either, the message says so rather
than claiming a chord is bound when none is:

```
peek hotkey: 'Ctrl+Alt+J' is already taken by another application; 'Ctrl+Alt+H' is still configured but was never registered, so no chord is bound; the peek verb still works
```

Text that is not a chord at all is refused before anything is registered:

```
peek hotkey: 'J' is not a usable chord (need at least one modifier and one key); 'Ctrl+Alt+H' is still the peek chord
```

The value is written to `huddle.json` in every case, so the chord the file names is the one
the next start will try, even when this run could not take it.

## The `settings` verb

```
settings                     every key: value, source, applies-when, help
settings <key>               one key, in detail
settings <key> <value>       validate and write back to huddle.json
settings unset <key>         remove the key, reverting to the built-in default
```

`settings unset peekHotkey` applies immediately too, exactly as its set path does: unsetting
is the way out of a failed chord experiment, and the candidate walk starts again there and
then.

Everything after the key is the value, spaces included — `settings backoffSeconds 2, 5, 15`
is written as `2,5,15`, not truncated at the first space.

Output names the file and the source of every value, because a setting that changes
behaviour without appearing on the surface built to answer *"what will this run use"*
recreates the failure the surface exists to remove:

```
settings — C:\Users\you\source\repos\myapp\huddle.json

  taskAckMinutes          15      default            live      unacked task escalates after this
  statsSinceDays          30      settings           live      default window for stats
  rescanIntervalSeconds   30      top-level (legacy) startup   command-inbox rescan backstop
  gitPollSeconds           5      default            startup   git activity poll interval
```

A `startup` key written while huddle is running says `takes effect on reload` rather than
pretending it is live.

## The command line

```
huddle --settings                    list, same content as the verb
huddle --set <key> <value>           validate and write
huddle --unset <key>                 revert to the built-in default
huddle --config <path> --set k v     target a specific config file
```

These are dispatched in the same position as `--claim` / `--release` / `--ledger`: **before
config load and before the console starts**, so a knob can be changed without launching the
orchestrator — from a session, a script, or a second window while huddle is already running.
Doing so does not disturb the running process; it re-reads on `reload`.

That is why **every** `--set` says `takes effect on reload`, `live` keys included. `live`
describes what a running orchestrator does with a value it re-reads; it says nothing about
this process, which holds no hotkey switch and knows no running instance. `peekHotkey` names
the one shortcut past the reload, because from inside a running huddle its verb applies the
chord on the spot:

```
> huddle --set peekHotkey Ctrl+Alt+J
set — peekHotkey = Ctrl+Alt+J (takes effect on reload, or immediately from `settings peekHotkey <chord>` inside a running huddle)
```

Because they run before the normal `--config` scan, the settings CLI performs its own scan
for `--config` / `-c` first.

Exit codes: `0` written or listed, `1` refused. Every refusal names the key and the reason,
with a did-you-mean when a catalog key is near:

```
> huddle --set rescanIntervalSecond 30
refused: unknown setting "rescanIntervalSecond" — did you mean "rescanIntervalSeconds"?
```

Writes rewrite the file from a **validated map**, so `--set` can never produce a config the
loader would reject — a config that saves but will not load is the worst outcome available
here. Every other top-level property (`sessions`, `groups`, `claudePath`, …) is carried
through untouched.

## Failure behaviour

Huddle is long-running, so the rule splits by when the problem is found:

- **At startup** — an unparseable `huddle.json`, an unknown key, or an out-of-range value
  **refuses to start**, printing *every* problem found rather than only the first. Starting
  with settings the operator does not have is worse than not starting.

  ```
  huddle.json settings refused — not starting:
    huddle.json: unknown setting "bogus" (see: huddle --settings)
    huddle.json: gitPollSeconds — 0 is out of range (1..300)
  Fix with: huddle --set <key> <value>   or   huddle --unset <key>
  ```

- **On `reload`** — `reload` is rebuild-and-relaunch, so the running process validates
  `huddle.json` **before** it exits. The same problems **refuse the reload** and leave the
  running settings in force, saying so. Killing a live orchestrator with sessions attached
  over a typo would be worse than the typo.

- **An absent `settings` block is not an error.** A config without one behaves exactly as it
  did before this existed.

- **Comments and trailing commas are accepted** — by the loader and the writer alike, using
  one shared option set. A loader stricter than the writer would let `--set` rewrite a file
  the loader then refuses. Note that a write does not preserve comments: `--set` reserialises
  the document, so a `// comment` in `huddle.json` survives until the first write.

- **A `--set` against a file that does not load is refused, not overwritten.** Rewriting
  would discard whatever else is in there along with the problem.

## Out of scope

- **Per-session settings.** `SessionDefinition` already carries its own overrides
  (`autoRestart`, `backoffSeconds`, `paths`) and is not touched by this.
- **Persona tuning.** `personas/*.json` is a separate, documented system — see
  [`persona-tuning.md`](persona-tuning.md).
- **Removing the legacy top-level keys.** They keep working and are labelled.
- **Hot-applying `startup` settings.** They are labelled, not made live. Rebuilding a
  running orchestrator's timers and watchers from under itself is its own project.
