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
                root.tag = "Interactable";   // the game's look-at only considers colliders with this tag

                GameObject model = ModAssets.Create("cannon", pos, anchor.transform.rotation, root.transform, solid: false);
                if (model != null) model.transform.localScale *= PirateModule.Cfg.DeckCannonScale.Value;
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
            pos = anchor.position;

            // The motors on display: every motor stand in the shop, measured by what you actually see
            // (the models the game outlines), not by the objects' pivots.
            bool any = false;
            Bounds row = default(Bounds);
            int stands = 0;
            foreach (MotorPurchasable m in UnityEngine.Object.FindObjectsByType<MotorPurchasable>())
            {
                if (m == null || !m.isActiveAndEnabled || (m.transform.position - anchor.position).sqrMagnitude > 15f * 15f) continue;
                stands++;
                foreach (Renderer r in DisplayRenderers(m))
                {
                    if (!any) { row = r.bounds; any = true; }
                    else row.Encapsulate(r.bounds);
                }
            }
            if (!any) row = new Bounds(anchor.position, Vector3.one * 0.6f);

            // Line up at either end of the row, on whatever the motors stand on. Searching from just
            // above the motors' base - never from high up - keeps it off roofs and shelves above.
            Vector3 along = row.size.x >= row.size.z ? Vector3.right : Vector3.forward;
            float half = row.size.x >= row.size.z ? row.extents.x : row.extents.z;
            float baseY = row.min.y;
            Vector3[] candidates =
            {
                row.center + along * (half + 0.9f), row.center - along * (half + 0.9f),
                row.center + along * (half + 1.6f), row.center - along * (half + 1.6f),
                anchor.position + anchor.forward * 1.2f, anchor.position - anchor.forward * 1.2f
            };

            foreach (Vector3 c in candidates)
            {
                Vector3 probe = new Vector3(c.x, baseY + 0.4f, c.z);
                RaycastHit hit;
                if (!Physics.Raycast(probe, Vector3.down, out hit, 2.5f, ~0, QueryTriggerInteraction.Ignore)) continue;
                if (Vector3.Dot(hit.normal, Vector3.up) < 0.8f) continue;

                Vector3 spot = hit.point;
                if (Physics.CheckSphere(spot + Vector3.up * 0.5f, 0.3f, ~0, QueryTriggerInteraction.Ignore)) continue;

                pos = spot;
                Diag.Info("ShopCannon: " + stands + " motor stand(s), display row " + row.center.ToString("F1") + " size " +
                          row.size.ToString("F1") + "; gun set on '" + hit.collider.name + "' at " + spot.ToString("F1") + ".");
                return true;
            }

            Diag.Warn("ShopCannon: no clear floor beside the motor row at " + row.center.ToString("F1") +
                      " (base y " + baseY.ToString("F1") + ").");
            return false;
        }

        private static IEnumerable<Renderer> DisplayRenderers(MotorPurchasable m)
        {
            var list = new List<Renderer>();
            if (Outline?.GetValue(m) is GameObject[] models)
                foreach (GameObject g in models)
                    if (g != null)
                        foreach (Renderer r in g.GetComponentsInChildren<Renderer>(false))
                            if (r != null && r.enabled) list.Add(r);
            if (list.Count == 0)
                foreach (Renderer r in m.GetComponentsInChildren<Renderer>(false))
                    if (r != null && r.enabled) list.Add(r);
            return list;
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
