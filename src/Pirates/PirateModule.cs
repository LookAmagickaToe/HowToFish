using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Expanded.Content;
using Expanded.Quests;
using FishNet;
using FishNet.Component.Transforming;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Managing.Object;
using FishNet.Object;
using UnityEngine;

namespace Expanded.Pirates
{
    /// <summary>
    /// The pirate encounter: an enemy ship that hunts the crew's boat, trades broadsides with it,
    /// and can be sunk with the deck cannon, dynamite and gunfire.
    ///
    /// The ship is a networked object the mod defines itself - an empty object carrying FishNet's
    /// own NetworkObject and NetworkTransform plus the kit model. Both components ship compiled
    /// inside FishNet, so nothing has to be code-generated for the mod. Because the prefab is
    /// registered at runtime, every player must run the same mod version: peers match objects by
    /// registration order.
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
        internal override Type[] PatchTypes => new[] { typeof(PiratePatches), typeof(GunPatches) };

        private GameObject _prefabRoot;       // inactive template, never rendered
        private NetworkObject _prefab;
        private NetworkManager _registeredWith;
        private GameObject _spawned;
        private bool _spawnedForStory;

        private readonly List<Vector3> _portsStarboard = new List<Vector3>();
        private readonly List<Vector3> _portsLarboard = new List<Vector3>();

        // Host trigger state.
        private float _atSeaSince = -1f;
        private float _lastRaidEnded = -9999f;
        private float _nextRaidRoll;

        // Client-side health bar state, fed by ShipStatus.
        private bool _barVisible;
        private float _barHp, _barMax, _barShown;
        private PirateShip.Stance _barStance;
        private string _barName = "";

        internal override void Configure(ConfigFile config)
        {
            Instance = this;
            Cfg = new PirateConfig(config);
        }

        internal override void OnEnable()
        {
            Instance = this;
            RegisterNetHandlers();
            Cannonballs.RegisterClientHandlers();
            ShopCannon.RegisterServerHandler();
            ChartSite.RegisterClientHandlers();
        }

        // ------------------------------------------------------------------ session

        internal override void OnSessionStart(bool asServer)
        {
            // Both roles must register, in the same order, before anything is spawned.
            EnsurePrefabRegistered();
            _atSeaSince = -1f;
            _nextRaidRoll = Time.time + 60f;
            ChartSite.ServerReset();

            if (asServer) MigrateOwnedCannon();
        }

        /// <summary>
        /// Saves from before the cannon was sold in the shop may own it without the "bought" flag;
        /// raise it so the story never asks them to buy something they already have.
        /// </summary>
        private static void MigrateOwnedCannon()
        {
            QuestEngine engine = QuestModule.Instance?.Engine;
            if (engine == null) return;
            if (SharedState.Has(PirateStory.UnlockCannon) && !engine.HasFlag(PirateStory.FlagCannonBought))
            {
                Diag.Info("Pirates: cannon already owned; marking it as bought.");
                QuestModule.Instance.SetFlag(PirateStory.FlagCannonBought);
            }
        }

        internal override void OnSessionEnd()
        {
            DespawnShip("session ended");
            Cannonballs.ServerClear();
            Cannonballs.ClientClear();
            DeckCannon.Clear();
            PirateHull.Clear();
            ShopCannon.Clear();
            ChartSite.ClientClear();
            ChartSite.ServerReset();
            _barVisible = false;
            _registeredWith = null;
        }

        internal override void Tick()
        {
            float dt = Time.deltaTime;

            if (IsServer)
            {
                Cannonballs.ServerTick(dt);
                ChartSite.ServerTick();
                TickRaids();
            }

            Cannonballs.ClientTick(dt);
            DeckCannon.ClientTick(ShipFightActive);
            PirateHull.ClientTick();
            ShopCannon.ClientTick();
            ChartSite.ClientTick();
        }

        /// <summary>True on the host while an enemy ship exists.</summary>
        internal bool ShipOut => _spawned != null;

        /// <summary>True on every machine while an enemy ship is afloat (from the replicated status).</summary>
        internal bool ShipFightActive => _barVisible && _barHp > 0f;

        /// <summary>The ship's stance as every client knows it, for local-only effects like crew animation.</summary>
        internal PirateShip.Stance ReplicatedStance => _barVisible ? _barStance : PirateShip.Stance.Closing;

        // ------------------------------------------------------------------ prefab

        /// <summary>
        /// Builds the template once and registers it with the running NetworkManager. Rebuilding it
        /// per session would hand different prefab ids to different players.
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

                collection.AddObject(_prefab, true, true);   // duplicates are ignored across sessions
                _registeredWith = nm;

