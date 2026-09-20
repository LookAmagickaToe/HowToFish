using System.Collections.Generic;
using UnityEngine;

namespace SeagullSwarm
{
    /// <summary>
    /// Server-side attack AI bolted onto a spawned Bird, replacing BirdManager's ambient
    /// food-seeking behaviour. Nothing here runs on clients: they only ever see the positions
    /// and anim-state bytes the host already replicates via Bird.ObserverSetPos.
    ///
    /// Cycle: Approach -> Circle -> Climb -> Hover -> Dive (ballistic) -> PeelOff -> Circle ...
    ///        and Flee when the encounter ends.
    ///
    /// Cover: a bird only commits to a dive along a path that is clear of rock (and the boat, if
    /// solid); otherwise it lines up from another side, or gives up if the target is fully covered.
    /// A bird that still flies into something mid-dive dies on impact.
    /// </summary>
    internal class GullAttacker : MonoBehaviour
    {
        internal enum State { Approach, Circle, Climb, Hover, Dive, PeelOff, Flee }

        // Bird.SetAnimState trigger ids, from the vanilla switch in Bird.cs.
        private const byte AnimSearching = 1;
        private const byte AnimDiving = 2;
        private const byte AnimFlapping = 3;
        private const byte AnimFlappingUp = 4;

        private const int PathSamples = 10;
        private const int MaxRepositions = 1;

        /// <summary>True while this mod is applying a gull hit, so the damage logger can tell sources apart.</summary>
        internal static bool ApplyingHit;

        private static int _nextId;

        private Bird _bird;
        private SwarmDirector _director;
        private int _id;
        private float _bobPhase;

        private State _state = State.Approach;
        private float _stateTime;

        private float _orbitAngle;
        private int _orbitDir = 1;

        private Vector3 _velocity;
        private Player _target;
        private Vector3 _attackOffset;   // attack point relative to the target's chest
        private float _hoverEndTime;
        private int _repositions;
        private float _readyAt;
        private Vector3 _fleeDir;

        private readonly HashSet<Player> _hitThisDive = new HashSet<Player>();

        // Diagnostics for the current dive cycle.
        private bool _diveLaunched;
        private float _closestSqr = float.MaxValue;

        internal State CurrentState { get { return _state; } }

        /// <summary>Available to be drafted into the next dive group.</summary>
        internal bool ReadyToDive
        {
            get { return _state == State.Circle && Time.time >= _readyAt && _bird != null && !IsDead; }
        }

        private bool IsDead
        {
            get { return _bird == null || _bird._hp.Value <= 0; }
        }

        internal void Bind(SwarmDirector director, Bird bird)
        {
            _director = director;
            _bird = bird;
            _id = ++_nextId;
            _bobPhase = Random.Range(0f, Mathf.PI * 2f);
            _orbitDir = Random.Range(0, 2) == 0 ? -1 : 1;
            SetState(State.Approach);
        }

        /// <summary>
        /// Called from the BirdManager.SimulateBird prefix, i.e. inside the server's Update loop,
        /// with the same delta the vanilla simulation would have used.
        /// </summary>
        internal void SimulateServer(float dt)
        {
            if (_bird == null || _director == null || IsDead) return;
            if (dt <= 0f) return;

            _stateTime += dt;

            switch (_state)
            {
                case State.Approach: TickApproach(dt); break;
                case State.Circle: TickCircle(dt); break;
                case State.Climb: TickClimb(dt); break;
                case State.Hover: TickHover(dt); break;
                case State.Dive: TickDive(dt); break;
                case State.PeelOff: TickPeelOff(dt); break;
                case State.Flee: TickFlee(dt); break;
            }
        }

