using System;
using System.Collections.Generic;
using Expanded.Npcs;
using Expanded.Pirates;
using UnityEngine;

namespace Expanded.Megalodon
{
    /// <summary>
    /// Local, cosmetic effects: the dun-dun and the heartbeat (synthesised, no audio files), screen
    /// flashes, the big "KNAPP." banner, the camera's zoom punch on a near miss, fog banks, a flying
    /// fish stuck to your face, and slime when you get spat out.
    /// </summary>
    internal static class MegaFx
    {
        // ------------------------------------------------------------------ audio

        private static AudioClip _dun, _dunHigh, _lub, _dub, _boom;
        private static AudioSource _music;
        private static float _nextDun;
        private static bool _dunHighNext;
        private static float _nextBeat;
        private static bool _dubNext;

        /// <summary>0 = silent, 1 = jaws at your heels. Drives the tempo of both the dun-dun and the heart.</summary>
        internal static float Threat;
        /// <summary>True while the megalodon hides (the fake-out): the music stops dead. That's the scary bit.</summary>
        internal static bool Hush;
        internal static bool FightOn;

        private static AudioClip Synth(string name, float freq, float seconds, float saw, float attack, float decay)
        {
            const int rate = 44100;
            int n = Mathf.CeilToInt(rate * seconds);
            var data = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)rate;
                float env = Mathf.Clamp01(t / attack) * Mathf.Exp(-t / decay);
                float ph = t * freq;
                float sine = Mathf.Sin(2f * Mathf.PI * ph);
                float sub = Mathf.Sin(Mathf.PI * ph);                 // an octave below, for weight
                float sw = 2f * (ph - Mathf.Floor(ph + 0.5f));      // sawtooth for the bite of a double bass
                float v = sine * 0.55f + sub * 0.35f + sw * saw;
                data[i] = Mathf.Clamp(v * env * 0.8f, -1f, 1f);
            }
            AudioClip c = AudioClip.Create(name, n, 1, rate, false);
            c.SetData(data, 0);
            return c;
        }

        private static void EnsureAudio()
        {
            if (_music != null) return;
            _dun = Synth("dun", 82.41f, 0.9f, 0.25f, 0.015f, 0.35f);       // E2
            _dunHigh = Synth("dun2", 87.31f, 0.6f, 0.25f, 0.012f, 0.22f);  // F2
            _lub = Synth("lub", 52f, 0.25f, 0f, 0.005f, 0.07f);
            _dub = Synth("dub", 44f, 0.22f, 0f, 0.005f, 0.06f);
            _boom = Synth("boom", 38f, 1.6f, 0.1f, 0.01f, 0.6f);

            var go = new GameObject("ExpandedMegaMusic");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _music = go.AddComponent<AudioSource>();
            _music.spatialBlend = 0f;
            _music.playOnAwake = false;
            try { _music.outputAudioMixerGroup = NpcVoice.MixerGroup; } catch { }
        }

        private static float Volume => MegaModule.Cfg != null ? Mathf.Clamp01(MegaModule.Cfg.MusicVolume.Value) : 0.5f;
        private static bool MusicOn => MegaModule.Cfg == null || MegaModule.Cfg.Music.Value;

        /// <summary>One deep hit, for the fin's first appearance and for its death.</summary>
        internal static void Boom(float volume = 1f)
        {
            if (!MusicOn) return;
            EnsureAudio();
            _music.PlayOneShot(_boom, Volume * volume * 1.3f);
        }

        private static void TickMusic()
        {
            if (!MusicOn || !FightOn || Hush) return;
            EnsureAudio();
            float now = Time.time;
            float threat = Mathf.Clamp01(Threat);

            // Duuun... dun. Duuun... dun. Faster and faster as it closes.
            if (now >= _nextDun)
            {
                AudioClip c = _dunHighNext ? _dunHigh : _dun;
                _music.PlayOneShot(c, Volume * Mathf.Lerp(0.55f, 1f, threat));
                float gap = Mathf.Lerp(0.95f, 0.2f, threat);
                _nextDun = now + (_dunHighNext ? gap : gap * 0.55f);
                _dunHighNext = !_dunHighNext;
            }

            // The heart joins in when it gets close.
            if (threat > 0.55f && now >= _nextBeat)
            {
                _music.PlayOneShot(_dubNext ? _dub : _lub, Volume * 1.2f);
                float bpm = Mathf.Lerp(80f, 170f, (threat - 0.55f) / 0.45f);
                _nextBeat = now + (_dubNext ? 60f / bpm - 0.16f : 0.16f);
                _dubNext = !_dubNext;
            }
        }

        // ------------------------------------------------------------------ overlays

        private sealed class Flash { public Color Color; public float Start; public float End; }
        private static readonly List<Flash> Flashes = new List<Flash>();
        private static string _banner;
        private static Color _bannerColor;
        private static float _bannerStart, _bannerEnd;
        private static float _slimeUntil;
        private static GUIStyle _bannerStyle;

        internal static void FlashScreen(Color c, float seconds)
        {
            Flashes.Add(new Flash { Color = c, Start = Time.time, End = Time.time + Mathf.Max(0.05f, seconds) });
        }

        internal static void Banner(string text, Color color, float seconds)
        {
            _banner = text;
            _bannerColor = color;
            _bannerStart = Time.time;
            _bannerEnd = Time.time + seconds;
        }

        internal static void Slime(float seconds) => _slimeUntil = Time.time + seconds;

        // ------------------------------------------------------------------ camera

        private static float _fovKick;
        private static float _fovKickStart, _fovKickEnd;

        /// <summary>Added to the camera's field of view (negative = zoom in). Read by the SetFov hook.</summary>
        internal static float FovOffset
        {
            get
            {
                if (Time.time >= _fovKickEnd) return 0f;
                float t = Mathf.InverseLerp(_fovKickStart, _fovKickEnd, Time.time);
                return _fovKick * (1f - t) * (1f - t);
            }
        }

        /// <summary>Zoom punch: the "slow motion" of a near miss, without touching time itself.</summary>
        internal static void Punch(float degrees, float seconds)
        {
            _fovKick = degrees;
            _fovKickStart = Time.time;
            _fovKickEnd = Time.time + seconds;
        }

        internal static void Shake(float force, int reps)
        {
            try { Player.LocalPlayer?.ScreenShake?.Shake(force, reps); } catch { }
        }

        // ------------------------------------------------------------------ fog

        private static float _fogStart, _fogUntil, _fogBase = -1f, _fogSet = -1f;
        private static Color _fogColorBase;

        internal static void FogBank(float seconds)
        {
            if (Time.time >= _fogUntil) _fogStart = Time.time;
            _fogUntil = Time.time + seconds;
        }

        private static void TickFog()
        {
            bool want = Time.time < _fogUntil && (MegaModule.Cfg == null || MegaModule.Cfg.FogBanks.Value);
            if (want)
            {
                if (_fogBase < 0f)
                {
                    _fogBase = RenderSettings.fogDensity;
                    _fogColorBase = RenderSettings.fogColor;
                }
                float remain = _fogUntil - Time.time;
                float k = Mathf.Clamp01(Mathf.Min(Time.time - _fogStart, remain) / 3f);
                float target = Mathf.Lerp(_fogBase, Mathf.Max(_fogBase * 7f, 0.035f), k);
                RenderSettings.fogDensity = target;
                RenderSettings.fogColor = Color.Lerp(_fogColorBase, new Color(0.72f, 0.76f, 0.8f), k);
                _fogSet = target;
            }
            else if (_fogBase >= 0f)
            {
                // Put it back only if nothing else (underwater, sunset) has changed it meanwhile.
                if (Mathf.Approximately(RenderSettings.fogDensity, _fogSet))
                {
                    RenderSettings.fogDensity = _fogBase;
                    RenderSettings.fogColor = _fogColorBase;
                }
                _fogBase = -1f;
            }
        }

        // ------------------------------------------------------------------ fish on face

        private static Transform _faceFish;
        private static float _faceFishUntil;

        internal static void FishOnFace(float seconds)
        {
            Camera cam = null;
            try { cam = GameInfo.CurCamera; } catch { }
            if (cam == null) return;
            if (_faceFish == null)
            {
                _faceFish = MegaShapes.FlyingFish(cam.transform);
                _faceFish.name = "FaceFish";
                _faceFish.localScale = Vector3.one * 1.4f;
            }
            _faceFish.SetParent(cam.transform, false);
            _faceFishUntil = Time.time + seconds;
        }

        private static void TickFaceFish()
        {
            if (_faceFish == null) return;
            if (Time.time >= _faceFishUntil)
            {
                UnityEngine.Object.Destroy(_faceFish.gameObject);
                _faceFish = null;
                return;
            }
            float w = Mathf.Sin(Time.time * 38f);
            _faceFish.localPosition = new Vector3(0.06f + w * 0.01f, -0.04f, 0.3f);
            _faceFish.localRotation = Quaternion.Euler(10f, 70f + w * 22f, 15f + w * 10f);
        }

        // ------------------------------------------------------------------ loop

        internal static void Tick()
        {
            TickMusic();
            TickFog();
            TickFaceFish();
        }

        internal static void OnGUI()
        {
            float now = Time.time;
            Flashes.RemoveAll(f => now >= f.End);
            foreach (Flash f in Flashes)
            {
                float a = 1f - Mathf.InverseLerp(f.Start, f.End, now);
                Color c = f.Color; c.a *= a;
                PirateModule.DrawRect(new Rect(0, 0, Screen.width, Screen.height), c);
            }

            if (now < _slimeUntil)
            {
                float a = Mathf.Clamp01((_slimeUntil - now) / 4f) * 0.45f;
                PirateModule.DrawRect(new Rect(0, 0, Screen.width, Screen.height), new Color(0.45f, 0.7f, 0.15f, a));
                // Drips.
                for (int i = 0; i < 9; i++)
                {
                    float x = (i * 0.113f + 0.05f) % 1f * Screen.width;
                    float len = (Mathf.Sin(i * 3.1f + now * 0.7f) * 0.5f + 0.5f) * Screen.height * 0.25f + 40f;
                    PirateModule.DrawRect(new Rect(x, 0, 14f + i % 3 * 6f, len), new Color(0.35f, 0.6f, 0.1f, a * 1.3f));
                }
            }

            if (_banner != null && now < _bannerEnd)
            {
                if (_bannerStyle == null)
                    _bannerStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, wordWrap = true };
                float age = now - _bannerStart;
                float pop = age < 0.15f ? Mathf.Lerp(2.2f, 1f, age / 0.15f) : 1f;
                float a = Mathf.Clamp01((_bannerEnd - now) / 0.4f);
                _bannerStyle.fontSize = Mathf.RoundToInt(64f * pop * Mathf.Clamp(Screen.height / 1080f, 0.6f, 1.4f));
                var r = new Rect(0f, Screen.height * 0.22f, Screen.width, 160f);
                _bannerStyle.normal.textColor = new Color(0f, 0f, 0f, 0.7f * a);
                GUI.Label(new Rect(r.x + 4f, r.y + 4f, r.width, r.height), _banner, _bannerStyle);
                Color c = _bannerColor; c.a *= a;
                _bannerStyle.normal.textColor = c;
                GUI.Label(r, _banner, _bannerStyle);
            }
        }

        internal static void Clear()
        {
            Flashes.Clear();
            _banner = null;
            _slimeUntil = 0f;
            _fogUntil = 0f;
            TickFog();
            _faceFishUntil = 0f;
            TickFaceFish();
            FightOn = false;
            Hush = false;
            Threat = 0f;
        }
    }
}
