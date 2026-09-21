using System;
using System.Collections.Generic;
using System.Reflection;
using Expanded.Content;
using FishNet;
using FishNet.Connection;
using HarmonyLib;
using UnityEngine;

namespace Expanded.Pirates
{
    /// <summary>
    /// The crew's own cannons, mounted on their boat: the bow swivel gun (bought in the shop) and,
    /// with the pirate hull, a stern gun.
    ///
    /// A gun is manned like the boat is driven: look at it and press the game's interact key (E).
    /// You are then fixed behind it and aim with the mouse within a realistic arc; left click fires,
    /// R reloads, E lets go.
    ///
    /// The models are local decoration on every client (their only collider is a trigger for the
    /// look-at prompt: solid colliders on the boat would change how it floats). Firing is a request
    /// to the host, which validates position and reload before launching the ball.
    /// </summary>
    internal static class DeckCannon
    {
        private sealed class Gun
        {
            public BoatMount.Slot Slot;
            public GameObject Model;
            public Quaternion RestLocalRotation;
            public bool Loaded = true;
            public float ReloadDoneAt;
            public float ServerReadyAt;
        }

        private static readonly List<Gun> Guns = new List<Gun>();
        private static Transform _mountRoot;
        private static Boat _mountedOn;

        /// <summary>The gun the local player is manning, or -1.</summary>
        private static int _manned = -1;
        private static float _mannedAt;
        private static float _lastShotAt;

        /// <summary>
        /// The gunner's height in the boat's frame, taken from where they stood when they took the
        /// gun. The player's position is not at their feet, so the deck height would sink them into
        /// the hull; standing height keeps them exactly as tall as when walking about.
        /// </summary>
        private static float _seatLocalY;

        private static PirateConfig Cfg => PirateModule.Cfg;

        /// <summary>True while the local player mans a gun.</summary>
        internal static bool PlayerAtGun => _manned >= 0;
        internal static bool Manning => _manned >= 0;

        /// <summary>
        /// Which slots are armed right now, in index order. Identical on host and clients, because it
        /// only depends on replicated unlocks. The bow gun is bought in the shop, like a motor.
        /// </summary>
        private static List<BoatMount.Slot> ArmedSlots(bool fightActive)
        {
            var slots = new List<BoatMount.Slot>(2);
            if (SharedState.Has(PirateStory.UnlockCannon)) slots.Add(BoatMount.Slot.Bow);
            if (SharedState.Has(PirateStory.UnlockPirateShip)) slots.Add(BoatMount.Slot.Stern);
            return slots;
        }

        // ------------------------------------------------------------------ client: building

        internal static void ClientTick(bool fightActive)
        {
            Boat boat = BoatManager.Boat;
            List<BoatMount.Slot> wanted = boat != null ? ArmedSlots(fightActive) : new List<BoatMount.Slot>();

            if (boat != _mountedOn || !SameSlots(wanted)) Rebuild(boat, wanted);
            if (Guns.Count == 0) { if (Manning) Dismount("no gun"); return; }

            TickManning();
        }

        private static bool SameSlots(List<BoatMount.Slot> wanted)
        {
            if (wanted.Count != Guns.Count) return false;
            for (int i = 0; i < wanted.Count; i++) if (Guns[i].Slot != wanted[i]) return false;
            return true;
        }

        private static void Rebuild(Boat boat, List<BoatMount.Slot> slots)
        {
            Clear();
            _mountedOn = boat;
            if (boat == null || slots.Count == 0) return;

            BoatMount.Invalidate();
            _mountRoot = new GameObject(BoatMount.MountRoot + "Cannons").transform;
            _mountRoot.SetParent(BoatMount.Frame(boat), false);

            for (int i = 0; i < slots.Count; i++)
            {
                var gun = new Gun { Slot = slots[i] };
                Vector3 lp, lf;
                if (BoatMount.TryGet(slots[i], out lp, out lf))
                {
                    gun.Model = ModAssets.Create("cannon", Vector3.zero, Quaternion.identity, _mountRoot, solid: false);
                    if (gun.Model != null)
                    {
                        gun.Model.transform.localPosition = lp;
                        gun.RestLocalRotation = Quaternion.LookRotation(lf, Vector3.up);
                        gun.Model.transform.localRotation = gun.RestLocalRotation;
                        gun.Model.transform.localScale *= Cfg.DeckCannonScale.Value;
                        AddSeat(gun.Model, i);
                    }
                }
                Guns.Add(gun);
            }
            Diag.Info("DeckCannon: " + Guns.Count + " gun(s) mounted (" + string.Join(", ", slots) + ").");
        }

