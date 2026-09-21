using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor-side half of the asset pipeline. Runs inside a throwaway Unity project in batch mode and
/// builds two bundles:
///
///   pirates     - Kenney's kit: static props and ships, one shared colour atlas.
///   characters  - Quaternius' animated characters.
///
/// Both import WITHOUT relying on their materials at runtime. The game renders with its own URP
/// shaders, and a shader packed here would clash with or be missing from the game, which is the
/// usual cause of everything turning pink. The mod builds materials at runtime instead.
///
/// Kenney models get their colour from a texture atlas shipped in the bundle. Quaternius models
/// have no textures, so their colours are extracted here into characters.json, which the mod
/// applies per renderer and material slot.
/// </summary>
public static class BundleBuilder
{
    private const string PiratesBundle = "pirates";
    private const string CharactersBundle = "characters";
    private const string PirateFolder = "Assets/PirateKit";
    private const string CharacterFolder = "Assets/Characters";

    public static void Build()
    {
        try
        {
            string outDir = Arg("-bundleOut", Path.Combine(Directory.GetCurrentDirectory(), "Build"));
            Directory.CreateDirectory(outDir);

            Log("Refreshing asset database...");
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

            int kit = TagPirateKit();
            var characters = TagCharacters();

            if (kit == 0 && characters.Count == 0)
            {
                Fail("No models found - nothing to build.");
                return;
            }

            AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(
                outDir, BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64);
            if (manifest == null)
            {
                Fail("BuildAssetBundles returned no manifest.");
                return;
            }

            foreach (string name in new[] { PiratesBundle, CharactersBundle })
            {
                string p = Path.Combine(outDir, name);
                if (File.Exists(p)) Log("BUILD OK: " + name + " (" + new FileInfo(p).Length + " bytes)");
            }

            if (characters.Count > 0)
            {
                string json = Path.Combine(outDir, "characters.json");
                File.WriteAllText(json, CharacterJson(characters), Encoding.UTF8);
                Log("Wrote character colours: " + json);
            }

            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Fail("Unhandled: " + e);
        }
    }

    // ------------------------------------------------------------------ Kenney kit

