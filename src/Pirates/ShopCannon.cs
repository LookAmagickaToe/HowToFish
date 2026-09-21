using System;
using System.Collections.Generic;
using System.Reflection;
using Expanded.Content;
using Expanded.Quests;
using FishNet;
using FishNet.Connection;
using HarmonyLib;
using UnityEngine;

namespace Expanded.Pirates
{
    /// <summary>
    /// Sells the swivel gun in the island shop, right next to the boat motors, and behaves exactly
    /// like them: look at it for the name and price (red when you cannot afford it), press the game's
    /// own interact key to buy.
    ///
    /// It is a real subclass of the game's Purchasable, so the game's own look-at and interaction code
    /// drives it. The display is local on every client; the purchase is a request the host validates.
    /// </summary>
    internal sealed class CannonPurchasable : Purchasable
    {
        internal int Price;

        public override bool InteractableWhenHoldingItem => true;

        public override void Hover()
        {
            bool owned = SharedState.Has(PirateStory.UnlockCannon);
            _customCost = Price;
            _customCanBuy = !owned;
            _hoverString = "Swivel gun\n$" + Price + "\n" + (owned ? "Already bought" : SafePickUpHint());
            base.Hover();
        }

        public override void Interact(Player player)
        {
            try
            {
                base.Interact(player);
                if (SharedState.Has(PirateStory.UnlockCannon)) return;

                if (!MoneyManager.CanAfford(Price))
                {
                    CantBuyEffects();
                    return;
                }
                ModNet.SendToServer(Msg.RequestBuy, w => w.Write(ShopCannon.ItemId));
            }
            catch (Exception e)
            {
                Diag.Exception("CannonPurchasable.Interact", e);
            }
        }

        private static string SafePickUpHint()
        {
            try { return SpriteManager.GetPickUpInput(); } catch { return "[E]"; }
        }
    }

    internal static class ShopCannon
    {
        internal const string ItemId = "cannon";

        // Interactable keeps its collider, text anchor and outline list in serialized fields that
        // are normally filled in the editor. They must be set before Awake runs, which is why the
        // object is built inactive and only switched on afterwards.
        private static readonly FieldInfo InteractCol = AccessTools.Field(typeof(Interactable), "_interactCol");
        private static readonly FieldInfo TextTarget = AccessTools.Field(typeof(Interactable), "_textTarget");
        private static readonly FieldInfo Outline = AccessTools.Field(typeof(Interactable), "_modelsToOutline");

        private static GameObject _display;
        private static MotorPurchasable _builtFor;   // the shop we last tried, successful or not
        private static float _nextScan;

        /// <summary>True if this island has a shop the gun is on display in.</summary>
        internal static bool OnDisplay => _display != null;

        // ------------------------------------------------------------------ display (every client)

        /// <summary>
        /// Looks for the motor display at most once a second, and tries to set up next to each shop
        /// exactly once: a shop with no free spot must not be retried every frame.
        /// </summary>
        internal static void ClientTick()
        {
            if (Time.time < _nextScan) return;
            _nextScan = Time.time + 1f;

            MotorPurchasable anchor = FindAnchor();
            if (anchor == _builtFor) return;   // Unity's null check also covers a destroyed shop

            Clear();
            _builtFor = anchor;
            if (anchor != null) Build(anchor);
        }

        private static MotorPurchasable FindAnchor()
        {
            try
            {
                foreach (MotorPurchasable m in UnityEngine.Object.FindObjectsByType<MotorPurchasable>())
                    if (m != null && m.isActiveAndEnabled) return m;
            }
            catch (Exception e)
            {
                Diag.Debug("ShopCannon: scan failed (" + e.Message + ").");
            }
            return null;
        }