        /// <summary>
        /// Director drafts this bird into a dive group. Returns false if there is no clear line of
        /// attack on the target from anywhere (target is in cover); the bird then stays circling.
        /// </summary>
        internal bool BeginDive(Player target, float commitAt)
        {
            if (target == null) return false;

            Vector3? point = FindAttackPoint(target);
            if (!point.HasValue)
            {
                Diag.Debug("[gull " + _id + "] " + target.SteamName + " is in cover - no clear dive line.");
                _readyAt = Time.time + 1.5f;
                return false;
            }

            _target = target;
            _attackOffset = point.Value - Chest(target);
            _hoverEndTime = commitAt;
            _repositions = 0;
            _hitThisDive.Clear();
            SetState(State.Climb);
            return true;
        }

        /// <summary>Encounter over: stop attacking and leave, flying away from the party.</summary>
        internal void Flee()
        {
            Vector3 away = transform.position - _director.Anchor;
            away.y = 0f;
            if (away.sqrMagnitude < 1f) away = Random.insideUnitSphere;
            away.y = 0f;
            _fleeDir = away.normalized;
            _target = null;
            SetState(State.Flee);
        }

        private void SetState(State s)
        {
            _state = s;
            _stateTime = 0f;
            Diag.Debug("[gull " + _id + "] -> " + s);
        }

        // ------------------------------------------------------------------ states

        private void TickApproach(float dt)
        {
            SwarmConfig cfg = SeagullSwarmPlugin.Cfg;

            Vector3 pos = _bird.transform.position;
            Vector3 flat = pos - _director.Anchor;
            flat.y = 0f;
            float dist = flat.magnitude;
            float arrive = cfg.CircleRadius.Value + 3f;

            // Head for the near edge of the holding pattern, climbing from wave-top height up to
            // circling height over the last stretch so the flock is visible the whole way in.
            Vector3 dirIn = dist > 0.01f ? -flat / dist : Vector3.forward;
            Vector3 want = _director.Anchor - dirIn * cfg.CircleRadius.Value;
            float progress = Mathf.InverseLerp(cfg.ApproachDistance.Value, arrive, dist);
            float lowAlt = WaterY() + cfg.ApproachHeight.Value;
            float highAlt = _director.Anchor.y + cfg.CircleHeight.Value;
            want.y = Mathf.Lerp(lowAlt, highAlt, progress * progress);

            MoveToward(want, cfg.ApproachSpeed.Value, dt);
            _bird.SetAnimState(want.y > pos.y + 0.5f ? AnimFlappingUp : AnimSearching);

            if (dist <= arrive || _stateTime > 30f)
            {
                _orbitAngle = Mathf.Atan2(flat.z, flat.x);
                _readyAt = Time.time + Random.Range(0.5f, 2.5f);
                SetState(State.Circle);
            }
        }

        private void TickCircle(float dt)
        {
            SwarmConfig cfg = SeagullSwarmPlugin.Cfg;

            float radius = cfg.CircleRadius.Value;
            float speed = cfg.CircleSpeed.Value;

            // Advance along the orbit at a constant ground speed.
            _orbitAngle += _orbitDir * (speed / Mathf.Max(1f, radius)) * dt;

            Vector3 want = _director.Anchor
                           + new Vector3(Mathf.Cos(_orbitAngle) * radius, cfg.CircleHeight.Value,
                                         Mathf.Sin(_orbitAngle) * radius);

            MoveToward(want, speed, dt);
            _bird.SetAnimState(AnimSearching);
        }

        private void TickClimb(float dt)
        {
            if (!TargetValid()) { Rearm(); return; }

            SwarmConfig cfg = SeagullSwarmPlugin.Cfg;

            Vector3 point = Chest(_target) + _attackOffset;
            MoveToward(point, cfg.ClimbSpeed.Value, dt);
            _bird.SetAnimState(AnimFlappingUp);

            bool arrived = (point - _bird.transform.position).sqrMagnitude < 2.25f;
            if (arrived || _stateTime > 4f) SetState(State.Hover);
        }

