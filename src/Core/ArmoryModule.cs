using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using FishNet;
using UnityEngine;

namespace Expanded
{
    /// <summary>
    /// Testing aid: the debug key drops one of the game's own weapons in front of you, so the pirate
    /// crew can be shot without first earning a gun. Uses the game's normal item spawn, so the weapon
    /// is a real, networked item everyone sees and anyone can pick up. Host only, like every spawn.
    /// </summary>
    internal sealed class ArmoryModule : ModuleBase
    {
        internal override string Id => "Armory";
        internal override string DisplayName => "Test Weapon";
        internal override KeyCode DefaultDebugKey => KeyCode.F7;

        private ConfigEntry<string> _item;
        private int _given;

        internal override void Configure(ConfigFile config)
        {
            _item = config.Bind(Id, "Item", "pistol",
                "Item the debug key spawns (the game's item name, lower case without spaces). " +
                "If it doesn't exist, the first weapon the game has is used and all weapon names are logged.");
        }

        internal override void OnDebugKey()
        {
            if (!InstanceFinder.IsServerStarted) { Say("Only the host can spawn items."); return; }
            if (ItemManager.Instance == null) { Say("No world loaded yet."); return; }

            Item prefab = GameInfo.GetSpawnable(_item.Value.Replace(" ", "").ToLowerInvariant());
            if (prefab == null)
            {
                List<Item> weapons = AllWeapons();
                Diag.Info("Armory: '" + _item.Value + "' not found. Weapons in the game: " +
                          (weapons.Count > 0 ? string.Join(", ", weapons.ConvertAll(w => w.name).ToArray()) : "none") + ".");
                if (weapons.Count == 0) { Say("The game has no weapon items to give."); return; }
                prefab = weapons[0];
            }

            Transform view = null;
            try { if (GameInfo.CurCamera != null) view = GameInfo.CurCamera.transform; } catch { }
            if (view == null && Player.LocalPlayer != null) view = Player.LocalPlayer.Transform;
            if (view == null) { Say("No player to hand it to."); return; }

            Vector3 forward = view.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            forward.Normalize();
            Vector3 at = view.position + forward * 1.2f + Vector3.down * 0.3f;

            try
            {
                Item spawned = ItemManager.Instance.SpawnNewItem(prefab, at, Quaternion.LookRotation(forward));
                if (spawned == null) { Say("Spawning '" + prefab.name + "' failed."); return; }
                _given++;
                Say(prefab.name + " dropped in front of you - pick it up with the interact key.");
            }
            catch (Exception e)
            {
                Diag.Exception("Armory spawn", e);
                Say("Spawning '" + prefab.name + "' failed, see the log.");
            }
        }

        private static List<Item> AllWeapons()
        {
            var list = new List<Item>();
            foreach (Item item in Resources.LoadAll<Item>("Items"))
                if (item is Weapon) list.Add(item);
            list.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            return list;
        }

        private static void Say(string text)
        {
            Diag.Info("[armory] " + text);
            try { ChatManager.ChatMessage("<color=#C0C0C0>[Armory]</color> " + text); }
            catch { /* chat not ready */ }
        }

        internal override string StatusLine() => IsEnabled ? _given + " given, item '" + _item.Value + "'" : "disabled";
    }
}