        private static readonly FieldInfo FInteractCol = AccessTools.Field(typeof(Interactable), "_interactCol");
        private static readonly FieldInfo FTextTarget = AccessTools.Field(typeof(Interactable), "_textTarget");
        private static readonly FieldInfo FOutline = AccessTools.Field(typeof(Interactable), "_modelsToOutline");

        /// <summary>The look-at prompt on the gun, through the game's own interactable system.</summary>
        private static void AddSeat(GameObject model, int index)
        {
            if (FInteractCol == null || FTextTarget == null || FOutline == null) return;
            try
            {
                Bounds b = ModAssets.Measure(model);
                var seat = new GameObject("GunSeat");
                seat.SetActive(false);
                seat.transform.SetParent(model.transform.parent, false);
                seat.transform.position = b.center;
                seat.layer = LayerMask.NameToLayer("Interactable");
                seat.tag = "Interactable";

                BoxCollider box = seat.AddComponent<BoxCollider>();
                box.isTrigger = true;   // prompt only; never something the boat or players bump into
                box.size = new Vector3(Mathf.Max(0.7f, b.size.x), Mathf.Max(0.7f, b.size.y), Mathf.Max(0.7f, b.size.z));

                var label = new GameObject("TextTarget").transform;
                label.SetParent(seat.transform, false);
                label.position = new Vector3(b.center.x, b.max.y + 0.35f, b.center.z);

                var outline = new List<GameObject>();
                foreach (Renderer r in model.GetComponentsInChildren<Renderer>(true)) outline.Add(r.gameObject);

                GunSeatInteractable it = seat.AddComponent<GunSeatInteractable>();
                it.Index = index;
                FInteractCol.SetValue(it, box);
                FTextTarget.SetValue(it, label);
                FOutline.SetValue(it, outline.ToArray());
                seat.SetActive(true);
            }
            catch (Exception e)
            {
                Diag.Exception("DeckCannon.AddSeat", e);
            }
        }

        /// <summary>The boat's shape changed (pirate hull fitted or removed): re-mount on the next tick.</summary>
        internal static void ForceRebuild()
        {
            Clear();
        }

        internal static void Clear()
        {
            if (Manning) Dismount("guns removed");
            if (_mountRoot != null) UnityEngine.Object.Destroy(_mountRoot.gameObject);
            _mountRoot = null;
            _mountedOn = null;
            Guns.Clear();
        }

        // ------------------------------------------------------------------ client: manning

        internal static void Mount(int index)
        {
            Player me = Player.LocalPlayer;
            if (me == null || me.Dying.IsDead || index < 0 || index >= Guns.Count || Guns[index].Model == null) return;
            if (Boat.IsDrivingLocally) return;

            _manned = index;
            _mannedAt = Time.time;
            Transform frame = Guns[index].Model.transform.parent;
            float standing = frame.InverseTransformPoint(me.Transform.position).y;
            float deck = Guns[index].Model.transform.localPosition.y;
            _seatLocalY = Mathf.Max(standing, deck);   // never below the deck the gun stands on
            GunPatches.SetFrozen(me, true);
            FaceGun(me, Guns[index]);
            Diag.Info("DeckCannon: manning gun " + index + " (" + Guns[index].Slot + ").");
        }

        internal static void Dismount(string why)
        {
            if (!Manning) return;
            int was = _manned;
            _manned = -1;
            Player me = Player.LocalPlayer;
            if (me != null) GunPatches.SetFrozen(me, false);
            if (was < Guns.Count && Guns[was].Model != null)
                Guns[was].Model.transform.localRotation = Guns[was].RestLocalRotation;
            Diag.Info("DeckCannon: left gun " + was + " (" + why + ").");
        }

