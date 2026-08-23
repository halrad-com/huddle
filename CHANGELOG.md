# Changelog

**The real version is the source HEAD commit + build date** — that's what `ver`
reports (`<version>+<branch>.<commit>`, stamped into the assembly at build). This
file is not a parallel version scheme; it's a human-readable index of what changed,
keyed by date, with each entry anchored to the commit(s) that delivered it.

Entries are date-indexed, newest first. Each change gets a light handle of the form
`YYYY-MM-DD.N` (N resets to 1 each day) and lists its commit hash(es) — the commit
is the source of truth, the handle is just for reading.

**Append-only.** New change → same day: bump N, add at the top of today's block; new
day: start a new day block at the top of the file. Never rewrite a shipped entry.
History from before this file lives in the git commit log.

## 2026-08-23

### 2026-08-23.2 — Feature ledger phase 2: obligations become durable and automatic — `6d3eb25`, `80df445`, `94fa94e`, `a945630`, `3ccb447`, `7021576`, `2fd3fcd`, `4960e8d`, `2ddfb05`, `a0715dc`

Phase 1 made the ledger readable. This makes a dropped delegation impossible to hide.
Schema and rules in [`docs/ledger/README.md`](docs/ledger/README.md); design in
DESIGN.md, *The feature ledger*.

**Any `type:"task"` mail, from any agent to any agent, now opens a tracked row** in the
recipient's repo ledger without anyone running a command — keyed on the mail file, so a
rescan, a retry or a restart re-finds it rather than opening a second. Moving that mail
to `processed/` appends `task-acked`: acknowledgement already had a filesystem meaning,
so this reuses it instead of inventing one. That is the timestamp task tracking never
had — the audit could only approximate age-at-read from archive mtimes, with a median
polluted to zero. All four of the audit's dropped assignments were `type:"task"`, and
every one would have shown up in `ledger open --by-age` the day it was sent.

**`TaskTracker` is now a façade over the ledger.** The dictionary and the in-memory
counter are gone, so ids survive restarts — `T001` was previously issued 23 times to 23
different pieces of work — and a status update for work that really happened no longer
nacks "unknown task". Ids are repo-qualified, because numbering is per repo. `Create`
returns null when the assignee's repo has no writable ledger, and both call sites refuse
rather than hand back an obligation nothing is recording.

**Delivered and accepted are different words now.** A work-queue unit reaching Done
appends `task-delivered` and never `task-accepted`; a test pins that no sequence of queue
events can produce an acceptance by any route. Acceptance is `ledger accept <id>`, which
refuses unless the item is delivered and refuses a Deliverable whose `accepts` gate is
unnamed. That conflation is why all 13 persisted units read Done, including the AutoCal
unit the operator later found broken.

New verbs: `ledger accept <id>`, `ledger drop <id> <why>` (reason required — dropping is
how work stops existing), `ledger decline <id> [note]` (cheap and recorded; started work
is `abandoned` instead). Because huddle never rewrites `ledger.md`, hierarchy state
changes are `state` events applied as an **overlay** at read time: the State column is
the baseline, events win, latest by timestamp rather than by write order.

**Unacknowledged tasks escalate once**, past `taskAckMinutes`, to the dispatcher by mail
and to the operator's console — hung off the existing rescan tick rather than a second
timer. "Already escalated" is rebuilt from the log, so a restart does not re-announce
every old assignment at once; the surface must not become a nag.

Underneath: `LedgerWriter` is the single append path, with id allocation, 5 MB rotation
and a machine-scoped mutex so two huddles cannot interleave half a line. Ids are compared
**parsed, never as text** — `T-7`, `T-007` and `repo:T-007` are one task. A forward jump
in the task state machine is now legal (an agent that does the work and reports complete
never sent an ack), except into `accepted`, which must still come from `delivered`.

Also fixed, and first because everything else depended on it: **mail to an idle session
was a dead letter.** The wake line goes to `pending.txt`, which is drained by hooks that
fire on a turn boundary — and an idle session ends no turn. Two fix tasks sat 27 minutes
with both recipients idle until the operator injected by hand. Huddle now nudges the
console after delivery, and re-drives held wakes from the retry tick; the foreground gate
is preserved, so an operator typing is never stomped.

### 2026-08-23.1 — `stats`: what moved where, who touched it, and when — `9596588`, `8bd3dca`, `f07c409`, `3a4abd1`, `9d7c58d`, `47c6346`, `5251c5d`, `38db86f`, `8b951a6`

