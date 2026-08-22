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

### Shell Discipline

Sessions run under a *gated* command allowlist — no unfettered access, but simple commands that match a pattern run without a prompt. Two habits keep the gate quiet and keep you moving; they are not optional:

1. **Prefer the dedicated tools over the shell.** Read, Grep, and Glob replace `cat`/`grep`/`find`/`ls` pipelines. They never prompt, return structured output, and are *cheaper* than a shell chain — one call, no subshell. Reach for Bash only when no dedicated tool fits.

2. **Keep every Bash call single-purpose and allowlist-shaped.** One command (`git status`, `dotnet build src/foo.csproj`). Do **not** stitch steps with `;`, `&&`, or pipes, and do **not** pipe into an interpreter (`python -c`, `sed -i`, `awk '{...}'`) to save a round-trip. A compound command matches no allowlist pattern, so it prompts every time — and a headless session then hangs waiting for a human. Two simple calls cost a little more; a hang costs the whole task.

3. **Never `cd <dir> && …` to reach another directory.** It is a compound command (so it prompts), and `cd`-ing into a directory before running git can execute untrusted hooks from that directory — an extra security prompt every single time. Use a directory flag or absolute paths instead: `git -C <dir> status`, `git -C <dir> add <file>`, `dotnet build <dir>/foo.csproj`. `git -C …` matches the existing `git:*` allow and never prompts.

Interpreters (`python -c`, `node -e`, `pwsh -Command`) run arbitrary code and are **not** auto-allowed — they prompt. If you're reaching for one to parse or transform text, use Read/Grep/Glob, or write a small named script file and run that instead.

4. **Dispatching a subagent or worker by ANY mechanism (Agent tool, dispatch-batch,
   start-session)? Include rules 1–3 verbatim in its prompt.** Fresh contexts do not
   inherit these rules; an uninstructed subagent WILL spam the operator with
   permission prompts (proven live, 2026-08-09 — three prompts from audit agents).

## Standard Project Terminology

Use these terms consistently across all projects:

- **Roadmap** — The over-arching vision and progression. Where this project is going, what milestones are ahead, the big picture arc. Not tasks.
- **Backlog** — Specific things or ideas, planned or to be planned. Concrete work items that can be picked up, prioritized, and executed.
- **Issues** — Bug list. Specific defects, broken behavior, things that need fixing. Not features, not ideas.

- **Sprint** — What is in flight NOW. Sprints are identified `YYMM-N` (e.g. `2608-1`),
  optionally correlated with a release version. The current sprint lives in `SPRINT.md`;
  closed sprints archive to `sprints/<id>.md`.

Do not conflate these. A roadmap item is not a backlog entry. A backlog entry is not an issue unless something is broken. An issue is not a roadmap item.

## Projects

Work belongs to **projects**: `docs/projects/<slug>/` in the project's primary repo,
holding `project.md` (frontmatter: slug/title/goal/status/repos) plus the typed
artifacts above (ROADMAP/BACKLOG/SPRINT/ISSUES as needed). The repo layer is
standalone truth; huddle's `projects` / `project <slug>` verbs are the lens over it.

- **Creating an artifact that belongs to a project? Declare it**: add `project: <slug>`
  to the doc's frontmatter — that is how the lens finds it, wherever it lives.
- **Dispatching work for a project?** Pass `"project": "<slug>"` in the dispatch-batch
  task / start-session body — the stamp flows to the session, its claims, and the
  crash-recovery roster.
- Huddle never edits project files; agents own them like any other doc (claim first).

## Work Coordination — claims are MANDATORY, not advisory

**Rule: no substantive edits without a claim in the ledger.** On 2026-07-16 two sessions
executed the same plan in parallel with no claims — duplicated hours, corrupted a
product file. The freeform ledger alone did not prevent it.

**Claims go straight to the ledger now — not through huddle.** The old protocol had you
mail a `claim` command to the orchestrator and wait for an ack. On 2026-08-16 huddle was
down: the mail sat unread in `_huddle/inbox/`, no claim was ever recorded, and two
`myapp` agents worked the same files invisible to each other. So
the write path no longer runs through an arbiter. `huddle --claim` / `--release` /
`--ledger` run the **binary** — they read and write `ipc/workledger/claims/` directly and
work whether or not the console is up. Your identity and the ledger's location arrive as
environment variables at spawn, so you type only paths.

**Before your first substantive edit** (any multi-file work, any plan execution,
anything beyond a trivial one-liner):

