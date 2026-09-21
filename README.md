# How to Fish: Expanded

A BepInEx 5 mod for **How to Fish** (Steam 4001890): a Zombies-style seagull swarm, the Gull Pirates
with a ship you fight, board-mounted cannons you man yourself, wakeboarding behind your boat - and the
megalodon that comes for whoever is on the end of the rope. Plus story characters who talk and eat
like the game's own NPCs, and a quest line that ties it together.

See [DESIGN.md](DESIGN.md) for the content plan, [ASSETS.md](ASSETS.md) for the 3D models, and
[CREDITS.md](CREDITS.md) for their authors.

## Install (players)

You need **How to Fish on Steam**. Nothing else - the mod loader is included.

1. Clone or download this repository (Code -> Download ZIP works too).
2. Close the game, then double-click **`Install.bat`**. It finds your Steam game folder by itself
   (or run `release\install.ps1 -GameDir "<folder with How to Fish.exe>"`).
3. Start How to Fish from Steam as usual.

**Everyone in a multiplayer session needs the same version.** The mod adds its own networked
objects and messages; mismatched versions show a "protocol mismatch" in the log.

`Toggle Mods.bat` (copied into the game folder) switches the mod off and on for a plain game without
deleting anything. The game's own files and saves are never modified.

## Controls