        private void TickHover(float dt)
        {
            if (!TargetValid()) { Rearm(); return; }

            // Hold station over the attack point with a slow bob, facing the victim. The group shares
            // a commit time (staggered slightly per bird), so a flight pauses and commits together.
            Vector3 point = Chest(_target) + _attackOffset;
            point.y += Mathf.Sin(Time.time * 2.2f + _bobPhase) * 0.35f;
            MoveToward(point, SeagullSwarmPlugin.Cfg.ClimbSpeed.Value * 0.5f, dt);

            FaceTowards(Chest(_target) - _bird.transform.position, dt, 6f);
            _bird.SetAnimState(AnimFlapping);

            if (Time.time >= _hoverEndTime) TryLaunchDive();
        }

        /// <summary>
        /// Last check before committing: the target may have stepped into cover while we hovered.
        /// If so, line up from another side once; if that fails too, give up on this target.
        /// </summary>
        private void TryLaunchDive()
        {
            Vector3 p0 = _bird.transform.position;
            if (DivePathClear(p0, Chest(_target)))
            {
                LaunchDive();
                return;
            }

            if (_repositions < MaxRepositions)
            {
                Vector3? point = FindAttackPoint(_target);
                if (point.HasValue)
                {
                    _repositions++;
                    _attackOffset = point.Value - Chest(_target);
                    _hoverEndTime = Time.time; // dive as soon as we are there
                    Diag.Debug("[gull " + _id + "] line blocked - repositioning around " + _target.SteamName);
                    SetState(State.Climb);
                    return;
                }
            }

            Diag.Debug("[gull " + _id + "] " + _target.SteamName + " took cover - dive called off.");
            Rearm();
        }

        /// <summary>
        /// The Sturzflug. Solve for the launch velocity that puts the bird on the target in
        /// DiveDuration seconds under constant downward acceleration, then integrate it. The path
        /// is a true parabola: shallow at the top, accelerating hard into the strike, and it
        /// carries real momentum out the far side.
        /// </summary>
        private void LaunchDive()
        {
            SwarmConfig cfg = SeagullSwarmPlugin.Cfg;

            Vector3 p0 = _bird.transform.position;
            Vector3 aim = Chest(_target);

            _velocity = LaunchVelocity(p0, aim);
            _hitThisDive.Clear();
            _diveLaunched = true;
            _closestSqr = float.MaxValue;

            if (cfg.ScreamBeforeDive.Value)
            {
                try
                {
                    AudioManager.PlayRandomClipAt("Seagull_V", 1, 11, p0, false, AudioDistance.Long,
                                                  cfg.ScreamVolume.Value);
                }
                catch (System.Exception e) { Diag.Exception("Dive scream", e); }
            }

            Diag.Debug("[gull " + _id + "] DIVE at " + _target.SteamName + " from " +
                       (p0 - aim).magnitude.ToString("0.0") + "m, launch speed " +
                       _velocity.magnitude.ToString("0.0") + "m/s");

            SetState(State.Dive);
            _bird.SetAnimState(AnimDiving);
        }

        private void TickDive(float dt)
        {
            SwarmConfig cfg = SeagullSwarmPlugin.Cfg;

            Vector3 from = _bird.transform.position;
            _velocity += Vector3.down * cfg.DiveGravity.Value * dt;
            Vector3 to = from + _velocity * dt;

            if (Crashed(from, to)) return;

            // Waterline / deck: never go below the floor; pull up hard instead of plunging.
            bool floored = false;
            float floor = FloorY();
            if (to.y < floor)
            {
                to.y = floor;
                if (_velocity.y < 0f) _velocity.y = 0f;
                floored = true;
            }

            _bird.transform.position = to;
            FaceTowards(_velocity, dt, 12f);
            _bird.SetAnimState(AnimDiving);

            CheckContact(from, to);

            bool spent = _stateTime > cfg.DiveDuration.Value * cfg.DiveOvershootFactor.Value;
            if (spent || floored || _hitThisDive.Count > 0) SetState(State.PeelOff);
        }

