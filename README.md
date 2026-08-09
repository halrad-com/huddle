# Claude-Huddle

A single-operator console for running a fleet of Claude Code sessions on Windows, each tuned to a role by a swappable persona prompt and an optional JSON tuning sidecar that sets model, effort, tool fence, MCP scope, and plugin scope. Sessions coordinate through a file-based IPC layer that does messaging, file-claim locking, and plain-English task dispatch — hand a job to your architect persona and it plans and fires the sub-sessions itself, with declared file scope and automatic conflict serialization (overlapping work queues instead of colliding). One place to hand off the work, instead of a fleet of consoles to juggle.

Windows-native. Offline. No cloud, no CDN, no external API at runtime. Each session is a real `claude.exe` in its own console window — if one crashes, the rest keep working.

> **What changed recently?** See [`CHANGELOG.md`](CHANGELOG.md) — date-indexed, newest first. There are no versioned releases: you clone the repo and build, so the real version is your source HEAD + build date (`ver` reports it).

---

## What It Is

Three things in one binary:

1. **A prompt tuner.** Each session is a repo + a persona — a markdown role file appended to Claude's system prompt that decides what the session is *for*. Shipped roles: `architect` · `reviewer` · `frontenddev` · `backenddev` · `documenter` · `versioner` · `researcher`. Each persona can pair its `.md` with an optional `.json` sidecar that pins model (Haiku/Sonnet/Opus), effort level, tool fence (e.g. architect literally cannot Edit), MCP whitelist, and plugin scope. Define repos once in `huddle.json`, then `start myapp architect` opens a Claude Code session in myapp under an architect persona; `start docs-site documenter` launches a documenter in docs-site. Personas are ordinary `.md` files in `personas/` — write your own.

2. **A crash wrapper.** Claude Code runs on Bun, and Bun panics — it takes the whole console window with it when it does. Huddle runs each session in its own `conhost.exe` so a crash kills one window, not your workspace; optional auto-restart with escalating backoff brings it back via `--continue`. And recovery isn't just chat-history resume — sessions write a work-ledger entry (what they're on, which files they're touching) and timestamped scratchpad checkpoints as they go, so the restarted agent comes back with task-specific context, not just where it was in the conversation.