A new `stats [<repo>] [--who] [--since 30d|12h] [html]` verb, answering the operator's
question *"what repos are being used, when, by who"* from corpora huddle already held —
so it reports on the past week rather than only from the moment it shipped. Full
reference in [`docs/repo-stats.md`](docs/repo-stats.md).

Per repo: remotes named by identity, pushes/fetches with the last push, local and
unpushed commits with line counts, dirty files, attribution, session time, work volume
(units, mail, handoffs, open claims), and a health note for a long-running session with
nothing attributable to it. A registered root that is not a git checkout is a note, not
an error.

**Attribution is graded and always labelled.** Every agent commits as the same git
identity, so a reflog line can never say who. `exact` means a signal named the instance
(a credential request, a claim, a dispatched unit, or a movement with exactly one live
session in that root). `inferred` means roster overlap, and renders as a *list* —
two candidates stay two, never collapsed into one name.

**Remotes are named by identity, not by local name** (`RemoteIdentity`). Every repo has
an `origin`, and `myapp` carries a second remote pointing at a repo a push must
never reach, so `origin/master` identified nothing. The console movement line now reads
`[git] myapp pushed to dev.azure.com/contoso/LIB (master 97a3aa8)`. Userinfo is
stripped — the `contoso@` in the Azure URL reaches no console line, log, or page.

**`logs/git-activity.jsonl`** (new, append-only, `gitActivityLog` setting) retains what
used to be discarded: the credential drop — huddle's one exact who-signal — was deleted
the moment it was logged, and movements were console-only, so nothing survived a
restart. `gitPollSeconds` now drives the poll interval that was hardcoded at 5s.

**`stats html`** writes a self-contained `logs/stats.html` with a per-repo commit
heatmap computed from the local clone: Azure DevOps has no equivalent of GitHub's
contribution graph, some roots are not hosted at all, and this counts commits that were
never pushed. Inline SVG, no script, no CDN, light and dark both defined.

## 2026-08-22

### 2026-08-22.3 — Settings fix pass: the block now drives behaviour, and `reload` cannot kill the fleet — `610b71b`, `9a46fdf`, `25d7035`

Six findings from a `high` adversarial review of `2026-08-22.1`. Read that entry with
this one: as shipped, settings validated and displayed correctly but **did not change what
huddle did**.

- **S1 — nothing at runtime read `config.Settings`.** Program.cs, Orchestrator and
  auto-restart all read the legacy POCO properties, so `--set` changed the file and the
  `settings` display and nothing else: the precedence `.1` documents was inverted in
  practice, and the startup "using settings (x)" warning was false. Every reader of the
  nine pre-existing keys now goes through `config.Settings`; the POCO properties remain
  as the resolver's fallback input and are documented as read in exactly one place.
  Per-session overrides still win. `ResolvedSettings.IntList` reads the validated
  comma-separated text tier. *The implementation plan had no task to repoint the readers —
  a plan gap, not a stray edit.*
- **S2 — `reload` could kill huddle with children attached.** The pre-validation added in
  `.1` caught only `SettingsException`; a trailing comma raises `JsonException`, which
  escaped a bare `HandleCommand` call and took the process down — worse than before the
  guard existed. `reload` now refuses on any load failure and names it, and `610b71b` adds
  `CommandGuard` so no verb, present or future, can unwind `Main`.
- **S3 — `huddle --config <path> --set k v` was never dispatched** (the verb had to be
  `args[0]`), despite being the documented form; it fell through and silently booted a
  second orchestrator. Dispatch is position-independent now, and a `--config` value that
  spells a verb cannot hijack it.
- **S4 — two parses, two option sets.** A strict deserialize plus a lenient document parse
  plus a lenient writer meant `--set` could rewrite a commented file the loader then
  refused. One option set, lenient everywhere; genuinely malformed JSON is still refused
  by both reader and writer.
- **S5 — `settings backoffSeconds 2, 5, 15` silently wrote `"2,"`.** The verb split on
  space with max 3 and dropped the tail. It takes the whole remainder as the value now and
  stores the list canonically; bare `settings unset` reports usage instead of refusing a
  setting named "unset".
- **S6 — three copies of the `--config` scan**, one of them missing the `myapp.json`
  fallback, so the CLI and the console could disagree about which config exists. One
  `ConfigPathResolver`, shared by `Main`, `--projects-html` and the settings CLI.