        private static void TickManning()
        {
            if (!Manning) return;
            Player me = Player.LocalPlayer;
            if (me == null || me.Dying.IsDead) { Dismount("died"); return; }
            if (Boat.IsDrivingLocally) { Dismount("took the helm"); return; }
            if (_manned >= Guns.Count || Guns[_manned].Model == null) { Dismount("gun gone"); return; }

            Gun g = Guns[_manned];

            // F: climb into the barrel and fire yourself. Purely for fun; the landing is your problem.
            if (Cfg.CannonballLaunchSpeed.Value > 0f && Time.time - _mannedAt > 0.3f &&
                !ChatManager.IsTyping && !me.BlockInputs && Input.GetKeyDown(KeyCode.F))
            {
                LaunchSelf(me, g);
                return;
            }

            if (!g.Loaded && g.ReloadDoneAt > 0f && Time.time >= g.ReloadDoneAt)
            {
                g.Loaded = true;
                g.ReloadDoneAt = 0f;
            }

            // The barrel follows your view, within its arc.
            Vector3 aim = AimDirection();
            g.Model.transform.rotation = Quaternion.Slerp(g.Model.transform.rotation,
                Quaternion.LookRotation(aim, Vector3.up), Time.deltaTime * 14f);
        }

        /// <summary>
        /// The human cannonball: the gunner leaves the gun at the muzzle with the gun's aim (a little
        /// extra loft so it's a proper arc) and a bang. Player movement is the client's own, so the
        /// flight is seen by everyone without any extra networking.
        /// </summary>
        private static void LaunchSelf(Player me, Gun g)
        {
            Vector3 aim = (AimDirection() + Vector3.up * 0.35f).normalized;
            Vector3 muzzle = g.Model.transform.position + Vector3.up * 0.9f + aim * 1.4f;

            Dismount("human cannonball");
            try
            {
                PlayerMovement mv = me.GetComponentInChildren<PlayerMovement>(true);
                if (mv == null) return;
                mv.Teleport(muzzle, true);
                mv.SetVel(aim * Cfg.CannonballLaunchSpeed.Value);
                AudioManager.PlayClipAt("Explosion", muzzle, true, AudioDistance.Long, 0.35f, 0.05f);
                ParticleManager.Play("Ashes", muzzle, aim);
                ModSave.AddCounter("cannon.humans.fired", 1);
                Diag.Info("DeckCannon: human cannonball away at " + Cfg.CannonballLaunchSpeed.Value.ToString("0") + " m/s.");
            }
            catch (Exception e)
            {
                Diag.Exception("DeckCannon.LaunchSelf", e);
            }
        }

        /// <summary>Where the gunner stands: on deck, a step behind the breech.</summary>
        internal static bool SeatPosition(out Vector3 pos)
        {
            pos = Vector3.zero;
            if (!Manning || _manned >= Guns.Count || Guns[_manned].Model == null) return false;
            Transform m = Guns[_manned].Model.transform;
            Transform frame = m.parent;
            Vector3 restLocal = Guns[_manned].RestLocalRotation * Vector3.forward;
            Vector3 local = m.localPosition - restLocal * 1.0f;
            local.y = _seatLocalY;
            pos = frame.TransformPoint(local);
            return true;
        }

        /// <summary>Straight-ahead yaw of the manned gun in world space (turns with the boat).</summary>
        internal static bool RestYaw(out float yaw)
        {
            yaw = 0f;
            if (!Manning || _manned >= Guns.Count || Guns[_manned].Model == null) return false;
            Transform m = Guns[_manned].Model.transform;
            Vector3 f = m.parent.rotation * Guns[_manned].RestLocalRotation * Vector3.forward;
            yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            return true;
        }

        private static void FaceGun(Player me, Gun g)
        {
            float yaw;
            if (!RestYaw(out yaw)) return;
            try { me.Camera.SetRot(yaw); } catch { }
        }

