using System;
using System.Reflection;
using Expanded.Pirates;
using FishNet;
using HarmonyLib;
using UnityEngine;

namespace Expanded.Megalodon
{
    /// <summary>
    /// Where the megalodon module reaches into the game:
    ///  - the local player's body is held on the wakeboard (or in a stomach) instead of walking;
    ///  - the camera's field of view takes the near-miss zoom punch;
    ///  - E lets go of the rope, or grabs the dentures;
    ///  - bullets are tested against the megalodon's body (it has no collider, so it can never shove
    ///    the boat or the players about);
    ///  - explosions hurt it; a stalled engine gives no thrust; Old Salt's driving runs inside the
    ///    boat's own physics step.
    /// </summary>
    internal static class MegaPatches
    {
        private static readonly FieldInfo FCam = AccessTools.Field(typeof(PlayerCamera), "_cam");
        private static readonly FieldInfo FCamPlayer = AccessTools.Field(typeof(PlayerCamera), "_player");
        private static readonly MethodInfo MRemove = AccessTools.Method(typeof(ProjectileManager), "AddToRemoveQueue");

        private static bool IsLocalCamera(PlayerCamera cam)
        {
            try { return FCamPlayer != null && ReferenceEquals(FCamPlayer.GetValue(cam), Player.LocalPlayer); }
            catch { return false; }
        }

        // ------------------------------------------------------------------ holding the body

        [HarmonyPrefix, HarmonyPatch(typeof(PlayerMovement), "Move")]
        private static bool PlayerMovement_Move(PlayerMovement __instance) =>
            !(PlayerHold.Active && PlayerHold.IsLocal(__instance));

        [HarmonyPostfix, HarmonyPatch(typeof(PlayerMovement), "FixedUpdate")]
        private static void PlayerMovement_FixedUpdate(PlayerMovement __instance)
        {
            if (PlayerHold.Active && PlayerHold.IsLocal(__instance)) PlayerHold.Apply();
        }

        [HarmonyPostfix, HarmonyPatch(typeof(PlayerMovement), "LateUpdate")]
        private static void PlayerMovement_LateUpdate(PlayerMovement __instance)
        {
            if (PlayerHold.Active && PlayerHold.IsLocal(__instance)) PlayerHold.Apply();
        }

        /// <summary>Advance the pose before the camera places itself, so the view never trails a frame.</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(PlayerCamera), "Update")]
        private static void PlayerCamera_Update(PlayerCamera __instance)
        {
            if (PlayerHold.Active && IsLocalCamera(__instance)) PlayerHold.FrameUpdate();
        }

        [HarmonyPostfix, HarmonyPatch(typeof(PlayerCamera), "SetFov")]
        private static void PlayerCamera_SetFov(PlayerCamera __instance)
        {
            float off = MegaFx.FovOffset;
            if (Mathf.Abs(off) < 0.01f || FCam == null || !IsLocalCamera(__instance)) return;
            try
            {
                var cam = FCam.GetValue(__instance) as Camera;
                if (cam != null) cam.fieldOfView = Mathf.Clamp(cam.fieldOfView + off, 20f, 120f);
            }
            catch { }
        }

        /// <summary>E: let go of the rope / grab the teeth, instead of picking something up.</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(PlayerHolding), "PickUpInput")]
        private static bool PlayerHolding_PickUpInput()
        {
            // Chat and menus don't switch input actions off; the game checks BlockInputs itself.
            // So must we, or typing "e" in chat drops the rider in front of the megalodon.
            Player me = Player.LocalPlayer;
            if (me == null || me.BlockInputs) return true;
            if (MegaStomach.Active) { MegaStomach.Grab(); return false; }
            if (Wakeboard.Riding && Time.time - _ridingSince > 0.4f) { Wakeboard.LetGo("let go"); return false; }
            return true;
        }

        private static float _ridingSince;
        internal static void NoteRideStart() => _ridingSince = Time.time;

        // ------------------------------------------------------------------ bullets