    private static int TagPirateKit()
    {
        if (!AssetDatabase.IsValidFolder(PirateFolder)) return 0;

        int models = 0;
        foreach (string guid in AssetDatabase.FindAssets("", new[] { PirateFolder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) continue;

            AssetImporter importer = AssetImporter.GetAtPath(path);
            if (importer == null) continue;

            ModelImporter model = importer as ModelImporter;
            if (model != null)
            {
                model.importCameras = false;
                model.importLights = false;
                model.materialImportMode = ModelImporterMaterialImportMode.None;
                model.importBlendShapes = false;
                // Read/write is required: the mod builds MeshColliders at runtime.
                model.isReadable = true;
                model.meshCompression = ModelImporterMeshCompression.Off;
                model.animationType = ModelImporterAnimationType.None;
                model.SaveAndReimport();
                models++;
            }
            importer.assetBundleName = PiratesBundle;
        }
        Log("Pirate kit: " + models + " model(s).");
        return models;
    }

    // ------------------------------------------------------------------ Quaternius characters

    private sealed class CharInfo
    {
        public string Asset;
        public List<string> Clips = new List<string>();
        public List<RendererInfo> Renderers = new List<RendererInfo>();
        public bool HasVertexColours;
    }

    private sealed class RendererInfo
    {
        public string Path;
        public List<Color> Colours = new List<Color>();
    }

    private static List<CharInfo> TagCharacters()
    {
        var result = new List<CharInfo>();
        if (!AssetDatabase.IsValidFolder(CharacterFolder)) return result;

        foreach (string guid in AssetDatabase.FindAssets("t:Model", new[] { CharacterFolder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            ModelImporter model = AssetImporter.GetAtPath(path) as ModelImporter;
            if (model == null) continue;

            model.importCameras = false;
            model.importLights = false;
            // Materials ARE imported here, but only so their colours can be read out below; the
            // mod never renders with them.
            model.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            // Legacy animation plays clips by name through the Animation component, which needs no
            // AnimatorController asset - exactly what a mod assembling characters at runtime wants.
            model.animationType = ModelImporterAnimationType.Legacy;
            model.importAnimation = true;
            model.isReadable = false;
            model.SaveAndReimport();
            model.assetBundleName = CharactersBundle;

            result.Add(Describe(path));
        }

        // The shared colour atlas, when the full Quaternius pack has been downloaded.
        int textures = 0;
        foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { CharacterFolder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            TextureImporter ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null) continue;
            // A 32x32 palette: no filtering, no mipmaps, no compression, or colours bleed together.
            ti.filterMode = FilterMode.Point;
            ti.mipmapEnabled = false;
            ti.textureCompression = TextureImporterCompression.Uncompressed;
            ti.SaveAndReimport();
            ti.assetBundleName = CharactersBundle;
            textures++;
        }

        Log("Characters: " + result.Count + " model(s), " + textures + " texture(s)" +
            (textures == 0 ? " - NO COLOUR ATLAS, characters will render grey." : "."));
        foreach (CharInfo c in result)
        {
            int slots = 0;
            foreach (RendererInfo r in c.Renderers) slots += r.Colours.Count;
            Log("  " + Path.GetFileName(c.Asset) + ": " + c.Renderers.Count + " renderer(s), " + slots +
                " material slot(s), vertex colours " + (c.HasVertexColours ? "YES" : "no") +
                ", clips [" + string.Join(", ", c.Clips) + "]");
        }
        return result;
    }

    private static CharInfo Describe(string assetPath)
    {
        var info = new CharInfo { Asset = assetPath.ToLowerInvariant() };

        foreach (UnityEngine.Object o in AssetDatabase.LoadAllAssetsAtPath(assetPath))
        {
            AnimationClip clip = o as AnimationClip;
            if (clip != null && !clip.name.StartsWith("__preview__", StringComparison.Ordinal))
                info.Clips.Add(clip.name);
        }

        GameObject root = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
        if (root == null) return info;

        foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
        {
            var ri = new RendererInfo { Path = PathFrom(root.transform, r.transform) };
            foreach (Material m in r.sharedMaterials)
                ri.Colours.Add(m != null ? ColourOf(m) : Color.white);
            info.Renderers.Add(ri);

            Mesh mesh = null;
            SkinnedMeshRenderer smr = r as SkinnedMeshRenderer;
            if (smr != null) mesh = smr.sharedMesh;
            else
            {
                MeshFilter mf = r.GetComponent<MeshFilter>();
                if (mf != null) mesh = mf.sharedMesh;
            }
            if (mesh != null && mesh.colors32 != null && mesh.colors32.Length > 0) info.HasVertexColours = true;
        }
        return info;
    }

    private static Color ColourOf(Material m)
    {
        if (m.HasProperty("_BaseColor")) return m.GetColor("_BaseColor");
        if (m.HasProperty("_Color")) return m.GetColor("_Color");
        return Color.white;
    }

    private static string PathFrom(Transform root, Transform t)
    {
        var parts = new List<string>();
        for (Transform p = t; p != null && p != root; p = p.parent) parts.Add(p.name);
        parts.Reverse();
        return string.Join("/", parts);
    }

    private static string CharacterJson(List<CharInfo> all)
    {
        var sb = new StringBuilder();
        sb.Append("{\n  \"models\": [\n");
        for (int i = 0; i < all.Count; i++)
        {
            CharInfo c = all[i];
            sb.Append("    {\n");
            sb.Append("      \"asset\": \"").Append(Esc(c.Asset)).Append("\",\n");
            sb.Append("      \"vertexColours\": ").Append(c.HasVertexColours ? "true" : "false").Append(",\n");
            sb.Append("      \"clips\": [").Append(string.Join(", ", c.Clips.ConvertAll(x => "\"" + Esc(x) + "\""))).Append("],\n");
            sb.Append("      \"renderers\": [\n");
            for (int j = 0; j < c.Renderers.Count; j++)
            {
                RendererInfo r = c.Renderers[j];
                sb.Append("        { \"path\": \"").Append(Esc(r.Path)).Append("\", \"colours\": [");
                for (int k = 0; k < r.Colours.Count; k++)
                {
                    Color col = r.Colours[k];
                    sb.Append("[").Append(F(col.r)).Append(",").Append(F(col.g)).Append(",")
                      .Append(F(col.b)).Append(",").Append(F(col.a)).Append("]");
                    if (k < r.Colours.Count - 1) sb.Append(",");
                }
                sb.Append("] }").Append(j < c.Renderers.Count - 1 ? ",\n" : "\n");
            }
            sb.Append("      ]\n    }").Append(i < all.Count - 1 ? ",\n" : "\n");
        }
        sb.Append("  ]\n}\n");
        return sb.ToString();
    }

    private static string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    private static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

    // ------------------------------------------------------------------ plumbing

    private static string Arg(string name, string fallback)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return fallback;
    }

    private static void Log(string msg) => Debug.Log("[BundleBuilder] " + msg);

    private static void Fail(string msg)
    {
        Debug.LogError("[BundleBuilder] FAILED: " + msg);
        EditorApplication.Exit(2);
    }
}
