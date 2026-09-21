using System;
using System.Collections.Generic;
using FishNet;
using UnityEngine;

namespace Expanded.Pirates
{
    /// <summary>
    /// The enemy ship. Sits on a runtime-built networked prefab, so it exists on every peer; only
    /// the host steers, fires and takes damage, and FishNet's NetworkTransform replicates movement.
    ///
    /// Sailing model: close to a broadside distance, then circle the party rather than ramming, so
    /// the fight reads as a naval duel. She only fires when the target is actually abeam, which is
    /// what makes positioning your own boat worth doing.
    /// </summary>
    internal sealed class PirateShip : MonoBehaviour
    {
        internal enum Stance { Closing, Broadside, Withdrawing, Leaving, Sinking, Surrendered }

        /// <summary>Host only: the authoritative ship.</summary>
        internal static PirateShip Active { get; private set; }

        /// <summary>Any machine: the ship as this client sees it, for local effects like crew deaths.</summary>
        internal static PirateShip Current { get; private set; }

        private void OnEnable() => Current = this;

        private void OnDisable()
        {
            if (Current == this) Current = null;
        }

        /// <summary>Client-side: play a crew member's death (host decided it).</summary>
        internal void ClientKillCrew(int index)
        {
            foreach (CrewMember c in GetComponentsInChildren<CrewMember>(true))
                if (c.Index == index) { c.Die(); return; }
        }

        internal void ClientApplyDeadMask(byte mask)
        {
            foreach (CrewMember c in GetComponentsInChildren<CrewMember>(true))
                if (c.Index < 8 && (mask & (1 << c.Index)) != 0) c.Die();
        }

        private Stance _stance = Stance.Closing;
        private float _stanceTime;
        private int _circleDir = 1;
        private float _bobPhase;
        private float _speed;
        private bool _aground;

        private float _nextVolley;
        private float _spawnedAt;
        private float _sinkTime;
        private float _lastStatusSent = -10f;
        private bool _statusDirty = true;

        // Crew-reported gun damage, rate-limited per second so a bad client cannot one-shot her.
        private float _gunDamageWindowStart;
        private float _gunDamageInWindow;

        private readonly List<Collider> _hull = new List<Collider>();

        // Crew, host-side. Index matches PirateCrew.Posts on every machine.
        private CrewMember[] _crew = new CrewMember[0];
        private float[] _crewHp = new float[0];
        private float _surrenderTime;

        internal Stance CurrentStance => _stance;
        internal float Health { get; private set; }
        internal float MaxHealth { get; private set; }
        internal bool Alive => Health > 0f && _stance != Stance.Sinking && _stance != Stance.Surrendered;

        /// <summary>Bit i set = crew member i is dead. Sent in the status so late joiners see the bodies.</summary>
        internal byte DeadMask
        {
            get
            {
                byte m = 0;
                for (int i = 0; i < _crewHp.Length && i < 8; i++) if (_crewHp[i] <= 0f) m |= (byte)(1 << i);
                return m;
            }
        }

        private static PirateConfig Cfg => PirateModule.Cfg;

        private void Awake()
        {
            _bobPhase = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
            _circleDir = UnityEngine.Random.Range(0, 2) == 0 ? -1 : 1;
            GetComponentsInChildren(true, _hull);
        }

        private void OnDestroy()
        {
            if (Active == this) Active = null;
            if (Current == this) Current = null;
        }

        internal void ServerInitialise(float maxHealth)
        {
            MaxHealth = Mathf.Max(1f, maxHealth);
            Health = MaxHealth;
            _spawnedAt = Time.time;
            _nextVolley = Time.time + Cfg.FirstVolleyDelay.Value;
            Active = this;
            _statusDirty = true;

            // Crew hit points, ordered by crew index.
            CrewMember[] found = GetComponentsInChildren<CrewMember>(true);
            _crew = new CrewMember[PirateCrew.Posts.Length];
            _crewHp = new float[PirateCrew.Posts.Length];
            foreach (CrewMember c in found)
                if (c.Index >= 0 && c.Index < _crew.Length) _crew[c.Index] = c;
            for (int i = 0; i < _crewHp.Length; i++)
            {
                bool present = _crew[i] != null;
                _crewHp[i] = !present ? 0f
                    : PirateCrew.Posts[i].Role == PirateCrew.Role.Captain ? Cfg.CaptainHealth.Value : Cfg.CrewHealth.Value;
            }
        }

