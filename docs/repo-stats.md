# Repo stats — the `stats` verb

Answers *"what repos are being used, when, by who"* from corpora huddle already holds,
so it reports on the past several days rather than only from the moment it shipped.

**Spec:** [`superpowers/specs/2026-08-22-repo-stats-design.md`](superpowers/specs/2026-08-22-repo-stats-design.md)

## Grammar

```
stats                    every repo, since statsSinceDays (default 7)
stats <repo>             one repo
stats --who              pivot by session instead of by repo
stats --since 30d        override the window (d = days, h = hours; a bare number is days)
stats html               self-contained page with the commit heatmap, written to logs/stats.html
```

Flags combine: `stats myapp --since 12h`, `stats --who --since 30d`.
The first argument completes from registered repo names.

## What a repo block says

```
myapp   C:\Users\you\source\repos\LIB\myapp
  remotes    github.com/halrad-com/otherapp (github)     dev.azure.com/contoso/LIB (origin)
  movement   104 pushes, 0 fetches      last push 08:48 → dev.azure.com/contoso/LIB (9705a8c)
  commits    445 local, 0 unpushed     +161479 / -5017 lines   last 08-23 01:48
  churn      2 dirty files
  who        myapp:architect      exact    (cred dev.azure.com 08-21 22:07)
             myapp:frontenddev    inferred (live at push 2bf09c9)
  time       3 sessions · 285 session-hours · idle gap 0m
  work       units 2 · mail 41 · handoffs 3 · open claims 1
  health     ok
```

| Row | Source |
|---|---|
| `remotes` | `git remote -v`, each URL reduced to a stable `host/org/repo` identity |
| `movement` | `<git-common-dir>/logs/refs/remotes/**` — git's own record, survives huddle being down |
| `commits` | `git log --since --pretty=%at --numstat`, `git rev-list --count @{upstream}..HEAD` |
| `churn` | `git status --porcelain` |
| `who` | credential drops, claims, dispatched units, and roster overlap — see below |
| `time` | `logs/state.json` session windows, clipped to the `--since` window |
| `work` | queue units, mail file counts, `logs/handoffs.jsonl`, open claims |
| `health` | a live session over 48h with nothing attributable to it |

A registered root that is not a git checkout gets the session / claims / mail rows and
a `not a git repo` note. That is never an error.

## Remotes are named by identity, not by local name

Every repo has an `origin`, so the remote *name* identifies nothing across a fleet —
and `myapp` carries a second remote (`github`) pointing at a repo a push must
never reach. `RemoteIdentity` reduces a URL to `host/org/repo`:

```
https://contoso@dev.azure.com/contoso/LIB/_git/LIB   → dev.azure.com/contoso/LIB
https://github.com/halrad-com/otherapp.git          → github.com/halrad-com/otherapp
git@github.com:halrad-com/huddle.git               → github.com/halrad-com/huddle
```

**Userinfo is stripped.** The `contoso@` in the Azure URL must never reach the console,
`logs/huddle.log`, or the HTML page. A URL that cannot be parsed yields `null` and the
caller falls back to the previous format, so nothing regresses.

The console movement line uses the identity too:

```
[git] myapp pushed to dev.azure.com/contoso/LIB (master 97a3aa8)
```

## Attribution is honest or it is wrong

Every agent commits as the same git identity (`Dev User`), so **a reflog line or
a commit can never say who**. Attribution therefore has two grades, and both are always
labelled in every rendered line — console and HTML:

- **exact** — a signal that names the instance: a credential request, a claim, a
  dispatched unit, or a movement where the roster shows exactly *one* live session in
  that root at that moment.
- **inferred** — roster overlap: sessions whose root matched and whose lifetime covered
  the event. Rendered as a **list of candidates**. Two candidates stay two; they are
  never collapsed into a single name.

An instance with any exact evidence is never also listed as inferred.

## `logs/git-activity.jsonl`

Append-only, never deleted, same pattern as `logs/handoffs.jsonl`. Written when the
`gitActivityLog` setting is on (default true).

```json
{"ts":"2026-08-21T22:07:41-07:00","kind":"auth","instance":"myapp:architect","session":"5050d332","host":"dev.azure.com","protocol":"https"}
{"ts":"2026-08-21T22:07:46-07:00","kind":"move","repo":"myapp","verb":"push","remote":"origin","identity":"dev.azure.com/contoso/LIB","branch":"master","sha":"97a3aa8"}
```

Null fields are omitted. A malformed line is skipped, not fatal.

The credential drop is the **one exact who-signal huddle has** — it names the instance,
the host and the time — and before this it was deleted the moment it was logged.
Movements were console lines only, so nothing survived a restart. A movement entry
carries the reflog line's *own* timestamp, not the clock, so replay stays exact.

## `stats html`

Writes `logs/stats.html` and prints the path. Self-contained: inline CSS, inline SVG,
no script, no CDN — it opens offline. Light and dark are both defined.

The **heatmap** is a contribution graph computed from the local clone. Azure DevOps has
no equivalent of GitHub's graph and some registered roots are not hosted at all, so
this is the only view uniform across the fleet — and it counts commits that were never
pushed, which is where nearly all agent activity lives. One row per weekday, 52 week
columns, five colour buckets (0, 1, 2–3, 4–6, 7+).

The graph always spans a year regardless of `--since` — a 7-day window would render 51
empty columns — but **only the graph is widened**; every other figure stays inside the
window. That year-wide pass reads commit times only (`--pretty=%at` without `--numstat`,
which walks every diff and costs ~39s on a large repo against ~0.03s without).

## Cost

`--since` is a real bound on every corpus, not decoration: `git log --since`, reflog
lines filtered by timestamp, mail counted by file mtime. Per-repo git calls run once per
invocation and are not cached across invocations — the verb is on demand, not a timer.

## Settings

| Key | Default | Effect |
|---|---|---|
| `statsSinceDays` | `7` | default window when `--since` is not given |
| `gitActivityLog` | `true` | append credential requests and movements to `logs/git-activity.jsonl` |
| `gitPollSeconds` | `5` | git activity poll interval |

See [`persona-tuning.md`](persona-tuning.md) for how settings resolve.

## Out of scope

Reading Azure DevOps or GitHub APIs (offline rule — local clones are the source), and
changing commit identity so agents can be told apart by git author (a fleet-wide policy
decision, not a stats feature).
