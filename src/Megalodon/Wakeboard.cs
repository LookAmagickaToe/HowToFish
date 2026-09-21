using System;
using UnityEngine;

namespace Expanded.Megalodon
{
    /// <summary>
    /// The local player on the end of the tow rope. Runs only on the rider's own machine: their
    /// position goes out to everyone through the game's normal player sync, like walking does.
    ///
    /// The model is a taut rope, not a rail: the rider has their own velocity, the water drags it
    /// down, carving (A/D) pushes sideways, and the rope only ever pulls. So when the boat turns you
    /// swing out wide and whip back across the wake, and when it slows the rope goes slack and you
    /// sink back into the water - which is exactly what the megalodon is waiting for.
    /// </summary>
    internal static class Wakeboard
    {
        internal static bool Riding { get; private set; }
        internal static float FeetOffset { get; private set; } = 0.95f;
        internal static float Spin { get; private set; }
        internal static float EdgeAngle { get; private set; }
        internal static bool Wrapping { get; private set; }
        internal static Vector3 WrapPoint { get; private set; }
        internal static bool Airborne => _air;
        internal static bool Planing => _planing;
        internal static float Height => _h;
        internal static Vector3 FlatVelocity => _v;
        internal static bool InRodeo => Riding && Time.time < _rodeoUntil;
        internal static bool Stunned => Time.time < _stunUntil;

        private static MegaConfig Cfg => MegaModule.Cfg;

        private static Vector3 _p, _v;          // flat position and velocity (y unused)
        private static float _h, _vy;           // height above the water while airborne
        private static bool _air;
        private static float _airStart;
        private static bool _planing;
        private static float _slowFor;
        private static float _stunUntil;
        private static float _prevSide;
        private static float _lastCross = -99f;
        private static float _spinTarget;
        private static float _wrapRadius, _wrapTurned, _wrapSign;
        private static float _rodeoUntil = -1f;
        private static float _rodeoCooldown;
        private static bool _chargePlanted;
        private static float _lastBite = -99f;
        private static float _lastNearMiss = -99f;
        private static float _nextSlowYell;
        private static float _startedAt;

        // ------------------------------------------------------------------ start / stop

        internal static void Begin()
        {
            Player me = Player.LocalPlayer;
            Vector3 tow;
            if (me == null || Riding || !Tow.Point(out tow)) return;

            try { if (Expanded.Pirates.DeckCannon.Manning) Expanded.Pirates.DeckCannon.Dismount("grabbed the tow rope"); } catch { }

            FeetOffset = PlayerHold.FeetOffset();
            Vector3 back = -Tow.Forward();
            _p = tow + back * Mathf.Max(3f, WakeRig.Rope);
            _p.y = 0f;
            _v = Tow.Velocity();
            _h = 0f; _vy = 0f; _air = false;
            _planing = false; _slowFor = 0f; _stunUntil = 0f;
            _prevSide = 0f; _spinTarget = 0f; Spin = 0f; EdgeAngle = 0f;
            Wrapping = false; _rodeoUntil = -1f; _chargePlanted = false;
            _startedAt = Time.time;

            Riding = true;
            MegaPatches.NoteRideStart();
            PlayerHold.Begin("wakeboard", Step, PoseFor(_p, false, 0f), () => End("the ride broke", true));

            // Face the boat: that's where the rope is.
            try
            {
                Vector3 look = tow - _p;
                me.Camera.SetRot(Mathf.Atan2(look.x, look.z) * Mathf.Rad2Deg);
            }
            catch { }

            Splash(PoseFor(_p, false, 0f), true);
            Diag.Info("Wakeboard: riding, rope " + WakeRig.Rope.ToString("0.0") + " m, on " + WakeRig.Tier + ".");
        }

        /// <summary>
        /// Stops riding. <paramref name="tellHost"/> when the rider chose it (let go, hit rocks); not
        /// when the host already moved the rope on.
        /// </summary>
        internal static void End(string why, bool tellHost)
        {
            if (!Riding) return;
            Riding = false;
            Wrapping = false;
            _rodeoUntil = -1f;
            Vector3 fling = _v * 0.4f + Vector3.up * (_air ? Mathf.Max(0f, _vy) : 1.5f);
            PlayerHold.End(fling, why);
            try { Player.LocalPlayer?.Camera?.SetMoveValues(0f, 0f, 0f); } catch { }
            if (tellHost) ModNet.SendToServer(Msg.WakeRequest, w => { w.Write(false); w.Write(why ?? ""); });
            Diag.Info("Wakeboard: off the rope (" + why + ").");
        }

