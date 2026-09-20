using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;

namespace Expanded
{
    /// <summary>
    /// Loads the CC0 model bundle built by tools/build-bundles.ps1 and hands out ready-to-use
    /// instances.
    ///
    /// The bundle deliberately contains no materials. Shaders packed by a different Unity project
    /// are the classic cause of everything rendering pink, so instead we take a material straight
    /// off an object the game itself is rendering, clone it, and put the kit's colour atlas on it.
    /// Whatever shader and render pipeline the game uses, our models then use exactly the same one.
    /// </summary>
    internal static class ModAssets
    {
        private const string BundleFolder = "ExpandedAssets";
        private const string BundleFile = "pirates";
        private const string AssetPrefix = "assets/piratekit/";
        private const string ColormapAsset = AssetPrefix + "textures/colormap.png";

        private static AssetBundle _bundle;
        private static Texture2D _colormap;
        private static Material _material;
        private static readonly Dictionary<string, GameObject> Cache = new Dictionary<string, GameObject>(StringComparer.Ordinal);

        internal static bool Available => _bundle != null;

        internal static string BundlePath =>
            Path.Combine(Path.Combine(Paths.PluginPath, BundleFolder), BundleFile);

        // ------------------------------------------------------------------ loading

        internal static void Load()
        {
            if (_bundle != null) return;

            string path = BundlePath;
            if (!File.Exists(path))
            {
                Diag.Warn("Assets: no model bundle at " + path + " - the mod will fall back to " +
                          "placeholders built from game parts. Run tools\\build-bundles.ps1 -Install.");
                return;
            }

            try
            {
                _bundle = AssetBundle.LoadFromFile(path);
                if (_bundle == null)
                {
                    Diag.Error("Assets: bundle at " + path + " could not be opened (built with a " +
                               "different Unity version?).");
                    return;
                }

                _colormap = _bundle.LoadAsset<Texture2D>(ColormapAsset);
                Diag.Info("Assets: bundle loaded, " + _bundle.GetAllAssetNames().Length + " asset(s), colormap " +
                          (_colormap != null ? "ok" : "MISSING") + ".");
            }
            catch (Exception e)
            {
                Diag.Exception("ModAssets.Load", e);
            }
        }

        internal static void Unload()
        {
            try
            {
                Cache.Clear();
                _material = null;
                if (_bundle != null) { _bundle.Unload(false); _bundle = null; }
            }
            catch (Exception e)
            {
                Diag.Exception("ModAssets.Unload", e);
            }
        }

        // ------------------------------------------------------------------ materials

        /// <summary>
        /// One shared material for every kit model, cloned from something the game is already
        /// rendering so the shader, lighting and fog match the rest of the world.
        /// </summary>
        internal static Material KitMaterial()
        {
            if (_material != null) return _material;

            Material template = FindTexturedTemplate();
            if (template != null)
            {
                // Cloning a real game material keeps its shader keywords and render-queue settings,
                // which a bare new Material(shader) would lose.
                _material = new Material(template) { name = "ExpandedKit" };
            }
            else
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                if (shader == null)
                {
                    Diag.Error("Assets: no usable shader found; models would render untextured.");
                    return null;
                }
                _material = new Material(shader) { name = "ExpandedKit" };
            }

            ApplyColormap(_material);
            Diag.Info("Assets: kit material shader '" + _material.shader.name + "'" +
                      (template != null ? " (cloned from '" + template.name + "')" : " (created)") +
                      ", texture " + (_material.mainTexture != null ? "set" : "MISSING") + ".");
            return _material;
        }

        private static void ApplyColormap(Material m)
        {
            if (_colormap == null || m == null) return;

            // URP names it _BaseMap, the built-in pipeline _MainTex. Set every one that exists, and
            // clear any tint the template carried, or the atlas colours come out wrong.
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", _colormap);
            if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", _colormap);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.white);
            if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
            m.mainTexture = _colormap;
        }

        /// <summary>
        /// Finds a game material that actually samples a texture map. The boat is a poor donor: it
        /// uses the game's skin shader, which colours geometry through its own properties and
        /// ignores _BaseMap entirely - that is why kit models came out plain white.
        /// </summary>
        private static Material FindTexturedTemplate()
        {
            Material fallback = null;
            foreach (MeshRenderer mr in UnityEngine.Object.FindObjectsByType<MeshRenderer>())
            {
                if (mr == null) continue;
                Material m = mr.sharedMaterial;
                if (m == null || m.shader == null) continue;
                if (!m.HasProperty("_BaseMap") && !m.HasProperty("_MainTex")) continue;

                // Must have a texture bound, otherwise the shader may not have the sampling
                // keyword enabled and we would be white again.
                if (m.mainTexture == null) { fallback = fallback ?? m; continue; }

                string n = m.shader.name;
                if (n.IndexOf("Universal Render Pipeline/Lit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Universal Render Pipeline/Simple Lit", StringComparison.OrdinalIgnoreCase) >= 0)
                    return m;

                fallback = fallback ?? m;
            }
            return fallback;
        }

