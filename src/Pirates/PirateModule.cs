using System;
using BepInEx.Configuration;
using FishNet;
using FishNet.Component.Transforming;
using FishNet.Managing;
using FishNet.Managing.Object;
using FishNet.Object;
using UnityEngine;

namespace Expanded.Pirates
{
    internal sealed class PirateConfig
    {
        public readonly ConfigEntry<string> Model;
        public readonly ConfigEntry<float> Scale;
        public readonly ConfigEntry<float> Health;

        public readonly ConfigEntry<float> Speed;
        public readonly ConfigEntry<float> Acceleration;
        public readonly ConfigEntry<float> TurnRate;
        public readonly ConfigEntry<float> StandoffDistance;
        public readonly ConfigEntry<float> CircleSwapSeconds;
        public readonly ConfigEntry<float> LandProbeDistance;
        public readonly ConfigEntry<float> SpawnDistance;

        public readonly ConfigEntry<float> Draft;
        public readonly ConfigEntry<float> BobHeight;
        public readonly ConfigEntry<float> RollDegrees;

        public PirateConfig(ConfigFile c)
        {
            const string S = "Pirates";
            Model = c.Bind(S, "Model", "ship-pirate-large",
                "Model from the kit bundle used for the enemy ship.");
            Scale = c.Bind(S, "Scale", 1f, "Extra scaling on top of the model's own size.");
            Health = c.Bind(S, "Health", 400f, "How much damage the ship takes before sinking.");

            Speed = c.Bind(S, "Speed", 7f, "Cruising speed in m/s.");
            Acceleration = c.Bind(S, "Acceleration", 3f, "How quickly she reaches that speed.");
            TurnRate = c.Bind(S, "TurnRate", 0.6f,
                "Steering rate. Low values make her turn like a heavy ship, which is the point.");
            StandoffDistance = c.Bind(S, "StandoffDistance", 35f,
                "Distance she tries to hold from the party while presenting a broadside.");
            CircleSwapSeconds = c.Bind(S, "CircleSwapSeconds", 20f,
                "How often she comes about to fire the other broadside.");
            LandProbeDistance = c.Bind(S, "LandProbeDistance", 18f,
                "How far ahead she looks for land before steering away.");
            SpawnDistance = c.Bind(S, "SpawnDistance", 90f, "How far out she appears.");

            Draft = c.Bind(S, "Draft", 0f, "Vertical offset from the waterline; raise if she sits too low.");
            BobHeight = c.Bind(S, "BobHeight", 0.25f, "Bobbing amplitude in metres.");
            RollDegrees = c.Bind(S, "RollDegrees", 4f, "How far she rolls with the swell.");
        }
    }

    /// <summary>
    /// The pirate encounter.
    ///
    /// The ship is a networked object the mod defines itself: an empty object carrying FishNet's own
    /// NetworkObject and NetworkTransform plus the kit model. Both components ship compiled inside
    /// FishNet, so no network code has to be generated for the mod - which is impossible for a mod
    /// assembly. The host steers; everyone else receives the transform.
    ///
    /// Because the prefab is registered at runtime, every player must run the same mod version: the
    /// two sides identify objects by registration order.
    /// </summary>
    internal sealed class PirateModule : ModuleBase
    {
        /// <summary>Arbitrary but fixed: keeps our prefab ids away from the game's own collection.</summary>
        private const ushort CollectionId = 1101;

        internal static PirateModule Instance { get; private set; }
        internal static PirateConfig Cfg { get; private set; }

        internal override string Id => "Pirates";
        internal override string DisplayName => "Pirate Raid";
        internal override KeyCode DefaultDebugKey => KeyCode.F4;

        private GameObject _prefabRoot;      // inactive template, never rendered
        private NetworkObject _prefab;
        private NetworkManager _registeredWith;
        private GameObject _spawned;

        internal override void Configure(ConfigFile config)
        {
            Instance = this;
            Cfg = new PirateConfig(config);
        }

        internal override void OnEnable() => Instance = this;

        // ------------------------------------------------------------------ session

