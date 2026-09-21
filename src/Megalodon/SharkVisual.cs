using System;
using System.Collections.Generic;
using Expanded.Pirates;
using UnityEngine;

namespace Expanded.Megalodon
{
    /// <summary>
    /// The megalodon as every player sees it: the model, its shadow and bubbles before a strike, the
    /// splashes, the teeth raining down when it dies. Positions come from the host's pose stream;
    /// scripted moves are played locally from the same <see cref="SharkEvent"/> numbers.
    ///
    /// It also answers the question that matters most, on the only machine that can answer it
    /// fairly: did the jaws just close on ME?
    /// </summary>
    internal static class SharkVisual
    {
        // Status as the host last reported it.
        internal static bool Active { get; private set; }
        internal static float Hp { get; private set; }
        internal static float MaxHp { get; private set; } = 1f;
        internal static Phase Phase { get; private set; } = Phase.One;
        internal static SharkMode Mode { get; private set; } = SharkMode.Gone;

        private static MegaConfig Cfg => MegaModule.Cfg;
        private static float Length => Cfg.Length.Value;
        private static float MouthOffset => Length * 0.42f;

        // Pose stream.
        private static Vector3 _sPos, _sVel;
        private static float _sYaw, _sTime;
        private static byte _sFlags;
        private static bool _haveStream;

        // Rendered state.
        private static Transform _root, _shadow;
        private static GameObject _model;
        private static Animation _anim;
        private static string _clip;
        private static float _halfHeight = 1.1f;
        private static Vector3 _pos, _lastPos, _vel;
        private static Quaternion _rot = Quaternion.identity;
        private static bool _hidden;

        // The scripted move being played.
        private static SharkEvent _ev;
        private static bool _evStruck, _evSnapped, _evEnded, _evNearDone, _evBitMe;
        private static float _evMinDist;
        private static float _nextBubble;

        private sealed class Tooth { public Transform T; public Vector3 V; public Vector3 Spin; }
        private static readonly List<Tooth> Teeth = new List<Tooth>();

        internal static bool Present => Active && _root != null && Mode != SharkMode.Gone;
        internal static bool Rodeo => (_sFlags & 1) != 0;

        // ------------------------------------------------------------------ networking

        internal static void RegisterHandlers()
        {
            ModNet.OnClient(Msg.MegaPose, r =>
            {
                _sPos = Cannonballs.ReadVec(r);
                _sYaw = r.ReadSingle();
                _sVel = Cannonballs.ReadVec(r);
                Mode = (SharkMode)r.ReadByte();
                _sFlags = r.ReadByte();
                _sTime = Time.time;
                if (!_haveStream) { _pos = _sPos; _rot = Quaternion.Euler(0f, _sYaw, 0f); }
                _haveStream = true;
            });

            ModNet.OnClient(Msg.MegaStatus, r =>
            {
                bool active = r.ReadBoolean();
                Hp = r.ReadSingle();
                MaxHp = Mathf.Max(1f, r.ReadSingle());
                Phase = (Phase)r.ReadByte();
                Mode = (SharkMode)r.ReadByte();
                MegaBoat.ReadStatus(r);
                if (active != Active)
                {
                    Active = active;
                    if (!active) Clear();
                }
            });

            ModNet.OnClient(Msg.MegaEvent, r => OnEvent(SharkEvent.Read(r)));
        }

        // ------------------------------------------------------------------ events

