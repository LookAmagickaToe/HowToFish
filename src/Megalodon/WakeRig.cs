using System;
using System.Collections.Generic;
using System.Reflection;
using Expanded.Content;
using Expanded.Pirates;
using FishNet;
using FishNet.Connection;
using HarmonyLib;
using UnityEngine;

namespace Expanded.Megalodon
{
    /// <summary>Where the tow rope is tied, and which way the boat is going.</summary>
    internal static class Tow
    {
        internal static Boat Boat => BoatManager.Boat;

        /// <summary>The tow point: a post at the stern, about hip height above the deck.</summary>
        internal static bool Point(out Vector3 p)
        {
            p = Vector3.zero;
            Vector3 fwd;
            if (Boat == null || !BoatMount.TryGetWorld(BoatMount.Slot.Stern, out p, out fwd)) return false;
            p += Vector3.up * 0.55f;
            return true;
        }

        /// <summary>Flat unit vector towards the bow.</summary>
        internal static Vector3 Forward()
        {
            Vector3 p, f;
            if (Boat != null && BoatMount.TryGetWorld(BoatMount.Slot.Bow, out p, out f))
            {
                f.y = 0f;
                if (f.sqrMagnitude > 1e-4f) return f.normalized;
            }
            return Vector3.forward;
        }

        internal static Vector3 Velocity()
        {
            try
            {
                if (Boat == null) return Vector3.zero;
                Vector3 v = Boat.Velocity;
                v.y = 0f;
                // A client's boat velocity is worked out from how its picture moves; when that jumps
                // (joining, a locked boat being placed) it reads absurdly high for a frame.
                return v.sqrMagnitude > 40f * 40f ? Vector3.zero : v;
            }
            catch { return Vector3.zero; }
        }

        internal static float Speed() => Velocity().magnitude;

        internal static Vector3 Centre()
        {
            try { return Boat != null ? BoatMount.Frame(Boat).position : Vector3.zero; }
            catch { return Vector3.zero; }
        }

        internal static float Water(Vector3 at)
        {
            try { return WaterManager.GetWaterHeight(at); }
            catch { return PirateModule.WaterY(); }
        }

        /// <summary>Distance of the boat from its island mooring, flat.</summary>
        internal static float DistanceFromMooring()
        {
            try
            {
                if (Boat == null) return 0f;
                Vector3 a = Centre(), b = SpawnManager.BoatSpawnPos;
                a.y = b.y = 0f;
                return Vector3.Distance(a, b);
            }
            catch { return 0f; }
        }
    }

    /// <summary>
    /// The wakeboard as a piece of kit on the boat: who is on the rope (host-owned, replicated), the
    /// board leaning by the helm when nobody is, the rope and board every client draws under the
    /// rider, and the look-at prompt that lets you grab it.
    /// </summary>
    internal static class WakeRig
    {
        // Replicated state, identical on every machine.
        internal static int RiderId { get; private set; } = -1;
        internal static Board Tier { get; private set; } = Board.Wakeboard;
        internal static float Rope { get; private set; } = 14f;

        private static MegaConfig Cfg => MegaModule.Cfg;

        internal static bool Owned => SharedState.Has(MegalodonStory.UnlockWakeboard);
        internal static bool LocalIsRider => Player.LocalPlayer != null && RiderId >= 0 && Player.LocalPlayer.OwnerId == RiderId;
        internal static Player Rider => RiderId >= 0 ? MegaModule.PlayerByOwner(RiderId) : null;

        // ------------------------------------------------------------------ networking

        internal static void RegisterHandlers()
        {
            ModNet.OnServer(Msg.WakeRequest, (conn, r) =>
            {
                bool attach = r.ReadBoolean();
                string why = r.ReadString();
                Player p = MegaModule.PlayerFor(conn);
                if (p == null) return;
                if (attach) HostAttach(p);
                else if (p.OwnerId == RiderId) HostClear(p.SteamName + " let go (" + why + ")");
            });

            ModNet.OnClient(Msg.WakeRiders, r =>
            {
                int rider = r.ReadInt32();
                var tier = (Board)r.ReadByte();
                float rope = r.ReadSingle();
                Apply(rider, tier, rope);
            });

            ModNet.OnServer(Msg.Hello, (conn, r) => ModNet.SendTo(conn, Msg.WakeRiders, Write));
        }

        private static void Write(System.IO.BinaryWriter w)
        {
            w.Write(RiderId);
            w.Write((byte)Tier);
            w.Write(Rope);
        }