        private void TickPeelOff(float dt)
        {
            SwarmConfig cfg = SeagullSwarmPlugin.Cfg;

            Vector3 from = _bird.transform.position;

            // Keep the momentum from the dive but bleed it off while hauling the nose up.
            _velocity += Vector3.up * cfg.PeelOffLift.Value * dt;
            _velocity = Vector3.Lerp(_velocity, _velocity.normalized * cfg.CircleSpeed.Value, dt * 1.5f);
            if (_velocity.y < 0f) _velocity.y = Mathf.Lerp(_velocity.y, 0f, dt * 6f);

            Vector3 to = from + _velocity * dt;
            if (Crashed(from, to)) return;

            float floor = FloorY();
            if (to.y < floor) { to.y = floor; if (_velocity.y < 0f) _velocity.y = 0f; }

            _bird.transform.position = to;
            FaceTowards(_velocity, dt, 8f);
            _bird.SetAnimState(AnimFlappingUp);

            // A bird still travelling fast can clip someone on the way out.
            CheckContact(from, to);

            if (_stateTime >= cfg.PeelOffSeconds.Value) Rearm();
        }

        private void TickFlee(float dt)
        {
            Vector3 pos = _bird.transform.position;
            Vector3 want = pos + _fleeDir * 30f + Vector3.up * 8f;
            MoveToward(want, SeagullSwarmPlugin.Cfg.ApproachSpeed.Value * 1.3f, dt);
            _bird.SetAnimState(AnimFlappingUp);
        }

        private void Rearm()
        {
            SwarmConfig cfg = SeagullSwarmPlugin.Cfg;

            if (_diveLaunched)
            {
                bool hit = _hitThisDive.Count > 0;
                _director.ReportDive(hit);
                if (!hit)
                    Diag.Debug("[gull " + _id + "] MISS, closest pass " +
                               (_closestSqr == float.MaxValue ? "n/a" : Mathf.Sqrt(_closestSqr).ToString("0.0") + "m") +
                               " (contact radius " + cfg.ContactRadius.Value + "m)");
                _diveLaunched = false;
            }

            _target = null;
            _readyAt = Time.time + Random.Range(cfg.DiveCooldownMin.Value, cfg.DiveCooldownMax.Value);

            Vector3 flat = _bird.transform.position - _director.Anchor;
            _orbitAngle = Mathf.Atan2(flat.z, flat.x);

            SetState(State.Circle);
        }

        // ------------------------------------------------------------------ cover & collisions

        private static int CoverMask()
        {
            int mask = GameInfo.LevelLayer.value;
            if (SeagullSwarmPlugin.Cfg.BoatIsSolid.Value) mask |= GameInfo.BoatLayer.value;
            return mask;
        }

        /// <summary>
        /// Pick a hover point with a clear ballistic line onto the target, preferring the bird's own
        /// side. With your back to a cliff, the only clear lines are in front of you, so that is
        /// where the birds line up.
        /// </summary>
        private Vector3? FindAttackPoint(Player target)
        {
            SwarmConfig cfg = SeagullSwarmPlugin.Cfg;

            Vector3 chest = Chest(target);
            float altitude = _director.Anchor.y + cfg.CircleHeight.Value + cfg.ClimbHeight.Value;
            altitude = Mathf.Max(altitude, chest.y + 8f);

            Vector3 flat = _bird.transform.position - chest;
            flat.y = 0f;
            float horiz = Mathf.Clamp(flat.magnitude, cfg.AttackMinDistance.Value, cfg.AttackMaxDistance.Value);
            float baseAngle = flat.sqrMagnitude > 0.01f ? Mathf.Atan2(flat.z, flat.x) : Random.Range(0f, Mathf.PI * 2f);

            int mask = CoverMask();

            // 0, +30, -30, +60, -60 ... 180 degrees: nearest clear side wins.
            for (int k = 0; k < 12; k++)
            {
                int step = (k + 1) / 2;
                float sign = (k % 2 == 1) ? 1f : -1f;
                float ang = baseAngle + sign * step * (Mathf.PI / 6f);

                Vector3 p = new Vector3(chest.x + Mathf.Cos(ang) * horiz, altitude, chest.z + Mathf.Sin(ang) * horiz);

                if (Physics.CheckSphere(p, 0.6f, mask, QueryTriggerInteraction.Ignore)) continue;
                if (!DivePathClear(p, chest)) continue;
                return p;
            }
            return null;
        }