        private static void OnEvent(SharkEvent ev)
        {
            ev.Start = Time.time;
            Player me = Player.LocalPlayer;

            switch (ev.Kind)
            {
                case EvKind.Appear:
                    Active = true;
                    MegaFx.FightOn = true;
                    MegaFx.Boom(1f);
                    MegaFx.Banner("...a fin.", new Color(0.8f, 0.9f, 1f), 2.5f);
                    return;
                case EvKind.Phase:
                    MegaFx.Banner(ev.Extra == 2 ? "IT'S ANGRY." : "IT'S FURIOUS.", new Color(1f, 0.35f, 0.25f), 2f);
                    MegaFx.Boom(0.7f);
                    try { if (_root != null) VFXManager.Play("Angry", _root.position + Vector3.up * 3f); } catch { }
                    return;
                case EvKind.Finale:
                    MegaFx.Banner("STOPF IHM DAS MAUL!", new Color(1f, 0.85f, 0.2f), 3.5f);
                    return;
                case EvKind.Leave:
                    MegaFx.FightOn = false;
                    MegaFx.Banner("...it's gone. For now.", new Color(0.8f, 0.9f, 1f), 2.5f);
                    return;
                case EvKind.Alongside:
                    if (me != null && me.OwnerId == ev.Extra && Wakeboard.Riding)
                        MegaFx.Banner("IT'S RIGHT BESIDE YOU!\n[SPACE] JUMP ON IT!", new Color(1f, 0.85f, 0.3f), 3.5f);
                    return;
                case EvKind.RodeoStart:
                    if (me != null && me.OwnerId == ev.Extra) Wakeboard.ConfirmRodeo();
                    return;
                case EvKind.RodeoEnd:
                    return;
                case EvKind.Swallow:
                    Wakeboard.AudioPlay("ItemHitWaterHeavy_V", 1, 3, ev.A, 0.8f);
                    try { AudioManager.PlayClipAt("Swallow", ev.A, true, AudioDistance.Long, 1f, 0.05f); } catch { }
                    return;
                case EvKind.Burp:
                    Wakeboard.AudioPlay("WhaleBurp_0", 1, 3, ev.A, 1f);
                    try { ParticleManager.Play("Spit", ev.A, (ev.B - ev.A).normalized); } catch { }
                    return;
                case EvKind.Eaten:
                {
                    Player victim = MegaModule.PlayerByOwner(ev.Extra);
                    if (me != null && me.OwnerId == ev.Extra) { MegaStomach.Begin(); return; }
                    string name = victim != null ? victim.SteamName : "SOMEONE";
                    MegaFx.Banner(name.ToUpperInvariant() + " GOT EATEN", new Color(1f, 0.3f, 0.25f), 3f);
                    if (victim != null)
                    {
                        try { AudioManager.PlayClipAt("Swallow", victim.Transform.position, true, AudioDistance.Long, 1f, 0.05f); } catch { }
                        try { ParticleManager.Play("Blood", victim.Transform.position, Vector3.up); } catch { }
                    }
                    return;
                }
                case EvKind.Bitten:
                {
                    if (me != null && me.OwnerId == ev.Extra) return;   // the victim did their own effects
                    Player victim = MegaModule.PlayerByOwner(ev.Extra);
                    if (victim == null) return;
                    try { ParticleManager.Play("Blood", victim.Transform.position, Vector3.up); } catch { }
                    Wakeboard.AudioPlay("FishBite_V", 1, 2, victim.Transform.position, 1f);
                    return;
                }
                case EvKind.Death:
                    StartDeath(ev);
                    break;
                case EvKind.PlayDead:
                    if (_root != null) Shouts.Say(_root, Vector3.up * 3.5f, "R.I.P.\n(definitely dead)", 5f);
                    break;
                case EvKind.Emerge:
                    if (Boat.IsDrivingLocally) Shouts.Yell(MegaLines.FakeOut[2]);
                    break;
            }

            if (!ev.OverridesPose) return;
            _ev = ev;
            _evStruck = _evSnapped = _evEnded = _evNearDone = _evBitMe = false;
            _evMinDist = float.MaxValue;
            _nextBubble = 0f;

            // The rider sees it coming. The rider says so.
            if (ev.Kind == EvKind.Lunge && Wakeboard.Riding && ev.Telegraph > 0.5f && UnityEngine.Random.value < 0.55f)
                Shouts.Yell(MegaLines.Pick(MegaLines.Telegraph));
        }

        // ------------------------------------------------------------------ per frame

