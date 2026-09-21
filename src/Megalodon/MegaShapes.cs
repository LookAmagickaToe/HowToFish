using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Expanded.Megalodon
{
    /// <summary>
    /// Small props built from Unity primitives: the wakeboard, the bathtub (with duck), buoys,
    /// jellyfish, flying fish, barrel mines, ramps, dentures. No download needed, and they all use a
    /// clone of a material the game is already rendering, so they sit in the world's lighting and fog.
    ///
    /// Every prop is decoration only: primitives come with colliders, which are removed at once so
    /// nothing here can knock the boat about or be stood on.
    /// </summary>
    internal static class MegaShapes
    {
        private static readonly Dictionary<string, Material> Materials = new Dictionary<string, Material>(StringComparer.Ordinal);

        // ------------------------------------------------------------------ materials

        /// <summary>A plain coloured material in the game's own shader; optional self-glow.</summary>
        internal static Material Flat(Color c, float glow = 0f)
        {
            string key = ColorUtility.ToHtmlStringRGBA(c) + "/" + glow.ToString("0.00");
            Material m;
            if (Materials.TryGetValue(key, out m) && m != null) return m;

            Material template = ModAssets.KitMaterial();
            if (template != null) m = new Material(template);
            else
            {
                Shader s = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                if (s == null) return null;
                m = new Material(s);
            }
            m.name = "ExpandedFlat_" + key;
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", Texture2D.whiteTexture);
            if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", Texture2D.whiteTexture);
            m.mainTexture = Texture2D.whiteTexture;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.25f);
            if (glow > 0f && m.HasProperty("_EmissionColor"))
            {
                if (m.HasProperty("_EmissionMap")) m.SetTexture("_EmissionMap", Texture2D.whiteTexture);
                m.SetColor("_EmissionColor", c * glow);
                m.EnableKeyword("_EMISSION");
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            }
            Materials[key] = m;
            return m;
        }

        internal static void ClearCache() => Materials.Clear();

        // ------------------------------------------------------------------ primitives

        internal static GameObject Prim(PrimitiveType type, Transform parent, Vector3 pos, Vector3 scale, Color c,
                                        Vector3? euler = null, float glow = 0f, bool shadows = true)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            Collider col = go.GetComponent<Collider>();
            if (col != null) UnityEngine.Object.DestroyImmediate(col);
            go.name = type.ToString();
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = euler.HasValue ? Quaternion.Euler(euler.Value) : Quaternion.identity;
            go.transform.localScale = scale;
            Renderer r = go.GetComponent<Renderer>();
            if (r != null)
            {
                Material m = Flat(c, glow);
                if (m != null) r.sharedMaterial = m;
                r.shadowCastingMode = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            }
            return go;
        }

        internal static Transform Root(string name, Transform parent = null)
        {
            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent, false);
            return go.transform;
        }

        /// <summary>Removes every collider under a kit model, so it is purely visual.</summary>
        internal static void StripColliders(GameObject go)
        {
            if (go == null) return;
            foreach (Collider c in go.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(c);
        }

        /// <summary>
        /// Wraps a kit model in a root and fits it: longest horizontal side scaled to
        /// <paramref name="length"/>, centred, resting on the root's origin.
        /// </summary>
        internal static Transform FitKit(string model, Transform parent, float length, Vector3 euler)
        {
            Transform root = Root("kit-fit:" + model, parent);
            GameObject go = ModAssets.Create(model, Vector3.zero, Quaternion.identity, null, solid: false);
            if (go == null) { UnityEngine.Object.Destroy(root.gameObject); return null; }
            StripColliders(go);

            go.transform.rotation = Quaternion.Euler(euler);
            Bounds b = ModAssets.Measure(go);
            float longest = Mathf.Max(b.size.x, b.size.z);
            if (longest > 1e-3f) go.transform.localScale *= length / longest;
            b = ModAssets.Measure(go);
            go.transform.position -= new Vector3(b.center.x, b.min.y, b.center.z);

            // Measured at the world origin with no parent, so its world pose is exactly the pose it
            // should have relative to the root.
            go.transform.SetParent(root, false);
            return root;
        }

        // ------------------------------------------------------------------ colours

        internal static readonly Color BoardOrange = new Color(1f, 0.42f, 0.08f);
        internal static readonly Color Charcoal = new Color(0.12f, 0.12f, 0.13f);
        internal static readonly Color Porcelain = new Color(0.95f, 0.95f, 0.92f);
        internal static readonly Color Duck = new Color(1f, 0.85f, 0.1f);
        internal static readonly Color Beak = new Color(1f, 0.45f, 0.05f);
        internal static readonly Color Gold = new Color(0.85f, 0.65f, 0.15f);
        internal static readonly Color Wood = new Color(0.45f, 0.29f, 0.15f);
        internal static readonly Color DarkWood = new Color(0.3f, 0.18f, 0.09f);
        internal static readonly Color BuoyRed = new Color(0.85f, 0.1f, 0.08f);
        internal static readonly Color Jelly = new Color(1f, 0.45f, 0.8f);
        internal static readonly Color FishBlue = new Color(0.45f, 0.62f, 0.8f);
        internal static readonly Color Gum = new Color(0.95f, 0.5f, 0.55f);

        // ------------------------------------------------------------------ boards

        /// <summary>A wakeboard lying flat, long axis along local Z, top face up, origin at its underside.</summary>
        internal static Transform Wakeboard(Transform parent)
        {
            Transform t = Root("Wakeboard", parent);
            Prim(PrimitiveType.Capsule, t, new Vector3(0f, 0.025f, 0f), new Vector3(0.43f, 0.7f, 0.05f), BoardOrange, new Vector3(90f, 0f, 0f));
            Prim(PrimitiveType.Capsule, t, new Vector3(0f, 0.052f, 0f), new Vector3(0.1f, 0.6f, 0.01f), Color.white, new Vector3(90f, 0f, 0f));
            Prim(PrimitiveType.Cube, t, new Vector3(0f, 0.08f, 0.27f), new Vector3(0.24f, 0.06f, 0.13f), Charcoal);
            Prim(PrimitiveType.Cube, t, new Vector3(0f, 0.08f, -0.27f), new Vector3(0.24f, 0.06f, 0.13f), Charcoal);
            return t;
        }

        /// <summary>Old Salt's cabin door. The Kenney door if the bundle has it, planks otherwise.</summary>
        internal static Transform Door(Transform parent)
        {
            Transform kit = FitKit("castle-door", parent, 1.75f, new Vector3(90f, 0f, 0f));
            if (kit != null) { kit.name = "Door"; return kit; }

            Transform t = Root("Door", parent);
            Prim(PrimitiveType.Cube, t, new Vector3(0f, 0.035f, 0f), new Vector3(0.8f, 0.07f, 1.75f), Wood);
            for (int i = -1; i <= 1; i++)
                Prim(PrimitiveType.Cube, t, new Vector3(i * 0.26f, 0.072f, 0f), new Vector3(0.02f, 0.01f, 1.7f), DarkWood);
            Prim(PrimitiveType.Cube, t, new Vector3(0f, 0.075f, 0.55f), new Vector3(0.78f, 0.015f, 0.1f), DarkWood);
            Prim(PrimitiveType.Cube, t, new Vector3(0f, 0.075f, -0.55f), new Vector3(0.78f, 0.015f, 0.1f), DarkWood);
            Prim(PrimitiveType.Sphere, t, new Vector3(0.3f, 0.1f, 0.05f), Vector3.one * 0.07f, Gold, null, 0.3f);
            return t;
        }

        /// <summary>A claw-foot bathtub with a rubber duck on the rim. You stand in it.</summary>
        internal static Transform Bathtub(Transform parent)
        {
            Transform t = Root("Bathtub", parent);
            const float L = 1.5f, W = 0.78f, H = 0.5f, Th = 0.06f;
            Prim(PrimitiveType.Cube, t, new Vector3(0f, Th * 0.5f, 0f), new Vector3(W, Th, L), Porcelain);
            Prim(PrimitiveType.Cube, t, new Vector3(W * 0.5f - Th * 0.5f, H * 0.5f, 0f), new Vector3(Th, H, L), Porcelain);
            Prim(PrimitiveType.Cube, t, new Vector3(-W * 0.5f + Th * 0.5f, H * 0.5f, 0f), new Vector3(Th, H, L), Porcelain);
            Prim(PrimitiveType.Cube, t, new Vector3(0f, H * 0.5f, L * 0.5f - Th * 0.5f), new Vector3(W, H, Th), Porcelain);
            Prim(PrimitiveType.Cube, t, new Vector3(0f, H * 0.5f, -L * 0.5f + Th * 0.5f), new Vector3(W, H, Th), Porcelain);
            foreach (float x in new[] { -1f, 1f })
                foreach (float z in new[] { -1f, 1f })
                    Prim(PrimitiveType.Sphere, t, new Vector3(x * (W * 0.5f - 0.08f), -0.02f, z * (L * 0.5f - 0.1f)), Vector3.one * 0.11f, Gold);
            Prim(PrimitiveType.Cylinder, t, new Vector3(0f, H * 0.75f, -L * 0.5f + 0.1f), new Vector3(0.05f, 0.12f, 0.05f), Gold, new Vector3(-60f, 0f, 0f));

            // The duck. Of course there is a duck.
            Transform duck = Root("RubberDuck", t);
            duck.localPosition = new Vector3(W * 0.5f - Th * 0.5f, H + 0.07f, L * 0.5f - 0.3f);
            Prim(PrimitiveType.Sphere, duck, Vector3.zero, new Vector3(0.17f, 0.13f, 0.21f), Duck);
            Prim(PrimitiveType.Sphere, duck, new Vector3(0f, 0.1f, 0.07f), Vector3.one * 0.12f, Duck);
            Prim(PrimitiveType.Cube, duck, new Vector3(0f, 0.09f, 0.14f), new Vector3(0.07f, 0.025f, 0.06f), Beak);
            Prim(PrimitiveType.Sphere, duck, new Vector3(0.035f, 0.125f, 0.11f), Vector3.one * 0.02f, Charcoal);
            Prim(PrimitiveType.Sphere, duck, new Vector3(-0.035f, 0.125f, 0.11f), Vector3.one * 0.02f, Charcoal);
            return t;
        }

        /// <summary>What you ride on at the given tier. Null for bare feet.</summary>
        internal static Transform BoardFor(Board b, Transform parent)
        {
            switch (b)
            {
                case Board.Wakeboard: return Wakeboard(parent);
                case Board.Door: return Door(parent);
                case Board.Bathtub: return Bathtub(parent);
                default: return null;
            }
        }

        // ------------------------------------------------------------------ sea things

        internal static Transform Buoy(Transform parent)
        {
            Transform t = Root("Buoy", parent);
            Prim(PrimitiveType.Cylinder, t, new Vector3(0f, 0.25f, 0f), new Vector3(0.7f, 0.5f, 0.7f), BuoyRed);
            Prim(PrimitiveType.Cylinder, t, new Vector3(0f, 0.45f, 0f), new Vector3(0.72f, 0.07f, 0.72f), Color.white);
            Prim(PrimitiveType.Sphere, t, new Vector3(0f, 0.78f, 0f), Vector3.one * 0.45f, BuoyRed);
            Prim(PrimitiveType.Cylinder, t, new Vector3(0f, 1.25f, 0f), new Vector3(0.04f, 0.45f, 0.04f), Charcoal);
            Prim(PrimitiveType.Cube, t, new Vector3(0f, 1.55f, 0.16f), new Vector3(0.02f, 0.2f, 0.3f), Duck);
            Prim(PrimitiveType.Sphere, t, new Vector3(0f, 1.72f, 0f), Vector3.one * 0.1f, new Color(1f, 0.9f, 0.4f), null, 2f, false);
            return t;
        }

        internal static Transform Jellyfish(Transform parent, float size)
        {
            Transform t = Root("Jellyfish", parent);
            Prim(PrimitiveType.Sphere, t, Vector3.zero, new Vector3(0.9f, 0.5f, 0.9f) * size, Jelly, null, 0.7f, false);
            Prim(PrimitiveType.Sphere, t, new Vector3(0f, -0.04f, 0f) * size, new Vector3(0.55f, 0.3f, 0.55f) * size, new Color(1f, 0.8f, 0.95f), null, 1f, false);
            for (int i = 0; i < 6; i++)
            {
                float a = i * Mathf.PI * 2f / 6f;
                Prim(PrimitiveType.Cylinder, t, new Vector3(Mathf.Cos(a) * 0.25f, -0.4f, Mathf.Sin(a) * 0.25f) * size,
                     new Vector3(0.04f, 0.4f, 0.04f) * size, Jelly, new Vector3(Mathf.Sin(a) * 12f, 0f, Mathf.Cos(a) * 12f), 0.5f, false);
            }
            return t;
        }

        /// <summary>A flying fish: silver body, big wing fins. Long axis along local Z.</summary>
        internal static Transform FlyingFish(Transform parent)
        {
            Transform t = Root("FlyingFish", parent);
            Prim(PrimitiveType.Capsule, t, Vector3.zero, new Vector3(0.13f, 0.21f, 0.13f), FishBlue, new Vector3(90f, 0f, 0f), 0.1f);
            Prim(PrimitiveType.Cube, t, new Vector3(0.2f, 0.02f, 0.04f), new Vector3(0.34f, 0.012f, 0.16f), new Color(0.7f, 0.85f, 1f), new Vector3(0f, -12f, 8f));
            Prim(PrimitiveType.Cube, t, new Vector3(-0.2f, 0.02f, 0.04f), new Vector3(0.34f, 0.012f, 0.16f), new Color(0.7f, 0.85f, 1f), new Vector3(0f, 12f, -8f));
            Prim(PrimitiveType.Cube, t, new Vector3(0f, 0f, -0.25f), new Vector3(0.012f, 0.18f, 0.12f), FishBlue);
            Prim(PrimitiveType.Sphere, t, new Vector3(0.05f, 0.03f, 0.15f), Vector3.one * 0.035f, Charcoal);
            Prim(PrimitiveType.Sphere, t, new Vector3(-0.05f, 0.03f, 0.15f), Vector3.one * 0.035f, Charcoal);
            return t;
        }

        /// <summary>A barrel with a blinking light: a mine. The Kenney barrel if available.</summary>
        internal static Transform Mine(Transform parent)
        {
            Transform t = Root("BarrelMine", parent);
            Transform kit = FitKit("barrel", t, 0.8f, Vector3.zero);
            if (kit == null)
            {
                Prim(PrimitiveType.Cylinder, t, new Vector3(0f, 0.45f, 0f), new Vector3(0.7f, 0.45f, 0.7f), Wood);
                Prim(PrimitiveType.Cylinder, t, new Vector3(0f, 0.2f, 0f), new Vector3(0.72f, 0.04f, 0.72f), Charcoal);
                Prim(PrimitiveType.Cylinder, t, new Vector3(0f, 0.7f, 0f), new Vector3(0.72f, 0.04f, 0.72f), Charcoal);
            }
            Prim(PrimitiveType.Cylinder, t, new Vector3(0f, 0.5f, 0f), new Vector3(0.74f, 0.06f, 0.74f), BuoyRed, null, 0.4f);
            Prim(PrimitiveType.Sphere, t, new Vector3(0f, 1.0f, 0f), Vector3.one * 0.14f, new Color(1f, 0.15f, 0.1f), null, 3f, false).name = "Blink";
            return t;
        }

        /// <summary>A floating ramp of planks, low end facing local -Z. About 3.2 m long.</summary>
        internal static Transform Ramp(Transform parent)
        {
            Transform t = Root("Ramp", parent);
            const float len = 3.2f, angle = 17f;
            float rise = Mathf.Sin(angle * Mathf.Deg2Rad) * len;
            Transform deck = Root("Deck", t);
            deck.localPosition = new Vector3(0f, rise * 0.5f, 0f);
            deck.localRotation = Quaternion.Euler(-angle, 0f, 0f);
            for (int i = 0; i < 6; i++)
                Prim(PrimitiveType.Cube, deck, new Vector3(-0.85f + i * 0.34f, 0f, 0f), new Vector3(0.3f, 0.07f, len), i % 2 == 0 ? Wood : DarkWood);
            Prim(PrimitiveType.Cube, deck, new Vector3(0f, -0.06f, len * 0.3f), new Vector3(2.1f, 0.08f, 0.15f), DarkWood);
            Prim(PrimitiveType.Cube, deck, new Vector3(0f, -0.06f, -len * 0.3f), new Vector3(2.1f, 0.08f, 0.15f), DarkWood);
            // Floats underneath, so it plausibly stays up.
            Transform barrel = FitKit("barrel", t, 0.7f, new Vector3(0f, 0f, 90f));
            if (barrel != null) barrel.localPosition = new Vector3(0f, -0.25f, len * 0.3f);
            else Prim(PrimitiveType.Cylinder, t, new Vector3(0f, 0.05f, len * 0.3f), new Vector3(0.6f, 0.9f, 0.6f), Wood, new Vector3(0f, 0f, 90f));
            return t;
        }

        /// <summary>The dark shape under the water before it strikes. Long axis along local Z.</summary>
        internal static Transform Shadow(Transform parent, float length)
        {
            Transform t = Root("SharkShadow", parent);
            Prim(PrimitiveType.Sphere, t, Vector3.zero, new Vector3(length * 0.28f, 0.02f, length), new Color(0.02f, 0.05f, 0.1f), null, 0f, false);
            Prim(PrimitiveType.Sphere, t, new Vector3(0f, 0f, -length * 0.1f), new Vector3(length * 0.62f, 0.018f, length * 0.16f), new Color(0.02f, 0.05f, 0.1f), null, 0f, false);
            return t;
        }

        /// <summary>A tooth for the tooth rain.</summary>
        internal static Transform Tooth(Transform parent)
        {
            Transform t = Root("Tooth", parent);
            Prim(PrimitiveType.Capsule, t, Vector3.zero, new Vector3(0.16f, 0.2f, 0.07f), Porcelain, new Vector3(0f, 0f, 0f), 0.35f);
            Prim(PrimitiveType.Cube, t, new Vector3(0f, -0.19f, 0f), new Vector3(0.1f, 0.12f, 0.06f), Porcelain, new Vector3(0f, 0f, 45f), 0.35f);
            return t;
        }

        /// <summary>Old Salt's dentures: a horseshoe of teeth on pink gums, upper and lower.</summary>
        internal static Transform Dentures(Transform parent)
        {
            Transform t = Root("Dentures", parent);
            for (int jaw = 0; jaw < 2; jaw++)
            {
                float y = jaw == 0 ? 0f : 0.07f;
                for (int i = 0; i < 9; i++)
                {
                    float a = Mathf.Lerp(-80f, 80f, i / 8f) * Mathf.Deg2Rad;
                    Vector3 p = new Vector3(Mathf.Sin(a) * 0.1f, y, Mathf.Cos(a) * 0.1f);
                    Prim(PrimitiveType.Sphere, t, p - new Vector3(0f, jaw == 0 ? 0.015f : -0.015f, 0f), new Vector3(0.05f, 0.03f, 0.05f), Gum, null, 0.3f, false);
                    Prim(PrimitiveType.Cube, t, p + new Vector3(0f, jaw == 0 ? 0.012f : -0.012f, 0.004f), new Vector3(0.026f, 0.03f, 0.018f), Color.white,
                         new Vector3(0f, a * Mathf.Rad2Deg, 0f), 0.5f, false);
                }
            }
            return t;
        }

        internal static Transform GameBoy(Transform parent)
        {
            Transform t = Root("GameBoy", parent);
            Prim(PrimitiveType.Cube, t, Vector3.zero, new Vector3(0.18f, 0.29f, 0.04f), new Color(0.72f, 0.72f, 0.7f));
            Prim(PrimitiveType.Cube, t, new Vector3(0f, 0.06f, 0.021f), new Vector3(0.13f, 0.1f, 0.004f), new Color(0.45f, 0.55f, 0.2f), null, 0.4f);
            Prim(PrimitiveType.Cube, t, new Vector3(-0.045f, -0.06f, 0.021f), new Vector3(0.05f, 0.015f, 0.006f), Charcoal);
            Prim(PrimitiveType.Cube, t, new Vector3(-0.045f, -0.06f, 0.021f), new Vector3(0.015f, 0.05f, 0.006f), Charcoal);
            Prim(PrimitiveType.Sphere, t, new Vector3(0.04f, -0.05f, 0.02f), Vector3.one * 0.022f, new Color(0.6f, 0.1f, 0.25f));
            Prim(PrimitiveType.Sphere, t, new Vector3(0.065f, -0.035f, 0.02f), Vector3.one * 0.022f, new Color(0.6f, 0.1f, 0.25f));
            return t;
        }

        // ------------------------------------------------------------------ meshes

        private static Mesh _inside;

        /// <summary>A unit sphere seen from the inside (faces and normals flipped): the megalodon's stomach.</summary>
        internal static Mesh InsideSphere()
        {
            if (_inside != null) return _inside;
            GameObject tmp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Mesh src = tmp.GetComponent<MeshFilter>().sharedMesh;
            var m = new Mesh { name = "ExpandedInsideSphere" };
            m.vertices = src.vertices;
            m.uv = src.uv;
            Vector3[] n = src.normals;
            for (int i = 0; i < n.Length; i++) n[i] = -n[i];
            m.normals = n;
            int[] tri = src.triangles;
            for (int i = 0; i < tri.Length; i += 3) { int a = tri[i]; tri[i] = tri[i + 1]; tri[i + 1] = a; }
            m.triangles = tri;
            m.RecalculateBounds();
            UnityEngine.Object.DestroyImmediate(tmp);
            _inside = m;
            return m;
        }
    }
}
