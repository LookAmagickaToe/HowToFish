using System;
using FishNet;
using HarmonyLib;
using UnityEngine;

namespace Expanded.Quests
{
    /// <summary>
    /// Game hooks that feed the quest engine. Kept to a minimum: every hook is a thing a game
    /// update can break, so we listen to one broad event (a creature dying) rather than many
    /// specific ones, and derive the rest from it.
    /// </summary>
    internal static class QuestPatches
    {
        /// <summary>
        /// Fires for every creature death, on every client. Gated to the host, which is the only
        /// machine that owns quest progress.
        ///
        /// A death is reported as both a kill and a catch: in this game you land a fish and then it
        /// dies, so one event serves "kill N of X" and "catch an X over N kg" objectives alike.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Creature), "OnDeath")]
        private static void Creature_OnDeath(Creature __instance)
        {
            try
            {
                if (!InstanceFinder.IsServerStarted) return;
                QuestModule qm = QuestModule.Instance;
                if (qm == null || __instance == null) return;

                string key = QuestModule.CreatureKey(__instance);
                if (key.Length == 0) return;

                qm.Raise(QuestEvent.Killed(key));

                int decigrams = WeightInDecigrams(__instance);
                if (decigrams > 0) qm.Raise(QuestEvent.Caught(key, decigrams));
            }
            catch (Exception e)
            {
                Diag.Exception("Patch Creature.OnDeath (quests)", e);
            }
        }

        /// <summary>
        /// Base weight is a protected field declared on Item and inherited by Creature, so the
        /// lookup must target the declaring type. Resolved once; null means a game update moved or
        /// renamed it, and weight-based objectives degrade to "unknown" instead of throwing.
        /// </summary>
        private static readonly System.Reflection.FieldInfo WeightField = AccessTools.Field(typeof(Item), "_weight");

        /// <summary>
        /// Weight in tenths of a gram, as an integer, so quest thresholds never depend on float
        /// comparison.
        /// </summary>
        private static int WeightInDecigrams(Creature c)
        {
            try
            {
                if (WeightField == null) return 0;
                float baseWeight = (float)WeightField.GetValue(c);
                if (baseWeight <= 0f) return 0;
                float kg = baseWeight * c.RandomizedWeight;
                return Mathf.RoundToInt(kg * 10000f);
            }
            catch (Exception e)
            {
                Diag.Debug("Could not read creature weight: " + e.Message);
                return 0;
            }
        }
    }
}