        internal static void LateTick()
        {
            TickTeeth();
            if (!Active) { MegaFx.FightOn = false; MegaFx.Threat = 0f; return; }
            if (_root == null) Build();
            if (_root == null) return;

            float dt = Mathf.Max(1e-4f, Time.deltaTime);
            float now = Time.time;
            bool submerged = false;
            _hidden = false;

            if (_ev != null && now - _ev.Start <= _ev.Total + (_ev.Kind == EvKind.Death ? 30f : 0f) &&
                !(_ev.Kind == EvKind.PlayDead && Mode != SharkMode.PlayDead && now - _ev.Start > 0.5f))
            {
                float t = now - _ev.Start;
                if (_ev.Target >= 0)
                {
                    Player tp = MegaModule.PlayerByOwner(_ev.Target);
                    if (tp != null)
                    {
                        Vector3 tpos = tp == Player.LocalPlayer && Wakeboard.Riding ? PlayerHold.Pose : tp.Transform.position;
                        _ev.Track(t, dt, tpos, Tow.Velocity());
                    }
                }
                Vector3 body; Quaternion rot;
                _ev.Pose(t, MouthOffset, out body, out rot, out submerged);
                _pos = body;
                _rot = rot;
                PlayEvent(t);
            }
            else
            {
                if (_ev != null) { EndEvent(); _ev = null; }
                float age = Mathf.Min(0.35f, now - _sTime);
                Vector3 target = _sPos + _sVel * age;
                float k = 1f - Mathf.Exp(-9f * dt);
                _pos = Vector3.Lerp(_pos, target, k);

                float roll = Mathf.Sin(now * 2.1f) * 4f;
                if (Rodeo) roll = Mathf.Sin(now * 11f) * 28f;
                Quaternion want = Quaternion.Euler(0f, _sYaw, roll);
                if (Mode == SharkMode.Finale) want = Quaternion.Euler(-12f + Mathf.Sin(now * 1.7f) * 4f, _sYaw, 0f);
                _rot = Quaternion.Slerp(_rot, want, k);
                _hidden = Mode == SharkMode.Hidden;
            }

            _vel = Vector3.Lerp(_vel, (_pos - _lastPos) / dt, 1f - Mathf.Exp(-6f * dt));
            _lastPos = _pos;

            _root.position = _pos;
            _root.rotation = _rot;
            if (_model != null && _model.activeSelf == _hidden) _model.SetActive(!_hidden);

            // The shadow: all you see of it while it's deep.
            bool showShadow = !_hidden && (submerged || Mode == SharkMode.Stalk);
            if (_shadow != null)
            {
                if (_shadow.gameObject.activeSelf != showShadow) _shadow.gameObject.SetActive(showShadow);
                if (showShadow)
                {
                    Vector3 f = _rot * Vector3.forward;
                    f.y = 0f;
                    Vector3 sp = _pos;
                    sp.y = Tow.Water(_pos) + 0.04f;
                    _shadow.position = sp;
                    if (f.sqrMagnitude > 1e-3f) _shadow.rotation = Quaternion.LookRotation(f.normalized);
                }
            }

            ChooseAnimation();
            UpdateThreat();
        }

