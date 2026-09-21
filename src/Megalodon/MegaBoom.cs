using System;
using System.Reflection;
using FishNet;
using HarmonyLib;
using UnityEngine;

namespace Expanded.Megalodon
{
    /// <summary>
    /// Host: sets off a real game explosion (the game's own dynamite, detonated on the spot), so
    /// damage, knockback, splash and stunned fish are exactly the game's. Same approach as the
    /// cannonballs. While it goes off, <see cref="Multiplier"/> tells the megalodon's explosion hook
    /// how much this particular blast should hurt it (0 = handled separately).
    /// </summary>
    internal static class MegaBoom
    {
        private static readonly FieldInfo FishField = AccessTools.Field(typeof(ExplosionInfo), "_underwaterFishMinMax");

        internal static bool Active { get; private set; }
        internal static float Multiplier { get; private set; } = 1f;

        internal static void Explode(Vector3 at, bool underwater, float multiplier, bool fish)
        {
            if (!InstanceFinder.IsServerStarted) return;
            try
            {
                Item prefab = GameInfo.GetSpawnable("dynamite");
                if (prefab == null || ItemManager.Instance == null)
                {
                    Diag.Warn("MegaBoom: dynamite prefab unavailable; no explosion.");
                    return;
                }

                Item charge = ItemManager.Instance.SpawnNewItem(prefab, at, Quaternion.identity);
                if (charge == null) return;
                ExplosionInfo info = charge.GetExplosionInfo();
                if (info == null)
                {
                    charge.DestroyItem((byte)DestroyReason.Immediate);
                    return;
                }

                object fishBefore = null;
                if (underwater && !fish && FishField != null)
                {
                    fishBefore = FishField.GetValue(info);
                    FishField.SetValue(info, Vector2Int.zero);
                }

                Active = true;
                Multiplier = multiplier;
                try
                {
                    ExplosionManager.ServerExplode(charge, info);
                }
                finally
                {
                    Active = false;
                    Multiplier = 1f;
                    if (fishBefore != null) FishField.SetValue(info, fishBefore);
                }
            }
            catch (Exception e)
            {
                Diag.Exception("MegaBoom.Explode", e);
            }
        }
    }
}
