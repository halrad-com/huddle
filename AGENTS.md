# Check files out before you edit them

This working directory is **shared**. Several agents — Claude Code sessions, Codex, and
whatever else is open — edit this same checkout at the same time, and none of them can see
the others. Two agents editing one file corrupts it or throws away someone's work. Git cannot help,
because the collision happens in the working tree before any commit exists, so there is no
second version for it to merge and the losing edit simply disappears.

So the files are a library. A file is either **available** or **checked out**, and you take
the ones you are about to edit.

## Three commands

**1. See what is out.** Needs nothing — no identity, no setup:

```
huddle --catalog
```

**2. Check out what you are about to edit,** before your first edit. Repo-relative paths.
`--as` is any stable name you pick for yourself:

```
huddle --checkout --as codex:refactor src/One.cs docs/two.md
```

It either succeeds or tells you who holds the file and until when. **A refusal means do not
edit that file** — pick different work, or wait for the due date to lapse. Nothing arbitrates
this for you. A set is all-or-nothing, so if one file is held you get none of them.

**3. Check in when you have committed:**

```
huddle --checkin --as codex:refactor --all
```

## The things worth knowing

- **Checkouts expire.** Default an hour. Run the same `--checkout` again to renew before the
  due date, or `huddle --catalog --renew --as <name>` to extend everything you hold. This is
  why a crashed agent does not lock a file forever — and why your own checkout can lapse
  under you on a long task.
- **Say who you are, the same way each time.** `--as codex:refactor` is fine; anything stable
  is fine. The name is your identity — it is how renewal, check-in, and "this is mine"
  all work. A different name each run means you cannot return your own books.
- **One file:** `huddle --status src/One.cs` says available, or who has it and whether it has
  changed since they took it.
- **Only yours:** `huddle --catalog --mine --as <name>`. **Late ones:** `huddle --catalog --overdue`.
- **Another repo** (a sibling checkout, a build dependency): `--repo <name>`, e.g.
  `huddle --checkout --as codex:refactor --repo netlib src/netcfg/netcfgManager.cs`.
  Without it the checkout lands in this repo and protects nothing.
- **If `huddle` is not on your PATH**, use the full path to the binary —
  `C:/Users/you/source/repos/myapp/publish/huddle.exe`. The commands find the shared
  ledger themselves by looking upward from wherever you are running; there is nothing to
  configure and no environment to set.
- **Reading needs no tool at all.** Checkouts are plain markdown in
  `ipc/workledger/catalog/`. If the commands fail, read that directory, confirm nobody holds
  your files, say plainly in your next message that you could not check out, and then work.

## Everything else about this project

This file covers one thing: not colliding with the other agents in this directory. For what
the project is, how to build it, and its conventions, read `README.md` and `CLAUDE.md` in
this same directory.
