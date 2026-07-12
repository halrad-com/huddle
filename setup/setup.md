# Huddle Guided Setup

You are running huddle's guided setup. Interview the user one question at a
time, then DO the work — write the configuration for them, don't tell them to.
Prefer multiple-choice questions with the default stated. Every numbered
section below is independently skippable: offer it, and move on cleanly if
declined.

**Hard rules for this setup session:**
- Write only inside this clone. The one exception is the statusline step
  (section 6), which asks explicitly before touching `~/.claude`.
- Never push, never create remotes, never modify other repositories' contents
  (reading them to infer purposes is fine).
- If `huddle.json` already exists, switch to **review-and-amend mode**: show
  what's configured, ask what to change, and never overwrite silently.
- One question per message. State the default. Accept "skip" everywhere.

## 1. Preflight

1. Run `dotnet --version`. Require an 8.x or later SDK. If missing or older,
   point the user at https://dotnet.microsoft.com/download/dotnet/8.0 and stop
   here — nothing else works without it.
2. Run `./build.cmd` from the repo root. It must finish with 0 errors and
   produce `publish/huddle.exe`. If the build fails, show the error and stop;
   a broken build means a broken install, not a config problem.

## 2. Repo discovery

1. Scan the PARENT directory of this clone for sibling directories containing
   a `.git` folder. Present the list as candidates. Ask which to register, and
   whether there are other root paths to scan (scan those too).
2. For each accepted repo, collect:
   - **name** — short, lowercase (the user types this constantly)
   - **aliases** — optional shorter forms
   - **purpose** — one line. Read the first heading/paragraph of that repo's
     README yourself, propose a purpose, and let the user confirm or correct.
   - **autoStart** — default `false`
3. Write `huddle.json` in the repo root following `template.json`'s shape
   (sessions array + the root-level keys template.json shows). Offer the
   `workspace` catch-all entry (a session rooted at the common parent, for
   cross-repo tasks) — optional.
4. Validate what you wrote: PowerShell
   `Get-Content huddle.json -Raw | ConvertFrom-Json` must succeed silently.

### 2b. Replay wiring (optional)

Ask once: "Any of these repos have a test setup you want wired to huddle's
`replay` verb?" If unsure or no: skip, and mention the README section
"Adding replay to your project" for later.

If yes, per repo walk the two-model choice:
- **HTTP endpoint testing** — the repo exposes a network API. Uses the
  MBXHVAL runner (https://github.com/halrad-com/MBXHVAL — clone, build,
  set the root-level `mbxhvalPath` in huddle.json to the built binary).
  Suites live at `<repo-root>/MBXHVAL/tests/suites/captures/`; set
  `replayHost` / `replayPort` on the repo's entry (use a literal IP, not
  `localhost`).
- **Native testing** — anything else (CLI, library, file-processing). Set
  `replayCommand` on the repo's entry: any command that runs the tests and
  writes `{"summary":{"total":N,"passed":N,"failed":N,"skipped":N}}` to the
  path huddle substitutes for the literal `{output}` token. Exit 0 = green,
  1 = failures, 2 = config error. An existing test harness qualifies if a
  small wrapper script emits that summary — see `template.json`'s
  `example-cli-repo` entry for a worked example.

## 3. Console tour

Using the user's ACTUAL registered repo names, show the daily verbs:
- `repos` — what's registered (they should see what they just configured)
- `personas` — available roles
- `start <repo> <persona> [prompt]` — launch a session, e.g.
  `start <their-first-repo> architect "review the build scripts"`
- `status` — live sessions
- `direct <task in plain English>` — hand a task to the architect persona,
  which plans and dispatches sub-sessions itself

Keep it to one screen. The full reference is in README.md.

## 4. Persona tuning

One line each, then ask: **defaults, or customize?** (default: defaults)

- `architect` — designs, plans, dispatches; read-only toward code by tuning
- `reviewer` — finds bugs and risks; doesn't modify
- `backenddev` / `frontenddev` — build code, backend/frontend flavored
- `documenter` — maintains docs, guarded against unsolicited changes
- `versioner` — version bumps and changelogs only
- `researcher` — scouts, synthesizes, proves concepts, hands off

If customizing: write `personas/<name>.json` sidecars with the fields from
`docs/persona-tuning.md` (e.g. `{"model": "claude-sonnet-5", "effort": "high"}`).
Explain the tool-fence idea (denying Edit/Write makes architect/reviewer
physically read-only) without forcing a decision.

## 5. Permissions tuning

Ask which tier fits their comfort; SHOW the JSON before writing it to
`.claude/settings.json` in this clone:

- **Conservative** — everything prompts:
  ```json
  { "permissions": {} }
  ```
- **Standard (recommended)** — building and committing flow, pushing and
  deleting prompt:
  ```json
  {
    "permissions": {
      "allow": [
        "Bash(dotnet build:*)", "Bash(dotnet test:*)", "Bash(dotnet run:*)",
        "Bash(./build.cmd)",
        "Bash(git add:*)", "Bash(git commit:*)", "Bash(git status)",
        "Bash(git diff:*)", "Bash(git log:*)",
        "Bash(ls:*)", "Bash(cd:*)", "Bash(grep:*)"
      ],
      "deny": [ "Bash(git push*)", "Bash(rm *)", "Bash(del *)" ]
    }
  }
  ```
- **Open** — Standard's allows plus `"Bash(git:*)"`, and no deny list. Warn
  explicitly: this stops prompting on `git push` — agents can push without
  asking. Only for users who accept that.

Note: this covers sessions in the huddle repo. Each registered repo can carry
its own `.claude/settings.json` — that's theirs to manage per repo.

## 6. Statusline (optional — touches ~/.claude, confirm first)

Offer: sessions can show `[repo:persona] branch | model | ctx%` in the
terminal status bar. If accepted, merge into `~/.claude/settings.json`
(create if absent; if a `statusLine` already exists, show both and ask
before replacing):

```json
{
  "statusLine": {
    "type": "command",
    "command": "powershell.exe -NoProfile -ExecutionPolicy Bypass -File <CLONE>/scripts/statusline.ps1"
  }
}
```

Replace `<CLONE>` with this clone's absolute path, forward slashes.

## 7. Finish

1. Launch `publish\huddle.exe` (or tell the user to, in their terminal).
2. Have them run `repos` — they should see every repo they registered. That
   is the success criterion of this setup.
3. Suggest a first real command:
   `start <their-repo> architect "<a small real task in that repo>"`.
4. Point onward: `setup/setup-cmdpal.md` for the optional PowerToys Command
   Palette companion, `docs/first-run.md` for the full manual reference,
   README's "Adding replay to your project" when they want regression replay.