1. **Read the ledger.** `huddle --ledger <repo>` prints what is claimed, by whom, and
   since when — one line per claimed file (omit the repo to see everything). This is the
   read-before-you-work view, and it works with huddle down.
2. **Claim what you are about to touch.** `huddle --claim <path> [more paths...]`, with
   repo-relative paths. It **always succeeds** — there is nothing to wait for and no ack
   to watch for. Claim your REAL scope up front; extending it later is fine (just run it
   again). **Executing a plan? Include the plan doc's path.** That puts the plan itself
   in the ledger, so a second session about to run the same plan sees you holding the
   plan file before any code collides.
3. **If the output names another holder, do not edit those files.** An overlapping claim
   prints `ALSO HELD BY <session> since <time>: <files>`. The ledger *records*; you and
   the other agent *decide*. Mail the holder, agree who goes first, take turns — nobody
   arbitrates this for you. Weigh the claim time while you're at it: a holder from days
   ago may be a session that has since died, and `huddle --claim` has no live roster to
   tell you (a `claim` mailed to a RUNNING huddle does — it reaps dead holders' claims
   before it reports, so any holder it names is one that still exists).
4. **Release as you finish**, after committing: `huddle --release <path> [more paths...]`
   (see the commit-then-release idiom below). It only releases files from your OWN
   claims; another session's claim on the same file is untouched. Anything you still
   hold is auto-released when your session stops — but only if huddle is up to do it,
   which is exactly why you release explicitly.
5. **Work that arrived via `dispatch-batch` already has its claim** — the orchestrator
   wrote it when it spawned you. Read the ledger, but do not re-claim.

### There are two ways to claim. If one is unavailable, use the other.

They fail in opposite conditions, so you are almost never without a route:

| Route | Works when | Answers | Needs |
|---|---|---|---|
| `huddle --claim <paths>` | huddle is **down** | **immediately** | spawn-time env (`HUDDLE_CLAIMS`, `HUDDLE_EXE`, PATH) |
| `claim` command mailed to `_huddle` | huddle is **up** | asynchronously | nothing but a file write |

**Prefer the CLI when you are about to start editing.** The mail route is asynchronous, so you
can claim, begin work, and only learn several minutes later that someone else holds the file —
this happened on 2026-08-16, where the ack naming the other holder arrived after two edits had
already been made. The CLI tells you before you touch anything.

**Mailing a claim is a first-class route, not a footnote.** Write JSON to
`ipc/_huddle/inbox/` with a unique filename — `{"from":"<you>","to":"_huddle",
"timestamp":"<ISO-8601-UTC>","type":"command","subject":"claim","body":{"repo":"<repo>",
"files":["path/one.cs"]}}` — and wait for `ack:claim`. Use forward slashes; an unescaped
backslash is invalid JSON and the message will not deliver. This path also *reaps dead
holders' claims* before it answers, which `huddle --claim` cannot.

**If `huddle` is not on your PATH, the binary still works — supply the environment yourself.**
Sessions spawned before the ledger CLI shipped have no `HUDDLE_*` variables and never will,
but nothing stops you invoking the executable directly. This is the fastest route when it
applies, because you get the answer immediately instead of mailing and waiting for an ack:

```bash
HUDDLE_CLAIMS=<huddle-root>/ipc/workledger/claims \
HUDDLE_INSTANCE=<your repo:persona> \
HUDDLE_REPO=<your registered repo name> \
<huddle-root>/publish/huddle.exe --claim src/One.cs src/Two.cs
```

`--release` and `--ledger` take the same prefix. Your instance id is the `repo:persona`
exactly as it appears in `logs/context.md`. Paths are **repo-relative** — an absolute path
records a claim that matches nobody and is rejected.

**Reading the ledger never needs tooling at all.** Claims are plain markdown in
`ipc/workledger/claims/`. Glob and Read them directly whenever you want to see who holds
what — that works with huddle up or down, with or without the CLI.

**`nack:claim` no longer means contention** — it means the request was malformed (bad
body, unknown repo, empty file list). Fix the request and resend.

### Before you say you are done

**Report what you did NOT verify, unprompted.** "Edited but not built" is a legitimate
state to be in and to report; silently implying it built is not. The same goes for "tests
not run", "not tried against a live instance", "verified by reading only". A gap you name
costs a sentence; a gap someone discovers later costs their trust in everything else you
said.

**Re-read immediately before editing.** The working directory is shared and siblings are
active, so a file may have changed since you last read it. Read → edit as one tight
sequence; never act on a read from twenty minutes ago.