        internal static void LetGo(string why) => End(why, true);

        // ------------------------------------------------------------------ simulation

        private static Vector3 PoseFor(Vector3 flat, bool planing, float h)
        {
            float water = Tow.Water(flat);
            // Planing: standing on the water. Not planing: sat in it up to the chest, board on your
            // feet, waiting for the boat to pull you up - a deep-water start.
            float feetY = planing || h > 0f ? water + 0.06f + h : water - 0.55f;
            return new Vector3(flat.x, feetY + FeetOffset, flat.z);
        }

        private static Vector3 Step(float dt)
        {
            Player me = Player.LocalPlayer;
            Vector3 tow;
            if (me == null || me.Dying.IsDead || !Tow.Point(out tow))
            {
                End("lost the boat", true);
                return PlayerHold.Pose;
            }
            tow.y = 0f;

            MegaConfig c = Cfg;
            Board tier = WakeRig.Tier;
            float rope = Mathf.Max(3f, WakeRig.Rope);
            Vector3 boatV = Tow.Velocity();
            Vector3 fwd = Tow.Forward();
            Vector3 boatRight = Vector3.Cross(Vector3.up, fwd);
            bool blocked = me.BlockInputs;

            // Riding the megalodon itself.
            if (InRodeo)
            {
                Vector3 backPt, sharkV;
                if (SharkVisual.BackPoint(out backPt, out sharkV))
                {
                    _p = new Vector3(backPt.x, 0f, backPt.z);
                    _v = new Vector3(sharkV.x, 0f, sharkV.z);
                    _h = 0f; _air = false; _planing = true;
                    if (!blocked && !_chargePlanted && Input.GetKeyDown(KeyCode.F))
                    {
                        _chargePlanted = true;
                        ModNet.SendToServer(Msg.Rodeo, w => w.Write((byte)1));
                        MegaFx.Banner("CHARGE PLANTED - JUMP OFF!", new Color(1f, 0.6f, 0.2f), 2f);
                    }
                    if (!blocked && JumpPressed()) { EndRodeo(true); }
                    EdgeAngle = Mathf.Sin(Time.time * 9f) * 25f;
                    return new Vector3(backPt.x, backPt.y + FeetOffset + 0.05f, backPt.z);
                }
                EndRodeo(false);
            }
            else if (_rodeoUntil > 0f)
            {
                EndRodeo(false);
            }

            float input = blocked || Stunned ? 0f : Mathf.Clamp(me.Movement.Input.x, -1f, 1f);
            float wob = MegaRules.Wobble(tier);
            float noise = wob > 0f ? (Mathf.PerlinNoise(Time.time * 1.1f, 7.3f) - 0.5f) * 2f * wob : 0f;

            Vector3 r = _p - tow;
            float dist = r.magnitude;
            Vector3 u = dist > 1e-3f ? r / dist : -fwd;

            if (Wrapping)
            {
                StepWrap(dt);
            }
            else
            {
                // Carve: push sideways relative to the way you face (towards the boat).
                Vector3 right = Vector3.Cross(Vector3.up, -u);
                float carve = c.CarveAcceleration.Value * MegaRules.CarveMultiplier(tier) *
                              (_air ? 0.35f : 1f) * (_planing ? 1f : 0.15f);
                _v += right * (input + noise * 0.45f) * carve * dt;

                float drag = _air ? 0.05f : (_planing ? c.WaterDrag.Value : 3f);
                _v *= Mathf.Exp(-drag * dt);
                _p += _v * dt;

                // The rope only pulls: past its length, remove any speed away from the tow post.
                r = _p - tow;
                dist = r.magnitude;
                u = dist > 1e-3f ? r / dist : -fwd;
                if (dist > rope)
                {
                    _p = tow + u * rope;
                    dist = rope;
                    Vector3 rel = _v - boatV;
                    float radial = Vector3.Dot(rel, u);
                    if (radial > 0f) rel -= u * radial;
                    _v = boatV + rel;
                }

                // Can't swing up alongside the boat.
                Vector3 back = -fwd;
                float ang = Vector3.SignedAngle(back, u, Vector3.up);
                float max = Mathf.Clamp(c.MaxRopeAngle.Value, 20f, 88f);
                if (Mathf.Abs(ang) > max)
                {
                    Vector3 uc = Quaternion.AngleAxis(Mathf.Sign(ang) * max, Vector3.up) * back;
                    _p = tow + uc * dist;
                    Vector3 tOut = Vector3.Cross(Vector3.up, uc);
                    if (Vector3.Dot(tOut, back) > 0f) tOut = -tOut;
                    float o = Vector3.Dot(_v - boatV, tOut);
                    if (o > 0f) _v -= tOut * o;
                    u = uc;
                }
            }
            _p.y = 0f;

            // Planing or sinking.
            float speed = _v.magnitude;
            bool taut = dist >= rope - 0.35f;
            if (!_planing)
            {
                if (taut && Vector3.Dot(boatV, fwd) > c.SinkSpeed.Value + 0.8f)
                {
                    _planing = true;
                    _slowFor = 0f;
                    Splash(PoseFor(_p, true, 0f), false);
                }
            }
            else if (!_air)
            {
                if (speed < c.SinkSpeed.Value)
                {
                    _slowFor += dt;
                    if (_slowFor > c.SinkSeconds.Value)
                    {
                        _planing = false;
                        _slowFor = 0f;
                        Splash(PoseFor(_p, false, 0f), true);
                    }
                }
                else _slowFor = 0f;
            }

            // Too slow with a megalodon behind you: say so. Loudly.
            if (SharkVisual.Present && MegaFx.Threat > 0.7f && Time.time > _nextSlowYell && Tow.Speed() < MegaModule.SafeSpeed)
            {
                _nextSlowYell = Time.time + 6f;
                Shouts.Yell(MegaLines.Pick(MegaLines.TooSlow));
            }

            // Crossing the boat's wake gives a little pop - and a big jump if you time Space to it.
            float side = Mathf.Sign(Vector3.Dot(_p - tow, boatRight));
            bool jump = !blocked && !Stunned && JumpPressed();
            if (_prevSide != 0f && side != _prevSide && _planing && !_air)
            {
                _lastCross = Time.time;
                float lateral = Mathf.Abs(Vector3.Dot(_v - boatV, boatRight));
                if (!jump && lateral > 3.5f) Launch(2.2f, null);
            }
            _prevSide = side;

            if (jump && _planing && !_air)
            {
                bool wake = Time.time - _lastCross < 0.45f;
                float v = c.JumpSpeed.Value * MegaRules.JumpMultiplier(tier) * (wake ? 1.55f : 1f);
                Launch(v, null);
                if (wake) MegaFx.Banner("WAKE JUMP!", new Color(0.6f, 0.95f, 1f), 0.9f);
                AudioPlay("ItemHitWaterMedium_V", 1, 3, PoseFor(_p, true, 0f), 0.6f);
            }

            // Up and down.
            if (_air)
            {
                _vy -= c.Gravity.Value * dt;
                _h += _vy * dt;
                if (_h <= 0f && _vy < 0f) Land();
                if (Time.time - _airStart > 0.45f && _h > 1.3f && _spinTarget == 0f)
                    _spinTarget = input < 0f ? -360f : 360f;
            }
            else _h = 0f;
            Spin = Mathf.MoveTowards(Spin, _spinTarget, 600f * dt);

            Vector3 body = PoseFor(_p, _planing, _air ? Mathf.Max(0f, _h) : 0f);

            // The sea's opinions: fish, jellies, buoys, ramps.
            MegaHazards.CheckRider(body + Vector3.down * FeetOffset, body, dt);

            // Rocks don't move. You do.
            try
            {
                if (Physics.CheckSphere(body, 0.35f, GameInfo.LevelLayer, QueryTriggerInteraction.Ignore))
                {
                    Shouts.Yell(MegaLines.Pick(MegaLines.Wipeout));
                    MegaFx.Shake(900f, 2);
                    End("hit the rocks", true);
                    return body;
                }
            }
            catch { }

            // Lean into the carve.
            EdgeAngle = Mathf.Lerp(EdgeAngle, _planing ? -input * 24f + noise * 10f : 0f, dt * 8f);
            try { me.Camera.SetMoveValues(input * 1.2f + noise * 0.8f, _air ? _vy : 0f, 0f); } catch { }
            return body;
        }

