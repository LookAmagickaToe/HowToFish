using System;
using System.Collections.Generic;
using Expanded.Pirates;
using FishNet;
using UnityEngine;

namespace Expanded.Megalodon
{
    internal enum HzKind : byte
    {
        FlyingFish = 1,
        Jellyfish = 2,
        Buoy = 3,
        FogBank = 4,
        Ramp = 5,
        Mine = 6
    }

    /// <summary>
    /// Everything else the sea throws at the wakeboarder. The host picks what and where and sends one
    /// small message; every client builds the same thing from the same seed. Whether it hits the
    /// rider is decided on the rider's machine, like the megalodon's bites.
    ///
    /// Barrel mines are the exception: they are the host's, because they explode for real.
    /// </summary>
    internal static class MegaHazards
    {
        private sealed class Hz
        {
            public ushort Id;
            public HzKind Kind;
            public Vector3 Pos, Dir;
            public int Seed;
            public int Count;
            public float Born, Life;
            public Transform Root;
            public readonly List<Transform> Parts = new List<Transform>();
            public readonly List<Vector3> Offsets = new List<Vector3>();
            public readonly List<Vector3> Vels = new List<Vector3>();
            public readonly List<float> Delays = new List<float>();
            public readonly List<bool> Spent = new List<bool>();
            public float LastTouch = -99f;
        }

        private static readonly Dictionary<ushort, Hz> Live = new Dictionary<ushort, Hz>();
        private static ushort _nextId = 1;

        // Host bookkeeping for mines (they explode on the host).
        private sealed class HostMine { public ushort Id; public Vector3 Pos; public float Born; }
        private static readonly List<HostMine> Mines = new List<HostMine>();
        private static readonly List<KeyValuePair<ushort, float>> HostExpiry = new List<KeyValuePair<ushort, float>>();
        private static float _nextFreeRamp;

        private const float FishGravity = 9.8f;

        // ------------------------------------------------------------------ networking

        internal static void RegisterHandlers()
        {
            ModNet.OnClient(Msg.HazardSpawn, r =>
            {
                var h = new Hz
                {
                    Kind = (HzKind)r.ReadByte(),
                    Id = r.ReadUInt16(),
                    Pos = Cannonballs.ReadVec(r),
                    Dir = Cannonballs.ReadVec(r),
                    Seed = r.ReadInt32(),
                    Count = r.ReadByte(),
                    Life = r.ReadSingle(),
                    Born = Time.time
                };
                Spawn(h);
            });

            ModNet.OnClient(Msg.HazardGone, r =>
            {
                ushort id = r.ReadUInt16();
                bool boom = r.ReadBoolean();
                Remove(id, boom);
            });
        }

        private static void Send(HzKind kind, Vector3 pos, Vector3 dir, int count, float life)
        {
            ushort id = _nextId++;
            if (_nextId == 0) _nextId = 1;
            int seed = UnityEngine.Random.Range(1, int.MaxValue);
            ModNet.SendToAll(Msg.HazardSpawn, w =>
            {
                w.Write((byte)kind);
                w.Write(id);
                Cannonballs.WriteVec(w, pos);
                Cannonballs.WriteVec(w, dir);
                w.Write(seed);
                w.Write((byte)Mathf.Clamp(count, 0, 255));
                w.Write(life);
            });
            if (kind == HzKind.Mine) Mines.Add(new HostMine { Id = id, Pos = pos, Born = Time.time });
            else HostExpiry.Add(new KeyValuePair<ushort, float>(id, Time.time + life));
        }

        private static void SendGone(ushort id, bool boom) => ModNet.SendToAll(Msg.HazardGone, w => { w.Write(id); w.Write(boom); });

        // ------------------------------------------------------------------ host: spawning