        // ------------------------------------------------------------------ instantiating

        /// <summary>
        /// Creates an instance of a kit model, e.g. "ship-pirate-large". Returns null if the bundle
        /// is missing, so callers can fall back to placeholders.
        /// </summary>
        internal static GameObject Create(string modelName, Vector3 position, Quaternion rotation,
                                          Transform parent = null, bool solid = true)
        {
            GameObject prefab = Prefab(modelName);
            if (prefab == null) return null;

            GameObject go = UnityEngine.Object.Instantiate(prefab, position, rotation, parent);
            go.name = "kit:" + modelName;
            ApplyMaterial(go);

            if (solid)
            {
                AddColliders(go);
                // Put it on the game's level layer so players can stand on it, and so the swarm's
                // cover checks treat it as real geometry - birds then smash into the rigging.
                SetLayerRecursively(go, LevelLayerIndex());
            }
            return go;
        }

        /// <summary>
        /// Gives every mesh a collider. Imported meshes have none, so without this you walk straight
        /// through the ship. Meshes are imported read/write enabled for exactly this reason.
        /// </summary>
        internal static int AddColliders(GameObject go)
        {
            int added = 0, failed = 0;
            foreach (MeshFilter mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf == null || mf.sharedMesh == null) continue;
                if (mf.GetComponent<Collider>() != null) continue;

                try
                {
                    MeshCollider mc = mf.gameObject.AddComponent<MeshCollider>();
                    mc.sharedMesh = mf.sharedMesh;   // static geometry: concave is fine and exact
                    added++;
                }
                catch (Exception e)
                {
                    if (failed++ == 0)
                        Diag.Warn("Assets: collider for '" + go.name + "' failed (" + e.Message +
                                  "). Rebuild the bundle so meshes are read/write enabled.");
                }
            }
            return added;
        }

        private static int _levelLayer = -1;

        private static int LevelLayerIndex()
        {
            if (_levelLayer >= 0) return _levelLayer;
            try
            {
                int mask = GameInfo.LevelLayer.value;
                for (int i = 0; i < 32; i++)
                {
                    if ((mask & (1 << i)) == 0) continue;
                    _levelLayer = i;
                    return i;
                }
            }
            catch (Exception e)
            {
                Diag.Debug("Assets: level layer unavailable (" + e.Message + ").");
            }
            _levelLayer = 0;
            return 0;
        }

        private static void SetLayerRecursively(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform t in go.transform)
                SetLayerRecursively(t.gameObject, layer);
        }

        private static GameObject Prefab(string modelName)
        {
            if (_bundle == null) return null;

            GameObject cached;
            if (Cache.TryGetValue(modelName, out cached) && cached != null) return cached;

            string assetPath = AssetPrefix + modelName.ToLowerInvariant() + ".fbx";
            GameObject prefab = _bundle.LoadAsset<GameObject>(assetPath);
            if (prefab == null)
            {
                Diag.Warn("Assets: model '" + modelName + "' not in bundle (looked for " + assetPath + ").");
                return null;
            }

            Cache[modelName] = prefab;
            return prefab;
        }

        /// <summary>Models are imported without materials, so every renderer needs ours.</summary>
        internal static void ApplyMaterial(GameObject go)
        {
            Material mat = KitMaterial();
            if (mat == null || go == null) return;

            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                var mats = new Material[r.sharedMaterials.Length == 0 ? 1 : r.sharedMaterials.Length];
                for (int i = 0; i < mats.Length; i++) mats[i] = mat;
                r.sharedMaterials = mats;
            }
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>
        /// World-space size of a model, used to scale kit pieces to the game's proportions. The kits
        /// are authored at their own scale, which has nothing to do with this game's metres.
        /// </summary>
        internal static Bounds Measure(GameObject go)
        {
            var bounds = new Bounds(go.transform.position, Vector3.zero);
            bool any = false;
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (!any) { bounds = r.bounds; any = true; }
                else bounds.Encapsulate(r.bounds);
            }
            return bounds;
        }

        internal static IEnumerable<string> ModelNames()
        {
            if (_bundle == null) yield break;
            foreach (string n in _bundle.GetAllAssetNames())
            {
                if (!n.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) continue;
                yield return Path.GetFileNameWithoutExtension(n);
            }
        }
    }
}
