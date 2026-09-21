using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;

namespace Expanded
{
    /// <summary>
    /// Loads the animated Quaternius characters and hands out ready-to-use, animated instances.
    ///
    /// Colour: every Quaternius model samples one shared palette texture, Atlas_Pirate.png. When it
    /// is present in the bundle the characters render in full colour through the game's own shader.
    /// Without it they still work, just in a neutral grey - an honest placeholder rather than a crash.
    ///
    /// Animation: clips are imported as legacy clips and played by name through the Animation
    /// component, so no AnimatorController asset is needed. Quaternius names clips like
    /// "CharacterArmature|...|Idle|..."; callers just ask for "Idle".
    /// </summary>
    internal static class ModCharacters
    {
        private const string BundleFile = "characters";
        private const string AssetPrefix = "assets/characters/";
        private const string AtlasAsset = AssetPrefix + "textures/atlas_pirate.png";

        private static AssetBundle _bundle;
        private static Texture2D _atlas;
        private static Material _material;

        /// <summary>Self-glow of the characters' own colours, 0 = none. Set from the Npcs config.</summary>
        internal static float Brightness = 0.35f;
        private static readonly Dictionary<string, GameObject> Cache = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        private static bool _warnedGrey;

        internal static bool Available => _bundle != null;
        internal static bool HasColour => _atlas != null;

        internal static void Load()
        {
            if (_bundle != null) return;

            string path = Path.Combine(Path.Combine(Paths.PluginPath, "ExpandedAssets"), BundleFile);
            if (!File.Exists(path))
            {
                Diag.Warn("Characters: no bundle at " + path + " - NPCs and crew will be missing.");
                return;
            }

            try
            {
                _bundle = AssetBundle.LoadFromFile(path);
                if (_bundle == null) { Diag.Error("Characters: bundle could not be opened."); return; }

                _atlas = _bundle.LoadAsset<Texture2D>(AtlasAsset);
                if (_atlas != null)
                {
                    // A 32x32 palette must not be filtered, or neighbouring colours bleed together.
                    _atlas.filterMode = FilterMode.Point;
                }

                Diag.Info("Characters: bundle loaded, " + _bundle.GetAllAssetNames().Length + " asset(s), colour atlas " +
                          (_atlas != null ? "present" : "MISSING (characters will be grey)") + ".");
            }
            catch (Exception e)
            {
                Diag.Exception("ModCharacters.Load", e);
            }
        }

        // ------------------------------------------------------------------ instances