        // ------------------------------------------------------------------ crew

        internal bool CrewAlive(int index) => index >= 0 && index < _crewHp.Length && _crewHp[index] > 0f;

        /// <summary>
        /// Host-side crew damage from a shooter's report or a nearby blast. A dead gunner silences
        /// most of his broadside; a dead captain means the ship strikes her colours.
        /// </summary>
        internal void ServerDamageCrew(int index, float amount, string source)
        {
            if (!Alive || !CrewAlive(index) || amount <= 0f) return;

            _crewHp[index] = Mathf.Max(0f, _crewHp[index] - amount);
            if (_crewHp[index] > 0f) return;

            PirateCrew.Post post = PirateCrew.Posts[index];
            Diag.Info("Pirate crew " + index + " (" + post.Role + ") killed by " + source + ".");
            _statusDirty = true;
            ModNet.SendToAll(Msg.CrewDied, w => w.Write((byte)index));

            switch (post.Role)
            {
                case PirateCrew.Role.Captain:
                    Surrender();
                    break;
                case PirateCrew.Role.Gunner:
                    PirateModule.Announce("Their " + (post.Side > 0 ? "starboard" : "port") + " gunner is down - that broadside is half silenced!");
                    break;
            }
        }

        /// <summary>The captain is dead: the crew strike their colours and the ship is yours.</summary>
        private void Surrender()
        {
            if (_stance == Stance.Surrendered || _stance == Stance.Sinking) return;
            SetStance(Stance.Surrendered);
            _surrenderTime = 0f;
            StopAllCoroutines();   // no more broadsides in flight
            PirateModule.Instance?.OnShipDefeated(this, true);
        }

        private float CrewBlastDamage(Vector3 at, float radius, int baseDamage)
        {
            float hurt = 0f;
            for (int i = 0; i < _crew.Length; i++)
            {
                if (!CrewAlive(i) || _crew[i] == null) continue;
                float d = Vector3.Distance(at, _crew[i].transform.position + Vector3.up * 0.9f);
                if (d > radius) continue;
                float dmg = baseDamage * (1f - d / Mathf.Max(0.1f, radius));
                ServerDamageCrew(i, dmg, "blast");
                hurt += dmg;
            }
            return hurt;
        }

        // ------------------------------------------------------------------ damage

        /// <summary>
        /// Explosion damage. Distance is measured to the nearest point of the hull, not the ship's
        /// centre: she is 13 m long, and a charge against her stern should count.
        /// </summary>
        internal void ServerTakeExplosion(Vector3 at, float radius, int baseDamage)
        {
            if (!Alive) return;

            // A ball bursting on deck cuts down whoever is standing near it.
            CrewBlastDamage(at, Mathf.Max(0.5f, radius), baseDamage);
            if (!Alive) return;   // that may have been the captain

            float dist = DistanceToHull(at);
            float reach = Mathf.Max(0.5f, radius) * Cfg.ExplosionReach.Value;
            if (dist > reach) return;

            // Full damage in the inner half, linear falloff to nothing at the edge of reach.
            float inner = reach * 0.5f;
            float falloff = dist <= inner ? 1f : 1f - Mathf.InverseLerp(inner, reach, dist);
            float damage = baseDamage * falloff * Cfg.ExplosionDamageMultiplier.Value;
            if (damage < 1f) return;

            ServerDamage(damage, "explosion " + dist.ToString("0.0") + "m from hull");
        }

        /// <summary>Gun damage reported by a shooter. Capped per second as a sanity limit.</summary>
        internal void ServerTakeGunfire(int reported, string shooter)
        {
            if (!Alive) return;

            if (Time.time - _gunDamageWindowStart > 1f)
            {
                _gunDamageWindowStart = Time.time;
                _gunDamageInWindow = 0f;
            }

            float allowed = Mathf.Max(0f, Cfg.MaxGunDamagePerSecond.Value - _gunDamageInWindow);
            float damage = Mathf.Min(Mathf.Clamp(reported, 0, 500) * Cfg.GunDamageMultiplier.Value, allowed);
            if (damage < 0.5f) return;

            _gunDamageInWindow += damage;
            ServerDamage(damage, "gunfire from " + shooter);
        }

        private void ServerDamage(float amount, string source)
        {
            if (!InstanceFinder.IsServerStarted || !Alive) return;

            Health = Mathf.Max(0f, Health - amount);
            _statusDirty = true;
            Diag.Info("Pirate ship hit: -" + amount.ToString("0") + " (" + source + ") -> " +
                      Health.ToString("0") + "/" + MaxHealth.ToString("0"));

            if (Health <= 0f) BeginSinking();
        }