        /// <summary>Raycast along the actual parabola the dive would follow, not just the chord.</summary>
        private bool DivePathClear(Vector3 p0, Vector3 aim)
        {
            SwarmConfig cfg = SeagullSwarmPlugin.Cfg;

            float t = Mathf.Max(0.2f, cfg.DiveDuration.Value);
            Vector3 g = Vector3.down * cfg.DiveGravity.Value;
            Vector3 v0 = LaunchVelocity(p0, aim);
            int mask = CoverMask();

            Vector3 prev = p0;
            for (int i = 1; i <= PathSamples; i++)
            {
                // Stop just short of the target so the deck or ground under their feet is not a "wall".
                float ti = t * 0.95f * i / PathSamples;
                Vector3 p = p0 + v0 * ti + 0.5f * g * ti * ti;
                if (Physics.Linecast(prev, p, mask, QueryTriggerInteraction.Ignore)) return false;
                prev = p;
            }
            return true;
        }

        private static Vector3 LaunchVelocity(Vector3 p0, Vector3 aim)
        {
            SwarmConfig cfg = SeagullSwarmPlugin.Cfg;
            float t = Mathf.Max(0.2f, cfg.DiveDuration.Value);
            Vector3 g = Vector3.down * cfg.DiveGravity.Value;
            // p(t) = p0 + v0*t + 0.5*g*t^2  =>  v0 = (aim - p0 - 0.5*g*t^2) / t
            return (aim - p0 - 0.5f * g * t * t) / t;
        }

        /// <summary>Mid-dive collision with rock, walls or the boat: the bird dies on impact.</summary>
        private bool Crashed(Vector3 from, Vector3 to)
        {
            if (!SeagullSwarmPlugin.Cfg.CrashKillsBird.Value) return false;

            RaycastHit hit;
            if (!Physics.Linecast(from, to, out hit, CoverMask(), QueryTriggerInteraction.Ignore)) return false;

            Vector3 dir = (to - from).normalized;
            _bird.transform.position = hit.point - dir * 0.3f;

            Diag.Info("CRASH gull " + _id + " into '" + hit.collider.name + "' (" +
                      LayerMask.LayerToName(hit.collider.gameObject.layer) + ") during " + _state + ".");

            if (_diveLaunched)
            {
                _director.ReportDive(_hitThisDive.Count > 0);
                _diveLaunched = false;
            }
            _director.ReportCrash();

            // Writing the synced HP kills it on every client: corpse drops, death squawk plays.
            _bird._hp.Value = 0;
            return true;
        }