- Console-verb and `reload` coverage moved out of a scratch harness into the real suite
  (`SettingsVerbTests`). 518/518.
- **Known, unchanged:** `crashLogRetention` has no consumer anywhere and did not before
  this work either — it is settable and inert, left alone rather than given an invented
  meaning. The five newer keys (`statsSinceDays`, `gitActivityLog`, `gitPollSeconds`,
  `taskAckMinutes`, `transcriptMaxScan`) have no consumer **by design**; the stats and
  ledger-phase-2 units own them.

### 2026-08-22.2 — Feature ledger phase 1: the ledger exists and can be read — `d17ddf8`..`41be7b2`

- **Why:** huddle tracked files, not obligations. The claims ledger answers *who is editing
  what right now*; nothing answered *what has this agent accepted and not delivered*, and
  nothing at all answered the operator's question — "did all the ideated things actually get
  done or not." The audit behind this (`docs/2026-08-21-task-tracking-gap-audit.md`) found
  `TaskTracker` in-memory only and reissuing `T001` 23 times, 14 completion reports nacked
  `unknown task`, peer `"type":"task"` mail creating no tracking at all, and one session
  holding four outstanding obligations — one unread for four days — while reporting
  "nothing in flight, inbox clear."
- **Read-only.** This phase parses, replays and renders; huddle writes nothing. Durable
  obligations, mail ingestion, `ledger accept`/`drop` and the `TaskTracker` repoint are
  phase 2; escalation and `status` annotation are phase 3.
- `d17ddf8` `LedgerId` — `<TYPE>-<n>` over `E S U F D T`, per type per repo, never reused;
  any digit width parses, three-digit zero-padded renders; `repo:ID` qualification with
  case-insensitive repo comparison so `Huddle:F-1` and `huddle:F-001` are one id.
- `59c04b8` the `ledger.md` parser: frontmatter plus the first pipe table, header matched
  case-insensitively by name, extra trailing columns tolerated. An unparseable row is
  **reported with its line number, never dropped** — silent loss is the failure this whole
  design exists to prevent — and `ledger` renders those errors first. Task rows are refused
  here: they live in `events.jsonl`.
- `7cca312` both state machines, forward-only, plus the `accepts` gate. `dropped`,
  `declined` and `abandoned` are terminal states, not removals. A Deliverable may not enter
  `accepted` while `accepts` is empty; huddle does not run the gate, it refuses to record
  acceptance without one being named.
- `ef22f3e` the `events*.jsonl` reader — rotation-aware (live file read last), blank lines
  skipped, a malformed line recorded as a problem rather than a crash. A task's current row
  is materialized by replaying its events in timestamp order, so out-of-order lines are
  fine; an illegal transition is reported and ignored, never applied.
- `b521993` the renderers: per-repo snapshot, `open --by-age` across every configured repo
  oldest first, the parent-nested tree, orphan tasks, and one item with ancestry, children
  and event history.
- `17601ce` `docs/ledger/README.md` (the schema scaffold, ships) and this installation's
  seeded `docs/ledger/ledger.md` (private). The playbook's "Explicitly NOT shipped" list now
  covers `docs/ledger/ledger.md` and `docs/ledger/events*.jsonl` — ledger rows name private
  repos, workstreams and operator priorities — while `docs/ledger/README.md` is added to the
  shipped-paths list **by name**, not by directory, so data can never be swept in.
- `41be7b2` the `ledger` verb: `ledger`, `ledger all`, `ledger <id>`, `ledger open
  [--by-age]`, `ledger orphans`, with `--repo` and `--owner` scoping.
- **Caveat, by design:** the age column fills for tasks, which come from `events.jsonl` —
  and nothing writes that file until phase 2. Hierarchy rows carry no timestamp in phase 1,
  so they sort after dated items and show `-`. `ledger open --by-age` is the acceptance test
  for the design; it is wired and rendering, but it cannot show real ages until phase 2
  produces events.

### 2026-08-22.1 — Settings: a validated allowlist, a `settings` verb, and a `--set` command line — `dc80d94`..`f83eb4c`

