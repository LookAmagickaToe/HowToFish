using System;
using System.Collections.Generic;
using Expanded.Content;
using FishNet;
using FishNet.Connection;
using UnityEngine;

namespace Expanded.Pirates
{
    /// <summary>
    /// The crew's own cannons, mounted on their boat.
    ///
    /// A bow swivel gun appears for the duration of a pirate fight, and stays for good once the
    /// pirates have been beaten; the pirate refit adds a second gun at the stern. Aim with your view,
    /// fire with the interact key.
    ///
    /// The models are local decoration on every client (and deliberately have no colliders: extra
    /// colliders on the boat's rigidbody would change how it floats and handles). Firing is a request
    /// to the host, which validates position and reload before launching the ball.
    /// </summary>
    internal static class DeckCannon
    {
        private sealed class Gun
        {
            public BoatMount.Slot Slot;
            public GameObject Model;
            public float ClientReadyAt;
            public float ServerReadyAt;
        }

        private static readonly List<Gun> Guns = new List<Gun>();
        private static Transform _mountRoot;
        private static Boat _mountedOn;
        private static int _nearIndex = -1;

        private static PirateConfig Cfg => PirateModule.Cfg;

        /// <summary>True while the local player stands within reach of a mounted gun.</summary>
        internal static bool PlayerAtGun => _nearIndex >= 0;

        /// <summary>Which slots are armed right now, in index order. Identical on host and clients.</summary>
        private static List<BoatMount.Slot> ArmedSlots(bool fightActive)
        {
            var slots = new List<BoatMount.Slot>(2);
            bool owned = SharedState.Has(PirateStory.UnlockCannon);
            if (fightActive || owned) slots.Add(BoatMount.Slot.Bow);
            if (SharedState.Has(PirateStory.UnlockPirateShip)) slots.Add(BoatMount.Slot.Stern);
            return slots;
        }

        // ------------------------------------------------------------------ client

        internal static void ClientTick(bool fightActive)
        {
            Boat boat = BoatManager.Boat;
            List<BoatMount.Slot> wanted = boat != null ? ArmedSlots(fightActive) : new List<BoatMount.Slot>();

            if (boat != _mountedOn || !SameSlots(wanted)) Rebuild(boat, wanted);
            if (Guns.Count == 0) { _nearIndex = -1; return; }

            UpdateNearestAndAim();
            HandleInput();
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
            _mountRoot.SetParent(boat.transform, false);

            foreach (BoatMount.Slot slot in slots)
            {
                var gun = new Gun { Slot = slot };
                Vector3 lp, lf;
                if (BoatMount.TryGet(slot, out lp, out lf))
                {
                    gun.Model = ModAssets.Create("cannon", Vector3.zero, Quaternion.identity, _mountRoot, solid: false);
                    if (gun.Model != null)
                    {
                        gun.Model.transform.localPosition = lp;
                        gun.Model.transform.localRotation = Quaternion.LookRotation(lf, Vector3.up);
                        gun.Model.transform.localScale *= Cfg.DeckCannonScale.Value;
                    }
                }
                Guns.Add(gun);
            }
            Diag.Info("DeckCannon: " + Guns.Count + " gun(s) mounted (" + string.Join(", ", slots) + ").");
        }

        internal static void Clear()
        {
            if (_mountRoot != null) UnityEngine.Object.Destroy(_mountRoot.gameObject);
            _mountRoot = null;
            _mountedOn = null;
            Guns.Clear();
            _nearIndex = -1;
        }

        /// <summary>
        /// Finds the gun the local player is standing at, and swings that gun to follow their view -
        /// a small thing that makes aiming feel physical rather than a hidden raycast.
        /// </summary>
        private static void UpdateNearestAndAim()
        {
            _nearIndex = -1;
            Player me = Player.LocalPlayer;
            if (me == null || me.Dying.IsDead) return;

            float best = Cfg.InteractRange.Value;
            for (int i = 0; i < Guns.Count; i++)
            {
                Gun g = Guns[i];
                if (g.Model == null) continue;
                float d = Vector3.Distance(me.Transform.position, g.Model.transform.position);
                if (d <= best) { best = d; _nearIndex = i; }
            }

            if (_nearIndex < 0) return;
            Gun gun = Guns[_nearIndex];
            Vector3 aim = AimDirection();
            gun.Model.transform.rotation = Quaternion.Slerp(gun.Model.transform.rotation,
                Quaternion.LookRotation(aim, Vector3.up), Time.deltaTime * 10f);
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

        private static void HandleInput()
        {
            if (_nearIndex < 0) return;
            if (ChatManager.IsTyping) return;
            if (Player.LocalPlayer != null && Player.LocalPlayer.BlockInputs) return;
            if (!Input.GetKeyDown(Cfg.InteractKey.Value)) return;

            Gun g = Guns[_nearIndex];
            if (Time.time < g.ClientReadyAt) return;

            // Predict the reload locally so the prompt reacts instantly; the host has the final say.
            g.ClientReadyAt = Time.time + Cfg.DeckCannonCooldown.Value;
            Vector3 aim = AimDirection();
            byte index = (byte)_nearIndex;
            ModNet.SendToServer(Msg.RequestFire, w =>
            {
                w.Write(index);
                Cannonballs.WriteVec(w, aim);
            });
        }

        private static GUIStyle _prompt;

        internal static void OnGUI()
        {
            if (_nearIndex < 0 || _nearIndex >= Guns.Count) return;

            if (_prompt == null)
            {
                _prompt = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 18, fontStyle = FontStyle.Bold };
                _prompt.normal.textColor = Color.white;
            }

            Gun g = Guns[_nearIndex];
            float left = g.ClientReadyAt - Time.time;
            string text = left > 0f
                ? "Reloading... " + left.ToString("0.0") + "s"
                : "[" + Cfg.InteractKey.Value + "] Fire cannon";

            Rect r = new Rect((Screen.width - 320f) * 0.5f, Screen.height * 0.62f, 320f, 34f);
            PirateModule.DrawRect(r, new Color(0f, 0f, 0f, 0.45f));
            GUI.Label(r, text, _prompt);
        }

        // ------------------------------------------------------------------ host

        /// <summary>
        /// Validates a fire request: the gun must exist on the host's view of the boat, the shooter must
        /// actually be standing at it, and it must be loaded. Aim is re-clamped here as well, so a
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
                if (Time.time < gun.ServerReadyAt - 0.15f) return; // small tolerance for latency
                gun.ServerReadyAt = Time.time + Cfg.DeckCannonCooldown.Value;
            }

            Vector3 dir = ClampElevation(aim);
            Vector3 muzzle = mount + Vector3.up * 0.7f + dir * 1.6f;
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
}
