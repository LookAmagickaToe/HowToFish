using System;
using System.Reflection;
using Expanded.Pirates;
using FishNet;
using HarmonyLib;
using UnityEngine;

namespace Expanded.Megalodon
{
    /// <summary>
    /// What the fight does to the boat, host-side: a bitten stern damages the engine (drag, smoke) and
    /// may stall it outright - the driver mashes Space to yank the pull cord. The driver drops barrel
    /// mines off the stern with G. And when you play alone, Old Salt takes the helm so you can ride:
    /// he means well, he steers mostly away from rocks, and he cannot see without his teeth.
    /// </summary>
    internal static class MegaBoat
    {
        private static readonly FieldInfo FCurMotor = AccessTools.Field(typeof(Boat), "_curMotor");

        private static MegaConfig Cfg => MegaModule.Cfg;

        // Host state.
        internal static bool Stalled { get; private set; }
        internal static bool Autopilot { get; private set; }
        private static int _pulls;
        private static float _damagedUntil;
        private static int _mines;
        private static float _nextRestock;

        // What every client knows (from MegaStatus), for the HUD and the smoke.
        internal static bool SeenStalled, SeenAutopilot;
        internal static int SeenPulls, SeenNeeded, SeenMines;
        internal static float SeenDamagedUntil;

        // Old Salt at the helm (host-only visual: autopilot only runs in single-player).
        private static GameObject _salt;
        private static Boat _saltBoat;
        private static float _nextSaltLine, _nextSwerve, _swerveUntil, _swerveDir, _nextBrake, _brakeUntil;
        private static float _cmdSteer, _cmdUntil, _fasterUntil;
        private static byte _pendingCmd;
        private static float _pendingAt = -1f;
        private static float _nextAutoPull;
        private static float _nextSmoke;

        // ------------------------------------------------------------------ networking

        internal static void RegisterHandlers()
        {
            ModNet.OnServer(Msg.DropMine, (conn, r) =>
            {
                Player p = MegaModule.PlayerFor(conn);
                if (p == null || Tow.Boat == null || Tow.Boat.Driver != p) return;
                if (!SharkBrain.Active || _mines <= 0) return;
                Vector3 stern, f;
                if (!BoatMount.TryGetWorld(BoatMount.Slot.Stern, out stern, out f)) return;
                _mines--;
                MegaHazards.HostDropMine(stern + f * 2.2f);
                Shouts.HostBroadcast(p, MegaLines.Pick(MegaLines.MineAway));
                SharkBrain.BroadcastStatus();
            });

            ModNet.OnServer(Msg.CordPull, (conn, r) =>
            {
                Player p = MegaModule.PlayerFor(conn);
                if (p == null || !Stalled || Tow.Boat == null || Tow.Boat.Driver != p) return;
                Pull(p);
            });

            ModNet.OnServer(Msg.SoloCommand, (conn, r) =>
            {
                byte cmd = r.ReadByte();
                if (!Autopilot) return;
                _pendingCmd = cmd;
                _pendingAt = Time.time + 0.6f;
            });
        }

        internal static void WriteStatus(System.IO.BinaryWriter w)
        {
            w.Write(Stalled);
            w.Write((byte)Mathf.Clamp(_pulls, 0, 255));
            w.Write((byte)Mathf.Clamp(Cfg.CordPullsNeeded.Value, 1, 255));
            w.Write(Mathf.Max(0f, _damagedUntil - Time.time));
            w.Write((byte)Mathf.Clamp(_mines, 0, 255));
            w.Write(Autopilot);
        }

        internal static void ReadStatus(System.IO.BinaryReader r)
        {
            SeenStalled = r.ReadBoolean();
            SeenPulls = r.ReadByte();
            SeenNeeded = r.ReadByte();
            SeenDamagedUntil = Time.time + r.ReadSingle();
            SeenMines = r.ReadByte();
            SeenAutopilot = r.ReadBoolean();
        }

        // ------------------------------------------------------------------ host

        internal static void HostTick()
        {
            float now = Time.time;
            int stock = Mathf.Max(0, Cfg.MineStock.Value);
            if (!SharkBrain.Active) { _mines = stock; _nextRestock = now + Cfg.MineRestockSeconds.Value; }
            else if (_mines < stock && now >= _nextRestock)
            {
                _mines++;
                _nextRestock = now + Cfg.MineRestockSeconds.Value;
                SharkBrain.BroadcastStatus();
            }

            bool auto = Cfg.SoloAutopilot.Value && PlayerManager.Players.Count == 1 && WakeRig.RiderId >= 0 &&
                        Tow.Boat != null && Tow.Boat.Driver == null;
            if (auto != Autopilot)
            {
                Autopilot = auto;
                SharkBrain.BroadcastStatus();
                if (auto) MegaModule.Announce("Old Salt climbs aboard and grabs the helm. \"I'll drive! ...Which one's forward?\"");
            }
            TickSalt(now);

            if (Autopilot && Stalled && now >= _nextAutoPull)
            {
                _nextAutoPull = now + 0.55f;
                Pull(null);
            }

            if (_pendingAt > 0f && now >= _pendingAt)
            {
                _pendingAt = -1f;
                Obey(_pendingCmd);
            }
        }

