using System;
using System.Collections.Generic;
using Expanded.Npcs;
using Expanded.Pirates;
using FishNet.Connection;
using UnityEngine;

namespace Expanded.Megalodon
{
    /// <summary>
    /// People yelling: a speech bubble over their head and the same gurgling mumble the game's NPCs
    /// talk with, pitched per player so everyone sounds a bit different. Players' shouts go through
    /// the host so everyone sees them; props (a skeleton, Old Salt at the helm) talk locally.
    /// </summary>
    internal static class Shouts
    {
        private sealed class Bubble
        {
            public Transform Anchor;
            public Vector3 Offset;
            public string Text;
            public float Born;
            public float Until;
            public bool Panic;
        }

        private static readonly List<Bubble> Bubbles = new List<Bubble>();
        private static readonly Dictionary<Transform, NpcVoice> Voices = new Dictionary<Transform, NpcVoice>();
        private static readonly Dictionary<int, float> LastByClient = new Dictionary<int, float>();
        private static float _lastLocal;
        private static GUIStyle _style;

        private static bool Enabled => MegaModule.Cfg == null || MegaModule.Cfg.Shouts.Value;

        internal static void RegisterHandlers()
        {
            ModNet.OnServer(Msg.ShoutRequest, (conn, r) =>
            {
                string text = Clean(r.ReadString());
                Player p = MegaModule.PlayerFor(conn);
                if (p == null || text.Length == 0) return;
                float last;
                int id = conn != null ? conn.ClientId : -1;
                if (LastByClient.TryGetValue(id, out last) && Time.time - last < 0.5f) return;
                LastByClient[id] = Time.time;
                HostBroadcast(p, text);
            });

            ModNet.OnClient(Msg.Shout, r =>
            {
                int owner = r.ReadInt32();
                string text = r.ReadString();
                Player p = MegaModule.PlayerByOwner(owner);
                if (p != null) ShowPlayer(p, text);
            });
        }

        /// <summary>The local player yells. Rate-limited so panic doesn't flood the network.</summary>
        internal static void Yell(string text)
        {
            if (!Enabled || string.IsNullOrEmpty(text)) return;
            if (Time.time - _lastLocal < 0.6f) return;
            _lastLocal = Time.time;
            string t = Clean(text);
            ModNet.SendToServer(Msg.ShoutRequest, w => w.Write(t));
        }

        /// <summary>Host: make a player yell (e.g. everyone on deck when it jumps over the boat).</summary>
        internal static void HostBroadcast(Player p, string text)
        {
            if (p == null || !Enabled) return;
            int owner = p.OwnerId;
            string t = Clean(text);
            ModNet.SendToAll(Msg.Shout, w => { w.Write(owner); w.Write(t); });
        }

        /// <summary>A prop says something: bubble plus, optionally, a mumble from its own voice.</summary>
        internal static void Say(Transform anchor, Vector3 offset, string text, float seconds, NpcVoice voice = null)
        {
            if (anchor == null || string.IsNullOrEmpty(text)) return;
            Add(anchor, offset, text, seconds, false);
            try { voice?.Speak(text, 2.5f); } catch { }
        }

        private static void ShowPlayer(Player p, string text)
        {
            Transform t = p.Transform;
            if (t == null) return;
            bool panic = text.IndexOf('!') >= 0 || text.ToUpperInvariant() == text;
            Add(t, Vector3.up * 1.35f, text, Mathf.Clamp(1.6f + text.Length * 0.05f, 2f, 4.5f), panic);

            try
            {
                NpcVoice v = VoiceFor(p);
                if (v != null)
                {
                    v.Loudness = panic ? 1.6f : 1.2f;
                    v.Speak(text, 2.2f);
                }
            }
            catch (Exception e) { Diag.Debug("Shout voice: " + e.Message); }
        }