        private float DistanceToHull(Vector3 p)
        {
            float best = float.MaxValue;
            for (int i = 0; i < _hull.Count; i++)
            {
                Collider c = _hull[i];
                if (c == null || !c.enabled) continue;
                // Collider.bounds works for every collider type, including the concave mesh colliders
                // the hull uses, where ClosestPoint would not.
                float d = Vector3.Distance(p, c.bounds.ClosestPoint(p));
                if (d < best) best = d;
            }
            return best == float.MaxValue ? Vector3.Distance(p, transform.position) : best;
        }

        private void BeginSinking()
        {
            SetStance(Stance.Sinking);
            _sinkTime = 0f;
            _statusDirty = true;
            StopAllCoroutines();
            PirateModule.Instance?.OnShipDefeated(this, false);
        }

        // ------------------------------------------------------------------ loop

        private void Update()
        {
            // Clients do nothing: their copy is driven entirely by the replicated transform.
            if (!InstanceFinder.IsServerStarted) return;

            try
            {
                float dt = Time.deltaTime;
                if (dt <= 0f) return;

                if (_stance == Stance.Sinking) { Sink(dt); }
                else if (_stance == Stance.Surrendered) { Drift(dt); }
                else
                {
                    CheckFightTimeout();
                    Sail(dt);
                    TryFireBroadside();
                }

                SendStatusIfDue();
            }
            catch (Exception e)
            {
                Diag.Exception("PirateShip.Update", e);
            }
        }

        private void CheckFightTimeout()
        {
            if (_stance == Stance.Leaving) return;

            bool partyDown = PlayerManager.AlivePlayers.Count == 0;
            bool tooLong = Time.time - _spawnedAt > Cfg.MaxFightSeconds.Value;
            if (!partyDown && !tooLong) return;

            SetStance(Stance.Leaving);
            PirateModule.Instance?.OnShipLeaving(partyDown ? "the crew is down" : "they tire of the fight");
        }

        // ------------------------------------------------------------------ sailing

        private void Sail(float dt)
        {
            Vector3 target = PirateModule.PartyPosition();
            Vector3 pos = transform.position;

            Vector3 toTarget = target - pos;
            toTarget.y = 0f;
            float distance = toTarget.magnitude;
            Vector3 toTargetDir = distance > 0.01f ? toTarget / distance : transform.forward;

            UpdateStance(distance);

            if (_stance == Stance.Leaving && distance > Cfg.SpawnDistance.Value * 1.6f)
            {
                PirateModule.Instance?.DespawnShip("sailed away");
                return;
            }

            Vector3 heading = DesiredHeading(toTargetDir, distance);
            heading = AvoidLand(pos, heading);

            // Ships turn slowly; that sluggishness is what makes positioning matter.
            Vector3 forward = Vector3.Slerp(transform.forward, heading, dt * Cfg.TurnRate.Value);
            forward.y = 0f;
            if (forward.sqrMagnitude > 0.001f) transform.forward = forward.normalized;

            float wanted = _aground ? Cfg.Speed.Value * 0.4f
                                    : (_stance == Stance.Broadside ? Cfg.Speed.Value * 0.55f : Cfg.Speed.Value);
            _speed = Mathf.MoveTowards(_speed, wanted, dt * Cfg.Acceleration.Value);

            Vector3 next = pos + transform.forward * (_speed * dt);
            next.y = SurfaceY();
            transform.position = next;

            ApplyRoll(dt);
        }

        private void UpdateStance(float distance)
        {
            _stanceTime += Time.deltaTime;
            if (_stance == Stance.Leaving) return;

            float standoff = Cfg.StandoffDistance.Value;
            switch (_stance)
            {
                case Stance.Closing:
                    if (distance <= standoff) SetStance(Stance.Broadside);
                    break;

                case Stance.Broadside:
                    // Drift too close and she pulls away; too far and she closes again.
                    if (distance < standoff * 0.6f) SetStance(Stance.Withdrawing);
                    else if (distance > standoff * 1.8f) SetStance(Stance.Closing);
                    // Periodically come about so both broadsides get used.
                    else if (_stanceTime > Cfg.CircleSwapSeconds.Value)
                    {
                        _stanceTime = 0f;
                        _circleDir = -_circleDir;
                        Diag.Debug("Pirate ship comes about.");
                    }
                    break;

                case Stance.Withdrawing:
                    if (distance > standoff || _stanceTime > 8f) SetStance(Stance.Broadside);
                    break;
            }
        }