        private static void StepWrap(float dt)
        {
            Vector3 centre = WrapPoint;
            centre.y = 0f;
            Vector3 rel = _p - centre;
            rel.y = 0f;
            float speed = Mathf.Max(6f, _v.magnitude);
            float omega = speed / Mathf.Max(1.2f, _wrapRadius) * Mathf.Rad2Deg;
            float step = omega * dt;
            rel = Quaternion.AngleAxis(_wrapSign * step, Vector3.up) * rel.normalized * _wrapRadius;
            _p = centre + rel;
            Vector3 tangent = Vector3.Cross(Vector3.up, rel.normalized) * _wrapSign;
            _v = tangent * speed * 1.015f;
            _wrapTurned += step;

            Vector3 tow;
            bool tooFar = Tow.Point(out tow) && Vector3.Distance(new Vector3(tow.x, 0f, tow.z), _p) > WakeRig.Rope * 1.8f;
            if (_wrapTurned >= 200f || tooFar)
            {
                Wrapping = false;
                _v *= 1.35f;
                Launch(3f, null);
                Shouts.Yell(MegaLines.Pick(MegaLines.Slingshot));
                MegaFx.Banner("SLINGSHOT!", new Color(1f, 0.9f, 0.3f), 1f);
            }
        }

        private static void Land()
        {
            bool big = Time.time - _airStart > 1f;
            _air = false;
            _h = 0f;
            _vy = 0f;
            Vector3 body = PoseFor(_p, true, 0f);
            Splash(body, big);

            if (Mathf.Abs(Spin) >= 300f) { MegaFx.Banner("360!", new Color(0.7f, 1f, 0.7f), 1f); ModSave.AddCounter("wake.spins", 1); }
            Spin = 0f;
            _spinTarget = 0f;

            // Landed on its back? Ask the host - it has the megalodon, so it decides.
            if (Time.time > _rodeoCooldown && SharkVisual.CanRodeo(body + Vector3.down * FeetOffset))
            {
                _rodeoCooldown = Time.time + 1.5f;
                ModNet.SendToServer(Msg.Rodeo, w => w.Write((byte)0));
            }
        }