        internal static void HostSpawn(Hazard h, Vector3 riderPos, Vector3 riderVel)
        {
            if (!InstanceFinder.IsServerStarted) return;
            Vector3 travel = riderVel.magnitude > 2f ? new Vector3(riderVel.x, 0f, riderVel.z).normalized : Tow.Forward();
            Vector3 side = Vector3.Cross(Vector3.up, travel);
            float sign = UnityEngine.Random.value < 0.5f ? -1f : 1f;
            float speed = Mathf.Max(6f, riderVel.magnitude);
            Vector3 boat = Tow.Centre();
            Vector3 fwd = Tow.Forward();
            Vector3 boatSide = Vector3.Cross(Vector3.up, fwd);

            switch (h)
            {
                case Hazard.FlyingFish:
                    Send(HzKind.FlyingFish, Flat(riderPos + riderVel * 1.7f), side * sign, UnityEngine.Random.Range(9, 15), 5f);
                    MegaModule.Announce("Flying fish! Heads down!");
                    break;
                case Hazard.Jellyfish:
                    Send(HzKind.Jellyfish, Flat(boat + fwd * 60f + boatSide * UnityEngine.Random.Range(-6f, 6f)), fwd, 18, 50f);
                    MegaModule.Announce("Jellyfish ahead. Don't touch the pink ones. They're all pink.");
                    break;
                case Hazard.Buoy:
                    Send(HzKind.Buoy, Flat(boat + fwd * 55f + boatSide * sign * UnityEngine.Random.Range(3.5f, 7f)), fwd, 1, 60f);
                    break;
                case Hazard.Ramp:
                    Send(HzKind.Ramp, Flat(riderPos + travel * (speed * 3.2f) + side * UnityEngine.Random.Range(-3f, 3f)), travel, 1, 45f);
                    break;
                case Hazard.FogBank:
                    if (MegaModule.Cfg.FogBanks.Value)
                    {
                        Send(HzKind.FogBank, boat, fwd, 0, 28f);
                        MegaModule.Announce("A fog bank rolls in. You can't see it. It can see you.");
                    }
                    break;
            }
        }

        internal static void HostDropMine(Vector3 at)
        {
            Send(HzKind.Mine, Flat(at), Vector3.forward, 1, 45f);
        }

        private static Vector3 Flat(Vector3 p) { p.y = PirateModule.WaterY(); return p; }

        /// <summary>Host: expiries, mines near the megalodon, and a ramp now and then for free riding.</summary>
        internal static void HostTick()
        {
            float now = Time.time;
            for (int i = HostExpiry.Count - 1; i >= 0; i--)
                if (now >= HostExpiry[i].Value) HostExpiry.RemoveAt(i);

            if (Mines.Count > 0)
            {
                Vector3 tail, mouth; float r;
                bool shark = SharkBrain.Active && SharkBrain.Mode != SharkMode.Hidden && SharkBrain.Mode != SharkMode.Dead && SharkBrain.Mode != SharkMode.Leaving;
                SharkBrain.Capsule(out tail, out mouth, out r);
                Player rider = WakeRig.Rider;

                for (int i = Mines.Count - 1; i >= 0; i--)
                {
                    HostMine m = Mines[i];
                    if (now - m.Born > 45f) { Mines.RemoveAt(i); SendGone(m.Id, false); continue; }
                    if (now - m.Born < 1.2f) continue;   // arming

                    bool near = shark && DistanceToSegment(m.Pos, tail, mouth) < r + 3.5f;
                    bool riderHit = rider != null && Vector3.Distance(Flat(rider.Transform.position), m.Pos) < 1.3f;
                    if (!near && !riderHit) continue;

                    Mines.RemoveAt(i);
                    if (near && !riderHit && SharkBrain.Phase >= Phase.Two && UnityEngine.Random.value < 0.3f)
                    {
                        SendGone(m.Id, false);
                        SharkBrain.SwallowMine(m.Id, m.Pos);
                        continue;
                    }
                    SendGone(m.Id, true);
                    MegaBoom.Explode(m.Pos + Vector3.down * 0.3f, true, MegaModule.Cfg.MineMultiplier.Value, false);
                    if (riderHit && !near) MegaModule.Announce("The wakeboarder rode straight into a barrel mine. Tactical.");
                }
            }

            // Free riding with nothing chasing you: a ramp now and then, just because.
            Player r2 = WakeRig.Rider;
            if (!SharkBrain.Active && r2 != null && MegaModule.Cfg.Hazards.Value && Tow.Speed() > 6f)
            {
                if (_nextFreeRamp <= 0f) _nextFreeRamp = now + 20f;
                if (now >= _nextFreeRamp)
                {
                    _nextFreeRamp = now + UnityEngine.Random.Range(22f, 35f);
                    HostSpawn(Hazard.Ramp, r2.Transform.position, Tow.Velocity());
                }
            }
            else _nextFreeRamp = 0f;
        }

        internal static void HostClear()
        {
            foreach (HostMine m in Mines) SendGone(m.Id, false);
            Mines.Clear();
            HostExpiry.Clear();
        }