        private static void Pull(Player who)
        {
            _pulls++;
            if (_pulls >= Mathf.Max(1, Cfg.CordPullsNeeded.Value))
            {
                Stalled = false;
                _pulls = 0;
                MegaModule.Announce("VROOOM - the engine roars back to life!");
            }
            SharkBrain.BroadcastStatus();
        }

        internal static void HostDamageEngine()
        {
            _damagedUntil = Time.time + Cfg.EngineDamageSeconds.Value;
            if (!Stalled && UnityEngine.Random.value < Cfg.StallChance.Value)
            {
                Stalled = true;
                _pulls = 0;
                _nextAutoPull = Time.time + 1.2f;
                MegaModule.Announce("The engine STALLED! Driver: mash SPACE to yank the pull cord!");
                Player driver = Tow.Boat != null ? Tow.Boat.Driver : null;
                if (driver != null) Shouts.HostBroadcast(driver, MegaLines.Pick(MegaLines.Stalled));
            }
            SharkBrain.BroadcastStatus();
        }

        internal static void HostReset()
        {
            Stalled = false;
            _pulls = 0;
            _damagedUntil = 0f;
            SharkBrain.BroadcastStatus();
        }

        /// <summary>Host, inside the boat's physics step: engine drag and Old Salt's driving.</summary>
        internal static void ServerFixed(Boat b)
        {
            if (b == null || !InstanceFinder.IsServerStarted) return;
            Rigidbody rig = b.HiddenPhysicsRig;
            if (rig == null) return;

            if (Time.time < _damagedUntil)
            {
                Vector3 v = rig.linearVelocity;
                v.y = 0f;
                rig.AddForce(-v * Cfg.EngineDamageDrag.Value, ForceMode.Acceleration);
            }

            if (Autopilot) Drive(b, rig);
        }

        private static void Drive(Boat b, Rigidbody rig)
        {
            var motor = FCurMotor?.GetValue(b) as BoatMotor;
            if (motor == null || motor.Propeller == null) return;
            float now = Time.time;

            float throttle = Mathf.Clamp01(Cfg.AutopilotThrottle.Value);
            if (now < _fasterUntil) throttle = 1f;
            if (now < _brakeUntil) throttle = 0.05f;
            if (Stalled) throttle = 0f;
            if (throttle > 0f && WaterManager.IsUnderWater(motor.Propeller.position))
                rig.AddForceAtPosition(-motor.Propeller.right * (motor.Force * throttle), motor.Propeller.position);

            Vector3 fwd = Tow.Forward();
            Vector3 pos = Tow.Centre();
            float steer = (Mathf.PerlinNoise(now * 0.12f, 3.3f) - 0.5f) * 1.1f;

            // Out to sea first: that's where the teeth are.
            if (Tow.DistanceFromMooring() < Cfg.TriggerDistance.Value + 35f)
            {
                Vector3 away = pos - SpawnManager.BoatSpawnPos;
                away.y = 0f;
                if (away.sqrMagnitude > 1f) steer += Mathf.Clamp(Vector3.SignedAngle(fwd, away.normalized, Vector3.up) / 45f, -1f, 1f);
            }

            if (now >= _nextSwerve)
            {
                _nextSwerve = now + UnityEngine.Random.Range(7f, 13f);
                _swerveUntil = now + 1.1f;
                _swerveDir = UnityEngine.Random.value < 0.5f ? -1f : 1f;
            }
            if (now < _swerveUntil) steer += _swerveDir * 1.3f;
            if (now < _cmdUntil) steer += _cmdSteer * 1.7f;

            if (now >= _nextBrake)
            {
                _nextBrake = now + UnityEngine.Random.Range(25f, 45f);
                _brakeUntil = now + 1.4f;
                SaltSays(MegaLines.Pick(MegaLines.SaltBrake));
            }

            // Rocks: he can't see them, but he can hear the surf. Mostly.
            try
            {
                int mask = GameInfo.LevelLayer.value;
                Vector3 eye = pos + Vector3.up * 1.2f;
                RaycastHit hit;
                if (Physics.Raycast(eye, fwd, out hit, 55f, mask, QueryTriggerInteraction.Ignore))
                {
                    bool leftClear = !Physics.Raycast(eye, Quaternion.AngleAxis(-40f, Vector3.up) * fwd, 55f, mask, QueryTriggerInteraction.Ignore);
                    steer += (leftClear ? -1f : 1f) * 3f * (1f - hit.distance / 55f);
                }
            }
            catch { }

            steer = Mathf.Clamp(steer, -2.5f, 2.5f);
            rig.AddTorque(Vector3.up * steer * Cfg.AutopilotTurn.Value, ForceMode.Acceleration);
        }

