# Work Ledger — Spec

## Problem

Multiple sessions on the same repo have no visibility into what each other is actively working on. They know who's running (via `context.md`) but not what files or areas anyone is touching. This leads to:

1. **File conflicts** — two sessions edit the same file, one clobbers the other on commit
2. **Semantic conflicts** — incompatible changes to different files (e.g., API signature changed in backend, frontend built against old signature)
3. **Redundant work** — two sessions solve the same problem independently

## Design

### Core Concept: Per-Session Claim Files

Each session maintains its own file in a shared `workledger/` directory. No shared mutable state — each session only writes to its own file.

```
ipc/workledger/
    myapp_featuredev.md
    myapp_frontenddev.md
    myapp_backenddev.md
    myapp_reviewer.md
```

### Why Per-File, Not a Single Ledger

A single shared file has the same problem we're trying to solve — concurrent writers. Per-session files mean each session owns its write path. Reading is naturally concurrent-safe.

### Entry Format

```markdown
---
session: myapp:feature-dev
updated: 2026-04-05T15:30:00Z
status: active
---

## Working On
WebSocket reconnection logic — adding automatic retry with exponential backoff

## Files
- src/WebSocket/WsHandler.cs
- src/WebSocket/ReconnectPolicy.cs
- src/WebSocket/WsConnection.cs

## Areas
- WebSocket subsystem
- Connection lifecycle

## Branch
feat/ws-reconnect (or: working tree, if on the main branch)

## Depends On
None (or: waiting on myapp:backenddev to finish the IMessageHandler refactor)

## Notes
Modifying the WsHandler message loop. Don't change the IMessageHandler interface until this lands.
```

### Field Definitions

| Field | Required | Purpose |
|-------|----------|---------|
| `session` | Yes | Instance ID — matches context.md |
| `updated` | Yes | ISO 8601 timestamp — staleness detection |
| `status` | Yes | `active`, `paused`, `done` |
| **Working On** | Yes | Brief description of current task |
| **Files** | Yes | Specific files being modified. Most important field for conflict detection. |
| **Areas** | No | Broader subsystem/area names for semantic conflict detection |
| **Branch** | No | Git branch, if using one |
| **Depends On** | No | Cross-session dependencies |
| **Notes** | No | Warnings to other sessions ("don't touch X until this lands") |

### Session Behavior

#### On Task Start (before writing code)

1. Read all files in `ipc/workledger/`
2. Compare intended files/areas against other sessions' claims
3. If overlap detected:
   - **Same file claimed by another active session** → Send IPC message to that session asking about the conflict. Do not proceed with that file until resolved.
   - **Same area but different files** → Proceed with caution. Note the overlap in your own ledger entry under Notes.
   - **No overlap** → Proceed normally.
4. Write your own ledger entry

#### On Checkpoint (at each scratchpad entry)

1. Update your ledger entry — especially the Files list and timestamp
2. If your scope has changed (new files, different area), re-check for conflicts

#### On Task Completion

1. Set `status: done` in your ledger entry
2. Or delete your ledger file entirely

#### On Crash (handled by huddle)

Stale entries are detectable: if `context.md` shows a session as stopped/crashed but its ledger entry says `active`, other sessions should treat the claim as stale and ignore it.

### Conflict Resolution Protocol

When a session detects overlap:

```
1. Check: Is the other session still running? (read context.md)
   - No → Ignore stale claim, proceed
   - Yes → Continue to step 2

2. Check: Is it the same file or just the same area?
   - Same area, different files → Proceed, note it in your ledger
   - Same file → Continue to step 3

3. Send IPC message to the other session:
   {
     "type": "request",
     "subject": "file-conflict",
     "body": {
       "files": ["src/WebSocket/WsHandler.cs"],
       "description": "I need to modify WsHandler for reconnect logic. Are you actively editing it?",
       "priority": "normal"
     }
   }

4. If no response within a reasonable working window:
   - Escalate to user via scratchpad note
   - Do not silently proceed with the conflicting file
```

### Persona Integration

#### Changes to `_shared.md`

Add this section:

