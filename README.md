# How to Fish: Expanded

A BepInEx 5 mod for **How to Fish** (Steam 4001890): a seagull swarm boss, a pirate storyline with
a ship you fight and sink, cannons, story characters with dialogue, and a quest system that ties it
together.

See [DESIGN.md](DESIGN.md) for the content plan, [ASSETS.md](ASSETS.md) for the 3D models, and
[CREDITS.md](CREDITS.md) for their authors.

## Playing

**Everyone in the session must run the same mod version**, with both the DLL and the
`ExpandedAssets` folder. The mod defines its own networked objects, and peers match those by
registration order.

| Key | Does |
|---|---|
| **F** | Talk to a character / fire the deck cannon you're standing at |
| **1-4** | Pick a dialogue option |
| **F1** | Module overlay (what each module is doing right now) |
| **F2** | Swarm: start or stop an encounter (host) |
| **F3** | Quests: print the journal; Shift+F3 force-completes the current quest (host) |
| **F4** | Pirates: send in or remove the pirate ship; Shift+F4 knocks a quarter off her hull (host) |
| **F5** | Model preview: cycle through every kit model |
| **F6** | Characters: log where every story character is standing |
| **F7** | Drop a test weapon (pistol) in front of you (host) |

## The story (Act 1)

Story characters work like the game's own NPCs: look at them, press **E** ("Talk"), and press **E**
again to click through what they say in the speech bubble. Listening to a job is taking it on.
Things they ask for are fed to them - throw or drop the item at them.

1. **Old Salt** waits at the landing of whatever island you're on. Look for the **!** over his head.
2. *Bad Omens* - kill three seagulls and feed them to him (1/3, 2/3, 3/3).
3. *The Flock Breaks* - kill five gulls inside three minutes to call the swarm, then survive every
   wave (Zombies-style: each bigger than the last, each with a time limit). Something falls out of
   the last flock: a chart.
4. **Anne** appears. *Colours at Dawn* - buy a swivel gun in the shop (next to the motors), sail to
   the buoy the chart marks, and deal with the pirate ship, the *Salted Widow*, waiting there.
5. Sink her, or shoot her captain so she strikes her colours. The *Widow* is yours to sail, and
   **Mako** the shipwright turns up (talk to him twice to swap hulls back and forth).
6. Rich crews at sea get raided by pirates from then on.

## Your saves are safe

Mod progress lives in its own file (`Saves/<steamid>/expanded.json`), never inside the game's save.
Removing the mod leaves a working vanilla save. Writes go via a temp file plus a backup.

## Layout

```
src/Core/      plugin host, modules, networking, save, assets, characters, logging
src/Quests/    quest engine (Unity-free) + game-side glue
src/Content/   story, quests and cast (Unity-free, validated by tests)
src/Pirates/   ship, cannons, ballistics, boat mounts, refit, crew
src/Npcs/      story characters, placement, dialogue
src/Swarm/     the seagull encounter
tests/         headless tests
tools/         asset bundle pipeline (Unity batch mode)
```

## Build, test, verify

No .NET SDK needed - the build drives Roslyn directly against the game's assemblies.

```powershell
.\build.ps1                          # -> dist\HowToFishExpanded.dll
.\tests\run-tests.ps1                # 124 headless tests: quest engine, story, ballistics
.\verify.ps1                         # every game member the mod touches still exists
.\tools\build-bundles.ps1 -Install   # rebuild the model bundles (needs Unity 6000.4.4f1)
```

`verify.ps1` is the one to run after a game update. It checks, without launching anything, that each
hooked method, field and Harmony argument name still exists as expected. A silently missing hook is
the most likely way this mod breaks.

## Install

1. BepInEx 5 in the game folder.
2. Copy `dist\HowToFishExpanded.dll` into `BepInEx\plugins\`.
3. Copy `dist\bundles\{pirates,characters,characters.json}` into `BepInEx\plugins\ExpandedAssets\`.
4. `Toggle Mods.bat` in the game folder switches all modding on and off without deleting anything.

## Logs

`BepInEx\Expanded.log` - one file per launch, timestamped, including errors the game itself reports.
Set `Debug/Verbose = true` for per-entity detail.
