using System;
using FishNet;
using HarmonyLib;
using UnityEngine;

namespace SeagullSwarm
{
    /// <summary>
    /// Every hook is wrapped: an exception in mod code is logged (rate-limited) and the game carries
    /// on with vanilla behaviour, instead of the exception escaping into BirdManager.Update and
    /// freezing every bird in the world, vanilla ones included.
    /// </summary>
    internal static class Patches
    {
        /// <summary>
        /// Session anchor. BirdManager is a NetworkBehaviour that exists in every island scene,
        /// and it is the object whose Update() drives all bird simulation, so it is the natural
        /// place to hang the director. OnStartServer only fires on the host.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BirdManager), "OnStartServer")]
        private static void BirdManager_OnStartServer(BirdManager __instance)
        {
            try
            {
                if (__instance.GetComponent<SwarmDirector>() != null) return;
                __instance.gameObject.AddComponent<SwarmDirector>();
                Diag.Info("Swarm director attached to BirdManager (scene '" +
                          __instance.gameObject.scene.name + "').");
            }
            catch (Exception e)
            {
                Diag.Exception("Patch BirdManager.OnStartServer", e);
            }
        }

        /// <summary>
        /// Ambient AI override. SimulateAllBirds() walks BirdManager's own list every Update on the
        /// server and calls this per bird; skipping it for our birds hands us full control of
        /// position and anim state, while SendBirdPos() keeps replicating them to clients for free.
        /// If our AI throws, the bird falls back to vanilla flight rather than freezing.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BirdManager), "SimulateBird")]
        private static bool BirdManager_SimulateBird(Bird bird)
        {
            try
            {
                if (bird == null) return true;

                GullAttacker attacker = bird.GetComponent<GullAttacker>();
                if (attacker == null) return true;

                attacker.SimulateServer(Time.deltaTime);
                return false;
            }
            catch (Exception e)
            {
                Diag.Exception("GullAttacker.SimulateServer", e);
                return true;
            }
        }

        /// <summary>
        /// Provocation counter. Runs on every client, so gate it to the server. Swarm birds are
        /// excluded: only gulls the players hunted of their own accord count toward the grudge.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Bird), "OnDeath")]
        private static void Bird_OnDeath(Bird __instance)
        {
            try
            {
                if (!InstanceFinder.IsServerStarted) return;
                if (__instance == null) return;
                if (__instance.GetComponent<GullAttacker>() != null) return;

                SwarmDirector d = SwarmDirector.Active;
                if (d != null) d.RegisterProvocationKill();
                else Diag.Warn("Seagull died but no director is active - kill not counted.");
            }
            catch (Exception e)
            {
                Diag.Exception("Patch Bird.OnDeath", e);
            }
        }

        /// <summary>
        /// BossManager recomputes the boss bar's maximum from the prefab's own health whenever the
        /// player count changes, and heals the boss up to it. For our Albatross that would jump its
        /// 500 HP back toward the vanilla 7800. Suppress it while our leader owns the bar.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BossManager), "UpdateBossMaxHp")]
        private static bool BossManager_UpdateBossMaxHp()
        {
            try
            {
                SwarmDirector d = SwarmDirector.Active;
                if (d == null || !d.OwnsBossBar) return true;

                Diag.Info("Player count changed mid-encounter; kept the Albatross at its configured HP.");
                return false;
            }
            catch (Exception e)
            {
                Diag.Exception("Patch BossManager.UpdateBossMaxHp", e);
                return true;
            }
        }

        /// <summary>
        /// Damage accounting. During an encounter, log every HP loss that did NOT come from a gull
        /// hit - Albatross droppings, its dive, drowning, falls - so the log explains every death.
        /// Read-only: never changes the damage.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlayerVitals), "TakeDamage")]
        private static void PlayerVitals_TakeDamage_Pre(PlayerVitals __instance, out int __state)
        {
            __state = -1;
            try { __state = __instance.Health; }
            catch { /* logging only */ }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerVitals), "TakeDamage")]
        private static void PlayerVitals_TakeDamage_Post(PlayerVitals __instance, int __state, int amount)
        {
            try
            {
                if (__state < 0 || GullAttacker.ApplyingHit) return;
                if (!InstanceFinder.IsServerStarted) return;

                SwarmDirector d = SwarmDirector.Active;
                if (d == null || !d.InEncounterPublic) return;

                int after = __instance.Health;
                if (after >= __state) return;

                Player p = Traverse.Create(__instance).Field("_player").GetValue<Player>();
                string name = p != null ? p.SteamName : "?";
                Diag.Info("DAMAGE " + name + " took " + (__state - after) + " from a non-gull source " +
                          "(Albatross droppings/dive, water, fall...): hp " + __state + " -> " + after +
                          " (raw " + amount + ")");
            }
            catch (Exception e)
            {
                Diag.Exception("Patch PlayerVitals.TakeDamage", e);
            }
        }
    }
}
