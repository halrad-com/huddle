# Claude Huddle Roadmap

## History: Seatbelt → Claude Huddle

Seatbelt started as a crash wrapper for Claude Code sessions. It evolved through six
phases into a session orchestrator with IPC and task delegation.

```
Phase 1: Auto-Restart            — CUT (not wanted, crashes should be visible)
Phase 2: Context Injection        — DONE
Phase 3: Session Identification   — superseded by v2 vision
Phase 4: File-Based IPC           — DONE
Phase 5: Session Orchestrator     — DONE
Phase 6: Operational Resilience   — DONE
```

The v1 codebase (.NET 8 console app) is the working orchestrator. It launches Claude
sessions as separate console windows, manages IPC via file-based mailboxes, tracks
cross-session context, handles task delegation, and survives orchestrator restarts via
PID-based session recovery.

**v1 stays the daily driver.** v2 is a parallel codebase with a different architecture.
v1 continues to run in production while v2 is built, and v1 itself is still evolving.

### v1 additions after Phase 6

```
Work Ledger (session file coordination)     — DONE
Broadcast trigger (fan-out + wake)          — DONE
`shell` verb (fire-and-forget)              — DONE
Orchestration streamlining (direct/dispatch)— DONE
Prompt injection (write to consoles)        — DONE
PowerToys CmdPal extension v1               — DONE
Persona tuning (JSON sidecars)              — DONE
Capture-to-test replay                      — DONE
Resource ledger + janitor                   — DONE
```

**Work Ledger.** Per-session ledger files where each active session declares what it's
working on and which files it's touching. `conflicts` flags overlaps; `progress`
summarises active scope.

