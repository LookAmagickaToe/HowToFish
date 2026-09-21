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
            public float BowSign;
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
            float z;
            switch (slot)
            {
                case Slot.Bow:
                    z = b.center.z + l.BowSign * Mathf.Max(0.5f, b.extents.z - 1.3f);
                    localForward = new Vector3(0f, 0f, l.BowSign);
                    break;
                case Slot.Stern:
                    z = b.center.z - l.BowSign * Mathf.Max(0.5f, b.extents.z - 1.6f);
                    localForward = new Vector3(0f, 0f, -l.BowSign);
                    break;
                default:
                    z = b.center.z;
                    localForward = new Vector3(0f, 0f, l.BowSign);
                    break;
            }

            float y = DeckHeight(boat, new Vector3(b.center.x, b.max.y, z), b);
            localPos = new Vector3(b.center.x, y, z);
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

            // With the pirate hull fitted, the old boat's meshes are hidden and the pirate hull is
            // what guns should sit on, so measure that instead.
            GameObject pirate = PirateHull.VisualRoot;
            IEnumerable<Renderer> source = pirate != null
                ? pirate.GetComponentsInChildren<Renderer>(true)
                : boat.GetComponentsInChildren<Renderer>(true);

            foreach (Renderer r in source)
            {
                if (r == null) continue;
                if (pirate == null && IsOurs(r.transform)) continue;
                if (r is ParticleSystemRenderer) continue;

                Bounds wb = r.bounds;
                foreach (Vector3 corner in Corners(wb))
                {
                    Vector3 p = root.InverseTransformPoint(corner);
                    if (!any) { local = new Bounds(p, Vector3.zero); any = true; }
                    else local.Encapsulate(p);
                }
            }

            if (!any)
            {
                Diag.Warn("BoatMount: boat has no renderers to measure.");
                return null;
            }

            float bowSign = 1f;
            BoatMotor motor = boat.GetComponentInChildren<BoatMotor>(true);
            if (motor != null)
            {
                float motorZ = root.InverseTransformPoint(motor.transform.position).z;
                bowSign = motorZ > local.center.z ? -1f : 1f;
            }
            else
            {
                Diag.Warn("BoatMount: no motor found, assuming the bow is +Z.");
            }

            _layout = new Layout { Boat = boat, Local = local, BowSign = bowSign };
            Diag.Info("BoatMount: boat measures " + local.size.ToString("F1") + " (local), bow towards " +
                      (bowSign > 0 ? "+Z" : "-Z") + (motor != null ? " (from motor)" : "") + ".");
            return _layout;
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
