using System;
using System.Collections.Generic;
using UnityEngine;

namespace Expanded.Pirates
{
    /// <summary>
    /// Puts a crew on the pirate ship: the captain at the stern, a gunner on each broadside, a
    /// lookout and a deckhand. They can be shot.
    ///
    /// The crew are children of the ship's networked prefab, so they ride along on every client with
    /// no networking of their own. Who is alive is decided by the host and broadcast; animation is
    /// local on each client.
    ///
    /// Why shooting them matters: a dead gunner silences most of his broadside, and a dead captain
    /// makes the ship strike her colours - a way to win that rewards marksmanship over firepower.
    /// </summary>
    internal static class PirateCrew
    {
        internal enum Role { Captain, Gunner, Lookout, Deckhand }

        internal struct Post
        {
            public string Model;
            public Vector2 LocalXZ;     // fractions of the hull's half-extents
            public float FaceYaw;
            public Role Role;
            public int Side;            // gunners: +1 starboard, -1 larboard
        }

        // Order is the crew index on the wire; it must be identical on every machine.
        internal static readonly Post[] Posts =
        {
            new Post { Model = "characters_captain_barbarossa", LocalXZ = new Vector2(0f, -0.62f),  FaceYaw = 0f,   Role = Role.Captain },
            new Post { Model = "characters_sharky",   LocalXZ = new Vector2(0.28f, -0.1f),  FaceYaw = 90f,  Role = Role.Gunner, Side = 1 },
            new Post { Model = "characters_henry",    LocalXZ = new Vector2(-0.28f, 0.15f), FaceYaw = -90f, Role = Role.Gunner, Side = -1 },
            new Post { Model = "characters_mako",     LocalXZ = new Vector2(0.05f, 0.45f),  FaceYaw = 0f,   Role = Role.Lookout },
            new Post { Model = "characters_skeleton", LocalXZ = new Vector2(-0.12f, -0.35f), FaceYaw = 160f, Role = Role.Deckhand },
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
            int npcLayer = LayerFromMask(GameInfo.NpcLayer.value);
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
                AddHitbox(c, npcLayer);

                CrewMember member = c.AddComponent<CrewMember>();
                member.Index = i;
                member.Role = p.Role;
                placed++;
            }

            Diag.Info("PirateCrew: " + placed + " crew aboard" + (ModCharacters.HasColour ? "." : " (grey until the colour atlas is installed)."));
            return placed;
        }

        /// <summary>
        /// A person-sized capsule on the game's NPC layer, tagged NPC. Bullets already stop on that
        /// layer, and the game plays blood effects for anything tagged NPC - so hits look right with
        /// no extra effect code.
        /// </summary>
        private static void AddHitbox(GameObject crew, int layer)
        {
            var hitbox = new GameObject("Hitbox");
            hitbox.transform.SetParent(crew.transform, false);
            hitbox.layer = layer;
            try { hitbox.tag = "NPC"; } catch (Exception e) { Diag.Debug("NPC tag unavailable: " + e.Message); }

            CapsuleCollider cap = hitbox.AddComponent<CapsuleCollider>();
            cap.height = 1.8f;
            cap.radius = 0.38f;
            cap.center = new Vector3(0f, 0.9f, 0f);
        }

        private static int LayerFromMask(int mask)
        {
            for (int i = 0; i < 32; i++) if ((mask & (1 << i)) != 0) return i;
            return 0;
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
                        // Skip the rigging: keep the highest surface in the lower half of the ship.
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
    /// One crew member on every client. Animation is local and follows the ship's replicated state;
    /// death is decided by the host and arrives as a message (or in the status for late joiners).
    /// </summary>
    internal sealed class CrewMember : MonoBehaviour
    {
        internal int Index;
        internal PirateCrew.Role Role = PirateCrew.Role.Deckhand;
        internal bool Dead { get; private set; }

        private string _current;
        private float _nextFlourish;

        // FishNet pools despawned ships and reuses them, so every life starts fresh here.
        private void OnEnable()
        {
            Dead = false;
            _current = null;
            _nextFlourish = Time.time + UnityEngine.Random.Range(2f, 6f);
            SetHitbox(true);
            SetClip("Idle", true);
        }

        internal void Die()
        {
            if (Dead) return;
            Dead = true;
            CancelInvoke();
            SetHitbox(false);
            SetClip("Death", false);
        }

        private void SetHitbox(bool on)
        {
            foreach (Collider c in GetComponentsInChildren<Collider>(true)) c.enabled = on;
        }

        private void Update()
        {
            try
            {
                if (Dead) return;

                PirateModule pm = PirateModule.Instance;
                PirateShip.Stance stance = pm != null ? pm.ReplicatedStance : PirateShip.Stance.Closing;

                if (stance == PirateShip.Stance.Sinking)
                {
                    // Abandon ship: some go down fighting, some just go down.
                    Dead = true;
                    SetHitbox(false);
                    SetClip(UnityEngine.Random.value < 0.5f ? "Death" : "Jump", false);
                    return;
                }
                if (stance == PirateShip.Stance.Surrendered)
                {
                    if (_current != "No") SetClip("No", true);   // hands up, heads shaking
                    return;
                }

                bool fighting = stance == PirateShip.Stance.Broadside;
                if (Time.time >= _nextFlourish)
                {
                    _nextFlourish = Time.time + UnityEngine.Random.Range(3f, 7f);
                    string flourish =
                        Role == PirateCrew.Role.Captain ? (fighting ? "Sword" : "Wave") :
                        Role == PirateCrew.Role.Gunner ? (fighting ? "Punch" : "Idle") :
                        Role == PirateCrew.Role.Lookout ? "Wave" : "Yes";
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
            if (!Dead) SetClip("Idle", true);
        }

        private void SetClip(string clip, bool loop)
        {
            if (_current == clip && loop) return;
            if (ModCharacters.Play(gameObject, clip, loop)) _current = clip;
        }
    }
}
