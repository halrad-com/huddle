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