        internal override void OnSessionStart(bool asServer)
        {
            // Both roles must register, in the same order, before anything is spawned.
            EnsurePrefabRegistered();
        }

        internal override void OnSessionEnd()
        {
            DespawnShip("session ended");
            _registeredWith = null;
        }

        // ------------------------------------------------------------------ prefab

        /// <summary>
        /// Builds the template once and registers it with the running NetworkManager. Rebuilding it
        /// per session would hand out different prefab ids to different players.
        /// </summary>
        private bool EnsurePrefabRegistered()
        {
            NetworkManager nm = InstanceFinder.NetworkManager;
            if (nm == null)
            {
                Diag.Warn("Pirates: no NetworkManager; cannot register the ship.");
                return false;
            }
            if (_registeredWith == nm && _prefab != null) return true;

            if (_prefab == null && !BuildPrefab()) return false;

            try
            {
                // Base type on purpose: whichever concrete collection FishNet creates, AddObject is
                // declared here, so we never depend on it being a SinglePrefabObjects.
                PrefabObjects collection = nm.GetPrefabObjects<SinglePrefabObjects>(CollectionId, true);
                if (collection == null)
                {
                    Diag.Error("Pirates: could not obtain prefab collection " + CollectionId + ".");
                    return false;
                }

                // checkForDuplicates guards against re-registering across sessions.
                collection.AddObject(_prefab, true, true);
                _registeredWith = nm;

                Diag.Info("Pirates: ship registered in collection " + CollectionId + " as prefab id " +
                          _prefab.PrefabId + " (" + collection.GetObjectCount() + " object(s) in collection).");
                return true;
            }
            catch (Exception e)
            {
                Diag.Exception("Pirates.EnsurePrefabRegistered", e);
                return false;
            }
        }

        private bool BuildPrefab()
        {
            if (!ModAssets.Available)
            {
                ModAssets.Load();
                if (!ModAssets.Available)
                {
                    Diag.Error("Pirates: model bundle missing, cannot build the ship.");
                    return false;
                }
            }

            try
            {
                // Created inactive so Unity never runs Awake on the template and FishNet never
                // mistakes it for a scene object.
                _prefabRoot = new GameObject("ExpandedPirateShip");
                _prefabRoot.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(_prefabRoot);

                GameObject hull = ModAssets.Create(Cfg.Model.Value, Vector3.zero, Quaternion.identity,
                                                   _prefabRoot.transform);
                if (hull == null)
                {
                    Diag.Error("Pirates: model '" + Cfg.Model.Value + "' missing from the bundle.");
                    UnityEngine.Object.Destroy(_prefabRoot);
                    _prefabRoot = null;
                    return false;
                }
                hull.transform.localPosition = Vector3.zero;
                if (!Mathf.Approximately(Cfg.Scale.Value, 1f))
                    _prefabRoot.transform.localScale = Vector3.one * Cfg.Scale.Value;

                NetworkObject nob = _prefabRoot.AddComponent<NetworkObject>();

                NetworkTransform nt = _prefabRoot.AddComponent<NetworkTransform>();
                nt.SetSynchronizePosition(true);
                nt.SetSynchronizeRotation(true);
                nt.SetSynchronizeScale(false);   // scale never changes; skip the bandwidth

                _prefabRoot.AddComponent<PirateShip>();
                _prefab = nob;

                Bounds b = ModAssets.Measure(hull);
                Diag.Info("Pirates: ship template built from '" + Cfg.Model.Value + "', size " +
                          b.size.x.ToString("0.0") + " x " + b.size.y.ToString("0.0") + " x " +
                          b.size.z.ToString("0.0") + " m.");
                return true;
            }
            catch (Exception e)
            {
                Diag.Exception("Pirates.BuildPrefab", e);
                return false;
            }
        }

        // ------------------------------------------------------------------ spawning