        /// <summary>The host says yes: you're riding a megalodon now.</summary>
        internal static void ConfirmRodeo()
        {
            if (!Riding) return;
            _rodeoUntil = Time.time + 4.5f;
            _chargePlanted = false;
            Shouts.Yell(MegaLines.Pick(MegaLines.Rodeo));
            MegaFx.Banner("RODEO!  [F] plant a charge   [Space] jump off", new Color(1f, 0.85f, 0.3f), 2.5f);
            ModSave.AddCounter("megalodon.rodeos", 1);
        }

        private static void EndRodeo(bool jumped)
        {
            if (_rodeoUntil < 0f) return;
            _rodeoUntil = -1f;
            _rodeoCooldown = Time.time + 6f;
            Vector3 side = Vector3.Cross(Vector3.up, Tow.Forward()) * (UnityEngine.Random.value < 0.5f ? -1f : 1f);
            _v += side * (jumped ? 5f : 8f);
            Launch(jumped ? 8f : 9.5f, null);
            ModNet.SendToServer(Msg.Rodeo, w => w.Write((byte)2));
            if (!jumped) MegaFx.Banner("BUCKED OFF!", new Color(1f, 0.6f, 0.4f), 1.2f);
        }

        // ------------------------------------------------------------------ things done to the rider

        internal static void Launch(float upSpeed, string yell)
        {
            if (!Riding) return;
            if (!_air) { _air = true; _airStart = Time.time; _h = Mathf.Max(_h, 0.01f); _vy = 0f; }
            _vy = Mathf.Max(_vy, upSpeed);
            _planing = true;
            if (yell != null) Shouts.Yell(yell);
        }

