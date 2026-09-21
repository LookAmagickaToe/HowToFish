using System;
using System.Collections.Generic;
using System.Reflection;
using Expanded.Content;
using HarmonyLib;
using UnityEngine;

namespace Expanded.Pirates
{
    /// <summary>
    /// The reward: the crew's own boat becomes the captured pirate ship - you sail it, stand on its
    /// deck, and fire its guns.
    ///
    /// How this stays safe: the game's boat floats and drives on a hidden physics body, pushed up at
    /// a single point and pushed along at the propeller. Neither depends on the hull's shape. The
    /// hull only matters for what you see and what you stand on, and the game keeps those on
    /// separate objects that follow the physics body. So only those are swapped:
    ///
    ///   visuals            the pirate hull replaces the boat's meshes
    ///   walkable colliders the pirate deck replaces the boat's (players and items)
    ///   on-board zone      enlarged to cover the bigger deck
    ///   helm               driver position, wheel and throttle move to the pirate stern
    ///
    /// Physics body and motor are never touched, so the boat handles exactly as before. Every
    /// change is recorded and undone by Revert(), which runs when you switch back, when the island
    /// (and with it the boat) changes, and if applying fails partway.
    /// </summary>
    internal static class PirateHull
    {
        internal const string UnlockDisabled = "ship.pirate.off";

        private static readonly FieldInfo FDynHolder = AccessTools.Field(typeof(Boat), "_dynamicObjectColsHolder");
        private static readonly FieldInfo FItemHolder = AccessTools.Field(typeof(Boat), "_itemColsHolder");
        private static readonly FieldInfo FDynCols = AccessTools.Field(typeof(Boat), "_dynamicObjectCols");
        private static readonly FieldInfo FWheel = AccessTools.Field(typeof(Boat), "_steeringWheel");
        private static readonly FieldInfo FThrottle = AccessTools.Field(typeof(Boat), "_throttle");
        private static readonly FieldInfo FInteract = AccessTools.Field(typeof(Boat), "_boatInteractable");

        private sealed class Moved
        {
            public Transform T;
            public Vector3 LocalPos;
        }

        private static Boat _boat;
        private static GameObject _visual;
        private static readonly List<GameObject> AddedColliderRoots = new List<GameObject>();
        private static readonly List<Collider> AddedColliders = new List<Collider>();
        private static readonly List<Renderer> HiddenRenderers = new List<Renderer>();
        private static readonly List<Collider> DisabledColliders = new List<Collider>();
        private static readonly List<Moved> MovedParts = new List<Moved>();
        private static BoxCollider _trigger;
        private static float _nextCheck;

        internal static bool Active => _visual != null;
        internal static GameObject VisualRoot => _visual;

        /// <summary>The crew owns the prize ship and has not switched back to the old boat.</summary>
        internal static bool Wanted =>
            SharedState.Has(PirateStory.UnlockPirateShip) && !SharedState.Has(UnlockDisabled);

        // ------------------------------------------------------------------ tick (every client)

        internal static void ClientTick()
        {
            if (Time.time < _nextCheck) return;
            _nextCheck = Time.time + 0.5f;

            Boat boat = BoatManager.Boat;

            // The boat was replaced (new island) or destroyed: our objects went with it; just forget them.
            if (Active && _boat != boat) Forget();

            if (Wanted && boat != null && !Active) Apply(boat);
            else if (!Wanted && Active) Revert();
        }

        // ------------------------------------------------------------------ apply

        private static void Apply(Boat boat)
        {
            if (FDynHolder == null || FItemHolder == null || FDynCols == null)
            {
                Diag.Error("PirateHull: the game's boat changed shape internally; cannot fit the pirate hull.");
                return;
            }
            if (boat.VisualBoat == null) return;

            try
            {
                _boat = boat;
                Transform visualRoot = boat.VisualBoat;
                var dynHolder = (Transform)FDynHolder.GetValue(boat);
                var itemHolder = (Rigidbody)FItemHolder.GetValue(boat);
                var dynCols = (Collider[])FDynCols.GetValue(boat);

                // Pose: keel at the same depth the enemy ship floats at, bow towards the boat's bow.
                float water = PirateModule.WaterY();
                float bowSign = BowSign(boat);
                Quaternion localRot = Quaternion.LookRotation(new Vector3(0f, 0f, bowSign), Vector3.up);
                Vector3 keelWorld = new Vector3(visualRoot.position.x, water + PirateModule.Cfg.Waterline.Value, visualRoot.position.z);
                Vector3 localPos = new Vector3(0f, visualRoot.InverseTransformPoint(keelWorld).y, 0f);

                // 1. Visual hull.
                _visual = ModAssets.Create(PirateModule.Cfg.Model.Value, Vector3.zero, Quaternion.identity, visualRoot, solid: false);
                if (_visual == null) { Diag.Error("PirateHull: hull model missing."); Revert(); return; }
                _visual.name = BoatMount.MountRoot + "PirateHull";
                _visual.transform.localPosition = localPos;
                _visual.transform.localRotation = localRot;

                // 2. Hide the old boat, except the controls we move to the new helm.
                var keep = new HashSet<Transform>();
                AddTree(keep, FWheel?.GetValue(boat) as Transform);
                AddTree(keep, FThrottle?.GetValue(boat) as Transform);
                foreach (Renderer r in visualRoot.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || !r.enabled) continue;
                    if (r is ParticleSystemRenderer) continue;          // splashes stay
                    if (r.transform.IsChildOf(_visual.transform)) continue;
                    if (keep.Contains(r.transform) || BoatMount.IsOurs(r.transform)) continue;
                    r.enabled = false;
                    HiddenRenderers.Add(r);
                }

                // 3. Walkable deck: pirate colliders replace the boat's, on the same layers.
                if (dynHolder != null) SwapColliders(dynHolder, dynCols, localPos, localRot, "PirateHullDeck", register: true);
                if (itemHolder != null) SwapColliders(itemHolder.transform, itemHolder.GetComponentsInChildren<Collider>(true),
                                                      localPos, localRot, "PirateHullItems", register: false);

                // 4. On-board zone sized to the new deck.
                EnlargeTrigger(boat, localPos, localRot);

                // 5. Helm at the pirate stern.
                MoveHelm(boat, dynHolder);

                BoatMount.Invalidate();
                DeckCannon.ForceRebuild();
                Diag.Info("PirateHull: fitted - " + HiddenRenderers.Count + " old meshes hidden, " +
                          AddedColliders.Count + " deck colliders, helm moved.");
            }
            catch (Exception e)
            {
                Diag.Exception("PirateHull.Apply", e);
                Revert();
            }
        }