        private static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float t = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
            return Vector3.Distance(p, a + ab * t);
        }

        // ------------------------------------------------------------------ clients: building

        private static void Spawn(Hz h)
        {
            Remove(h.Id, false);
            if (h.Kind == HzKind.FogBank)
            {
                MegaFx.FogBank(h.Life);
                return;
            }

            h.Root = MegaShapes.Root("ExpandedHazard_" + h.Kind + "_" + h.Id);
            h.Root.position = h.Pos;
            var rng = new System.Random(h.Seed);

            switch (h.Kind)
            {
                case HzKind.FlyingFish:
                {
                    Vector3 across = h.Dir.normalized;
                    Vector3 along = Vector3.Cross(across, Vector3.up);
                    for (int i = 0; i < h.Count; i++)
                    {
                        Transform f = MegaShapes.FlyingFish(h.Root);
                        f.gameObject.SetActive(false);
                        Vector3 start = h.Pos - across * Range(rng, 8f, 12f) + along * Range(rng, -6f, 6f);
                        Vector3 vel = across * Range(rng, 9f, 13f) + Vector3.up * Range(rng, 4.5f, 6.5f);
                        h.Parts.Add(f);
                        h.Offsets.Add(start);
                        h.Vels.Add(vel);
                        h.Delays.Add(Range(rng, 0f, 1.8f));
                        h.Spent.Add(false);
                    }
                    break;
                }
                case HzKind.Jellyfish:
                {
                    for (int i = 0; i < h.Count; i++)
                    {
                        float a = Range(rng, 0f, Mathf.PI * 2f);
                        float d = Mathf.Sqrt(Range(rng, 0f, 1f)) * 16f;
                        Vector3 off = new Vector3(Mathf.Cos(a) * d, 0f, Mathf.Sin(a) * d);
                        Transform j = MegaShapes.Jellyfish(h.Root, Range(rng, 0.8f, 1.3f));
                        j.localPosition = off;
                        h.Parts.Add(j);
                        h.Offsets.Add(h.Pos + off);
                        h.Delays.Add(Range(rng, 0f, 6f));
                        h.Spent.Add(false);
                    }
                    break;
                }
                case HzKind.Buoy:
                    h.Parts.Add(MegaShapes.Buoy(h.Root));
                    break;
                case HzKind.Ramp:
                {
                    Transform ramp = MegaShapes.Ramp(h.Root);
                    Vector3 d = new Vector3(h.Dir.x, 0f, h.Dir.z);
                    ramp.rotation = Quaternion.LookRotation(d.sqrMagnitude > 1e-3f ? d.normalized : Vector3.forward);
                    h.Parts.Add(ramp);
                    break;
                }
                case HzKind.Mine:
                    h.Parts.Add(MegaShapes.Mine(h.Root));
                    try { ParticleManager.Play("WaterSplash", h.Pos); } catch { }
                    break;
            }
            Live[h.Id] = h;
        }

        private static float Range(System.Random r, float a, float b) => a + (float)r.NextDouble() * (b - a);

        private static void Remove(ushort id, bool boom)
        {
            Hz h;
            if (!Live.TryGetValue(id, out h)) return;
            Live.Remove(id);
            if (h.Root != null) UnityEngine.Object.Destroy(h.Root.gameObject);
        }

        internal static void ClientClear()
        {
            foreach (Hz h in Live.Values) if (h.Root != null) UnityEngine.Object.Destroy(h.Root.gameObject);
            Live.Clear();
        }

        // ------------------------------------------------------------------ clients: animating

        private static readonly List<ushort> Expired = new List<ushort>();

