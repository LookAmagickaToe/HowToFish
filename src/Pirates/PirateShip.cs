using System;
using FishNet;
using UnityEngine;

namespace Expanded.Pirates
{
    /// <summary>
    /// The enemy ship. Sits on a runtime-built networked prefab, so it exists on every peer; only
    /// the host steers, and FishNet's NetworkTransform replicates the result.
    ///
    /// Sailing model: hold station at a broadside distance from the party, circling rather than
    /// ramming, so the fight reads as a naval duel instead of a chase. The ship stays on the water
    /// surface and steers away from land rather than beaching itself.
    /// </summary>
    internal sealed class PirateShip : MonoBehaviour
    {
        internal enum Stance { Closing, Broadside, Withdrawing }

        internal static PirateShip Active { get; private set; }

        private Stance _stance = Stance.Closing;
        private float _stanceTime;
        private int _circleDir = 1;
        private float _bobPhase;
        private float _speed;
        private bool _aground;

        internal Stance CurrentStance => _stance;
        internal float Health { get; private set; }
        internal float MaxHealth { get; private set; }
        internal bool Alive => Health > 0f;

        private PirateConfig Cfg => PirateModule.Cfg;

        private void Awake()
        {
            _bobPhase = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
            _circleDir = UnityEngine.Random.Range(0, 2) == 0 ? -1 : 1;
        }

        private void OnEnable()
        {
            // Only the host's instance is the authoritative one; clients hold a replicated copy.
            if (InstanceFinder.IsServerStarted) Active = this;
        }

        private void OnDestroy()
        {
            if (Active == this) Active = null;
        }

        internal void ServerInitialise(float maxHealth)
        {
            MaxHealth = Mathf.Max(1f, maxHealth);
            Health = MaxHealth;
            Active = this;
        }

        /// <summary>Host-side damage. Returns true if this hit sank her.</summary>
        internal bool ServerDamage(float amount, string source)
        {
            if (!InstanceFinder.IsServerStarted || !Alive) return false;

            Health = Mathf.Max(0f, Health - Mathf.Abs(amount));
            Diag.Info("Pirate ship took " + amount.ToString("0") + " from " + source +
                      " -> " + Health.ToString("0") + "/" + MaxHealth.ToString("0"));

            if (Health > 0f) return false;

            Diag.Info("Pirate ship destroyed.");
            return true;
        }

        private void Update()
        {
            // Clients do nothing: their copy is driven entirely by the replicated transform.
            if (!InstanceFinder.IsServerStarted) return;

            try
            {
                float dt = Time.deltaTime;
                if (dt <= 0f) return;
                Sail(dt);
            }
            catch (Exception e)
            {
                Diag.Exception("PirateShip.Update", e);
            }
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
                    // Periodically swap sides so both broadsides get used.
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
            Diag.Debug("Pirate ship stance -> " + s);
        }

        private Vector3 DesiredHeading(Vector3 toTargetDir, float distance)
        {
            switch (_stance)
            {
                case Stance.Closing:
                    return toTargetDir;

                case Stance.Withdrawing:
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

            if (!Physics.Raycast(eye, heading, probe, mask, QueryTriggerInteraction.Ignore))
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
                if (!Physics.Raycast(eye, left, probe, mask, QueryTriggerInteraction.Ignore)) return left;

                Vector3 right = Quaternion.Euler(0f, angle, 0f) * heading;
                if (!Physics.Raycast(eye, right, probe, mask, QueryTriggerInteraction.Ignore)) return right;
            }

            // Boxed in: reverse course.
            return -heading;
        }

        private float SurfaceY()
        {
            float water = 0f;
            try { water = WaterManager.WaterHeight; } catch { /* not ready */ }
            return water + Cfg.Draft.Value + Mathf.Sin(Time.time * 0.6f + _bobPhase) * Cfg.BobHeight.Value;
        }

        /// <summary>Gentle roll, exaggerated while turning, so she looks like she has weight.</summary>
        private void ApplyRoll(float dt)
        {
            float wave = Mathf.Sin(Time.time * 0.8f + _bobPhase) * Cfg.RollDegrees.Value;
            float turnRoll = _stance == Stance.Broadside ? _circleDir * Cfg.RollDegrees.Value * 0.5f : 0f;

            Vector3 e = transform.eulerAngles;
            Quaternion wanted = Quaternion.Euler(0f, e.y, wave + turnRoll);
            transform.rotation = Quaternion.Slerp(transform.rotation, wanted, dt * 2f);
        }
    }
}
