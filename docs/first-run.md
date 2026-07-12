# First Run — Setting Up a Fresh Huddle Enlistment

This is the start-to-finish guide for a **new clone** of huddle: clone (or download the
repo zip and extract), build, configure, run. The repo *is* the distribution — there is no
separate installer.

## What you get in a fresh clone

A clone gives you the **base** — source, build scripts, the default personas, and a
**config template**. It does **not** include anyone's personal state. Specifically:

| In the repo (base / default) | Not in the repo (per-machine — you create it) |
|------------------------------|-----------------------------------------------|
| `src/`, `huddle.sln`, `build.cmd`, `build-restart.cmd` | `huddle.json` — your live config (gitignored) |
| `personas/*.md` + `*.json` — the shipped roles | `.claude/settings.local.json` — your Claude Code permissions (gitignored) |
| `template.json` — the config starter | `publish/`, `bin/`, `obj/` — build output (gitignored) |
| `.claude/settings.json` — default permissions | `logs/` — scratchpads, crash logs (gitignored) |
| `scripts/statusline.ps1`, `inspect-jsonl.ps1` | `ipc/` traffic — inboxes, claims, ledger (gitignored) |
| `README.md`, `DESIGN.md`, `llms.txt`, `docs/` | |

Your configuration and the default configuration are kept separate by design: the repo
ships the base, and your machine-specific files are gitignored so they never travel with
the repo.

## Prerequisites

- **Windows 10/11.**
- **.NET 8 SDK** — `dotnet --version` should report 8.x. Huddle builds as a
  framework-dependent single-file exe, so the .NET 8 runtime must be present to run it.
- **Claude Code** installed and on `PATH` (`claude --version`). Huddle launches `claude`
  per session.
- *(Optional)* PowerShell 7 (`pwsh`) for the statusline; Windows PowerShell works too.
- *(Optional, for the Command Palette extension)* PowerToys 0.95+ and Visual Studio 2022
  with the Windows app development workload.

## Steps

### 1. Get the repo

```
git clone <repo-url> huddle
cd huddle
```

…or download the repo zip, extract it, and `cd` into the folder.

### 2. Create your config from the template

`huddle.json` is per-machine and gitignored. Copy the template and edit it to point at
your repos:

```
copy template.json huddle.json
```