        private static void PlayEvent(float t)
        {
            SharkEvent ev = _ev;
            float now = Time.time;

            // Bubbles where it will come up.
            if (t < ev.Telegraph && now >= _nextBubble && ev.Kind != EvKind.PlayDead && ev.Kind != EvKind.Death)
            {
                _nextBubble = now + 0.18f;
                Vector3 b = ev.Kind == EvKind.TailSlap ? ev.A : ev.A;
                b += new Vector3(UnityEngine.Random.Range(-1.4f, 1.4f), 0f, UnityEngine.Random.Range(-1.4f, 1.4f));
                b.y = Tow.Water(b);
                try { ParticleManager.Play("WaterSplash", b); } catch { }
            }

            // Out it comes.
            if (!_evStruck && t >= ev.Telegraph && ev.Kind != EvKind.PlayDead && ev.Kind != EvKind.Death)
            {
                _evStruck = true;
                Vector3 at = ev.Kind == EvKind.TailSlap ? ev.A : ev.A;
                BigSplash(at, ev.Style == SharkEvent.StyleHuge ? 3 : 2);
                if (ev.Kind != EvKind.Dive) Play("Swim_Bite", false, 1.2f);
            }

            // The jaws shut.
            if (!_evSnapped && t >= ev.SnapTime && ev.Kind != EvKind.PlayDead && ev.Kind != EvKind.Death && ev.Kind != EvKind.Dive)
            {
                _evSnapped = true;
                OnSnapLocal(ev);
            }

            // Watching how close the jaws came, for the near miss.
            if (ev.BitesPeople && !_evNearDone && t >= ev.Telegraph)
            {
                Vector3 me;
                if (LocalTarget(out me))
                {
                    float d = Vector3.Distance(Mouth(), me + Vector3.up * 0.4f);
                    if (d < _evMinDist) _evMinDist = d;
                }
                if (t >= ev.SnapTime + 0.3f)
                {
                    _evNearDone = true;
                    if (!_evBitMe && _evMinDist < Cfg.NearMissRadius.Value && Wakeboard.Riding) Wakeboard.NearMiss(_evMinDist);
                }
            }

            // Back into the water.
            if (!_evEnded && t >= ev.Total && ev.Kind != EvKind.PlayDead && ev.Kind != EvKind.Death)
            {
                _evEnded = true;
                BigSplash(ev.B, ev.Style == SharkEvent.StyleHuge ? 3 : 1);
            }
        }

        private static void EndEvent()
        {
            _evStruck = _evSnapped = _evEnded = true;
        }

        private static void OnSnapLocal(SharkEvent ev)
        {
            Vector3 mouth = ev.SnapPoint;
            Wakeboard.AudioPlay("FishBite_V", 1, 2, mouth, 1f);

            switch (ev.Kind)
            {
                case EvKind.Lunge:
                case EvKind.Chomp:
                {
                    Vector3 body;
                    if (!LocalTarget(out body)) break;
                    float feet = body.y - (Wakeboard.Riding ? Wakeboard.FeetOffset : 0.95f);
                    float head = body.y + 0.8f;
                    float flat = new Vector2(mouth.x - body.x, mouth.z - body.z).magnitude;
                    // Jaws coming down from above still get you - hence the generous margin upwards.
                    bool inHeight = mouth.y >= feet - 0.4f && mouth.y <= head + 1f;
                    if (flat < Cfg.BiteRadius.Value && inHeight)
                    {
                        _evBitMe = true;
                        Wakeboard.Bitten(mouth);
                    }
                    break;
                }
                case EvKind.TailSlap:
                {
                    BigSplash(ev.B, 3);
                    try { VFXManager.Play("WaterSplash", ev.B, Vector3.up); } catch { }
                    MegaFx.Shake(900f, 2);
                    if (!Wakeboard.Riding) break;
                    Vector3 me = PlayerHold.Pose;
                    Vector3 away = me - ev.B;
                    away.y = 0f;
                    float d = away.magnitude;
                    if (d < 10f)
                    {
                        Wakeboard.Launch(Mathf.Lerp(12f, 6f, d / 10f), "WAAAAAVE—");
                        Wakeboard.Knock(away.normalized * 6f);
                        MegaFx.Banner("TAIL SLAP!", new Color(0.6f, 0.9f, 1f), 1f);
                    }
                    break;
                }
                case EvKind.RopeBite:
                    if (Wakeboard.Riding)
                    {
                        MegaFx.Banner("IT BIT THE ROPE!", new Color(1f, 0.6f, 0.3f), 1.2f);
                        MegaFx.Shake(800f, 2);
                    }
                    break;
                case EvKind.JumpOver:
                {
                    try { AudioManager.PlayGlobalClip("EarthRumble", false, 0.9f, 0.1f, false); } catch { }
                    if (Vector3.Distance(Tow.Centre(), Local()) < 12f) MegaFx.Shake(2000f, 4);
                    break;
                }
                case EvKind.SternChomp:
                case EvKind.Emerge:
                    MegaFx.Shake(1500f, 3);
                    break;
                case EvKind.Decoy:
                    Wakeboard.AudioPlay("WhaleBurp_0", 1, 3, mouth, 1f);
                    break;
            }
        }