| Where | Key | Does |
|---|---|---|
| Story characters | **E** | Talk / next line (same as the game's NPCs) |
| Story characters | throw / drop | Feed them what they asked for |
| Swivel gun | **E** | Man the gun / let go |
| Swivel gun (manned) | **Left click** | Fire |
| Swivel gun (manned) | **R** | Reload |
| Swivel gun (manned) | **F** | Fire *yourself* - human cannonball |
| Wakeboard (by the helm) | **E** | Grab the tow rope |
| Wakeboarding | **A / D** | Carve (swing out wide, whip back across the wake) |
| Wakeboarding | **Space** | Jump - time it as you cross the wake for a big one |
| Wakeboarding | **E** | Let go |
| Wakeboarding | **G** | Shout (hold A or D for LEFT!/RIGHT!, alone for FASTER!) |
| On the megalodon's back | **F** / **Space** | Plant a charge / jump off |
| Driving, megalodon out | **G** | Drop a barrel mine off the stern |
| Driving, engine stalled | **Space** (mash) | Yank the pull cord |
| Inside the megalodon | **E** | Grab Old Salt's dentures |

There are no cheat keys in normal play. For testing, set `Debug / DevHotkeys = true` in
`BepInEx\config\dazed.howtofish.expanded.cfg` (F1 overlay, F2 swarm, F4 pirate ship, F7 test pistol, F9 megalodon - Ctrl+F9 gives a free
wakeboard, Shift+F9 knocks a quarter off it, Alt+F9 throws a random sea hazard in).

## The story (Act 1)

1. From the **second island** on (where the game sells guns), **Old Salt** waits at the landing of
   whatever island you're on - look for the **!**. Nobody from the story is on the first island.
2. *Bad Omens*: shoot three seagulls and throw them to him. He eats them (1/3,
   2/3, 3/3). Reward: $50. Extra gulls fly in while a gull job is running.
3. **The swarm is the flock's revenge** - at any time, killing five gulls inside three minutes brings
   it down on you. Old Salt warns you about it once Bad Omens is done.
   *The Flock Breaks*: call the swarm on purpose, then survive every
   wave - each bigger than the last, each with a time limit (bar at the top). Everyone down or time
   up and they simply leave. Win and a chart falls out of the last flock: an eyepatched gull on it.
4. **Anne** reads it: the mark of the **Gull Pirates** - humans who live like gulls. They sail from
   the **third island**. There, *Colours at Dawn*: buy a swivel gun in the shop (next to the motors),
   sail to the red dot on your radar, and deal with Captain Squawk's ship, the *Greedy Gull*.
5. Sink her, or shoot the captain so she strikes her colours. She's yours to sail, and **Mako** the
   shipwright turns up (talk to him twice to swap hulls back and forth).
6. From then on: sail out with **5 or more grilled animals** lying in your boat and the Gull Pirates
   come for your barbecue. Beat them for loot, or lose it to them.

Optional side job from Old Salt: a pike of 2 kg or more ($100).

## Act 2: Old Salt's teeth - the megalodon

The gull that stole Old Salt's teeth got eaten. By a fish. A **big** fish, that only comes up for
things dangling off the back of a boat.

1. Buy the **wakeboard & tow line** in the shop (next to the motors). It leans by the helm; press
   **E** to grab the rope. Someone drives - playing alone, **Old Salt takes the helm** (he can't see
   without his teeth; shout directions with **G**).
2. You start sitting in the water; the boat pulls you up. Carve with A/D, jump with Space (off the
   wake = big air), hit the floating ramps. Let the boat slow down and you sink back in.
3. Ride far enough out (about 110 m from the mooring) and a **fin** shows up behind you. Dun-dun.
4. It keeps its distance while the boat is fast and creeps up when it's slow (watch the bite meter).
   It lunges - bubbles and a shadow first, then jaws: carve away, or jump the low ones. Each bite
   costs you your board: **wakeboard -> Old Salt's cabin door -> a bathtub (with duck) -> bare feet
   -> eaten**.
5. Hurt it with the pistol, dynamite (drop it behind you), the deck gun, and the driver's **barrel
   mines** (G). Throw grilled food in the water to distract it. Land on its back and you ride it -
   plant a charge and jump.
6. It gets angrier: flying fish (one might stick to your face), jellyfish, buoys that catch the rope
   and slingshot you, fog banks, rope bites, tail slaps, fake-outs, playing dead, biting the stern
   (engine trouble - the driver mashes Space), and jumping clean over the boat.
7. Near the end it surfaces ahead of the boat with its jaws wide open: **stuff its mouth** with
   something explosive. Kill it for money, a sea of stunned fish, raining teeth - and Old Salt's
   dentures. Or get eaten, grab the dentures in its stomach, and get spat out at the island.
8. Give Old Salt his teeth back.

## Your saves are safe

Mod progress lives in its own file (`Saves/<steamid>/expanded.json`), never inside the game's save.
Removing the mod leaves a working vanilla save. Writes go via a temp file plus a backup.

## Layout

```
Install.bat    one-click install for players
release/       what gets installed: BepInEx 5.4.23 + the mod + its model bundles
src/Core/      plugin host, modules, networking, save, assets, characters, logging
src/Quests/    quest engine (Unity-free) + game-side glue
src/Content/   story, quests and cast (Unity-free, validated by tests)
src/Pirates/   ship, cannons, ballistics, boat mounts, refit, crew, radar mark
src/Npcs/      story characters, placement, dialogue, feeding
src/Swarm/     the seagull encounter
src/Megalodon/ wakeboard, tow rope, the megalodon, sea hazards, the stomach
tests/         headless tests (quest engine, story, ballistics, megalodon rules)
tools/         asset bundle pipeline (Unity batch mode)
```

## Build, test, verify (developers)

No .NET SDK needed - the build drives Roslyn directly against the game's assemblies (read from your
local game install; they are not part of this repository).

```powershell
.\build.ps1                          # -> dist\HowToFishExpanded.dll
.\tests\run-tests.ps1                # headless tests: quest engine, story, ballistics, megalodon rules
.\verify.ps1                         # every game member the mod touches still exists
.\tools\build-bundles.ps1 -Install   # rebuild the model bundles (needs Unity 6000.4.4f1)
```

After a build, copy `dist\HowToFishExpanded.dll` into `release\game-files\BepInEx\plugins\` so the
installer ships it. `verify.ps1` is the one to run after a game update: it checks, without launching
anything, that each hooked method, field and Harmony argument name still exists as expected.

## Logs

`BepInEx\Expanded.log` - one file per launch, timestamped, including errors the game itself reports.
Set `Debug / Verbose = true` for per-entity detail.