        private static void Broadcast() => ModNet.SendToAll(Msg.WakeRiders, Write);

        private static void Apply(int rider, Board tier, float rope)
        {
            bool tierChanged = tier != Tier;
            // On the host these fields are already the truth; its own copy of an older broadcast
            // arriving late must not roll them back.
            if (!InstanceFinder.IsServerStarted)
            {
                RiderId = rider;
                Tier = tier;
                Rope = rope;
            }

            bool mine = LocalIsRider;
            if (mine && !Wakeboard.Riding)
            {
                Wakeboard.Begin();
                if (!Wakeboard.Riding)
                    ModNet.SendToServer(Msg.WakeRequest, w => { w.Write(false); w.Write("could not start riding"); });
            }
            else if (!mine && Wakeboard.Riding) Wakeboard.End("the rope went to someone else", false);
            if (tierChanged) _boardTier = (Board)255;   // rebuild the board under the rider
        }

        // ------------------------------------------------------------------ host

        private static void HostAttach(Player p)
        {
            if (!InstanceFinder.IsServerStarted) return;
            if (!Owned || RiderId >= 0 || p.Dying.IsDead || Tow.Boat == null || !Unlocked(Tow.Boat)) return;
            if (new Vector2(p.Transform.position.x - Tow.Centre().x, p.Transform.position.z - Tow.Centre().z).magnitude > 12f) return;
            try { if (Tow.Boat.Driver == p) return; } catch { }

            RiderId = p.OwnerId;
            Tier = Board.Wakeboard;
            Rope = Cfg.RopeLength.Value;
            Broadcast();
            ModSave.AddCounter("wake.rides", 1);
            Diag.Info("Wakeboard: " + p.SteamName + " grabbed the tow rope.");
        }

        /// <summary>Host: nobody is on the rope any more. The board is magically back at the rack.</summary>
        internal static void HostClear(string why)
        {
            if (RiderId < 0) return;
            Diag.Info("Wakeboard: rope free (" + why + ").");
            RiderId = -1;
            Tier = Board.Wakeboard;
            Rope = Cfg.RopeLength.Value;
            Broadcast();
        }

        internal static void HostSetTier(Board b)
        {
            Tier = b;
            Broadcast();
        }

        internal static void HostShortenRope(float by)
        {
            if (RiderId < 0) return;
            Rope = Mathf.Max(Cfg.MinRopeLength.Value, Rope - by);
            Broadcast();
        }

        /// <summary>Host, every frame: the rider left, died or the boat vanished.</summary>
        internal static void HostTick()
        {
            if (RiderId < 0) return;
            Player p = Rider;
            if (p == null) { HostClear("rider left the game"); return; }
            if (p.Dying.IsDead) { HostClear("rider died"); return; }
            if (Tow.Boat == null) { HostClear("no boat"); return; }
        }

        /// <summary>No tow rope on a boat the crew hasn't got yet.</summary>
        private static bool Unlocked(Boat b)
        {
            try { return b.BoatUnlocked; } catch { return true; }
        }

        internal static void Reset()
        {
            RiderId = -1;
            Tier = Board.Wakeboard;
            Rope = Cfg != null ? Cfg.RopeLength.Value : 14f;
        }

        // ------------------------------------------------------------------ visuals (every client)

        private static Transform _rack;
        private static Boat _rackBoat;
        private static float _nextRackCheck;

        private static Transform _boardRoot, _board;
        private static Board _boardTier = (Board)255;
        private static LineRenderer _rope;
        private static float _spin;

        internal static void LateTick()
        {
            TickRack();
            TickRiderVisuals();
        }

        private static void TickRack()
        {
            Boat boat = Tow.Boat;
            bool want = boat != null && Owned && RiderId < 0 && Unlocked(boat);
            if (!want)
            {
                if (_rack != null) { UnityEngine.Object.Destroy(_rack.gameObject); _rack = null; }
                return;
            }
            if (_rack != null && _rackBoat == boat) return;
            if (Time.time < _nextRackCheck) return;
            _nextRackCheck = Time.time + 1f;

            if (_rack != null) UnityEngine.Object.Destroy(_rack.gameObject);
            _rack = BuildRack(boat);
            _rackBoat = boat;
        }

        private static readonly FieldInfo FInteractCol = AccessTools.Field(typeof(Interactable), "_interactCol");
        private static readonly FieldInfo FTextTarget = AccessTools.Field(typeof(Interactable), "_textTarget");
        private static readonly FieldInfo FOutline = AccessTools.Field(typeof(Interactable), "_modelsToOutline");

