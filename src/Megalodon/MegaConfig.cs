using BepInEx.Configuration;

namespace Expanded.Megalodon
{
    /// <summary>Every wakeboard and megalodon tunable. Host-authoritative except the purely local ones.</summary>
    internal sealed class MegaConfig
    {
        // --- wakeboard ---------------------------------------------------------
        public readonly ConfigEntry<int> WakeboardPrice;
        public readonly ConfigEntry<float> RopeLength;
        public readonly ConfigEntry<float> MinRopeLength;
        public readonly ConfigEntry<float> RopeBiteShortens;
        public readonly ConfigEntry<float> CarveAcceleration;
        public readonly ConfigEntry<float> WaterDrag;
        public readonly ConfigEntry<float> JumpSpeed;
        public readonly ConfigEntry<float> Gravity;
        public readonly ConfigEntry<float> SinkSpeed;
        public readonly ConfigEntry<float> SinkSeconds;
        public readonly ConfigEntry<float> MaxRopeAngle;
        public readonly ConfigEntry<float> RackSide;
        public readonly ConfigEntry<float> RackForward;
        public readonly ConfigEntry<float> RackLean;

        // --- the megalodon -----------------------------------------------------
        public readonly ConfigEntry<float> Health;
        public readonly ConfigEntry<float> Length;
        public readonly ConfigEntry<float> YawOffset;
        public readonly ConfigEntry<float> SwimDepth;
        public readonly ConfigEntry<float> ChaseDistance;
        public readonly ConfigEntry<float> CloseDistance;
        public readonly ConfigEntry<float> SafeBoatSpeed;
        public readonly ConfigEntry<float> MaxSpeed;
        public readonly ConfigEntry<float> BiteRadius;
        public readonly ConfigEntry<float> NearMissRadius;
        public readonly ConfigEntry<float> LatencyLead;
        public readonly ConfigEntry<float> MaxFightMinutes;
        public readonly ConfigEntry<int> RewardMoney;

        // --- trigger -----------------------------------------------------------
        public readonly ConfigEntry<float> TriggerDistance;
        public readonly ConfigEntry<float> TriggerRideSeconds;
        public readonly ConfigEntry<float> CooldownMinutes;
        public readonly ConfigEntry<int> FromIsland;

        // --- damage ------------------------------------------------------------
        public readonly ConfigEntry<float> ExplosionMultiplier;
        public readonly ConfigEntry<float> ExplosionReach;
        public readonly ConfigEntry<float> MineMultiplier;
        public readonly ConfigEntry<float> GunMultiplier;
        public readonly ConfigEntry<float> MaxGunDamagePerSecond;
        public readonly ConfigEntry<float> RodeoChargeFraction;
        public readonly ConfigEntry<int> BiteDamage;
        public readonly ConfigEntry<int> BoatHitDamage;

        // --- boat --------------------------------------------------------------
        public readonly ConfigEntry<int> MineStock;
        public readonly ConfigEntry<float> MineRestockSeconds;
        public readonly ConfigEntry<float> EngineDamageSeconds;
        public readonly ConfigEntry<float> EngineDamageDrag;
        public readonly ConfigEntry<float> StallChance;
        public readonly ConfigEntry<int> CordPullsNeeded;
        public readonly ConfigEntry<bool> SoloAutopilot;
        public readonly ConfigEntry<float> AutopilotThrottle;
        public readonly ConfigEntry<float> AutopilotTurn;

        // --- extras ------------------------------------------------------------
        public readonly ConfigEntry<bool> Hazards;
        public readonly ConfigEntry<bool> FogBanks;
        public readonly ConfigEntry<bool> Shouts;
        public readonly ConfigEntry<bool> Music;
        public readonly ConfigEntry<float> MusicVolume;
        public readonly ConfigEntry<bool> Swearing;

