using System;
using System.Collections.Generic;
using Expanded.Content;
using Expanded.Pirates;
using Expanded.Quests;
using FishNet;
using FishNet.Connection;
using UnityEngine;

namespace Expanded.Megalodon
{
    /// <summary>
    /// The megalodon's brain. Host only.
    ///
    /// It follows whoever is on the tow rope, keeps its distance while the boat is fast and creeps up
    /// when it's slow, and every few seconds picks a move from its current phase. Moves are sent as
    /// <see cref="SharkEvent"/>s and played identically everywhere; whether the jaws caught someone is
    /// decided on that someone's own machine (they know exactly where they are), then reported back.
    /// The boat is the host's, so hits on the boat are decided here.
    /// </summary>
    internal static class SharkBrain
    {
        internal static bool Active { get; private set; }
        internal static float Hp { get; private set; }
        internal static float MaxHp { get; private set; } = 1f;
        internal static Phase Phase { get; private set; } = Phase.One;
        internal static SharkMode Mode { get; private set; } = SharkMode.Gone;

        private static MegaConfig Cfg => MegaModule.Cfg;
        internal static float MouthOffset => Cfg.Length.Value * 0.42f;

        private static Vector3 _pos;                  // body centre
        private static Vector3 _heading = Vector3.forward;
        private static Vector3 _vel;
        private static SharkEvent _ev;
        private static bool _snapDone;
        private static Action _after;
        private static ushort _nextId = 1;

        private static float _modeUntil, _nextAttack, _nextHazard, _startedAt, _noRiderSince = -1f, _closeFor;
        private static float _lastFightEnded = -9999f, _rideOutFor, _triggerExtra;
        private static Attack _lastAttack;
        private static Hazard _lastHazard;
        private static bool _playDeadUsed;
        private static float _finaleNotBefore;
        private static float _nextChomp;
        private static float _nextPose, _nextStatus;
        private static bool _statusDirty;

        // Who it is after.
        private static Player _rider;
        private static Vector3 _riderPos, _riderVel, _lastSample;
        private static float _lastSampleTime = -1f;

        // Rodeo, burps, decoys.
        private static int _rodeoOwner = -1;
        private static float _rodeoUntil, _rodeoChargeAt = -1f;
        private static float _burpAt = -1f;
        private static float _nextDecoyScan, _decoyCooldown;
        private static Item _decoy;
        private static float _gunWindowStart, _gunWindowDamage;
        private static readonly Dictionary<int, float> LastBiteReport = new Dictionary<int, float>();

        // ------------------------------------------------------------------ handlers

        internal static void RegisterHandlers()
        {
            ModNet.OnServer(Msg.WakeBitten, (conn, r) =>
            {
                bool riding = r.ReadBoolean();
                Player p = MegaModule.PlayerFor(conn);
                if (p != null) HostBitten(p, riding);
            });

            ModNet.OnServer(Msg.MegaHit, (conn, r) =>
            {
                int damage = r.ReadInt32();
                Vector3 point = Cannonballs.ReadVec(r);
                HostGunHit(damage, point);
            });

            ModNet.OnServer(Msg.Rodeo, (conn, r) =>
            {
                byte what = r.ReadByte();
                Player p = MegaModule.PlayerFor(conn);
                if (p != null) HostRodeo(p, what);
            });

            ModNet.OnServer(Msg.Dentures, (conn, r) =>
            {
                Player p = MegaModule.PlayerFor(conn);
                if (p == null) return;
                QuestModule.Instance?.SetFlag(MegalodonStory.FlagTeethRecovered);
                ModSave.AddCounter("megalodon.dentures.grabbed", 1);
                MegaModule.Announce(p.SteamName + " came back out of the megalodon holding Old Salt's dentures. Sticky ones.");
            });

            ModNet.OnServer(Msg.Hello, (conn, r) =>
            {
                if (!Active) return;
                ModNet.SendTo(conn, Msg.MegaStatus, WriteStatus);
            });
        }

        // ------------------------------------------------------------------ host loop

        internal static void HostTick(float dt)
        {
            if (!Active) { TickTrigger(dt); return; }

            float now = Time.time;
            SampleRider();

            if (Mode != SharkMode.Dead && Mode != SharkMode.Leaving)
            {
                if (_rider == null)
                {
                    if (_noRiderSince < 0f) _noRiderSince = now;
                    if (now - _noRiderSince > (_debugSpawn ? 90f : 15f)) { Leave("it lost interest - nobody's dangling off the boat"); return; }
                }
                else _noRiderSince = -1f;

                if (Tow.Boat == null) { Leave("the boat is gone"); return; }
                if (!_debugSpawn && Tow.DistanceFromMooring() < 55f) { Leave("it won't follow you into the shallows"); return; }
                if (now - _startedAt > Cfg.MaxFightMinutes.Value * 60f) { Leave("it got bored"); return; }
            }

            TickTimers(now);

            switch (Mode)
            {
                case SharkMode.Stalk:
                    Follow(dt, 26f, 0.8f);
                    if (now >= _modeUntil) SetMode(SharkMode.Chase);
                    break;
                case SharkMode.Chase:
                    Chase(dt, now);
                    break;
                case SharkMode.Strike:
                    Advance(now);
                    break;
                case SharkMode.Hidden:
                    _pos += Tow.Velocity() * dt;
                    _pos.y = PirateModule.WaterY() - 8f;
                    if (now >= _modeUntil) Next();
                    break;
                case SharkMode.PlayDead:
                    if (_ev != null) Advance(now);
                    if (_rider != null && Flat(_riderPos - _pos).magnitude < 8f) { SetMode(SharkMode.Chase); Chomp(); break; }
                    if (now >= _modeUntil) Next();
                    break;
                case SharkMode.Finale:
                    HoldFinale(dt);
                    if (now >= _modeUntil) { _finaleNotBefore = now + 30f; SternChomp(); }
                    break;
                case SharkMode.Distracted:
                    ChaseDecoy(dt, now);
                    break;
                case SharkMode.Dead:
                    if (_ev != null) Advance(now);
                    if (now >= _modeUntil) Despawn("dead");
                    break;
                case SharkMode.Leaving:
                    _pos += (_heading * 10f + Vector3.down * 3f) * dt;
                    if (now >= _modeUntil) Despawn("left");
                    break;
            }

            if (now >= _nextPose) { _nextPose = now + 1f / 12f; BroadcastPose(); }
            if (_statusDirty || now >= _nextStatus) { _nextStatus = now + 1f; _statusDirty = false; BroadcastStatus(); }
        }

