using BepInEx.Configuration;
using UnityEngine;

namespace Expanded.Pirates
{
    /// <summary>Every pirate tunable. Host-authoritative except the purely visual ones.</summary>
    internal sealed class PirateConfig
    {
        // --- ship --------------------------------------------------------------
        public readonly ConfigEntry<string> ShipName;
        public readonly ConfigEntry<string> Model;
        public readonly ConfigEntry<float> Scale;
        public readonly ConfigEntry<float> Health;

        // --- sailing -----------------------------------------------------------
        public readonly ConfigEntry<float> Speed;
        public readonly ConfigEntry<float> Acceleration;
        public readonly ConfigEntry<float> TurnRate;
        public readonly ConfigEntry<float> StandoffDistance;
        public readonly ConfigEntry<float> CircleSwapSeconds;
        public readonly ConfigEntry<float> LandProbeDistance;
        public readonly ConfigEntry<float> SpawnDistance;
        public readonly ConfigEntry<float> MaxFightSeconds;

        // --- floating ----------------------------------------------------------
        public readonly ConfigEntry<float> Waterline;
        public readonly ConfigEntry<float> BobHeight;
        public readonly ConfigEntry<float> RollDegrees;
        public readonly ConfigEntry<float> SinkSpeed;
        public readonly ConfigEntry<float> SinkSeconds;
        public readonly ConfigEntry<float> SurrenderSeconds;
        public readonly ConfigEntry<float> CrewHealth;
        public readonly ConfigEntry<float> CaptainHealth;

        // --- enemy gunnery -----------------------------------------------------
        public readonly ConfigEntry<int> PortsPerSide;
        public readonly ConfigEntry<float> PortSideFraction;
        public readonly ConfigEntry<float> PortHeightFraction;
        public readonly ConfigEntry<float> PortLengthFraction;
        public readonly ConfigEntry<float> CannonModelScale;
        public readonly ConfigEntry<float> VolleyInterval;
        public readonly ConfigEntry<float> VolleyStagger;
        public readonly ConfigEntry<float> FirstVolleyDelay;
        public readonly ConfigEntry<float> FiringArcDegrees;
        public readonly ConfigEntry<float> SpreadAtStandoff;
        public readonly ConfigEntry<float> CannonSpeed;
        public readonly ConfigEntry<float> CannonGravity;
        public readonly ConfigEntry<float> CannonballScale;
        public readonly ConfigEntry<float> WaterFishChance;
        public readonly ConfigEntry<float> DeckCannonYawArc;

        // --- damage to the ship ------------------------------------------------
        public readonly ConfigEntry<float> ExplosionDamageMultiplier;
        public readonly ConfigEntry<float> ExplosionReach;
        public readonly ConfigEntry<float> GunDamageMultiplier;
        public readonly ConfigEntry<float> MaxGunDamagePerSecond;

        // --- your deck cannon --------------------------------------------------
        public readonly ConfigEntry<KeyCode> InteractKey;
        public readonly ConfigEntry<float> InteractRange;
        public readonly ConfigEntry<float> DeckCannonCooldown;
        public readonly ConfigEntry<float> DeckCannonSpeed;
        public readonly ConfigEntry<float> DeckCannonMaxElevation;
        public readonly ConfigEntry<float> DeckCannonScale;
        public readonly ConfigEntry<int> CannonPrice;

        // --- triggers ----------------------------------------------------------
        public readonly ConfigEntry<float> AtSeaDistance;
        public readonly ConfigEntry<float> SiteDistance;
        public readonly ConfigEntry<float> SiteReachRadius;
        public readonly ConfigEntry<float> SiteEngageRadius;
        public readonly ConfigEntry<int> RaidMoneyThreshold;
        public readonly ConfigEntry<float> RaidCooldownMinutes;
        public readonly ConfigEntry<float> RaidChancePerMinute;
        public readonly ConfigEntry<int> RaidLootMoney;

