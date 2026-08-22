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