        private static void TickTrigger(float dt)
        {
            Player rider = WakeRig.Rider;
            bool eligible = rider != null && Tow.Boat != null &&
                            QuestModule.HighestIsland >= Cfg.FromIsland.Value &&
                            Time.time - _lastFightEnded > Cfg.CooldownMinutes.Value * 60f &&
                            Tow.DistanceFromMooring() > Cfg.TriggerDistance.Value &&
                            Tow.Speed() > 4f;
            if (!eligible) { _rideOutFor = 0f; return; }
            if (_rideOutFor == 0f) _triggerExtra = UnityEngine.Random.Range(0f, 8f);
            _rideOutFor += dt;
            if (_rideOutFor >= Cfg.TriggerRideSeconds.Value + _triggerExtra) Spawn();
        }

        internal static void Spawn()
        {
            if (Active || !InstanceFinder.IsServerStarted) return;
            _rider = WakeRig.Rider;
            SampleRider();

            Vector3 fwd = Tow.Forward();
            Vector3 origin = _rider != null ? _riderPos : Tow.Centre();
            Vector3 side = Vector3.Cross(Vector3.up, fwd) * UnityEngine.Random.Range(-10f, 10f);
            _pos = origin - fwd * 45f + side;
            _pos.y = PirateModule.WaterY() - 1.2f;
            _heading = fwd;
            _vel = Tow.Velocity();

            Active = true;
            MaxHp = Mathf.Max(1f, Cfg.Health.Value);
            Hp = MaxHp;
            Phase = Phase.One;
            _startedAt = Time.time;
            _nextAttack = Time.time + 10f;
            _nextHazard = Time.time + 16f;
            _lastAttack = Attack.None;
            _lastHazard = Hazard.None;
            _playDeadUsed = false;
            _finaleNotBefore = 0f;
            _noRiderSince = -1f;
            _rodeoOwner = -1;
            _rodeoChargeAt = -1f;
            _burpAt = -1f;
            _decoy = null;
            _ev = null;
            _debugSpawn = false;
            SetMode(SharkMode.Stalk);
            _modeUntil = Time.time + 7f;

            Send(new SharkEvent { Kind = EvKind.Appear, A = _pos });
            BroadcastStatus();
            ModSave.AddCounter("megalodon.fights", 1);
            MegaModule.Announce("A fin. Behind the wakeboarder. A BIG fin. Keep the boat FAST - it likes slow.");
            Diag.Info("Megalodon: spawned at " + _pos.ToString("F1") + ".");
        }

        // ------------------------------------------------------------------ movement

        private static void SampleRider()
        {
            Player p = WakeRig.Rider;
            _rider = p != null && !p.Dying.IsDead ? p : null;
            if (_rider == null) { _lastSampleTime = -1f; return; }

            Vector3 now = _rider.Transform.position;
            if (_lastSampleTime > 0f)
            {
                float dt = Time.time - _lastSampleTime;
                if (dt > 1e-3f)
                {
                    Vector3 v = (now - _lastSample) / dt;
                    v.y = 0f;
                    if (v.magnitude < 60f) _riderVel = Vector3.Lerp(_riderVel, v, Mathf.Clamp01(dt * 6f));
                }
            }
            _lastSample = now;
            _lastSampleTime = Time.time;
            _riderPos = now;
        }

        private static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

        private static Vector3 TravelDir()
        {
            Vector3 v = _riderVel;
            return v.magnitude > 2f ? v.normalized : Tow.Forward();
        }

        /// <summary>Swims towards a point <paramref name="behind"/> metres behind the rider's mouth-line.</summary>
        private static void Follow(float dt, float behind, float depthSign)
        {
            Vector3 dir = TravelDir();
            Vector3 anchor = _rider != null ? _riderPos : Tow.Centre();
            Vector3 side = Vector3.Cross(Vector3.up, dir);
            Vector3 target = anchor - dir * (behind + MouthOffset) + side * (Mathf.Sin(Time.time * 0.45f) * 3f);
            Steer(dt, target, _rider != null ? _riderVel : Tow.Velocity());
            float water = PirateModule.WaterY();
            // Stalking: only the fin cuts the surface. Chasing: half out of the water.
            _pos.y = Mathf.Lerp(_pos.y, water + (depthSign > 0.5f ? -1.3f : -Cfg.SwimDepth.Value), dt * 2f);
        }

        private static void Steer(float dt, Vector3 target, Vector3 carrierVel)
        {
            Vector3 to = Flat(target - _pos);
            Vector3 desired = carrierVel + to * 1.2f;
            float max = Mathf.Max(Cfg.MaxSpeed.Value, Tow.Speed() + 3f);
            desired = Vector3.ClampMagnitude(desired, max);
            _vel = Vector3.MoveTowards(_vel, desired, 18f * dt);
            _pos += _vel * dt;

            Vector3 face = Flat(_vel.sqrMagnitude > 1f ? _vel : to);
            if (face.sqrMagnitude > 1e-3f)
                _heading = Vector3.RotateTowards(_heading, face.normalized, 100f * Mathf.Deg2Rad * dt, 0f);
        }

