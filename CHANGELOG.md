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

## 2026-07-16

### 2026-07-16.5 — Model floor pinned in `personas/_shared.json` — `8570b3b`

- `_shared.json` now pins a model + effort floor that every persona inherits. Unpinned
  sidecars made every agent inherit the CLI default model, with uneven results; per-persona
  overrides may only move the model up. Edit the pinned model to your preferred tier.

### 2026-07-16.4 — `history` verb: browse and resume past sessions — `863fe7c`

- `history [@repo] [kw] [-Nh/d/w]` lists past sessions from Claude Code transcripts, newest
  first (title · repo · last activity · files touched); `history more` pages; `history <n>`
  shows detail (opening prompt, where it left off, files written — wired into `open <n>`);
  `resume <n>` reopens the conversation in its cwd, refusing sessions that are still live.

### 2026-07-16.3 — Honest stop: no ack until the process is dead — `cfd863a`

- `stop-session` / `stop` no longer report success on a failed kill. The session reverts to
  Running, keeps its claims, and the command nacks with the PID. Previously a surviving agent
  kept working untracked while its claims were released.

### 2026-07-16.2 — Runtime `claim` command: the arbiter covers every path — `0138d88`

- Any session must claim its file scope before substantive edits (`claim` command; mandate in
  `_shared.md`), not just dispatch-batch workers. Atomic check-and-write (`TryClaim`); nack names
  the holder. Include the plan doc's path to lock a whole plan. Queue dispatch acquires through the
  same arbiter, so batches and runtime claimants can't collide — closes the gap where two sessions
  could execute the same plan in parallel unchecked.

### 2026-07-16.1 — Singleton guard: one huddle per root — `6d4bff1`

- A second huddle.exe started on the same root now prints why and exits instead of silently
  double-executing every inbox command (two spawns per dispatch, context.md ping-pong between
  two instances' registries).

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
