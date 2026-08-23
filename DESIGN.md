# claude huddle

Claude Code session orchestrator.

## What It Is

A control center for Claude Code sessions. Define your repos once in `huddle.json`,
then launch Claude into any of them by name. Each session runs in its own console window.
If one crashes, the others keep running and you restart from the huddle prompt.

Three things in one:
1. **Repo launcher** — `start myapp` opens Claude in myapp. `start docs-site` opens
   it in docs-site. No more `cd`-ing around. All your repos, one command each.
2. **Persona system** — `start myapp architect` launches Claude as an architect focused on MyApp.
   `start webshop reviewer` launches a code reviewer in webshop. Session's primary focus is X on project Y.
3. **Crash wrapper** — When Bun (Claude's runtime) panics and kills a console window, huddle
   survives, logs it, and lets you restart immediately.

## Usage

### Running from source

```
cd C:\path\to\huddle
dotnet run --project src/huddle.csproj
```

### Running the published exe

```
cd C:\path\to\huddle
huddle.exe
```

Or point at a config elsewhere:

```
huddle.exe --config C:\path\to\huddle.json
```

The exe looks for `huddle.json` in the current directory by default. Everything
(personas, logs, context.md, ipc) is relative to the config file location.

### Building the exe

```
build.cmd
```

Produces a single-file self-contained exe at `publish/huddle.exe`.

### Startup

Huddle loads `huddle.json`, registers all repos, and shows the command prompt.
No sessions start automatically unless `autoStart: true` is set.

```
=== claude huddle ===
Claude Code session orchestrator

>
```

### Commands

```
> start myapp                    # Launch Claude in myapp's directory
> start app architect             # Same repo via alias, with architect persona
> start webshop backenddev        # Backend developer in webshop
> start docs-site documenter task  # Documenter with an initial task prompt
> stop myapp                     # Stop all instances of a repo (alias works too)
> stop myapp:architect      # Stop a specific instance
> restart myapp:architect   # Restart (keeps persona, --continue on crash)
> repos                          # List all repos with aliases
> personas                       # List available personas
> status                         # Show all running/crashed/stopped instances
> huddle dev                     # Launch a named group of sessions
> send myapp:architect msg  # Send an IPC message to a session
> messages myapp:architect  # Read a session's inbox
> delegate "fix the bug" to app:backenddev   # Delegate a task (auto-starts if needed)
> tasks                          # Show tracked tasks
> progress                       # Show last scratchpad checkpoint per session
> docs                           # List artifacts sessions declared (Docs; +plans / +logs)
> open 1                         # Open the nth document from the last docs listing
> quit                           # Exit huddle (sessions keep running)
> shutdown                       # Stop all sessions and exit
> help                           # Show command reference
```

Aliases work everywhere a repo name is accepted. `start app` = `start myapp`.

### Why Huddle Exists

**Without huddle:**
1. Open terminal, `cd` to myapp, run `claude`
2. Work for 20 minutes
3. Bun panics — terminal window vanishes. Context gone. Work gone.
4. Open new terminal, `cd` again, `claude`, `/resume`, hope for the best

**With huddle:**
1. Run huddle, `start app`
2. Claude opens in its own window. Work for 20 minutes.
3. Bun panics — that Claude window vanishes.
4. **Huddle window is still there.** Says: `*** CRASH *** myapp exited with code 3`
5. `restart myapp:architect` — new Claude window with `--continue`. Back to work.
6. If auto-restart is on, huddle restarts it automatically with backoff.

Other sessions you had running? Untouched. Nothing else died.

## The Bun Problem

Claude Code runs on **Bun** (a JavaScript runtime). `claude.exe` is Bun. When Bun crashes —
and it does — it takes out the entire console window, killing context, losing work, killing
the session. There is no recovery.

### The Crash

Observed repeatedly on Windows 11, Bun v1.3.10, Claude Code 2.1.50:

```
panic(main thread): switch on corrupt value
oh no: Bun has crashed. This indicates a bug in Bun, not your code.
```

- **Exit code:** 3 (0x00000003)
- **Trigger:** Appears to happen during concurrent operations (git commands, file reads, tool calls in parallel)
- **Frequency:** Multiple times per session hour during heavy use
- **Impact:** Console window destroyed, all session context lost, any in-progress work gone
- **Bun version:** 1.3.10 (1423d3c8) Windows x64 (baseline)

#### Crash Signature

```
Bun v1.3.10 (1423d3c8) Windows x64 (baseline)
Windows v.win11_dt
CPU: sse42 avx avx2
Args: "claude"
Features: Bun.stderr(2) Bun.stdin(2) Bun.stdout(2) fetch(37) jsc spawn(23)
          standalone_executable process_dlopen(2) yaml_parse(7)
Elapsed: 45925ms | User: 5312ms | Sys: 1421ms
RSS: 0.72GB | Peak: 0.86GB | Commit: 1.06GB | Faults: 347046 | Machine: 33.84GB

panic(main thread): switch on corrupt value
```

#### Crash Footer Fields

| Field | Meaning |
|-------|---------|
| **Elapsed** | Wall-clock time since Bun started |
| **User** | CPU time in user-mode code (Bun + JS execution) |
| **Sys** | CPU time in kernel calls (file I/O, network, process spawning) |
| **RSS** | Resident Set Size — physical RAM actually occupied right now. 0.72GB for a CLI is heavy (~2% of system RAM per session). |
| **Peak** | Highest RSS reached during the session |
| **Commit** | Virtual memory reserved from the OS. Includes paged-out and untouched pages. Gap between RSS and Commit (0.72 vs 1.06GB) = ~340MB allocated but not resident. |
| **Faults** | Page faults — times the OS loaded a memory page into RAM. 347K in 45s is extremely high — aggressive/scattered memory allocation pattern. |
| **Machine** | Total physical RAM on the system |

**What the numbers say:** Bun allocates ~1GB, actively uses 0.7-0.86GB, page faults
constantly, then its JIT compiler (JSC) hits corrupted memory and panics. Not an
out-of-memory crash — 33.84GB available, only 0.72GB used. Internal JIT memory corruption.

#### Second Crash (21 seconds in, lighter load)

```
Elapsed: 21713ms | User: 2453ms | Sys: 953ms
RSS: 0.78GB | Peak: 0.79GB | Commit: 1.19GB | Faults: 259944 | Machine: 33.84GB
```

Report URL pattern: `https://bun.report/1.3.10/e_11423d3c...`

#### What We Can't Fix

This is a bug in Bun's JavaScript engine (JSC — JavaScriptCore). The "switch on corrupt value"
panic means the JIT compiler generated bad machine code or memory got corrupted. This is not
something Claude Code or huddle can prevent. It has to be fixed upstream by the Bun team.

#### What We Can Do

Contain the blast radius. When Bun crashes, it should only kill one session window — not your
entire workspace, not your other sessions, not your control center.

## Architecture

```
huddle (console app — .NET 8.0)
    |
    +-- reads huddle.json (session definitions)
    |
    +-- reads personas/*.md + personas/*.json (role + tuning)
    |       _shared.md      -- prose rules injected into every persona
    |       _shared.json    -- tuning defaults (model, effort, etc.)
    |       architect.md/.json    -- Opus, high effort, denies Edit/Write/NotebookEdit
    |       reviewer.md/.json     -- denies Edit/Write, allows Bash(git *)
    |       frontenddev.md/.json  -- Sonnet high
    |       backenddev.md/.json   -- Sonnet high
    |       documenter.md/.json   -- Haiku low, bare, 4-tool whitelist
    |       versioner.md/.json    -- Haiku low, bare, allows Bash(git *)
    |
    +-- launches Claude Code processes (one per session)
    |       each in its own console window (UseShellExecute = true)
    |       --append-system-prompt-file persona+_shared+session context
    |       --session-id <pre-assigned uuid> so we can locate the JSONL log
    |       --model / --effort / --bare / --tools / --disallowedTools / etc.
    |          materialized from PersonaConfig per persona
    |       --mcp-config <temp> if persona declares mcpServers
    |       --agents <inline json> if persona declares custom subagents
    |       --settings <temp> for settingsOverride escape hatch
    |       BUN_CRASH_REPORTER_URL="" to suppress Bun's crash reporter
    |
    +-- maintains (next to huddle.json)
    |       context.md      -- shared awareness file (what each session is doing)
    |       logs/<name>/    -- per-session crash logs + persona temp files
    |       ipc/            -- file-based inter-session communication
    |
    +-- orchestrator engine (watches ipc/_huddle/inbox/)
    |       start-session, stop-session, delegate-task commands
    |       task tracking (in-memory)
    |
    +-- status monitor (huddle's own console window)
            shows live status of all sessions
            accepts commands (start, stop, restart, huddle, delegate, tasks, progress, etc.)
```

## Document log

The `docs`/`open` verbs surface a leveled, on-demand log of artifacts sessions produce.
Design goals: no background watcher (read only when the verb runs), zero new dependencies,
and a storage seam so a future virtual-storage backend can replace the file-backed source
untouched. Full spec: [`docs/superpowers/specs/2026-06-27-document-log-design.md`](docs/superpowers/specs/2026-06-27-document-log-design.md).

```
ConsoleUI (docs / open verbs)         never touches the filesystem directly
    |
    +-- IDocumentSource.GetDocuments(maxLevel)
    |       CompositeDocumentSource          merges children, sorts newest-first
    |           ScratchpadDocumentSource     Output/Plans — parses the `## Documents` section
    |           |                            of each logs/<session>/scratchpad.md.
    |           |                            Read directly (not via git) so gitignored
    |           |                            artifacts still appear when declared.
    |           FilesystemDocSource          Output/Plans — auto-discovers the huddle repo's
    |           |                            own docs/**/*.md + root *.md (declared wins
    |           |                            on dedupe).
    |           GitChurnSource               Churn — `git status` per repo via GitHelper;
    |                                        queried ONLY when maxLevel >= Churn, so the
    |                                        default `docs` path never shells git
    |                                        (binaries/build output filtered out).
    |
    +-- IDocumentOpener.Open(path)
            ShellDocumentOpener              Process.Start(UseShellExecute=true) today;
                                             swappable for a virtual-storage opener later.
```

- **Levels:** `DocLevel { Output=0, Plans=1, Churn=2 }` — the name is the filter token and the badge, no opaque codes. Plain `docs`=Output, `docs plans`=Plans, `docs churn`=Churn (binaries/build output filtered out).
- **Declaration, not discovery:** Output/Plans are agent-declared in scratchpads (`- [Title](path) #output|#plans`); level is inferred from the path (under `/plans/` → Plans) when the tag is omitted. The convention lives in `personas/_shared.md`.
- **No store:** scratchpad files are the persistence; parsing is fresh per call (cheap; a cache can drop behind `IDocumentSource` if ever needed).
- **Clickable:** titles are emitted as OSC 8 hyperlinks (URI built via `new Uri(path).AbsoluteUri`); `open <n>` is the guaranteed fallback against the last listing.

## Tech Stack

- **C# / .NET 8.0** — Console application
- **No external dependencies** — Process, Console, System.Text.Json only
- **Config format** — JSON (`huddle.json`)

## Config: huddle.json

All repos are registered with `autoStart: false`. Huddle is a manual launcher —
you pick what to open.

### Session Definition

```json
{
  "name": "myapp",
  "aliases": ["app", "web"],
  "root": "C:\\Users\\you\\source\\repos\\myapp",
  "purpose": "MyApp active development - REST API, web UI",
  "autoStart": false,
  "autoRestart": true,
  "maxAutoRestarts": 5,
  "backoffSeconds": [1, 3, 10, 30],
  "paths": {
    "testLogs": "C:\\test\\myapp\\logs",
    "deploy": "C:\\Users\\you\\source\\repos\\myapp\\deploy"
  },
  "notes": "Free-form context injected into the session prompt."
}
```

| Field | Required | Description |
|-------|----------|-------------|
| `name` | Yes | Canonical repo name, used as the primary identifier |
| `aliases` | No | Alternative names for the repo (short forms, old names, etc.) |
| `root` | Yes | Absolute path to the repo working directory |
| `purpose` | No | Injected into session context so Claude knows what the project is |
| `autoStart` | No | Launch on huddle startup (default: `false`) |
| `autoRestart` | No | Auto-restart on crash (overrides global, default: inherits) |
| `maxAutoRestarts` | No | Max consecutive restart attempts (overrides global) |
| `backoffSeconds` | No | Delay array for escalating restarts (overrides global) |
| `paths` | No | Named paths injected into session context |
| `notes` | No | Free-form text injected into session context |

### Global Settings

```json
{
  "sessions": [ ... ],
  "claudePath": null,
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

| Field | Default | Description |
|-------|---------|-------------|
| `claudePath` | auto-detect | Path to `claude.exe`. Searches PATH, then `~/.local/bin/claude.exe` |
| `contextFile` | `true` | Maintain `context.md` with live session status |
| `ipc` | `true` | Enable file-based IPC and orchestrator engine |
| `crashLogRetention` | `10` | Number of crash logs to keep per session |
| `autoRestart` | `false` | Global default — auto-restart crashed sessions |
| `maxAutoRestarts` | `3` | Global default — max consecutive restart attempts |
| `backoffSeconds` | `[2, 5, 15]` | Global default — escalating restart delays |
| `groups` | none | Named groups for `huddle <group>` command |

### Repo Aliases

Sessions can have aliases — short names or alternative references:

```json
{ "name": "myapp", "aliases": ["app", "web"], ... }
```

Aliases work everywhere a repo name is accepted: `start app architect`, `stop bee`, etc.
Use the `repos` command to see all registered repos and their aliases. Aliases must be
unique across all repos — conflicts are logged and skipped on startup.

### Groups

Groups let you launch a predefined set of sessions with one command:

```
> huddle dev
```

Each group member specifies a repo, optional persona, and optional initial prompt.
Use `huddle` with no argument to list available groups.

## Personas

Personas give each session a primary focus. They're markdown files in `personas/` that get
injected into Claude via `--append-system-prompt`. The idea: **session's primary focus is X
on project Y.**

```
> start myapp architect        # "You are an architect working on MyApp"
> start webshop reviewer         # "You are a code reviewer working on Webshop"
> start docs-site documenter   # "You are a documenter working on docs-site"
```

### Available Personas

| Persona | Use Case | Tuned For (Strengths) | Tuned Against (Weaknesses) |
|---------|----------|----------------------|---------------------------|
| `architect` | System design, structure analysis, dependency mapping, trade-off evaluation | Thinks in components, boundaries, interfaces, data flow. Surfaces assumptions, failure modes, race conditions. Challenges over-engineering. Asks clarifying questions before designing. | Will not write implementation code unless explicitly asked. Designs but doesn't build. |
| `reviewer` | Code review, bug hunting, security audit, convention checks | Finds bugs, logic errors, security issues, edge cases. Specific citations (file, line, what's wrong). Prioritizes by severity: crashes > data loss > security > correctness > style. | Will not make changes unless explicitly asked. Does not fix what it finds. Does not nitpick style when logic is the concern. |
| `backenddev` | Building APIs, services, server-side logic | Reads existing code first, matches patterns and conventions. Builds incrementally. Consistent routes, clear request/response shapes, proper status codes. Validates input at boundaries. Writes logging from the start. Tests what it builds. | No architecture vision — follows existing patterns rather than questioning them. Focused on "make it work" not "make it right." |
| `frontenddev` | HTML, CSS, UX, responsive layout, cross-browser work | Clean semantic HTML, CSS-first approach, Firefox-first testing. Accessibility built in (semantic elements, contrast, keyboard nav). Matches existing styles before adding new ones. | No backend awareness. JavaScript only when necessary — may under-reach on interactive features. |
| `documenter` | Writing and maintaining project documentation | Updates existing docs, documents the why not just the what. Concise and clear. Respects CLAUDE.md and DESIGN.md as sources of truth. | Will never create doc files unless explicitly asked. Will never change status labels (Draft, Alpha, WIP, etc.). Heavily restricted to prevent unsolicited changes. |
| `versioner` | Version bumps across a repo using recipes | Append-only changelog discipline. Deterministic find/set of version strings. | Narrow specialist — version/changelog work only. |
| `researcher` | External research, subsystem deep-dives, hypothesis-driven PoC spikes | Ranges wide (web + local repos + mirrors), synthesizes rather than summarizes, states hypotheses explicitly, cites sources. Builds PoCs in the `labs` repo with Hypothesis → Method → Result → Recommendation discipline. Treats "unknown" and "refuted" as valid findings. | Never integrates its own PoC — hand-off to architect is the boundary. In product repos writes only under `docs/`; never touches product source. No scheduled/proactive scanning — every mission is explicitly launched. |

### How Injection Works

When `start myapp architect` runs, huddle:
1. Reads `personas/_shared.md` (standard terminology, shared rules)
2. Reads `personas/architect.md` (role-specific instructions)
3. Appends session context ("Your session is 'myapp', working directory: ..., project context: ...")
4. Passes the combined text via `claude --append-system-prompt "..."`

On `restart`, the last persona is preserved automatically.

### _shared.md

Rules injected into every persona. Currently defines standard project terminology:

- **Roadmap** — Over-arching vision and progression. Where the project is going. Not tasks.
- **Backlog** — Specific things or ideas, planned or to be planned. Concrete work items.
- **Issues** — Bug list. Specific defects, broken behavior. Not features, not ideas.

### Adding New Personas

Drop a markdown file in `personas/`. Name = persona name. Content = instructions for Claude.
Files starting with `_` are shared/internal (like `_shared.md`) and won't show in `personas` command.

Optional: pair the `.md` with a `<name>.json` sidecar to also tune model, effort, tool
fence, MCP scope, plugin scope, custom subagents, and per-session budget. See the
**Persona Tuning** section below and [`docs/persona-tuning.md`](docs/persona-tuning.md).

## Persona Tuning

Each persona may carry a JSON sidecar at `personas/<name>.json`. `personas/_shared.json`
holds defaults that every persona inherits; the persona's own JSON overrides per field.
All fields are optional — missing JSON files = current behavior, fully backward compatible.

### Schema (summary)

| Field | Maps to Claude CLI | Notes |
|-------|--------------------|-------|
| `model` | `--model` | `claude-opus-4-7` / `claude-sonnet-4-6` / `claude-haiku-4-5` |
| `effort` | `--effort` | `low` / `medium` / `high` / `xhigh` / `max` |
| `bare` | `--bare` | Strip skills, hooks, auto-memory, CLAUDE.md auto-discovery |
| `pluginDirs` | `--plugin-dir` (×N) | Per-bundle plugin scope |
| `disableSlashCommands` | `--disable-slash-commands` | Kill all skills |
| `tools` | `--tools` | Hard whitelist |
| `allowedTools` / `disallowedTools` | same | Additive grants / denies (deny wins) |
| `mcpServers` | `--mcp-config` (temp file) | Inline server config |
| `strictMcp` | `--strict-mcp-config` | Only listed MCPs load |
| `agents` | `--agents` (inline JSON) | Additive custom subagents |
| `permissionMode` | `--permission-mode` | `default` / `acceptEdits` / `plan` / etc. |
| `addDirs` | `--add-dir` (×N) | Extra allowed roots |
| `settingsOverride` | `--settings` (temp file) | Escape hatch for any settings.json key |

### Merge rules

`_shared.json` first, then `<persona>.json`:

- **Scalars** — persona replaces shared
- **Arrays** — persona **replaces** shared (not concat)
- **Objects** (`mcpServers`, `agents`, `settingsOverride`) — per-key shallow merge

### Materialization

`PersonaConfigLoader.LoadAndMerge` is called inside `SessionManager.Start`, between the
persona-prompt write and the positional task prompt. `PersonaFlagBuilder.Build` turns the
merged `PersonaConfig` into a flag string plus a list of temp files (mcp + settings) that
get cleaned up on session stop. Malformed JSON in a sidecar aborts the spawn with a clear
error and never starts a session in a half-configured state.

### Source files

- `src/PersonaConfig.cs` — 16-property record, JSON-deserializable
- `src/PersonaConfigLoader.cs` — load + merge
- `src/PersonaFlagBuilder.cs` — CLI args + temp file materialization

## Cost Telemetry — REMOVED 2026-06-21

An earlier iteration tailed each session's Claude JSONL log
(`~/.claude/projects/<encoded-cwd>/<session-id>.jsonl`), parsed per-turn `usage` blocks,
computed USD via a per-million-token price table, rolled the totals onto `SessionInstance`
(`TurnCount`, `TotalCostUsd`, `LastTurnCostUsd`, `LastTurnAt`,
`BudgetWarnedAt80/100`), surfaced them via a `cost` verb plus `<turns>t $<cost>` in
`status` rows, and enforced a soft per-persona `budgetUsd` with optional
`budgetAction: "stop"` auto-stop at 100%.

Removed because the operator never adjusted behavior based on the numbers — the rollups
ran every turn and the budget warnings either didn't fire or fired without changing what
got done. Deleted: `src/CostTelemetry.cs`, `src/SessionCostWatcher.cs`,
`src/BudgetMonitor.cs`, the two cost test files, and the `budgetUsd` / `budgetAction`
fields on `PersonaConfig`. Persona tuning itself stayed; only the cost surface went.

## Bun Crash Containment

Containment strategy:
1. **Separate console per session** — `UseShellExecute = true` gives each Claude its own
   `conhost.exe`. If Bun kills it, only that session's window dies. Huddle survives.
2. **Suppress Bun crash reporter** — `BUN_CRASH_REPORTER_URL=""` in the child process
   environment to disable Bun's crash reporter and any associated instability.
3. **Crash logging** — Exit code, timestamps, uptime, persona written to
   `logs/<name>/crash-<timestamp>.log` (next to huddle.json)
4. **Exit code decoding** — Exit 0 = clean. Exit 3 = Bun panic. Non-zero = crash.

## Auto-Restart

When a session crashes, huddle can automatically restart it with escalating backoff delays.

**How it works:**
1. Session crashes (exit code != 0)
2. If auto-restart is enabled and consecutive attempts haven't exceeded the limit, schedule a restart
3. Delay increases with each consecutive attempt: `backoffSeconds[0]`, then `[1]`, then `[2]`, etc. (clamps to last element)
4. Status shows `AutoRestarting (in 5s)` during the countdown
5. Restart uses `--continue` so Claude can `/resume` the crashed session
6. If the session runs for >60 seconds before the next crash, the consecutive counter resets (sustained run, not a crash loop)
7. `stop` during the countdown cancels the pending restart
8. Manual `restart` also resets the consecutive counter

**Config:** Set globally or per-session. Per-session values override global defaults.

```json
{ "autoRestart": true, "maxAutoRestarts": 3, "backoffSeconds": [2, 5, 15] }
```

## File-Based IPC

Sessions can communicate with each other and with the huddle orchestrator via JSON message files.

**Structure:**
```
ipc/
    _huddle/inbox/          -- Orchestrator command inbox
    myapp_architect/inbox/    -- Per-instance mailbox
    myapp_backenddev/inbox/   -- Per-instance mailbox
```

**Message format:**
```json
{
  "from": "myapp_architect",
  "to": "myapp_backenddev",
  "timestamp": "2026-02-21T03:15:00Z",
  "type": "request",
  "subject": "Need input validation on the new endpoint",
  "body": "The new API endpoint should validate the request body and return 400 on bad input."
}
```

Sessions are told their inbox/outbox paths and the IPC root via the injected system prompt.
They can write messages to other sessions' inboxes directly, or send orchestration commands
to `_huddle/inbox/` to start/stop sessions and delegate tasks.

## Task Tracking & Orchestration

The orchestrator engine watches `ipc/_huddle/inbox/` for command messages and provides
in-memory task tracking.

**Orchestration commands** (written as JSON to `_huddle/inbox/`):
- `start-session` — Spin up a new Claude session with repo, persona, and task
- `stop-session` — Stop a running session
- `delegate-task` — Assign a task to a session (auto-starts if needed)
- `task-complete` / `task-failed` / `task-progress` — Update task status
- `broadcast` — Fan out one informational message to every live session
- `dispatch-batch` — Multi-session dispatch with declared file scope per task and optional `dependsOn`; enqueues the batch into a work queue, dispatches the immediately-dispatchable units (claims + spawns), and leaves overlapping or dependency-blocked units queued to dispatch later. See [Conflict-guarding work queue](#conflict-guarding-work-queue).
- `release` — Agents hand back claims on specific files after committing a unit of work; a fully-released unit advances the queue

This means a session running the `architect` persona (or the now-retired `orchestrator`
persona) can programmatically coordinate work across the entire workspace — starting
sessions, assigning tasks, and tracking progress without human intervention.

## Work Ledger and Claim Lifecycle

Two parallel mechanisms prevent parallel sessions from stomping on each other's file
edits. They complement each other — keep both.

### Freeform session ledger

Each running session writes a markdown file at `ipc/workledger/<session>.md` that
announces what it's working on and which files it's editing:

```markdown
# huddle:architect

- **Working on:** Orchestration-streamlining spec (Phase 1)
- **Files modified:**
  - docs/superpowers/specs/2026-04-21-orchestration-streamlining-design.md
  - ipc/workledger/huddle_architect.md
- **Files I will NOT modify (reserved for X):** src/Orchestrator.cs
- **Updated:** 2026-04-22 00:10
- **Status:** active
```

This is **prompt-driven** — agents write it themselves as a convention, and other agents
read it before editing to avoid conflicts. Written to by personas; parsed by
`HandleConflicts`. Best for long-running sessions that want to declare scope loosely.

### Structured claims

A claim is a file at `ipc/workledger/claims/<batchId>-<session>.md`. Agents write these
themselves (`huddle --claim`, below); huddle also writes one per unit when
`dispatch-batch` spawns it:

```markdown
# B-20260422-001500-myapp_backenddev

- **Session:** myapp:backenddev
- **Repo:** myapp
- **Batch:** B-20260422-001500
- **Claimed at:** 2026-04-22T00:15:00Z
- **Base commit:** 40-char-sha
- **Files:**
  - src/Foo.cs
  - docs/bar.md
```

This was **orchestrator-driven** until 2026-08-16 — agents mailed a `claim` command and
waited for huddle to write the file for them. With huddle down the mail sat unread, no
claim was ever recorded, and two sessions worked the same files invisible to each other
(ISSUES.md I011). **Agents now write claims directly**; huddle is an observer of the same
ledger, not a gatekeeper on the way into it. Two routes, one meaning:

- **`huddle --claim` / `--release` / `--ledger`** — the primary route. See
  [Direct ledger access](#direct-ledger-access-huddle---claim).
- **`dispatch-batch`** — huddle writes a claim per unit it spawns, because the batch
  already declared each task's file set up front. It enqueues the batch into a work queue
  and dispatches the immediately-dispatchable units; overlapping or dependency-blocked
  units stay queued and dispatch on a later advance. See
  [Conflict-guarding work queue](#conflict-guarding-work-queue).
- The mail `claim` command still works and behaves the same way — it records and reports,
  and always acks. `nack:claim` no longer means contention; it means a malformed request.

## The feature ledger

Two ledgers, different questions. The **claims** ledger above answers *who is touching
which file right now* and is disposable — it lives under `ipc/` and means nothing after
the session ends. The **feature** ledger answers *what did we say we would do, and what
became of it*, and is committed to the repo it describes.

It exists because the audit found four assignments that were mailed, arrived, and were
never acted on — with nothing anywhere that made the omission visible afterwards. The
session holding them reported "nothing in flight, inbox clear" repeatedly and was, by
every surface huddle had, telling the truth.

### Two files per repo, under `docs/ledger/`

| File | Written by | Why |
|---|---|---|
| `ledger.md` | humans and architect agents | the hierarchy — epic, scenario, story, feature, deliverable. Rare, deliberate, reviewable in a diff |
| `events.jsonl` | the orchestrator, only | append-only. Every task, and every state transition of everything |

The split is by **write frequency and writer**, not by concept. Task state changes many
times a day and is written concurrently by a machine; a shared markdown table would
conflict daily. The hierarchy changes rarely and benefits from review.

Schema, states, id scheme and parsing rules are in
[`docs/ledger/README.md`](docs/ledger/README.md), which ships publicly. The ledger *data*
does not — it names private repos and priorities, and is on the release playbook's
"Explicitly NOT shipped" list.

### Rules worth knowing before reading the code

- **The orchestrator is the only writer.** `LedgerWriter` is the single append path, and
  it holds a machine-scoped named mutex, so two huddles cannot interleave half a line.
  The `huddle --ledger` CLI and every render path stay read-only.
- **Ids are compared parsed, never as text.** `T-7`, `T-007` and `repo:T-007` are one
  task. Keying on the string made them three, so an acknowledgement could silently open a
  second task and leave the first hanging in `assigned` forever.
- **Nothing is deleted.** `dropped`, `declined` and `abandoned` are terminal states, not
  removals. The trail of work that did not happen is the point.
- **Hierarchy state is an overlay.** Huddle never rewrites `ledger.md`, so
  `ledger accept` and `ledger drop` append a `state` event and every reader applies the
  latest by timestamp on top. The State column is the baseline; events win.
- **Orphans are a signal, not an error.** A task with no parent is work nobody ideated.
  `ledger orphans` counts them, and they are never blocked or auto-parented.
- **Delivered is not accepted.** A work-queue unit reaching Done appends
  `task-delivered`. Acceptance is `ledger accept`, and it refuses when a Deliverable's
  `accepts` gate is unnamed. Those were the same word before, which is why all 13
  persisted units read Done including the one the operator later found broken.

### How an obligation gets recorded without anyone asking

| Producer | Dedup key | Opens |
|---|---|---|
| `type:"task"` mail, any agent to any agent | the mail file's path | `task-assigned` owned by the recipient |
| `delegate-task` / the `delegate` verb | the task id | `task-assigned` owned by the assignee |
| a dispatched work-queue unit | `unit:<id>` | `task-assigned` owned by `repo:persona` |

Each is keyed so a rescan, a retry or a restart re-finds the existing row instead of
opening a second. Moving mail from `inbox/` to `processed/` appends `task-acked` —
acknowledgement already had a filesystem meaning and this reuses it rather than inventing
a second one. Anything still in `assigned` past `taskAckMinutes` escalates **once**, to
the dispatcher by mail and to the operator on the console; "already escalated" is read
back out of the log, so a restart does not re-announce every old assignment at once.

### Direct ledger access (`huddle --claim`)

Three argument modes on the binary. They **run huddle.exe and never contact a running
huddle**, which is the whole point: a claim lands whether or not the console is up.

| Mode | What it does |
|------|--------------|
| `huddle --claim <repo-relative-path> [more...]` | Records the claim and prints every OTHER session already holding any of those files (`ALSO HELD BY <session> since <time>`). It **never refuses** — an overlap is reported, not denied, because refusing needs an arbiter alive and reporting does not. Exit 0. |
| `huddle --release <repo-relative-path> [more...]` | Removes those files from the caller's OWN claims; another session's claim on the same file is untouched. A claim with nothing left is deleted. |
| `huddle --ledger [repo]` | Prints one line per claimed file — what, by whom, since when. The read-before-you-work view. With a repo argument that matches nothing while claims exist elsewhere, it says so rather than printing an all-clear. |

Identity and ledger location arrive as environment variables exported at spawn (and on
both resume paths), so the agent types only paths: `HUDDLE_CLAIMS` (absolute claims dir),
`HUDDLE_INSTANCE`, `HUDDLE_REPO`, `HUDDLE_GUID`. Launch also prepends huddle's own
directory to `PATH` and exports `HUDDLE_EXE` with the full executable path — a protocol
the agent cannot invoke fails exactly like the outage it exists to survive.

**Resume is adopted, not fire-and-forget.** A resumed console is registered as its
instance's live process (`SessionManager.AdoptResumed`, used by both `resume` paths), for
the same reason it gets ledger context: it does real work and claims files. The claim
arbiter's live roster is `Instances.Values.Where(i => i.IsAlive)`, so an unadopted resume
would be absent from it, its claims would be classified as orphans, and the reaper would
archive them mid-session — and since the reap runs *before* the overlap scan, the next
claimant on the same file would archive the resumed agent's claim and then be told there
was no overlap. Adoption means a resumed session shows in `status`, counts as live, and
`stop` kills it, and its exit runs the ordinary stop path that auto-releases its claims.
The one case left unadopted is a `history`/`find` resume of a transcript that belongs to
no tracked instance: there is no identity to adopt it as, `HUDDLE_INSTANCE` is empty, and
`huddle --claim` refuses loudly rather than writing an ownerless claim.

Exit codes: `0` success, `2` usage error (no paths, an absolute path, missing
`HUDDLE_CLAIMS`/`HUDDLE_INSTANCE`), `3` the operation failed and **nothing was recorded**.
Paths must be repo-relative: claims are matched relative to a repo, so an absolute path
records something that can never collide with another session's `src/a.cs`.

Non-goals, decided in the spec and worth restating because each looks tempting: no lock
primitive, no queue of waiters, no scheduler or lease, no ordered acquisition, no
enforcement hook. Turn-taking is a conversation between two named agents, not a mechanism.

### Dispatch flow

1. Architect composes a batch: `{batchId, tasks: [{repo, persona, prompt, files, dependsOn?, id?}, ...]}`.
2. Sends `dispatch-batch` to `_huddle/inbox/`.
3. Orchestrator validates per-task schema, checks self-overlap within the batch and
   duplicate `repo:persona`, then builds a `WorkUnit` per task and `Enqueue`s the batch.
4. Hard errors nack the whole batch, nothing spawns: self-overlap, duplicate session,
   unknown repo/persona, duplicate unit id, unknown `dependsOn`, or a dependency cycle.
5. On success: `AdvanceQueue` dispatches every currently-dispatchable unit (claim + spawn +
   mark Active); units that overlap an active unit or wait on an unfinished `dependsOn`
   remain queued. The ack reports `dispatched=` vs `queued=`.

### Conflict-guarding work queue

`WorkQueue` (`src/WorkQueue.cs`) is a pure, thread-safe scheduler holding work units and
their state (`Queued` / `Active` / `Done` / `Failed`). A unit is **dispatchable** when its
`Files` overlap no `Active` unit's files (case-insensitive) *and* every `DependsOn` is
`Done`. `Enqueue` validates duplicate ids, unknown deps, and cycles (DFS). State and units
persist as one JSON file per unit under `ipc/workledger/queue/` (`IpcManager.QueueDir`), so
the queue survives a huddle restart (`Load` on construction).

The queue advances on two completion signals, each mapping a finishing session back to its
unit via the claim's `BatchId` (which carries the unit id):

- **Auto-release on stop/crash** (`AuditAndReleaseClaims`) — the reliable signal; marks the
  unit `Done` and calls `AdvanceQueue`.
- **Explicit `release`** — when a session's claim file fully disappears (all files released),
  its unit is marked `Done` and the queue advances.

The `queue` console verb prints the live table. *v1 scope (intra-machine, dispatch-batch
path only):* overlap is computed against the queue's own `Active` units — a queued unit is
**not** blocked by files claimed by a plain `start`/`direct` session. Worktrees, a merge
gate, and cross-machine sync are explicit non-goals for v1.

### Release

Agents release after committing a unit of work. Preferred form is
`huddle --release src/Foo.cs`, which writes the ledger directly and works with huddle
down. The mail form still works and does the same thing:

```json
{ "subject": "release", "body": { "files": ["src/Foo.cs"] } }
```

Either way the listed files are removed from the caller's own claim(s). If a claim has no
files remaining, the claim file is deleted.

### Auto-release on session stop

When `SessionManager` fires `SessionStateChanged` with `Stopped` or `Crashed`, huddle:

1. Scans `ipc/workledger/claims/` for claims held by the stopping session.
2. For each claim, runs `git diff --name-only <baseCommit>..HEAD` and `git status
   --porcelain` in the claim's repo root.
3. Compares to declared scope:
   - Committed-since-base or dirty files outside declared scope → logs **scope creep**.
   - Declared files still dirty in the working tree → logs **uncommitted changes at stop**.
4. Deletes the claim files.
5. Marks the claim's work unit `Done` (via the claim's `BatchId` → unit id) and calls
   `AdvanceQueue`, so dependent or previously-overlapping queued units can now dispatch.

The audit is informational — it never reverts agent work. Every `WorkLedgerClaims`
operation takes a private in-process lock, so a concurrent `dispatch-batch` from the
inbox-watcher thread cannot race with an auto-release from the session-state poll thread.
That lock is per-process and therefore not enough on its own now that separate huddle
processes write the ledger: `RecordWithOverlaps` — the path both `huddle --claim` and the
mail `claim` command take — additionally holds a machine-scoped named mutex (`Local\huddle-ledger-<hash-of-claims-dir>`)
across its read-compare-write, so two simultaneous claimants each see the other instead of
both seeing an empty ledger. Every mutex failure (timeout, abandoned, or unopenable)
degrades to recording unguarded; it never refuses the claim.

### Commit-then-release idiom

`personas/_shared.md` tells every persona how to use the release mechanism:

> When you finish a logical unit of work that touches claimed files, commit with a clear
> descriptive message, then send `release <files>`. Hold the claim if you're still
> editing the file in the next unit.

Commit messages become the decision trail. Session crashes are recoverable because
committed work stays in git; uncommitted dirty files get a warning in the log.

## Crash Recovery: the Roster and `recover`

Dead is not disposable. When recovery finds a `state.json` entry whose process is gone
(or fails the PID-identity check), the entry is retained as `"status": "recoverable"`
instead of dropped, and every subsequent save carries the roster forward. The startup
banner announces `N session(s) recoverable — 'recover' to list.`

`recover` lists each dead session with its **persona**, **declared purpose** (captured
at spawn from the task prompt; falls back to the transcript's opening user turn),
**last evidence** (transcript last-write), and the ready-to-run resume command.
`recover <n>` / `recover all` relaunch via the same spawn path as `resume` (still
refusing live sessions), and inherit its adoption: when the transcript belongs to a
tracked instance, the relaunched console becomes that instance's live process; when it
belongs to none, it launches untracked and unable to claim. `recover dismiss` archives.
Nothing is ever deleted — removals append to `logs/state-archive.jsonl`.

**Topology-aware ordering:** workers report to hubs (the session that dispatched them,
the lane-lead holding unread mail). The listing derives lineage from dispatch-batch
files in `_huddle/processed/` (`← dispatched by <sender>`, matched by repo:persona
within a 30-minute spawn window) and marks `HUB (n waiting)` from unread inbox counts —
hubs sort first, so `recover all` brings coordinators up before their workers.

Two companions make the crash class shrink over time:

- **Permission seeding** (`PermissionSeeder`, `"seedPermissions"` in huddle.json,
  default true): startup merges the standing allow-set into every registered repo's
  `.claude/settings.local.json` — merge-only, daily backup before first modify,
  unparseable files left untouched with a loud log.
- **Dispatch shell discipline**: every orchestrator-dispatched prompt is prefixed with
  `SessionManager.ShellDisciplinePreamble` (one command per call, no compound bash),
  because fresh contexts don't inherit persona rules.

## Projects: the Lens

Work belongs to projects, and the model is two layers with distinct authority:

- **Repo layer — standalone truth.** `docs/projects/<slug>/` in the project's primary
  repo: `project.md` (frontmatter: slug/title/goal/status/repos) plus optional typed
  artifacts using the standing terminology — `ROADMAP.md`, `BACKLOG.md`, `SPRINT.md`
  (frontmatter `sprint: YYMM-N`, optional `version:`), `ISSUES.md`. Complete without
  huddle; any reader of the repo understands the project.
- **Huddle map — overlay.** `projects-map.json` beside `huddle.json` adds operator
  notes/links and may hold map-only slugs (project doc not yet written). It never owns
  identity — delete it and the repo layer stands.

Association is both-ways: any doc anywhere joins a project via `project: <slug>`
frontmatter; dispatch-batch tasks and `start-session` bodies carry an optional
`"project"` that stamps the spawned session, its claims (`Project:` line), state.json,
and the recovery roster. Live bindings are derived fresh at read time — nothing stored,
nothing stale.

`projects` lists what was discovered (status, sprint id, live/claim counts,
slug-conflict warnings); `project <slug>` details one (artifacts wired into
`open <n>`, frontmatter-declared children found by a bounded docs scan, live sessions,
claims, recoverables). `projects html [path]` renders the whole lens to a
self-contained HTML status page (inline CSS, file:// links, offline by construction) —
per-project cards with a **usual suspects** table: agents that work/worked on the
project with their last task-focus, merged from the live registry, the crash roster,
and the state archive. Pure render over gathered data: identical inputs, identical
page.

## Capture Replay (`replay` verb)

Verification that an agent does once should not evaporate. The capture-to-test loop turns a
one-off endpoint check into a committed regression test, and the `replay` verb re-runs those
tests on demand. Two halves: a **protocol** (how agents emit captures) and a **runner** (how
huddle replays them).

### The protocol (agent side)

`personas/_shared.md` instructs every persona: when you verify a change by calling an HTTP
endpoint, freeze that check into an MBXHVAL capture suite committed *in the same change*. The
suite lives **in the target repo**, not in a huddle-side store — so collection is just git,
and the test travels with the code it guards.

- Suites go in `<repo-root>/MBXHVAL/tests/suites/captures/<short-name>.yaml`.
- Gates are **invariants**, expressed with the `each:` / `all:` operator: a property asserted
  across every element of a result array, true for *any* data state rather than a frozen
  snapshot. `each:exact:` for an exact filter, `each:contains:` (or "first result matches")
  for a fuzzy/ranked endpoint. Over-asserting (e.g. `exact` on a ranked search) produces
  false-reds; under-asserting is safe.
- Only the fields the verification cared about are listed. Unlisted fields (scores, ids,
  timings) are not asserted.

### The runner (`CaptureReplay.cs`)

`replay <repo>` → `ConsoleUI.HandleReplay` → `CaptureReplay.Run(capturesDir, mbxhvalPath, host, port, log)`:

1. Resolve the repo (aliases work). Captures dir is `<def.Root>/MBXHVAL/tests/suites/captures`.
   Host/port come from the repo's `replayHost`/`replayPort` (defaults `127.0.0.1` / `8080`).
2. Require `mbxhvalPath` (global, in `huddle.json`). Unset → log and bail. The path may be a
   published `mbxhval.exe` (run directly) or an `mbxhval.dll` (run via `dotnet <dll>`).
   The runner is published at https://github.com/halrad-com/MBXHVAL.
3. Shell out, capturing a JSON report to a temp file:
   ```
   mbxhval validate --suite-dir <captures> --host <host> --port <port> --report json --output <tmp> --no-quality
   ```
   Both stdout and stderr are drained so the child can't block on a full pipe buffer.
4. **No report file written** ⇒ treated as a connection failure (the target instance is
   unreachable), distinct from real test failures: `is the test instance at host:port running?`
5. Parse `summary.total/passed/failed/skipped` from the report, delete the temp file, and
   surface `ALL GREEN — N/N passed` or `M FAILED — …`.

Config: `mbxhvalPath` (root, `string?`), `replayHost` / `replayPort` (per-session, on the repo
definition). See `HuddleConfig.cs`. **Minimum `mbxhval` version:** the binary must support
`--suite-dir` and the `each:`/`all:` invariant operators.

## `direct <task>` and Auto-Fire Dispatch

`direct` collapses the "ask architect → copy plan → type delegate commands" loop into
one verb. The user types:

```
> direct refactor the auth flow to use the new token service
```

Huddle writes a message to `huddle:architect`'s inbox with subject `direct-task` and
body `{"task": "...", "autoFire": true}`. The architect persona prompt (see
`personas/architect.md`) instructs:

1. Read `body.task`.
2. Run `git status` in the target repos to factor in dirty state.
3. Plan the work — personas, files, parallel vs sequential.
4. Narrate briefly in the log so the operator can follow.
5. Fire the plan via `dispatch-batch`. Do NOT ask the operator for confirmation.

The operator retains control via `stop`, `broadcast`, or interrupting the architect
session directly. Auto-fire is an escape hatch from the relay problem, not a blind
override — architect still narrates its plan before firing.

`direct` always addresses `huddle:architect` (the architect running in the huddle repo,
canonical name "myapp"). Multi-architect dispatch (directing a per-repo architect)
is a later enhancement.

## Shared Context: context.md

Written to `context.md` next to `huddle.json` on every state change. Format:

```markdown
# Claude Huddle — Active Sessions

Last updated: 2026-02-21 03:15:00

## myapp
- **Root:** C:\Users\you\source\repos\myapp
- **Purpose:** MyApp active development
- **Status:** Running (2h 14m)
- **Started:** 2026-02-21 01:01:00

## docs-site
- **Root:** C:\Users\you\source\repos\docs-site
- **Purpose:** Documentation site deployment
- **Status:** Stopped
```

## Project Structure

```
myapp/
    huddle.sln
    huddle.json                 -- Session config (all repos)
    context.md                  -- Shared session awareness (auto-generated)
    personas/
        _shared.md              -- Shared rules (terminology, conventions)
        architect.md
        reviewer.md
        backenddev.md
        frontenddev.md
        documenter.md
        versioner.md
    logs/                       -- Crash logs (auto-created, per session)
        myapp/
            crash-20260221-031500.log
        docs-site/
            crash-20260221-040100.log
    ipc/                        -- Inter-session communication (auto-created)
        _huddle/inbox/          -- Orchestrator command inbox
        <instance>/inbox/       -- Per-session mailboxes
    publish/                    -- Single-file exe output (build.cmd)
        huddle.exe
    src/
        huddle.csproj
        Program.cs              -- Entry point, CLI parsing, main loop
        HuddleConfig.cs         -- Config model + loading (huddle.json)
        SessionManager.cs       -- Launches/monitors/restarts, persona injection
        SessionInstance.cs      -- Per-session state (process, status, persona)
        ContextWriter.cs        -- Writes/updates context.md
        ConsoleUI.cs            -- Status display and command input
        IpcManager.cs           -- File-based inter-session communication
        TaskTracker.cs          -- In-memory task tracking
        Orchestrator.cs         -- Command processing engine
        CaptureReplay.cs        -- `replay` runner: shells out to mbxhval, parses the report
    build.cmd                   -- Publishes single-file exe to publish/
    DESIGN.md                   -- This file
    CLAUDE.md                   -- Project instructions for Claude Code
```

Everything is relative to the config file location. `personas/`, `logs/`, `context.md`,
and `publish/` all sit next to `huddle.json`. The binary in `publish/` is independent —
run it from the repo root or use `--config` to point at the config.

## Command Reference

| Command | Use Case | Example |
|---------|----------|---------|
| `start <repo> [persona] [prompt]` | Launch a Claude session in a repo's directory. Optionally assign a persona role and initial task. Aliases work. | `start app architect` |
| `stop <instance\|repo>` | Stop a specific instance by ID, or all instances of a repo by name/alias. | `stop myapp` or `stop app:architect` |
| `restart <instance>` | Restart a crashed or stopped instance. Keeps the last persona. Uses `--continue` if recovering from a crash so Claude can `/resume`. | `restart app:architect` |
| `repos` | List all registered repos with their purpose and aliases. Use this to see what's available and what short names you can use. | `repos` |
| `personas` | List available persona roles. Shows all `.md` files in `personas/` (excluding `_` prefixed). | `personas` |
| `status` | Show all active instances with status, uptime, root path, and active persona. Color-coded: green=running, red=crashed, yellow=starting/restarting. | `status` |
| `huddle <group>` | Launch all sessions in a named group. Groups are defined in `huddle.json`. Run without argument to list available groups. | `huddle dev` |
| `send <instance> <msg>` | Send an IPC message to a session's inbox. The session can read it from its mailbox. | `send app:architect check the API docs` |
| `messages <instance>` | Read messages in a session's inbox. Shows sender, type, subject, and body. | `messages app:architect` |
| `delegate "desc" to <instance>` | Delegate a task to a session. Opens a tracked task in the assignee repo's feature ledger, sends it via IPC, and auto-starts the session if it's not running. Refuses when that repo has no writable ledger, rather than dispatching an obligation nothing is recording. | `delegate "fix login bug" to app:backenddev` |
| `direct <english task>` | Hand a free-form task to `huddle:architect` with `autoFire: true`. Architect plans and dispatches via `dispatch-batch` without a confirmation step. | `direct clean up the auth flow` |
| `broadcast [@repo] <message>` | Fan out an informational message to every live session (optionally scoped to one or more repos). The subject is derived from the message. Orchestrator refuses command-type broadcasts. | `broadcast heads-up merge window in 30 min` |
| `shell [<repo>] <data>` | Hand `<data>` to the OS shell (`ShellExecute`) — opens files, URLs, folders. Optional repo sets working directory. Fire-and-forget. | `shell app deploy\\build.cmd` |
| `tasks` | Show all tracked tasks with state (pending, delegated, in-progress, completed, failed), assignee, and description. Materialized from each repo's feature-ledger event log, so ids and obligations survive a restart. | `tasks` |
| `ledger [all\|<id>\|open\|orphans]` | The feature ledger (`docs/ledger/`). No argument = tree for the current repo, open items only; `all` includes accepted and dropped; `<id>` shows one item with its ancestry, children and event history; `open` is every open item across repos oldest first (`--by-age` names that default); `orphans` lists tasks nobody parented. `--repo` / `--owner` scope it. See [The feature ledger](#the-feature-ledger). | `ledger open` |
| `ledger accept <id>` | Record acceptance. Refused unless the item is `delivered`, and refused for a Deliverable whose `accepts` gate is unnamed — huddle does not run the gate, it declines to record acceptance when nobody has said what would prove the work. An orphan task has no Deliverable to gate against, so it is allowed and the event records `ungated`. | `ledger accept F-002` |
| `ledger drop <id> <why>` | Stop a hierarchy item. The reason is required and kept: `dropped` is a terminal state, not a deletion, and an unexplained drop is the audit gap this design closes. | `ledger drop F-004 superseded` |
| `ledger decline <id> [note]` | Hand a task back. Cheap and recorded (§6.2) — the release valve for auto-created rows. Work already under way is `abandoned` instead. | `ledger decline T-107 not mine` |
| `progress` | Show the last scratchpad checkpoint for each running session. Sessions write checkpoints to `logs/<name>/scratchpad.md` as they work. | `progress` |
| `conflicts` | Show file claim overlaps across sessions. Reads both the freeform `workledger/*.md` files and the structured `workledger/claims/` files — written by agents via `huddle --claim` and by the orchestrator for dispatched work; lists active claims even when no overlap. | `conflicts` |
| `replay <repo>` | Run the repo's captured regression tests (MBXHVAL capture suites in `MBXHVAL/tests/suites/captures/`) against its live test instance via `mbxhval`, and report pass/fail. Needs `mbxhvalPath` in `huddle.json`. See [Capture Replay](#capture-replay-replay-verb). | `replay myapp` |
| `docs [plans\|churn]` | List artifacts sessions declared in their scratchpads, newest first. Default = Docs (deliverables); `plans` adds Plans; `churn` adds git working-tree changes (binaries/build output filtered). Clickable (OSC 8) with `open <n>` fallback. See [Document log](#document-log). | `docs churn` |
| `open <n>` | Open the nth entry from the last `docs` listing via the OS file handler. | `open 1` |
| `scan` | Re-scan `_huddle/inbox/` for command files the watcher missed. | `scan` |
| `ver` | Print huddle's build version — branch, commit, build time (`BuildInfo`). | `ver` |
| `reload` | Rebuild huddle and relaunch without killing anything (advanced; confirms first). Spawns `build-restart.cmd` detached, then exits via the graceful `quit` path; the helper waits for huddle to exit, rebuilds, relaunches. Child sessions keep running. | `reload` |
| `quit` | Exit huddle. Running sessions keep running in their own console windows. | `quit` |
| `shutdown` | Stop all running sessions, then exit huddle. | `shutdown` |
| `help` | Show the command reference. | `help` |

## Features Built

| Feature | Description |
|---------|-------------|
| Repo launcher | Define repos once, launch Claude into any by name. No more `cd`-ing around. |
| Repo aliases | Short names and alternative references for repos (`app` for `myapp`). |
| Persona system | Assign a role (architect, reviewer, backenddev, etc.) per session via `--append-system-prompt`. |
| Session context injection | Working directory, project purpose, paths, notes, IPC mailbox info, and scratchpad path injected into every session. |
| Bun crash containment | Each session in its own console window. Crash kills one window, not huddle. |
| Crash logging | Exit code, timestamps, uptime, persona written to `logs/<name>/crash-<timestamp>.log`. |
| Auto-restart | Crashed sessions restart automatically with configurable escalating backoff. Crash-loop protection built in. |
| Shared context | `context.md` written on every state change so sessions can see what else is running. |
| File-based IPC | JSON message files for inter-session communication. Each session gets inbox/outbox paths. |
| Orchestrator engine | Watches `_huddle/inbox/` for commands. Sessions can programmatically start/stop other sessions and delegate tasks. |
| Task tracking | Tasks are rows in the assignee repo's feature-ledger event log, not an in-memory dictionary. Ids survive restarts (`T001` was previously issued 23 times), and a status update for work that really happened no longer nacks "unknown task". |
| Feature ledger | Per-repo `docs/ledger/` — a git-tracked index of work from epic to task that references documents rather than containing them. `ledger.md` is the operator's hierarchy; `events.jsonl` is the orchestrator's append-only log of every task and every transition. Answers what the claims ledger cannot: what was accepted, and what was asked for and never done. See [The feature ledger](#the-feature-ledger). |
| Obligations are durable and automatic | Any `type:"task"` mail between any two agents opens a tracked row keyed on the mail file; moving it to `processed/` timestamps the acknowledgement; a dispatched queue unit is a task keyed on `unit:<id>`; a unit reaching Done is **delivered**, never accepted; anything unacknowledged past `taskAckMinutes` escalates once to the dispatcher and the operator. |
| Session groups | Named groups in config for launching multiple sessions with one command. |
| Scratchpad & checkpoints | Per-session scratchpad for progress notes. `progress` command shows last checkpoint across all sessions. |
| Graceful shutdown | `shutdown` stops all sessions. `quit` exits huddle but leaves sessions running. |
| Broadcast | Fan out a single informational message to every live session. Used for "heads up", "stop what you're doing", etc. Guardrail blocks command-type broadcasts. |
| Shell hand-off | `shell [<repo>] <data>` wraps `ShellExecute` for opening files, URLs, folders, or invoking tools in a repo's working directory. |
| Work Ledger — freeform | Per-session `workledger/<session>.md` files that announce scope. Prompt-driven convention. `conflicts` reads them. |
| Work Ledger — orchestrator claims | Structured `workledger/claims/<batch>-<session>.md` files written by `dispatch-batch` per dispatched unit. Auto-release on session stop with git-diff scope audit; release advances the work queue. |
| `dispatch-batch` | Multi-session dispatch. One IPC command declares a batch of tasks with file scope + optional `dependsOn`; orchestrator validates, enqueues, and dispatches the dispatchable units — overlapping/blocked units queue and dispatch on a later advance. Caller errors (self-overlap, dup session/id, unknown dep, cycle) nack the batch. |
| Conflict-guarding work queue | `WorkQueue` serializes conflicting/dependency-ordered dispatched units (state persisted under `workledger/queue/`); a unit dispatches when its files are free and its `dependsOn` are done. `queue` verb shows it. |
| `release` IPC | Agents hand back claims on specific files after committing a logical unit of work. Empty claims auto-delete. |
| Commit-then-release idiom | Persona prompt convention: commit a unit with a descriptive message, then `release` files done with. Commit messages become the decision trail. |
| `direct` verb + auto-fire | User-facing one-shot. Types English, architect plans and dispatches via `dispatch-batch` without a confirmation step. |
| Capture-to-test + `replay` | Agents freeze HTTP verifications into MBXHVAL capture suites committed in the target repo (invariant `each:`/`all:` gates). `replay <repo>` re-runs them via `mbxhval` and reports pass/fail. |
