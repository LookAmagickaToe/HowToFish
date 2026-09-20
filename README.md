# Seagull Swarm

A BepInEx 5 plugin for **How to Fish** (Steam 4001890). Kill too many seagulls and the flock
remembers. Five escalating waves of dive-bombing gulls, led by an albatross, in the style of
*The Birds*.

No new meshes, models or textures. Everything reuses the game's own `Seagull` and `Albatross`
prefabs and their existing networked spawn path.

## How it plays

1. Kill **5 seagulls inside a rolling 3-minute window**. Kills outside the window are forgotten.
2. An **Albatross** arrives as flock leader and takes over the top-centre boss bar and its timer.
3. **Five waves**, `10 × 1.3^(n-1)` birds each — 10 / 13 / 17 / 22 / 29, **91 birds total**. The
   whole wave spawns at once, on a ring above the party.
4. Gulls circle, then peel off in groups that **climb, hover in unison for 1.5–2 s, and dive**.
   The dive is a true ballistic parabola — shallow at the top, accelerating hard into the strike,
   carrying momentum out the far side before arcing back up into the circle.
5. Damage lands **on contact** during the dive, swept against the movement segment so a fast dive
   cannot tunnel past you between frames.
6. A wave ends when its last bird dies. Clear wave 5 and the flock is broken.
7. The boss bar drains **once across the whole encounter** — it shows birds remaining out of 91.
   One countdown covers all five waves; if it runs out, the flock disperses.

Group size and the interval between dive groups both scale with wave number, so wave 5 is a
near-continuous stream of simultaneous dives.

## Host-only

Only the host needs the mod. All spawning, AI and damage run behind `InstanceFinder.IsServerStarted`;
clients see the birds through normal FishNet replication and need no files.

This works because the vanilla bird pipeline is already server-authoritative: `BirdManager.Update`
simulates on the server and pushes position plus an animation-state byte to clients via
`Bird.ObserverSetPos`, an `ObserversRpc`. Clients only lerp toward `ServerPos`. The plugin takes
over movement with a Harmony prefix on the private `BirdManager.SimulateBird`, and the vanilla
`SendBirdPos` keeps replicating the result for free — the mod defines no RPCs of its own.

### Why the leader is an Albatross

The boss bar cannot be pointed at a seagull on an unmodded client. `BossManager.Boss` is only ever
assigned inside `Creature.OnStartClient`, from the prefab's own serialized `_bossType`, and every UI
path (`ToggleBossUI`, `UpdateBossHp`, the timer in `LateUpdate`) null-guards on that static. There
is no server-to-client route to point it at a non-boss creature.

The Albatross prefab is already boss-typed, so each client sets `BossManager.Boss` on its own. Its
`_hp` is a replicated `SyncVar<int>`, and `Creature.OnHealthChange` fires `OnBossTakeDamage` on
every client whenever it moves. So the host writes **birds remaining** into `_hp` and
**total birds** into `BossManager._bossMaxHp`, and every unmodded client's bar tracks the swarm
exactly. Driving `_hp` to 0 at the end runs the normal boss-death path: bar tears down, outro music
plays.

The leader is held immortal (`BossManager.ToggleImmortal`) so players cannot drain the swarm counter
by shooting it. The plugin still writes `_hp` directly, which bypasses the immortality check in
`Creature.ServerChangeHp`.

### The countdown is not ours to set

`BossTotalTimeInTicks` is computed **client-side** from `Boss.BossTimeInSeconds`, a plain serialized
field on the Albatross prefab — not a SyncVar. Unmodded clients render whatever the prefab has baked
in. The encounter is therefore sized to exactly that value so host and clients agree; changing it
host-side would only desync the display.

## Install

BepInEx 5 is **not** currently installed in the game folder.

1. Download **BepInEx 5 (win x64)** and extract it into the game root, next to `How to Fish.exe`:
   `...\steamapps\common\How to Fish\How to Fish\`
2. Launch the game once so BepInEx generates its folders, then quit.
3. Copy `dist\SeagullSwarm.dll` into `...\How to Fish\How to Fish\BepInEx\plugins\`.
4. Launch again. `BepInEx\config\dazed.howtofish.seagullswarm.cfg` appears after the first run.

## Configuration

Every tunable is a BepInEx `ConfigFile` entry, grouped under `Trigger`, `Waves`, `Spawning`,
`Circling`, `Dive`, `Damage`, `Pacing`, `Leader` and `Debug`. Highlights:

| Key | Default | Meaning |
| --- | --- | --- |
| `Trigger / KillsToProvoke` | 5 | Kills needed to anger the flock |
| `Trigger / WindowSeconds` | 180 | Rolling window length |
| `Waves / FirstWaveSize` | 10 | Birds in wave 1 |
| `Waves / Growth` | 1.3 | Per-wave multiplier |
| `Dive / Duration` | 1.15 | Seconds from hover to impact. Lower is nastier |
| `Dive / Gravity` | 22 | Downward accel; higher bends the parabola harder |
| `Damage / ContactDamage` | 7 | Per bird that connects |
| `Pacing / GroupSizeFinalWave` | 6 | Birds diving together at wave 5 |
| `Leader / Spawn` | true | Set false to run without the boss bar entirely |

Set `Debug / Verbose = true` to log every state transition and hit.

## Building

No .NET SDK required — `build.ps1` invokes Roslyn's `csc` directly against the game's `Managed`
folder and the BepInEx core libraries.

```powershell
.\build.ps1
```

`verify.ps1` statically checks, via Mono.Cecil, that every Harmony target and every game member the
plugin touches still exists with the expected signature. Run it after any game update — a silently
missing patch target is the most likely way this breaks.

```powershell
.\verify.ps1
```

## Status

Compiles clean and all patch targets verify against the shipped `Assembly-CSharp.dll`.
**Not yet run in-game** — that needs BepInEx installed and a live session.