        private static void Chase(float dt, float now)
        {
            if (_rodeoOwner >= 0)
            {
                // Someone is stood on its back, still holding the tow rope: it gets towed too.
                Vector3 tow;
                if (Tow.Point(out tow))
                {
                    Vector3 outward = Flat(_pos - tow);
                    Vector3 end = tow + (outward.sqrMagnitude > 1f ? outward.normalized : -Tow.Forward()) * WakeRig.Rope;
                    end.y = _pos.y;
                    Steer(dt, end, Tow.Velocity());
                    _pos.y = Mathf.Lerp(_pos.y, PirateModule.WaterY() - Cfg.SwimDepth.Value, dt * 2f);
                }
                return;
            }
            if (_rider != null && now < _alongsideUntil)
            {
                // Right beside the rider, close enough to jump on. Then a sideways snap.
                Vector3 dir = TravelDir();
                Vector3 side = Vector3.Cross(Vector3.up, dir) * _alongsideSide;
                Steer(dt, _riderPos + side * 3.4f - dir * (MouthOffset * 0.35f), _riderVel);
                _pos.y = Mathf.Lerp(_pos.y, PirateModule.WaterY() - Cfg.SwimDepth.Value, dt * 3f);
                if (now + dt >= _alongsideUntil) { _nextChomp = now + 2.6f; Chomp(); }
                return;
            }

            float want = MegaRules.PreferredDistance(Tow.Speed(), MegaModule.SafeSpeed, Cfg.ChaseDistance.Value, Cfg.CloseDistance.Value);
            Follow(dt, want, 0f);

            if (_rider == null) return;

            // A slow boat means it gets close. Close means CHOMP.
            float mouthDist = Flat(Mouth() - _riderPos).magnitude;
            if (_rodeoOwner < 0 && mouthDist < Cfg.CloseDistance.Value + 1.8f)
            {
                _closeFor += dt;
                if (_closeFor > 0.7f && now >= _nextChomp)
                {
                    _closeFor = 0f;
                    _nextChomp = now + 2.6f;
                    Chomp();
                    return;
                }
            }
            else _closeFor = 0f;

            if (_rodeoOwner < 0 && MaxHp > 0f && Hp / MaxHp <= 0.12f && now >= _finaleNotBefore) { StartFinale(); return; }

            if (now >= _nextDecoyScan) { _nextDecoyScan = now + 0.7f; if (TryDecoy(now)) return; }

            if (_rodeoOwner < 0 && now >= _nextAttack)
            {
                Attack a = MegaRules.PickAttack(Phase, UnityEngine.Random.value, _lastAttack, _playDeadUsed);
                _lastAttack = a;
                _nextAttack = now + MegaRules.AttackInterval(Phase) * UnityEngine.Random.Range(0.8f, 1.25f);
                Execute(a);
            }
        }

        private static void TickTimers(float now)
        {
            if (Cfg.Hazards.Value && now >= _nextHazard && Mode != SharkMode.Dead && Mode != SharkMode.Leaving && _rider != null)
            {
                Hazard h = MegaRules.PickHazard(Phase, UnityEngine.Random.value, _lastHazard);
                _lastHazard = h;
                _nextHazard = now + MegaRules.HazardInterval(Phase) * UnityEngine.Random.Range(0.8f, 1.3f);
                MegaHazards.HostSpawn(h, _riderPos, _riderVel);
            }

            if (_rodeoOwner >= 0 && now >= _rodeoUntil) EndRodeo();

            if (_rodeoChargeAt > 0f && now >= _rodeoChargeAt)
            {
                _rodeoChargeAt = -1f;
                Vector3 back = _pos + Vector3.up * 1.2f;
                MegaBoom.Explode(back, false, 0f, false);
                Damage(MaxHp * Cfg.RodeoChargeFraction.Value, "a charge planted on its back");
            }

            if (_burpAt > 0f && now >= _burpAt)
            {
                _burpAt = -1f;
                Vector3 mouth = Mouth() + Vector3.up * 1f;
                Vector3 target = Tow.Centre() + Tow.Velocity() * 1.2f;
                float g = Mathf.Max(0.1f, PirateModule.Cfg.CannonGravity.Value);
                if (PirateModule.Instance != null && PirateModule.Instance.IsEnabled)
                    Cannonballs.Fire(mouth, Cannonballs.AimAt(mouth, target, 28f, g), true);
                else
                    MegaBoom.Explode(target + Vector3.up * 0.5f, false, 0f, false);   // no cannonball flight without the Pirates module
                Send(new SharkEvent { Kind = EvKind.Burp, A = mouth, B = target });
            }
        }

        // ------------------------------------------------------------------ moves

        private static void Execute(Attack a)
        {
            switch (a)
            {
                case Attack.BreachLunge: StrikeAtRider(SharkEvent.StyleBreach, 0f); break;
                case Attack.SkimLunge: StrikeAtRider(SharkEvent.StyleSkim, 0f); break;
                case Attack.FakeOut: FakeOut(); break;
                case Attack.RopeBite: RopeBite(); break;
                case Attack.TailSlap: TailSlap(); break;
                case Attack.PlayDead: PlayDead(); break;
                case Attack.SternChomp: SternChomp(); break;
                case Attack.JumpOver: JumpOver(); break;
                case Attack.Alongside: StartAlongside(); break;
            }
        }

        private static float Water(Vector3 at) => Tow.Water(at);

        private static Vector3 AtWater(Vector3 p, float above)
        {
            p.y = Water(p) + above;
            return p;
        }

        private static float Lead(float seconds)
        {
            bool localRider = _rider != null && _rider == Player.LocalPlayer;
            return seconds + (localRider ? 0f : Mathf.Max(0f, Cfg.LatencyLead.Value));
        }