        private static NpcVoice VoiceFor(Player p)
        {
            Transform t = p.Transform;
            NpcVoice v;
            if (Voices.TryGetValue(t, out v) && v != null) return v;
            v = NpcVoice.Attach(t.gameObject);
            v.transform.localPosition = new Vector3(0f, 0.65f, 0f);
            // Stable per player: the same friend always sounds the same.
            int seed = (int)(p.SteamID % 997);
            v.Pitch = 0.85f + (seed % 40) / 100f;
            Voices[t] = v;
            return v;
        }

        private static void Add(Transform anchor, Vector3 offset, string text, float seconds, bool panic)
        {
            Bubbles.RemoveAll(b => b.Anchor == anchor);
            Bubbles.Add(new Bubble { Anchor = anchor, Offset = offset, Text = text, Born = Time.time, Until = Time.time + seconds, Panic = panic });
        }

        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("<", "").Replace(">", "").Trim();
            return s.Length > 90 ? s.Substring(0, 90) : s;
        }

        internal static void Clear()
        {
            Bubbles.Clear();
            foreach (NpcVoice v in Voices.Values) if (v != null) UnityEngine.Object.Destroy(v.gameObject);
            Voices.Clear();
            LastByClient.Clear();
        }

        internal static void OnGUI()
        {
            Bubbles.RemoveAll(b => b.Anchor == null || Time.time > b.Until);
            if (Bubbles.Count == 0) return;

            Camera cam = null;
            try { cam = GameInfo.CurCamera; } catch { }
            if (cam == null) return;

            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = true, fontStyle = FontStyle.Bold };
            }

            Transform self = Player.LocalPlayer != null ? Player.LocalPlayer.Transform : null;
            foreach (Bubble b in Bubbles)
            {
                Vector3 head = b.Anchor.position + b.Offset;
                bool mine = b.Anchor == self;
                Vector3 sp;
                if (mine)
                {
                    // Your own shouts float at the bottom of the screen, like subtitles for your panic.
                    sp = new Vector3(Screen.width * 0.5f, Screen.height * 0.3f, 1f);
                }
                else
                {
                    sp = cam.WorldToScreenPoint(head);
                    if (sp.z <= 0f || sp.z > 200f) continue;
                }

                float age = Time.time - b.Born;
                float pop = Mathf.Clamp01(age / 0.12f);
                float dist = mine ? 10f : sp.z;
                int size = Mathf.RoundToInt(Mathf.Lerp(b.Panic ? 26f : 21f, 13f, Mathf.InverseLerp(10f, 140f, dist)) * Mathf.Lerp(1.4f, 1f, pop));
                _style.fontSize = size;
                float w = Mathf.Lerp(360f, 220f, Mathf.InverseLerp(10f, 140f, dist));
                float h = _style.CalcHeight(new GUIContent(b.Text), w - 16f) + 12f;
                Vector2 jitter = b.Panic ? new Vector2(UnityEngine.Random.Range(-2.5f, 2.5f), UnityEngine.Random.Range(-2f, 2f)) : Vector2.zero;
                var r = new Rect(sp.x - w * 0.5f + jitter.x, Screen.height - sp.y - h + jitter.y, w, h);

                float a = Mathf.Clamp01((b.Until - Time.time) / 0.5f);
                PirateModule.DrawRect(new Rect(r.x - 2f, r.y - 2f, r.width + 4f, r.height + 4f), new Color(0.1f, 0.08f, 0.06f, 0.85f * a));
                PirateModule.DrawRect(r, b.Panic ? new Color(1f, 0.92f, 0.85f, 0.96f * a) : new Color(1f, 0.97f, 0.9f, 0.95f * a));
                _style.normal.textColor = b.Panic ? new Color(0.55f, 0.05f, 0.02f, a) : new Color(0.12f, 0.1f, 0.08f, a);
                GUI.Label(new Rect(r.x + 8f, r.y + 6f, r.width - 16f, r.height - 12f), b.Text, _style);
            }
        }
    }
}