3. **An orchestration console.** Sessions coordinate through a file-based IPC layer that does three things: messaging (sessions drop JSON into each other's inboxes), file-claim locking (a session declares the files it's editing so two agents can't stomp on the same code), and task dispatch (hand a plain-English task to your architect persona and it plans and fires sub-sessions, with declared file scope per task and a work queue that serializes conflicting or dependency-ordered units instead of rejecting them). Commits become the natural release signal. When a session stops (cleanly or from a crash), its claims are auto-released with a git-diff audit of what actually changed.

If you're running one Claude session at a time, huddle is overkill. If you're running three or more sessions in parallel, the coordination story is the feature.

---

## Features

### Key differentiators

What you won't get from a terminal full of `claude` tabs:

- **Crash containment for the Bun panic.** Claude Code runs on Bun, and Bun takes the whole console window down when it panics. Huddle runs every session in its own `conhost.exe` — one crash kills one window, never your workspace or your other agents. Nobody else solves this on Windows.
- **Human stays the conductor.** Orchestration is operator-in-the-loop, not headless AI-to-AI. You hand off work and watch it happen; you can `stop` or `broadcast` to redirect mid-flight. No autonomous swarm running unsupervised.
- **Agents can't stomp each other.** File-claim locking plus `dispatch-batch` (declared file scope per task) means two sessions never quietly edit the same code. Overlapping or dependency-blocked work is *serialized* — it queues and waits its turn instead of being discovered in a merge. (Self-overlap within a single batch is still refused up front as a caller error.)
- **Hard role fences, not vibes.** Personas tune model, effort, and an enforced tool boundary — the architect literally *cannot* Edit or Write; the reviewer can't modify code. Roles are guaranteed by the harness, not by hoping the prompt holds.
- **Offline, Windows-native, zero runtime dependencies.** No cloud, no CDN, no external API, nothing beyond the .NET BCL. Your orchestration layer works on a plane.
- **Plain-English dispatch.** Type a task in English and your architect persona plans it and fires the sub-sessions itself, with per-task file scope and automatic conflict serialization (overlapping work queues instead of colliding).

### Key capabilities

- **Sessions & personas** — one-command repo launch with aliases and named groups; seven shipped roles (`architect`, `reviewer`, `frontenddev`, `backenddev`, `documenter`, `versioner`, `researcher`); per-persona JSON tuning for model / effort / tool fence / MCP scope / plugin scope; write your own personas as plain `.md` files.
- **Resilience & recovery** — per-window crash isolation; optional auto-restart with escalating backoff; `--continue` chat resume *plus* work-ledger entries and timestamped scratchpad checkpoints so a restarted agent comes back with task context, not just conversation history.
- **Coordination** — file-based IPC messaging between sessions; `broadcast` to the whole fleet; `delegate` with task tracking; Work Ledger + orchestrator claims with a unified `conflicts` view; auto-release of claims on stop with a git-diff audit of what actually changed.
- **Orchestration** — `direct` hands a task to the architect to plan and fire; `dispatch-batch` does multi-session dispatch with declared file scope, `dependsOn` ordering, and a work queue that serializes conflicting units (view it with `queue`); commits become the natural release signal.
- **Visibility** — `status` (live, color-coded), `progress` (last checkpoint per session), and the **document log** (`docs` / `open`) — a leveled, quiet-by-default, clickable list of artifacts your sessions produce; optional statusline integration.
- **Quality gate** — `replay` runs a repo's captured regression tests (MBXHVAL invariant suites) that agents commit *into the repo* as a byproduct of verifying their work.
- **Scales by location, not rewrite** — the same model runs one session, many sessions on one machine, or many sessions across many machines. Because IPC is just files, going multi-machine is identical to single-machine multi-agent — you simply put `ipc/` on a shared file share every box maps to the same drive letter. See [first-run guide](docs/first-run.md#scaling-one-instance-up-to-many-machines).
- **Surfaces** — the console REPL plus a PowerToys Command Palette extension for huddle's daily verbs — optional; see [setup/setup-cmdpal.md](setup/setup-cmdpal.md).
- **Operations** — `reload` rebuilds and relaunches huddle *without killing your running sessions*; everything is driven by a single `huddle.json`.

---

## Why Huddle

- **Crash protection that resumes, not just restarts.** Huddle began life as *myapp*, a crash wrapper for Claude Code sessions — your myapp against AI whiplash. It grew into something better: crashed sessions are isolated so they can't take anything else down, and the work ledger + scratchpad convention means a restarted agent picks up mid-task *with its context*. Watch a crash, type `restart`, and it carries on where it left off.
- **Human console AND agent-to-agent coordination — both, not either.** Most tools pick headless AI-to-AI or a human dashboard. Huddle is a human operator's console over a live inter-agent mail system.
- **Fully auditable AI-to-AI communication.** Every message between agents is a JSON file on disk — inspectable, greppable, replayable. No opaque channels.
- **Capture-to-test replay engine.** Agents freeze their verifications into regression suites as they work; `replay <repo>` re-runs the accumulated suite against a live instance. Verification becomes an asset, not an event.
- **Documentation enumeration built in.** The `docs` verb tracks every artifact agents produce across repos — declared deliverables, plans, and doc churn — so documentation stays findable instead of scattered.
- **Offline-first, Windows-native.** No cloud, no CDN, no runtime dependencies beyond .NET.

---

## Who This Is For

- Solo developers juggling several repos or several roles on one repo and tired of losing track of which window is which.
- Anyone running parallel Claude Code sessions who has had one crash, one silently get stuck, or two agents quietly stomp on each other's files.
- People who want a human-in-control orchestration layer — not headless AI-to-AI coordination — and want it offline on Windows.

---

## Quick Start

**Guided setup (recommended):**

```
git clone https://github.com/halrad-com/huddle.git
cd huddle
claude          # then type: /setup
```

An agent interviews you, registers your repos, tunes personas and permissions,
and finishes with huddle running and your repos registered. Manual path below,
full reference in [docs/first-run.md](docs/first-run.md).

```
# Build (first time — needs .NET 8 SDK)
build.cmd

# Or run directly from source
dotnet run --project src/huddle.csproj

# Run the published exe from the repo root
publish\huddle.exe
```

On startup huddle reads `huddle.json`, registers every repo, and gives you a prompt:

```
=== claude huddle ===
Claude Code session orchestrator

>
```

No sessions launch automatically unless `autoStart: true` is set in the config. You choose what to open.

```
> repos                         # see what's registered, including aliases
> personas                      # see available roles
> start app architect           # launch architect in myapp (alias: app)
> status                        # see what's running
> direct clean up the auth flow # hand a task to huddle:architect; it plans + fires
> conflicts                     # see which files are currently claimed
> docs                          # list artifacts sessions created (clickable)
> stop app:architect            # stop a specific session
> shutdown                      # stop everything and exit
```

`app` is an alias for `myapp`. Aliases work anywhere a repo name is accepted.

> Setting up a fresh clone? See the [First Run guide](docs/first-run.md) — prerequisites,
> config from `template.json`, and how the same setup scales from one machine to several
> over a shared file share.

---

## Why It Exists

**Without huddle:** open a terminal, `cd` to the repo, run `claude`. Work for 20 minutes. Bun panics. Window gone, context gone, work gone. Open a new terminal, `cd` again, `claude`, `/resume`, hope.

**With huddle:** `start app` opens a session in its own window. Bun panics, that window dies. The huddle prompt stays up and shows:

```
*** CRASH *** myapp exited with code 3
```

`restart app:architect` brings it back with `--continue`. Other sessions you had running? Untouched. If auto-restart is on, huddle does the restart for you with backoff.

That covered the original problem. Everything else — personas, IPC, tasks, claims — is the orchestration layer that grew on top.

---

## Core Concepts

### Sessions and instance IDs

One running Claude Code process = one **session**. Each session has a display identity of the form `repo:persona`, e.g. `myapp:architect`. That's what you'll see in the prompt, in the status command, in log files, and on the statusline.

### Personas

A persona is a markdown file in `personas/` that gets appended to the Claude system prompt when a session starts. It shapes what the session is for: architect thinks about design and orchestrates; backenddev and frontenddev build; reviewer reads and critiques but doesn't modify; documenter writes docs; researcher scouts and proves concepts, then hands off. The shipped set:

| Persona | Tuning | What it does |
|---------|--------|--------------|
| `architect` | Opus, high effort, no Edit/Write | System design, trade-off evaluation, sub-delegation. Owns orchestration planning. |
| `frontenddev` | Sonnet, high effort | HTML/CSS/UX. Firefox-first. Accessibility built in. |
| `backenddev` | Sonnet, high effort | Server-side code, APIs, data layer. |
| `reviewer` | Sonnet, high effort, no Edit/Write | Reads and critiques — finds bugs, security issues, edge cases. Does not modify. |
| `documenter` | Haiku, low effort, bare | Writes and maintains docs. Heavily guarded against unsolicited changes. |
| `versioner` | Haiku, low effort, bare | Version bumps across a repo using recipes. |
| `researcher` | inherits defaults | R&D: web research, synthesis, PoC spikes in a `labs` repo, subsystem deep-dives into `docs/reference/`. Hands findings to the architect; never integrates its own work. |

Personas are ordinary `.md` files paired with optional `.json` tuning sidecars. `personas/_shared.json` carries defaults (Sonnet, medium effort) that each persona's JSON overrides. Add your own by dropping new files. See [`docs/persona-tuning.md`](docs/persona-tuning.md) for the schema.

### Inter-session communication

Sessions talk to each other and to huddle through JSON files under `ipc/`. Each session has an inbox. Huddle has an inbox at `ipc/_huddle/inbox/` where orchestration commands land.

Available commands agents can send to `_huddle`:

| Command | What it does |
|---------|--------------|
| `start-session` | Spin up a new session |
| `stop-session` | Stop a running session |
| `delegate-task` | Send a task to a specific session (auto-starts if needed) |
| `task-complete` / `task-failed` / `task-progress` | Update task status |
| `broadcast` | Fan out a message to every live session, or only a given repo's agents (optional `"repo":"name-or-alias[,more]"`) |
| `dispatch-batch` | Multi-session dispatch with declared file scope and optional `dependsOn`; units that overlap active work or wait on a prerequisite are queued and dispatched when the conflict clears |
| `release` | Hand back claimed files after committing (advances the queue) |

### Work Ledger and claims

When two sessions need to edit overlapping files at the same time, they stomp on each other. The Work Ledger solves this at two levels:

- **Freeform ledger** (`ipc/workledger/<session>.md`) — each active session writes what it's working on and which files it's touching. Agents check this before they edit; a prompt convention. Useful for long-running sessions that announce scope declaratively.
- **Orchestrator claims** (`ipc/workledger/claims/<batch>-<session>.md`) — when `dispatch-batch` spawns a session, huddle writes a structured claim file itself with the session's declared file scope. Auto-released on session stop with a git-diff audit that logs scope creep and uncommitted-dirty files.

#### Conflict-guarding: serialize, don't reject

`dispatch-batch` feeds a declarative **work queue** rather than rejecting conflicts outright. Each task becomes a *work unit* with its declared `files` and optional `dependsOn` (prerequisite unit ids). A unit dispatches as soon as its files overlap no currently-active unit **and** every `dependsOn` is done; otherwise it stays queued and dispatches later — when an active unit's claims release (on `release` or session stop) or a prerequisite finishes. So overlapping or dependency-blocked work *waits its turn* instead of being refused.

What still nacks the batch up front (caller errors, not transient conflicts): self-overlap within one batch (two tasks claiming the same file — declare a `dependsOn` instead), the same `repo:persona` named twice, an unknown repo/persona, a duplicate unit id, an unknown `dependsOn`, or a dependency cycle.

The queue persists under `ipc/workledger/queue/` (one JSON file per unit) so it survives a huddle restart. *v1 scope:* the queue serializes against other queued/dispatched units only — it does not block a unit against files held by a plain `start`/`direct` session.

Run `conflicts` anytime to see claim overlaps, and `queue` to see the work queue (active / queued / done / failed).

### Resource ledger (leak tracking)

Files aren't the only thing sessions contend over — they also spawn OS resources: headless browsers for UI probes, servers, temp profiles. Anything that outlives a single command gets registered in `ipc/resledger/<safe-name>.json` with the literal cleanup command, torn down in a finally, and marked `cleanedAt` once verified dead. Huddle reports leaks when a session stops and on demand via `janitor`; `scripts/sweep-orphans.ps1` (add `-Kill` to reclaim) also catches *unregistered* strays like orphaned headless browsers. Auto-reclaim on stop is opt-in: `"reclaimResourcesOnStop": true` in `huddle.json` (default false — report-only). Spec: [docs/resource-ledger-spec.md](docs/resource-ledger-spec.md). Born from a real incident: an orphaned headless browser haunted the desktop as a click-through ghost window, on and off, for weeks.

### `direct` — hand tasks to architect

Instead of typing `start`/`delegate` commands yourself, type a task in English:

```
> direct refactor the auth flow to use the new token service
```

Huddle routes the task to `huddle:architect`'s inbox with `autoFire: true`. The architect persona's prompt tells it to plan, narrate briefly, and use `dispatch-batch` to fire sessions without asking for confirmation. You intervene via `stop` or `broadcast` if you disagree with what it's doing.

This collapses "ask architect what's best → copy the plan out → type it in" into one command. Architect stays the brain; you stop being the relay.

### Persona tuning

Every persona can pair its `.md` prompt with a `personas/<name>.json` sidecar:

```json
{
  "model": "claude-opus-4-7",
  "effort": "high",
  "disallowedTools": ["Edit", "Write", "NotebookEdit"]
}
```

That makes the architect persona physically incapable of editing code at the tool layer — not just instructed not to. Conversely, the `documenter` persona pins Haiku with the `--bare` flag (no skills, no hooks, no auto-memory), so doc edits stay light. New personas are five-line JSON files; no code changes needed.

See [`docs/persona-tuning.md`](docs/persona-tuning.md) for the full schema, merging rules, and examples.

> **Removed 2026-06-21:** an earlier iteration added cost telemetry (JSONL tailer, per-turn USD rollups, `cost` verb, `budgetUsd`/`budgetAction` soft caps). Removed because it added moving parts without changing how the operator actually worked.

### Commit-then-release idiom

Once a session is working on claimed files, it follows this loop:

1. Do a logical unit of work.
2. Commit with a message that says *why*, not just *what*. Commit messages become the decision trail.
3. Send a `release` IPC to huddle naming files it's done with. Empty claims auto-delete.
4. Move to the next unit (or stop).

If a session stops without releasing (crash or user action), huddle auto-releases remaining claims and logs warnings about anything left dirty in the working tree.

---

## Command Reference

Run `help` in huddle for the live version. Current commands:

| Command | What it does |
|---------|--------------|
| `start <repo> [persona] [prompt]` | Launch a session. Aliases work. Optional persona and opening task prompt. |
| `stop <instance\|repo>` | Stop one session (by instance ID) or every session of a repo (by name or alias). |
| `restart <instance>` | Restart a specific session. Keeps the last persona. Uses `--continue` for crash recovery. |
| `resume <instance>` | Reopen a stopped/crashed session's conversation (`claude --resume <session-id>`) in a fresh console at the repo root. Refuses if the session is still running. |
| `repos` | List every registered repo with purpose and aliases. |
| `personas` | List every available persona. |
| `status` | Show every active session with status, uptime, and working directory. Color-coded. |
| `send <instance> <msg>` | Drop a message into a session's inbox. |
| `messages <instance>` | Read messages currently sitting in a session's inbox. |
| `huddle <group>` | Launch a named group of sessions defined in `huddle.json`. |
| `delegate "desc" to <instance>` | Send a task to a session. Auto-starts if needed. Tracks it. |
| `direct <english task>` | Hand a task to `huddle:architect` with auto-fire. Architect plans and dispatches. |
| `broadcast [@repo[,repo]] <subject> <msg>` | Fan out an informational message to every live session — or only the named repos' agents (`@app` works with aliases; unknown repo is rejected loudly). |
| `shell [<repo>] <data>` | Hand `data` to the OS shell (opens files/URLs). Optional repo sets CWD. |
| `tasks` | Show tracked tasks with state and assignee. |
| `progress` | Show last scratchpad checkpoint for each running session. |
| `conflicts` | Show file claim overlaps across sessions — freeform ledger and orchestrator claims both. |
| `janitor` | Report leaked session resources — resource-ledger entries never marked cleaned whose process is still alive. |
| `queue` | Show the dispatch-batch work queue — active / queued (with what they wait on) / done / failed. |
| `docs [plans\|logs]` | List documents sessions created, newest first. Default shows Docs (deliverables); `plans` adds Plans; `logs` adds git working-tree churn. See [Document log](#document-log). |
| `open <n>` | Open the nth document from the last `docs` listing via the OS file handler. |
| `find <kw> [@repo] [-Nh\|-Nd\|-Nw]` | Content search across doc bodies, session transcripts, scratchpads, and IPC mail — grouped hits, `open <n>` / `resume <n>` interop. |
| `history [@repo] [kw] [-Nh\|-Nd\|-Nw]` | List past sessions from transcripts; `history <n>` for detail, `resume <n>` to reopen. |
| `resume <instance\|n>` | Reopen a stopped session's conversation (`claude --resume`) in its repo root. Refuses live sessions. |
| `backlog` | Per-session queued wake lines + unread mail, oldest first — who is sitting on what. |
| `focus <instance>` | Raise a session's console window (handle captured at spawn). |
| `recover [n\|all\|dismiss n]` | List sessions lost to a crash — persona, declared purpose, last evidence, hubs first — and relaunch them show-and-pick. Dismissals archive, never delete. |
| `projects [html [path]]` | List projects discovered from `docs/projects/<slug>/` across repos (+ map overlay). `projects html` writes a self-contained status page — the reproducible output report. |
| `project <slug>` | Project detail: goal, sprint, artifacts (wired into `open <n>`), live sessions, claims, recoverables. |
| `replay <repo>` | Run the repo's captured regression tests (MBXHVAL capture suites) via `mbxhval`. See [Capture replay](#capture-replay). |
| `scan` | Re-scan huddle's inbox for any commands the watcher missed. |
| `ver` | Show huddle's version (branch, commit, build time). |
| `reload` | Rebuild huddle and relaunch (advanced — confirms first). Exits gracefully; child sessions keep running. |
| `quit` | Exit huddle. Running sessions keep running. |
| `shutdown` | Stop every session, then exit. |
| `help` | Show the command reference. |

---

## Document log

`docs` is a leveled, quiet-by-default log of the artifacts your sessions produce — design
specs, plans, reports, READMEs — newest first and clickable to open. It is **read
on-demand** (no background watcher) and populated by what sessions **declare**, not by
scanning every file they touch.

Three levels — the name you see is the name you type, no opaque codes:

| Verb | Shows | Source |
|------|-------|--------|
| `docs` | **Output** — human deliverables (specs, design docs, reports, READMEs) | Declared by agents + auto-discovered repo docs |
| `docs plans` | Docs **+ Plans** — planning docs (plans, roadmaps, ideas) | Declared + auto-discovered |
| `docs churn` | Docs + Plans **+ Churn** — git working-tree changes, source included (binaries/build output filtered out) | git report, on demand |

Output/Plans are **declared**: an agent records a deliverable in a `## Documents` section of
its scratchpad (the convention lives in `personas/_shared.md`):

```
## Documents
- [Document Log design](docs/superpowers/specs/2026-06-27-document-log-design.md) #output
- [Document Log plan](docs/superpowers/plans/2026-06-27-document-log.md) — impl plan #plans
```

Because declarations are parsed straight from scratchpads — not from git — **gitignored
artifacts still appear** (a report dropped under `logs/`, say), as long as the agent
declared it. The huddle repo's own `docs/**/*.md` and root `*.md` are also auto-discovered
and merged in (declarations win on dedupe, keeping their richer titles). Churn is the
opposite: it comes from `git status`, so it only ever lists tracked working-tree changes
and never appears in the default `docs` view.

Each entry is numbered and rendered as an OSC 8 hyperlink. Click the title in a terminal
that supports it, or use the always-available fallback:

```
> docs
    1. [Output] Document Log design   huddle:architect-2   2026-06-27 13:04
> open 1                          # opens via the OS file handler
```

Nothing is stored separately — the scratchpad files are the store, parsed fresh each call.
The list is empty until sessions start declaring artifacts; pre-existing docs are not
back-filled.

## Capture replay

`replay <repo>` runs a repo's **captured regression tests** against its live test instance and reports pass/fail. The captures are MBXHVAL YAML suites (an HTTP-API validation runner; repos without it can plug in any runner via `replayCommand`) that agents emit as a byproduct of verifying their work (the capture-to-test protocol in `personas/_shared.md`) and commit *into the target repo* alongside the code they test — so the test ships with the change and the "store" is just git.

The whole point is that verification doesn't evaporate. When an agent confirms "search by album artist works" by hitting the endpoint once, that one-off check becomes a permanent gate:

```
agent verifies a change through an HTTP endpoint
  └─ writes an MBXHVAL capture suite (an invariant gate on what it just checked)
       └─ commits it WITH the code change          ← the test ships with the feature
            └─ operator runs `replay <repo>` later  → the invariant is re-asserted on demand
```

Mechanically, `replay myapp`:

1. Looks for capture suites in `<repo-root>/MBXHVAL/tests/suites/captures/`.
2. Shells out to the `mbxhval` binary set in `huddle.json` (`mbxhvalPath`), pointed at the repo's `replayHost`/`replayPort`:
   ```
   mbxhval validate --suite-dir <captures> --host <host> --port <port> --report json --no-quality
   ```
3. Parses the JSON report and prints `ALL GREEN — N/N passed` or `M FAILED — …`.

`mbxhvalPath` may be a published `mbxhval.exe` or an `mbxhval.dll` (run via `dotnet <dll>`). If it's unset, `replay` tells you to set it and does nothing. `replay` has a **minimum `mbxhval` version**: the binary must support `--suite-dir` and the `each:`/`all:` invariant operators.

### Two replay models — pick per repo

**HTTP endpoint testing** — for repos exposing a network API. Captures are YAML suites run by [MBXHVAL](https://github.com/halrad-com/MBXHVAL) (build it, set `mbxhvalPath` in huddle.json). Suites live in `<repo-root>/MBXHVAL/tests/suites/captures/`; the repo entry sets `replayHost`/`replayPort`.

**Native testing** — for everything else (CLIs, libraries, file-processing tools). The repo entry sets `replayCommand`: any command that runs your tests and writes `{"summary":{"total":N,"passed":N,"failed":N,"skipped":N}}` to the path huddle substitutes for the literal `{output}` token. Exit 0 = green, 1 = failures, 2 = config error. Your existing test harness qualifies if you can wrap it in a script that emits that summary.

You may not need MBXHVAL at all — it exists for network endpoint testing.

### Adding replay to your project

1. Pick the model above and add the matching keys to the repo's entry in `huddle.json` (`replayHost`/`replayPort`, or `replayCommand` — see template.json's `example-cli-repo` for a worked native example).
2. Create the captures home: `MBXHVAL/tests/suites/captures/` for the HTTP model, or wire your runner script for the native model.
3. Agents working in the repo follow the capture-to-test protocol in `personas/_shared.md`: every verified behavior gets frozen as a capture in the same commit as the change it verifies.
4. `replay <repo>` from the huddle console runs the accumulated suite.

### Anatomy of a capture suite

A capture is a small YAML file in the repo's `captures/` dir. Each test names an endpoint, the expected status, and **only the fields the verification actually cared about**:

```yaml
tests:
  - id: CAP-search-albumartist
    name: search by album artist returns only that album artist
    method: GET
    endpoint: /api/search?q=albumartist:Miles%20Davis
    expectedStatus: 200
    expectedFields:
      data.results[*].albumArtist: each:contains:Miles Davis
```

Volatile fields (scores, timings, ids) are left unlisted — **unlisted means not asserted**, so the suite doesn't go red when unrelated data changes.

### Invariant gates (`each:` / `all:`)

The `each:` / `all:` operator asserts a property across *every* element of an array, so the gate holds for **any data state** rather than a frozen snapshot. Match the endpoint's real contract — under-asserting is safe, over-asserting rots:

| Endpoint behaviour | Gate to use |
|--------------------|-------------|
| Exact filter (every result must equal the term) | `each:exact:<value>` |
| Fuzzy / ranked match | `each:contains:<value>`, or just assert the first result |

An overstated gate — asserting `exact` on a ranked search that legitimately returns near-matches — is a false-red waiting to happen. Gate the contract the endpoint actually promises, nothing tighter.

### Reading the result

| huddle prints | What it means |
|---------------|---------------|
| `replay <repo>: ALL GREEN — N/N passed` | Every capture held. |
| `replay <repo>: M FAILED — N/total passed` | A gate broke — either a real regression, or an overstated gate (check which before "fixing" the code). |
| `no report written (exit …) — is the test instance at host:port running?` | The runner couldn't reach the instance. Start your app's test instance and retry — this is **not** a test failure. |

---

## Statusline

`scripts/statusline.ps1` is a PowerShell statusline for Claude Code. It renders:

```
[repo:persona] branch | model | ctx:N%
```

Identity resolves in this order:

1. `CLAUDE_SESSION_LABEL` env var — what huddle sets when it launches a session (always the full `repo:persona`).
2. `session_name` in the Claude statusline JSON.
3. `CLAUDE_PERSONA` env var combined with the repo basename.
4. Bare repo basename.

Context color-coded: green above 50%, yellow 20–50%, red below 20%.

Install:
```
copy scripts\statusline.ps1 %USERPROFILE%\.claude\statusline.ps1
```

Then in `%USERPROFILE%\.claude\settings.json`:
```json
{
  "statusLine": {
    "type": "command",
    "command": "powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:/Users/<you>/.claude/statusline.ps1"
  }
}
```

Restart the session — `statusLine.command` is not hot-reloaded.

### Why PowerShell

Windows PowerShell 5.1 ships with Windows, parses JSON natively, and needs nothing installed. An earlier bash version needed `jq` on PATH, which wasn't always present, so the label degraded to `[?]`. Zero external dependencies is the point.

---

## Command Palette Extension

Companion surface: a PowerToys Command Palette (CmdPal) extension that exposes huddle's daily verbs — Status, Repos, Personas, Conflicts, Direct, Start session, Send message, Stop, Broadcast, Launch, Setup launcher — as palette commands. Summon CmdPal, type `huddle status`, see the live session list; type `huddle direct fix the auth flow`, hand the task to architect — without focusing the console window.

The extension is **stateless**. State reads come from disk (`huddle.json`, `personas/*.md`, `ipc/`); writes are the same JSON-envelope drops into `ipc/_huddle/inbox/` that the orchestrator already watches. No new wire protocol, no daemon, no edits to `src/`. The console keeps every verb it has today; the palette is additive.

The project lives at [`extension/CmdPalHuddle/`](extension/CmdPalHuddle/) — MSIX-packaged C# (.NET 8 + Windows App SDK) using `Microsoft.CommandPalette.Extensions.Toolkit`. v1 shipped 2026-04-26. Build / deploy / settings notes are in [`CLAUDE.md`](CLAUDE.md) under **Build (CmdPal extension)**; follow-ons are tracked in [`BACKLOG.md`](BACKLOG.md).

- Design: [`docs/superpowers/specs/2026-04-25-cmdpal-huddle-extension-design.md`](docs/superpowers/specs/2026-04-25-cmdpal-huddle-extension-design.md)
- Implementation plan: [`docs/superpowers/plans/2026-04-25-cmdpal-huddle-extension.md`](docs/superpowers/plans/2026-04-25-cmdpal-huddle-extension.md)

---

## Config: `huddle.json`

Huddle loads `huddle.json` from the current directory by default. Everything else — personas, logs, IPC, context — resolves relative to that file. Use `--config path/to/huddle.json` to point elsewhere.

### A minimal session

```json
{
  "name": "myapp",
  "aliases": ["app"],
  "root": "C:\\Users\\you\\source\\repos\\myapp",
  "purpose": "MyApp active development — REST API, web UI"
}
```

### Everything you can set on a session

| Field | Required | What it does |
|-------|----------|--------------|
| `name` | yes | Canonical identifier. Used in instance IDs. |
| `aliases` | no | Alternate names — must be globally unique across repos. |
| `root` | yes | Absolute path to the working directory. |
| `purpose` | no | Free-form text injected into the session prompt. |
| `autoStart` | no | Launch on huddle startup. Default `false`. |
| `autoRestart` | no | Auto-restart on crash. Overrides global. |
| `maxAutoRestarts` | no | Consecutive restart cap. Overrides global. |
| `backoffSeconds` | no | Escalating restart delays. Overrides global. |
| `paths` | no | Named paths (e.g. test logs, deploy folder) injected into session context. |
| `replayHost` | no | Host the `replay` verb points `mbxhval` at for this repo. Default `127.0.0.1`. Use the literal IP, not `localhost` — `mbxhval` warns that `localhost` can resolve to IPv6 `::1`. |
| `replayPort` | no | Port the `replay` verb points `mbxhval` at for this repo. Default `8080`. |
| `notes` | no | Free-form context. |

### Global settings and groups

```json
{
  "sessions": [ /* ... */ ],
  "claudePath": null,
  "mbxhvalPath": null,
  "contextFile": true,
  "ipc": true,
  "crashLogRetention": 10,
  "autoRestart": false,
  "maxAutoRestarts": 3,
  "backoffSeconds": [2, 5, 15],
  "groups": {
    "dev": [
      { "repo": "myapp", "persona": "backenddev", "prompt": "continue where you left off" },
      { "repo": "myapp", "persona": "frontenddev" }
    ]
  }
}
```

`huddle dev` launches every session in the `dev` group.

---

## Auto-restart

When a session crashes (exit code ≠ 0), huddle can restart it with escalating backoff:

1. Crash detected.
2. If auto-restart is on and the consecutive-attempt counter hasn't hit the cap, schedule a restart.
3. Delay increases per attempt: `backoffSeconds[0]`, `[1]`, `[2]`, ... — clamps at the last element.
4. Status displays `AutoRestarting (in Ns)` during the countdown.
5. Restart uses `--continue` so Claude can `/resume` the crashed session.
6. Sustained run (>60 s) resets the counter.
7. `stop` during countdown cancels.
8. Manual `restart` also resets the counter.

Config globally or per-session. Per-session wins where both are set.

---

## The Bun problem (why the crash wrapper exists)

Claude Code runs on [Bun](https://bun.sh). Bun panics, and the panic kills the entire console window — not just the Claude process. On Windows 11 with Bun 1.3.10, Claude Code 2.1.50, the specific signature we've observed repeatedly:

```
panic(main thread): switch on corrupt value
oh no: Bun has crashed. This indicates a bug in Bun, not your code.
```

Exit code 3. Trigger looks concurrency-related (git, parallel tool calls). RSS ~0.7 GB, faults 300k+, peaks at ~0.9 GB — this is internal JIT memory corruption, not out-of-memory. Machine has 33 GB; Claude is using a fraction of it.

This is not a huddle bug, not a Claude Code bug. It's a Bun JSC panic and has to be fixed upstream.

What huddle can do is contain the blast: separate console per session (`UseShellExecute = true`), `BUN_CRASH_REPORTER_URL=""` in the child env to kill the crash reporter, optional auto-restart. Crash logs land in `logs/<instance>/crash-<timestamp>.log` with exit code, uptime, and persona.

---

## Project Layout

```
huddle/
  huddle.sln
  huddle.json              — session config (all repos)
  context.md               — auto-generated cross-session awareness
  personas/
    _shared.md             — rules injected into every persona
    _shared.json           — tuning defaults (model, effort) inherited by every persona
    architect.md/.json     — system design focus (Opus, denies Edit/Write)
    reviewer.md/.json
    frontenddev.md/.json
    backenddev.md/.json
    documenter.md/.json    — Haiku + bare, cheap doc edits
    versioner.md/.json
  logs/                    — per-session crash logs, scratchpads
  ipc/                     — inter-session communication
    _huddle/inbox/         — orchestrator command inbox
    <instance>/inbox/      — per-session mailboxes
    workledger/            — freeform session ledger files
    workledger/claims/     — structured orchestrator-owned claims
  publish/                 — single-file exe (build.cmd output)
  src/                     — C# source (see DESIGN.md for per-file)
  scripts/statusline.ps1   — Claude Code statusline for Windows
  docs/                    — specs, plans, analyses, superpowers artefacts
  build.cmd                — publishes publish/huddle.exe
  DESIGN.md                — deep reference (components, IPC, personas, claims)
  ROADMAP.md               — history + v2 vision
  BACKLOG.md               — planned and in-flight work
  ISSUES.md                — bug list
  HUDDLE-COORDINATION-PROTOCOL.md  — check-out / check-in rules for multi-session work
  CLAUDE.md                — project rules for Claude Code itself
  README.md                — this file
```

Everything is relative to the config file location. Drop `publish\huddle.exe` wherever you like and point it at a config with `--config`.

---

## Where to Go Next

- **`DESIGN.md`** — deep reference. Per-file breakdown of `src/`, IPC wire format, persona injection mechanics, claims lifecycle, auto-restart internals.
- **`ROADMAP.md`** — history (myapp → huddle), what's shipped, v2 vision (WPF + stream-JSON), near-term direction.
- **`BACKLOG.md`** — planned work and known follow-ons. Phase 2 (runtime claim + yield-for-commit) and Phase 3 (worktree-per-session) live here.
- **`ISSUES.md`** — bug list.
- **`HUDDLE-COORDINATION-PROTOCOL.md`** — the check-out / check-in rules agents follow when editing shared files.
- **`docs/superpowers/specs/`** and **`docs/superpowers/plans/`** — design + implementation docs for significant features, keyed by date and topic. The CmdPal extension design and plan live here under `2026-04-25-cmdpal-huddle-extension*`.
- **`CLAUDE.md`** — rules that Claude Code itself follows when doing work in this repo.

---

## Conventions

- Simple, minimal code. No frameworks. No abstractions for one-time operations.
- Logging via `Console.WriteLine` with timestamps.
- Config via `huddle.json` with `System.Text.Json`.
- Each Claude session in its own console window (`UseShellExecute = true`).
- `BUN_CRASH_REPORTER_URL=""` in the child environment (disables Bun's crash reporter).
- No external runtime dependencies beyond the .NET BCL.
- Offline-first. Windows-native. Firefox-first for any HTML we ever ship.

---

## Tech Stack

- **C# / .NET 8** — console application.
- **`System.Text.Json`** and **`System.Diagnostics.Process`** — that's the dependency list.
- **PowerShell 5.1** — for the statusline script only; nothing huddle itself depends on.

---

## License

Apache License 2.0 — see [LICENSE](LICENSE). Attribution notices in [NOTICE](NOTICE)
must be preserved in redistributions, per the license terms.
