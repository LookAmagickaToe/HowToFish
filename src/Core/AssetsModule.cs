using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace Expanded
{
    /// <summary>
    /// Model previewer. Spawns one kit model at a time in front of the player and reports its real
    /// world size, which is the only reliable way to work out how to scale a kit authored for a
    /// different game. Purely local and visual: nothing is networked, nothing is persisted.
    /// </summary>
    internal sealed class AssetsModule : ModuleBase
    {
        internal override string Id => "Assets";
        internal override string DisplayName => "Model Preview";
        internal override KeyCode DefaultDebugKey => KeyCode.F5;

        private ConfigEntry<float> _distance;
        private ConfigEntry<string> _startModel;

        private readonly List<string> _models = new List<string>();
        private int _index = -1;
        private GameObject _current;

        internal override void Configure(ConfigFile config)
        {
            _distance = config.Bind(Id, "PreviewDistance", 8f,
                "How far in front of you preview models appear.");
            _startModel = config.Bind(Id, "StartModel", "ship-pirate-large",
                "First model shown. Press the debug key again to step through the rest.");
        }

        internal override void OnSessionEnd() => Clear();

        internal override void OnDebugKey()
        {
            if (!ModAssets.Available)
            {
                ModAssets.Load();
                if (!ModAssets.Available)
                {
                    Say("No model bundle installed. Run tools\\build-bundles.ps1 -Install.");
                    return;
                }
            }

            if (_models.Count == 0)
            {
                _models.AddRange(ModAssets.ModelNames());
                _models.Sort(StringComparer.Ordinal);
                int start = _models.IndexOf(_startModel.Value);
                _index = start >= 0 ? start - 1 : -1;
                Diag.Info("Assets: " + _models.Count + " model(s) available for preview.");
            }
            if (_models.Count == 0) { Say("Bundle contains no models."); return; }

            _index = (_index + 1) % _models.Count;
            Show(_models[_index]);
        }

        private void Show(string modelName)
        {
            Clear();

            Transform view = ViewTransform();
            if (view == null) { Say("No camera or player to place the model in front of."); return; }

            Vector3 forward = view.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            forward.Normalize();

            Vector3 pos = view.position + forward * _distance.Value;
            _current = ModAssets.Create(modelName, pos, Quaternion.LookRotation(-forward));
            if (_current == null) { Say("Could not load '" + modelName + "'."); return; }

            // Diagnostics that explain the two classic failures: no texture (missing UVs) and
            // walking through the model (no colliders).
            int uvs = 0, colliders = 0;
            foreach (MeshFilter mf in _current.GetComponentsInChildren<MeshFilter>(true))
                if (mf.sharedMesh != null) uvs += mf.sharedMesh.uv != null ? mf.sharedMesh.uv.Length : 0;
            foreach (Collider c in _current.GetComponentsInChildren<Collider>(true)) colliders++;
            Diag.Info("Preview mesh: " + uvs + " UV(s), " + colliders + " collider(s), layer " +
                      LayerMask.LayerToName(_current.layer));

            Bounds b = ModAssets.Measure(_current);
            string size = b.size.x.ToString("0.00") + " x " + b.size.y.ToString("0.00") + " x " +
                          b.size.z.ToString("0.00") + " m";

            Diag.Info("Preview " + (_index + 1) + "/" + _models.Count + ": " + modelName + "  size " + size +
                      "  pivot offset " + (b.center - pos).ToString("F2"));
            Say(modelName + " (" + (_index + 1) + "/" + _models.Count + ") - " + size);
        }

        private static Transform ViewTransform()
        {
            try
            {
                if (GameInfo.CurCamera != null) return GameInfo.CurCamera.transform;
            }
            catch { /* camera not ready */ }
            return Player.LocalPlayer != null ? Player.LocalPlayer.Transform : null;
        }

        private void Clear()
        {
            if (_current == null) return;
            UnityEngine.Object.Destroy(_current);
            _current = null;
        }

        private static void Say(string text)
        {
            Diag.Info("[assets] " + text);
            try { ChatManager.ChatMessage("<color=#C0C0C0>[Assets]</color> " + text); }
            catch { /* chat not ready */ }
        }

        internal override string StatusLine()
        {
            if (!IsEnabled) return "disabled";
            if (!ModAssets.Available) return "no bundle";
            string cur = _index >= 0 && _index < _models.Count ? _models[_index] : "none";
            return _models.Count + " models, showing " + cur;
        }
    }
}
