using BepInEx.Configuration;
using Expanded;
using UnityEngine;

namespace SeagullSwarm
{
    /// <summary>
    /// Every tunable the encounter uses. Host-authoritative: only the host's values matter,
    /// because only the host runs the director and the AI.
    /// </summary>
    internal class SwarmConfig
    {
        // --- Trigger -------------------------------------------------------
        public readonly ConfigEntry<int> KillsToProvoke;
        public readonly ConfigEntry<float> ProvokeWindowSeconds;
        public readonly ConfigEntry<float> RetriggerCooldownSeconds;

        // --- Lure (extra gulls while a quest needs them) ------------------
        public readonly ConfigEntry<bool> LureEnabled;
        public readonly ConfigEntry<int> LureMaxGulls;
        public readonly ConfigEntry<float> LureIntervalSeconds;
        public readonly ConfigEntry<float> LureRadius;
        public readonly ConfigEntry<float> LureReplaceSeconds;

        // --- Waves ---------------------------------------------------------
        public readonly ConfigEntry<int> WaveCount;
        public readonly ConfigEntry<int> FirstWaveSize;
        public readonly ConfigEntry<float> WaveGrowth;
        public readonly ConfigEntry<float> WaveTimeBaseSeconds;
        public readonly ConfigEntry<float> WaveTimePerBirdSeconds;
        public readonly ConfigEntry<float> WaveBreakSeconds;

        // --- Approach ------------------------------------------------------
        public readonly ConfigEntry<float> ApproachDistance;
        public readonly ConfigEntry<float> ApproachHeight;
        public readonly ConfigEntry<float> ApproachSpread;
        public readonly ConfigEntry<float> ApproachSpeed;
        public readonly ConfigEntry<float> MinDirectionChange;
        public readonly ConfigEntry<bool> ScreamOnArrival;

        // --- Circling ------------------------------------------------------
        public readonly ConfigEntry<float> CircleRadius;
        public readonly ConfigEntry<float> CircleHeight;
        public readonly ConfigEntry<float> CircleSpeed;
        public readonly ConfigEntry<float> TurnSpeed;

        // --- Dive cycle ----------------------------------------------------
        public readonly ConfigEntry<float> ClimbHeight;
        public readonly ConfigEntry<float> ClimbSpeed;
        public readonly ConfigEntry<float> HoverMinSeconds;
        public readonly ConfigEntry<float> HoverMaxSeconds;
        public readonly ConfigEntry<float> DiveDuration;
        public readonly ConfigEntry<float> DiveGravity;
        public readonly ConfigEntry<float> DiveOvershootFactor;
        public readonly ConfigEntry<float> PeelOffSeconds;
        public readonly ConfigEntry<float> PeelOffLift;
        public readonly ConfigEntry<float> DiveCooldownMin;
        public readonly ConfigEntry<float> DiveCooldownMax;
        public readonly ConfigEntry<float> StaggerMin;
        public readonly ConfigEntry<float> StaggerMax;
        public readonly ConfigEntry<float> WaterMargin;
        public readonly ConfigEntry<bool> ScreamBeforeDive;
        public readonly ConfigEntry<float> ScreamVolume;

        // --- Cover ---------------------------------------------------------
        public readonly ConfigEntry<bool> BoatIsSolid;
        public readonly ConfigEntry<bool> CrashKillsBird;
        public readonly ConfigEntry<float> AttackMinDistance;
        public readonly ConfigEntry<float> AttackMaxDistance;

        // --- Damage --------------------------------------------------------
        public readonly ConfigEntry<int> ContactDamage;
        public readonly ConfigEntry<float> ContactRadius;
        public readonly ConfigEntry<float> ContactKnockback;

        // --- Group pacing --------------------------------------------------
        public readonly ConfigEntry<float> GroupIntervalStart;
        public readonly ConfigEntry<float> GroupIntervalEnd;
        public readonly ConfigEntry<int> GroupSizeStart;
        public readonly ConfigEntry<int> GroupSizeEnd;

        // --- Ending ----------------------------------------------------------
        public readonly ConfigEntry<float> FleeSeconds;

        // --- Testing -------------------------------------------------------
        public readonly ConfigEntry<bool> HotkeysEnabled;
        public readonly ConfigEntry<KeyCode> StartKey;
        public readonly ConfigEntry<KeyCode> SkipWaveKey;
        public readonly ConfigEntry<KeyCode> StopKey;
        public readonly ConfigEntry<float> AutoStartAfterSeconds;
        public readonly ConfigEntry<bool> ChatFeedback;

        // --- Misc ----------------------------------------------------------
        public readonly ConfigEntry<bool> Verbose;
        public readonly ConfigEntry<float> StatusIntervalSeconds;