        /// <summary>
        /// Damage on contact. Swept against the frame's movement segment so a fast dive cannot
        /// tunnel past a player between frames, and blocked if cover is in between.
        /// </summary>
        private void CheckContact(Vector3 from, Vector3 to)
        {
            SwarmConfig cfg = SeagullSwarmPlugin.Cfg;
            float r = cfg.ContactRadius.Value;
            float rSqr = r * r;
            int mask = CoverMask();

            List<Player> alive = PlayerManager.AlivePlayers;
            for (int i = 0; i < alive.Count; i++)
            {
                Player p = alive[i];
                if (p == null || p.Dying.IsDead) continue;
                if (_hitThisDive.Contains(p)) continue;

                Vector3 chest = Chest(p);
                float dSqr = SqrDistanceToSegment(chest, from, to);
                if (dSqr < _closestSqr) _closestSqr = dSqr;
                if (dSqr > rSqr) continue;

                if (Physics.Linecast(to, chest, mask, QueryTriggerInteraction.Ignore))
                {
                    Diag.Debug("[gull " + _id + "] contact with " + p.SteamName + " blocked by cover.");
                    continue;
                }

                _hitThisDive.Add(p);

                Vector3 dir = (to - from).normalized;
                if (dir == Vector3.zero) dir = Vector3.down;

                int damage = cfg.ContactDamage.Value;
                Vector3 force = dir * cfg.ContactKnockback.Value;

                // Same two calls Server.HitPlayer makes server-side. TakeDamage writes the synced
                // health and handles death; ObserverHit is what actually plays blood and sound on
                // every client, so both are required.
                int hpBefore = p.Vitals.Health;
                ApplyingHit = true;
                try
                {
                    p.Vitals.TakeDamage(damage, chest, force);
                }
                finally
                {
                    ApplyingHit = false;
                }
                p.Vitals.ObserverHit(null, chest, dir, damage, DamageType.Bite);
                int hpAfter = p.Vitals.Health;

                Diag.Info("HIT " + p.SteamName + " for " + damage + " (gull " + _id + ", " + _state + "): hp " +
                          hpBefore + " -> " + hpAfter + (hpAfter == hpBefore ? " [damage blocked by game]" : ""));
            }
        }

        // ------------------------------------------------------------------ helpers

        private bool TargetValid()
        {
            return _target != null && !_target.Dying.IsDead;
        }

        private static Vector3 Chest(Player p)
        {
            return p.Transform.position + Vector3.up * 0.9f;
        }

        internal static float WaterY()
        {
            try { return WaterManager.WaterHeight; }
            catch { return 0f; }
        }

        /// <summary>Lowest a bird may fly: the waterline, or the feet of its target if higher (deck, cliff).</summary>
        private float FloorY()
        {
            float margin = SeagullSwarmPlugin.Cfg.WaterMargin.Value;
            float floor = WaterY() + margin;
            if (_target != null) floor = Mathf.Max(floor, _target.Transform.position.y + margin * 0.5f);
            return floor;
        }

        private static float SqrDistanceToSegment(Vector3 point, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float lenSqr = ab.sqrMagnitude;
            if (lenSqr < 1e-6f) return (point - a).sqrMagnitude;

            float t = Mathf.Clamp01(Vector3.Dot(point - a, ab) / lenSqr);
            return (point - (a + ab * t)).sqrMagnitude;
        }

        /// <summary>
        /// Cruise movement for every non-dive state. Slides up over obstacles instead of flying
        /// through them, and never drops below the waterline.
        /// </summary>
        private void MoveToward(Vector3 want, float speed, float dt)
        {
            Vector3 pos = _bird.transform.position;
            Vector3 delta = want - pos;

            if (delta.sqrMagnitude > 1e-4f)
            {
                Vector3 step = Vector3.ClampMagnitude(delta, speed * dt);
                Vector3 next = pos + step;

                if (Physics.Linecast(pos, next + step.normalized * 1.5f, CoverMask(), QueryTriggerInteraction.Ignore))
                    next = pos + Vector3.up * speed * dt;

                float minY = WaterY() + SeagullSwarmPlugin.Cfg.WaterMargin.Value;
                if (next.y < minY) next.y = minY;

                _bird.transform.position = next;
                FaceTowards(delta, dt, SeagullSwarmPlugin.Cfg.TurnSpeed.Value);
            }

            _bird.SetSpeed(speed);
        }

        private void FaceTowards(Vector3 dir, float dt, float turnSpeed)
        {
            if (dir.sqrMagnitude < 1e-4f) return;
            _bird.transform.forward =
                Vector3.Slerp(_bird.transform.forward, dir.normalized, Mathf.Clamp01(dt * turnSpeed));
        }
    }
}