        private void SetStance(Stance s)
        {
            if (_stance == s) return;
            _stance = s;
            _stanceTime = 0f;
            _statusDirty = true;
            Diag.Debug("Pirate ship stance -> " + s);
        }

        private Vector3 DesiredHeading(Vector3 toTargetDir, float distance)
        {
            switch (_stance)
            {
                case Stance.Closing:
                    return toTargetDir;

                case Stance.Withdrawing:
                case Stance.Leaving:
                    return -toTargetDir;

                default:
                    // Tangent around the party, with a slight inward bias so she does not spiral out.
                    Vector3 tangent = Vector3.Cross(Vector3.up, toTargetDir) * _circleDir;
                    float bias = Mathf.Clamp01((distance - Cfg.StandoffDistance.Value) /
                                               Mathf.Max(1f, Cfg.StandoffDistance.Value));
                    return (tangent + toTargetDir * bias).normalized;
            }
        }

        /// <summary>
        /// Steers away from land. Without this she sails straight into an island and sticks there,
        /// which looks broken and makes the fight trivial.
        /// </summary>
        private Vector3 AvoidLand(Vector3 pos, Vector3 heading)
        {
            float probe = Cfg.LandProbeDistance.Value;
            Vector3 eye = pos + Vector3.up * 1.5f;
            int mask = GameInfo.LevelLayer.value;

            if (!HitsLand(eye, heading, probe, mask))
            {
                _aground = false;
                return heading;
            }

            _aground = true;

            // Try progressively wider turns to both sides and take the first clear one.
            for (int step = 1; step <= 6; step++)
            {
                float angle = step * 25f;
                Vector3 left = Quaternion.Euler(0f, -angle, 0f) * heading;
                if (!HitsLand(eye, left, probe, mask)) return left;

                Vector3 right = Quaternion.Euler(0f, angle, 0f) * heading;
                if (!HitsLand(eye, right, probe, mask)) return right;
            }

            return -heading; // boxed in: reverse course
        }

        /// <summary>Land probe that ignores the ship's own colliders, which sit on the same layer.</summary>
        private bool HitsLand(Vector3 eye, Vector3 dir, float dist, int mask)
        {
            RaycastHit[] hits = Physics.RaycastAll(eye, dir, dist, mask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                if (hits[i].collider == null) continue;
                if (hits[i].collider.transform.IsChildOf(transform)) continue;
                return true;
            }
            return false;
        }

        private float SurfaceY()
        {
            return PirateModule.WaterY() + Cfg.Waterline.Value +
                   Mathf.Sin(Time.time * 0.6f + _bobPhase) * Cfg.BobHeight.Value;
        }

        /// <summary>Gentle roll, leaning into the turn while circling, so she looks like she has weight.</summary>
        private void ApplyRoll(float dt)
        {
            float wave = Mathf.Sin(Time.time * 0.8f + _bobPhase) * Cfg.RollDegrees.Value;
            float turnRoll = _stance == Stance.Broadside ? _circleDir * Cfg.RollDegrees.Value * 0.5f : 0f;

            Vector3 e = transform.eulerAngles;
            Quaternion wanted = Quaternion.Euler(0f, e.y, wave + turnRoll);
            transform.rotation = Quaternion.Slerp(transform.rotation, wanted, dt * 2f);
        }

        // ------------------------------------------------------------------ gunnery

        /// <summary>
        /// Fires the broadside facing the target, and only when the target is actually abeam.
        /// Guns point sideways on a ship like this: a target dead ahead is safe.
        /// </summary>
        private void TryFireBroadside()
        {
            if (_stance != Stance.Broadside) return;
            if (Time.time < _nextVolley) return;

            Vector3 target = PirateModule.PartyPosition();
            Vector3 rel = target - transform.position;
            rel.y = 0f;
            if (rel.sqrMagnitude < 1f) return;

            float side = Vector3.Dot(rel.normalized, transform.right);
            if (Mathf.Abs(side) < Mathf.Cos(Cfg.FiringArcDegrees.Value * Mathf.Deg2Rad)) return;

            _nextVolley = Time.time + Cfg.VolleyInterval.Value;
            StartCoroutine(Volley(side > 0f ? 1 : -1));
        }