        public PirateConfig(ConfigFile c)
        {
            const string S = "Pirates";
            const string G = "Pirates.Gunnery";
            const string D = "Pirates.Damage";
            const string P = "Pirates.DeckCannon";
            const string T = "Pirates.Triggers";

            ShipName = c.Bind(S, "ShipName", "The Salted Widow", "Name shown on the health bar.");
            Model = c.Bind(S, "Model", "ship-pirate-large", "Kit model used for the enemy ship.");
            Scale = c.Bind(S, "Scale", 1f, "Extra scaling on top of the model's own size.");
            Health = c.Bind(S, "Health", 400f,
                "Hull points. A dynamite blast or cannonball against the hull does about 100.");

            Speed = c.Bind(S, "Speed", 7f, "Cruising speed in m/s.");
            Acceleration = c.Bind(S, "Acceleration", 3f, "How quickly she reaches that speed.");
            TurnRate = c.Bind(S, "TurnRate", 0.6f,
                "Steering rate. Low values make her turn like a heavy ship, which is the point.");
            StandoffDistance = c.Bind(S, "StandoffDistance", 35f,
                "Distance she holds from your boat while presenting a broadside.");
            CircleSwapSeconds = c.Bind(S, "CircleSwapSeconds", 20f,
                "How often she comes about to fire the other broadside.");
            LandProbeDistance = c.Bind(S, "LandProbeDistance", 18f,
                "How far ahead she looks for land before steering away.");
            SpawnDistance = c.Bind(S, "SpawnDistance", 90f, "How far out she appears.");
            MaxFightSeconds = c.Bind(S, "MaxFightSeconds", 420f,
                "After this long she gives up and sails off.");

            // New key name on purpose: the old "Draft" entry defaulted to 0, which left the hull
            // sitting on the water instead of in it.
            Waterline = c.Bind(S, "Waterline", -1.6f,
                "Vertical offset from the water surface. Negative sinks the hull into the water.");
            BobHeight = c.Bind(S, "BobHeight", 0.25f, "Bobbing amplitude in metres.");
            RollDegrees = c.Bind(S, "RollDegrees", 4f, "How far she rolls with the swell.");
            SinkSpeed = c.Bind(S, "SinkSpeed", 0.9f, "How fast she goes down, m/s.");
            SinkSeconds = c.Bind(S, "SinkSeconds", 12f, "How long the sinking lasts before she is removed.");
            SurrenderSeconds = c.Bind(S, "SurrenderSeconds", 10f,
                "After the captain falls and she strikes her colours, how long she drifts before being towed off.");
            CrewHealth = c.Bind(S, "CrewHealth", 50f, "Hit points of each crew member.");
            CaptainHealth = c.Bind(S, "CaptainHealth", 120f, "Hit points of the captain. Kill him and she surrenders.");

            PortsPerSide = c.Bind(G, "PortsPerSide", 3, "Guns on each side.");
            // 0.85 puts the guns just inside the rail. (The earlier 0.5 was sized from a rotated
            // preview measurement that overstated the beam; the true beam is under 5 m.)
            PortSideFraction = c.Bind(G, "GunRailFraction", 0.85f,
                "Gun distance from the centreline, as a fraction of the half-width.");
            PortHeightFraction = c.Bind(G, "PortHeightFraction", 0.24f,
                "Gun height above the keel, as a fraction of the total height.");
            PortLengthFraction = c.Bind(G, "PortLengthFraction", 0.55f,
                "How much of the hull length the gun row spans.");
            CannonModelScale = c.Bind(G, "CannonModelScale", 1f, "Size of the cannon models at the ports.");
            VolleyInterval = c.Bind(G, "VolleyInterval", 7f, "Seconds between broadsides.");
            VolleyStagger = c.Bind(G, "VolleyStagger", 0.25f, "Gap between guns within one broadside.");
            FirstVolleyDelay = c.Bind(G, "FirstVolleyDelay", 10f,
                "Grace period after she appears before the first broadside.");
            FiringArcDegrees = c.Bind(G, "FiringArcDegrees", 50f,
                "How far off the beam the target may be and still get fired at.");
            SpreadAtStandoff = c.Bind(G, "SpreadAtStandoff", 4f,
                "Aim scatter in metres at standoff range; scales with distance.");
            CannonSpeed = c.Bind(G, "CannonSpeed", 42f, "Muzzle velocity in m/s.");
            CannonGravity = c.Bind(G, "CannonGravity", 9.81f, "Gravity on cannonballs.");
            CannonballScale = c.Bind(G, "CannonballScale", 1.6f, "Visual size of cannonballs.");
            WaterFishChance = c.Bind(G, "WaterFishChance", 1f / 6f,
                "Chance (0-1) that a cannonball landing in the water throws up stunned fish, like dynamite does. " +
                "The splash and shock wave happen either way.");

            ExplosionDamageMultiplier = c.Bind(D, "ExplosionDamageMultiplier", 1f,
                "Scales damage from explosions (your cannon, thrown dynamite) to the ship.");
            ExplosionReach = c.Bind(D, "ExplosionReach", 1.5f,
                "How far from the hull an explosion still hurts her, as a multiple of its blast radius.");
            GunDamageMultiplier = c.Bind(D, "GunDamageMultiplier", 0.25f,
                "Scales bullet damage to the ship. Wooden hulls shrug off pistol rounds.");
            MaxGunDamagePerSecond = c.Bind(D, "MaxGunDamagePerSecond", 60f,
                "Upper limit on gunfire damage per second across the whole crew.");

            InteractKey = c.Bind(P, "InteractKey", KeyCode.F,
                "Unused since the gun is manned with the game's interact key (E); kept for old configs.");
            InteractRange = c.Bind(P, "InteractRange", 2.5f, "How far from the gun a shot is still accepted (host check).");
            DeckCannonCooldown = c.Bind(P, "Cooldown", 4f, "Reload time in seconds (press R at the gun).");
            DeckCannonYawArc = c.Bind(P, "TraverseDegrees", 40f,
                "How far the manned gun swings to either side of straight ahead.");
            DeckCannonSpeed = c.Bind(P, "MuzzleVelocity", 45f, "Muzzle velocity in m/s.");
            DeckCannonMaxElevation = c.Bind(P, "MaxElevationDegrees", 35f,
                "Highest you can aim. The lowest is fixed at -10 so you cannot shoot your own deck.");
            DeckCannonScale = c.Bind(P, "ModelScale", 0.55f,
                "Size of the swivel gun on your boat and in the shop. The model is built for a 13 m pirate ship; " +
                "0.55 suits the small boat.");
            CannonPrice = c.Bind(P, "Price", 750, "What the swivel gun costs in the shop.");

            AtSeaDistance = c.Bind(T, "AtSeaDistance", 70f,
                "How far your boat must be from the island's mooring to count as at sea.");
            SiteDistance = c.Bind(T, "ChartMarkDistance", 200f,
                "How far from the island's mooring the chart's mark (the buoy) is placed.");
            SiteReachRadius = c.Bind(T, "ChartMarkReachRadius", 35f,
                "How close to the buoy counts as having arrived.");
            SiteEngageRadius = c.Bind(T, "ChartMarkEngageRadius", 140f,
                "During the fight step, coming this close to the mark brings the pirate ship in.");
            RaidMoneyThreshold = c.Bind(T, "RaidMoneyThreshold", 2500,
                "After the story fight, crews with at least this much money can be raided. 0 = never.");
            RaidCooldownMinutes = c.Bind(T, "RaidCooldownMinutes", 20f, "Minimum time between raids.");
            RaidChancePerMinute = c.Bind(T, "RaidChancePerMinute", 0.2f,
                "Chance per minute at sea of a raid once eligible.");
            RaidLootMoney = c.Bind(T, "RaidLootMoney", 600, "Money for sinking a raider.");
        }
    }
}