        /// <summary>
        /// The board leaning against the hull, just left of the driver's seat, where a wakeboard
        /// lives on every real speedboat. Parented to the drawn boat, so it rides along.
        /// </summary>
        private static Transform BuildRack(Boat boat)
        {
            try
            {
                Transform frame = BoatMount.Frame(boat);
                if (frame == null || boat.DriverPos == null) return null;

                Vector3 seat = frame.InverseTransformPoint(boat.DriverPos.position);
                Vector3 fwd = frame.InverseTransformDirection(boat.DriverPos.forward);
                fwd.y = 0f;
                if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
                fwd.Normalize();
                Vector3 left = Vector3.Cross(fwd, Vector3.up).normalized;

                Vector3 local = seat + left * Cfg.RackSide.Value + fwd * Cfg.RackForward.Value;
                float deck;
                if (!BoatMount.TryDeckHeight(local, out deck)) deck = seat.y - 0.5f;
                local.y = deck;

                var root = new GameObject(BoatMount.MountRoot + "Wakeboard");
                root.SetActive(false);
                root.transform.SetParent(frame, false);
                root.transform.localPosition = local;

                // Stood on its tail, face towards the seat, top leaning out against the hull.
                float lean = Cfg.RackLean.Value * Mathf.Deg2Rad;
                Vector3 along = (Vector3.up * Mathf.Cos(lean) + left * Mathf.Sin(lean)).normalized;
                Vector3 face = Vector3.Cross(along, fwd).normalized;
                if (Vector3.Dot(face, -left) < 0f) face = -face;
                Transform board = MegaShapes.Wakeboard(root.transform);
                board.localRotation = Quaternion.LookRotation(along, face);
                board.localPosition = along * 0.72f;

                // The look-at prompt, through the game's own interactable system.
                var seatGo = new GameObject("WakeboardGrab");
                seatGo.transform.SetParent(root.transform, false);
                seatGo.transform.localPosition = along * 0.72f;
                seatGo.layer = LayerMask.NameToLayer("Interactable");
                seatGo.tag = "Interactable";
                BoxCollider box = seatGo.AddComponent<BoxCollider>();
                box.isTrigger = true;
                box.size = new Vector3(0.7f, 1.5f, 0.7f);
                var label = new GameObject("TextTarget").transform;
                label.SetParent(seatGo.transform, false);
                label.localPosition = Vector3.up * 0.95f;

                var outline = new List<GameObject>();
                foreach (Renderer rr in board.GetComponentsInChildren<Renderer>(true)) outline.Add(rr.gameObject);

                WakeRackInteractable it = seatGo.AddComponent<WakeRackInteractable>();
                if (FInteractCol != null && FTextTarget != null && FOutline != null)
                {
                    FInteractCol.SetValue(it, box);
                    FTextTarget.SetValue(it, label);
                    FOutline.SetValue(it, outline.ToArray());
                }
                root.SetActive(true);
                Diag.Info("Wakeboard: leaning by the helm at " + local.ToString("F2") + " (boat-local).");
                return root.transform;
            }
            catch (Exception e)
            {
                Diag.Exception("WakeRig.BuildRack", e);
                return null;
            }
        }

