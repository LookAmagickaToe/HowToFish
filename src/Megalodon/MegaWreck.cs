using System.Collections.Generic;
using Expanded.Pirates;
using UnityEngine;

namespace Expanded.Megalodon
{
    /// <summary>
    /// The boat's fourth bite: it bursts into planks. Every client hides the real boat for a few
    /// seconds (the host sends it home meanwhile) and throws a cloud of wood into the air that then
    /// floats and drifts. Purely cosmetic; the deaths are the host's business.
    /// </summary>
    internal static class MegaWreck
    {
        private sealed class Plank
        {
            public Transform T;
            public Vector3 V;
            public Vector3 Spin;
            public bool Floating;
            public float Bob;
        }

        private static readonly List<Plank> Planks = new List<Plank>();
        private static readonly List<Renderer> Hidden = new List<Renderer>();
        private static float _restoreAt = -1f, _planksUntil = -1f;

        internal static void Play(Vector3 at)
        {
            Clear();
            HideBoat();
            _restoreAt = Time.time + 5f;
            _planksUntil = Time.time + 45f;

            for (int i = 0; i < 34; i++)
            {
                bool chunk = i < 5;
                Color c = i % 3 == 0 ? MegaShapes.DarkWood : MegaShapes.Wood;
                Vector3 size = chunk ? new Vector3(Random.Range(0.8f, 1.4f), 0.12f, Random.Range(1.2f, 2.2f))
                                     : new Vector3(Random.Range(0.12f, 0.22f), 0.05f, Random.Range(0.6f, 1.5f));
                GameObject go = MegaShapes.Prim(PrimitiveType.Cube, null, Vector3.zero, size, c, null, 0f, true);
                go.name = "ExpandedWreckPlank";
                go.transform.position = at + new Vector3(Random.Range(-2f, 2f), Random.Range(0.3f, 1.5f), Random.Range(-2f, 2f));
                go.transform.rotation = Random.rotation;
                Vector3 outward = new Vector3(Random.Range(-1f, 1f), 0f, Random.Range(-1f, 1f)).normalized;
                Planks.Add(new Plank
                {
                    T = go.transform,
                    V = outward * Random.Range(4f, 11f) + Vector3.up * Random.Range(6f, chunk ? 9f : 15f),
                    Spin = Random.insideUnitSphere * 540f,
                    Bob = Random.Range(0f, 6f)
                });
            }

            try { AudioManager.PlayClipAt("Explosion", at, true, AudioDistance.Long, 1f, 0.05f); } catch { }
            try { AudioManager.PlayClipAt("LavaWhaleDeathExplosion", at, true, AudioDistance.Long, 0.8f, 0.05f); } catch { }
            for (int i = 0; i < 4; i++)
            {
                Vector3 p = at + new Vector3(Random.Range(-3f, 3f), 0f, Random.Range(-3f, 3f));
                p.y = Tow.Water(p);
                try { ParticleManager.Play("WaterSplash", p); } catch { }
            }
            try { VFXManager.Play("WaterSplash", at, Vector3.up); } catch { }
            MegaFx.Shake(3000f, 5);
            MegaFx.FlashScreen(new Color(0.3f, 0.2f, 0.1f, 0.6f), 0.8f);
            MegaFx.Banner("THE BOAT IS GONE.", new Color(1f, 0.6f, 0.35f), 3.5f);
            ModSave.AddCounter("megalodon.boats.wrecked", 1);
        }

        private static void HideBoat()
        {
            Boat boat = Tow.Boat;
            if (boat == null) return;
            var roots = new List<Transform> { boat.transform };
            Transform frame = BoatMount.Frame(boat);
            if (frame != null && !frame.IsChildOf(boat.transform)) roots.Add(frame);
            foreach (Transform root in roots)
                foreach (Renderer r in root.GetComponentsInChildren<Renderer>(false))
                {
                    if (r == null || !r.enabled) continue;
                    r.enabled = false;
                    Hidden.Add(r);
                }
        }

        private static void Restore()
        {
            foreach (Renderer r in Hidden) if (r != null) r.enabled = true;
            Hidden.Clear();
            _restoreAt = -1f;
        }

        internal static void Tick()
        {
            if (_restoreAt > 0f && Time.time >= _restoreAt) Restore();
            if (Planks.Count == 0) return;

            float dt = Time.deltaTime, now = Time.time;
            bool expire = now >= _planksUntil;
            for (int i = Planks.Count - 1; i >= 0; i--)
            {
                Plank p = Planks[i];
                if (p.T == null) { Planks.RemoveAt(i); continue; }
                if (expire)
                {
                    // Waterlogged: they sink away.
                    p.T.position += Vector3.down * 0.4f * dt;
                    if (p.T.position.y < Tow.Water(p.T.position) - 2f) { Object.Destroy(p.T.gameObject); Planks.RemoveAt(i); }
                    continue;
                }

                Vector3 pos = p.T.position;
                float water = Tow.Water(pos);
                if (!p.Floating)
                {
                    p.V += Vector3.down * 11f * dt;
                    pos += p.V * dt;
                    p.T.Rotate(p.Spin * dt, Space.World);
                    if (pos.y <= water && p.V.y < 0f)
                    {
                        p.Floating = true;
                        p.V = new Vector3(p.V.x, 0f, p.V.z) * 0.25f;
                        if (i % 4 == 0) Wakeboard.AudioPlay("ItemHitWaterLight_V", 1, 3, pos, 0.5f);
                    }
                }
                else
                {
                    p.V *= Mathf.Exp(-0.4f * dt);
                    pos += p.V * dt;
                    pos.y = water + 0.02f + Mathf.Sin(now * 1.3f + p.Bob) * 0.03f;
                    Vector3 e = p.T.eulerAngles;
                    p.T.rotation = Quaternion.Slerp(p.T.rotation, Quaternion.Euler(Mathf.Sin(now + p.Bob) * 6f, e.y, Mathf.Cos(now * 0.8f + p.Bob) * 6f), dt * 2f);
                }
                p.T.position = pos;
            }
        }

        internal static void Clear()
        {
            Restore();
            foreach (Plank p in Planks) if (p.T != null) Object.Destroy(p.T.gameObject);
            Planks.Clear();
        }
    }
}
