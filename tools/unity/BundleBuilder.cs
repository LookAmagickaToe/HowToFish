using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor-side half of the asset pipeline. Runs inside a throwaway Unity project in batch mode:
/// imports the CC0 model kits, packs them into an AssetBundle, and writes it where the mod expects.
///
/// Deliberately imports models WITHOUT materials. The game renders with its own URP shaders, and a
/// shader packed here would either be missing variants or clash with the game's, which is the usual
/// cause of everything turning pink. The mod therefore builds materials at runtime from the game's
/// own shader plus the colour atlas shipped in this bundle.
/// </summary>
public static class BundleBuilder
{
    private const string BundleName = "pirates";
    private const string SourceFolder = "Assets/PirateKit";

    public static void Build()
    {
        try
        {
            string outDir = Arg("-bundleOut", Path.Combine(Directory.GetCurrentDirectory(), "Build"));
            Directory.CreateDirectory(outDir);

            Log("Refreshing asset database...");
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

            if (!AssetDatabase.IsValidFolder(SourceFolder))
            {
                Fail("Source folder missing: " + SourceFolder);
                return;
            }

            int models = 0, textures = 0;
            foreach (string guid in AssetDatabase.FindAssets("", new[] { SourceFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) continue;

                AssetImporter importer = AssetImporter.GetAtPath(path);
                if (importer == null) continue;

                ModelImporter model = importer as ModelImporter;
                if (model != null)
                {
                    // Geometry only: no cameras, lights, materials or rigs we do not use.
                    model.importCameras = false;
                    model.importLights = false;
                    model.materialImportMode = ModelImporterMaterialImportMode.None;
                    model.importBlendShapes = false;
                    model.isReadable = false;          // smaller bundle; we never edit meshes at runtime
                    model.meshCompression = ModelImporterMeshCompression.Medium;
                    model.SaveAndReimport();
                    models++;
                }
                else if (importer is TextureImporter)
                {
                    textures++;
                }

                importer.assetBundleName = BundleName;
            }

            Log("Tagged " + models + " model(s) and " + textures + " texture(s) for bundle '" + BundleName + "'.");
            if (models == 0)
            {
                Fail("No models found under " + SourceFolder + " - nothing to build.");
                return;
            }

            AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(
                outDir,
                BuildAssetBundleOptions.ChunkBasedCompression,
                BuildTarget.StandaloneWindows64);

            if (manifest == null)
            {
                Fail("BuildAssetBundles returned no manifest.");
                return;
            }

            string bundlePath = Path.Combine(outDir, BundleName);
            long size = File.Exists(bundlePath) ? new FileInfo(bundlePath).Length : -1;
            Log("BUILD OK: " + bundlePath + " (" + size + " bytes)");

            // Print the contents so the build log doubles as an inventory for the mod side.
            AssetBundle bundle = AssetBundle.LoadFromFile(bundlePath);
            if (bundle != null)
            {
                string[] names = bundle.GetAllAssetNames();
                Log("Bundle contains " + names.Length + " asset(s):");
                foreach (string n in names) Log("  " + n);
                bundle.Unload(true);
            }

            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Fail("Unhandled: " + e);
        }
    }

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