        internal static void Knock(Vector3 dv)
        {
            if (!Riding) return;
            dv.y = 0f;
            _v += dv;
        }

        internal static void Stun(float seconds, bool sink)
        {
            if (!Riding) return;
            _stunUntil = Mathf.Max(_stunUntil, Time.time + seconds);
            if (sink && !_air) { _planing = false; _slowFor = 0f; }
        }

        internal static void StartWrap(Vector3 centre, float sign)
        {
            if (!Riding || Wrapping || _air) return;
            WrapPoint = centre;
            Vector3 rel = _p - new Vector3(centre.x, 0f, centre.z);
            rel.y = 0f;
            _wrapRadius = Mathf.Clamp(rel.magnitude, 1.5f, 6f);
            _wrapTurned = 0f;
            // Keep going the way you were going: orbit in the direction of your current speed.
            float along = Vector3.Dot(_v, Vector3.Cross(Vector3.up, rel.normalized));
            _wrapSign = Mathf.Abs(along) > 0.5f ? Mathf.Sign(along) : (sign >= 0f ? 1f : -1f);
            Wrapping = true;
            MegaFx.Banner("ROPE CAUGHT THE BUOY!", new Color(1f, 0.8f, 0.4f), 0.9f);
        }

        /// <summary>The jaws closed on you. Tell the host; it decides what you ride next.</summary>
        internal static void Bitten(Vector3 mouth)
        {
            if (Time.time - _lastBite < 1.2f) return;
            _lastBite = Time.time;
            Board next = MegaRules.Next(WakeRig.Tier);

            MegaFx.FlashScreen(new Color(0.8f, 0f, 0f, 0.55f), 0.6f);
            MegaFx.Shake(2500f, 4);
            MegaFx.Punch(-18f, 0.8f);
            Vector3 body = PlayerHold.Pose;
            try { ParticleManager.Play("Blood", body, (body - mouth).normalized); } catch { }
            AudioPlay("FishBite_V", 1, 2, body, 1f);

            if (Riding)
            {
                Vector3 away = body - mouth;
                away.y = 0f;
                Knock(away.normalized * 5f);
                Stun(0.5f, false);
                Launch(3f, null);
                Shouts.Yell(next == Board.Eaten ? MegaLines.Pick(MegaLines.Bitten, MegaLines.BittenClean) : MegaLines.NewBoard(next));
                MegaFx.Banner(next == Board.Eaten ? "CHOMP." : "BITTEN!  " + MegaRules.BoardName(next).ToUpperInvariant(),
                              new Color(1f, 0.3f, 0.25f), 1.6f);
            }
            ModSave.AddCounter("wake.bites", 1);
            ModNet.SendToServer(Msg.WakeBitten, w => w.Write(Riding));
        }

        internal static void NearMiss(float distance)
        {
            if (Time.time - _lastNearMiss < 1.5f || Time.time - _lastBite < 1.5f) return;
            _lastNearMiss = Time.time;
            MegaFx.Punch(-16f, 1.1f);
            MegaFx.Banner("KNAPP.", new Color(1f, 0.95f, 0.85f), 1.3f);
            MegaFx.Shake(1200f, 2);
            MegaFx.FlashScreen(new Color(1f, 1f, 1f, 0.25f), 0.25f);
            Shouts.Yell(MegaLines.Pick(MegaLines.NearMiss, MegaLines.NearMissClean));
            ModSave.AddCounter("wake.nearmisses", 1);
        }

        // ------------------------------------------------------------------ helpers

        private static bool JumpPressed()
        {
            return Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.JoystickButton0);
        }

        private static void Splash(Vector3 body, bool big)
        {
            Vector3 at = body + Vector3.down * FeetOffset;
            try { ParticleManager.Play("WaterSplash", at); } catch { }
            AudioPlay(big ? "ItemHitWaterHeavy_V" : "ItemHitWaterMedium_V", 1, 3, at, big ? 0.9f : 0.6f);
        }

        internal static void AudioPlay(string clip, int min, int max, Vector3 at, float volume)
        {
            try { AudioManager.PlayRandomClipAt(clip, min, max, at, true, AudioDistance.Medium, volume, 0.05f); }
            catch (Exception e) { Diag.Debug("Audio " + clip + ": " + e.Message); }
        }
    }
}
