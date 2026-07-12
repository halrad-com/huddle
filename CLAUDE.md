# claude huddle

Claude Code session orchestrator.

## Project

- **Type:** .NET 8.0 Console Application (C#)
- **Status:** Working — daily-driver orchestrator for multi-session Claude Code work on Windows. v2 (WPF + stream-JSON) is a parallel codebase under active development on `feat/v2-protocol`.
- **Dependencies:** None beyond .NET BCL (System.Text.Json, System.Diagnostics.Process)
- **Design:** See README.md for an intro, DESIGN.md for deep reference.

## Build

```
dotnet build src/huddle.csproj
```

## Build (CmdPal extension)

The PowerToys Command Palette extension lives in `extension/CmdPalHuddle/`. It is a separate MSIX project; it does not change `huddle.exe`.

Dev cycle:
1. Open `extension/CmdPalHuddle/CmdPal.Huddle.sln` in Visual Studio 2022.
2. Build → Deploy `CmdPalHuddle`.
3. In Command Palette, run `Reload` (the entry whose subtitle is "Reload Command Palette Extension").
4. Type `huddle` to see the registered commands.

Compile-only verification from the CLI: `dotnet build extension/CmdPalHuddle/CmdPalHuddle/CmdPalHuddle.csproj`. Sideloading the MSIX still requires Visual Studio's Deploy step.

Prerequisites: Windows 11, PowerToys 0.95+, VS 2022 with the Windows application development workload, Developer Mode enabled.

Settings: open the extension's settings via the gear icon in Command Palette. Configure huddle root path and launch command if auto-detection doesn't find them.

Form-driven write verbs (Direct, Start session, Send message, Broadcast) are not yet wired — they need Toolkit `FormContent` + AdaptiveCards work. The five non-form verbs (Status, Repos, Personas, Conflicts, Launch) are live.

## Run

```
dotnet run --project src/huddle.csproj
```

Or with a specific config:

```
huddle --config path/to/huddle.json
```

## Persona tuning

Each persona may pair its `.md` prompt with an optional `personas/<name>.json` sidecar
that tunes model, effort, tools, MCPs, plugins, and subagents.
`personas/_shared.json` provides defaults that every persona inherits.

See [`docs/persona-tuning.md`](docs/persona-tuning.md) for schema and examples.

Cost telemetry was tried and removed 2026-06-21 — the JSONL tailer + budget enforcement
added complexity without changing how the operator actually worked. `budgetUsd` /
`budgetAction` no longer exist; the `cost` verb is gone.

## Conventions

- Simple, minimal code — no frameworks, no abstractions for one-time operations
- Logging via Console.WriteLine with timestamps
- Config via huddle.json (System.Text.Json deserialization)
- Process isolation via UseShellExecute = true (each Claude session gets its own console)
- Bun crash containment: BUN_CRASH_REPORTER_URL="" on child processes
