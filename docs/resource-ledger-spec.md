# Resource Ledger

Any OS resource a session spawns that outlives a single tool call MUST be
registered in `ipc/resledger/<safe-name>.json` and cleaned up before the
task is reported done. Safe name = `repo_persona`, same convention as your
ipc directory (e.g. `myapp_architect`).

## Ledger file format

One JSON file per session. Rewrite the whole file on every change.

```json
{
  "session": "myapp:architect",
  "updated": "2026-07-10T19:47:20Z",
  "resources": [
    {
      "id": "gs-probe-edge",
      "type": "process",
      "pid": 7780,
      "port": 9333,
      "what": "headless Edge (CDP) for dashboard narrow-width group-slide probe",
      "artifacts": ["C:/Users/you/AppData/Local/Temp/claude/gs-probe-profile"],
      "cleanup": "taskkill /PID 7780 /T /F",
      "spawnedAt": "2026-07-10T19:47:18Z",
      "cleanedAt": null
    }
  ]
}
```

- `type`: `process` | `port` | `dir` | `other`. `pid` / `port` / `artifacts`
  are optional per type.
- All timestamps UTC ISO-8601.
- A **leak** = an entry with `cleanedAt: null` whose pid is still alive after
  the owning session stopped (or whose artifacts remain on disk).
- Registration and cleanup-marking are plain file writes — no Bash needed, so
  restricted (write-only) personas can comply (lesson of I003).

## Obligations

1. **Register immediately after spawn** — pid, what, why, and the literal
   cleanup command. If you can't write the cleanup command, you shouldn't
   spawn the resource.
2. **Tear down in a finally** — cleanup must run on success AND failure.
   For bash: `trap 'taskkill //PID $SPAWNED_PID //T //F' EXIT` or an explicit
   kill at every exit path.
3. **Verify dead, then mark cleaned** — confirm the pid is gone / port
   closed / dir deleted, then set `cleanedAt`. "I sent a kill" is not
   "it is dead."
4. **Never leave a headless browser running past task end.** Browsers get
   session-scoped profile dirs under `$LOCALAPPDATA/Temp/claude/` named
   `<safe-name>-<purpose>-profile`, deleted at teardown.

## Canonical headless-browser probe recipe (bash)

```bash
EDGE="/c/Program Files (x86)/Microsoft/Edge/Application/msedge.exe"
PROFILE="$LOCALAPPDATA/Temp/claude/${SESSION_SAFE_NAME}-probe-profile"
"$EDGE" --headless=new --remote-debugging-port=9333 \
  --user-data-dir="$PROFILE" --no-first-run about:blank &
EDGE_PID=$!
trap 'kill $EDGE_PID 2>/dev/null; sleep 1; rm -rf "$PROFILE"' EXIT
# >>> register in ipc/resledger/<safe-name>.json here <<<
node probe.mjs "$TARGET"
# trap fires on every exit path; afterwards verify the pid is dead and
# set cleanedAt in the ledger entry
```

## Enforcement

- `scripts/sweep-orphans.ps1` — standalone sweep: reports uncleaned ledger
  entries with live pids plus unregistered headless browsers pointing at
  `Temp\claude` paths. `-Kill` reclaims (opt-in).
- huddle: leak report on session stop + `janitor` console verb
  (`ResourceLedger.cs`). Auto-reclaim only when `reclaimResourcesOnStop`
  is `true` in `huddle.json` (default `false` — report-only; the operator
  decides what dies).

## Why this exists

2026-07-10: a probe's headless Edge, backgrounded with no teardown, was
composited onto the operator's desktop by a Windows/Chromium bug and sat
there for hours — recurring across weeks, misattributed to PowerToys. See
`docs/2026-07-10-headless-edge-ghost-window-incident.md` (I004, B016).