- **Why:** huddle already had nine settings, and nothing validated them. `HuddleConfig.Load`
  called `JsonSerializer.Deserialize` with no options, which silently ignores unknown
  properties — so `rescanIntervalSecond` (one missing `s`) started huddle, reported nothing,
  and ran the built-in default forever. A typo'd key was indistinguishable from a setting
  that does nothing. There was no range check either (`rescanIntervalSeconds: -5` was
  accepted), and no surface anywhere answered "what settings are actually in force."
- `dc80d94` the allowlist: one `SettingDef` record per settable knob as the single source of
  what is settable, with kind, range, and an `Applies` column (`live` vs `startup`) huddle
  needs and otherapp did not, because an operator who changes a startup knob and sees no
  effect concludes the settings system is broken. Load-time validation reports **every**
  problem, not just the first, and offers a did-you-mean on a near miss.
- `a138ec4` precedence — `"settings"` block > legacy top-level key > built-in default. The
  nine existing top-level keys keep working and are labelled `top-level (legacy)`; a key set
  in both resolves to `settings` and is reported once at startup. `HuddleConfig.Load` now
  throws `SettingsException` listing every error rather than starting on settings the
  operator does not have.
- `5445940` write-back that rewrites from a validated map, so `--set` can never produce a
  file the loader would reject; every other top-level property is carried through untouched,
  and a file that does not load is refused rather than overwritten.
- `84f8be0` `huddle --settings` / `--set <key> <value>` / `--unset <key>`, dispatched in the
  same position as `--claim` (before config load, before the console starts) so a knob can be
  changed from a script or a second window without launching the orchestrator. Exit 0
  written, 1 refused, every refusal naming the key and the reason.
- `f83eb4c` the `settings` console verb, and `reload` now validates `huddle.json` **before**
  the running process agrees to exit — killing a live orchestrator with sessions attached
  over a typo would be worse than the typo.
- Design: `docs/superpowers/specs/2026-08-21-settings-design.md`. Reference: `docs/settings.md`.

## 2026-08-16

### 2026-08-16.1 — Ledger claims: reservations without an arbitrator — `063852d`..`23770d2`

- **Why:** on 2026-08-16 huddle was down and two `myapp` agents worked the same
  files and conflicted (ISSUES.md I011). They had not skipped the claim protocol — they
  could not reach it: the protocol mailed a `claim` command to the orchestrator and
  waited for `ack:claim`, so with nothing reading `_huddle/inbox/` no claim was ever
  recorded and the two sessions were invisible to each other by construction. Huddle is
  now out of the claim write path.
- `063852d` `LedgerCli` — record-and-report: `Claim` ALWAYS writes and returns the other
  holders instead of refusing (refusing needs an arbiter alive; reporting does not);
  `Release` touches only the caller's own claims; `Describe` renders the ledger one line
  per claimed file — what is claimed, by whom, since when. `40590a8` makes the record +
  overlap scan one cross-process critical section, so two simultaneous claimants each see
  the other rather than both seeing an empty ledger. That named mutex guards
  `RecordWithOverlaps` — the path `huddle --claim` and the mail `claim` command both take —
  and **only** that path: `dispatch-batch`'s `AdvanceQueue` still pre-flights through
  `TryClaim`, which takes the in-process lock alone (deliberately frozen; the batch path
  depends on its refuse-on-conflict behaviour).
- `83db472` `huddle --claim <path>…` / `--release <path>…` / `--ledger [repo]` —
  argument modes that run the binary and never contact a running huddle, which is what
  makes a claim survive the orchestrator being down. `ac61f6d` guards release against an
  absent claims dir.
- `d469fa3` spawned sessions export `HUDDLE_CLAIMS` / `HUDDLE_INSTANCE` / `HUDDLE_REPO` /
  `HUDDLE_GUID`, so an agent types only paths; `fcf249c` keeps that export off the
  mail-hook failure path — a hook-file write failure must not silently strip a session's
  ledger identity.
- `2d6f45e` the mail `claim` command records and reports too, so the ledger means the
  same thing by either route: it always replies `ack:claim`, naming any other holder.
  **`nack:claim` no longer means contention** — it now means a malformed request only
  (bad body, unknown repo, empty file list). `23770d2` restores inline orphan reaping on
  that path.
- `personas/_shared.md` Work Coordination rewritten to the ledger protocol — read the
  ledger, claim (it always succeeds, nothing to wait for), mail the holder if one is
  named and take turns, release after committing, don't re-claim `dispatch-batch` work.
  The 2026-07-16 duplication warning (I005) stands; the mechanism under it changed.
  ISSUES.md I011 files the incident. — (commit below)