        /// <summary>
        /// Bullets are swept against the megalodon's capsule before the game's own scan. Only the
        /// shooter's copy reports damage (the game's own rule for hits); everyone removes the bullet
        /// and sees blood, so nobody watches a round sail through it.
        /// </summary>
        [HarmonyPrefix, HarmonyPatch(typeof(ProjectileManager), "UpdateProjectileScan")]
        private static bool ProjectileManager_UpdateProjectileScan(ProjectileManager __instance, Projectile projectile, ProjectileType type)
        {
            try
            {
                if (projectile == null || projectile.FromNpc || !SharkVisual.Present) return true;
                if (type != null && type.ProjectilesToRemove.Contains(projectile)) return true;

                Vector3 p0 = projectile.Position;
                Vector3 p1 = p0 + projectile.Velocity * Time.fixedDeltaTime * (1f + projectile.CatchingUpToDo * 0.5f);
                Vector3 hit;
                if (!SharkVisual.SegmentHits(p0, p1, type != null ? type.WidthRadius : 0.05f, out hit)) return true;

                try
                {
                    DazedUtils.PlayCreatureHitEffects(hit, projectile.Velocity.normalized, projectile.Damage, 2.5f, false, false, 100, projectile.Owner);
                }
                catch { try { ParticleManager.Play("Blood", hit, -projectile.Velocity.normalized); } catch { } }

                if (projectile.IsLocal)
                {
                    int damage = projectile.Damage;
                    ModNet.SendToServer(Msg.MegaHit, w => { w.Write(damage); Cannonballs.WriteVec(w, hit); });
                    try { AudioManager.PlayGlobalClip("Hitmarker", false, 0.6f, 0.02f, false); } catch { }
                }
                MRemove?.Invoke(__instance, new object[] { projectile });
                return false;
            }
            catch (Exception e)
            {
                Diag.Exception("Patch UpdateProjectileScan (megalodon)", e);
                return true;
            }
        }

        // ------------------------------------------------------------------ explosions

        internal struct Blast
        {
            public bool Valid;
            public Vector3 Position;
            public float Radius;
            public int Damage;
        }

        [HarmonyPrefix, HarmonyPatch(typeof(ExplosionManager), "ServerExplode")]
        private static void ExplosionManager_ServerExplode_Pre(Item item, ExplosionInfo info, out Blast __state)
        {
            __state = default(Blast);
            try
            {
                if (item == null || info == null || !SharkBrain.Active) return;
                if (info.HasExploded && info.OnlyExplodeOnce) return;
                __state = new Blast { Valid = true, Position = item.transform.position, Radius = info.DamageRadius, Damage = info.Damage };
            }
            catch (Exception e) { Diag.Exception("Patch ServerExplode (megalodon, pre)", e); }
        }

        [HarmonyPostfix, HarmonyPatch(typeof(ExplosionManager), "ServerExplode")]
        private static void ExplosionManager_ServerExplode_Post(Blast __state)
        {
            try
            {
                if (!__state.Valid || !InstanceFinder.IsServerStarted) return;
                if (Cannonballs.DetonatingPirateBall) return;   // its own burped mine, or the pirates' shot
                float mult = MegaBoom.Active ? MegaBoom.Multiplier : 1f;
                if (mult <= 0f) return;
                SharkBrain.HostExplosion(__state.Position, __state.Radius, __state.Damage, mult);
            }
            catch (Exception e) { Diag.Exception("Patch ServerExplode (megalodon, post)", e); }
        }

        // ------------------------------------------------------------------ the boat

        [HarmonyPrefix, HarmonyPatch(typeof(Boat), "ApplyInputForce")]
        private static bool Boat_ApplyInputForce() => !MegaBoat.Stalled;

        /// <summary>No drifting home while Old Salt has the helm.</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(Boat), "ApplyReturnForce")]
        private static bool Boat_ApplyReturnForce() => !MegaBoat.Autopilot;

        [HarmonyPostfix, HarmonyPatch(typeof(Boat), "FixedUpdate")]
        private static void Boat_FixedUpdate(Boat __instance)
        {
            try { MegaBoat.ServerFixed(__instance); }
            catch (Exception e) { Diag.Exception("Patch Boat.FixedUpdate (megalodon)", e); }
        }
    }
}
