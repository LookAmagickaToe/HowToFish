# Assets

Everything here is **CC0** (public domain): no attribution required, no licence problem when the
mod is shared with friends. That last point rules out most Unity Asset Store models, whose licences
forbid redistributing the model inside something other people download.

Until these are downloaded, the mod uses **placeholders built from parts the game already ships**
(see "Placeholders" below), so everything is playable now and only the looks change later.

## Shortlist

| # | What for | Source | Licence | Notes |
|---|---|---|---|---|
| 1 | **Pirate ship hull, masts, sails, cannons, barrels, chests** | [Kenney – Pirate Kit](https://kenney.nl/assets/pirate-kit) | CC0 | ~60 models, built as a modular kit: hull pieces, masts, sails, cannon, crates. The best single match for this game's chunky low-poly style. |
| 2 | Larger set of the same (flags, props, docks) | [Kenney – Pirate Pack](https://kenney.nl/assets/pirate-pack) | CC0 | ~190 assets. Grab if #1 feels thin. |
| 3 | **Alternative complete sail ship** | [Poly Pizza – Sail Ship by Quaternius](https://poly.pizza/m/cIzO4MBPqI) | CC0 | One finished ship rather than a kit. Faster to drop in; less flexible. |
| 4 | Pirate characters (captain, crew) | [Poly Pizza – Pirate kit by Quaternius](https://poly.pizza/bundle/Pirate-kit-0q5ulmIYqQ) | CC0 | 70+ models incl. animated characters, for boarding parties and new NPCs. |
| 5 | Same kit, single download | [Sketchfab – Pirate Kit (70+ models), Quaternius](https://sketchfab.com/3d-models/pirate-kit-70-models-52af2bc5ac5846ff84a0d671b897c0a2) | CC0 | Mirror of #4. |

**Recommended minimum: #1 and #4.** Together they cover the ship, the cannons and the pirates
themselves, for nothing.

## What to download and where to put it

1. Download #1 and #4 as **FBX** or **glTF** (both work; FBX is the safer Unity import).
2. Drop the archives in `assets/raw/` in this repo (git ignores that folder).
3. Tell me, and I'll pack them into a bundle the mod loads at runtime.

Packing needs **Unity 6000.4.4** (the game's exact version), which is the one step I can't do for
you. It's a one-off: after that, replacing a model is just rebuilding the bundle.

## The megalodon and the wakeboard need nothing new

- The **megalodon** is the Quaternius **Shark** (`Characters_Shark`, animations Swim / Swim_Fast /
  Swim_Bite) from pack #4 above - already in the characters bundle. Its nose direction is detected
  from the mesh; if it ever swims backwards, set `Megalodon / ModelYawOffset = 180`.
- The **door** tier and the **barrel mines** use the Kenney `castle-door` and `barrel` from pack #1.
- The **wakeboard, bathtub (with duck), buoys, jellyfish, flying fish, ramps, dentures, GameBoy and
  the stomach** are built from Unity primitives in `src/Megalodon/MegaShapes.cs` - no download.
- Sounds are the game's own (splashes, bites, burps, rumble); the dun-dun and heartbeat are
  synthesised in code.

Optional nicer models, if you want them later (all CC0, drop into `assets/raw/` and tell me):
a bathtub and a rubber duck (e.g. Kenney *Furniture Kit*), a wakeboard/surfboard, a jellyfish.

## Placeholders used until then

The mod builds stand-ins from assets already inside the game, so nothing is blocked:

| Needed | Placeholder | Where it comes from |
|---|---|---|
| Ship hull | The fishing boat, dark-tinted | `other/boat` |
| Sails | Generated cloth sheets | `Shader Graphs/FlagShader`, the game's own waving-cloth shader |
| Cannon | Scaled-up gun barrel | shotgun / sniper rifle models |
| Cannonball | Existing sphere item | `snowball1`, `badball` |
| Treasure chest | Crate-sized box using boat wood material | boat renderer material |
| Pirate NPC | Existing NPC body with the game's sailor hat | game clothing system |

These look deliberately rough but sit correctly in the world: right shaders, right lighting, right
art direction. Swapping in the real models later touches only the visual layer, never the gameplay.

## Licence hygiene

- CC0 means no attribution is required. A `CREDITS.md` naming Kenney and Quaternius is still good
  manners and costs nothing.
- Do **not** drop in Asset Store or Sketchfab models under other licences: most forbid redistributing
  the model file, which is exactly what shipping the mod to a friend does.
- If you ever want a specific ship that isn't CC0, the safe route is commissioning it or buying a
  licence that explicitly allows redistribution in a mod.