        internal static void LateTick()
        {
            if (Live.Count == 0) return;
            float now = Time.time;
            Expired.Clear();

            foreach (Hz h in Live.Values)
            {
                float age = now - h.Born;
                if (age > h.Life + 0.5f || h.Root == null) { Expired.Add(h.Id); continue; }

                switch (h.Kind)
                {
                    case HzKind.FlyingFish:
                        for (int i = 0; i < h.Parts.Count; i++)
                        {
                            Transform f = h.Parts[i];
                            float t = age - h.Delays[i];
                            if (t < 0f || h.Spent[i]) { if (f.gameObject.activeSelf && h.Spent[i]) f.gameObject.SetActive(false); continue; }
                            Vector3 p = FishAt(h, i, t);
                            if (p.y < Tow.Water(p) - 0.2f && t > 0.2f)
                            {
                                h.Spent[i] = true;
                                f.gameObject.SetActive(false);
                                continue;
                            }
                            if (!f.gameObject.activeSelf)
                            {
                                f.gameObject.SetActive(true);
                                if (i % 3 == 0) Wakeboard.AudioPlay("ItemHitWaterLight_V", 1, 3, p, 0.5f);
                            }
                            Vector3 v = h.Vels[i] + Vector3.down * FishGravity * t;
                            f.position = p;
                            f.rotation = Quaternion.LookRotation(v) * Quaternion.Euler(0f, 0f, Mathf.Sin(now * 30f + i) * 20f);
                        }
                        break;
                    case HzKind.Jellyfish:
                        for (int i = 0; i < h.Parts.Count; i++)
                        {
                            Vector3 o = h.Offsets[i];
                            float pulse = Mathf.Sin(now * 2.2f + h.Delays[i]);
                            h.Parts[i].position = new Vector3(o.x, Tow.Water(o) - 0.12f + pulse * 0.05f, o.z);
                            h.Parts[i].localScale = new Vector3(1f + pulse * 0.08f, 1f - pulse * 0.1f, 1f + pulse * 0.08f);
                        }
                        break;
                    default:
                    {
                        // Bob on the swell.
                        Vector3 p = h.Pos;
                        p.y = Tow.Water(p) + (h.Kind == HzKind.Buoy ? -0.25f : h.Kind == HzKind.Mine ? -0.35f : -0.2f);
                        h.Root.position = p;
                        if (h.Kind == HzKind.Buoy || h.Kind == HzKind.Mine)
                            h.Root.rotation = Quaternion.Euler(Mathf.Sin(now * 1.3f + h.Id) * 6f, h.Id * 37f, Mathf.Cos(now * 1.1f + h.Id) * 6f);
                        if (h.Kind == HzKind.Mine && h.Parts.Count > 0)
                        {
                            Transform blink = h.Parts[0].Find("Blink");
                            if (blink != null) blink.gameObject.SetActive(Mathf.Repeat(now * (age > 30f ? 5f : 2f), 1f) < 0.5f);
                        }
                        break;
                    }
                }
            }

            foreach (ushort id in Expired) Remove(id, false);
        }

        private static Vector3 FishAt(Hz h, int i, float t) => h.Offsets[i] + h.Vels[i] * t + Vector3.down * (0.5f * FishGravity * t * t);

        // ------------------------------------------------------------------ the rider (local)