- Spec `docs/superpowers/specs/2026-08-16-ledger-claims-design.md`, plan
  `docs/superpowers/plans/2026-08-16-ledger-claims.md`.

## 2026-08-09

### 2026-08-09.8 — Official icon kit lands — (commit below)

- Operator-supplied huddle mark (seven-dot ring, small-size five-dot variant,
  transparent) replaces the generated placeholder. Full kit checked in at
  `assets/icon/` — SVG masters, PNG ladder 16..1024, windows .ico, web favicon set
  (manifest + head snippet), and `gen.py` (Pillow) that regenerates every asset from
  one geometry. `src/huddle.ico` (the exe embed) now carries the official 9-frame ico.

### 2026-08-09.7 — Console window actually shows huddle's icon — (commit below)

- The `ApplicationIcon` embed (2026-08-08.2) covers Explorer/shortcuts only — the live
  console window belongs to conhost, which keeps its generic icon unless the app sends
  `WM_SETICON`. `ConsoleIcon.TrySet()` extracts the embedded icon from huddle.exe at
  startup and sets both window icon sizes. Best-effort (Windows Terminal tabs manage
  their own icons; dotnet-run has no .exe to extract from); cosmetic failures never
  block startup.

### 2026-08-09.6 — Spawn attribution: no window surprises the operator — (commit below)

- Agent-spawned sessions announce loudly and attributed: `Orchestrator: <sender>
  spawned <repo>:<persona> [project|no-project] — task: <snippet>` (start-session and
  queue dispatch). `status` rows show `[project]` + a task snippet; a task-spawned
  session missing its stamp shows `[no-project]` — absence is information, but only
  where a stamp was expected (bare operator starts stay unadorned). `context.md` gains
  a Project line, including an explicit "(unstamped)" marker for dispatched tasks.
  Feedback source: myapp:frontenddev-2 appearing unannounced (WiiM charm v2
  dispatch) — the session self-answered via the new Task line; the operator couldn't.

### 2026-08-09.5 — `projects html`: the reproducible status page — (commit below)