        public MegaConfig(ConfigFile c)
        {
            const string W = "Megalodon.Wakeboard";
            const string M = "Megalodon";
            const string T = "Megalodon.Trigger";
            const string D = "Megalodon.Damage";
            const string B = "Megalodon.Boat";
            const string X = "Megalodon.Extras";

            WakeboardPrice = c.Bind(W, "ShopPrice", 150, "What the wakeboard and tow line cost in the shop.");
            RopeLength = c.Bind(W, "RopeLength", 14f, "Tow rope length in metres.");
            MinRopeLength = c.Bind(W, "MinRopeLength", 7f, "Shortest the rope gets after the megalodon has bitten chunks off it.");
            RopeBiteShortens = c.Bind(W, "RopeBiteShortens", 3f, "Metres of rope a rope bite takes.");
            CarveAcceleration = c.Bind(W, "CarveAcceleration", 20f, "Sideways push when carving with A/D, m/s².");
            WaterDrag = c.Bind(W, "WaterDrag", 0.55f, "How quickly the water slows the rider when the rope goes slack.");
            JumpSpeed = c.Bind(W, "JumpSpeed", 6.5f, "Upward speed of a jump (Space), m/s.");
            Gravity = c.Bind(W, "Gravity", 15f, "Gravity while airborne. A bit more than real, so jumps feel snappy.");
            SinkSpeed = c.Bind(W, "SinkSpeed", 3f, "Below this speed the board stops planing and you start to sink.");
            SinkSeconds = c.Bind(W, "SinkSeconds", 1.6f, "How long you may be too slow before you sink off the board.");
            MaxRopeAngle = c.Bind(W, "MaxRopeAngle", 78f, "How far out to the side of the boat you can swing, in degrees.");
            RackSide = c.Bind(W, "RackSide", 0.55f, "Where the board leans in the boat: metres to the left of the driver's seat.");
            RackForward = c.Bind(W, "RackForward", 0.1f, "...and metres forward of it.");
            RackLean = c.Bind(W, "RackLean", 14f, "How far the board leans against the hull, in degrees.");

            Health = c.Bind(M, "Health", 1500f,
                "Hit points. A dynamite blast or a cannonball right on it does about 100, a barrel mine twice that.");
            Length = c.Bind(M, "Length", 11f, "Nose to tail, in metres. Your boat is about five.");
            YawOffset = c.Bind(M, "ModelYawOffset", 0f, "Turns the model if it swims backwards (180) or sideways (90).");
            SwimDepth = c.Bind(M, "SwimDepth", 0.25f, "How deep it swims while chasing. 0 = half out of the water.");
            ChaseDistance = c.Bind(M, "ChaseDistance", 16f, "Distance it keeps behind the rider when the boat is fast.");
            CloseDistance = c.Bind(M, "CloseDistance", 2f, "Distance it creeps up to when the boat is too slow.");
            SafeBoatSpeed = c.Bind(M, "SafeBoatSpeed", 8f, "Boat speed (m/s) that keeps it at arm's length. Slower and it closes in.");
            MaxSpeed = c.Bind(M, "MaxSpeed", 22f, "Its top speed, m/s.");
            BiteRadius = c.Bind(M, "BiteRadius", 1.9f, "Jaws this close to you when they shut = bitten.");
            NearMissRadius = c.Bind(M, "NearMissRadius", 4.5f, "Jaws this close = a near miss (and a lot of swearing).");
            LatencyLead = c.Bind(M, "LatencyLeadSeconds", 0.12f,
                "Aims lunges this far ahead to make up for network lag to a rider on another PC. 0 on a LAN.");
            MaxFightMinutes = c.Bind(M, "MaxFightMinutes", 9f, "It gives up after this long.");
            RewardMoney = c.Bind(M, "RewardMoney", 500, "Money for killing it.");

            TriggerDistance = c.Bind(T, "DistanceFromIsland", 110f, "How far out (from the boat's mooring) a wakeboarder must be for it to notice.");
            TriggerRideSeconds = c.Bind(T, "RideSeconds", 12f, "How long someone must ride out there before the fin shows.");
            CooldownMinutes = c.Bind(T, "CooldownMinutes", 3f, "Minimum time between megalodon fights.");
            FromIsland = c.Bind(T, "FromIsland", 0, "Only from this island on (0 = the first).");

            ExplosionMultiplier = c.Bind(D, "ExplosionMultiplier", 1f, "Scales damage from explosions (dynamite, cannon).");
            ExplosionReach = c.Bind(D, "ExplosionReach", 1.6f, "How far from its body an explosion still hurts, as a multiple of the blast radius.");
            MineMultiplier = c.Bind(D, "MineMultiplier", 2f, "Barrel mines are built for this. Extra damage multiplier.");
            GunMultiplier = c.Bind(D, "GunMultiplier", 0.5f, "Scales bullet damage. It's a very big fish.");
            MaxGunDamagePerSecond = c.Bind(D, "MaxGunDamagePerSecond", 45f, "Upper limit on gunfire damage per second, whole crew.");
            RodeoChargeFraction = c.Bind(D, "RodeoChargeFraction", 0.18f, "A charge planted on its back takes this fraction of its max health.");
            BiteDamage = c.Bind(D, "BiteDamage", 25, "Health a bite takes from the wakeboarder, on top of losing the board. 0 = boards only.");
            BoatHitDamage = c.Bind(D, "BoatHitDamage", 10, "Health everyone on deck loses when it rams the boat or bites the stern.");

            MineStock = c.Bind(B, "MineStock", 3, "Barrel mines the driver can drop (G). Restocked over time.");
            MineRestockSeconds = c.Bind(B, "MineRestockSeconds", 22f, "A new mine every this many seconds, up to the stock.");
            EngineDamageSeconds = c.Bind(B, "EngineDamageSeconds", 12f, "How long a stern chomp cripples the engine.");
            EngineDamageDrag = c.Bind(B, "EngineDamageDrag", 0.35f, "Extra drag while the engine is damaged.");
            StallChance = c.Bind(B, "StallChance", 0.45f, "Chance (0-1) a stern chomp stalls the engine outright.");
            CordPullsNeeded = c.Bind(B, "CordPullsNeeded", 8, "Space presses to restart a stalled engine.");
            SoloAutopilot = c.Bind(B, "SoloOldSaltDrives", true,
                "Playing alone? Old Salt takes the helm while you wakeboard. He can't see without his teeth.");
            AutopilotThrottle = c.Bind(B, "OldSaltThrottle", 0.85f, "How hard Old Salt opens the throttle (0-1).");
            AutopilotTurn = c.Bind(B, "OldSaltTurn", 0.9f, "How hard he steers.");

            Hazards = c.Bind(X, "SeaHazards", true, "Flying fish, jellyfish, buoys and ramps during the fight.");
            FogBanks = c.Bind(X, "FogBanks", true, "Fog banks roll in during the fight (local fog, nothing else).");
            Shouts = c.Bind(X, "Shouts", true, "Players yell (speech bubble + NPC mumble) at near misses, bites and so on.");
            Music = c.Bind(X, "Music", true, "The dun-dun. And the heartbeat.");
            MusicVolume = c.Bind(X, "MusicVolume", 0.55f, "Volume of the dun-dun and heartbeat (0-1).");
            Swearing = c.Bind(X, "Swearing", true, "Let the players swear when things go wrong. Off = family edition.");
        }
    }
}