        private static void Build(MotorPurchasable anchor)
        {
            if (InteractCol == null || TextTarget == null || Outline == null)
            {
                Diag.Error("ShopCannon: the game's Interactable fields changed; the gun cannot be sold in the shop.");
                return;
            }

            Vector3 pos;
            if (!FindSpot(anchor.transform, out pos))
            {
                Diag.Warn("ShopCannon: no free spot next to the motors.");
                return;
            }

            try
            {
                var root = new GameObject(BoatMount.MountRoot + "ShopCannon");
                root.SetActive(false);
                root.transform.SetPositionAndRotation(pos, anchor.transform.rotation);
                root.layer = LayerMask.NameToLayer("Interactable");

                GameObject model = ModAssets.Create("cannon", pos, anchor.transform.rotation, root.transform, solid: false);
                Bounds b = model != null ? ModAssets.Measure(model) : new Bounds(pos + Vector3.up * 0.4f, new Vector3(0.8f, 0.8f, 1.2f));

                // The collider the game's look-at raycast hits: a box around the model.
                BoxCollider box = root.AddComponent<BoxCollider>();
                box.center = root.transform.InverseTransformPoint(b.center);
                box.size = new Vector3(Mathf.Max(0.6f, b.size.x), Mathf.Max(0.6f, b.size.y), Mathf.Max(0.6f, b.size.z));

                var label = new GameObject("TextTarget").transform;
                label.SetParent(root.transform, false);
                label.position = new Vector3(b.center.x, b.max.y + 0.3f, b.center.z);

                CannonPurchasable buy = root.AddComponent<CannonPurchasable>();
                buy.Price = Mathf.Max(0, PirateModule.Cfg.CannonPrice.Value);
                InteractCol.SetValue(buy, box);
                TextTarget.SetValue(buy, label);
                Outline.SetValue(buy, OutlineTargets(model));

                root.SetActive(true);
                _display = root;
                Diag.Info("ShopCannon: swivel gun for sale next to the motors at " + pos.ToString("F1") + ".");
            }
            catch (Exception e)
            {
                Diag.Exception("ShopCannon.Build", e);
            }
        }

        private static GameObject[] OutlineTargets(GameObject model)
        {
            var list = new List<GameObject>();
            if (model != null)
                foreach (Renderer r in model.GetComponentsInChildren<Renderer>(true)) list.Add(r.gameObject);
            return list.ToArray();
        }

        /// <summary>
        /// A free spot beside the motor display: on a surface, with room for the gun, and not inside
        /// the shelf or the motor itself.
        /// </summary>
        private static bool FindSpot(Transform anchor, out Vector3 pos)
        {
            Vector3[] offsets =
            {
                anchor.right * 1.4f, -anchor.right * 1.4f, anchor.forward * 1.3f,
                anchor.right * 2.4f, -anchor.right * 2.4f, -anchor.forward * 1.3f
            };

            foreach (Vector3 off in offsets)
            {
                Vector3 probe = anchor.position + off + Vector3.up * 1.5f;
                RaycastHit hit;
                if (!Physics.Raycast(probe, Vector3.down, out hit, 4f, ~0, QueryTriggerInteraction.Ignore)) continue;
                if (Vector3.Dot(hit.normal, Vector3.up) < 0.8f) continue;

                Vector3 spot = hit.point;
                if (Physics.CheckSphere(spot + Vector3.up * 0.55f, 0.35f, ~0, QueryTriggerInteraction.Ignore)) continue;

                pos = spot;
                return true;
            }
            pos = anchor.position;
            return false;
        }

        internal static void Clear()
        {
            if (_display != null) UnityEngine.Object.Destroy(_display);
            _display = null;
            _builtFor = null;
        }

        // ------------------------------------------------------------------ purchase (host)

        internal static void RegisterServerHandler()
        {
            ModNet.OnServer(Msg.RequestBuy, (conn, r) =>
            {
                string item = r.ReadString();
                if (item == ItemId) ServerBuy(conn);
            });
        }

        /// <summary>
        /// Same checks the game makes when you buy a motor, done on the host: not already owned,
        /// money actually there. RemoveMoney plays the till sound for everyone.
        /// </summary>
        internal static void ServerBuy(NetworkConnection conn)
        {
            if (!InstanceFinder.IsServerStarted) return;
            if (SharedState.Has(PirateStory.UnlockCannon)) return;

            int price = Mathf.Max(0, PirateModule.Cfg.CannonPrice.Value);
            if (!MoneyManager.CanAfford(price))
            {
                Diag.Debug("ShopCannon: purchase rejected, not enough money.");
                return;
            }

            Player buyer = PlayerFor(conn);
            MoneyManager.RemoveMoney(price, buyer != null ? buyer : Player.LocalPlayer);
            SharedState.Grant(PirateStory.UnlockCannon);
            QuestModule.Instance?.SetFlag(PirateStory.FlagCannonBought);

            PirateModule.Announce((buyer != null ? buyer.SteamName : "Someone") +
                                  " bought a swivel gun. It's bolted to the bow - stand at it and press " +
                                  PirateModule.Cfg.InteractKey.Value + " to fire.");
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