- `projects html [path]` renders the lens to a self-contained HTML page (inline CSS,
  system fonts, file:// artifact links — offline by construction): per-project card
  with status pill, sprint badge, goal, artifacts, map notes/links, open claims, and
  the **usual suspects** table — agents that work/worked on the project with their
  last task-focus (live registry + crash roster + state archive, deduped, live >
  recoverable > past). Default output `logs/workspace/projects-report.html`; derived
  output, safe to delete, identical for identical inputs. This page is the output-demo
  north star for the projects feature acceptance.
- myapp gets its own real project dir: `docs/projects/oracle/` (project.md +
  ROADMAP/BACKLOG/SPRINT/ISSUES, sprint 2608-1) — dogfood content, first project in
  the lens.

### 2026-08-09.4 — Projects phase 1: the lens — `97ee0c7`..`2ae6ff4`

- **Model:** Project → typed artifacts (ROADMAP/BACKLOG/SPRINT/ISSUES per the standing
  terminology; sprints identified `YYMM-N`) → tasks. Repo layer
  (`docs/projects/<slug>/`) is standalone truth; `projects-map.json` overlays operator
  notes/links and can hold map-only slugs — it never owns identity.
- `97ee0c7` ProjectMap: tolerant frontmatter discovery across registered repos, sprint
  id/version lift, slug-conflict warnings, overlay merge.
- `7136599` project stamp: optional `project` on dispatch/start flows to sessions,
  claims (`Project:` line), state.json, and the recovery roster (`[slug]`).
- `2ae6ff4` `projects` / `project <slug>` verbs: listing + detail with artifacts
  (typed + frontmatter-declared children) wired into `open <n>`, live
  sessions/claims/recoverables derived at read time.
- 13 new tests (167 total). Spec `88e4384`+`12b8a6d`, plan `12b8a6d`. Pilot: the
  casting consolidator's deliverable becomes the first real project dir.

### 2026-08-09.3 — Oracle recovery: `recover` verb, roster persistence, seeding, dispatch discipline — `e250b5a`..`5dea3fb`

- **Dead ≠ disposable (I010):** recovery retains dead sessions as `status:recoverable`
  state entries instead of dropping the roster — the exact loss that made the
  2026-08-09 mass-termination recovery an hour of forensics. `e250b5a`
- **Declared purpose captured at spawn** + `context.md` Task line — the roster says
  what a session was for without transcript archaeology. `156fd22`
- **`recover` verb (show & pick):** lists persona, purpose (transcript fallback), last
  evidence, resume command; `recover <n>`/`all` relaunch via the resume path;
  `dismiss` archives to `logs/state-archive.jsonl` (never deleted). Startup banner
  announces "N session(s) recoverable". `b294575`
- **Coordination topology:** hubs (dispatchers, mail-waiting sessions) sort first with
  `HUB (n waiting)` marks; dispatched workers show `← dispatched by <sender>` derived
  from processed dispatch-batch files — recover coordinators before workers. `15531bf`
- **Permission seeding (F4):** startup merges the standing allow-set (`Bash(*)` +
  dedicated-tool wildcards) into every registered repo's `.claude/settings.local.json`
  — merge-only, daily backup, unparseable files untouched, `seedPermissions:false`
  opts out. Makes the 2026-08-09 prompt-spam fix durable. `200bb01`
- **Dispatch shell discipline (F5):** every orchestrator-dispatched prompt carries the
  no-compound-bash preamble; `_shared.md` rule 4 extends it to subagent dispatch.
  `5dea3fb`
- 21 new tests (154 total). Spec `cff3aec`, plan `c8f7516`.

### 2026-08-09.2 — Recovery verifies process identity; reclaim exec hardened — `2c4fa43`

- `state.json` entries persist process StartTime + image name; `Recover` verifies both
  before re-attaching, so a recycled PID can never bind a session to an unrelated process
  (I009/F1). Legacy entries fall back to a ±2min birth-window check. The opt-in
  `reclaimResourcesOnStop` executor logs the exact command, captures output, enforces a
  30s tree-kill timeout, and isolates faults per entry (F2).

### 2026-08-09.1 — `find` verb ships; claims arbiter is repo-aware — `c7e94c2`..`d01e030`, `c2d9804`

- `find <kw> [@repo] [-Nh/d/w]` searches doc bodies, session transcripts, scratchpad
  notes, and IPC mail in one grouped, numbered listing; `open`/`resume`/`history <n>`
  interop via a shared map. On-demand bounded scan — no index.
- Claims conflict matching is repo-qualified (I008): same-repo path overlap conflicts,
  cross-repo does not; paths normalized both sides; legacy repo-less claims stay
  wildcards so old locks never silently weaken.

## 2026-08-08

### 2026-08-08.2 — Application icon — `34541f7`

- huddle.exe gets its own icon (huddle-cluster mark, 7 frames 16–256px), generated
  BCL-only and wired via `ApplicationIcon`.

### 2026-08-08.1 — VT fallback: `docs`/`history` no longer print escape garbage — `2ee4999`

- Under legacy conhost nothing enabled VT processing, so OSC 8 hyperlinks rendered as
  literal escape bytes. `VtConsole.TryEnable()` at startup; when VT is unavailable,
  links degrade to plain readable titles.

## 2026-08-07

### 2026-08-07.1 — Orphan-claim reap + claim identity normalization — `9927c58`

- Claims now record the owner session's GUID; orphaned claims (owner session no longer
  live) are reaped at startup and on `conflicts` — archived, never deleted. Claim/release
  normalize the owner identity to the canonical `repo:persona` form, closing the
  underscore/colon mismatch that left dead sessions' claims permanently stuck.

## 2026-07-31

### 2026-07-31.1 — Info replies deliver quietly, not as "Stop hook error" — `03863ca`

- Ack/nack mail (`ack:claim`, `ack:release`, …) is delivered non-blocking via
  additionalContext instead of a Stop-block, so it no longer renders under a red error
  banner. Actionable mail still wakes the session; info-only mail doesn't.

## 2026-07-25

### 2026-07-25.4 — `status` shows trouble: API errors and idle time — `69e7549`

- A session whose latest transcript entry is an API error shows `[!] API: <reason>`
  (red); quiet sessions show `idle Nm` (dim).

### 2026-07-25.3 — Git auth requests name the requesting agent — `4e4e5c2`

- The credential-prompt console line carries the requesting session's short GUID, so a
  surprise auth popup is attributable to a specific agent.

### 2026-07-25.2 — `ver` reports nearest tag + commits-past — `565bda1`

- `git describe --tags --long --always` is baked into the assembly at build and shown
  by `ver`.

