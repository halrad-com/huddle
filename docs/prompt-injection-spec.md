# Prompt Injection — Orchestrator-Driven Turns (v1)

**Status:** Spec
**Author:** huddle:architect (2026-04-23)
**Target:** huddle:feature-dev

## Problem

Agents are suspended between turns. Inbox mail is a dead drop — the only way a
session sees it is if the agent remembers to `ScheduleWakeup` at end-of-turn.
Agents forget (demonstrated: architect had 3 unread mails spanning 2 days, one
of them a dispatch-batch ack it needed).

The user is being used as the keyboard courier:

- Broadcast fires → mail lands in N inboxes → user types "read your inbox" into
  every console window to actually trigger delivery.
- dispatch-batch ack arrives for architect → architect doesn't see it until
  user types something.

Result: orchestration stalls unless the user drives it turn by turn.

## Goal

Huddle (the orchestrator process) drives sessions directly. When something
needs to reach a running session — a broadcast, a dispatch-batch ack, a
one-off operator message — huddle writes into that session's console input
buffer. The newline submits, Claude Code treats it as a user turn.

No polling. No hooks. No user typing.

## Non-goals

- **Not replacing inbox mail between agents.** Agent-to-agent file mail stays
  as-is (it's a log/audit trail).
- **Not a general RPC.** Huddle injects *user-prompt-shaped text* — strings that
  make sense as a turn's opening message. Not binary, not structured commands
  to the LLM.
- **Not v2 stream-JSON.** This is a v1 patch. v2 gets proper stdin ownership.
- **Not a replacement for `start-session`.** Starting a new session still
  spawns with an initial prompt via existing code paths.

## Design

New module: `src/PromptInjector.cs`. Static-ish helper with one public method:

```csharp
public static class PromptInjector
{
    /// Returns true if the injection succeeded.
    public static bool Inject(int targetPid, string text, Action<string> log);
}
```

Internally on Windows:

1. Acquire the `_consoleLock` (static lock — `AttachConsole` is process-global).
2. `FreeConsole()` to detach huddle's own console.
3. `AttachConsole(targetPid)` to attach to the session's console.
4. Build `INPUT_RECORD[]`: one `KEY_EVENT` record per character (down+up),
   followed by a `VK_RETURN` key event.
5. `WriteConsoleInput` on `GetStdHandle(STD_INPUT_HANDLE)` (note: after
   attaching, `STD_INPUT_HANDLE` refers to the *new* console).
6. `FreeConsole()`.
7. `AttachConsole(ATTACH_PARENT_PROCESS)` *or* re-open huddle's original
   console. If restoration fails, log and continue — huddle's logging still
   works because `Console.WriteLine` falls back cleanly when no console is
   attached, but verify.
8. Release lock.

**P/Invoke surface:**

```csharp
[DllImport("kernel32.dll")]   static extern bool AttachConsole(uint dwProcessId);
[DllImport("kernel32.dll")]   static extern bool FreeConsole();
[DllImport("kernel32.dll")]   static extern IntPtr GetStdHandle(int nStdHandle);
[DllImport("kernel32.dll", CharSet=CharSet.Unicode)]
                              static extern bool WriteConsoleInputW(IntPtr h, INPUT_RECORD[] r, uint len, out uint written);
const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;
const int  STD_INPUT_HANDLE = -10;
```

`INPUT_RECORD` / `KEY_EVENT_RECORD` structs are standard — use the layout from
pinvoke.net or Win32 docs. Mark them with `[StructLayout(LayoutKind.Explicit)]`
to pack the union correctly.

### Open question for you to resolve before coding

**How does huddle actually spawn sessions today?** Grep `SessionManager.cs` for
the `ProcessStartInfo` setup. Two scenarios:

- **A: `UseShellExecute = true` in a new console window** → each Claude process
  owns its own console. `AttachConsole(claude_pid)` works; the above design
  applies.
- **B: `UseShellExecute = false` with redirected stdin** → much simpler: just
  `session.Process.StandardInput.WriteLine(text)`. No P/Invoke needed.

The scratchpad says "Process isolation via UseShellExecute = true (each Claude
session gets its own console)" — so (A) is the expected world, but verify.

**Another wrinkle in scenario A:** The PID you track may be an intermediary
(explorer, cmd, conhost) rather than `claude.exe` itself. If so,
`AttachConsole(trackedPid)` attaches to the wrong console or fails. Check by
logging `Process.GetProcessById(trackedPid).ProcessName` after spawn. If it's
not `claude` or `node` or the CLI binary, walk child processes to find the
real Claude Code process. Windows has no clean "find my children" API;
`wmic process where parentprocessid=X` works, or enumerate and match by PPID
via `System.Management`.

Don't ship without verifying the attached console is actually driving the
agent we want.

## Integration points