**When in doubt, ask.** A question to the operator costs one turn. An unauthorised merge, a
wrong-configuration build, or a `git add -A` costs an afternoon.

### If you genuinely cannot claim by either route

**Do not stop working.** A stalled fleet is worse than an unclaimed edit — the claim
exists to stop two agents silently editing one file, not to stop you working. Sessions
spawned before the ledger CLI shipped have no `HUDDLE_*` environment and never will
(environment is fixed at launch), so this case is real and expected.

1. **Read `ipc/workledger/claims/` directly** and check nobody holds your files. This is
   the part that actually prevents the collision, and it always works.
2. **Write your intent into the freeform ledger** at `ipc/workledger/<your-safe-name>.md`
   — repo, files, timestamp, status. Other agents read it.
3. **Say so plainly at the top of your next reply**, and mail your dispatcher if you were
   dispatched, so the gap is visible rather than silent.
4. **Then proceed.**

**The freeform ledger entry** at `ipc/workledger/<your-safe-name>.md` is still
required as human-readable status — what you're doing, expected files, timestamp,
status: active/paused/done — but it is narrative. The claim is the record.

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
5. **Clear the mail you just handled — this is a read receipt, not housekeeping.** Mail
   stays in `ipc/<self>/inbox/` until you clear it, so your inbox *is* the list of what
   you have not read. The operator reads it that way (`backlog` in the huddle console),
   so an inbox you never clear reports you as behind whether you are or not.

   Move the file to `ipc/<self>/processed/` (`git mv`-style move, or a shell `move`). If
   you have no shell, **write a copy** of the file into `ipc/<self>/processed/` with the
   same filename — huddle sees the copy and removes the inbox original for you. Either
   way the receipt is the file appearing in `processed/`.

**Write VALID JSON.** The #1 mail defect is an unescaped backslash in a string value —
Windows paths (`C:\Users\...`, `X:\Library`) and regex (`(\d)`) are invalid JSON escapes
and break delivery. In any JSON string you write (mail, commands, claim files): escape
backslashes (`"C:\\Users\\..."`) or use forward slashes (`"C:/Users/..."`). This applies
to every IPC file you author, including orchestrator commands.

Do NOT call `ScheduleWakeup` for inbox polling — the orchestrator delivers live; polling just burns billed turns. If huddle was down when mail arrived, it stays in `inbox/` and you're nudged for it on next session start.

Broadcast triggers arrive the same way — treat them as ordinary mail.

**Handing off work — say it in a `handoff` mail, not just prose.** When you hand a task to
another agent, send mail with `"type": "handoff"` and a body naming the target, the task,
and where it got to:

```json
{"from":"<you>","to":"<target>","timestamp":"<ISO-8601-UTC>","type":"handoff",
 "subject":"<short task>",
 "body":{"to":"<target>","task":"<what>","state":"<where it got to / what's left>"}}
```

Write it into the target's inbox (`ipc/<target-safe-name>/inbox/`) like any mail — it both
nudges them AND is recorded. Saying "I handed it off" in your console is invisible to the
operator; the `handoff` mail is what huddle announces the moment it lands (`[handoff] you
-> target: task`) and lists under the `handoffs` verb. Every real handoff gets one.

## Commit-Then-Release (for claimed file work)

Whenever the ledger holds a claim for your work — one you wrote with `huddle --claim`, or one the orchestrator wrote for you at `dispatch-batch` — follow this idiom at each logical unit of work:

1. **Commit** with a clear, descriptive message that explains the *why* of the change, not just the *what*. Commit messages are the decision trail — write them like you're writing to a future developer (because you are).
2. **Release** the claims you are done with, *after* the commit:

   ```
   huddle --release <path> [more paths...]
   ```

   Repo-relative paths, same as `--claim`. It writes the ledger directly, so it works with huddle down, and it touches only your own claims.

   The mail form still works if you'd rather send it — a `release` command to `<ipc-root>/_huddle/inbox/`, `{"from":"<you>","to":"_huddle","timestamp":"<ISO-8601-UTC>","type":"command","subject":"release","body":{"files":["path/relative/to/repo.cs","another.md"]}}` — but it only lands if the orchestrator is up to read it.

3. **Hold the claim** if you are still editing the file in the next unit — don't release prematurely. Only release when you are genuinely done with that file for this batch.

If your session stops (normal or crash) without releasing, the orchestrator auto-releases your remaining claims and logs a warning about any file you left dirty in the working tree — but that cleanup needs huddle running, so an explicit `huddle --release` is the only one you can count on. Prefer committing before you stop.

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
