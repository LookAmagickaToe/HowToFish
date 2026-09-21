using System;
using System.Collections.Generic;
using UnityEngine;

namespace Expanded.Pirates
{
    /// <summary>
    /// Puts a crew on the pirate ship: the captain at the stern, hands at the guns.
    ///
    /// The crew are children of the ship's networked prefab, so they ride along on every client with
    /// no networking of their own. Their animation is local: each client reads the ship's replicated
    /// state (from the health bar feed) and picks idle, fighting or abandon-ship animations itself.
    ///
    /// Deck height is measured, not guessed: a temporary copy of the hull is placed far below the
    /// world with its colliders live, and the deck is found by raycasting down onto it.
    /// </summary>
    internal static class PirateCrew
    {
        private struct Post
        {
            public string Model;
            public Vector2 LocalXZ;     // as fractions of the hull's half-extents
            public float FaceYaw;
            public string Role;
        }

        // Positions are fractions of the hull size so they scale with whichever ship model is used.
        private static readonly Post[] Posts =
        {
            new Post { Model = "characters_captain_barbarossa", LocalXZ = new Vector2(0f, -0.62f), FaceYaw = 0f, Role = "captain" },
            new Post { Model = "characters_sharky",   LocalXZ = new Vector2(0.28f, -0.1f),  FaceYaw = 90f,  Role = "gunner" },
            new Post { Model = "characters_henry",    LocalXZ = new Vector2(-0.28f, 0.15f), FaceYaw = -90f, Role = "gunner" },
            new Post { Model = "characters_mako",     LocalXZ = new Vector2(0.05f, 0.45f),  FaceYaw = 0f,   Role = "lookout" },
            new Post { Model = "characters_skeleton", LocalXZ = new Vector2(-0.12f, -0.35f), FaceYaw = 160f, Role = "deckhand" },
        };

        internal static int Populate(Transform shipRoot, Bounds hull)
        {
            ModCharacters.Load();
            if (!ModCharacters.Available)
            {
                Diag.Warn("PirateCrew: no character bundle; the ship sails without a visible crew.");
                return 0;
            }

            List<float> decks = MeasureDeck(hull);
            int placed = 0;

            for (int i = 0; i < Posts.Length; i++)
            {
                Post p = Posts[i];
                var local = new Vector3(
                    hull.center.x + p.LocalXZ.x * hull.extents.x,
                    decks[i],
                    hull.center.z + p.LocalXZ.y * hull.extents.z);

                GameObject c = ModCharacters.Create(p.Model, Vector3.zero, Quaternion.identity, shipRoot);
                if (c == null) continue;

                c.transform.localPosition = local;
                c.transform.localRotation = Quaternion.Euler(0f, p.FaceYaw, 0f);

                CrewMember member = c.AddComponent<CrewMember>();
                member.Role = p.Role;
                placed++;
            }

            Diag.Info("PirateCrew: " + placed + " crew aboard" + (ModCharacters.HasColour ? "." : " (grey until the colour atlas is installed)."));
            return placed;
        }

        /// <summary>
        /// Deck height for each post. The template prefab is inactive, so its own colliders are off;
        /// a live throwaway copy of the hull is raycast instead, then destroyed.
        /// </summary>
        private static List<float> MeasureDeck(Bounds hull)
        {
            var result = new List<float>(Posts.Length);
            float fallback = hull.min.y + hull.size.y * 0.3f;

            Vector3 far = new Vector3(0f, -5000f, 0f);
            GameObject probe = ModAssets.Create(PirateModule.Cfg.Model.Value, far, Quaternion.identity, null, solid: true);
            if (probe == null)
            {
                for (int i = 0; i < Posts.Length; i++) result.Add(fallback);
                return result;
            }

            try
            {
                Physics.SyncTransforms();
                Transform root = probe.transform;

                foreach (Post p in Posts)
                {
                    var local = new Vector3(hull.center.x + p.LocalXZ.x * hull.extents.x, hull.max.y + 2f,
                                            hull.center.z + p.LocalXZ.y * hull.extents.z);
                    Vector3 origin = root.TransformPoint(local);

                    float deck = fallback;
                    float best = float.MaxValue;
                    foreach (RaycastHit h in Physics.RaycastAll(origin, Vector3.down, hull.size.y + 4f, ~0, QueryTriggerInteraction.Ignore))
                    {
                        if (h.collider == null || !h.collider.transform.IsChildOf(root)) continue;
                        // Skip the rigging: a sailor standing on a yardarm looks silly. Keep the
                        // highest surface in the lower half of the ship.
                        float y = root.InverseTransformPoint(h.point).y;
                        if (y > hull.min.y + hull.size.y * 0.5f) continue;
                        if (h.distance < best) { best = h.distance; deck = y; }
                    }
                    result.Add(deck);
                }
            }
            catch (Exception e)
            {
                Diag.Exception("PirateCrew.MeasureDeck", e);
                while (result.Count < Posts.Length) result.Add(fallback);
            }
            finally
            {
                UnityEngine.Object.Destroy(probe);
            }
            return result;
        }
    }

    /// <summary>
    /// Per-client animation for one crew member, driven by the ship's replicated state rather than
    /// by any network traffic of its own.
    /// </summary>
    internal sealed class CrewMember : MonoBehaviour
    {
        internal string Role = "deckhand";

        private string _current;
        private float _nextFlourish;
        private bool _dead;

        private void OnEnable()
        {
            _current = null;
            _dead = false;
            _nextFlourish = Time.time + UnityEngine.Random.Range(2f, 6f);
            SetClip("Idle", true);
        }

        private void Update()
        {
            try
            {
                if (_dead) return;

                PirateModule pm = PirateModule.Instance;
                PirateShip.Stance stance = pm != null ? pm.ReplicatedStance : PirateShip.Stance.Closing;

                if (stance == PirateShip.Stance.Sinking)
                {
                    // Abandon ship: some go down fighting, some just go down.
                    _dead = true;
                    SetClip(UnityEngine.Random.value < 0.5f ? "Death" : "Jump", false);
                    return;
                }

                bool fighting = stance == PirateShip.Stance.Broadside;
                if (Time.time >= _nextFlourish)
                {
                    _nextFlourish = Time.time + UnityEngine.Random.Range(3f, 7f);
                    string flourish = Role == "captain" ? (fighting ? "Sword" : "Wave")
                                    : Role == "gunner" ? (fighting ? "Punch" : "Idle")
                                    : Role == "lookout" ? "Wave" : "Yes";
                    SetClip(flourish, false);
                    Invoke(nameof(BackToIdle), 1.6f);
                }
            }
            catch (Exception e)
            {
                Diag.Exception("CrewMember.Update", e);
            }
        }

        private void BackToIdle()
        {
            if (!_dead) SetClip("Idle", true);
        }

        private void SetClip(string clip, bool loop)
        {
            if (_current == clip && loop) return;
            if (ModCharacters.Play(gameObject, clip, loop)) _current = clip;
        }
    }
}
