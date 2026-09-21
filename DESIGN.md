# How to Fish: Expanded — design

The goal is the feeling you described from your first hours with the game: *you didn't expect
dynamite fishing, and you didn't expect guns*. Every addition below is built to be a surprise that
still fits, and to sit **alongside** the game's linear island progression rather than replacing it.

Three rules the content follows:

1. **Earn the toy, then play with it.** Every major reward is a verb you didn't have before: a
   cannon, a ship, a harpoon. Never a stat bump.
2. **The sea should misbehave.** Open water is currently a commute. It should have weather, wrecks,
   things circling the boat, and things worth stopping for.
3. **Nudge, never railroad.** Content announces itself (a shape on the horizon, a bottle bumping the
   hull) and then waits. Nothing blocks the vanilla progression.

---

## Act structure (main line)

Pirates and sunken treasure, in three acts across the existing islands.

**Act 1 — "Colours at Dawn"** *(implemented; see `src/Content/PirateStory.cs`)*
The gulls turn on you, wave after wave. Something falls out of the beaten flock: an oilcloth chart.
Someone else wants that chart, and they have cannons. (No albatross: the game has its own, and
using it here would give that away.)
Reward: **the pirate ship** (the cannon is bought in the shop).

**Act 2 — "Old Salt's Teeth"** *(implemented on the `megalodon` branch; see `src/Megalodon/`)*
The gull that stole Old Salt's teeth was eaten by a megalodon that only hunts things dangling off the
back of a boat. Buy a wakeboard, get towed far out, survive three phases of increasingly unreasonable
shark, and get the teeth back - from its corpse, or from its stomach.
Reward: **a megalodon tooth** (and a sea of stunned fish).

**Act 2b — "The Salvage War"** *(designed)*
The chart marks three wrecks. Each is a dive site guarded by something: a shark that has claimed it,
a pressure-cracked hull that floods, a rival crew already anchored there. Salvage funds the refit.
Reward: **ship upgrades** — more cannons, a reinforced hull, a powder magazine (risky: it explodes).

**Act 3 — "What the Chart Was Really For"** *(designed)*
The last wreck is not a treasure ship. It's a research vessel, and the crates aren't gold. This ties
into what the military NPCs on island 5 and the mutated whale are already doing in the base game.
Reward: **the front-loader**, a black-powder cannon you carry, and the true ending of the chart.

---

## Side quests

Deliberately soft: no timers, no fail states, repeatable where sensible.

**Sea (the main gap you identified)**
1. **Message in a bottle** — bottles drift into the boat. Each is a fragment: a wreck location, a
   rumour, sometimes a joke. Collect a set to reveal a dive site.
2. **Flotsam runs** — a storm passes, leaving floating crates. First to grab one keeps it.
3. **The stalker** — stop the boat too long in deep water and a fin starts circling. Leave, feed it,
   or fight it. It remembers you: the same shark returns with scars.
4. **Chum the water** — throw meat overboard on purpose to summon what's below. A gamble.
5. **Something took my catch** — mid-reel, your fish is bitten in half. What did it is still down
   there and is now the quest.
6. **Distress call** — the radio picks up a mayday on a frequency. Sail to it: survivors, or an
   ambush.
7. **Ghost net** — a drifting net full of live fish. Free them, or harvest them. Quiet moral choice.
8. **The lighthouse wants fish** — a standing weekly order: deliver N kg of anything. Pure routine
   income for players who just want to fish.

**Land and NPCs**
9. **The shipwright** — a new NPC who only appears once you own a hull. Repairs, upgrades, and
   increasingly alarming opinions about what's in the water.
10. **Barfly bounties** — a drunk in the harbour pays for specific catches, badly described
    ("something with too many eyes"). You work out what he means.
11. **The taxidermist** — trades trophies for wall mounts and the occasional weapon.
12. **The collector** — wants one of every fish, never explains why, pays absurdly for the last one.

**Surprises (the "dynamite moment" list)**
13. **The cannon works on fish.** Nobody says so. It just does.
14. **A fish that fishes back** — something rod-shaped and hostile.
15. **The chart is wrong** — one dive site is a decoy set by the pirates. Finding out is the fun.
16. **The crate that shouldn't be opened** — and the achievement for opening it anyway.

---

## New verbs, not new numbers

| Reward | What it changes | Earned from |
|---|---|---|
| **Cannon** | Arcing explosive shots, mounted or carried | Act 1 |
| **Pirate ship** | A hull with deck space, cannon mounts, worse handling | Act 1 |
| **Harpoon** | Attach to a big fish and get dragged | Act 2 |
| **Front-loader** | Slow, absurd, enormous damage, reload per shot | Act 3 |
| **Minigun** | Exactly what you think; ammo hunger is the drawback | secret |
| **Powder magazine** | More cannon damage, and your ship can now detonate | Act 2 |

---

## Nudges along the linear path

Small, cheap signposts so content is discovered rather than looked up:

- A **shape on the horizon** when a sea event is live, visible from the boat.
- **Chat lines from the world**, not a quest log: "something bumps the hull."
- **NPC barks** that change after story beats, so the harbour reacts to what you did.
- **Bottles wash up on the beach** of the island you're on when a chain is ready to continue.
- **The radio** plays a different station after each act.

---

## Build status

| Layer | State |
|---|---|
| Module system, hotkeys, overlay | **Done** |
| Custom networking, late-joiner sync | **Done** |
| Mod-only save file, per Steam account | **Done** |
| Quest engine + story + ballistics, 124 headless tests | **Done** |
| Act 1 content (4 quests), fully playable | **Done** |
| Seagull swarm | **Done** |
| Asset bundle pipeline (Unity batch mode) | **Done** |
| Pirate ship: sailing AI, broadsides, sinking | **Done** |
| Damage to the ship from cannon, dynamite and gunfire | **Done** |
| Health bar | **Done** |
| Deck cannon (bow during the fight, kept afterwards) | **Done** |
| Boat refit reward (mast, colours, stern gun) | **Done** |
| Pirate crew, animated | **Done** - grey until the colour atlas is installed |
| Story characters + dialogue + quest markers | **Done** - grey until the colour atlas is installed |
| Pirate raids on rich crews after Act 1 | **Done** |
| Boarding (pirates fighting on your deck) | Not built |
| Wakeboard: shop, rack by the helm, tow-rope physics, jumps, ramps | **Done** - untested in game |
| Megalodon: 3-phase fight, lunges, fake-out, rope bite, tail slap, play dead, stern chomp, jump-over, finale | **Done** - untested in game |
| Sea hazards (flying fish, jellyfish, buoys, fog banks, barrel mines), rodeo, stomach, solo autopilot | **Done** - untested in game |
| Act 2 "Old Salt's Teeth" (quest + barks), headless tests for the fight rules | **Done** |
| Act 2b "Salvage War" and Act 3 | Designed, not built |
| Sea events (bottles, sharks, flotsam) | Designed, not built |