        /// <summary>Called by the rider's simulation every frame with where they are about to be.</summary>
        internal static void CheckRider(Vector3 feet, Vector3 body, float dt)
        {
            if (Live.Count == 0 || !Wakeboard.Riding || Wakeboard.InRodeo) return;
            float now = Time.time;
            Vector3 chest = body + Vector3.up * 0.4f;

            foreach (Hz h in Live.Values)
            {
                if (h.Root == null) continue;   // torn down with the scene; LateTick will drop it
                float age = now - h.Born;
                switch (h.Kind)
                {
                    case HzKind.FlyingFish:
                        for (int i = 0; i < h.Parts.Count; i++)
                        {
                            float t = age - h.Delays[i];
                            if (t < 0f || h.Spent[i] || h.Parts[i] == null) continue;
                            Vector3 p = FishAt(h, i, t);
                            if ((p - chest).sqrMagnitude > 0.85f * 0.85f && (p - body).sqrMagnitude > 0.7f * 0.7f) continue;
                            h.Spent[i] = true;
                            h.Parts[i].gameObject.SetActive(false);
                            FishSlap(h.Vels[i], p);
                        }
                        break;

                    case HzKind.Jellyfish:
                        if (Wakeboard.Airborne && Wakeboard.Height > 0.4f) break;
                        if (now - h.LastTouch < 1.5f) break;
                        for (int i = 0; i < h.Offsets.Count; i++)
                        {
                            Vector3 o = h.Offsets[i];
                            if (new Vector2(o.x - feet.x, o.z - feet.z).sqrMagnitude > 0.95f * 0.95f) continue;
                            h.LastTouch = now;
                            Zap(o);
                            break;
                        }
                        break;

                    case HzKind.Buoy:
                    {
                        if (now - h.LastTouch < 3f) break;
                        Vector3 b = h.Pos;
                        float board = new Vector2(b.x - feet.x, b.z - feet.z).magnitude;
                        if (board < 0.85f && !(Wakeboard.Airborne && Wakeboard.Height > 1f))
                        {
                            h.LastTouch = now;
                            Wakeboard.Stun(1.2f, true);
                            Wakeboard.Knock((new Vector3(feet.x - b.x, 0f, feet.z - b.z)).normalized * 4f);
                            MegaFx.Shake(1400f, 2);
                            Wakeboard.AudioPlay("TakeDamage_0", 1, 5, feet, 0.8f);
                            Shouts.Yell(MegaLines.Pick(MegaLines.Wipeout));
                            break;
                        }
                        // The rope catching the buoy: you swing round it like a tetherball.
                        Vector3 tow;
                        if (Wakeboard.Wrapping || !Tow.Point(out tow)) break;
                        Vector2 a2 = new Vector2(tow.x, tow.z), p2 = new Vector2(feet.x, feet.z), c2 = new Vector2(b.x, b.z);
                        Vector2 ab = p2 - a2;
                        float len2 = ab.sqrMagnitude;
                        if (len2 < 1f) break;
                        float s = Vector2.Dot(c2 - a2, ab) / len2;
                        if (s < 0.25f || s > 0.9f) break;
                        if ((a2 + ab * s - c2).magnitude > 0.6f) break;
                        Vector3 v = Wakeboard.FlatVelocity;
                        float cross = ab.x * v.z - ab.y * v.x;
                        h.LastTouch = now;
                        Wakeboard.StartWrap(b, cross >= 0f ? -1f : 1f);
                        break;
                    }

                    case HzKind.Ramp:
                    {
                        if (now - h.LastTouch < 2f || Wakeboard.Airborne) break;
                        Vector3 local = Quaternion.Inverse(h.Parts.Count > 0 ? h.Parts[0].rotation : Quaternion.identity) * (feet - h.Pos);
                        if (Mathf.Abs(local.x) > 1.15f || local.z < -1.7f || local.z > 1.7f) break;
                        float along = Vector3.Dot(Wakeboard.FlatVelocity, h.Dir.normalized);
                        if (along < 3f) break;
                        h.LastTouch = now;
                        Wakeboard.Launch(6.5f + along * 0.3f, MegaLines.Pick(MegaLines.Ramp));
                        MegaFx.Banner("SEND IT!", new Color(1f, 0.85f, 0.3f), 0.9f);
                        Wakeboard.AudioPlay("ItemHitWaterHeavy_V", 1, 3, feet, 0.7f);
                        ModSave.AddCounter("wake.ramps", 1);
                        break;
                    }
                }
            }
        }

        private static float _lastFishYell;

        private static void FishSlap(Vector3 fishVel, Vector3 at)
        {
            Vector3 push = new Vector3(fishVel.x, 0f, fishVel.z).normalized * 3f;
            Wakeboard.Knock(push);
            MegaFx.Shake(700f, 1);
            try { AudioManager.PlayClipAt("FishPunchHit", at, true, AudioDistance.Short, 1f, 0.02f); } catch { }
            try { ParticleManager.Play("WaterSplash", at); } catch { }
            ModSave.AddCounter("wake.fishslaps", 1);

            if (UnityEngine.Random.value < 0.3f)
            {
                MegaFx.FishOnFace(2.2f);
                Shouts.Yell(MegaLines.Pick(MegaLines.FishFace));
            }
            else if (Time.time - _lastFishYell > 3f)
            {
                _lastFishYell = Time.time;
                MegaFx.Banner("PATSCH!", new Color(0.7f, 0.85f, 1f), 0.6f);
            }
        }

        private static void Zap(Vector3 at)
        {
            Wakeboard.Stun(1f, false);
            MegaFx.FlashScreen(new Color(1f, 1f, 1f, 0.7f), 0.35f);
            MegaFx.Shake(1600f, 3);
            MegaFx.Banner("BZZZT", new Color(1f, 0.5f, 0.9f), 0.8f);
            Shouts.Yell(MegaLines.Pick(MegaLines.Jelly));
            try { AudioManager.PlayClipAt("Ignite", at, true, AudioDistance.Short, 1f, 0.05f); } catch { }
            try
            {
                Player me = Player.LocalPlayer;
                if (me != null) Server.Instance.HitPlayer(me, 4, Vector3.zero, at, (byte)DamageType.NoEffect, null);
            }
            catch { }
            ModSave.AddCounter("wake.jellies", 1);
        }
    }
}
