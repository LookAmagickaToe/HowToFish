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
    /// roof or down a cliff), and have room for a person to stand.
    /// </summary>
    internal static class NpcPlacement
    {
        private static readonly float[] Rings = { 4.5f, 6f, 8f, 10.5f, 13f };
        private const int Headings = 12;
        private const float MaxHeightDifference = 3.5f;

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

            int level = GameInfo.LevelLayer.value;
            int blockers = level | GameInfo.BoatLayer.value;
            float water = 0f;
            try { water = WaterManager.WaterHeight; } catch { }

            // Each slot starts at a different bearing so characters stand apart, and the search sweeps
            // outward ring by ring from there. Bearings are relative to how the landing faces.
            float start = SpawnManager.PlayerSpawnRot + 35f + slot * 70f;

            foreach (float ring in Rings)
            {
                for (int k = 0; k < Headings; k++)
                {
                    // Alternate either side of the preferred bearing: 0, +30, -30, +60, ...
                    int step = (k + 1) / 2;
                    float yaw = start + (k % 2 == 1 ? step : -step) * (360f / Headings);
                    Vector3 dir = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
                    Vector3 probe = anchor + dir * ring;

                    RaycastHit hit;
                    if (!Physics.Raycast(probe + Vector3.up * 15f, Vector3.down, out hit, 40f, level, QueryTriggerInteraction.Ignore))
                        continue;

                    Vector3 ground = hit.point;
                    if (ground.y < water + 0.3f) continue;                              // no standing in the sea
                    if (Mathf.Abs(ground.y - anchor.y) > MaxHeightDifference) continue;  // no roofs or ravines
                    if (Vector3.Dot(hit.normal, Vector3.up) < 0.75f) continue;          // no slopes

                    // Room to stand: a person-sized capsule must be clear of walls and props.
                    Vector3 feet = ground + Vector3.up * 0.35f;
                    Vector3 head = ground + Vector3.up * 1.7f;
                    if (Physics.CheckCapsule(feet, head, 0.35f, blockers, QueryTriggerInteraction.Ignore)) continue;

                    // And the landing must be able to see them: no characters hidden behind buildings.
                    if (Physics.Linecast(anchor + Vector3.up * 1.5f, ground + Vector3.up * 1.5f, level, QueryTriggerInteraction.Ignore))
                        continue;

                    position = ground;
                    return true;
                }
            }

            Diag.Warn("NpcPlacement: no clear spot for slot " + slot + " around " + anchor.ToString("F1") + ".");
            return false;
        }
    }
}