        public SwarmConfig(ConfigFile c)
        {
            KillsToProvoke = c.Bind("Trigger", "KillsToProvoke", 5,
                "Seagull kills required inside the rolling window before the flock turns hostile.");
            ProvokeWindowSeconds = c.Bind("Trigger", "WindowSeconds", 180f,
                "Length of the rolling window, in seconds. Kills older than this are forgotten.");
            RetriggerCooldownSeconds = c.Bind("Trigger", "RetriggerCooldownSeconds", 300f,
                "Quiet period after an encounter ends before the flock can be provoked again.");

            LureEnabled = c.Bind("Lure", "Enabled", true,
                "While a story quest needs seagulls, ordinary gulls keep turning up near the players so " +
                "there is always something to shoot.");
            LureMaxGulls = c.Bind("Lure", "MaxNearby", 10, "How many extra gulls may be near the players at once.");
            LureIntervalSeconds = c.Bind("Lure", "EverySeconds", 10f,
                "How often more gulls fly in (up to 4 at a time). At the defaults far more than the 5 kills the " +
                "swarm needs turn up within its 3-minute window.");
            LureReplaceSeconds = c.Bind("Lure", "ReplaceAfterKillSeconds", 15f,
                "Every gull shot down outside an encounter is replaced by a fresh one after this many seconds, " +
                "so the 5 kills in 3 minutes that call the swarm are always possible. 0 = off.");
            LureRadius = c.Bind("Lure", "NearbyRadius", 70f,
                "Only gulls this close count as 'around'; ones that wander further off are cleared away and replaced.");

            WaveCount = c.Bind("Waves", "WaveCount", 5, "Number of waves in a full encounter.");
            // Pure gull waves, Zombies-style: survive every wave to win. (Keys renamed from the
            // Albatross version so the new defaults apply to existing config files.)
            FirstWaveSize = c.Bind("Waves", "BirdsInWave1", 15, "Birds in wave 1.");
            WaveGrowth = c.Bind("Waves", "Growth", 1.3f,
                "Per-wave multiplier. 15 x 1.3^(n-1) gives 15 / 20 / 25 / 33 / 43 = 136 birds.");
            WaveTimeBaseSeconds = c.Bind("Waves", "WaveTimeBaseSeconds", 60f,
                "Each wave must be cleared in time: this many seconds...");
            WaveTimePerBirdSeconds = c.Bind("Waves", "WaveTimePerBirdSeconds", 6f,
                "...plus this many per bird in the wave. When it runs out the flock leaves and the swarm is lost.");
            WaveBreakSeconds = c.Bind("Waves", "BreakSeconds", 6f, "Breather between waves.");

            ApproachDistance = c.Bind("Approach", "Distance", 120f,
                "How far out each wave appears. The whole wave comes from one compass direction.");
            ApproachHeight = c.Bind("Approach", "HeightAboveWater", 8f,
                "Waves appear low over the water so they are visible on the horizon.");
            ApproachSpread = c.Bind("Approach", "FlockSpread", 10f, "Radius of the flock cluster at spawn.");
            ApproachSpeed = c.Bind("Approach", "Speed", 16f, "Flight speed while the flock flies in, m/s.");
            MinDirectionChange = c.Bind("Approach", "MinDirectionChangeDegrees", 90f,
                "Each wave comes from a direction at least this far from the previous one.");
            ScreamOnArrival = c.Bind("Approach", "ScreamOnArrival", true,
                "Play seagull screams at the flock when a wave appears. Heard by the host only.");

            CircleRadius = c.Bind("Circling", "Radius", 26f, "Radius of the holding pattern.");
            CircleHeight = c.Bind("Circling", "Height", 26f, "Height above the anchor birds circle at.");
            CircleSpeed = c.Bind("Circling", "Speed", 14f, "Flight speed while circling, m/s.");
            TurnSpeed = c.Bind("Circling", "TurnSpeed", 3.5f, "How sharply birds steer toward their heading.");

            ClimbHeight = c.Bind("Dive", "ClimbHeight", 12f, "Extra altitude gained above the circle before a dive.");
            ClimbSpeed = c.Bind("Dive", "ClimbSpeed", 18f, "Climb speed, m/s.");
            HoverMinSeconds = c.Bind("Dive", "HoverMinSeconds", 1.5f, "Shortest hover pause before a dive.");
            HoverMaxSeconds = c.Bind("Dive", "HoverMaxSeconds", 2.0f, "Longest hover pause before a dive.");
            DiveDuration = c.Bind("Dive", "DurationSeconds", 1.5f,
                "Seconds the ballistic dive takes to reach the target. Lower is nastier.");
            DiveGravity = c.Bind("Dive", "ArcGravity", 16f,
                "Downward acceleration during the dive. Higher bends the parabola harder.");
            DiveOvershootFactor = c.Bind("Dive", "OvershootFactor", 1.4f,
                "Dive is abandoned after Duration x this, so a miss does not chase forever.");
            PeelOffSeconds = c.Bind("Dive", "PeelOffSeconds", 1.1f, "Length of the climb-away arc after a dive.");
            PeelOffLift = c.Bind("Dive", "PeelOffLift", 30f, "Upward acceleration while peeling off.");
            DiveCooldownMin = c.Bind("Dive", "CooldownMin", 3.5f, "Shortest rest before a bird may be picked again.");
            DiveCooldownMax = c.Bind("Dive", "CooldownMax", 7f, "Longest rest before a bird may be picked again.");
            StaggerMin = c.Bind("Dive", "StaggerMin", 0.3f,
                "Minimum gap between birds of one group striking. The game ignores hits within 0.25s of the " +
                "last one, so keep this above that or simultaneous hits are wasted.");
            StaggerMax = c.Bind("Dive", "StaggerMax", 0.5f, "Maximum gap between birds of one group striking.");
            WaterMargin = c.Bind("Dive", "WaterMargin", 1f,
                "Birds never go lower than this above the water, or above the feet of the player they attack.");
            ScreamBeforeDive = c.Bind("Dive", "ScreamBeforeDive", true,
                "Scream as each bird tips into its dive. Heard by the host only; other players get the " +
                "game's own louder squawking from low-flying gulls.");
            ScreamVolume = c.Bind("Dive", "ScreamVolume", 0.8f, "Volume of the dive scream.");

            BoatIsSolid = c.Bind("Cover", "BoatIsSolid", true,
                "The boat blocks birds like rock does: hiding under the cabin roof protects you, and birds " +
                "can smash into the hull.");
            CrashKillsBird = c.Bind("Cover", "CrashKillsBird", true,
                "A bird that flies into rock, walls or (if solid) the boat during a dive dies on impact.");
            AttackMinDistance = c.Bind("Cover", "AttackMinDistance", 6f,
                "Closest horizontal distance a bird lines up its dive from.");
            AttackMaxDistance = c.Bind("Cover", "AttackMaxDistance", 22f,
                "Farthest horizontal distance a bird lines up its dive from.");

            ContactDamage = c.Bind("Damage", "ContactDamage", 7, "Damage per bird that connects during a dive.");
            ContactRadius = c.Bind("Damage", "ContactRadius", 1.1f, "Contact sphere radius, metres.");
            ContactKnockback = c.Bind("Damage", "Knockback", 2.5f,
                "Force imparted along the dive direction. Only matters if the hit is lethal.");

            GroupIntervalStart = c.Bind("Pacing", "DiveIntervalWave1", 3f,
                "Seconds between dive groups during wave 1.");
            GroupIntervalEnd = c.Bind("Pacing", "DiveIntervalFinalWave", 1.4f,
                "Seconds between dive groups during the final wave. Interpolated in between.");
            GroupSizeStart = c.Bind("Pacing", "DiveGroupWave1", 3, "Birds diving together in wave 1.");
            GroupSizeEnd = c.Bind("Pacing", "DiveGroupFinalWave", 7,
                "Birds diving together in the final wave. Interpolated in between.");

            FleeSeconds = c.Bind("Ending", "FleeSeconds", 8f,
                "When the encounter ends, surviving gulls fly away for this long before being removed.");

            HotkeysEnabled = c.Bind("Testing", "HotkeysEnabled", false,
                "Host-only debug keys: start the encounter, skip a wave, stop the encounter.");
            StartKey = c.Bind("Testing", "StartKey", KeyCode.F8,
                "Start the encounter immediately, ignoring the kill requirement and cooldown.");
            SkipWaveKey = c.Bind("Testing", "SkipWaveKey", KeyCode.F9,
                "Remove every bird in the current wave so the next one spawns.");
            StopKey = c.Bind("Testing", "StopKey", KeyCode.F10,
                "End the encounter: remaining birds disperse and the boss bar closes.");
            AutoStartAfterSeconds = c.Bind("Testing", "AutoStartAfterSeconds", 0f,
                "Start the encounter automatically this many seconds after hosting an island. 0 = off.");
            ChatFeedback = c.Bind("Testing", "ChatFeedback", true,
                "Echo encounter events into the host's own chat box. Only the host sees these.");

            Verbose = c.Bind("Debug", "Verbose", false, "Log every bird state change and dive result. Noisy.");
            StatusIntervalSeconds = c.Bind("Debug", "StatusIntervalSeconds", 5f,
                "During an encounter, write a status snapshot to the log this often. 0 = off.");
        }
    }
}
