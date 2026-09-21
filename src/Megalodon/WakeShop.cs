using System;
using System.Collections.Generic;
using System.Reflection;
using Expanded.Content;
using Expanded.Pirates;
using Expanded.Quests;
using FishNet;
using FishNet.Connection;
using HarmonyLib;
using UnityEngine;

namespace Expanded.Megalodon
{
    /// <summary>
    /// "Wakeboard and tow line" for sale in the island shop, beside the motors (and the swivel gun),
    /// bought exactly like them: look at it, press E. Once bought, it leans by the helm on your boat.
    /// </summary>
    internal sealed class WakeboardPurchasable : Purchasable
    {
        internal int Price;

        public override bool InteractableWhenHoldingItem => true;

        public override void Hover()
        {
            bool owned = WakeRig.Owned;
            _customCost = Price;
            _customCanBuy = !owned;
            string key = "[E]";
            try { key = SpriteManager.GetPickUpInput(); } catch { }
            _hoverString = "Wakeboard & tow line\n$" + Price + "\n" + (owned ? "Already bought - it's by the helm" : key);
            base.Hover();
        }

        public override void Interact(Player player)
        {
            try
            {
                base.Interact(player);
                if (WakeRig.Owned) return;
                if (!MoneyManager.CanAfford(Price)) { CantBuyEffects(); return; }
                ModNet.SendToServer(Msg.RequestBuy, w => w.Write(WakeShop.ItemId));
            }
            catch (Exception e)
            {
                Diag.Exception("WakeboardPurchasable.Interact", e);
            }
        }
    }

    internal static class WakeShop
    {
        internal const string ItemId = "wakeboard";

        private static readonly FieldInfo InteractCol = AccessTools.Field(typeof(Interactable), "_interactCol");
        private static readonly FieldInfo TextTarget = AccessTools.Field(typeof(Interactable), "_textTarget");
        private static readonly FieldInfo Outline = AccessTools.Field(typeof(Interactable), "_modelsToOutline");

        private static GameObject _display;
        private static MotorPurchasable _builtFor;
        private static float _nextScan;

        internal static void RegisterServerHandler()
        {
            ModNet.OnServer(Msg.RequestBuy, (conn, r) =>
            {
                string item = r.ReadString();
                if (item == ItemId) ServerBuy(conn);
            });
        }

        internal static void ClientTick()
        {
            if (Time.time < _nextScan) return;
            _nextScan = Time.time + 1f;

            // Let the swivel gun claim its spot first, so the two never end up in the same place.
            if (!ShopCannon.OnDisplay && Time.time < _firstScanAt + 3f) return;

            MotorPurchasable anchor = null;
            try
            {
                foreach (MotorPurchasable m in UnityEngine.Object.FindObjectsByType<MotorPurchasable>())
                    if (m != null && m.isActiveAndEnabled) { anchor = m; break; }
            }
            catch (Exception e) { Diag.Debug("WakeShop: scan failed (" + e.Message + ")."); }

            if (anchor == _builtFor) return;
            Clear();
            _builtFor = anchor;
            if (anchor != null) Build(anchor);
        }

        private static float _firstScanAt = -1f;

        internal static void SessionStart() => _firstScanAt = Time.time;

        private static void Build(MotorPurchasable anchor)
        {
            if (InteractCol == null || TextTarget == null || Outline == null) return;

            Physics.SyncTransforms();
            Vector3 pos;
            if (!ShopCannon.FindSpot(anchor.transform, out pos))
            {
                Diag.Warn("WakeShop: no free spot next to the motors.");
                return;
            }

            try
            {
                var root = new GameObject(BoatMount.MountRoot + "ShopWakeboard");
                root.SetActive(false);
                root.transform.SetPositionAndRotation(pos, anchor.transform.rotation);
                root.layer = LayerMask.NameToLayer("Interactable");
                root.tag = "Interactable";

                // Stood on its tail, leaning back a little, like in a surf shop.
                Transform board = MegaShapes.Wakeboard(root.transform);
                Vector3 along = (Vector3.up + Vector3.back * 0.2f).normalized;
                board.localRotation = Quaternion.LookRotation(along, Vector3.forward);
                board.localPosition = along * 0.72f;

                BoxCollider box = root.AddComponent<BoxCollider>();
                box.center = new Vector3(0f, 0.75f, 0f);
                box.size = new Vector3(0.6f, 1.5f, 0.5f);

                var label = new GameObject("TextTarget").transform;
                label.SetParent(root.transform, false);
                label.localPosition = Vector3.up * 1.65f;

                WakeboardPurchasable buy = root.AddComponent<WakeboardPurchasable>();
                buy.Price = Mathf.Max(0, MegaModule.Cfg.WakeboardPrice.Value);
                InteractCol.SetValue(buy, box);
                TextTarget.SetValue(buy, label);
                var outline = new List<GameObject>();
                foreach (Renderer rr in board.GetComponentsInChildren<Renderer>(true)) outline.Add(rr.gameObject);
                Outline.SetValue(buy, outline.ToArray());

                root.SetActive(true);
                _display = root;
                Diag.Info("WakeShop: wakeboard for sale at " + pos.ToString("F1") + ".");
            }
            catch (Exception e)
            {
                Diag.Exception("WakeShop.Build", e);
            }
        }

        internal static void Clear()
        {
            if (_display != null) UnityEngine.Object.Destroy(_display);
            _display = null;
            _builtFor = null;
        }

        private static void ServerBuy(NetworkConnection conn)
        {
            if (!InstanceFinder.IsServerStarted || WakeRig.Owned) return;
            int price = Mathf.Max(0, MegaModule.Cfg.WakeboardPrice.Value);
            if (!MoneyManager.CanAfford(price)) return;

            Player buyer = MegaModule.PlayerFor(conn);
            MoneyManager.RemoveMoney(price, buyer != null ? buyer : Player.LocalPlayer);
            SharedState.Grant(MegalodonStory.UnlockWakeboard);
            QuestModule.Instance?.SetFlag(MegalodonStory.FlagWakeboardBought);
            MegaModule.Announce((buyer != null ? buyer.SteamName : "Someone") +
                                " bought a wakeboard and tow line. It leans by the helm - press E at it to grab the rope, " +
                                "then have someone drive. A/D carve, Space jumps, E lets go. Far out at sea, something may notice.");
        }
    }
}
