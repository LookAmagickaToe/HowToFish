using System;
using BepInEx.Configuration;
using Expanded;
using UnityEngine;

namespace SeagullSwarm
{
    /// <summary>
    /// The seagull swarm encounter, wrapped as a module. All of its behaviour is host-side; the
    /// director itself is attached to BirdManager by a Harmony patch when a session starts.
    /// </summary>
    internal sealed class SwarmModule : ModuleBase
    {
        internal static SwarmConfig Cfg;

        internal override string Id => "Swarm";
        internal override string DisplayName => "Seagull Swarm";
        internal override KeyCode DefaultDebugKey => KeyCode.F2;
        internal override Type[] PatchTypes => new[] { typeof(Patches) };

        // What every player's swarm bar shows, as last sent by the host.
        private bool _hudActive;
        private int _hudWave, _hudWaves, _hudLeft;
        private float _hudSeconds, _hudReceivedAt;
        private bool _hudBreak;
        private GUIStyle _hudTitle, _hudLine;

        internal override void Configure(ConfigFile config)
        {
            Cfg = new SwarmConfig(config);
        }

        internal override void OnEnable()
        {
            ModNet.OnClient(Msg.SwarmStatus, r =>
            {
                _hudActive = r.ReadBoolean();
                _hudWave = r.ReadByte();
                _hudWaves = r.ReadByte();
                _hudLeft = r.ReadUInt16();
                _hudSeconds = r.ReadSingle();
                _hudBreak = r.ReadBoolean();
                _hudReceivedAt = Time.time;
            });
        }

        internal override void OnSessionEnd()
        {
            // The director lives on a game object destroyed with the scene; nothing to clean up here,
            // but make sure a stale static never leaks into the next session.
            SwarmDirector.Active = null;
            _hudActive = false;
        }

        /// <summary>The swarm bar at the top of the screen: wave, gulls left, time left.</summary>
        internal override void OnGUI()
        {
            // A host that stops talking (left, crashed) must not leave the bar up forever.
            if (!_hudActive || Time.time - _hudReceivedAt > 3f) return;

            if (_hudTitle == null)
            {
                _hudTitle = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                _hudTitle.normal.textColor = new Color(1f, 0.92f, 0.75f);
                _hudLine = new GUIStyle(GUI.skin.label) { fontSize = 17, alignment = TextAnchor.MiddleCenter };
                _hudLine.normal.textColor = Color.white;
            }

            float secs = Mathf.Max(0f, _hudSeconds - (Time.time - _hudReceivedAt));
            string clock = Mathf.FloorToInt(secs / 60f) + ":" + Mathf.FloorToInt(secs % 60f).ToString("00");

            const float w = 420f, h = 64f;
            var box = new Rect((Screen.width - w) * 0.5f, 18f, w, h);
            Expanded.Pirates.PirateModule.DrawRect(box, new Color(0.05f, 0.04f, 0.03f, 0.7f));
            GUI.Label(new Rect(box.x, box.y + 4f, w, 28f), "SEAGULL SWARM  -  WAVE " + _hudWave + " / " + _hudWaves, _hudTitle);

            string line;
            if (_hudBreak)
            {
                line = "Next wave in " + Mathf.CeilToInt(secs) + "s";
                _hudLine.normal.textColor = new Color(0.75f, 0.9f, 1f);
            }
            else
            {
                line = _hudLeft + " gull" + (_hudLeft == 1 ? "" : "s") + " left   |   " + clock;
                _hudLine.normal.textColor = secs < 15f ? new Color(1f, 0.45f, 0.35f) : Color.white;
            }
            GUI.Label(new Rect(box.x, box.y + 32f, w, 26f), line, _hudLine);
        }

        /// <summary>Debug key: start an encounter, or stop the running one.</summary>
        internal override void OnDebugKey()
        {
            SwarmDirector d = SwarmDirector.Active;
            if (d == null)
            {
                Diag.Warn("Swarm: no director (host an island first).");
                return;
            }
            d.DebugToggleEncounter();
        }

        internal override string StatusLine()
        {
            if (!IsEnabled) return "disabled";
            SwarmDirector d = SwarmDirector.Active;
            if (d == null) return "idle (not hosting)";
            return d.DebugStatus();
        }
    }
}