        /// <summary>Creates an animated character, e.g. "characters_captain_barbarossa". Null if unavailable.</summary>
        internal static GameObject Create(string model, Vector3 position, Quaternion rotation, Transform parent = null)
        {
            GameObject prefab = Prefab(model);
            if (prefab == null) return null;

            GameObject go = UnityEngine.Object.Instantiate(prefab, position, rotation, parent);
            go.name = "char:" + model;
            ApplyMaterial(go);

            // Characters are decoration and story actors, never physics: strip anything that could
            // collide with the ship or the boat.
            foreach (Collider c in go.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.Destroy(c);

            // Skinned meshes outside the camera frustum stop animating by default; keep them moving
            // so a crew member does not freeze mid-pose when you look back.
            foreach (SkinnedMeshRenderer smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                smr.updateWhenOffscreen = true;

            return go;
        }

        private static GameObject Prefab(string model)
        {
            if (_bundle == null) return null;

            GameObject cached;
            if (Cache.TryGetValue(model, out cached) && cached != null) return cached;

            GameObject prefab = _bundle.LoadAsset<GameObject>(AssetPrefix + model.ToLowerInvariant() + ".fbx");
            if (prefab == null)
            {
                Diag.Warn("Characters: '" + model + "' not in bundle.");
                return null;
            }
            Cache[model] = prefab;
            return prefab;
        }

        private static Material CharacterMaterial()
        {
            if (_material != null) return _material;

            Material kit = ModAssets.KitMaterial();
            if (kit == null) return null;

            _material = new Material(kit) { name = "ExpandedCharacter" };
            if (_atlas != null)
            {
                if (_material.HasProperty("_BaseMap")) _material.SetTexture("_BaseMap", _atlas);
                if (_material.HasProperty("_MainTex")) _material.SetTexture("_MainTex", _atlas);
                _material.mainTexture = _atlas;
                if (_material.HasProperty("_BaseColor")) _material.SetColor("_BaseColor", Color.white);

                // The pirate palette is mostly black, charcoal and dark brown; under the game's light
                // the figures read as dark silhouettes. Matte surface plus a faint glow of their own
                // colours lifts the shadows without washing out the palette.
                if (_material.HasProperty("_Metallic")) _material.SetFloat("_Metallic", 0f);
                if (_material.HasProperty("_Smoothness")) _material.SetFloat("_Smoothness", 0.1f);
                float glow = Mathf.Clamp01(Brightness);
                if (glow > 0f && _material.HasProperty("_EmissionMap") && _material.HasProperty("_EmissionColor"))
                {
                    _material.SetTexture("_EmissionMap", _atlas);
                    _material.SetColor("_EmissionColor", new Color(glow, glow, glow));
                    _material.EnableKeyword("_EMISSION");
                    _material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
                }
            }
            else
            {
                // No atlas: a flat, slightly warm grey reads as "unpainted figure" rather than an error.
                if (_material.HasProperty("_BaseMap")) _material.SetTexture("_BaseMap", Texture2D.whiteTexture);
                if (_material.HasProperty("_MainTex")) _material.SetTexture("_MainTex", Texture2D.whiteTexture);
                if (_material.HasProperty("_BaseColor")) _material.SetColor("_BaseColor", new Color(0.72f, 0.7f, 0.66f));
                if (!_warnedGrey)
                {
                    _warnedGrey = true;
                    Diag.Warn("Characters: Atlas_Pirate.png missing - download the full pack from " +
                              "quaternius.com/packs/piratekit.html and rebuild the bundles.");
                }
            }
            return _material;
        }

        private static void ApplyMaterial(GameObject go)
        {
            Material mat = CharacterMaterial();
            if (mat == null) return;

            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                int n = Mathf.Max(1, r.sharedMaterials.Length);
                var mats = new Material[n];
                for (int i = 0; i < n; i++) mats[i] = mat;
                r.sharedMaterials = mats;
            }
        }

        // ------------------------------------------------------------------ animation

        /// <summary>
        /// Plays a clip by its short name ("Idle", "Wave", "Death"...). Matches whole segments of
        /// Quaternius' pipe-separated clip names, so "Idle" never accidentally picks "Jump_Idle".
        /// Returns false if the character has no such clip.
        /// </summary>
        internal static bool Play(GameObject character, string shortName, bool loop = true, float fade = 0.25f)
        {
            if (character == null) return false;
            Animation anim = character.GetComponentInChildren<Animation>(true);
            if (anim == null) return false;

            string clip = FindClip(anim, shortName);
            if (clip == null) return false;

            AnimationState st = anim[clip];
            st.wrapMode = loop ? WrapMode.Loop : WrapMode.ClampForever;
            if (fade > 0f) anim.CrossFade(clip, fade);
            else anim.Play(clip);
            return true;
        }

        internal static bool HasClip(GameObject character, string shortName)
        {
            Animation anim = character != null ? character.GetComponentInChildren<Animation>(true) : null;
            return anim != null && FindClip(anim, shortName) != null;
        }

        private static string FindClip(Animation anim, string shortName)
        {
            foreach (AnimationState st in anim)
            {
                if (st == null || st.clip == null) continue;
                string full = st.name;
                if (string.Equals(full, shortName, StringComparison.OrdinalIgnoreCase)) return full;

                string[] parts = full.Split('|');
                foreach (string p in parts)
                    if (string.Equals(p, shortName, StringComparison.OrdinalIgnoreCase)) return full;
            }
            return null;
        }
    }
}