        /// <summary>Where the local player is, if they're somewhere the jaws can reach (in the water).</summary>
        private static bool LocalTarget(out Vector3 body)
        {
            body = Vector3.zero;
            Player me = Player.LocalPlayer;
            if (me == null || me.Dying.IsDead || MegaStomach.Active) return false;
            if (Wakeboard.Riding) { body = PlayerHold.Pose; return true; }
            if (Boat.IsDrivingLocally) return false;
            try { if (me.Movement.OnBoat) return false; } catch { }
            body = me.Transform.position;
            return body.y < Tow.Water(body) + 0.6f;   // swimming
        }

        private static Vector3 Local()
        {
            Player me = Player.LocalPlayer;
            return me != null ? me.Transform.position : Vector3.zero;
        }

        internal static Vector3 Mouth() => _pos + _rot * Vector3.forward * MouthOffset;

        private static void UpdateThreat()
        {
            bool fight = Active && Mode != SharkMode.Dead && Mode != SharkMode.Leaving && Mode != SharkMode.Gone;
            MegaFx.FightOn = fight;
            MegaFx.Hush = Mode == SharkMode.Hidden || Mode == SharkMode.PlayDead;
            if (!fight) { MegaFx.Threat = 0f; return; }

            Player rider = WakeRig.Rider;
            Vector3 target = rider != null ? (rider == Player.LocalPlayer && Wakeboard.Riding ? PlayerHold.Pose : rider.Transform.position) : Tow.Centre();
            Vector3 m = Mouth();
            float d = new Vector2(m.x - target.x, m.z - target.z).magnitude;
            float meter = MegaRules.BiteMeter(d, Cfg.BiteRadius.Value, Cfg.ChaseDistance.Value);
            if (_ev != null && _ev.BitesPeople) meter = Mathf.Max(meter, 0.85f);
            MegaFx.Threat = Mathf.Lerp(MegaFx.Threat, meter, 1f - Mathf.Exp(-4f * Time.deltaTime));
        }

        // ------------------------------------------------------------------ model

        private static void Build()
        {
            _root = MegaShapes.Root("ExpandedMegalodon");
            _root.position = _haveStream ? _sPos : Tow.Centre();
            _pos = _lastPos = _root.position;

            try
            {
                GameObject go = ModCharacters.Create("characters_shark", Vector3.zero, Quaternion.identity, null);
                if (go != null)
                {
                    FitShark(go);
                    go.transform.SetParent(_root, false);
                    _model = go;
                    _anim = go.GetComponentInChildren<Animation>(true);
                }
            }
            catch (Exception e)
            {
                Diag.Exception("SharkVisual.Build", e);
            }

            if (_model == null)
            {
                // No model bundle: a grey torpedo with a fin. It's still terrifying if you squint.
                Transform t = MegaShapes.Root("PlaceholderShark", _root);
                Color grey = new Color(0.42f, 0.47f, 0.52f);
                MegaShapes.Prim(PrimitiveType.Capsule, t, Vector3.zero, new Vector3(Length * 0.2f, Length * 0.5f, Length * 0.16f), grey, new Vector3(90f, 0f, 0f));
                MegaShapes.Prim(PrimitiveType.Cube, t, new Vector3(0f, Length * 0.12f, 0f), new Vector3(0.2f, Length * 0.16f, Length * 0.12f), grey, new Vector3(-30f, 0f, 0f));
                MegaShapes.Prim(PrimitiveType.Cube, t, new Vector3(0f, 0f, -Length * 0.48f), new Vector3(0.2f, Length * 0.22f, Length * 0.08f), grey);
                _model = t.gameObject;
                _halfHeight = Length * 0.08f;
            }

            _shadow = MegaShapes.Shadow(null, Length);
            _shadow.gameObject.SetActive(false);
            _clip = null;
            Diag.Info("SharkVisual: megalodon built (" + (_anim != null ? "animated" : "static") + ").");
        }