        /// <summary>The rider yelled a direction. Old Salt does his best. His best varies.</summary>
        private static void Obey(byte cmd)
        {
            float roll = UnityEngine.Random.value;
            if (roll < 0.1f) { SaltSays(MegaLines.Pick(MegaLines.SaltIgnore)); return; }
            bool wrong = roll < 0.3f;
            switch (cmd)
            {
                case 1:
                case 2:
                    float dir = cmd == 1 ? -1f : 1f;
                    _cmdSteer = wrong ? -dir : dir;
                    _cmdUntil = Time.time + 1.6f;
                    SaltSays(wrong ? MegaLines.Pick(MegaLines.SaltWrongWay) : (cmd == 1 ? "LEFT! Aye!" : "RIGHT! Aye!"));
                    break;
                case 3:
                    _fasterUntil = Time.time + 4f;
                    SaltSays("FASTER? I'll give you faster!");
                    break;
            }
        }

        private static void TickSalt(float now)
        {
            Boat boat = Tow.Boat;
            bool want = Autopilot && boat != null && ModCharacters.Available;
            if (!want)
            {
                if (_salt != null) { UnityEngine.Object.Destroy(_salt); _salt = null; }
                return;
            }
            if (_salt == null || _saltBoat != boat)
            {
                if (_salt != null) UnityEngine.Object.Destroy(_salt);
                _salt = BuildSalt(boat);
                _saltBoat = boat;
                _nextSaltLine = now + 3f;
            }
            if (_salt != null && now >= _nextSaltLine)
            {
                _nextSaltLine = now + UnityEngine.Random.Range(9f, 15f);
                SaltSays(MegaLines.Pick(MegaLines.SaltDriving));
            }
        }

        private static GameObject BuildSalt(Boat boat)
        {
            try
            {
                Transform frame = BoatMount.Frame(boat);
                if (frame == null || boat.DriverPos == null) return null;
                Vector3 seat = frame.InverseTransformPoint(boat.DriverPos.position);
                float deck;
                if (BoatMount.TryDeckHeight(seat, out deck)) seat.y = deck;
                Vector3 fwd = frame.InverseTransformDirection(boat.DriverPos.forward);
                fwd.y = 0f;

                GameObject go = ModCharacters.Create("characters_henry", Vector3.zero, Quaternion.identity, frame);
                if (go == null) return null;
                go.name = BoatMount.MountRoot + "OldSaltAtHelm";
                go.transform.localPosition = seat;
                go.transform.localRotation = Quaternion.LookRotation(fwd.sqrMagnitude > 1e-3f ? fwd.normalized : Vector3.forward);
                Bounds b = ModAssets.Measure(go);
                if (b.size.y > 0.1f) go.transform.localScale *= 1.7f / b.size.y;
                ModCharacters.Play(go, "Idle");
                return go;
            }
            catch (Exception e)
            {
                Diag.Exception("MegaBoat.BuildSalt", e);
                return null;
            }
        }

        private static void SaltSays(string line)
        {
            if (_salt != null) Shouts.Say(_salt.transform, Vector3.up * 2.1f, line, 3.5f);
        }

        // ------------------------------------------------------------------ every client

        internal static void ClientTick()
        {
            // A damaged engine smokes.
            if (Time.time < SeenDamagedUntil && Time.time >= _nextSmoke && Tow.Boat != null)
            {
                _nextSmoke = Time.time + 0.35f;
                try
                {
                    var motor = FCurMotor?.GetValue(Tow.Boat) as BoatMotor;
                    if (motor != null) ParticleManager.Play("Smoke", motor.transform.position + Vector3.up * 0.4f);
                }
                catch { }
            }
        }

        internal static void Clear()
        {
            if (_salt != null) UnityEngine.Object.Destroy(_salt);
            _salt = null;
            _saltBoat = null;
            Stalled = false;
            Autopilot = false;
            _pulls = 0;
            _damagedUntil = 0f;
            SeenStalled = SeenAutopilot = false;
            SeenDamagedUntil = 0f;
            _pendingAt = -1f;
        }
    }
}
