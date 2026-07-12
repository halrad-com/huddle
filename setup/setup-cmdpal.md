# Huddle — PowerToys Command Palette Extension Setup

This sets up the **optional** Command Palette companion. It is a convenience
surface — summon the palette, type `huddle`, fire a verb without focusing the
console. The huddle console remains the primary interface; skip this entirely
if you don't use PowerToys.

Guide the user through the steps below, checking each prerequisite yourself
where a command can (`dotnet build` compile check); the deploy step requires
Visual Studio interaction the user performs.

## 1. Prerequisites

Verify with the user, in order — each with where to get it:

1. **Windows 11.**
2. **PowerToys 0.95+** — https://aka.ms/installpowertoys (Command Palette
   module enabled in PowerToys settings).
3. **Visual Studio 2022** with the **Windows application development**
   workload (VS Installer → Modify → check the workload).
4. **Developer Mode** — Windows Settings → System → For developers → on.
   (Required to sideload the MSIX.)

## 2. Compile check (CLI, you can run this)

```
dotnet build extension/CmdPalHuddle/CmdPalHuddle/CmdPalHuddle.csproj
```

Expect 0 errors. This verifies the source; sideloading still requires the
Visual Studio Deploy step below.

## 3. Build and deploy (user, in Visual Studio)

1. Open `extension/CmdPalHuddle/CmdPal.Huddle.sln` in Visual Studio 2022.
2. Build → **Deploy CmdPalHuddle** (Deploy, not just Build — Deploy registers
   the MSIX package with Windows).
3. Open Command Palette and run **Reload** (the entry whose subtitle is
   "Reload Command Palette Extension").
4. Type `huddle` — the commands should list.

## 4. Extension settings

If the commands show but can't find your huddle: gear icon on the extension
in Command Palette → set the **huddle root path** (this clone) and the
**launch command** (how you start huddle, e.g. your terminal profile) if
auto-detection missed them.

## 5. Verify

- `Huddle: Status`, `Repos`, `Personas`, `Conflicts`, `Launch` appear.
- With the huddle console CLOSED, the palette shows a "Huddle is not running"
  placeholder row — that is **expected behavior**, not a bug. Start huddle
  from your usual shortcut (or `Huddle: Launch`) and reload the palette.

Form-driven verbs (Direct, Start session, Send message, Broadcast) are wired
progressively — see the repo README for the current list of live palette
verbs.