        internal static void FireInput()
        {
            if (!Manning) return;
            if (Time.time - _mannedAt < 0.2f) return;   // the E press that mounted must not also fire
            // With empty hands the game hears one click twice (item input and punch input).
            if (Time.time - _lastShotAt < 0.15f) return;
            Gun g = Guns[_manned];
            if (!g.Loaded) return;

            g.Loaded = false;
            g.ReloadDoneAt = 0f;
            _lastShotAt = Time.time;
            Vector3 aim = AimDirection();
            byte index = (byte)_manned;
            ModNet.SendToServer(Msg.RequestFire, w =>
            {
                w.Write(index);
                Cannonballs.WriteVec(w, aim);
            });
        }

        internal static void ReloadInput()
        {
            if (!Manning) return;
            Gun g = Guns[_manned];
            if (g.Loaded || g.ReloadDoneAt > 0f) return;
            g.ReloadDoneAt = Time.time + Mathf.Max(0.5f, Cfg.DeckCannonCooldown.Value);
        }

        private static Vector3 AimDirection()
        {
            Transform view = null;
            try { if (GameInfo.CurCamera != null) view = GameInfo.CurCamera.transform; } catch { }
            Vector3 dir = view != null ? view.forward : (Player.LocalPlayer != null ? Player.LocalPlayer.Transform.forward : Vector3.forward);
            return ClampElevation(dir);
        }

        /// <summary>Keeps shots out of your own deck and out of the sky. Applied on both sides.</summary>
        internal static Vector3 ClampElevation(Vector3 dir)
        {
            if (dir.sqrMagnitude < 1e-6f) return Vector3.forward;
            dir.Normalize();

            Vector3 flat = new Vector3(dir.x, 0f, dir.z);
            if (flat.sqrMagnitude < 1e-6f) flat = Vector3.forward;
            flat.Normalize();

            float elev = Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg;
            elev = Mathf.Clamp(elev, -10f, Cfg.DeckCannonMaxElevation.Value);
            float r = elev * Mathf.Deg2Rad;
            return (flat * Mathf.Cos(r) + Vector3.up * Mathf.Sin(r)).normalized;
        }

        // ------------------------------------------------------------------ UI

        private static GUIStyle _hud, _state;

        internal static void OnGUI()
        {
            if (!Manning || _manned >= Guns.Count) return;

            if (_hud == null)
            {
                _hud = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 16 };
                _hud.normal.textColor = new Color(0.9f, 0.9f, 0.9f);
                _state = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 20, fontStyle = FontStyle.Bold };
            }

            Gun g = Guns[_manned];
            string state;
            if (g.Loaded) { state = "LOADED"; _state.normal.textColor = new Color(0.6f, 1f, 0.6f); }
            else if (g.ReloadDoneAt > 0f) { state = "RELOADING  " + Mathf.Max(0f, g.ReloadDoneAt - Time.time).ToString("0.0") + "s"; _state.normal.textColor = new Color(1f, 0.85f, 0.4f); }
            else { state = "EMPTY - press R"; _state.normal.textColor = new Color(1f, 0.45f, 0.35f); }

