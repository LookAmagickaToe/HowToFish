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

            Shader shader = BorrowGameShader();
            if (shader == null)
            {
                Diag.Error("Assets: could not find a usable shader; models would render pink.");
                return null;
            }

            _material = new Material(shader) { name = "ExpandedKit" };
            if (_colormap != null)
            {
                // URP calls it _BaseMap, the built-in pipeline _MainTex. Set whichever exists.
                if (_material.HasProperty("_BaseMap")) _material.SetTexture("_BaseMap", _colormap);
                if (_material.HasProperty("_MainTex")) _material.SetTexture("_MainTex", _colormap);
                _material.mainTexture = _colormap;
            }

            Diag.Info("Assets: kit material using shader '" + shader.name + "'.");
            return _material;
        }

        /// <summary>
        /// Takes the shader off a renderer the game owns. Tries the boat first because it is
        /// always present during play and is lit exactly like our ship should be.
        /// </summary>
        private static Shader BorrowGameShader()
        {
            try
            {
                if (BoatManager.Boat != null)
                {
                    Renderer r = BoatManager.Boat.GetComponentInChildren<Renderer>();
                    if (r != null && r.sharedMaterial != null && r.sharedMaterial.shader != null)
                        return r.sharedMaterial.shader;
                }
            }
            catch (Exception e)
            {
                Diag.Debug("Assets: boat shader unavailable (" + e.Message + ").");
            }

            // Any lit renderer in the scene will do.
            foreach (MeshRenderer mr in UnityEngine.Object.FindObjectsByType<MeshRenderer>())
            {
                if (mr == null || mr.sharedMaterial == null) continue;
                Shader s = mr.sharedMaterial.shader;
                if (s != null && s.name.IndexOf("Universal Render Pipeline", StringComparison.OrdinalIgnoreCase) >= 0)
                    return s;
            }

            return Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        }

        // ------------------------------------------------------------------ instantiating

        /// <summary>
        /// Creates an instance of a kit model, e.g. "ship-pirate-large". Returns null if the bundle
        /// is missing, so callers can fall back to placeholders.
        /// </summary>
        internal static GameObject Create(string modelName, Vector3 position, Quaternion rotation, Transform parent = null)
        {
            GameObject prefab = Prefab(modelName);
            if (prefab == null) return null;

            GameObject go = UnityEngine.Object.Instantiate(prefab, position, rotation, parent);
            go.name = "kit:" + modelName;
            ApplyMaterial(go);
            return go;
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
