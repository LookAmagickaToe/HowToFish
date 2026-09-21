using System;
using System.Collections.Generic;
using FishNet;
using UnityEngine;

namespace Expanded.Pirates
{
    /// <summary>
    /// Every cannonball in flight, from any cannon.
    ///
    /// Split authority:
    ///  - The host simulates each ball, sweeps it against the world, and on impact detonates it
    ///    through the game's own explosion code, so damage, knockback, splash, stunned fish and
    ///    effects are exactly what dynamite does, on every client.
    ///  - Clients never simulate hits. They get one "fired" message and animate a purely visual ball
    ///    along the same parabola, then drop it when the host reports the impact. That is far
    ///    smoother than streaming positions, and costs two small messages per shot.
    /// </summary>
    internal static class Cannonballs
    {
        private sealed class Ball
        {
            public uint Id;
            public Vector3 Pos;
            public Vector3 Vel;
            public float Gravity;
            public bool FromPirates;
            public float Age;
        }

        private sealed class Visual
        {
            public GameObject Go;
            public Vector3 Pos;
            public Vector3 Vel;
            public float Gravity;
            public float Age;
        }

        private const float MaxLifetime = 9f;
        private const float WaterExplosionDepth = 0.35f;

        private static readonly List<Ball> Live = new List<Ball>();
        private static readonly Dictionary<uint, Visual> Visuals = new Dictionary<uint, Visual>();
        private static uint _nextId = 1;

        /// <summary>True while a pirate ball is detonating, so the ship does not damage itself.</summary>
        internal static bool DetonatingPirateBall { get; private set; }

        internal static int InFlight => Live.Count;

        // ------------------------------------------------------------------ host

        /// <summary>Host only. Launches a ball and tells every client to animate it.</summary>
        internal static void Fire(Vector3 origin, Vector3 velocity, bool fromPirates)
        {
            if (!InstanceFinder.IsServerStarted) return;

            float g = Mathf.Max(0.1f, PirateModule.Cfg.CannonGravity.Value);
            var ball = new Ball
            {
                Id = _nextId++,
                Pos = origin,
                Vel = velocity,
                Gravity = g,
                FromPirates = fromPirates
            };
            Live.Add(ball);

            ModNet.SendToAll(Msg.CannonFired, w =>
            {
                w.Write(ball.Id);
                WriteVec(w, origin);
                WriteVec(w, velocity);
                w.Write(g);
                w.Write(fromPirates);
            });
        }

        /// <summary>Host only. Advances every live ball and resolves impacts.</summary>
        internal static void ServerTick(float dt)
        {
            if (Live.Count == 0 || dt <= 0f) return;

            float waterY = PirateModule.WaterY();

            for (int i = Live.Count - 1; i >= 0; i--)
            {
                Ball b = Live[i];
                b.Age += dt;

                Vector3 from = b.Pos;
                b.Vel += Vector3.down * b.Gravity * dt;
                Vector3 to = from + b.Vel * dt;

                Vector3 hitPoint;
                if (SweepHit(from, to, b, out hitPoint))
                {
                    Live.RemoveAt(i);
                    Detonate(b, hitPoint, false);
                    continue;
                }

                if (to.y <= waterY)
                {
                    // Interpolate to the exact surface crossing so the splash lines up.
                    float t = Mathf.InverseLerp(from.y, to.y, waterY);
                    Vector3 surface = Vector3.Lerp(from, to, t);
                    Live.RemoveAt(i);
                    Detonate(b, surface, true);
                    continue;
                }

                if (b.Age > MaxLifetime)
                {
                    Live.RemoveAt(i);
                    NotifyImpact(b.Id);
                    continue;
                }

                b.Pos = to;
            }
        }