### 1. ConsoleUI — new `say` verb

`say <instanceId> <text>` — manual operator tool. Resolves `<instanceId>` via
`SessionManager.TryResolve` (same logic as other verbs), looks up the
`SessionInstance.Process.Id`, calls `PromptInjector.Inject`.

Add to help text (line ~156 of ConsoleUI.cs, in the command list). Keep
grammar consistent with `broadcast`: first token = target, rest = message.

Permit shorthand: `say architect "check your inbox"` should resolve
`architect` using the same friendly-name resolution existing verbs use.

### 2. Orchestrator — broadcast delivery

`HandleBroadcast` (Orchestrator.cs:252) currently only writes IPC mail. Change
the per-target fan-out loop to **also** inject:

```
foreach target:
    ipc.Send(...)                         // keep: audit trail / log
    PromptInjector.Inject(pid, text)      // new: actually wake the agent
```

Injection text should be a one-line nudge that points at the mail, not the
full body (bodies can be 64 KB — too much to inject). Something like:

```
[huddle broadcast from <msg.From>] <subject> — check your inbox
```

The agent reads that, goes to its inbox, finds the full message. Simple and
keeps injections small.

Count `injected` separately from `delivered` (delivered = mail written,
injected = console poked). Report both in the ack.

### 3. Orchestrator — dispatch-batch ack

dispatch-batch replies go to `msg.From`'s inbox. Currently same problem — the
architect (the usual caller) doesn't see the ack. Inject the ack string
directly. `SendAck` and `SendNack` should route through PromptInjector when
`msg.From` resolves to a live session.

Find `SendAck`/`SendNack` helpers and update them to: (a) always write the
mail file (audit trail), (b) if `From` resolves to a live `SessionInstance`,
also inject a short one-liner:

```
[huddle ack:dispatch-batch] <bodyText>
```

This unblocks the architect's workflow. When I fire a batch and get an ack,
the ack arrives as a turn in my console — I can react immediately.

## Edge cases

- **Session mid-typing:** if the user has focus on a session's window and is
  typing, injected characters interleave. Accept this. Low frequency, no data
  corruption.
- **Multiple injections in quick succession:** the `_consoleLock` serializes
  them. No race. Brief user-visible delay is fine.
- **Target process exited between resolve and attach:** `AttachConsole`
  returns false. Log once, move on. Don't retry.
- **huddle's own console corrupted after Free/Attach cycle:** verify by
  printing a line after every injection and looking at huddle's log output.
  If corruption happens, fall back to using a child helper process instead —
  see next.
- **Fallback: child helper.** If the in-process Attach/Free dance destabilizes
  huddle's own console (some Windows versions are finicky), spawn a tiny
  helper exe that takes `--pid X --text "..."` and does the Attach+Write+exit.
  Huddle invokes this via `Process.Start` per injection. Cost: ~30ms per
  inject. Acceptable.

## Build plan

- **T1: PromptInjector.cs** — P/Invoke wrappers + `Inject` method + lock.
  Standalone; no callers yet. Smoke: unit test or tiny `Program.Main`-style
  scratch that injects "hello" into any running console PID you give it.
  Verify it works against a Claude Code session manually before integrating.
- **T2: ConsoleUI `say` verb** — add handler, wire help text, resolve
  instance, call `Inject`. Easy to test interactively from the huddle console.
- **T3: broadcast integration** — update `HandleBroadcast` to inject
  alongside mail write. Add `injected` counter to the ack body.
- **T4: ack/nack injection** — update `SendAck`/`SendNack` to inject when
  target is live. Verify architect (me) sees a dispatch-batch ack as a turn
  without any manual intervention.

## Verification

Must be demonstrated with the architect (me) running:

1. From huddle console, `say architect "hello from huddle"`. Architect's
   console should show a new turn containing that text. Architect processes
   it and responds.
2. From huddle console, `broadcast test "tick-tock"`. Every live session
   should see a `[huddle broadcast …] tick-tock — check your inbox` turn
   appear in its console.
3. Architect fires a `dispatch-batch` command. The `ack:dispatch-batch` reply
   should show up as a turn in architect's console within 1-2 seconds of the
   orchestrator processing it — no user keystrokes in between.

If all three work, this is done. If any don't, debug before claiming
completion (read: use the systematic-debugging skill, don't thrash).

## Files to claim

- `src/PromptInjector.cs` (new)
- `src/ConsoleUI.cs`
- `src/Orchestrator.cs`
- `docs/prompt-injection-spec.md` (this file; hold while iterating)

## Out of scope for this batch

- v2 stream-JSON migration (separate effort)
- Hook-based inbox injection (the weaker fix — skip, this replaces it)
- Retry logic / queuing for injections (keep MVP synchronous)
- Any change to how sessions are spawned (only observe, don't refactor)
