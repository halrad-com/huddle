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

`backoffSeconds` is text holding a comma-separated list because there are three kinds and an
int array is not one of them. Adding a fourth kind for a single setting costs more than
parsing one string, and that parse is validated at load like everything else.

## The `settings` verb

```
settings                     every key: value, source, applies-when, help
settings <key>               one key, in detail
settings <key> <value>       validate and write back to huddle.json
settings unset <key>         remove the key, reverting to the built-in default
```

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
