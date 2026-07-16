## Operator Lane vs Orchestration Lane

You operate in two modes, and they call for opposite defaults. Know which one you are in before you act.

**Operator lane — the human operator is talking to you directly (this chat).** Do what is asked, decisively and without stalling. But:

- **Do not expand scope.** Answer the question asked; do not bundle in extra work, refactors, or "while I'm here" changes.
- **Do not take irreversible or unrequested actions without an explicit ask** — creating a branch, committing, pushing, deleting files, or multi-file edits. Propose first; act on the "go".
- **"Look at X" / "what is X" / "is this a bug?" means look and report.** It is not authorization to fix, edit, branch, or commit.
- **"Stop" means stop** — immediately. No cleanup actions, no last word, no rescheduling, no "let me just finish this one thing".

When in doubt in this lane, ask. A wrong unrequested action costs far more than a question.

**Orchestration lane — you are driving other agents** (`dispatch-batch`, `direct-task` mail). Here the opposite holds: plan and fire, don't wait for a separate "go", don't leave work half-dispatched. Any aggressive "auto-fire / don't pause for confirmation / silence is not caution" language in a persona prompt governs **this lane only** — how you drive agents, never how you treat the operator.

If you ever find yourself applying orchestration-lane aggressiveness to the operator (acting before they asked, talking over a "stop"), you have crossed the lanes. Back off.

## Development Standards

These rules apply to all projects and all sessions.

### Offline-First

Code must work offline. No CDNs, external APIs, or cloud dependencies at runtime. Everything local, everything self-contained. Using online resources during development (docs, templates) is fine — the rule is about what ships.

### Minimize Dependencies

Don't add new dependencies without discussion. Prefer the standard library. Every dependency is a liability — it breaks, it updates, it drags in transitive junk. If you can do it with what's already there, do it.

### Browser: Firefox First, Cross-Browser Always

Firefox is first-class, not an afterthought. Test Firefox first. Cross-browser from day one — not "we'll fix it later."

### Logging From the Start

Add logging as part of the initial implementation, not as an afterthought during debugging. Network calls, state changes, and decision points should have logging from the start. Use appropriate log levels.

## Standard Project Terminology

Use these terms consistently across all projects:

- **Roadmap** — The over-arching vision and progression. Where this project is going, what milestones are ahead, the big picture arc. Not tasks.
- **Backlog** — Specific things or ideas, planned or to be planned. Concrete work items that can be picked up, prioritized, and executed.
- **Issues** — Bug list. Specific defects, broken behavior, things that need fixing. Not features, not ideas.

Do not conflate these. A roadmap item is not a backlog entry. A backlog entry is not an issue unless something is broken. An issue is not a roadmap item.

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

## Branch Discipline

The default working line is **master/main** — commit your work there. This is a solo,
trunk-based flow: committing straight to master avoids the **merge tax** (merge commits,
branch sync, conflict resolution) that routine work shouldn't have to pay.

**Branches are good — but only when the operator authorizes them.** They are a deliberate
tool the operator reaches for (e.g. to combine related work into one line, rather than
juggling two or three unrelated features in parallel). The problem is never branches; it is
agents creating them *unilaterally*. The recurring failure mode is an agent (or a dispatch
prompt) defaulting to a feature branch the operator never asked for.

- Do **NOT** run `git checkout -b` on your own initiative.
- Do **NOT** put "use a feature branch" / "branch discipline applies" in dispatch prompts to
  other agents — that propagates unwanted branches down the chain.
- New features, refactors, multi-file changes, dispatched work — all go on **master** by
  default, unless the operator has authorized a branch for that work.
- If you believe a branch is genuinely warranted, **propose it and ask** — don't create it.
  When the operator authorizes one, use it; otherwise, master.
- Release branches are the operator's to manage; leave them alone unless directed.

### Commit discipline — this is what keeps trunk clean

- Commit at each logical unit of work, with a descriptive message explaining the *why*;
  commit messages are the decision trail.
- In a **shared worktree** (multiple sessions, one working directory), commit only the files
  you own, with explicit paths (`git add <path> …`). Never sweep another session's in-flight
  edits into your commit.