**Broadcast trigger.** Orchestrator `broadcast` command fans out an informational
message to every live session (optionally scoped to one repo's agents).

**Orchestration streamlining.** The `direct <english task>` verb, `dispatch-batch`
primitive, orchestrator-owned claim files, `release` IPC, and auto-release on session
stop with git-diff scope audit. Declared file scope is enforced at dispatch time;
conflicting work serializes through a queue instead of colliding.

**PowerToys Command Palette extension.** Separate MSIX-packaged extension exposing
huddle's verbs as CmdPal commands. Stateless; reads the IPC tree on disk and drops the
same JSON envelopes the console writes.

**Persona tuning.** Each persona optionally pairs its `.md` with a JSON sidecar tuning
model, effort, tool fence, MCP scope, plugin scope, custom subagents, permission mode,
and extra dirs. `personas/_shared.json` carries inherited defaults. See
`docs/persona-tuning.md`.

Follow-ons under consideration:
- Runtime `claim` verb with yield-for-commit (mid-task scope expansion).
- Per-session git worktree isolation (physical filesystem separation).
- Persona tuning hot reload (apply JSON edits without restart).

---

## Claude Huddle v2: The Orchestration Console

### The Problem

Running multiple Claude Code sessions means multiple console windows. You can't tell
them apart. You can't see them all at once. The orchestrator and agent sessions are
disconnected processes communicating through file-based IPC.

### The Solution: Stream-JSON Protocol

Claude Code supports `--input-format stream-json --output-format stream-json`. NDJSON
over stdin/stdout — text blocks, tool calls, file edits, status events. The protocol is
officially documented at `code.claude.com/docs/en/headless`.

Huddle speaks this protocol directly from C#. No SDK dependency — `System.Text.Json`
+ `Process` with redirected stdin/stdout.

### The Product

```
+--[ claude-huddle ]--[ start myapp:arch ______________ [v] ]---+
|                                                                |
|  +-- Sessions ---+  +-- Activity ----------------------------+|
|  |               |  |                                         ||
|  | myapp         |  |  myapp:architect                        ||
|  |  > architect  |  |  [text] Analyzing ROADMAP.md changes    ||
|  |    active     |  |  [tool] Read ROADMAP.md                 ||
|  |  > backenddev |  |  [tool] Edit ROADMAP.md lines 52-98     ||
|  |    active     |  |  [text] Updated phase 3 to reflect...   ||
|  |               |  |                                         ||
|  | webshop       |  +~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~+|
|  |  > architect  |  |                                         ||
|  |    idle       |  |  myapp:backenddev                       ||
|  |  > frontend   |  |  [tool] Bash: dotnet build              ||
|  |    crashed    |  |  [result] Build succeeded, 0 warnings   ||
|  |               |  |  [text] Build passes. Moving to tests.  ||
|  +---------------+  +----------------------------------------+|
+----------------------------------------------------------------+
```

### Architecture

```
claude-huddle v2 (C# / .NET 8, WPF)
    |
    +-- Shell (WPF window, split layout, command bar)
    |
    +-- AgentHost (one per session) — SERVICE LAYER
    |       spawns: claude -p --input-format stream-json
    |                         --output-format stream-json
    |                         --verbose
    |       redirected stdin/stdout (NOT UseShellExecute)
    |       reads NDJSON lines → HuddleItems (text, tool, error)
    |       writes NDJSON lines ← user prompts
    |       emits C# events: ItemStarted/Updated/Completed, TurnCompleted
    |       crash detection: process exit + exit code
    |
    +-- Data Model
    |       HuddleItem  — atomic I/O unit (text block, tool call, error)
    |       HuddleTurn  — one prompt→response cycle (groups Items)
    |       HuddleThread — durable session container (groups Turns)
    |
    +-- ActivityRenderer (one per visible session)
    |       renders HuddleItems as WPF elements
    |       streaming text, tool calls with args, errors
    |       turn separators, scrollback
    |
    +-- SessionManagerV2
    |       config-driven session launch (huddle.json)
    |       persona prompt building + injection
    |       multi-AgentHost lifecycle management
    |       crash containment + auto-restart with backoff
    |
    +-- IpcManager (carried from v1)
    |       file-based mailboxes (sessions still read files)
    |       FileSystemWatcher for inbox monitoring
    |
    +-- ContextWriter (carried from v1)
    |       cross-session awareness via context.md
    |
    +-- Orchestrator + TaskTracker (carried from v1)
    |       task delegation, progress tracking
    |
    +-- CommandRouter
            routes command bar input
            huddle commands vs session prompts
            MRU history
```

### Key Decisions (Resolved)

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Language | C# / .NET 8 | Keeps existing code. JSON + Process are native. |
| UI Framework | **WPF** | Split layout, data binding, native Windows. |
| Protocol | Stream-JSON (NDJSON) | Documented, no SDK dependency. |
| Data model | Item/Turn/Thread | Clean separation for rendering. |
| v1 migration | Parallel codebase | v1 stays working. |
| IPC | File-based (kept) | Sessions are Claude Code instances that read files. |

### Build Sequence (5 Sprints) — ALL COMPLETE

```
Sprint 1: Protocol Foundation — DONE
   - Stream-JSON C# types (input + output NDJSON messages)
   - Item/Turn/Thread data model
   - AgentHost service layer (process + parser + events)
   - Console POC validating the protocol works from C#

Sprint 2: WPF Shell — DONE
   - WPF app with dark theme
   - Split layout: command bar (top), sessions (left), activity (right)
   - ActivityRenderer: stream events → WPF elements

Sprint 3: Multi-Session Management — DONE
   - SessionManagerV2 (multiple AgentHosts)
   - Session tiles with status colors and uptime
   - Click-to-switch activity panel

Sprint 4: Orchestration Port — DONE
   - Config model, IPC manager, context writer, state persistence
   - Task tracker + orchestrator
   - CommandRouter with all v1 commands + new commands

Sprint 5: Polish + New Features — DONE
   - Command bar MRU history
   - Session tile uptime display
   - Approval policies (5 presets + custom, CLI flag generation)
```

### What's Next

The v2 codebase is feature-complete against the original plan. Remaining work
before merge:

1. **Live protocol validation** — run against a real claude process
2. **Auto-restart implementation** — restart intent is logged but not yet acted on
3. **Worktree-per-agent isolation** — WorktreeManager not yet created
4. **Activity panel replay** — switching sessions shows an empty panel until the next
   event (needs replay from Thread history)
5. **Branch integration** — v2 branch → main