        /// <summary>
        /// Swept test against everything a ball should stop on. Each side's balls ignore their own
        /// platform, or they would detonate in the muzzle on the first frame: a pirate ball ignores
        /// the pirate ship, a crew ball ignores the crew's boat and, just after firing, the crew
        /// standing around the gun.
        /// </summary>
        private static bool SweepHit(Vector3 from, Vector3 to, Ball ball, out Vector3 point)
        {
            point = to;
            Vector3 d = to - from;
            float len = d.magnitude;
            if (len < 1e-5f) return false;

            int mask = ball.FromPirates ? GameInfo.NpcProjectileHitLayer.value : GameInfo.ProjectileHitLayer.value;
            mask |= GameInfo.LevelLayer.value | GameInfo.BoatLayer.value;

            RaycastHit[] hits = Physics.RaycastAll(from, d / len, len, mask, QueryTriggerInteraction.Ignore);
            if (hits.Length == 0) return false;

            Transform ownBoat = BoatManager.Boat != null ? BoatManager.Boat.transform : null;

            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (RaycastHit h in hits)
            {
                if (h.collider == null) continue;
                Transform t = h.collider.transform;

                if (ball.FromPirates)
                {
                    if (h.collider.GetComponentInParent<PirateShip>() != null) continue;
                }
                else
                {
                    if (ownBoat != null && t.IsChildOf(ownBoat)) continue;
                    if (ball.Age < 0.35f && PlayerManager.GetPlayerFromBodyPart(t) != null) continue;
                }

                point = h.point;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Detonates by spawning a stick of the game's own dynamite at the impact point and letting
        /// the game explode it. Underwater impacts go slightly below the surface so the game plays
        /// its water explosion, knocks the boat about and throws up stunned fish.
        /// </summary>
        private static readonly System.Reflection.FieldInfo FishField =
            HarmonyLib.AccessTools.Field(typeof(ExplosionInfo), "_underwaterFishMinMax");

        private static void Detonate(Ball b, Vector3 point, bool water)
        {
            NotifyImpact(b.Id);

            try
            {
                Item prefab = GameInfo.GetSpawnable("dynamite");
                if (prefab == null || ItemManager.Instance == null)
                {
                    Diag.Warn("Cannonballs: dynamite prefab unavailable; impact has no explosion.");
                    return;
                }

                Vector3 at = water ? point + Vector3.down * WaterExplosionDepth : point;
                Item charge = ItemManager.Instance.SpawnNewItem(prefab, at, Quaternion.identity);
                if (charge == null) return;

                ExplosionInfo info = charge.GetExplosionInfo();
                if (info == null)
                {
                    Diag.Warn("Cannonballs: spawned charge has no explosion info.");
                    charge.DestroyItem((byte)DestroyReason.Immediate);
                    return;
                }

                // Dynamite in the water always throws up a handful of stunned fish. From a cannon that
                // floods the sea with fish on every miss, so it only happens now and then. The value
                // is restored afterwards in case the game pools and reuses this stick.
                object fishBefore = null;
                if (water && FishField != null &&
                    UnityEngine.Random.value >= Mathf.Clamp01(PirateModule.Cfg.WaterFishChance.Value))
                {
                    fishBefore = FishField.GetValue(info);
                    FishField.SetValue(info, Vector2Int.zero);
                }

                DetonatingPirateBall = b.FromPirates;
                try
                {
                    ExplosionManager.ServerExplode(charge, info);
                }
                finally
                {
                    DetonatingPirateBall = false;
                    if (fishBefore != null) FishField.SetValue(info, fishBefore);
                }

                Diag.Debug("Cannonball " + b.Id + (b.FromPirates ? " (pirate)" : " (crew)") + " hit " +
                           (water ? "water" : "solid") + " at " + point.ToString("F1") + " after " +
                           b.Age.ToString("0.00") + "s.");
            }
            catch (Exception e)
            {
                Diag.Exception("Cannonballs.Detonate", e);
            }
        }

        private static void NotifyImpact(uint id)
        {
            ModNet.SendToAll(Msg.CannonImpact, w => w.Write(id));
        }

        internal static void ServerClear() => Live.Clear();

        // ------------------------------------------------------------------ clients (host included)

        internal static void RegisterClientHandlers()
        {
            ModNet.OnClient(Msg.CannonFired, r =>
            {
                uint id = r.ReadUInt32();
                Vector3 origin = ReadVec(r);
                Vector3 vel = ReadVec(r);
                float g = r.ReadSingle();
                bool fromPirates = r.ReadBoolean();
                SpawnVisual(id, origin, vel, g, fromPirates);
            });

            ModNet.OnClient(Msg.CannonImpact, r =>
            {
                uint id = r.ReadUInt32();
                RemoveVisual(id);
            });
        }

        private static void SpawnVisual(uint id, Vector3 origin, Vector3 vel, float g, bool fromPirates)
        {
            RemoveVisual(id);

            GameObject go = ModAssets.Create("cannon-ball", origin, Quaternion.identity, null, solid: false);
            if (go == null)
            {
                // No bundle: still show something rather than an invisible shot.
                go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                UnityEngine.Object.Destroy(go.GetComponent<Collider>());
                go.transform.localScale = Vector3.one * 0.4f;
            }
            go.transform.localScale *= PirateModule.Cfg.CannonballScale.Value;
            go.name = "Cannonball" + id;

            Visuals[id] = new Visual { Go = go, Pos = origin, Vel = vel, Gravity = g };
            MuzzleEffects(origin, vel, fromPirates);
        }

        private static void MuzzleEffects(Vector3 origin, Vector3 vel, bool fromPirates)
        {
            try
            {
                AudioManager.PlayClipAt("Explosion", origin, true, AudioDistance.Long,
                                        fromPirates ? 0.45f : 0.35f, 0.05f);
                ParticleManager.Play("Ashes", origin, vel.normalized);
            }
            catch (Exception e)
            {
                Diag.Debug("Muzzle effects failed: " + e.Message);
            }
        }

        private static void RemoveVisual(uint id)
        {
            Visual v;
            if (!Visuals.TryGetValue(id, out v)) return;
            Visuals.Remove(id);
            if (v.Go != null) UnityEngine.Object.Destroy(v.Go);
        }

        /// <summary>Animates visual balls. Runs on every machine, host included.</summary>
        internal static void ClientTick(float dt)
        {
            if (Visuals.Count == 0 || dt <= 0f) return;

            List<uint> expired = null;
            foreach (KeyValuePair<uint, Visual> kv in Visuals)
            {
                Visual v = kv.Value;
                v.Age += dt;
                v.Vel += Vector3.down * v.Gravity * dt;
                v.Pos += v.Vel * dt;

                if (v.Go != null)
                {
                    v.Go.transform.position = v.Pos;
                    v.Go.transform.Rotate(Vector3.right, 540f * dt, Space.Self);
                }

                // Safety net if an impact message never arrives.
                if (v.Age > MaxLifetime + 1f || v.Go == null)
                    (expired ??= new List<uint>()).Add(kv.Key);
            }

            if (expired != null)
                foreach (uint id in expired) RemoveVisual(id);
        }

        internal static void ClientClear()
        {
            foreach (Visual v in Visuals.Values)
                if (v.Go != null) UnityEngine.Object.Destroy(v.Go);
            Visuals.Clear();
        }

        // ------------------------------------------------------------------ helpers

        internal static void WriteVec(System.IO.BinaryWriter w, Vector3 v)
        {
            w.Write(v.x); w.Write(v.y); w.Write(v.z);
        }

        internal static Vector3 ReadVec(System.IO.BinaryReader r) =>
            new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        /// <summary>
        /// Launch velocity that lands on <paramref name="target"/> from <paramref name="origin"/>,
        /// preferring the flat low arc of a broadside.
        /// </summary>
        internal static Vector3 AimAt(Vector3 origin, Vector3 target, float speed, float gravity)
        {
            Vector3 flat = target - origin;
            float dy = flat.y;
            flat.y = 0f;
            float dx = flat.magnitude;
            Vector3 dir = dx > 1e-3f ? flat / dx : Vector3.forward;

            double angle;
            Ballistics.SolveLowArc(dx, dy, speed, gravity, out angle);

            return (dir * (float)Math.Cos(angle) + Vector3.up * (float)Math.Sin(angle)) * speed;
        }
    }
}
