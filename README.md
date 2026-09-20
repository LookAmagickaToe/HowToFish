# How to Fish: Expanded

A BepInEx 5 mod for **How to Fish** (Steam 4001890) that adds a seagull swarm boss encounter, a
quest and story system, and — in progress — pirates, new sea life and new weapons.

See [DESIGN.md](DESIGN.md) for the content plan and [ASSETS.md](ASSETS.md) for the 3D assets to
download.

## Modules

The mod is a set of independent modules over shared plumbing. Each can be switched off in the config
without affecting the others, and each has a debug hotkey.

| Module | Key | What it does |
|---|---|---|
| Seagull Swarm | **F2** | Five escalating waves of dive-bombing gulls led by a killable Albatross |
| Quests & Story | **F3** | Progression, rewards, chat notifications, mod-only save |

**F1** toggles an on-screen overlay listing every module and its state.

## Multiplayer

The seagull swarm is **host-only**: friends need nothing installed. Everything built on the quest
and networking layer requires **everyone to install the mod**, because it adds objects and messages
the base game doesn't know about. The mod refuses to act on messages from a mismatched version
rather than corrupting a session.

## Your saves are safe

Mod progress lives in its own file (`Saves/<steamid>/expanded.json`), never inside the game's save.
Removing the mod leaves a working vanilla save. Writes go via a temp file plus a backup.

## Layout

```
src/Core/      plugin host, module system, networking, save, logging
src/Quests/    quest engine (Unity-free) + its game-side glue
src/Content/   the actual story and quest definitions
src/Swarm/     the seagull encounter
tests/         headless tests for the Unity-free parts
```

`src/Quests/QuestEngine.cs` deliberately contains no UnityEngine code so the whole progression
system can be tested without launching the game.

## Build, test, verify

No .NET SDK needed — the build drives Roslyn directly against the game's assemblies.

```powershell
.\build.ps1          # -> dist\HowToFishExpanded.dll
.\tests\run-tests.ps1 # 56 headless tests
.\verify.ps1         # every game member the mod touches still exists
```

`verify.ps1` is the important one after a game update: it checks, without launching anything, that
each hooked method and field still exists with the expected signature. A silently missing hook is
the most likely way this mod breaks.

## Install

1. BepInEx 5 in the game folder (already installed here).
2. Copy `dist\HowToFishExpanded.dll` into `BepInEx\plugins\`.
3. `Toggle Mods.bat` in the game folder switches all modding on and off without deleting anything.

## Logs

`BepInEx\Expanded.log` — one file per launch, timestamped, including errors the game itself reports.
Set `Debug/Verbose = true` for per-entity detail.
