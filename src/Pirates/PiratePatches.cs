using System;
using FishNet;
using HarmonyLib;
using UnityEngine;

namespace Expanded.Pirates
{
    /// <summary>
    /// The two ways the pirate ship takes damage. The ship is not an item or creature, so the game
    /// has no idea it exists; these hooks let the game's own weapons reach it.
    /// </summary>
    internal static class PiratePatches
    {
        internal struct Blast
        {
            public bool Valid;
            public Vector3 Position;
            public float Radius;
            public int Damage;
        }

        /// <summary>
        /// Every explosion in the game - thrown dynamite, a crew cannonball, a chain reaction -
        /// goes through ServerExplode. Record where it happened before the item is despawned.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ExplosionManager), "ServerExplode")]
        private static void ExplosionManager_ServerExplode_Pre(Item item, ExplosionInfo info, out Blast __state)
        {
            __state = default(Blast);
            try
            {
                if (item == null || info == null) return;
                // Mirror the game's own early-out, or a dud would still damage the ship.
                if (info.HasExploded && info.OnlyExplodeOnce) return;

                __state = new Blast
                {
                    Valid = true,
                    Position = item.transform.position,
                    Radius = info.DamageRadius,
                    Damage = info.Damage
                };
            }
            catch (Exception e)
            {
                Diag.Exception("Patch ServerExplode (pre)", e);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ExplosionManager), "ServerExplode")]
        private static void ExplosionManager_ServerExplode_Post(Blast __state)
        {
            try
            {
                if (!__state.Valid || !InstanceFinder.IsServerStarted) return;
                if (Cannonballs.DetonatingPirateBall) return; // their own shot never hurts them

                PirateShip ship = PirateShip.Active;
                if (ship == null || !ship.Alive) return;

                ship.ServerTakeExplosion(__state.Position, __state.Radius, __state.Damage);
            }
            catch (Exception e)
            {
                Diag.Exception("Patch ServerExplode (post)", e);
            }
        }

        /// <summary>
        /// Bullets are simulated on every client, but only the shooter's copy counts - the same rule
        /// the game uses for hitting fish and players. So the shooter reports the hit to the host,
        /// which validates it before applying damage.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ProjectileManager), "Hit")]
        private static void ProjectileManager_Hit_Post(Projectile projectile, RaycastHit hit)
        {
            try
            {
                if (projectile == null || !projectile.IsLocal) return;
                if (projectile.FromNpc) return;
                if (hit.collider == null) return;
                if (hit.collider.GetComponentInParent<PirateShip>() == null) return;

                int damage = projectile.Damage;
                Vector3 point = hit.point;
                ModNet.SendToServer(Msg.ShipHit, w =>
                {
                    w.Write(damage);
                    Cannonballs.WriteVec(w, point);
                });
            }
            catch (Exception e)
            {
                Diag.Exception("Patch ProjectileManager.Hit", e);
            }
        }
    }
}