        private static void StrikeAtRider(byte style, float telegraphOverride)
        {
            if (_rider == null) return;
            bool breach = style == SharkEvent.StyleBreach;
            float T = telegraphOverride > 0f ? telegraphOverride : breach ? 1.15f : 0.95f;
            float D = breach ? 1.3f : 0.95f;
            float S = breach ? 0.72f : 0.5f;
            float len = breach ? 12f : 14f;

            Vector3 pred = _riderPos + _riderVel * Lead(T + S * D);
            Vector3 dir = Quaternion.AngleAxis(UnityEngine.Random.Range(-35f, 35f), Vector3.up) * TravelDir();
            Vector3 a = AtWater(pred - dir * (S * len), breach ? 0.2f : 0.2f);
            Vector3 b = AtWater(pred + dir * ((1f - S) * len), breach ? -1.6f : 0f);
            Run(new SharkEvent
            {
                Kind = EvKind.Lunge, From = _pos, A = a, B = b,
                Telegraph = T, Duration = D, Apex = breach ? 3.4f : 0.5f, Snap = S, Style = style,
                Target = _rider.OwnerId
            }, null);
        }

        private static float _alongsideUntil, _alongsideSide = 1f;

        /// <summary>Swims up right beside the rider: the chance to jump on its back.</summary>
        private static void StartAlongside()
        {
            if (_rider == null) return;
            _alongsideSide = UnityEngine.Random.value < 0.5f ? -1f : 1f;
            _alongsideUntil = Time.time + 4.5f;
            Send(new SharkEvent { Kind = EvKind.Alongside, Extra = _rider.OwnerId });
        }

        private static void Chomp()
        {
            if (_rider == null) return;
            Vector3 dir = TravelDir();
            Vector3 pred = _riderPos + _riderVel * Lead(0.35f + 0.33f);
            Vector3 a = AtWater(Mouth() - dir * 1.5f, 0.2f);
            Vector3 b = AtWater(pred + dir * 2.5f, -0.3f);
            Run(new SharkEvent
            {
                Kind = EvKind.Chomp, From = _pos, A = a, B = b,
                Telegraph = 0.35f, Duration = 0.55f, Apex = 0.6f, Snap = 0.6f, Style = SharkEvent.StyleSmall,
                Target = _rider.OwnerId, LockFrac = 1f
            }, null);
        }

        private static void FakeOut()
        {
            Dive(() =>
            {
                SetMode(SharkMode.Hidden);
                _modeUntil = Time.time + UnityEngine.Random.Range(3f, 5.5f);
                _next = Emerge;
            });
        }

        private static Action _next;

        private static void Next()
        {
            Action n = _next;
            _next = null;
            if (n != null) n();
            else SetMode(SharkMode.Chase);
        }

        private static void Dive(Action then)
        {
            Vector3 m = Mouth();
            Vector3 a = m;
            Vector3 b = m + _heading * 6f + Vector3.down * 7f;
            Run(new SharkEvent { Kind = EvKind.Dive, From = _pos, A = a, B = b, Telegraph = 0f, Duration = 1f, Apex = 0.8f, Snap = 1f }, then);
        }

        private static void Emerge()
        {
            Vector3 bow, bowFwd;
            if (!BoatMount.TryGetWorld(BoatMount.Slot.Bow, out bow, out bowFwd)) { SetMode(SharkMode.Chase); return; }
            Vector3 fwd = Tow.Forward();
            Vector3 side = Vector3.Cross(Vector3.up, fwd) * (UnityEngine.Random.value < 0.5f ? -1f : 1f);
            const float T = 0.55f, D = 1f, S = 0.5f;
            Vector3 p = bow + Tow.Velocity() * (T + S * D);
            _pos = AtWater(p + side * 16f, -5f);
            Run(new SharkEvent
            {
                Kind = EvKind.Emerge, From = _pos, A = AtWater(p + side * 11f + fwd * 2f, 0.3f), B = AtWater(p - side * 9f - fwd, -1.2f),
                Telegraph = T, Duration = D, Apex = 2.4f, Snap = S, Style = SharkEvent.StyleHuge
            }, null);
        }

        private static void RopeBite()
        {
            Vector3 tow;
            if (_rider == null || !Tow.Point(out tow)) { StrikeAtRider(SharkEvent.StyleSkim, 0f); return; }
            const float T = 0.8f, D = 0.8f, S = 0.45f;
            Vector3 mid = (Flat(tow) + Flat(_riderPos)) * 0.5f + Tow.Velocity() * (T + S * D);
            Vector3 ropeDir = Flat(_riderPos - tow).normalized;
            Vector3 side = Vector3.Cross(Vector3.up, ropeDir) * (UnityEngine.Random.value < 0.5f ? -1f : 1f);
            Vector3 fwd = Tow.Forward();
            Run(new SharkEvent
            {
                Kind = EvKind.RopeBite, From = _pos, A = AtWater(mid + side * 9f - fwd * 2f, 0.2f), B = AtWater(mid - side * 5f + fwd, -0.8f),
                Telegraph = T, Duration = D, Apex = 0.9f, Snap = S
            }, null);
        }

        private static void TailSlap()
        {
            if (_rider == null) return;
            Vector3 dir = TravelDir();
            Vector3 side = Vector3.Cross(Vector3.up, dir) * (UnityEngine.Random.value < 0.5f ? -1f : 1f);
            const float T = 0.8f, D = 1.3f;
            Vector3 body = AtWater(_riderPos + _riderVel * Lead(T + D * 0.6f) + side * 6f + dir * 3f, -0.2f);
            Vector3 heading = (dir + side * 0.3f).normalized;
            Vector3 tail = AtWater(body - heading * (Cfg.Length.Value * 0.45f), 0f);
            Run(new SharkEvent
            {
                Kind = EvKind.TailSlap, From = _pos, A = body, B = tail, Telegraph = T, Duration = D, Snap = 0.6f
            }, null);
        }

