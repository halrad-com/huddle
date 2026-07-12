# Multi-Huddle Setup — How To

**Status:** Concept (depends on implementation of `docs/multi-huddle-spec.md`)
**Audience:** Operator running Huddle on two or more machines that need to collaborate.

This guide assumes the multi-huddle work in `multi-huddle-spec.md` has shipped — `huddleId` and `ipcRoot` exist in `huddle.json`, and per-Huddle orchestrator mailboxes (`_huddle_<id>/`) and prefixed session safe-names are live.

## When to use this

You have two (or more) workstations, each with its own Claude Code installation, and you want sessions on one to talk to sessions on the other. Examples:

- Big build machine running long compilations + laptop driving the conversation.
- Two devs pair-driving from different desks.
- Primary box + a sandbox VM running risky changes.

This is **not** the right setup for: cloud orchestration, headless agents on a server you can't see, or running sessions on a box you don't have an interactive console on. Sessions still spawn local Claude consoles on whichever Huddle started them — there's no remote spawning.

## Prerequisites

1. **One shared filesystem** both boxes can read and write — most commonly a Windows file share on a NAS, a shared workstation, or a synced folder over a LAN. SMB is the tested path.
2. **A drive letter** mapped to that share on both boxes. UNC paths technically work; mapped drives sidestep path-length and tooling quirks. Recommend the same letter on both boxes for sanity (`Z:` on alpha and `Z:` on beta).
3. **Reasonable clock sync** between machines — within a few seconds is fine. The work-ledger staleness check uses file timestamps, so big skew makes a healthy session look dead. Default Windows time service is enough; just don't disable it.
4. **No cloud-sync providers** (OneDrive, Dropbox, Google Drive) on the share. They fight `FileSystemWatcher` and corrupt half-written JSON. SMB on a real file server is fine; "I'll just sync a OneDrive folder" is not.

## One-time setup

### 1. Pick a Huddle ID per machine

Short, lowercase, distinct. Examples: `alpha`, `beta`, `tower`, `laptop`. Anything matching `[a-z0-9_-]{1,32}`. If you don't set one, Huddle uses the lowercased machine name — fine if your boxes have distinct hostnames.

### 2. Create the shared IPC root

On the share, create:

```
\\fileserver\huddle\
```

Map it on both boxes (e.g., `Z:\` → `\\fileserver\huddle`). Inside, Huddle will create `ipc\` on first run.

### 3. Configure each Huddle's `huddle.json`

On **alpha**:

```json
{
  "huddleId": "alpha",
  "ipcRoot": "Z:\\ipc",
  "claudePath": "...",
  "sessions": [ ... ]
}
```

On **beta**:

```json
{
  "huddleId": "beta",
  "ipcRoot": "Z:\\ipc",
  "claudePath": "...",
  "sessions": [ ... ]
}
```

Same `ipcRoot`, different `huddleId`. The session list can differ — each Huddle only spawns the sessions it knows about.

### 4. Start them in order

Start alpha first, watch its console for the line:

```
Orchestrator: Watching Z:\ipc\_huddle_alpha\inbox
```

Then start beta. It should report:

```
Orchestrator: Watching Z:\ipc\_huddle_beta\inbox
```

If beta complains about a `huddleId` collision or a stale heartbeat, either alpha is misconfigured (same id) or there's an old `_huddle_beta/` dir on the share from a previous run on a now-dead machine — clean it up by hand.

## Daily use

### Starting sessions

Each Huddle starts its own sessions exactly as before. From alpha's console:

```
start huddle feature-dev
```

That session's safe-name will be `alpha_huddle_feature-dev/`, and it lives at `Z:\ipc\alpha_huddle_feature-dev\`. Beta's `huddle:feature-dev` (if you start one) is `beta_huddle_feature-dev/` — no collision.

### Messaging a session on the other machine

From an agent's perspective, nothing changes structurally — you still drop a JSON file into the target's inbox. The only difference is the path includes the huddle prefix. If alpha's `feature-dev` wants to mail beta's `architect`:

```
Path:  Z:\ipc\beta_huddle_architect\inbox\NNN-from-alpha_huddle_feature-dev-<ts>.json
```

The prompt block sessions receive at startup will already show the prefixed safe-names of any sessions live at start time. For sessions that come up later, agents can list `Z:\ipc\` to discover counterparts.

### Console commands — local only

`say`, `focus`, and Alt+Tab-style window operations only work on the Huddle that owns the session. From alpha you cannot `say beta_huddle_architect "hi"` — that injects keystrokes into a console window via `AttachConsole`, which is process-local. To wake a session on another machine, send mail; their inbox-poll wakeup picks it up.

`broadcast` from one Huddle still only fans out to its own live sessions. Cross-machine broadcast is a Phase-3 feature and opt-in; if you've enabled it, a single `broadcast` from alpha hits everyone on both boxes.

### Work ledger

Every session on every machine writes its claim file into `Z:\ipc\workledger\`. Both Huddles see all entries. `conflicts` and `progress` console commands work across the whole shared set automatically — alpha can see that a session on beta is touching the same file you're about to edit.

## What stays local

- Each Huddle's `logs/context.md` lists its own sessions only. There's no merged "everyone everywhere" view in the console — read `Z:\ipc\` directly if you need that.
- Each Huddle's scratchpad files are under its own `logs/<safe-name>/scratchpad.md` on the **local** disk, not on the share. Sessions on different machines can't read each other's scratchpads. If you need shared notes, use the work ledger or send mail.
- Crash logs, state.json, and persona files are local.

## Tearing down

To stop using shared IPC, on each Huddle: remove `huddleId` and `ipcRoot` from `huddle.json` and restart. Sessions started on the next launch will use the local `ipc/` dir. Existing inboxes on the share stay where they are; you can delete the share contents at your leisure.

## Troubleshooting

**Beta doesn't see new mail from alpha.**
SMB `FileSystemWatcher` notifications can drop under load. Each Huddle scans its inbox at startup, but mid-run it relies on watcher events. The fix in spec is a 5-second poll fallback; if you're still on a build without it, restarting the receiving Huddle re-scans and clears the backlog.

**A session shows up "stale" in `conflicts` but is actually running.**
Clock skew. Sync time on both boxes, then have the session touch its ledger file (any update will refresh the timestamp).

**Two Huddles, same `huddleId`.**
Second-to-start refuses with a clear error and a heartbeat path. If a previous Huddle crashed without cleaning up its heartbeat file, delete `Z:\ipc\_huddle_<id>\heartbeat` by hand and retry.

**`File.Move` errors in the orchestrator log.**
Usually means another process has the file open. If it persists, check that no cloud sync is touching the share (see Prerequisites). NTFS-on-SMB works; ReFS-on-SMB has had move-atomicity bugs in some Windows Server versions — switch to NTFS if so.
