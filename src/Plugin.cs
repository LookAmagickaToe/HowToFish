using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SeagullSwarm
{
    [BepInPlugin(Guid, "Seagull Swarm", "1.0.0")]
    public class SeagullSwarmPlugin : BaseUnityPlugin
    {
        public const string Guid = "dazed.howtofish.seagullswarm";

        internal static ManualLogSource Log;
        internal static SwarmConfig Cfg;

        private void Awake()
        {
            Log = Logger;
            Cfg = new SwarmConfig(Config);
            Diag.Init(Path.Combine(Paths.BepInExRootPath, "SeagullSwarm.log"));

            Diag.Info("Seagull Swarm 1.0.0 on Unity " + Application.unityVersion + ", log file " + Diag.FilePath);
            Diag.Info("Config: provoke " + Cfg.KillsToProvoke.Value + " kills/" + Cfg.ProvokeWindowSeconds.Value +
                      "s, waves " + Cfg.WaveCount.Value + " from " + Cfg.FirstWaveSize.Value + " x" + Cfg.WaveGrowth.Value +
                      ", dmg " + Cfg.ContactDamage.Value + ", leader " + Cfg.SpawnLeader.Value +
                      ", hotkeys " + (Cfg.HotkeysEnabled.Value
                          ? Cfg.StartKey.Value + "/" + Cfg.SkipWaveKey.Value + "/" + Cfg.StopKey.Value
                          : "off") +
                      ", autostart " + Cfg.AutoStartAfterSeconds.Value + "s, verbose " + Cfg.Verbose.Value);

            Harmony harmony = new Harmony(Guid);
            try
            {
                harmony.PatchAll(typeof(Patches));
            }
            catch (Exception e)
            {
                Diag.Exception("Harmony.PatchAll", e);
            }

            // Confirm every hook actually landed. A missing one is the most likely way a game update
            // breaks the mod, and it would otherwise fail silently.
            int count = 0;
            foreach (MethodBase m in harmony.GetPatchedMethods())
            {
                Diag.Info("  hooked " + m.DeclaringType.Name + "." + m.Name);
                count++;
            }
            if (count == ExpectedPatchCount)
                Diag.Info("All " + count + " hooks attached. Ready: host an island to arm the director.");
            else
                Diag.Error("Only " + count + " of " + ExpectedPatchCount + " hooks attached - mod will not work correctly.");
        }

        private const int ExpectedPatchCount = 5;
    }

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

        // --- Waves ---------------------------------------------------------
        public readonly ConfigEntry<int> WaveCount;
        public readonly ConfigEntry<int> FirstWaveSize;
        public readonly ConfigEntry<float> WaveGrowth;

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

        // --- Leader --------------------------------------------------------
        public readonly ConfigEntry<bool> SpawnLeader;
        public readonly ConfigEntry<int> LeaderHp;
        public readonly ConfigEntry<bool> LeaderHpScalesWithPlayers;
        public readonly ConfigEntry<bool> LeaderDeathEndsSwarm;
        public readonly ConfigEntry<float> LeaderSpawnHeight;
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

            WaveCount = c.Bind("Waves", "WaveCount", 5, "Number of waves in a full encounter.");
            FirstWaveSize = c.Bind("Waves", "FirstWaveSize", 10, "Birds in wave 1.");
            WaveGrowth = c.Bind("Waves", "Growth", 1.3f,
                "Per-wave multiplier. 10 x 1.3^(n-1) gives 10 / 13 / 17 / 22 / 29 = 91 birds.");

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
            DiveDuration = c.Bind("Dive", "Duration", 1.15f,
                "Seconds the ballistic dive takes to reach the target. Lower is nastier.");
            DiveGravity = c.Bind("Dive", "Gravity", 22f,
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

            GroupIntervalStart = c.Bind("Pacing", "GroupIntervalWave1", 4.5f,
                "Seconds between dive groups during wave 1.");
            GroupIntervalEnd = c.Bind("Pacing", "GroupIntervalFinalWave", 1.6f,
                "Seconds between dive groups during the final wave. Interpolated in between.");
            GroupSizeStart = c.Bind("Pacing", "GroupSizeWave1", 2, "Birds diving together in wave 1.");
            GroupSizeEnd = c.Bind("Pacing", "GroupSizeFinalWave", 6,
                "Birds diving together in the final wave. Interpolated in between.");

            SpawnLeader = c.Bind("Leader", "Spawn", true,
                "Spawn an Albatross as flock leader. It is a real, killable boss with the game's boss bar " +
                "and timer; kill it for its trophy and meat.");
            LeaderHp = c.Bind("Leader", "Hp", 500,
                "Albatross health for a solo player. The unmodded boss has 7800.");
            LeaderHpScalesWithPlayers = c.Bind("Leader", "HpScalesWithPlayers", true,
                "Add the game's usual bonus health per extra player (+50% of base each).");
            LeaderDeathEndsSwarm = c.Bind("Leader", "DeathEndsSwarm", true,
                "Killing the Albatross wins the encounter immediately and the gulls flee. If false, the " +
                "remaining waves must still be cleared.");
            LeaderSpawnHeight = c.Bind("Leader", "SpawnHeight", 34f, "Height above the anchor the leader appears at.");
            FleeSeconds = c.Bind("Leader", "FleeSeconds", 8f,
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