        private static void PlayDead()
        {
            _playDeadUsed = true;
            SetMode(SharkMode.PlayDead);
            _modeUntil = Time.time + 5f;
            _ev = new SharkEvent { Kind = EvKind.PlayDead, A = AtWater(_pos, 0f), B = AtWater(_pos + _heading, 0f), Duration = 5f, Id = _nextId++ };
            _ev.Start = Time.time;
            _snapDone = true;
            Send(_ev);
            _next = () => Dive(() =>
            {
                SetMode(SharkMode.Hidden);
                _modeUntil = Time.time + 1.2f;
                if (_rider != null) _pos = AtWater(_riderPos - TravelDir() * 20f, -6f);
                _next = () => { SetMode(SharkMode.Chase); StrikeAtRider(SharkEvent.StyleSkim, 0.6f); };
            });
        }

        private static void SternChomp()
        {
            Vector3 stern, sternFwd;
            if (!BoatMount.TryGetWorld(BoatMount.Slot.Stern, out stern, out sternFwd)) { SetMode(SharkMode.Chase); return; }
            Vector3 fwd = Tow.Forward();
            Vector3 side = Vector3.Cross(Vector3.up, fwd) * (UnityEngine.Random.value < 0.5f ? -1f : 1f);
            const float T = 0.8f, D = 0.8f, S = 0.65f;
            Vector3 target = stern + Tow.Velocity() * (T + S * D);
            Run(new SharkEvent
            {
                Kind = EvKind.SternChomp, From = _pos, A = AtWater(target - fwd * 9f + side * 4f, 0.1f), B = AtWater(target + fwd * 1.5f - side, -0.4f),
                Telegraph = T, Duration = D, Apex = 1.4f, Snap = S, Style = SharkEvent.StyleHuge
            }, null);
        }

        private static void JumpOver()
        {
            Vector3 fwd = Tow.Forward();
            Vector3 side = Vector3.Cross(Vector3.up, fwd) * (UnityEngine.Random.value < 0.5f ? -1f : 1f);
            const float T = 1f, D = 1.7f;
            Vector3 c = Tow.Centre() + Tow.Velocity() * (T + 0.5f * D);
            Run(new SharkEvent
            {
                Kind = EvKind.JumpOver, From = _pos, A = AtWater(c + side * 13f, 0.3f), B = AtWater(c - side * 13f, -1f),
                Telegraph = T, Duration = D, Apex = 9.5f, Snap = 0.5f, Style = SharkEvent.StyleHuge
            }, null);
        }

        private static void StartFinale()
        {
            SetMode(SharkMode.Finale);
            _modeUntil = Time.time + 8f;
            Send(new SharkEvent { Kind = EvKind.Finale, A = _pos, Duration = 8f });
            MegaModule.Announce("It's surfacing in FRONT of you, jaws wide open. Stuff its mouth with something explosive!");
        }

        private static void HoldFinale(float dt)
        {
            Vector3 fwd = Tow.Forward();
            Vector3 target = Tow.Centre() + fwd * 26f + Tow.Velocity() * 0.5f;
            Vector3 to = Flat(target - _pos);
            _vel = Vector3.MoveTowards(_vel, Tow.Velocity() + to * 1.5f, 25f * dt);
            _pos += _vel * dt;
            _pos.y = Mathf.Lerp(_pos.y, PirateModule.WaterY() + 0.3f, dt * 3f);
            _heading = Vector3.RotateTowards(_heading, -fwd, 200f * Mathf.Deg2Rad * dt, 0f);
        }

        // ------------------------------------------------------------------ decoys: grilled food in the water

        private static bool TryDecoy(float now)
        {
            if (now < _decoyCooldown || _rodeoOwner >= 0) return false;
            Item best = null;
            float bestD = 30f;
            float water = PirateModule.WaterY();
            Vector3 mouth = Mouth();
            try
            {
                foreach (KeyValuePair<Transform, Item> kv in ItemManager.Items)
                {
                    Item it = kv.Value;
                    if (it == null || it.IsDestroying || it.Creature == null || it.HasPlayerHolder) continue;
                    if (it.Cookness < 0.6f) continue;
                    if (it.RigidbodySync != null && it.RigidbodySync.OnBoat) continue;
                    Vector3 p = it.transform.position;
                    if (p.y > water + 0.6f) continue;
                    float d = Flat(p - mouth).magnitude;
                    if (d < bestD) { bestD = d; best = it; }
                }
            }
            catch (Exception e) { Diag.Debug("Decoy scan: " + e.Message); }

            if (best == null) return false;
            _decoy = best;
            SetMode(SharkMode.Distracted);
            _modeUntil = now + 6f;
            MegaModule.Announce("It smells the barbecue in the water and goes for it!");
            return true;
        }

        private static void ChaseDecoy(float dt, float now)
        {
            if (_decoy == null || _decoy.IsDestroying || now >= _modeUntil)
            {
                _decoy = null;
                _decoyCooldown = now + 6f;
                SetMode(SharkMode.Chase);
                return;
            }
            Vector3 p = _decoy.transform.position;
            Steer(dt, p - _heading * MouthOffset, Vector3.zero);
            _pos.y = Mathf.Lerp(_pos.y, PirateModule.WaterY() - Cfg.SwimDepth.Value, dt * 2f);
            if (Flat(Mouth() - p).magnitude < 4f)
            {
                Item food = _decoy;
                _decoy = null;
                _decoyCooldown = now + 8f;
                Run(new SharkEvent
                {
                    Kind = EvKind.Decoy, From = _pos, A = AtWater(Mouth(), 0.2f), B = AtWater(p + _heading * 2f, -0.8f),
                    Telegraph = 0f, Duration = 0.6f, Apex = 1.2f, Snap = 0.5f
                }, null);
                try { food.DestroyItem((byte)DestroyReason.Default); } catch (Exception e) { Diag.Debug("Decoy eat: " + e.Message); }
                ModSave.AddCounter("megalodon.decoys", 1);
            }
        }

        // ------------------------------------------------------------------ event playback (host side)