        private static void TickRiderVisuals()
        {
            Player rider = Rider;
            Vector3 tow;
            if (rider == null || rider.Transform == null || !Tow.Point(out tow))
            {
                if (_boardRoot != null) { UnityEngine.Object.Destroy(_boardRoot.gameObject); _boardRoot = null; _board = null; }
                if (_rope != null) _rope.enabled = false;
                _boardTier = (Board)255;
                return;
            }

            bool local = rider == Player.LocalPlayer && Wakeboard.Riding;
            Vector3 body = local ? PlayerHold.Pose : rider.Transform.position;
            float feetOffset = local ? Wakeboard.FeetOffset : 0.95f;
            Vector3 feet = body + Vector3.down * feetOffset;

            // The board, rebuilt when the tier changes.
            if (_boardRoot == null)
            {
                _boardRoot = MegaShapes.Root("ExpandedRiderBoard");
                _boardTier = (Board)255;
            }
            if (_boardTier != Tier)
            {
                if (_board != null) UnityEngine.Object.Destroy(_board.gameObject);
                _board = MegaShapes.BoardFor(Tier, _boardRoot);
                _boardTier = Tier;
            }

            // Sideways to the rope, like every wakeboarder; spinning when the rider pulls a trick.
            Vector3 toTow = tow - feet;
            toTow.y = 0f;
            if (toTow.sqrMagnitude < 1e-3f) toTow = Tow.Forward();
            toTow.Normalize();
            Vector3 along = Vector3.Cross(Vector3.up, toTow);
            _spin = local ? Wakeboard.Spin : Mathf.MoveTowards(_spin, 0f, Time.deltaTime * 720f);
            Quaternion edge = Quaternion.AngleAxis(local ? Wakeboard.EdgeAngle : 0f, toTow);
            _boardRoot.position = feet;
            _boardRoot.rotation = Quaternion.AngleAxis(_spin, Vector3.up) * edge * Quaternion.LookRotation(along, Vector3.up);

            // The rope: tow post to the rider's hands, sagging when slack.
            if (_rope == null)
            {
                var go = new GameObject("ExpandedTowRope");
                _rope = go.AddComponent<LineRenderer>();
                _rope.positionCount = 12;
                _rope.startWidth = _rope.endWidth = 0.035f;
                _rope.numCapVertices = 2;
                _rope.useWorldSpace = true;
                Material m = MegaShapes.Flat(new Color(0.85f, 0.8f, 0.6f));
                if (m != null) _rope.sharedMaterial = m;
                _rope.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            _rope.enabled = true;
            Vector3 hands = body + Vector3.up * 0.25f + toTow * 0.45f;
            Vector3 via = local && Wakeboard.Wrapping ? Wakeboard.WrapPoint : Vector3.zero;
            DrawRope(tow, hands, via, local && Wakeboard.Wrapping);
        }

        private static void DrawRope(Vector3 a, Vector3 b, Vector3 via, bool wrapped)
        {
            int n = _rope.positionCount;
            float slack = Mathf.Max(0f, Rope - Vector3.Distance(a, b));
            float sag = Mathf.Min(1.6f, slack * 0.35f) + 0.05f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)(n - 1);
                Vector3 p;
                if (wrapped)
                {
                    // Tow post -> buoy -> rider, in two straight pulls.
                    p = t < 0.5f ? Vector3.Lerp(a, via + Vector3.up * 0.6f, t * 2f) : Vector3.Lerp(via + Vector3.up * 0.6f, b, (t - 0.5f) * 2f);
                }
                else
                {
                    p = Vector3.Lerp(a, b, t) + Vector3.down * (sag * 4f * t * (1f - t));
                }
                _rope.SetPosition(i, p);
            }
        }

        internal static void ClearVisuals()
        {
            if (_rack != null) UnityEngine.Object.Destroy(_rack.gameObject);
            if (_boardRoot != null) UnityEngine.Object.Destroy(_boardRoot.gameObject);
            if (_rope != null) UnityEngine.Object.Destroy(_rope.gameObject);
            _rack = null; _rackBoat = null; _boardRoot = null; _board = null; _rope = null;
            _boardTier = (Board)255;
        }

        /// <summary>The boat's shape changed (pirate hull): re-place the board.</summary>
        internal static void RebuildRack()
        {
            if (_rack != null) UnityEngine.Object.Destroy(_rack.gameObject);
            _rack = null;
            _rackBoat = null;
        }
    }

    /// <summary>"Wakeboard [E]" on the board by the helm: grab the tow rope.</summary>
    internal sealed class WakeRackInteractable : Interactable
    {
        private string _hover;

        public override bool InteractableWhenHoldingItem => true;

        public override void Hover()
        {
            if (!_isHovering)
            {
                string key = "[E]";
                try { key = SpriteManager.GetPickUpInput(); } catch { }
                _hover = "Wakeboard\n" + key + " grab the tow rope";
                PlayerUI.SetLookAtText(_hover, TextTarget);
            }
            base.Hover();
        }

        public override void UnHover()
        {
            PlayerUI.HideLookAtText(_hover);
            base.UnHover();
        }

        public override void Interact(Player player)
        {
            try
            {
                base.Interact(player);
                PlayerUI.HideLookAtText(_hover);
                if (player == null || player != Player.LocalPlayer) return;
                if (Boat.IsDrivingLocally) return;
                ModNet.SendToServer(Msg.WakeRequest, w => { w.Write(true); w.Write("grab"); });
            }
            catch (Exception e)
            {
                Diag.Exception("WakeRackInteractable.Interact", e);
            }
        }
    }
}