        private static void AddTree(HashSet<Transform> set, Transform root)
        {
            if (root == null) return;
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true)) set.Add(t);
        }

        /// <summary>
        /// Builds pirate-hull colliders under a collider holder, copying the layer and physics material
        /// of the colliders it replaces - the layer decides what they collide with, and getting it wrong
        /// would let the deck shove the boat's own physics body.
        /// </summary>
        private static void SwapColliders(Transform holder, Collider[] existing, Vector3 localPos, Quaternion localRot,
                                          string name, bool register)
        {
            int layer = holder.gameObject.layer;
            PhysicsMaterial material = null;
            if (existing != null)
            {
                foreach (Collider c in existing)
                {
                    if (c == null || c.isTrigger) continue;
                    layer = c.gameObject.layer;
                    material = c.sharedMaterial;
                    break;
                }
            }

            var root = new GameObject(name);
            root.transform.SetParent(holder, false);
            root.transform.localPosition = localPos;
            root.transform.localRotation = localRot;
            AddedColliderRoots.Add(root);

            foreach (MeshFilter mf in _visual.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var part = new GameObject("part");
                part.layer = layer;
                part.transform.SetParent(root.transform, false);
                // Same pose relative to the hull root as the visual part has.
                part.transform.localPosition = _visual.transform.InverseTransformPoint(mf.transform.position);
                part.transform.localRotation = Quaternion.Inverse(_visual.transform.rotation) * mf.transform.rotation;
                part.transform.localScale = mf.transform.lossyScale;

                MeshCollider mc = part.AddComponent<MeshCollider>();
                mc.sharedMesh = mf.sharedMesh;
                if (material != null) mc.sharedMaterial = material;
                AddedColliders.Add(mc);
                if (register && !BoatManager.ColToBoat.ContainsKey(mc)) BoatManager.ColToBoat.Add(mc, _boat);
            }

            if (existing != null)
            {
                foreach (Collider c in existing)
                {
                    if (c == null || !c.enabled || c.isTrigger) continue;
                    if (c.transform.IsChildOf(root.transform)) continue;
                    c.enabled = false;
                    DisabledColliders.Add(c);
                }
            }
        }

        private static void EnlargeTrigger(Boat boat, Vector3 localPos, Quaternion localRot)
        {
            if (boat.BoatTrigger == null) return;

            Bounds hullLocal = LocalBounds(_visual.transform);          // in hull space
            Transform trig = boat.BoatTrigger.transform;
            Transform visual = boat.VisualBoat;

            // Hull-space box -> trigger space, via the boat's visual frame.
            Vector3 centreWorld = _visual.transform.TransformPoint(hullLocal.center + Vector3.up * 1.5f);
            _trigger = boat.BoatTrigger.gameObject.AddComponent<BoxCollider>();
            _trigger.isTrigger = true;
            _trigger.center = trig.InverseTransformPoint(centreWorld);
            Vector3 size = hullLocal.size + new Vector3(0.5f, 3f, 0.5f);   // headroom for jumping players
            Vector3 lossy = trig.lossyScale;
            _trigger.size = new Vector3(size.x / Mathf.Max(0.01f, lossy.x),
                                        size.y / Mathf.Max(0.01f, lossy.y),
                                        size.z / Mathf.Max(0.01f, lossy.z));
        }

        /// <summary>
        /// Moves the driving position, wheel, throttle and "take the wheel" spot to the pirate stern,
        /// keeping their positions relative to each other.
        /// </summary>
        private static void MoveHelm(Boat boat, Transform dynHolder)
        {
            if (boat.DriverPos == null) return;

            Bounds hull = LocalBounds(_visual.transform);
            float bowSign = BowSign(boat);
            Vector3 helmHull = new Vector3(hull.center.x, hull.max.y, hull.center.z - bowSign * hull.extents.z * 0.55f);
            Vector3 helmWorldTop = _visual.transform.TransformPoint(helmHull);

            // Deck height under the helm, measured on the colliders we just built.
            Physics.SyncTransforms();
            float deckY = _visual.transform.TransformPoint(new Vector3(0f, hull.min.y + hull.size.y * 0.3f, 0f)).y;
            float best = float.MaxValue;
            foreach (RaycastHit h in Physics.RaycastAll(helmWorldTop + Vector3.up, Vector3.down, hull.size.y + 3f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (!AddedColliders.Contains(h.collider)) continue;
                if (h.point.y > _visual.transform.TransformPoint(new Vector3(0f, hull.min.y + hull.size.y * 0.5f, 0f)).y) continue; // skip rigging
                if (h.distance < best) { best = h.distance; deckY = h.point.y; }
            }

            Transform visual = boat.VisualBoat;
            Vector3 helmLocal = visual.InverseTransformPoint(new Vector3(helmWorldTop.x, deckY, helmWorldTop.z));
            Vector3 delta = helmLocal - boat.DriverPos.localPosition;

            MoveBy(boat.DriverPos, delta, visual);
            MoveBy(FWheel?.GetValue(boat) as Transform, delta, visual);
            MoveBy(FThrottle?.GetValue(boat) as Transform, delta, visual);
            var interact = FInteract?.GetValue(boat) as Component;
            if (interact != null) MoveBy(interact.transform, delta, visual);
        }

        /// <summary>Moves a transform by a delta expressed in the boat's visual frame, and records it.</summary>
        private static void MoveBy(Transform t, Vector3 deltaInVisual, Transform visual)
        {
            if (t == null) return;
            MovedParts.Add(new Moved { T = t, LocalPos = t.localPosition });

            Vector3 world = t.position + visual.TransformVector(deltaInVisual);
            t.position = world;
        }

        // ------------------------------------------------------------------ revert

        /// <summary>Undoes every change, in reverse order. Safe to call at any point, even half-applied.</summary>
        internal static void Revert()
        {
            try
            {
                for (int i = MovedParts.Count - 1; i >= 0; i--)
                    if (MovedParts[i].T != null) MovedParts[i].T.localPosition = MovedParts[i].LocalPos;

                if (_trigger != null) UnityEngine.Object.Destroy(_trigger);

                foreach (Collider c in DisabledColliders) if (c != null) c.enabled = true;
                foreach (Collider c in AddedColliders) if (c != null) BoatManager.ColToBoat.Remove(c);
                foreach (GameObject g in AddedColliderRoots) if (g != null) UnityEngine.Object.Destroy(g);
                foreach (Renderer r in HiddenRenderers) if (r != null) r.enabled = true;
                if (_visual != null) UnityEngine.Object.Destroy(_visual);

                Diag.Info("PirateHull: removed, the old boat is back.");
            }
            catch (Exception e)
            {
                Diag.Exception("PirateHull.Revert", e);
            }
            Forget();
            BoatMount.Invalidate();
            DeckCannon.ForceRebuild();
        }

        /// <summary>Drops all bookkeeping without touching the (possibly destroyed) boat.</summary>
        private static void Forget()
        {
            // Destroyed colliders still sit as keys in the game's registry; take them out.
            foreach (Collider c in AddedColliders) if (!ReferenceEquals(c, null)) BoatManager.ColToBoat.Remove(c);

            MovedParts.Clear();
            DisabledColliders.Clear();
            AddedColliders.Clear();
            AddedColliderRoots.Clear();
            HiddenRenderers.Clear();
            _trigger = null;
            _visual = null;
            _boat = null;
        }

        internal static void Clear()
        {
            if (Active) Revert(); else Forget();
        }

        // ------------------------------------------------------------------ helpers

        private static float BowSign(Boat boat)
        {
            BoatMotor motor = boat.GetComponentInChildren<BoatMotor>(true);
            if (motor == null || boat.VisualBoat == null) return 1f;
            return boat.VisualBoat.InverseTransformPoint(motor.transform.position).z > 0f ? -1f : 1f;
        }

        /// <summary>Bounds of all renderers under <paramref name="root"/>, in root's local space.</summary>
        internal static Bounds LocalBounds(Transform root)
        {
            bool any = false;
            var b = new Bounds();
            foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                Bounds mb = mf.sharedMesh.bounds;
                Vector3 c = mb.center, e = mb.extents;
                for (int x = -1; x <= 1; x += 2)
                for (int y = -1; y <= 1; y += 2)
                for (int z = -1; z <= 1; z += 2)
                {
                    Vector3 p = root.InverseTransformPoint(mf.transform.TransformPoint(c + Vector3.Scale(e, new Vector3(x, y, z))));
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; }
                    else b.Encapsulate(p);
                }
            }
            return b;
        }
    }
}