        private static void Run(SharkEvent ev, Action after)
        {
            ev.Id = _nextId++;
            ev.Start = Time.time;
            _ev = ev;
            _snapDone = false;
            _after = after;
            SetMode(SharkMode.Strike);
            Send(ev);
        }

        private static void Advance(float now)
        {
            if (_ev == null) { SetMode(SharkMode.Chase); return; }
            float t = now - _ev.Start;
            if (_ev.Target >= 0 && _rider != null) _ev.Track(t, Time.deltaTime, _riderPos, Tow.Velocity());
            Vector3 body; Quaternion rot; bool sub;
            _ev.Pose(t, MouthOffset, out body, out rot, out sub);
            Vector3 f = rot * Vector3.forward;
            _vel = (body - _pos) / Mathf.Max(1e-3f, Time.deltaTime);
            _pos = body;
            Vector3 flat = Flat(f);
            if (flat.sqrMagnitude > 1e-3f) _heading = flat.normalized;

            if (!_snapDone && t >= _ev.SnapTime)
            {
                _snapDone = true;
                OnSnap(_ev);
            }

            if (t >= _ev.Total && Mode == SharkMode.Strike)
            {
                _ev = null;
                _vel = _heading * Mathf.Max(6f, Tow.Speed());
                Action after = _after;
                _after = null;
                if (after != null) after();
                else SetMode(SharkMode.Chase);
            }
        }

        private static void OnSnap(SharkEvent ev)
        {
            switch (ev.Kind)
            {
                case EvKind.Emerge:
                {
                    Vector3 bow, bf;
                    if (BoatMount.TryGetWorld(BoatMount.Slot.Bow, out bow, out bf) && Flat(ev.SnapPoint - bow).magnitude < 4.2f)
                        HitBoat(bow, (ev.B - ev.A).normalized, false);
                    else DeckShout(MegaLines.Pick(MegaLines.FakeOut));
                    break;
                }
                case EvKind.SternChomp:
                {
                    Vector3 stern, sf;
                    if (BoatMount.TryGetWorld(BoatMount.Slot.Stern, out stern, out sf) && Flat(ev.SnapPoint - stern).magnitude < 4.8f)
                        HitBoat(stern, (ev.B - ev.A).normalized, true);
                    break;
                }
                case EvKind.RopeBite:
                    WakeRig.HostShortenRope(Cfg.RopeBiteShortens.Value);
                    break;
                case EvKind.JumpOver:
                    foreach (Player p in PlayersOnBoat())
                        try { p.Movement.RPCKnockback(p.Owner, Vector3.up * 4.5f); } catch { }
                    DeckShout(MegaLines.Pick(MegaLines.JumpOver, MegaLines.JumpOverClean));
                    break;
            }
        }

        /// <summary>The jaws or the body hit the boat: a shove, everyone aboard stumbles, maybe the engine.</summary>
        private static void HitBoat(Vector3 at, Vector3 dir, bool engine)
        {
            try
            {
                Rigidbody rig = Tow.Boat.HiddenPhysicsRig;
                Vector3 push = Flat(dir).normalized * 3.5f + Vector3.up * 1.2f;
                rig.AddForceAtPosition(push, at, ForceMode.VelocityChange);
                rig.AddTorque(Vector3.up * UnityEngine.Random.Range(-1.2f, 1.2f), ForceMode.VelocityChange);
            }
            catch (Exception e) { Diag.Debug("HitBoat: " + e.Message); }

            int hurt = Mathf.Max(0, Cfg.BoatHitDamage.Value);
            foreach (Player p in PlayersOnBoat())
            {
                try { p.Movement.RPCKnockback(p.Owner, Flat(dir).normalized * 4f + Vector3.up * 3.5f); } catch { }
                if (hurt > 0) Hurt(p, hurt, at);
            }

            if (engine) MegaBoat.HostDamageEngine();
            MegaModule.Announce(engine ? "It bit the stern! The engine's coughing smoke!" : "It rammed the bow!");
            ModSave.AddCounter("megalodon.boat.hits", 1);
        }

        private static List<Player> PlayersOnBoat()
        {
            var list = new List<Player>();
            Vector3 c = Tow.Centre();
            foreach (Player p in PlayerManager.AlivePlayers)
            {
                if (p == null || p == _rider) continue;
                if (Flat(p.Transform.position - c).magnitude < 5.5f) list.Add(p);
            }
            return list;
        }

        private static void DeckShout(string line)
        {
            List<Player> aboard = PlayersOnBoat();
            if (aboard.Count == 0) return;
            Shouts.HostBroadcast(aboard[UnityEngine.Random.Range(0, aboard.Count)], line);
        }

        // ------------------------------------------------------------------ what players did to it

        private static void HostBitten(Player p, bool riding)
        {
            if (!Active || Mode == SharkMode.Dead || Mode == SharkMode.Leaving) return;
            float last;
            if (LastBiteReport.TryGetValue(p.OwnerId, out last) && Time.time - last < 2f) return;
            LastBiteReport[p.OwnerId] = Time.time;

            if (riding && p.OwnerId == WakeRig.RiderId)
            {
                Board next = MegaRules.Next(WakeRig.Tier);
                if (next == Board.Eaten) { Eat(p); return; }
                WakeRig.HostSetTier(next);
                Hurt(p, Mathf.Max(0, Cfg.BiteDamage.Value), p.Transform.position);
                Send(new SharkEvent { Kind = EvKind.Bitten, Extra = p.OwnerId, Style = (byte)next });
                MegaModule.Announce(p.SteamName + " got bitten! Now riding: " + MegaRules.BoardName(next) + ".");
                return;
            }
            Eat(p);
        }

