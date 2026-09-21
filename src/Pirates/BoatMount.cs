using System;
using System.Collections.Generic;
using UnityEngine;

namespace Expanded.Pirates
{
    /// <summary>
    /// Finds mounting points on the crew's boat - bow, midships, stern - without hard-coding its
    /// geometry, so mounts land correctly on every boat skin and survive a game update that
    /// reshapes the hull.
    ///
    /// The bow is found from the outboard motor: whichever end of the hull the motor is NOT on.
    /// The deck height at each point is found by raycasting down onto the boat itself.
    /// </summary>
    internal static class BoatMount
    {
        internal enum Slot { Bow, Midships, Stern }

        private sealed class Layout
        {
            public Boat Boat;
            public Bounds Local;
            /// <summary>True if the hull's length runs along local X, false for local Z.</summary>
            public bool AlongX;
            /// <summary>+1 if the bow is at the positive end of that axis, -1 if at the negative end.</summary>
            public float BowSign;

            public Vector3 Bow => (AlongX ? Vector3.right : Vector3.forward) * BowSign;
            public float Extent => AlongX ? Local.extents.x : Local.extents.z;
            public float Centre => AlongX ? Local.center.x : Local.center.z;
        }

        private static Layout _layout;

        internal static void Invalidate() => _layout = null;

        /// <summary>
        /// The frame everything mounted on the boat must hang off: the visual boat, which the game
        /// keeps in step with the physics body (the driver's position is computed relative to it).
        /// The boat's root object is NOT a safe parent - with separate physics and visual rigs, a root
        /// may well stay where the boat was spawned while the boat sails away.
        /// </summary>
        internal static Transform Frame(Boat boat)
        {
            if (boat == null) return null;
            return boat.VisualBoat != null ? boat.VisualBoat : boat.transform;
        }

        /// <summary>
        /// Mount point in the boat's local space plus the local direction that faces outward from
        /// it. Returns false if there is no boat.
        /// </summary>
        internal static bool TryGet(Slot slot, out Vector3 localPos, out Vector3 localForward)
        {
            localPos = Vector3.zero;
            localForward = Vector3.forward;

            Boat boat = BoatManager.Boat;
            if (boat == null) return false;

            Layout l = LayoutFor(boat);
            if (l == null) return false;

            Bounds b = l.Local;
            float ext = l.Extent;
            float along;   // offset from the centre along the hull, towards the bow
            switch (slot)
            {
                case Slot.Bow:
                    // Right up in the bow, on the centreline, where the stem narrows.
                    along = Mathf.Max(0.3f, Mathf.Min(ext - 0.55f, ext * 0.85f));
                    localForward = l.Bow;
                    break;
                case Slot.Stern:
                    along = -Mathf.Max(0.3f, Mathf.Min(ext - 1.6f, ext * 0.65f));
                    localForward = -l.Bow;
                    break;
                default:
                    along = 0f;
                    localForward = l.Bow;
                    break;
            }

            Vector3 p = b.center + l.Bow * along;
            float y = DeckHeight(boat, new Vector3(p.x, b.max.y, p.z), b);
            // Guns sit on a short pedestal above the deck (config), so the barrel clears the gunwale.
            float lift = PirateModule.Cfg != null ? PirateModule.Cfg.DeckCannonLift.Value : 0f;
            localPos = new Vector3(p.x, y + lift, p.z);
            return true;
        }

        internal static bool TryGetWorld(Slot slot, out Vector3 worldPos, out Vector3 worldForward)
        {
            worldPos = Vector3.zero;
            worldForward = Vector3.forward;
            Vector3 lp, lf;
            if (!TryGet(slot, out lp, out lf)) return false;

            Transform t = Frame(BoatManager.Boat);
            worldPos = t.TransformPoint(lp);
            worldForward = t.TransformDirection(lf);
            return true;
        }

        private static Layout LayoutFor(Boat boat)
        {
            // Cached per boat object; a new island means a new boat instance and a fresh measurement.
            if (_layout != null && _layout.Boat == boat) return _layout;

            Transform root = Frame(boat);
            bool any = false;
            Bounds local = default(Bounds);
            string source;

            // With the pirate hull fitted, the old boat's meshes are hidden and the pirate hull is
            // what guns should sit on, so measure that instead.
            GameObject pirate = PirateHull.VisualRoot;
            if (pirate != null)
            {
                any = EncapsulateRenderers(pirate.GetComponentsInChildren<Renderer>(true), root, false, ref local);
                source = "pirate hull";
            }
            else
            {
                // The old boat's renderers include things far bigger than the hull (it measured
                // 10 x 10 m for a small dinghy, which put the bow gun beside the boat). The deck the
                // game lets players walk on is the hull itself, so measure that.
                any = EncapsulateColliders(DeckColliders(boat), root, ref local);
                source = "deck colliders";
                if (!any || local.size.x > 6f || local.size.z > 9f)
                {
                    Bounds fromRenderers = default(Bounds);
                    if (EncapsulateRenderers(boat.GetComponentsInChildren<Renderer>(true), root, true, ref fromRenderers) &&
                        (!any || fromRenderers.size.sqrMagnitude < local.size.sqrMagnitude))
                    {
                        local = fromRenderers;
                        any = true;
                        source = "renderers";
                    }
                }
            }

            if (!any)
            {
                Diag.Warn("BoatMount: boat has nothing to measure.");
                return null;
            }

            // Which way is forward: the driver faces the bow, and the outboard motor sits at the
            // stern. The driver decides; the motor vetoes if it would end up at the front.
            bool alongX = local.size.x > local.size.z;
            float bowSign = 1f;
            string how = "hull shape";

            Vector3 driverFwd = Vector3.zero;
            try { if (boat.DriverPos != null) driverFwd = root.InverseTransformDirection(boat.DriverPos.forward); }
            catch { /* no driver seat */ }
            driverFwd.y = 0f;
            if (driverFwd.sqrMagnitude > 0.1f)
            {
                alongX = Mathf.Abs(driverFwd.x) > Mathf.Abs(driverFwd.z);
                bowSign = Mathf.Sign(alongX ? driverFwd.x : driverFwd.z);
                how = "driver seat";
            }

            BoatMotor motor = boat.GetComponentInChildren<BoatMotor>(true);
            if (motor != null)
            {
                Vector3 m = root.InverseTransformPoint(motor.transform.position);
                float motorAlong = (alongX ? m.x - local.center.x : m.z - local.center.z);
                if (Mathf.Abs(motorAlong) > 0.2f && Mathf.Sign(motorAlong) == bowSign)
                {
                    bowSign = -Mathf.Sign(motorAlong);
                    how += ", flipped: motor is at the stern";
                }
                else if (how == "hull shape")
                {
                    bowSign = motorAlong > 0f ? -1f : 1f;
                    how = "motor";
                }
            }

            _layout = new Layout { Boat = boat, Local = local, AlongX = alongX, BowSign = bowSign };
            Diag.Info("BoatMount: boat measures " + local.size.ToString("F1") + " (local, from " + source +
                      "), centre " + local.center.ToString("F1") + ", bow towards " +
                      (bowSign > 0 ? "+" : "-") + (alongX ? "X" : "Z") + " (" + how + ").");
            return _layout;
        }