            bool human = Cfg.CannonballLaunchSpeed.Value > 0f;
            const float w = 420f;
            Rect box = new Rect((Screen.width - w) * 0.5f, Screen.height - 150f - (human ? 22f : 0f), w, human ? 84f : 62f);
            PirateModule.DrawRect(box, new Color(0f, 0f, 0f, 0.45f));
            GUI.Label(new Rect(box.x, box.y + 4f, w, 28f), state, _state);
            GUI.Label(new Rect(box.x, box.y + 32f, w, 24f), "Left click  Fire     R  Reload     E  Leave", _hud);
            if (human) GUI.Label(new Rect(box.x, box.y + 54f, w, 24f), "F  Fire yourself (human cannonball!)", _hud);
        }

        // ------------------------------------------------------------------ host

        /// <summary>
        /// Validates a fire request: the gun must exist on the host's view of the boat, the shooter must
        /// actually be at it, and it must have had time to reload. Aim is re-clamped here as well, so a
        /// client cannot fire into the boat or straight up.
        /// </summary>
        internal static void ServerHandleFire(NetworkConnection conn, byte index, Vector3 aim)
        {
            if (!InstanceFinder.IsServerStarted) return;

            bool fight = PirateShip.Active != null && PirateShip.Active.Alive;
            List<BoatMount.Slot> slots = ArmedSlots(fight);
            if (index >= slots.Count)
            {
                Diag.Debug("DeckCannon: rejected fire for unarmed gun " + index + ".");
                return;
            }

            Vector3 mount, forward;
            if (!BoatMount.TryGetWorld(slots[index], out mount, out forward)) return;

            Player shooter = PlayerFor(conn);
            if (shooter == null || shooter.Dying.IsDead) return;
            if (Vector3.Distance(shooter.Transform.position, mount) > Cfg.InteractRange.Value * 2f)
            {
                Diag.Debug("DeckCannon: rejected fire, " + shooter.SteamName + " is not at the gun.");
                return;
            }

            Gun gun = index < Guns.Count ? Guns[index] : null;
            if (gun != null)
            {
                // A reload takes Cooldown seconds; allow some slack for latency.
                if (Time.time < gun.ServerReadyAt - 0.3f) return;
                gun.ServerReadyAt = Time.time + Cfg.DeckCannonCooldown.Value;
            }

            Vector3 dir = ClampElevation(aim);
            Vector3 muzzle = mount + Vector3.up * 0.5f + dir * 1.2f;
            Cannonballs.Fire(muzzle, dir * Cfg.DeckCannonSpeed.Value, false);
            Diag.Debug("DeckCannon: " + shooter.SteamName + " fired gun " + index + ".");
        }

        private static Player PlayerFor(NetworkConnection conn)
        {
            if (conn == null) return null;
            foreach (Player p in PlayerManager.Players)
                if (p != null && p.Owner == conn) return p;
            return null;
        }
    }

    /// <summary>"Swivel gun [E]" - the game's look-at prompt; pressing it mans the gun.</summary>
    internal sealed class GunSeatInteractable : Interactable
    {
        internal int Index;
        private string _hover;

        public override bool InteractableWhenHoldingItem => true;

        public override void Hover()
        {
            if (!_isHovering)
            {
                string key = "[E]";
                try { key = SpriteManager.GetPickUpInput(); } catch { }
                _hover = "Swivel gun\n" + key;
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
                if (player != null && player == Player.LocalPlayer) DeckCannon.Mount(Index);
            }
            catch (Exception e)
            {
                Diag.Exception("GunSeatInteractable.Interact", e);
            }
        }
    }

    /// <summary>
    /// While the local player mans a gun they are held in place like the boat's driver, their view is
    /// kept inside the gun's arc, and the game's own buttons are borrowed: left click fires the gun
    /// instead of the held item, R reloads it, E (the interact key) lets go.
    /// </summary>
    internal static class GunPatches
    {
        private static readonly FieldInfo FMoveRig = AccessTools.Field(typeof(PlayerMovement), "_rig");
        private static readonly FieldInfo FMovePlayer = AccessTools.Field(typeof(PlayerMovement), "_player");
        private static readonly FieldInfo FCamPlayer = AccessTools.Field(typeof(PlayerCamera), "_player");
        private static readonly FieldInfo FCamRot = AccessTools.Field(typeof(PlayerCamera), "_rot");

        private static bool IsLocal(object component, FieldInfo playerField)
        {
            try { return playerField != null && ReferenceEquals(playerField.GetValue(component), Player.LocalPlayer); }
            catch { return false; }
        }

        internal static void SetFrozen(Player me, bool frozen)
        {
            try
            {
                PlayerMovement mv = me.GetComponentInChildren<PlayerMovement>(true);
                if (mv == null || FMoveRig == null) return;
                var rig = (Rigidbody)FMoveRig.GetValue(mv);
                if (rig == null) return;
                if (!frozen) rig.isKinematic = false;
                else
                {
                    rig.linearVelocity = Vector3.zero;
                    rig.isKinematic = true;
                }
            }
            catch (Exception e)
            {
                Diag.Exception("GunPatches.SetFrozen", e);
            }
        }

        private static void HoldAtSeat(PlayerMovement mv)
        {
            if (!DeckCannon.Manning || !IsLocal(mv, FMovePlayer)) return;
            Vector3 seat;
            if (!DeckCannon.SeatPosition(out seat)) return;
            var rig = (Rigidbody)FMoveRig.GetValue(mv);
            if (rig != null) rig.transform.position = seat;
        }

        [HarmonyPrefix, HarmonyPatch(typeof(PlayerMovement), "Move")]
        private static bool PlayerMovement_Move(PlayerMovement __instance) =>
            !(DeckCannon.Manning && IsLocal(__instance, FMovePlayer));

        [HarmonyPostfix, HarmonyPatch(typeof(PlayerMovement), "FixedUpdate")]
        private static void PlayerMovement_FixedUpdate(PlayerMovement __instance) => HoldAtSeat(__instance);

        [HarmonyPostfix, HarmonyPatch(typeof(PlayerMovement), "LateUpdate")]
        private static void PlayerMovement_LateUpdate(PlayerMovement __instance) => HoldAtSeat(__instance);

        /// <summary>Keeps the view (and so the barrel) inside the gun's traverse and elevation.</summary>
        [HarmonyPostfix, HarmonyPatch(typeof(PlayerCamera), "MouseMovement")]
        private static void PlayerCamera_MouseMovement(PlayerCamera __instance)
        {
            if (!DeckCannon.Manning || FCamRot == null || !IsLocal(__instance, FCamPlayer)) return;
            float rest;
            if (!DeckCannon.RestYaw(out rest)) return;

            var rot = (Vector3)FCamRot.GetValue(__instance);
            float arc = Mathf.Clamp(PirateModule.Cfg.DeckCannonYawArc.Value, 5f, 170f);
            float delta = Mathf.DeltaAngle(rest, rot.y);
            float clamped = Mathf.Clamp(delta, -arc, arc);
            rot.y -= delta - clamped;
            // Camera pitch: positive looks down. Up to the gun's elevation, a little below the horizon.
            rot.x = Mathf.Clamp(rot.x, -PirateModule.Cfg.DeckCannonMaxElevation.Value, 10f);
            FCamRot.SetValue(__instance, rot);
        }

        [HarmonyPrefix, HarmonyPatch(typeof(PlayerHolding), "PickUpInput")]
        private static bool PlayerHolding_PickUpInput()
        {
            if (!DeckCannon.Manning) return true;
            DeckCannon.Dismount("pressed interact");
            return false;
        }

        [HarmonyPrefix, HarmonyPatch(typeof(PlayerHolding), "PrimaryInput")]
        private static bool PlayerHolding_PrimaryInput()
        {
            if (!DeckCannon.Manning) return true;
            DeckCannon.FireInput();
            return false;
        }

        [HarmonyPrefix, HarmonyPatch(typeof(PlayerHolding), "PrimaryInputCanceled")]
        private static bool PlayerHolding_PrimaryInputCanceled() => !DeckCannon.Manning;

        [HarmonyPrefix, HarmonyPatch(typeof(PlayerHolding), "SecondaryInput")]
        private static bool PlayerHolding_SecondaryInput() => !DeckCannon.Manning;

        [HarmonyPrefix, HarmonyPatch(typeof(PlayerHolding), "ReloadInput")]
        private static bool PlayerHolding_ReloadInput()
        {
            if (!DeckCannon.Manning) return true;
            DeckCannon.ReloadInput();
            return false;
        }

        /// <summary>No punching the gun: with empty hands left click would otherwise also throw a punch.</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(PlayerPunching), "PunchInput")]
        private static bool PlayerPunching_PunchInput()
        {
            if (!DeckCannon.Manning) return true;
            DeckCannon.FireInput();
            return false;
        }
    }
}