        /// <summary>Real damage through the game's own hit path: blood, sound, and yes, you can die.</summary>
        private static void Hurt(Player p, int damage, Vector3 at)
        {
            if (p == null || damage <= 0 || p.Dying.IsDead) return;
            try { Server.Instance.HitPlayer(p, damage, Vector3.up * 2f, at, (byte)DamageType.Bite, null); }
            catch (Exception e) { Diag.Debug("Megalodon hurt: " + e.Message); }
        }

        private static void Eat(Player p)
        {
            Send(new SharkEvent { Kind = EvKind.Eaten, Extra = p.OwnerId });
            ModSave.AddCounter("megalodon.eaten", 1);
            if (p.OwnerId == WakeRig.RiderId) WakeRig.HostClear("eaten");
            MegaModule.Announce(p.SteamName + " got EATEN. Mission over - it's full. For now.");
            Leave("it's full");
        }

        private static void HostGunHit(int damage, Vector3 point)
        {
            if (!Active || Mode == SharkMode.Dead || Mode == SharkMode.Leaving) return;
            Vector3 a, b; float r;
            Capsule(out a, out b, out r);
            if (DistanceToSegment(point, a, b) > r + 6f) return;   // stale or forged

            float now = Time.time;
            if (now - _gunWindowStart > 1f) { _gunWindowStart = now; _gunWindowDamage = 0f; }
            float amount = Mathf.Max(0, damage) * Cfg.GunMultiplier.Value;
            float room = Cfg.MaxGunDamagePerSecond.Value - _gunWindowDamage;
            if (room <= 0f) return;
            amount = Mathf.Min(amount, room);
            _gunWindowDamage += amount;
            Damage(amount, "gunfire");
        }

        /// <summary>Host: an explosion went off (dynamite, cannonball, mine). Called from the explosion hook.</summary>
        internal static void HostExplosion(Vector3 at, float radius, int baseDamage, float multiplier)
        {
            if (!Active || Mode == SharkMode.Dead || Mode == SharkMode.Leaving || Mode == SharkMode.Hidden) return;

            if (Mode == SharkMode.Finale && Vector3.Distance(at, Mouth()) < 4.5f)
            {
                MegaModule.Announce("STUFFED ITS MOUTH! Right down the gullet!");
                Damage(Hp + 1f, "an explosion right in its mouth");
                return;
            }

            Vector3 a, b; float r;
            Capsule(out a, out b, out r);
            float d = Mathf.Max(0f, DistanceToSegment(at, a, b) - r);
            int dmg = MegaRules.ExplosionDamage(d, radius, baseDamage, Cfg.ExplosionReach.Value, Cfg.ExplosionMultiplier.Value * multiplier);
            if (dmg > 0) Damage(dmg, "an explosion " + d.ToString("0.0") + " m away");
        }

        private static void HostRodeo(Player p, byte what)
        {
            if (!Active) return;
            if (what == 0 && _rodeoOwner < 0 && p.OwnerId == WakeRig.RiderId && (Mode == SharkMode.Chase || Mode == SharkMode.Stalk))
            {
                _rodeoOwner = p.OwnerId;
                _rodeoUntil = Time.time + 5f;
                if (Mode == SharkMode.Stalk) SetMode(SharkMode.Chase);
                Send(new SharkEvent { Kind = EvKind.RodeoStart, Extra = p.OwnerId });
                MegaModule.Announce(p.SteamName + " is RIDING THE MEGALODON.");
            }
            else if (what == 1 && _rodeoOwner == p.OwnerId && _rodeoChargeAt < 0f)
            {
                _rodeoChargeAt = Time.time + 2.5f;
            }
            else if (what == 2 && _rodeoOwner == p.OwnerId)
            {
                EndRodeo();
            }
        }

        /// <summary>Host: it swallowed a barrel mine instead of setting it off. It will be back. Quickly.</summary>
        internal static void SwallowMine(ushort id, Vector3 at)
        {
            if (!Active || _burpAt > 0f) return;
            _burpAt = Time.time + 2.5f;
            Send(new SharkEvent { Kind = EvKind.Swallow, A = at, Extra = id });
            MegaModule.Announce("It SWALLOWED the mine. ...Why is it looking at the boat like that?");
        }

        private static void EndRodeo()
        {
            if (_rodeoOwner < 0) return;
            _rodeoOwner = -1;
            _nextAttack = Mathf.Max(_nextAttack, Time.time + 3f);
            Send(new SharkEvent { Kind = EvKind.RodeoEnd });
        }

        internal static void Damage(float amount, string source)
        {
            if (!Active || amount <= 0f || Mode == SharkMode.Dead || Mode == SharkMode.Leaving) return;
            Hp = Mathf.Max(0f, Hp - amount);
            _statusDirty = true;
            Diag.Debug("Megalodon: -" + amount.ToString("0") + " (" + source + "), " + Hp.ToString("0") + "/" + MaxHp.ToString("0") + ".");
            if (Hp <= 0f) { Kill(source); return; }

            Phase p = MegaRules.PhaseFor(Hp / MaxHp);
            if (p != Phase)
            {
                Phase = p;
                _nextAttack = Mathf.Min(_nextAttack, Time.time + 2f);
                Send(new SharkEvent { Kind = EvKind.Phase, Extra = (int)p });
                MegaModule.Announce(p == Phase.Two ? "It's hurt - and ANGRY. The sea is getting busy."
                                                   : "It's FURIOUS. It's going for the boat now!");
            }
        }

        /// <summary>The megalodon's current capsule, tail to mouth, for hit tests on the host.</summary>
        internal static void Capsule(out Vector3 tail, out Vector3 mouth, out float radius)
        {
            Vector3 f = _heading;
            if (_ev != null && _ev.OverridesPose && Mode != SharkMode.Chase)
            {
                Vector3 body; Quaternion rot; bool sub;
                _ev.Pose(Time.time - _ev.Start, MouthOffset, out body, out rot, out sub);
                f = rot * Vector3.forward;
            }
            float half = Cfg.Length.Value * 0.46f;
            mouth = _pos + f * half;
            tail = _pos - f * half;
            radius = Cfg.Length.Value * 0.11f;
        }