        internal bool SpawnShip()
        {
            if (!InstanceFinder.IsServerStarted)
            {
                Diag.Warn("Pirates: only the host can send in the pirates.");
                return false;
            }
            if (_spawned != null)
            {
                Diag.Info("Pirates: a ship is already out there.");
                return false;
            }
            if (!EnsurePrefabRegistered()) return false;

            try
            {
                Vector3 party = PartyPosition();
                float angle = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                Vector3 offset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * Cfg.SpawnDistance.Value;

                Vector3 pos = party + offset;
                pos.y = WaterY() + Cfg.Draft.Value;

                Vector3 look = party - pos;
                look.y = 0f;
                Quaternion rot = look.sqrMagnitude > 0.01f
                    ? Quaternion.LookRotation(look.normalized)
                    : Quaternion.identity;

                GameObject ship = UnityEngine.Object.Instantiate(_prefabRoot, pos, rot);
                ship.name = "PirateShip";
                ship.SetActive(true);

                PirateShip brain = ship.GetComponent<PirateShip>();
                brain?.ServerInitialise(Cfg.Health.Value);

                InstanceFinder.ServerManager.Spawn(ship);
                _spawned = ship;

                Diag.Info("Pirates: ship spawned " + Cfg.SpawnDistance.Value.ToString("0") + "m from the party at " +
                          pos.ToString("F1") + ".");
                Announce("A ship flying no colours is closing from the horizon.");
                return true;
            }
            catch (Exception e)
            {
                Diag.Exception("Pirates.SpawnShip", e);
                return false;
            }
        }

        internal void DespawnShip(string reason)
        {
            if (_spawned == null) return;
            try
            {
                if (InstanceFinder.IsServerStarted && InstanceFinder.ServerManager != null)
                {
                    NetworkObject nob = _spawned.GetComponent<NetworkObject>();
                    if (nob != null && nob.IsSpawned) InstanceFinder.ServerManager.Despawn(nob);
                    else UnityEngine.Object.Destroy(_spawned);
                }
                else
                {
                    UnityEngine.Object.Destroy(_spawned);
                }
                Diag.Info("Pirates: ship removed (" + reason + ").");
            }
            catch (Exception e)
            {
                Diag.Exception("Pirates.DespawnShip", e);
            }
            _spawned = null;
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Where the fight centres: the boat if there is one, else the living players.</summary>
        internal static Vector3 PartyPosition()
        {
            try
            {
                if (BoatManager.Boat != null) return BoatManager.Boat.transform.position;
            }
            catch { /* boat not ready */ }

            var alive = PlayerManager.AlivePlayers;
            if (alive != null && alive.Count > 0)
            {
                Vector3 sum = Vector3.zero;
                int n = 0;
                for (int i = 0; i < alive.Count; i++)
                {
                    if (alive[i] == null) continue;
                    sum += alive[i].Transform.position;
                    n++;
                }
                if (n > 0) return sum / n;
            }

            return Player.LocalPlayer != null ? Player.LocalPlayer.Transform.position : Vector3.zero;
        }

        internal static float WaterY()
        {
            try { return WaterManager.WaterHeight; } catch { return 0f; }
        }

        private static void Announce(string text)
        {
            Diag.Info("[pirates] " + text);
            try { ChatManager.ChatMessage("<color=#E08040>[Pirates]</color> " + text); }
            catch (Exception e) { Diag.Exception("Pirates chat", e); }
        }

        // ------------------------------------------------------------------ debug

        internal override void OnDebugKey()
        {
            if (!InstanceFinder.IsServerStarted)
            {
                Diag.Warn("Pirates: host-only.");
                return;
            }
            if (_spawned != null) DespawnShip("hotkey");
            else SpawnShip();
        }

        internal override string StatusLine()
        {
            if (!IsEnabled) return "disabled";
            if (!ModAssets.Available) return "no model bundle";
            if (_spawned == null) return _prefab != null ? "ready (prefab id " + _prefab.PrefabId + ")" : "not registered";

            PirateShip s = _spawned.GetComponent<PirateShip>();
            if (s == null) return "spawned";

            float dist = Vector3.Distance(_spawned.transform.position, PartyPosition());
            return s.CurrentStance + ", " + dist.ToString("0") + "m, hull " +
                   s.Health.ToString("0") + "/" + s.MaxHealth.ToString("0");
        }
    }
}
