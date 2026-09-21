using System;
using Expanded.Content;
using Expanded.Quests;
using FishNet;
using UnityEngine;

namespace Expanded.Pirates
{
    /// <summary>
    /// The spot the gulls' chart marks: a buoy with a pirate flag, a couple of hundred metres
    /// off the island, shown as a red dot on the game's radar (boat radar and handheld map).
    ///
    /// It gives the finale a destination instead of "sail somewhere and wait". The host picks the spot
    /// (open water, reachable) and decides when the party has arrived; every client draws the buoy
    /// and the marker from the broadcast position.
    /// </summary>
    internal static class ChartSite
    {
        // Host state.
        private static bool _active;
        private static Vector3 _pos;
        private static int _island = -1;
        private static float _nextCheck;

        // Client state.
        private static bool _shown;
        private static Vector3 _shownPos;
        private static GameObject _buoy;
        private static float _bobPhase;

        private static PirateConfig Cfg => PirateModule.Cfg;

        internal static bool HostActive => _active;
        internal static Vector3 HostPosition => _pos;

        // ------------------------------------------------------------------ host

        internal static void ServerTick()
        {
            if (Time.time < _nextCheck) return;
            _nextCheck = Time.time + 0.5f;

            QuestProgress p = QuestModule.Instance?.Engine?.Progress(PirateStory.QuestPirates);
            // The mark matters from "sail to it" (step 1) until the fight is won (step 2 done).
            bool wanted = p != null && p.Status == QuestStatus.Active && p.StepIndex >= 1;

            int island = CurrentIsland();
            if (wanted && (!_active || island != _island))
            {
                Place(island);
                return;
            }
            if (!wanted)
            {
                if (_active) Hide();
                return;
            }

            float dist = ClosestApproach(_pos);

            // Step 1: arriving at the mark is the story beat.
            if (p.StepIndex == 1 && dist <= Cfg.SiteReachRadius.Value)
            {
                Diag.Info("ChartSite: party reached the mark.");
                QuestModule.Instance?.SetFlag(PirateStory.FlagSiteReached);
                return;
            }

            // Step 2: the Widow is waiting here. If she is not out (first arrival, or she broke off
            // earlier), coming back within range brings her in again - a natural retry.
            if (p.StepIndex == 2 && !PirateModule.Instance.ShipOut && dist <= Cfg.SiteEngageRadius.Value)
                PirateModule.Instance.SpawnShip(true, _pos);
        }

        private static void Place(int island)
        {
            Vector3 mooring = SpawnManager.BoatSpawnPos;
            if (mooring == Vector3.zero) mooring = PirateModule.PartyPosition();

            _pos = PirateModule.FindOpenWater(mooring, Cfg.SiteDistance.Value);
            _pos.y = PirateModule.WaterY();
            _island = island;
            _active = true;

            Diag.Info("ChartSite: mark placed at " + _pos.ToString("F0") + ", " +
                      FlatDistance(mooring, _pos).ToString("0") + " m from the mooring.");
            Broadcast();
        }

        private static void Hide()
        {
            _active = false;
            Broadcast();
        }

        internal static void Broadcast()
        {
            bool active = _active;
            Vector3 pos = _pos;
            ModNet.SendToAll(Msg.SiteMarker, w => { w.Write(active); Cannonballs.WriteVec(w, pos); });
        }

        internal static void SendTo(FishNet.Connection.NetworkConnection conn)
        {
            bool active = _active;
            Vector3 pos = _pos;
            ModNet.SendTo(conn, Msg.SiteMarker, w => { w.Write(active); Cannonballs.WriteVec(w, pos); });
        }

        internal static void ServerReset()
        {
            _active = false;
            _island = -1;
        }

        private static int CurrentIsland()
        {
            try { return OnlineIslandManager.CurIsland; } catch { return 0; }
        }

        /// <summary>
        /// How close anyone has come: the nearest living player or the boat as it is drawn. (The
        /// boat's root object stays where the boat was spawned while the boat itself sails, so it
        /// must never be used as "where the boat is" - that is why arriving at the mark did nothing.)
        /// </summary>
        private static float ClosestApproach(Vector3 mark)
        {
            float best = float.MaxValue;
            try
            {
                Boat boat = BoatManager.Boat;
                if (boat != null) best = FlatDistance(BoatMount.Frame(boat).position, mark);
            }
            catch { /* boat not ready */ }

            var alive = PlayerManager.AlivePlayers;
            if (alive != null)
                foreach (Player p in alive)
                    if (p != null) best = Mathf.Min(best, FlatDistance(p.Transform.position, mark));
            return best;
        }

        private static float FlatDistance(Vector3 a, Vector3 b)
        {
            a.y = b.y = 0f;
            return Vector3.Distance(a, b);
        }

        // ------------------------------------------------------------------ clients

        internal static void RegisterClientHandlers()
        {
            ModNet.OnClient(Msg.SiteMarker, r =>
            {
                bool active = r.ReadBoolean();
                Vector3 pos = Cannonballs.ReadVec(r);
                _shown = active;
                _shownPos = pos;
                if (!active) ClearVisual();
                else BuildVisual();
            });
        }

        private static void BuildVisual()
        {
            ClearVisual();

            // A floating barrel with a tall pirate flag: readable from a distance, obviously man-made.
            // Solid, so boats bump into it and players can't wade through; the colliders ride a
            // kinematic body because the buoy moves every frame with the swell.
            _buoy = new GameObject(BoatMount.MountRoot + "ChartBuoy");
            _buoy.transform.position = _shownPos;
            ModAssets.Create("barrel", _shownPos, Quaternion.identity, _buoy.transform, solid: true);
            GameObject flag = ModAssets.Create("flag-pirate-high", _shownPos, Quaternion.identity, _buoy.transform, solid: true);
            if (flag != null) flag.transform.localPosition = new Vector3(0f, 0.6f, 0f);
            Rigidbody body = _buoy.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            _bobPhase = UnityEngine.Random.Range(0f, 6.28f);
        }

        private static void ClearVisual()
        {
            if (_buoy != null) UnityEngine.Object.Destroy(_buoy);
            _buoy = null;
            ChartRadar.Clear();
        }

        internal static void ClientTick()
        {
            if (_shown) ChartRadar.Tick(_shownPos);
            if (_buoy == null) return;

            // Ride the swell with a lazy wobble so it reads as floating, not planted.
            float water = PirateModule.WaterY();
            Vector3 p = _shownPos;
            p.y = water - 0.3f + Mathf.Sin(Time.time * 1.1f + _bobPhase) * 0.25f;
            Quaternion rot = Quaternion.Euler(Mathf.Sin(Time.time * 0.9f + _bobPhase) * 6f,
                                              Time.time * 4f,
                                              Mathf.Cos(Time.time * 0.7f + _bobPhase) * 6f);
            Rigidbody body = _buoy.GetComponent<Rigidbody>();
            if (body != null) { body.MovePosition(p); body.MoveRotation(rot); }
            else _buoy.transform.SetPositionAndRotation(p, rot);
        }

        internal static void ClientClear()
        {
            _shown = false;
            ClearVisual();
        }
    }
}
