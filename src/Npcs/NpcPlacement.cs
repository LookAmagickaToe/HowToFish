using UnityEngine;

namespace Expanded.Npcs
{
    /// <summary>
    /// Finds a standing spot for a story character near the island's landing point.
    ///
    /// Deterministic by design: every client runs the same searches against the same island
    /// geometry in the same order, so every player sees each character in the same place without
    /// positions ever crossing the network.
    ///
    /// A spot must be solid ground, above the waterline, close to the landing's height (not on a
    /// roof or down a cliff), and have room for a person to stand. If nothing passes the strict
    /// rules, a second, relaxed pass searches further out - a character standing a bit off is far
    /// better than no character at all.
    /// </summary>
    internal static class NpcPlacement
    {
        private sealed class Rules
        {
            public float[] Rings;
            public float MaxHeightDifference;
            public float MinUpDot;
            public bool NeedLineOfSight;
        }

        private static readonly Rules Strict = new Rules
        {
            Rings = new[] { 4.5f, 6f, 8f, 10.5f, 13f },
            MaxHeightDifference = 3.5f,
            MinUpDot = 0.75f,
            NeedLineOfSight = true
        };

        private static readonly Rules Relaxed = new Rules
        {
            Rings = new[] { 3f, 5f, 7f, 9f, 12f, 15f, 19f, 24f, 30f },
            MaxHeightDifference = 8f,
            MinUpDot = 0.6f,
            NeedLineOfSight = false
        };

        private const int Headings = 12;

        // Rejection counters of the last failed search, for the log.
        private static int _noGround, _underwater, _height, _slope, _blocked, _hidden;
        private static string _lastFailureLogged;

        internal static bool Find(int slot, out Vector3 position, out Vector3 lookAt)
        {
            Vector3 anchor = SpawnManager.PlayerSpawnPos;
            lookAt = anchor;
            position = anchor;

            if (anchor == Vector3.zero)
            {
                Diag.Warn("NpcPlacement: no landing point on this island yet.");
                return false;
            }

            float water = 0f;
            try { water = WaterManager.WaterHeight; } catch { }

            // The landing itself may sit right at the waterline (a beach or jetty), so compare heights
            // against the actual ground under the landing where there is some.
            float anchorY = anchor.y;
            RaycastHit under;
            if (Physics.Raycast(anchor + Vector3.up * 3f, Vector3.down, out under, 10f, GameInfo.LevelLayer.value, QueryTriggerInteraction.Ignore))
                anchorY = under.point.y;

            if (TrySearch(slot, anchor, anchorY, water, Strict, out position)) return true;
            string strictStats = Stats();
            if (TrySearch(slot, anchor, anchorY, water, Relaxed, out position))
            {
                Diag.Info("NpcPlacement: slot " + slot + " used the relaxed search (strict rejected: " + strictStats + ").");
                return true;
            }

            // Only log a failure once per landing, not every retry.
            string key = slot + "@" + anchor.ToString("F1");
            if (_lastFailureLogged != key)
            {
                _lastFailureLogged = key;
                Diag.Warn("NpcPlacement: no clear spot for slot " + slot + " around " + anchor.ToString("F1")
                    + " (ground y " + anchorY.ToString("F1") + ", water " + water.ToString("F1") + "). Rejected - strict: "
                    + strictStats + "; relaxed: " + Stats() + ".");
            }
            return false;
        }

        private static bool TrySearch(int slot, Vector3 anchor, float anchorY, float water, Rules rules, out Vector3 position)
        {
            position = anchor;
            _noGround = _underwater = _height = _slope = _blocked = _hidden = 0;

            int level = GameInfo.LevelLayer.value;
            int blockers = level | GameInfo.BoatLayer.value;

            // Each slot starts at a different bearing so characters stand apart, and the search sweeps
            // outward ring by ring from there. Bearings are relative to how the landing faces.
            float start = SpawnManager.PlayerSpawnRot + 35f + slot * 70f;

            foreach (float ring in rules.Rings)
            {
                for (int k = 0; k < Headings; k++)
                {
                    // Alternate either side of the preferred bearing: 0, +30, -30, +60, ...
                    int step = (k + 1) / 2;
                    float yaw = start + (k % 2 == 1 ? step : -step) * (360f / Headings);
                    Vector3 dir = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
                    Vector3 probe = anchor + dir * ring;

                    RaycastHit hit;
                    if (!Physics.Raycast(probe + Vector3.up * 20f, Vector3.down, out hit, 50f, level, QueryTriggerInteraction.Ignore))
                    { _noGround++; continue; }

                    Vector3 ground = hit.point;
                    if (ground.y < water + 0.05f) { _underwater++; continue; }                           // no standing in the sea
                    if (Mathf.Abs(ground.y - anchorY) > rules.MaxHeightDifference) { _height++; continue; } // no roofs or ravines
                    if (Vector3.Dot(hit.normal, Vector3.up) < rules.MinUpDot) { _slope++; continue; }      // no slopes

                    // Room to stand: a person-sized capsule, lifted clear of the ground it stands on
                    // (a capsule touching the floor would always report itself as blocked).
                    Vector3 feet = ground + Vector3.up * 0.6f;
                    Vector3 head = ground + Vector3.up * 1.7f;
                    if (Physics.CheckCapsule(feet, head, 0.3f, blockers, QueryTriggerInteraction.Ignore)) { _blocked++; continue; }

                    // And the landing should be able to see them: no characters hidden behind buildings.
                    if (rules.NeedLineOfSight &&
                        Physics.Linecast(anchor + Vector3.up * 1.5f, ground + Vector3.up * 1.5f, level, QueryTriggerInteraction.Ignore))
                    { _hidden++; continue; }

                    position = ground;
                    return true;
                }
            }
            return false;
        }

        private static string Stats() =>
            "no ground " + _noGround + ", water " + _underwater + ", height " + _height +
            ", slope " + _slope + ", blocked " + _blocked + ", hidden " + _hidden;
    }
}