                Diag.Info("Pirates: ship registered in collection " + CollectionId + " as prefab id " +
                          _prefab.PrefabId + " (" + collection.GetObjectCount() + " object(s)).");
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
            if (!ModAssets.Available) ModAssets.Load();
            if (!ModAssets.Available)
            {
                Diag.Error("Pirates: model bundle missing, cannot build the ship.");
                return false;
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
                TagForBulletImpacts(hull);

                Bounds b = ModAssets.Measure(hull);
                BuildGunPorts(b);

                if (!Mathf.Approximately(Cfg.Scale.Value, 1f))
                    _prefabRoot.transform.localScale = Vector3.one * Cfg.Scale.Value;

                PirateCrew.Populate(_prefabRoot.transform, b);

                NetworkObject nob = _prefabRoot.AddComponent<NetworkObject>();
                NetworkTransform nt = _prefabRoot.AddComponent<NetworkTransform>();
                nt.SetSynchronizePosition(true);
                nt.SetSynchronizeRotation(true);
                nt.SetSynchronizeScale(false);   // scale never changes; skip the bandwidth

                _prefabRoot.AddComponent<PirateShip>();
                _prefab = nob;

                Diag.Info("Pirates: ship template built from '" + Cfg.Model.Value + "', size " +
                          b.size.x.ToString("0.0") + " x " + b.size.y.ToString("0.0") + " x " +
                          b.size.z.ToString("0.0") + " m, " + _portsStarboard.Count + " guns a side.");
                return true;
            }
            catch (Exception e)
            {
                Diag.Exception("Pirates.BuildPrefab", e);
                return false;
            }
        }

        /// <summary>
        /// Tagging the hull as level geometry makes the game treat bullets that hit it like bullets
        /// hitting a wall: they stick and leave a mark, which is the feedback that you are hitting her.
        /// </summary>
        private static void TagForBulletImpacts(GameObject hull)
        {
            foreach (Collider c in hull.GetComponentsInChildren<Collider>(true))
            {
                try { c.gameObject.tag = "Level"; }
                catch (Exception e) { Diag.Debug("Could not tag hull collider: " + e.Message); return; }
            }
        }

        /// <summary>
        /// Lays gun ports along both sides from the hull's own size, and mounts a cannon model at
        /// each so the ship visibly carries the guns firing at you.
        /// </summary>
        private void BuildGunPorts(Bounds b)
        {
            _portsStarboard.Clear();
            _portsLarboard.Clear();

            int n = Mathf.Clamp(Cfg.PortsPerSide.Value, 1, 8);
            float halfWidth = b.extents.x * Cfg.PortSideFraction.Value;
            float y = b.min.y + b.size.y * Cfg.PortHeightFraction.Value;
            float span = b.size.z * Cfg.PortLengthFraction.Value;

            for (int i = 0; i < n; i++)
            {
                float t = n == 1 ? 0.5f : (float)i / (n - 1);
                float z = b.center.z - span * 0.5f + span * t;

                AddPort(new Vector3(halfWidth, y, z), _portsStarboard, 90f);
                AddPort(new Vector3(-halfWidth, y, z), _portsLarboard, -90f);
            }
        }

        private void AddPort(Vector3 local, List<Vector3> list, float yaw)
        {
            list.Add(local);

            GameObject cannon = ModAssets.Create("cannon", Vector3.zero, Quaternion.identity,
                                                 _prefabRoot.transform, solid: false);
            if (cannon == null) return;
            cannon.transform.localPosition = local;
            cannon.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            cannon.transform.localScale *= Cfg.CannonModelScale.Value;
        }

        /// <summary>Local-space muzzle positions for the side facing +1 (starboard) or -1 (larboard).</summary>
        internal IReadOnlyList<Vector3> GunPorts(int side) => side > 0 ? _portsStarboard : _portsLarboard;

        // ------------------------------------------------------------------ spawning

        /// <summary>
        /// Sends in the pirate ship. With <paramref name="near"/> she appears out beyond that point
        /// (the chart's mark), otherwise on the horizon around the party.
        /// </summary>
        internal bool SpawnShip(bool story, Vector3? near = null)
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
            if (BoatManager.Boat == null && PlayerManager.AlivePlayers.Count == 0)
            {
                Diag.Warn("Pirates: nobody to attack.");
                return false;
            }
            if (!EnsurePrefabRegistered()) return false;

            try
            {
                Vector3 party = PartyPosition();
                Vector3 pos = near.HasValue
                    ? FindOpenWater(near.Value, 45f)          // lurking just past the mark
                    : FindOpenWater(party, Cfg.SpawnDistance.Value);

                Vector3 look = party - pos;
                look.y = 0f;
                Quaternion rot = look.sqrMagnitude > 0.01f ? Quaternion.LookRotation(look.normalized) : Quaternion.identity;

                GameObject ship = UnityEngine.Object.Instantiate(_prefabRoot, pos, rot);
                ship.name = "PirateShip";
                ship.SetActive(true);

                PirateShip brain = ship.GetComponent<PirateShip>();
                brain?.ServerInitialise(Cfg.Health.Value);

                InstanceFinder.ServerManager.Spawn(ship);
                _spawned = ship;
                _spawnedForStory = story;

                Diag.Info("Pirates: ship spawned (" + (story ? "story" : "raid") + ") at " + pos.ToString("F1") + ".");
                Announce(story
                    ? "Sails behind the buoy - the Salted Widow was waiting for you. Man the bow gun, and shoot her captain if you can."
                    : "A ship flying no colours is closing fast. Word of your money travels.");
                return true;
            }
            catch (Exception e)
            {
                Diag.Exception("Pirates.SpawnShip", e);
                return false;
            }
        }

        /// <summary>
        /// Picks a spawn point on open water. Spawning her inside an island would leave her stuck
        /// and unreachable, so candidates in a ring are tested for land before one is used.
        /// </summary>
        internal static Vector3 FindOpenWater(Vector3 around, float radius)
        {
            float water = WaterY();
            int mask = GameInfo.LevelLayer.value;
            float start = UnityEngine.Random.Range(0f, Mathf.PI * 2f);

            for (int i = 0; i < 16; i++)
            {
                float a = start + i * (Mathf.PI * 2f / 16f);
                Vector3 p = around + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * radius;
                p.y = water + Cfg.Waterline.Value;

                // Clear of land around the hull and along the line in towards the party.
                if (Physics.CheckSphere(p + Vector3.up * 3f, 10f, mask, QueryTriggerInteraction.Ignore)) continue;
                if (Physics.Linecast(p + Vector3.up * 2f, around + Vector3.up * 2f, mask, QueryTriggerInteraction.Ignore)
                    && i < 12) continue; // prefer a clear approach, accept a blocked one late on

                return p;
            }

            Vector3 fallback = around + new Vector3(Mathf.Cos(start), 0f, Mathf.Sin(start)) * radius;
            fallback.y = water + Cfg.Waterline.Value;
            Diag.Warn("Pirates: no clear water found around the party; spawning anyway.");
            return fallback;
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
            _lastRaidEnded = Time.time;
            if (InstanceFinder.IsServerStarted) BroadcastStatus(false, 0f, 1f, PirateShip.Stance.Leaving, 0);
        }

        // ------------------------------------------------------------------ outcomes

        /// <summary>Host: the ship is beaten - sunk, or her captain shot and her colours struck.</summary>
        internal void OnShipDefeated(PirateShip ship, bool surrendered)
        {
            Announce(surrendered
                ? "Their captain is down! The crew strike their colours - " + Cfg.ShipName.Value + " surrenders!"
                : "She's going down! " + Cfg.ShipName.Value + " is taking on water.");
            ModSave.AddCounter(surrendered ? "pirates.captured" : "pirates.sunk", 1);

            if (_spawnedForStory)
            {
                // The quest grants the reward; this flag is what completes it.
                QuestModule.Instance?.SetFlag(PirateStory.FlagPiratesBeaten);
            }
            else
            {
                int loot = Mathf.Max(0, Cfg.RaidLootMoney.Value);
                if (loot > 0)
                {
                    try { MoneyManager.AddMoney(loot, Player.LocalPlayer); } catch (Exception e) { Diag.Exception("Raid loot", e); }
                    Announce("You salvage " + loot + " coins from the wreckage.");
                }
            }
        }

        internal void OnShipLeaving(string why)
        {
            Announce("The pirates break off - " + why + ".");
        }

        // ------------------------------------------------------------------ raids (host)

        /// <summary>
        /// Random raids on rich crews, only after the story fight. The story fight itself is
        /// triggered by reaching the chart's mark (see ChartSite).
        /// </summary>
        private void TickRaids()
        {
            if (_spawned != null) { _atSeaSince = -1f; return; }

            bool atSea = BoatAtSea();
            if (!atSea) { _atSeaSince = -1f; return; }
            if (_atSeaSince < 0f) _atSeaSince = Time.time;

            if (StoryFightPending()) return;   // the story owns the sea until Act 1 is done
            if (!RaidEligible()) return;
            if (Time.time < _nextRaidRoll) return;
            _nextRaidRoll = Time.time + 60f;

            if (UnityEngine.Random.value < Cfg.RaidChancePerMinute.Value)
                SpawnShip(false);
        }

        private static bool BoatAtSea()
        {
            try
            {
                if (BoatManager.Boat == null) return false;
                Vector3 boat = BoatMount.Frame(BoatManager.Boat).position;
                Vector3 mooring = SpawnManager.BoatSpawnPos;
                boat.y = mooring.y = 0f;
                return Vector3.Distance(boat, mooring) >= Cfg.AtSeaDistance.Value;
            }
            catch { return false; }
        }

        private static bool StoryFightPending()
        {
            QuestEngine engine = QuestModule.Instance?.Engine;
            QuestProgress p = engine?.Progress(PirateStory.QuestPirates);
            return p != null && p.Status == QuestStatus.Active;
        }

        private bool RaidEligible()
        {
            if (Cfg.RaidMoneyThreshold.Value <= 0) return false;
            QuestEngine engine = QuestModule.Instance?.Engine;
            if (engine == null || !engine.HasFlag(PirateStory.FlagPiratesBeaten)) return false;
            if (Time.time - _lastRaidEnded < Cfg.RaidCooldownMinutes.Value * 60f) return false;

            int money;
            try { money = MoneyManager.Money; } catch { return false; }
            return money >= Cfg.RaidMoneyThreshold.Value;
        }

        // ------------------------------------------------------------------ networking

        private void RegisterNetHandlers()
        {
            ModNet.OnClient(Msg.EventBanner, r => ShowBanner(r.ReadString()));

            ModNet.OnClient(Msg.ShipStatus, r =>
            {
                bool alive = r.ReadBoolean();
                float hp = r.ReadSingle();
                float max = r.ReadSingle();
                var stance = (PirateShip.Stance)r.ReadByte();
                byte deadMask = r.ReadByte();
                string name = r.ReadString();

                bool wasVisible = _barVisible;
                _barVisible = alive;
                _barHp = hp;
                _barMax = Mathf.Max(1f, max);
                _barStance = stance;
                _barName = name;
                if (alive && !wasVisible) _barShown = hp; // no animated fill-up on first sight

                // Late joiners (and anyone who missed a death message) catch up on the bodies.
                if (deadMask != 0) PirateShip.Current?.ClientApplyDeadMask(deadMask);
            });

            ModNet.OnClient(Msg.CrewDied, r =>
            {
                int index = r.ReadByte();
                PirateShip.Current?.ClientKillCrew(index);
            });

            ModNet.OnServer(Msg.CrewHit, (conn, r) =>
            {
                int index = r.ReadByte();
                int damage = r.ReadInt32();
                PirateShip ship = PirateShip.Active;
                if (ship == null || !ship.Alive) return;
                ship.ServerTakeCrewGunfire(index, damage, Describe(conn));
            });

            ModNet.OnServer(Msg.ShipHit, (conn, r) =>
            {
                int damage = r.ReadInt32();
                Vector3 point = Cannonballs.ReadVec(r);

                PirateShip ship = PirateShip.Active;
                if (ship == null || !ship.Alive) return;

                // Reject reports that are nowhere near her: a stale or forged hit.
                if (Vector3.Distance(point, ship.transform.position) > 40f)
                {
                    Diag.Debug("Pirates: rejected hit report far from the ship from " + Describe(conn) + ".");
                    return;
                }
                ship.ServerTakeGunfire(damage, Describe(conn));
            });

            ModNet.OnServer(Msg.RequestFire, (conn, r) =>
            {
                byte index = r.ReadByte();
                Vector3 aim = Cannonballs.ReadVec(r);
                DeckCannon.ServerHandleFire(conn, index, aim);
            });

            // A late joiner sees the fight at once rather than after the next status heartbeat.
            ModNet.OnServer(Msg.Hello, (conn, r) =>
            {
                if (ChartSite.HostActive) ChartSite.SendTo(conn);

                PirateShip ship = PirateShip.Active;
                if (ship == null) return;
                ModNet.SendTo(conn, Msg.ShipStatus,
                    w => WriteStatus(w, true, ship.Health, ship.MaxHealth, ship.CurrentStance, ship.DeadMask));
            });
        }

        internal static void BroadcastStatus(bool alive, float hp, float max, PirateShip.Stance stance, byte deadMask)
        {
            ModNet.SendToAll(Msg.ShipStatus, w => WriteStatus(w, alive, hp, max, stance, deadMask));
        }

        private static void WriteStatus(System.IO.BinaryWriter w, bool alive, float hp, float max,
                                        PirateShip.Stance stance, byte deadMask)
        {
            w.Write(alive);
            w.Write(hp);
            w.Write(max);
            w.Write((byte)stance);
            w.Write(deadMask);
            w.Write(Cfg.ShipName.Value ?? "");
        }

        private static string Describe(NetworkConnection c) => c == null ? "?" : "client " + c.ClientId;

        // ------------------------------------------------------------------ UI

        private GUIStyle _barLabel;

        internal override void OnGUI()
        {
            DeckCannon.OnGUI();
            ChartSite.OnGUI();

            if (!_barVisible) return;

            // Ease the displayed value so hits read as a chunk being knocked off.
            _barShown = Mathf.MoveTowards(_barShown, _barHp, Time.unscaledDeltaTime * _barMax * 0.8f);

            const float w = 460f, h = 20f;
            float x = (Screen.width - w) * 0.5f;
            float y = 18f;

            if (_barLabel == null)
            {
                _barLabel = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Bold,
                    fontSize = 15
                };
                _barLabel.normal.textColor = new Color(1f, 0.93f, 0.85f);
            }

            string title = _barName + (_barStance == PirateShip.Stance.Sinking ? "  - sinking" :
                                       _barStance == PirateShip.Stance.Surrendered ? "  - colours struck" :
                                       _barStance == PirateShip.Stance.Leaving ? "  - fleeing" : "");
            GUI.Label(new Rect(x, y, w, 22f), title, _barLabel);

            Rect back = new Rect(x, y + 24f, w, h);
            DrawRect(back, new Color(0f, 0f, 0f, 0.6f));

            float lag = Mathf.Clamp01(_barShown / _barMax);
            float now = Mathf.Clamp01(_barHp / _barMax);
            DrawRect(new Rect(back.x + 2, back.y + 2, (back.width - 4) * lag, back.height - 4), new Color(0.95f, 0.85f, 0.5f, 0.9f));
            DrawRect(new Rect(back.x + 2, back.y + 2, (back.width - 4) * now, back.height - 4), new Color(0.72f, 0.12f, 0.1f, 1f));
        }

        private static Texture2D _white;

        internal static void DrawRect(Rect r, Color c)
        {
            if (_white == null)
            {
                _white = new Texture2D(1, 1);
                _white.SetPixel(0, 0, Color.white);
                _white.Apply();
            }
            Color prev = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _white);
            GUI.color = prev;
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Where the fight centres: the boat if there is one, else the living players.</summary>
        internal static Vector3 PartyPosition()
        {
            try
            {
                // The drawn boat, not its root: the root stays at the spawn point while the boat sails.
                if (BoatManager.Boat != null) return BoatMount.Frame(BoatManager.Boat).position;
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

        /// <summary>
        /// Host-side announcement to the whole crew. Goes out as a broadcast which the host's own
        /// client also receives, so it is shown exactly once everywhere.
        /// </summary>
        internal static void Announce(string text)
        {
            Diag.Info("[pirates] " + text);
            if (InstanceFinder.IsServerStarted) ModNet.SendToAll(Msg.EventBanner, w => w.Write(text ?? ""));
            else ShowBanner(text);
        }

        private static void ShowBanner(string text)
        {
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

            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

            if (ctrl && PirateShip.Active != null)
            {
                // Test helper: shoot the captain, to test the surrender path.
                PirateShip.Active.ServerTakeCrewGunfire(0, 9999, "debug");
                return;
            }
            if (shift && PirateShip.Active != null)
            {
                // Test helper: knock a quarter off her hull. Aimed at the waterline so it does not
                // also wipe out the crew standing on deck.
                PirateShip s = PirateShip.Active;
                s.ServerTakeExplosion(s.transform.position + Vector3.down * 3f, 99f,
                                      Mathf.CeilToInt(Cfg.Health.Value * 0.25f));
                return;
            }

            if (_spawned != null) DespawnShip("hotkey");
            else SpawnShip(StoryFightPending());
        }

        internal override string StatusLine()
        {
            if (!IsEnabled) return "disabled";
            if (!ModAssets.Available) return "no model bundle";
            if (_spawned == null)
                return (_prefab != null ? "ready" : "not registered") + (IsServer ? ", at sea " + BoatAtSea() : "");

            PirateShip s = _spawned.GetComponent<PirateShip>();
            if (s == null) return "spawned";
            float dist = Vector3.Distance(_spawned.transform.position, PartyPosition());
            return s.CurrentStance + ", " + dist.ToString("0") + "m, hull " + s.Health.ToString("0") + "/" +
                   s.MaxHealth.ToString("0") + ", balls " + Cannonballs.InFlight;
        }
    }
}
