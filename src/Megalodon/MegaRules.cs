using System;

namespace Expanded.Megalodon
{
    /// <summary>What the wakeboarder is standing on. Every bite takes one step down the list.</summary>
    public enum Board : byte
    {
        Wakeboard = 0,
        Door = 1,
        Bathtub = 2,
        Barefoot = 3,
        Eaten = 4
    }

    public enum Phase : byte
    {
        One = 1,
        Two = 2,
        Three = 3
    }

    /// <summary>Things the megalodon itself does.</summary>
    public enum Attack : byte
    {
        None = 0,
        BreachLunge,
        SkimLunge,
        FakeOut,
        RopeBite,
        TailSlap,
        PlayDead,
        SternChomp,
        JumpOver,
        Alongside
    }

    /// <summary>Things the sea throws in while the megalodon is busy.</summary>
    public enum Hazard : byte
    {
        None = 0,
        FlyingFish,
        Jellyfish,
        Buoy,
        FogBank,
        Ramp
    }

    /// <summary>
    /// The megalodon fight's rules, kept free of Unity so they can be tested headlessly: board
    /// tiers, fight phases, which attack comes next, the bite meter, explosion fall-off.
    /// </summary>
    public static class MegaRules
    {
        // ------------------------------------------------------------------ boards

        public static Board Next(Board b) => b >= Board.Eaten ? Board.Eaten : (Board)((byte)b + 1);

        public static string BoardName(Board b)
        {
            switch (b)
            {
                case Board.Wakeboard: return "Wakeboard";
                case Board.Door: return "Old Salt's cabin door";
                case Board.Bathtub: return "A bathtub (with duck)";
                case Board.Barefoot: return "Your bare feet";
                default: return "Nothing. You were eaten.";
            }
        }

        /// <summary>Bites left before the next one eats you.</summary>
        public static int BitesLeft(Board b) => Math.Max(0, (int)Board.Barefoot - (int)b);

        /// <summary>How hard you can carve on it. A door is not a wakeboard.</summary>
        public static float CarveMultiplier(Board b)
        {
            switch (b)
            {
                case Board.Wakeboard: return 1f;
                case Board.Door: return 0.8f;
                case Board.Bathtub: return 0.62f;
                case Board.Barefoot: return 0.5f;
                default: return 0f;
            }
        }

        /// <summary>How high you can jump off it.</summary>
        public static float JumpMultiplier(Board b)
        {
            switch (b)
            {
                case Board.Wakeboard: return 1f;
                case Board.Door: return 0.85f;
                case Board.Bathtub: return 0.55f;
                case Board.Barefoot: return 0.45f;
                default: return 0f;
            }
        }

        /// <summary>How much it wobbles you about (camera roll, drift), 0 = rock steady.</summary>
        public static float Wobble(Board b)
        {
            switch (b)
            {
                case Board.Wakeboard: return 0f;
                case Board.Door: return 0.35f;
                case Board.Bathtub: return 0.7f;
                case Board.Barefoot: return 1f;
                default: return 0f;
            }
        }

        // ------------------------------------------------------------------ phases

        /// <summary>Fight phase from the megalodon's remaining health (0-1).</summary>
        public static Phase PhaseFor(float hpFraction)
        {
            if (hpFraction > 2f / 3f) return Phase.One;
            if (hpFraction > 1f / 3f) return Phase.Two;
            return Phase.Three;
        }

        /// <summary>Seconds between the megalodon's own attacks.</summary>
        public static float AttackInterval(Phase p)
        {
            switch (p)
            {
                case Phase.One: return 6f;
                case Phase.Two: return 5f;
                default: return 4.2f;
            }
        }

        /// <summary>Seconds between sea hazards (flying fish, jellyfish...).</summary>
        public static float HazardInterval(Phase p)
        {
            switch (p)
            {
                case Phase.One: return 14f;
                case Phase.Two: return 10f;
                default: return 8f;
            }
        }

        private static readonly Attack[] PoolOne = { Attack.SkimLunge, Attack.BreachLunge, Attack.Alongside, Attack.SkimLunge, Attack.BreachLunge, Attack.PlayDead };
        private static readonly Attack[] PoolTwo = { Attack.SkimLunge, Attack.BreachLunge, Attack.FakeOut, Attack.Alongside, Attack.RopeBite, Attack.TailSlap, Attack.PlayDead };
        private static readonly Attack[] PoolThree = { Attack.SkimLunge, Attack.BreachLunge, Attack.FakeOut, Attack.RopeBite, Attack.Alongside, Attack.TailSlap, Attack.SternChomp, Attack.JumpOver, Attack.SternChomp };