        private System.Collections.IEnumerator Volley(int side)
        {
            IReadOnlyList<Vector3> ports = PirateModule.Instance != null
                ? PirateModule.Instance.GunPorts(side)
                : null;
            if (ports == null || ports.Count == 0) yield break;

            // With the gunner dead, the rest of the crew can only keep one gun on that side going.
            int guns = SideGunnerAlive(side) ? ports.Count : Mathf.Min(1, ports.Count);
            Diag.Debug("Pirate broadside, " + (side > 0 ? "starboard" : "port") + ", " + guns + "/" + ports.Count + " guns.");

            for (int i = 0; i < guns; i++)
            {
                if (!Alive) yield break;

                Vector3 origin = transform.TransformPoint(ports[i]);
                Vector3 aim = PirateModule.PartyPosition();

                // Spread grows with range: close shots are deadly, long ones are a gamble.
                float range = Vector3.Distance(origin, aim);
                float spread = Cfg.SpreadAtStandoff.Value * range / Mathf.Max(1f, Cfg.StandoffDistance.Value);
                Vector2 jitter = UnityEngine.Random.insideUnitCircle * spread;
                aim += new Vector3(jitter.x, 0f, jitter.y);

                Vector3 vel = Cannonballs.AimAt(origin, aim, Cfg.CannonSpeed.Value, Cfg.CannonGravity.Value);
                Cannonballs.Fire(origin, vel, true);

                yield return new WaitForSeconds(Cfg.VolleyStagger.Value);
            }
        }

        // ------------------------------------------------------------------ surrender

        /// <summary>Colours struck: she loses way and drifts, then is towed off as a prize.</summary>
        private void Drift(float dt)
        {
            _surrenderTime += dt;
            _speed = Mathf.MoveTowards(_speed, 0f, dt * Cfg.Acceleration.Value);
            Vector3 next = transform.position + transform.forward * (_speed * dt);
            next.y = SurfaceY();
            transform.position = next;
            ApplyRoll(dt);

            if (_surrenderTime >= Cfg.SurrenderSeconds.Value)
                PirateModule.Instance?.DespawnShip("taken as a prize");
        }

        private bool SideGunnerAlive(int side)
        {
            for (int i = 0; i < PirateCrew.Posts.Length; i++)
            {
                PirateCrew.Post p = PirateCrew.Posts[i];
                if (p.Role == PirateCrew.Role.Gunner && p.Side == side) return CrewAlive(i);
            }
            return true; // no gunner posted on that side: the guns work as normal
        }

        /// <summary>A shooter reports hitting a crew member. Per-hit cap as a sanity limit.</summary>
        internal void ServerTakeCrewGunfire(int index, int reported, string shooter)
        {
            if (!Alive || !CrewAlive(index)) return;
            float damage = Mathf.Clamp(reported, 0, 250);
            if (damage < 0.5f) return;
            ServerDamageCrew(index, damage, shooter);
        }

        // ------------------------------------------------------------------ sinking

        private void Sink(float dt)
        {
            _sinkTime += dt;

            // Settles by the stern and rolls onto her side, like she is taking on water.
            Vector3 pos = transform.position;
            pos.y -= Cfg.SinkSpeed.Value * dt * Mathf.Clamp01(_sinkTime / 2f);
            transform.position = pos;

            Vector3 e = transform.eulerAngles;
            float roll = Mathf.Min(35f, _sinkTime * 4f);
            float pitch = Mathf.Min(15f, _sinkTime * 1.5f);
            transform.rotation = Quaternion.Slerp(transform.rotation,
                                                  Quaternion.Euler(pitch, e.y, roll), dt * 0.8f);

            if (_sinkTime >= Cfg.SinkSeconds.Value)
                PirateModule.Instance?.DespawnShip("sunk");
        }

        // ------------------------------------------------------------------ status

        /// <summary>Health bar feed: on change (throttled), plus a heartbeat for late joiners.</summary>
        private void SendStatusIfDue()
        {
            bool heartbeat = Time.time - _lastStatusSent > 1f;
            bool throttled = Time.time - _lastStatusSent < 0.1f;
            if (!(_statusDirty && !throttled) && !heartbeat) return;

            _statusDirty = false;
            _lastStatusSent = Time.time;
            PirateModule.BroadcastStatus(true, Health, MaxHealth, _stance, DeadMask);
        }
    }
}