Open `huddle.json` and replace the example repos with your own (name, `root` path,
optional aliases). See [Config: `huddle.json`](../README.md#config-huddlejson) in the
README for every field.

### 3. Build

```
build.cmd
```

This produces `publish/huddle.exe` (single file). To build without packaging, use
`dotnet build src/huddle.csproj`.

### 4. Run

```
publish\huddle.exe
```

…or run from source: `dotnet run --project src/huddle.csproj`. You'll get the huddle
prompt:

```
=== claude huddle 0.0.1 ===
Claude Code session orchestrator

>
```

No sessions start automatically unless you set `autoStart: true` in `huddle.json`.

### 5. First commands

```
> repos            # confirm your repos registered
> personas         # see the shipped roles
> start <repo>     # launch a Claude session in one of them
> status           # see it running
> help             # full command reference
```

## Optional setup

- **Statusline** — point Claude Code's statusline at `scripts/statusline.ps1`. See
  [Statusline](../README.md#statusline) in the README.
- **Permissions** — the repo ships a conservative default in `.claude/settings.json`
  (build/test/git/common commands). As you work, Claude Code writes your own approvals to
  `.claude/settings.local.json`, which stays local to your machine.
- **Command Palette extension** — build and deploy `extension/CmdPalHuddle` from Visual
  Studio to drive huddle's daily verbs from PowerToys. See the build steps in
  [CLAUDE.md](../CLAUDE.md).

## Scaling: one instance up to many machines

Huddle is designed to scale along one continuous path — the same model, just more of it:

| Tier | What it is | Status |
|------|------------|--------|
| **1 session, 1 machine** | One Claude session in a crash-isolated window; restart on Bun panic. | Shipping |
| **N sessions, 1 machine** | Many sessions in parallel under one huddle, sharing `ipc/` — messaging, file-claim locking, Work Ledger, `dispatch-batch`, plain-English `direct`. | Shipping (the default) |
| **N sessions × M machines** | Several machines, each running several sessions, coordinating across boxes over a shared IPC directory on a file share. | Shipping (shared file share) |

You don't change tools as you grow: the verbs, personas, and coordination primitives are
the same whether you're running one session or a fleet across machines. Because IPC is just
files, the jump to multiple machines is a matter of *where those files live* — point each
machine's huddle at a config on a shared path (below) and the same messaging, claims, and
ledger span the fleet.

**Single-machine, multi-agent — this is the shipping product and the default.** One huddle
process on one box runs many Claude sessions in parallel. They all share that machine's
`ipc/` directory, so messaging, file-claim locking, the Work Ledger, and `dispatch-batch`
just work — no extra setup beyond `huddle.json`. This is what every command in the README
assumes. If you run three or more sessions at once, the coordination layer is the point.

**Multi-machine, multi-agent — works today via a shared file share.** Conceptually this is
**identical to running multiple agents on one machine** — the only difference is that the
IPC files live on a shared filesystem instead of a local one. Same model, same verbs, same
coordination; just a different location for the files. Nothing else changes.

Mechanically: huddle resolves `ipc/` (and `logs/`, `personas/`) relative to the directory
of its config file. So the way to span machines is to put the working set — including
`ipc/` — on a **shared file share** that every machine maps to the **same drive letter**,
and point each machine's huddle at the same config.

Concrete example (the setup this repo runs on): the repo lives at the UNC path
`\\atom\users\source\repos\seatbelt` and **every** machine maps that share to `S:`. So on
each box:

```
S:\source\repos\seatbelt\publish\huddle.exe
# or: huddle.exe --config S:\source\repos\seatbelt\huddle.json
```

Because all machines see the identical `S:\...` paths, the repo `root` paths in
`huddle.json` resolve everywhere, and `ipc/` under `S:` is one shared directory. Every
machine's sessions read and write the **same** inboxes, Work Ledger, and orchestrator
claims — a session on the laptop can message or hand work to a session on the build box,
and file claims prevent two machines editing the same file. No special protocol; it's the
same file-based IPC, just located where every machine can reach it.

Practical notes for the shared-share setup:

- **Map the same drive letter on every machine** (here, `S:` → `\\atom\users`). That's what
  makes the `root` paths in `huddle.json` valid on all boxes without per-machine configs.
- The share must support normal file create/rename/delete with reasonable consistency
  (SMB on a LAN is fine). The orchestrator already tolerates missed events via its rescan
  backstop.
- Avoid two huddles both claiming the orchestrator mailbox `_huddle` over one shared root.
  The richer scheme that makes many *distinct* huddles coexist on one shared root —
  per-huddle identity (`huddleId` / `ipcRoot`), separate `_huddle_<id>` mailboxes, and
  prefixed session safe-names — is designed but not yet built; see
  [`docs/multi-huddle-spec.md`](multi-huddle-spec.md) and
  [`docs/multi-huddle-howto.md`](multi-huddle-howto.md) (marked **Concept**). Basic
  multi-machine over a single shared `ipc/` does not require it.

A related but separate concern — keeping `~/.claude/` (CLAUDE.md, memory, skills, settings)
consistent across machines so agents share calibration — is captured in
[`docs/claude-config-sync-spec.md`](claude-config-sync-spec.md) (idea stage), with a
real migration log at
[`docs/2026-04-26-workstation-b-claude-config-migration.md`](2026-04-26-workstation-b-claude-config-migration.md).

## Notes for verifying a clean enlistment

After a fresh clone, before you create `huddle.json`, the working tree should contain no
per-machine state: no `huddle.json`, no `.claude/settings.local.json`, no `logs/`,
`publish/`, `bin/`, or `obj/`, and `ipc/` should hold only its structure (e.g.
`ipc/workledger/.gitkeep`), not message traffic. If any of those appear, they leaked past
`.gitignore` — fix the ignore rules rather than shipping them.