        public static Attack[] Pool(Phase p) => p == Phase.One ? PoolOne : p == Phase.Two ? PoolTwo : PoolThree;

        private static readonly Hazard[] HazardsOne = { Hazard.Ramp, Hazard.Ramp, Hazard.FlyingFish };
        private static readonly Hazard[] HazardsTwo = { Hazard.FlyingFish, Hazard.FlyingFish, Hazard.Jellyfish, Hazard.Buoy, Hazard.Ramp, Hazard.FogBank };
        private static readonly Hazard[] HazardsThree = { Hazard.FlyingFish, Hazard.FlyingFish, Hazard.Jellyfish, Hazard.Buoy, Hazard.FogBank, Hazard.Ramp };

        public static Hazard[] Hazards(Phase p) => p == Phase.One ? HazardsOne : p == Phase.Two ? HazardsTwo : HazardsThree;

        /// <summary>
        /// Picks the next attack from the phase's pool. <paramref name="roll"/> is a uniform random
        /// number in [0,1). The same move never comes twice in a row (a pool always has more than one).
        /// Play-dead only works once per phase at most, so it is skipped when <paramref name="playDeadUsed"/>.
        /// </summary>
        public static Attack PickAttack(Phase p, double roll, Attack last, bool playDeadUsed)
        {
            Attack[] pool = Pool(p);
            int n = pool.Length;
            int start = (int)(Clamp01(roll) * n) % n;
            for (int i = 0; i < n; i++)
            {
                Attack a = pool[(start + i) % n];
                if (a == last) continue;
                if (a == Attack.PlayDead && playDeadUsed) continue;
                return a;
            }
            return pool[start] == Attack.PlayDead ? Attack.SkimLunge : pool[start];
        }

        public static Hazard PickHazard(Phase p, double roll, Hazard last)
        {
            Hazard[] pool = Hazards(p);
            int n = pool.Length;
            int start = (int)(Clamp01(roll) * n) % n;
            for (int i = 0; i < n; i++)
            {
                Hazard h = pool[(start + i) % n];
                if (h != last) return h;
            }
            return pool[start];
        }

        // ------------------------------------------------------------------ chase

        /// <summary>
        /// How close the megalodon wants to be. A fast boat keeps it at arm's length; a slow boat lets
        /// it creep right up behind the rider - which is how "too slow" turns into "eaten".
        /// </summary>
        public static float PreferredDistance(float boatSpeed, float minSafeSpeed, float chaseDistance, float closeDistance)
        {
            if (minSafeSpeed <= 1f) return chaseDistance;
            float t = Clamp01((boatSpeed - minSafeSpeed * 0.45f) / (minSafeSpeed * 0.55f));
            return closeDistance + (chaseDistance - closeDistance) * t;
        }

        /// <summary>0 = far away, 1 = its jaws are at your heels.</summary>
        public static float BiteMeter(float distance, float biteRadius, float chaseDistance)
        {
            float far = Math.Max(biteRadius + 0.5f, chaseDistance + 6f);
            return 1f - Clamp01((distance - biteRadius) / (far - biteRadius));
        }

        // ------------------------------------------------------------------ damage

        /// <summary>
        /// Damage from an explosion <paramref name="distance"/> metres from the megalodon's body:
        /// full inside the blast radius, falling to nothing at radius x reach.
        /// </summary>
        public static int ExplosionDamage(float distance, float radius, int baseDamage, float reach, float multiplier)
        {
            if (baseDamage <= 0 || multiplier <= 0f) return 0;
            float r = Math.Max(0.5f, radius);
            float outer = r * Math.Max(1f, reach);
            if (distance >= outer) return 0;
            float falloff = distance <= r ? 1f : 1f - (distance - r) / (outer - r);
            return (int)Math.Round(baseDamage * multiplier * Clamp01(falloff));
        }

        // ------------------------------------------------------------------ helpers

        public static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
        private static double Clamp01(double v) => v < 0.0 ? 0.0 : v >= 1.0 ? 0.999999 : v;
    }
}