```markdown
## Work Coordination

Before starting substantive file changes, check the work ledger at `ipc/workledger/` 
for what other sessions on your repo are working on:

1. Read all `.md` files in the workledger directory
2. If any active entries claim files you plan to modify, coordinate first:
   - Stale entries (session not running per context.md) can be ignored
   - Area overlap with different files: proceed, note it in your entry
   - Same file overlap: send an IPC message to that session before proceeding
3. Write/update your own ledger entry at: ipc/workledger/<your-safe-name>.md

Your ledger file should include:
- What you're working on
- Which files you expect to modify
- Updated timestamp (update at each checkpoint)
- Status: active, paused, or done

Update your entry when your focus changes. Set status to 'done' or delete it when finished.
```

#### Changes to persona files

**Read-only personas** (reviewer, architect): Should *read* the ledger but don't need to write claims (they don't modify files). Add:

```
Check the work ledger (`ipc/workledger/`) to understand what other sessions are 
actively changing — this informs what to review and where conflicts might arise.
```

**Write personas** (feature-dev, frontenddev, backenddev, deployer): Full ledger participation as described in `_shared.md`. No persona-specific additions needed beyond what's in `_shared.md`.

**Orchestrator**: Should read all ledger entries to inform task delegation. Before delegating work that touches specific files, check if another session already claims them.

### Huddle Integration (Optional, Phase 2)

These are improvements to the huddle C# code. Not required for the ledger to work — the Phase 1 approach is purely prompt-driven.

#### `progress` command enhancement

When showing progress, also show active ledger claims:

```
> progress
myapp:feature-dev — 15:30 — WebSocket reconnection (WsHandler.cs, ReconnectPolicy.cs)
myapp:frontenddev — 15:58 — Settings page (settings.html, settings.css)
myapp:reviewer    — 15:07 — (read-only, no claims)
```

#### `conflicts` command (new)

Scan all ledger entries and report overlaps:

```
> conflicts
⚠ myapp:feature-dev and myapp:backenddev both claim:
  - src/WebSocket/WsHandler.cs
```

#### Ledger cleanup on session stop

When huddle stops a session, either:
- Delete its ledger file, or
- Set `status: done` in the entry

#### Ledger path in session prompt

Add the workledger directory path to the injected session context (alongside inbox/outbox paths) so sessions don't have to guess at it:

```csharp
// In BuildBasePrompt, after IPC section:
parts.Add($"Work ledger: {Path.Combine(Ipc.IpcDir, "workledger")}");
```

## What This Doesn't Solve

- **Semantic conflicts across files** — If feature-dev changes a method signature in file A and frontenddev calls it from file B, the ledger won't catch that unless both claim the same area. This requires understanding code dependencies, which is a much harder problem (and where worktree isolation + integration testing at merge time is the real answer).

- **Enforcement** — The ledger is advisory. A session that ignores it will still clobber files. This is acceptable because Claude sessions generally follow prompt instructions reliably. The risk is crash recovery where the session doesn't re-read the ledger.

- **Real-time notification** — Sessions only see changes when they read the ledger. There's no push mechanism. A session that starts editing a file won't know another session just claimed it 30 seconds ago unless it happens to check. Acceptable for the current cadence of work (checkpoints every few minutes).

## Implementation Sequence

### Phase 1: Prompt-only (no code changes)

1. Add Work Coordination section to `_shared.md`
2. Add read-only ledger awareness to reviewer and architect personas
3. Add orchestrator ledger awareness
4. Create `ipc/workledger/` directory
5. Test with a multi-session scenario

**Effort:** Persona file edits only. Zero C# changes.

### Phase 2: Huddle support (code changes)

1. Add ledger directory path to session prompt injection (`SessionManager.cs`)
2. Add `conflicts` command to `ConsoleUI.cs`
3. Enhance `progress` command to show ledger claims
4. Add ledger cleanup on session stop
5. Add ledger directory creation in `IpcManager.cs` initialization

**Effort:** ~100 lines of C# across 3-4 files.

### Phase 3: Git worktree isolation (future)

When the ledger data shows frequent file-level conflicts, add per-session worktree support. The ledger still serves as the coordination layer — worktrees handle isolation, the ledger handles awareness.