### 2026-07-25.1 — Git activity surfaces in the console — `12f0169`

- Pushes/fetches (remote-tracking reflog tail) and credential-prompt requests print as
  console lines. A per-session `GIT_CONFIG_SYSTEM` override runs `huddle --cred-log`
  before GCM so auth prompts are announced before they pop. Poll-based, never FSW.

## 2026-07-22

### 2026-07-22.4 — Inbox means UNREAD: read receipts + `backlog` verb — `bb2ebfe`

- Mail stays in `inbox/` until the recipient clears it (move or copy to `processed/` —
  the copy is reaped). A per-session delivered-index prevents re-announcing. `backlog`
  shows queued wake lines + unread mail per session. Replaces auto-archive-at-delivery,
  which made "delivered" and "read" indistinguishable (the failure mode behind a day of
  silently undelivered handoffs).

### 2026-07-22.3 — `focus` works: window captured at spawn — `a7e994f`

- The session's console window handle is captured at spawn while the launch title is
  still on it (Claude Code overwrites titles later). `focus <id>` raises the right
  window even with concurrent spawns.

### 2026-07-22.2 — windowed/headless status column removed — `f8c5c3e`

- Reverses 2026-07-15.3: `MainWindowHandle` is 0 for every console-attached process on
  modern Windows (the window belongs to the console host), so the column was a constant.

### 2026-07-22.1 — Mail hand-off restored: trailing-space env var — `74456d6`

- `set HUDDLE_PENDING={path} && ` baked a trailing space into the path; the hook's
  `Test-Path` missed, and every wake line queued silently for a day. Quoted assignment +
  `Trim()` + UTF-8 both directions (em dashes were mojibake).

## 2026-07-21

### 2026-07-21.1 — Mail wake lines via Stop-hook pending queue — `aaf0814`

- Wake delivery moved to a Claude Code hook + per-session `pending.txt` queue: mail
  arriving between turns drains into the next turn instead of depending on console
  injection timing.

## 2026-07-17

### 2026-07-17.2 — Tier 0 shell discipline — `936b8ad`

- `personas/_shared.md` Shell Discipline section (prefer dedicated tools; single-purpose
  allowlist-shaped Bash; no interpreters) + arbitrary-code wildcards removed from the
  local allowlist.

### 2026-07-17.1 — Injection no longer stomps operator typing — `5fbfb70`

- `PromptInjector` holds off when the target console is foreground and the operator is
  active (`OperatorBusy`: foreground + input within 180s); held nudges retry ~4s via a
  quiet timer. Explicit `say` still forces.

## 2026-07-16

### 2026-07-16.5 — Model floor re-pinned: opus + high for every persona — (commit below)

- `personas/_shared.json` pins `claude-opus-4-8` / effort `high` as the inherited floor.
  Sidecars had been `{}` since `5157b0d` unpinned them (Jun 20) — every agent ran on the CLI
  default model. Operator rule: never Sonnet/Haiku for huddle sessions; overrides only move up.

### 2026-07-16.4 — `history` verb: browse and resume past sessions — (commit below)

- `history [@repo] [kw] [-Nh/d/w]` lists past sessions from Claude transcripts, newest first
  (title · repo · last activity · files touched); `history more` pages; `history <n>` shows
  detail (opening prompt, where it left off, files written — wired into `open <n>`);
  `resume <n>` reopens the conversation in its cwd, refusing sessions that are still live.
  Implements docs/superpowers/specs/2026-07-14-session-history-verb-design.md (built for the
  first time — previously spec-only despite being reported "done").

### 2026-07-16.3 — Honest stop: no ack until the process is dead — `cfd863a`

- `stop-session` / `stop` no longer report success on a failed kill. The session reverts to
  Running, keeps its claims, and the command nacks with the PID. Previously a surviving agent
  kept working untracked while its claims were released (I007).

### 2026-07-16.2 — Runtime `claim` command: the arbiter covers every path — `0138d88`

- Any session must claim its file scope before substantive edits (`claim` command; mandate in
  `_shared.md`), not just dispatch-batch workers. Atomic check-and-write (`TryClaim`); nack names
  the holder. Include the plan doc's path to lock a whole plan. Queue dispatch acquires through the
  same arbiter, so batches and runtime claimants can't collide. Closes I005 — the 2026-07-16
  same-plan-twice collision that corrupted a product file.