        private static readonly System.Reflection.FieldInfo FDynCols =
            HarmonyLib.AccessTools.Field(typeof(Boat), "_dynamicObjectCols");

        /// <summary>The colliders players stand on in the boat - the hull's real footprint.</summary>
        private static IEnumerable<Collider> DeckColliders(Boat boat)
        {
            var list = new List<Collider>();
            try
            {
                if (FDynCols?.GetValue(boat) is Collider[] cols)
                    foreach (Collider c in cols)
                        if (c != null && c.enabled && !c.isTrigger && !IsOurs(c.transform)) list.Add(c);
            }
            catch (Exception e)
            {
                Diag.Debug("BoatMount: deck colliders unavailable (" + e.Message + ").");
            }
            return list;
        }

        private static bool EncapsulateColliders(IEnumerable<Collider> cols, Transform root, ref Bounds local)
        {
            bool any = false;
            foreach (Collider c in cols)
                foreach (Vector3 corner in Corners(c.bounds))
                    Add(root.InverseTransformPoint(corner), ref local, ref any);
            return any;
        }

        private static bool EncapsulateRenderers(IEnumerable<Renderer> rs, Transform root, bool skipOurs, ref Bounds local)
        {
            bool any = false;
            foreach (Renderer r in rs)
            {
                if (r == null || r is ParticleSystemRenderer || !r.enabled) continue;
                if (skipOurs && IsOurs(r.transform)) continue;
                foreach (Vector3 corner in Corners(r.bounds))
                    Add(root.InverseTransformPoint(corner), ref local, ref any);
            }
            return any;
        }

        private static void Add(Vector3 p, ref Bounds b, ref bool any)
        {
            if (!any) { b = new Bounds(p, Vector3.zero); any = true; }
            else b.Encapsulate(p);
        }

        /// <summary>
        /// Deck height under a local point, by casting down onto the boat's own colliders only -
        /// water, players and anything else in the way are ignored.
        /// </summary>
        private static float DeckHeight(Boat boat, Vector3 localTop, Bounds b)
        {
            Transform root = Frame(boat);
            Vector3 origin = root.TransformPoint(localTop + Vector3.up * 3f);
            Vector3 down = -root.up;
            float dist = b.size.y + 6f;

            RaycastHit[] hits = Physics.RaycastAll(origin, down, dist, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            float y = b.min.y + b.size.y * 0.45f;

            foreach (RaycastHit h in hits)
            {
                if (h.collider == null || !h.collider.enabled) continue;
                // The walkable deck lives in a collider holder that follows the physics body, not
                // necessarily under the visual frame; identify it through the game's own
                // collider-to-boat registry (the fitted pirate deck registers there too).
                Boat owner;
                bool onBoat = h.collider.transform.IsChildOf(boat.transform) ||
                              (BoatManager.ColToBoat.TryGetValue(h.collider, out owner) && owner == boat);
                if (!onBoat) continue;
                // Our own decoration never counts as deck; the fitted pirate deck does (its colliders
                // are not under an ExpandedMount root, so IsOurs lets them through).
                if (IsOurs(h.collider.transform)) continue;
                // Masts and sails are not somewhere to bolt a cannon: ignore the upper half.
                float ly = root.InverseTransformPoint(h.point).y;
                if (ly > b.min.y + b.size.y * 0.55f) continue;
                if (h.distance >= best) continue;
                best = h.distance;
                y = ly;
            }
            return y;
        }

        /// <summary>Our own mounted objects must never be measured as part of the boat.</summary>
        internal static bool IsOurs(Transform t)
        {
            for (Transform p = t; p != null; p = p.parent)
                if (p.name.StartsWith(MountRoot, StringComparison.Ordinal)) return true;
            return false;
        }

        internal const string MountRoot = "ExpandedMount";

        private static IEnumerable<Vector3> Corners(Bounds b)
        {
            Vector3 c = b.center, e = b.extents;
            for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
            for (int z = -1; z <= 1; z += 2)
                yield return c + new Vector3(e.x * x, e.y * y, e.z * z);
        }
    }
}
