# Persona Tuning

Personas in huddle are paired files:

- `personas/<name>.md` — the system prompt prose (existing)
- `personas/<name>.json` — optional tuning sidecar (new)

Plus `personas/_shared.json` for defaults that apply to every persona.

Missing JSON files = current behavior. Tuning is purely opt-in.

## Quick examples

**Cheap doc editor:**

```json
{
  "model": "claude-haiku-4-5",
  "effort": "low",
  "bare": true,
  "tools": ["Read", "Edit", "Glob", "Grep"]
}
```

**Premium thinker that cannot write code:**

```json
{
  "model": "claude-opus-4-7",
  "effort": "high",
  "disallowedTools": ["Edit", "Write", "NotebookEdit"]
}
```

**Read-only survey config (bare, search-only tools):**

```json
{
  "model": "claude-sonnet-4-6",
  "effort": "low",
  "bare": true,
  "tools": ["Read", "Glob", "Grep", "Agent"]
}
```

## Schema

| Field                  | Type     | Maps to                    | Notes                                                      |
| ---------------------- | -------- | -------------------------- | ---------------------------------------------------------- |
| `model`                | string   | `--model`                  | `claude-opus-4-7`, `claude-sonnet-4-6`, `claude-haiku-4-5` |
| `effort`               | string   | `--effort`                 | `low`, `medium`, `high`, `xhigh`, `max`                    |
| `bare`                 | bool     | `--bare`                   | Strip skills, hooks, auto-memory, CLAUDE.md auto-discovery |
| `pluginDirs`           | string[] | `--plugin-dir` (×N)        | Per-bundle plugin scope                                    |
| `disableSlashCommands` | bool     | `--disable-slash-commands` | All-or-nothing skill kill                                  |
| `tools`                | string[] | `--tools`                  | Hard whitelist of built-in tools                           |
| `allowedTools`         | string[] | `--allowedTools`           | Additive grants (e.g. `Bash(git *)`)                       |
| `disallowedTools`      | string[] | `--disallowedTools`        | Denies (wins over allowed)                                 |
| `mcpServers`           | object   | `--mcp-config` (temp file) | Per-server config inline                                   |
| `strictMcp`            | bool     | `--strict-mcp-config`      | Only listed MCPs load                                      |
| `agents`               | object   | `--agents` (inline JSON)   | Additive custom subagents                                  |
| `permissionMode`       | string   | `--permission-mode`        | `default`, `acceptEdits`, `plan`, …                        |
| `addDirs`              | string[] | `--add-dir` (×N)           | Extra allowed roots                                        |
| `settingsOverride`     | object   | `--settings` (temp file)   | Escape hatch for settings.json keys                        |

## Merging

`_shared.json` first, then `<persona>.json` overrides:

- **Scalars** (model, effort, …) — persona replaces shared
- **Arrays** (tools, pluginDirs, addDirs) — persona **replaces** shared (not concat)
- **Objects** (mcpServers, agents, settingsOverride) — per-key merge

## Hot reload

Not supported. Persona config is captured at spawn time. Edit the JSON, then `stop-session` + `start-session` to apply.

## Adding a new persona

1. Drop `personas/myname.md` with the prose system prompt.
2. (Optional) Drop `personas/myname.json` with tuning.
3. `start-session` with `persona=myname`. No code changes.