### 2026-07-16.1 — Singleton guard: one huddle per root — `6d4bff1`

- A second huddle.exe started on the same root now prints why and exits instead of silently
  double-executing every inbox command (two spawns per dispatch, context.md ping-pong — I006,
  the amplifier of tonight's incident).

## 2026-07-15

### 2026-07-15.4 — Declared doc links resolve correctly — `606ee45`

- A declared doc link written markdown-style — relative to the scratchpad file, e.g.
  `../../repo/docs/x.json` — now resolves. It was resolved only against the repo root, landing
  at a bogus path so `open` failed with "cannot find the file". Now it tries the scratchpad
  directory first (markdown convention), then the repo root, using whichever exists.

### 2026-07-15.3 — status shows windowed/headless — `558bb3b`

- `status` now marks each running session `windowed` or `headless` (based on whether its
  process has a top-level window handle). Headless sessions can't receive console-injected
  prompts/nudges, so surfacing them matters. (`0290aa0` also: the resilient doc scan now logs
  when it skips an unreadable directory, so a skip is visible in `huddle.log`.)

### 2026-07-15.2 — Resilient doc discovery — `92304a4`

- Doc discovery no longer drops a whole repo's docs when a subdirectory scan throws (a dir
  removed/renamed mid-walk while another session writes, or a reparse cycle). A per-directory
  walk isolates the bad dir, so a doc on disk stays visible instead of vanishing from one
  `docs` run and reappearing on the next.

### 2026-07-15.1 — janitor surfaces stale mail — `c5541da`, `87c5ff0`

- `janitor` gains a stale-mail section: unprocessed mail still sitting in `ipc/*/inbox/` is
  reported as "old business" for review — mail to a stopped recipient is surfaced (task-type
  flagged as possible dropped work), and mail waiting for a running agent is counted.
  Report-only; moves nothing. Surfaces the dropped-work failure mode where a task pinned to a
  dead instance rots unseen. Stage 1 of the "nothing halted-incomplete or dropped" fix.
- Adds `tests/test-janitor-stale-mail.ps1` — first v1 regression capture test.

## 2026-07-14

### 2026-07-14.3 — Clarify: commit-when-ready is not gated — `6c0e1fb`

- Refines 2026-07-14.2, which read too broadly as "don't commit autonomously" (an agent
  sitting on finished work is work halted-incomplete). Committing your own finished work to
  master with explicit paths stays encouraged; only `git push` and `git add -A` are gated to
  an explicit operator instruction.

### 2026-07-14.2 — Guard against autonomous push — `71a0f62`

- `personas/_shared.md` never-push rule now explicitly overrides repo-local "just commit
  and push" habits (project memory / CLAUDE.md / churn rules): when running autonomously,
  unattended, or on restart-recovery with no operator command, agents must report a dirty
  tree and wait — no `git add -A`, no push. Fixes the root cause of myapp sessions
  autonomously pushing churn (including another session's edits) to origin after a restart.

### 2026-07-14.1 — Shutdown confirmation + durable log — `c4db626`

- Every teardown path (`shutdown` command, Ctrl+C, EOF/stdin-closed) now confirms
  `N session(s) running… (y/N)` before stopping anything — closes the footgun where a
  single stray Ctrl+C tore down every worker session with no prompt.
- `ConsoleUI.Log` tees to an append-only `logs\huddle.log`; the raw command line is
  recorded on entry and each shutdown logs its trigger and reason, so an abnormal
  teardown is reconstructable instead of scrolling away with the console.

## 2026-07-12

### 2026-07-12.1 — Session resume GUID surfacing — `2dfd915`, `9dd8f09`

Every session is already assigned a `--session-id` GUID at spawn — the token
`claude --resume <guid>` needs — but it was only logged transiently and held in
memory. It is now surfaced three ways off one definition
(`SessionInstance.ResumeCommand`):

- the spawn console log prints the copy-paste-ready `claude --resume <guid>` plus
  the repo root to run it in;
- each session block in `context.md` gains a `- **Resume:**` line;
- a new `resume <instance>` verb reopens a session's conversation in a fresh console
  with the working directory set to its repo root (Claude keys session storage by
  cwd).

The GUID is persisted to `state.json` and restored on recovery, so sessions keep
their resume line across a huddle restart. The `resume` verb refuses a session that
is still running (two live writers on one transcript would fork and corrupt it) — it
is for stopped or crashed sessions only.