        /// <summary>
        /// Scales the kit shark to <see cref="Length"/>, centres it, and turns it so its nose points
        /// along +Z. The nose is found from the mesh itself: the tail end is the one with the tall fin.
        /// </summary>
        private static void FitShark(GameObject go)
        {
            // Keep whatever rotation the import gave it (FBX files often stand up with -90 on X),
            // and only turn it about the vertical.
            Quaternion import = go.transform.rotation;
            Bounds b = ModAssets.Measure(go);
            float yaw = b.size.x > b.size.z ? -90f : 0f;
            go.transform.rotation = Quaternion.Euler(0f, yaw, 0f) * import;
            if (NoseAtBack(go)) yaw += 180f;
            yaw += Cfg.YawOffset.Value;
            go.transform.rotation = Quaternion.Euler(0f, yaw, 0f) * import;

            b = ModAssets.Measure(go);
            float longest = Mathf.Max(b.size.x, b.size.z);
            if (longest > 1e-3f) go.transform.localScale *= Length / longest;
            b = ModAssets.Measure(go);
            go.transform.position -= b.center;
            _halfHeight = Mathf.Max(0.4f, b.extents.y);
        }

        private static bool NoseAtBack(GameObject go)
        {
            try
            {
                var pts = new List<Vector3>();
                foreach (SkinnedMeshRenderer smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    var m = new Mesh();
                    smr.BakeMesh(m);
                    Matrix4x4 w = smr.transform.localToWorldMatrix;
                    foreach (Vector3 v in m.vertices) pts.Add(w.MultiplyPoint3x4(v));
                    UnityEngine.Object.Destroy(m);
                }
                foreach (MeshFilter mf in go.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf.sharedMesh == null || !mf.sharedMesh.isReadable) continue;
                    Matrix4x4 w = mf.transform.localToWorldMatrix;
                    foreach (Vector3 v in mf.sharedMesh.vertices) pts.Add(w.MultiplyPoint3x4(v));
                }
                if (pts.Count < 20) return false;

                float minZ = float.MaxValue, maxZ = float.MinValue;
                foreach (Vector3 p in pts) { minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z); }
                float cut = (maxZ - minZ) * 0.18f;
                float frontLo = float.MaxValue, frontHi = float.MinValue, backLo = float.MaxValue, backHi = float.MinValue;
                foreach (Vector3 p in pts)
                {
                    if (p.z > maxZ - cut) { frontLo = Mathf.Min(frontLo, p.y); frontHi = Mathf.Max(frontHi, p.y); }
                    if (p.z < minZ + cut) { backLo = Mathf.Min(backLo, p.y); backHi = Mathf.Max(backHi, p.y); }
                }
                bool tailAtFront = (frontHi - frontLo) > (backHi - backLo) * 1.15f;
                Diag.Info("SharkVisual: tail fin is at the " + (tailAtFront ? "+Z" : "-Z") + " end of the model.");
                return tailAtFront;
            }
            catch (Exception e)
            {
                Diag.Debug("SharkVisual: nose detection failed (" + e.Message + ").");
                return false;
            }
        }

        private static void ChooseAnimation()
        {
            if (_anim == null) return;
            string want;
            float speed = 1f;
            if (_ev != null && _evStruck && !_evEnded && _ev.Kind != EvKind.Dive) return;   // bite in progress
            if (Mode == SharkMode.Finale) { want = "Swim_Bite"; speed = 0.35f; }
            else if (Mode == SharkMode.PlayDead || Mode == SharkMode.Dead) { want = "Swim"; speed = 0.15f; }
            else if (Rodeo) { want = "Swim_Fast"; speed = 2.2f; }
            else
            {
                float v = new Vector3(_vel.x, 0f, _vel.z).magnitude;
                want = v > 9f ? "Swim_Fast" : "Swim";
                speed = Mathf.Clamp(v / (v > 9f ? 14f : 7f), 0.5f, 1.8f);
            }
            if (want != _clip) Play(want, true, speed);
            else SetSpeed(speed);
        }

        private static void Play(string clip, bool loop, float speed)
        {
            if (_model == null) return;
            if (ModCharacters.Play(_model, clip, loop, 0.15f)) { _clip = clip; SetSpeed(speed); }
        }

        private static void SetSpeed(float speed)
        {
            if (_anim == null) return;
            foreach (AnimationState st in _anim) if (st != null) st.speed = speed;
        }

        private static void BigSplash(Vector3 at, int size)
        {
            at.y = Tow.Water(at);
            Wakeboard.AudioPlay("ItemHitWaterHeavy_V", 1, 3, at, 1f);
            for (int i = 0; i < size; i++)
            {
                Vector3 p = at + new Vector3(UnityEngine.Random.Range(-1.8f, 1.8f), 0f, UnityEngine.Random.Range(-1.8f, 1.8f));
                try { ParticleManager.Play("WaterSplash", p); } catch { }
            }
            if (size >= 3) { try { VFXManager.Play("WaterSplash", at, Vector3.up); } catch { } }
        }

        // ------------------------------------------------------------------ death

        private static void StartDeath(SharkEvent ev)
        {
            MegaFx.FightOn = false;
            MegaFx.Boom(1.2f);
            MegaFx.Banner("MEGALODON DOWN!", new Color(1f, 0.9f, 0.4f), 4f);
            Vector3 at = ev.A;
            try { AudioManager.PlayClipAt("LavaWhaleDeathExplosion", at, true, AudioDistance.Long, 1f, 0.05f); } catch { }
            try { ParticleManager.Play("Confetti", at + Vector3.up * 3f); } catch { }
            try { AudioManager.PlayClipAt("Confetti", at, true, AudioDistance.Long, 1f, 0.05f); } catch { }

            for (int i = 0; i < 26; i++)
            {
                Transform t = MegaShapes.Tooth(null);
                t.position = at + new Vector3(UnityEngine.Random.Range(-2f, 2f), UnityEngine.Random.Range(1f, 3f), UnityEngine.Random.Range(-2f, 2f));
                Vector3 v = new Vector3(UnityEngine.Random.Range(-7f, 7f), UnityEngine.Random.Range(8f, 16f), UnityEngine.Random.Range(-7f, 7f));
                // A few fly straight at the boat, for the souvenir shot.
                if (i % 6 == 0)
                {
                    Vector3 to = Tow.Centre() - at;
                    to.y = 0f;
                    v = to * 0.45f + Vector3.up * 13f;
                }
                Teeth.Add(new Tooth { T = t, V = v, Spin = UnityEngine.Random.insideUnitSphere * 720f });
            }
            ModSave.AddCounter("megalodon.teeth.rained", 26);
        }

        private static void TickTeeth()
        {
            if (Teeth.Count == 0) return;
            float dt = Time.deltaTime;
            for (int i = Teeth.Count - 1; i >= 0; i--)
            {
                Tooth t = Teeth[i];
                if (t.T == null) { Teeth.RemoveAt(i); continue; }
                t.V += Vector3.down * 12f * dt;
                t.T.position += t.V * dt;
                t.T.Rotate(t.Spin * dt, Space.Self);
                if (t.T.position.y < Tow.Water(t.T.position) - 0.3f)
                {
                    if (i % 3 == 0) Wakeboard.AudioPlay("ItemHitWaterLight_V", 1, 3, t.T.position, 0.5f);
                    UnityEngine.Object.Destroy(t.T.gameObject);
                    Teeth.RemoveAt(i);
                }
            }
        }

        // ------------------------------------------------------------------ queries

        /// <summary>The top of its back, for the rodeo.</summary>
        internal static bool BackPoint(out Vector3 point, out Vector3 velocity)
        {
            point = Vector3.zero;
            velocity = Vector3.zero;
            if (!Present || _hidden || Mode == SharkMode.Dead || Mode == SharkMode.Leaving) return false;
            point = _pos + _rot * (Vector3.up * (_halfHeight * 0.8f) + Vector3.back * 0.4f);
            velocity = _vel;
            return true;
        }

        /// <summary>True if the feet just landed on its back. <paramref name="generous"/> after a jump aimed at it.</summary>
        internal static bool CanRodeo(Vector3 feet, bool generous = false)
        {
            if (!Present || _hidden || (Mode != SharkMode.Chase && Mode != SharkMode.Stalk)) return false;
            if (_ev != null && !_evEnded) return false;
            Vector3 local = Quaternion.Inverse(_rot) * (feet - _pos);
            float wide = generous ? 2.8f : 1.5f;
            return Mathf.Abs(local.x) < wide && Mathf.Abs(local.z) < Length * (generous ? 0.42f : 0.3f) &&
                   local.y > (generous ? -1.8f : -0.6f) && local.y < _halfHeight + 2.5f;
        }

        /// <summary>Its back, if it is close enough to jump onto from where you are.</summary>
        internal static bool RodeoReach(Vector3 from, out Vector3 back, out Vector3 velocity)
        {
            back = velocity = Vector3.zero;
            if (Mode != SharkMode.Chase && Mode != SharkMode.Stalk) return false;
            if (_ev != null && !_evEnded) return false;
            if (!BackPoint(out back, out velocity)) return false;
            return new Vector2(back.x - from.x, back.z - from.z).magnitude < 7.5f;
        }

        /// <summary>Does a bullet's path this step pass through its body?</summary>
        internal static bool SegmentHits(Vector3 p0, Vector3 p1, float extra, out Vector3 hit)
        {
            hit = p1;
            if (!Present || _hidden || Mode == SharkMode.Dead) return false;
            Vector3 f = _rot * Vector3.forward;
            float half = Length * 0.46f;
            Vector3 a = _pos + f * half, b = _pos - f * half;
            float r = Length * 0.11f + extra;

            Vector3 c1, c2;
            ClosestPoints(p0, p1, a, b, out c1, out c2);
            if ((c1 - c2).sqrMagnitude > r * r) return false;
            hit = c1;
            return true;
        }

        private static void ClosestPoints(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2, out Vector3 c1, out Vector3 c2)
        {
            Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
            float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
            float s, t;
            if (a <= 1e-6f && e <= 1e-6f) { c1 = p1; c2 = p2; return; }
            if (a <= 1e-6f) { s = 0f; t = Mathf.Clamp01(f / e); }
            else
            {
                float c = Vector3.Dot(d1, r);
                if (e <= 1e-6f) { t = 0f; s = Mathf.Clamp01(-c / a); }
                else
                {
                    float b = Vector3.Dot(d1, d2);
                    float denom = a * e - b * b;
                    s = denom > 1e-6f ? Mathf.Clamp01((b * f - c * e) / denom) : 0f;
                    t = (b * s + f) / e;
                    if (t < 0f) { t = 0f; s = Mathf.Clamp01(-c / a); }
                    else if (t > 1f) { t = 1f; s = Mathf.Clamp01((b - c) / a); }
                }
            }
            c1 = p1 + d1 * s;
            c2 = p2 + d2 * t;
        }

        // ------------------------------------------------------------------ cleanup

        internal static void Clear()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root.gameObject);
            if (_shadow != null) UnityEngine.Object.Destroy(_shadow.gameObject);
            _root = _shadow = null;
            _model = null;
            _anim = null;
            _ev = null;
            _haveStream = false;
            Mode = SharkMode.Gone;
            MegaFx.FightOn = false;
            MegaFx.Threat = 0f;
            MegaFx.Hush = false;
        }

        internal static void Reset()
        {
            Clear();
            Active = false;
            foreach (Tooth t in Teeth) if (t.T != null) UnityEngine.Object.Destroy(t.T.gameObject);
            Teeth.Clear();
        }
    }
}
