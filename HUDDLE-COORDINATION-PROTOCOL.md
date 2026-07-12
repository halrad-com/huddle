# Huddle Coordination — Check-Out / Check-In

**Load at every session start.**

This is check-out / check-in source control, not advisory coordination. Treat every file like it's under a reserved checkout.

---

## The model

1. **Get a task** from the dispatcher (`_huddle`) or a plan. Don't self-pick from BACKLOG.
2. **Check out the files** you need to edit — exclusive lock. Nobody else edits them until you check in.
3. **Make the changes.**
4. **Check them back in** — commit + release lock.
5. If two tasks touch the same file, the second waits, or merges when both are done.

## Two check-out mechanisms

Huddle supports two parallel claim mechanisms. Both exist, both count.

### A. Orchestrator claims (preferred when work came via `dispatch-batch`)

When architect fires `dispatch-batch`, huddle writes structured claim files to `ipc/workledger/claims/` on your behalf. You didn't write them, but you own them: the files listed in your claim are exclusively yours until you release them or the session stops.

**Check in (preferred path):**

1. Commit your unit of work. Clear message, explains *why*.
2. Send a `release` IPC to `_huddle/inbox/` naming the files you're done with:

   ```json
   {
     "from": "<your-instance-id>",
     "to": "_huddle",
     "timestamp": "<ISO-8601-UTC>",
     "type": "command",
     "subject": "release",
     "body": { "files": ["src/Foo.cs", "docs/bar.md"] }
   }
   ```

3. Empty claims auto-delete. If you're still editing the file in the next unit, hold the claim — don't release prematurely.

If your session stops without releasing, huddle auto-releases remaining claims and logs a warning about any file you left dirty in the working tree. Prefer committing before you stop.

### B. Freeform ledger (for work that didn't come through `dispatch-batch`)

If you started as a standalone session (not part of a batch), or you're coordinating loosely with other sessions by convention, write a freeform ledger file at `ipc/workledger/<your-safe-name>.md`:

```markdown
# <your-instance-id>

- **Working on:** <short description>
- **Files modified:**
  - path/to/file-a.cs
  - path/to/file-b.md
- **Files I will NOT modify (reserved for X):** ...
- **Updated:** <date>
- **Status:** active | paused | done
```

Other agents read this file before editing. `conflicts` in the huddle console shows both freeform ledger and orchestrator claims.

**Check in (freeform path):** commit, then update your ledger file — either mark the files done, or delete the entry if the task is finished. Set status to `done` or remove the file when your session ends cleanly.

## The rules

- **Check out before you edit.** Either via the orchestrator claim (dispatched work) or via your freeform ledger (standalone work). No silent edits on shared files.

- **Check in means commit.** Stage *your* files by name, commit, then release (via IPC if dispatched, or by updating the ledger file if freeform).

- **Never revert another session's work.** Not their unstaged edits, not their commits. If you screwed up your own work and a file is shared, you merge — keep their changes, drop yours. `git checkout HEAD -- <file>` and `git checkout <rev> -- <file>` are OFF LIMITS against any file you don't have checked out. No exceptions.

- **If a file you need is checked out to someone else,** either work on a different file, or send them an IPC and wait for them to check it in. Do not edit alongside them. `dispatch-batch` enforces this at dispatch time — the orchestrator rejects overlapping claims with FIFO semantics. Architect must re-plan when this happens.

- **If you made a mistake on your own uncommitted work,** use `git checkout HEAD -- <your-checked-out-files>` — but ONLY files in your own claims list. Every other file is someone else's.

- **On task completion:** commit, release claims (IPC or ledger), send `task-complete` to `_huddle`.

## The one-sentence version

**Your files to edit are the ones you checked out. Edit those. Commit those. Don't touch anyone else's.**