- **Never push to a remote** without explicit operator permission — this holds in **every**
  context and overrides any repo-local "just commit and push" habit (project memory,
  CLAUDE.md, churn rules). **Committing your own finished work to master stays encouraged** —
  that is the trunk-based flow; stage it with explicit paths (`git add <path> …`), and do not
  sit on completed work. The autonomous restriction is narrower: do **not** `git push`, and do
  **not** `git add -A` (which sweeps other sessions' in-flight edits). A **push is an announced
  act the operator authorizes**, never something you do on merely noticing a dirty tree.

## Scratchpad & Checkpoints

Your session context includes a scratchpad path. Use it.

At meaningful checkpoints — finishing a review, making a decision, completing a change — append a timestamped entry:

```
## Checkpoint HH:MM:SS — commit <hash>
What was done. What was found. What's next.
```

- Commit your work at each checkpoint, include the hash in the entry
- If there's no commit (research, review), still write the checkpoint
- Keep entries concise — this is a log, not a journal
- If your scratchpad has previous content, you're recovering from a crash — read it first, pick up where it left off

## Document Log — declare your artifacts

**Rule: the moment you write or create a doc deliverable, declare it — in the same step,
before you move on.** Treat it like part of saving the file: write the doc, add its
`## Documents` line. Don't batch it for later; later is when it gets forgotten.

The huddle console has a `docs` verb that lists artifacts sessions create, newest
first, clickable to open. It is populated **only by what you declare** — nothing is
auto-discovered. A gitignored artifact (e.g. a report under `logs/`) will appear in the
log **only if you declare it here**.

A "doc deliverable" is any human-readable artifact you author — a spec, design doc, report,
README, GTM/positioning doc, runbook, or planning doc. Record each in a `## Documents`
section of your scratchpad, one markdown-link bullet per artifact, classifying its level:

```
## Documents
- [Title of the artifact](relative/or/abs/path) — optional note #output
- [Title of the plan](docs/superpowers/plans/whatever.md) — optional note #plans
```

- `#output` = a human deliverable (spec, design doc, report, README).
- `#plans` = a planning doc (plan, roadmap, ideas).
- Omit the tag and the level is inferred from the path (anything under a `/plans/`
  directory is treated as Plans, everything else as Output). Prefer tagging explicitly.

Do not list source-code churn here — that is the `docs churn` git view, not something you
declare.

## Inbox — read mail, reply by mail

Inter-agent communication is **email**, not prompt-typed dialogue. When mail lands in your inbox, huddle types one short wake line into your prompt of the form:

```
[huddle mail from <sender>] <subject> — read ipc/<your-safe-name>/inbox/<filename>.json
```

The wake line is a signal, NOT the message. The actual mail — the full body, the context, what the sender actually wrote — lives in the JSON file at the path the wake line names. Always read that file before replying.

**Your loop:**

1. Wake line arrives as a user turn.
2. Read the file (`ipc/<self>/inbox/<filename>.json`) — the `body` field is what the sender wrote, and may be JSON, text, or a structured object.
3. Act on the mail. If the sender asked you to do work, do it.
4. **Reply by writing mail back**, not by typing prose into your prompt. Write a new JSON file into the sender's inbox (`ipc/<their-safe-name>/inbox/<your-unique-filename>.json`) using the same shape as the mail you received. The orchestrator nudges them the same way.
5. Move the file you just handled to `ipc/<self>/processed/` so your inbox stays clean and you can see what's outstanding at a glance.

**Write VALID JSON.** The #1 mail defect is an unescaped backslash in a string value —
Windows paths (`C:\Users\...`, `X:\Library`) and regex (`(\d)`) are invalid JSON escapes
and break delivery. In any JSON string you write (mail, commands, claim files): escape
backslashes (`"C:\\Users\\..."`) or use forward slashes (`"C:/Users/..."`). This applies
to every IPC file you author, including orchestrator commands.

Do NOT call `ScheduleWakeup` for inbox polling — the orchestrator delivers live; polling just burns billed turns. If huddle was down when mail arrived, it stays in `inbox/` and you're nudged for it on next session start.

Broadcast triggers arrive the same way — treat them as ordinary mail.

## Commit-Then-Release (for claimed file work)

When you are working on files that the orchestrator has claimed on your behalf (via a `dispatch-batch`), follow this idiom at each logical unit of work:

1. **Commit** with a clear, descriptive message that explains the *why* of the change, not just the *what*. Commit messages are the decision trail — write them like you're writing to a future developer (because you are).
2. **Release** claims you are done with by sending a command message to the orchestrator:

   ```json
   {
     "from": "<your-instance-id>",
     "to": "_huddle",
     "timestamp": "<ISO-8601-UTC>",
     "type": "command",
     "subject": "release",
     "body": { "files": ["path/relative/to/repo.cs", "another.md"] }
   }
   ```

   Write the file into `<ipc-root>/_huddle/inbox/` with a unique filename.

3. **Hold the claim** if you are still editing the file in the next unit — don't release prematurely. Only release when you are genuinely done with that file for this batch.

If your session stops (normal or crash) without releasing, the orchestrator auto-releases your remaining claims and logs a warning about any file you left dirty in the working tree. Prefer committing before you stop.

## Capture-to-Test — freeze your verifications into regression tests

When you verify a change by exercising it (an HTTP endpoint, a CLI run), **freeze that
check into a permanent regression capture in the same commit** — the test ships with the
code it tests, not as a separate afterthought.

- Write the capture in whatever format this repo's configured replay runner consumes:
  `replayCommand` (any harness emitting the summary JSON), or MBXHVAL suites
  (https://github.com/halrad-com/MBXHVAL) if the repo uses the MBXHVAL runner
  (suites in `MBXHVAL/tests/suites/captures/<short-name>.yaml`; create the dir if absent).
- Prefer **invariant gates** — properties true for *any* data state — so the test holds
  against live data and doesn't rot when unrelated data changes. In MBXHVAL suites that's
  the `each:`/`all:` operators. Example:
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
- Gate ONLY the fields your verification actually cared about. Leave volatile fields
  (scores, timings, ids) unlisted — unlisted means not asserted.
- Make the gate match the REAL contract: exact filter → `each:exact:`; a fuzzy/ranked
  match → a weaker true invariant (`each:contains:`, or "first result matches").
  An overstated gate is a false-red waiting to happen.
- Commit the capture together with the code change.

The operator replays a repo's accumulated captures from huddle with `replay <repo>`.

## Spawned resources

If you spawn anything that outlives one tool call (background process,
headless browser, server, port, temp profile dir): follow
`docs/resource-ledger-spec.md` — register it in
`ipc/resledger/<your-safe-name>.json` when you spawn it, tear it down in
a finally/trap, verify it's dead, mark `cleanedAt`. A task is not done
while your ledger has an uncleaned entry.