        internal static Vector3 Mouth()
        {
            Vector3 f = _heading;
            if (_ev != null && _ev.OverridesPose && Mode == SharkMode.Strike)
            {
                Vector3 body; Quaternion rot; bool sub;
                _ev.Pose(Time.time - _ev.Start, MouthOffset, out body, out rot, out sub);
                return body + rot * Vector3.forward * MouthOffset;
            }
            return _pos + f * MouthOffset;
        }

        internal static Vector3 Position => _pos;

        private static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float t = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
            return Vector3.Distance(p, a + ab * t);
        }

        // ------------------------------------------------------------------ endings

        private static void Kill(string source)
        {
            Hp = 0f;
            SetMode(SharkMode.Dead);
            _modeUntil = Time.time + 9f;
            _ev = new SharkEvent { Kind = EvKind.Death, A = AtWater(_pos, 0f), B = AtWater(_pos + _heading, 0f), Duration = 9f, Id = _nextId++ };
            _ev.Start = Time.time;
            _snapDone = true;
            Send(_ev);
            BroadcastStatus();

            // A last underwater blast throws up a sea of stunned fish: dinner is served.
            MegaBoom.Explode(AtWater(_pos, -0.6f), true, 0f, true);

            int money = Mathf.Max(0, Cfg.RewardMoney.Value);
            try { if (money > 0) MoneyManager.AddMoney(money, Player.LocalPlayer); } catch (Exception e) { Diag.Exception("Megalodon reward", e); }
            QuestModule.Instance?.SetFlag(MegalodonStory.FlagMegalodonBeaten);
            QuestModule.Instance?.SetFlag(MegalodonStory.FlagTeethRecovered);
            ModSave.AddCounter("megalodon.kills", 1);
            MegaModule.Announce("THE MEGALODON IS DEAD (" + source + "). Teeth everywhere - and one set of dentures. +" + money + " coins.");
            foreach (Player p in PlayerManager.AlivePlayers)
                if (p != null && UnityEngine.Random.value < 0.7f) Shouts.HostBroadcast(p, MegaLines.Pick(MegaLines.Victory));
            _lastFightEnded = Time.time;
            Diag.Info("Megalodon: killed by " + source + ".");
        }

        private static void Leave(string why)
        {
            if (!Active || Mode == SharkMode.Leaving || Mode == SharkMode.Dead) return;
            SetMode(SharkMode.Leaving);
            _modeUntil = Time.time + 3f;
            _ev = null;
            _rodeoOwner = -1;
            Send(new SharkEvent { Kind = EvKind.Leave, A = _pos });
            BroadcastStatus();
            MegaModule.Announce("The megalodon sinks away - " + why + ".");
            _lastFightEnded = Time.time;
            Diag.Info("Megalodon: leaving (" + why + ").");
        }

        internal static void Despawn(string why)
        {
            if (!Active) return;
            Active = false;
            SetMode(SharkMode.Gone);
            _ev = null;
            _after = null;
            _next = null;
            _lastFightEnded = Time.time;
            BroadcastStatus();
            MegaHazards.HostClear();
            MegaBoat.HostReset();
            Diag.Info("Megalodon: gone (" + why + ").");
        }

        /// <summary>Debug: leave now, or send it away if it is out.</summary>
        private static bool _debugSpawn;

        /// <summary>
        /// Debug: send it in (or away). A debug megalodon also hands out the wakeboard, ignores the
        /// shallows and waits a good while for someone to grab the rope - it only hunts wakeboarders.
        /// </summary>
        internal static void DebugToggle()
        {
            if (Active) { Leave("debug key"); return; }
            if (SharedState.Grant(MegalodonStory.UnlockWakeboard))
                MegaModule.Announce("Debug: wakeboard unlocked - it leans by the helm, press E at it.");
            Spawn();
            _debugSpawn = true;
            if (WakeRig.RiderId < 0)
                MegaModule.Announce("It only hunts wakeboarders: grab the board by the helm (E) and have someone drive.");
        }

        internal static void DebugDamage(float fraction) => Damage(MaxHp * fraction, "debug key");

        internal static void Reset()
        {
            Active = false;
            Mode = SharkMode.Gone;
            _ev = null;
            _after = null;
            _next = null;
            _rodeoOwner = -1;
            _rideOutFor = 0f;
            LastBiteReport.Clear();
        }

        private static void SetMode(SharkMode m)
        {
            if (Mode == m) return;
            Mode = m;
            _statusDirty = true;
        }

        // ------------------------------------------------------------------ network

        private static void Send(SharkEvent ev)
        {
            if (ev.Id == 0) ev.Id = _nextId++;
            ModNet.SendToAll(Msg.MegaEvent, ev.Write);
        }

        private static void BroadcastPose()
        {
            Vector3 pos = _pos;
            float yaw = Mathf.Atan2(_heading.x, _heading.z) * Mathf.Rad2Deg;
            Vector3 vel = _vel;
            byte mode = (byte)Mode;
            byte flags = (byte)((_rodeoOwner >= 0 ? 1 : 0) | (Mode == SharkMode.Finale ? 2 : 0));
            ModNet.SendToAll(Msg.MegaPose, w =>
            {
                Cannonballs.WriteVec(w, pos);
                w.Write(yaw);
                Cannonballs.WriteVec(w, vel);
                w.Write(mode);
                w.Write(flags);
            });
        }

        internal static void BroadcastStatus() => ModNet.SendToAll(Msg.MegaStatus, WriteStatus);

        private static void WriteStatus(System.IO.BinaryWriter w)
        {
            w.Write(Active);
            w.Write(Hp);
            w.Write(MaxHp);
            w.Write((byte)Phase);
            w.Write((byte)Mode);
            MegaBoat.WriteStatus(w);
        }
    }
}
