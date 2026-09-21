using System.Reflection;
using UnityEngine;

namespace Expanded.Npcs
{
    /// <summary>
    /// Gives story characters the same gurgling mumble the game's own NPCs make when they talk.
    ///
    /// The game's NPCs keep a looping "mouth" clip on an AudioSource and fade it in for a moment
    /// with every line. We borrow that clip - and the source's mixer group and 3D settings, so it
    /// sits in the mix exactly like theirs - from any vanilla NPC, and do the same fade here.
    /// </summary>
    internal sealed class NpcVoice : MonoBehaviour
    {
        private static readonly FieldInfo MouthSourceField = HarmonyLib.AccessTools.Field(typeof(NPC), "_mouthSource");
        private static readonly FieldInfo MouthVolField = HarmonyLib.AccessTools.Field(typeof(NPC), "_mouthVol");

        private static AudioSource _template;
        private static float _templateVolume = 0.75f;
        private static bool _warned;

        private const float Fade = 0.1f;

        internal static float VolumeScale = 1f;

        /// <summary>Per-speaker loudness on top of the global scale (players shouting are louder).</summary>
        internal float Loudness = 1f;

        /// <summary>Per-speaker pitch on top of the borrowed clip's own (so two players don't sound alike).</summary>
        internal float Pitch = 1f;

        private AudioSource _source;
        private float _speakUntil;
        private float _basePitch = 1f;

        /// <summary>The mixer group vanilla NPC voices play through, or null if none has been found yet.</summary>
        internal static UnityEngine.Audio.AudioMixerGroup MixerGroup => FindTemplate()?.outputAudioMixerGroup;

        /// <summary>Adds a voice at head height. Harmless if no vanilla voice can be found.</summary>
        internal static NpcVoice Attach(GameObject character)
        {
            var mouth = new GameObject("Voice");
            mouth.transform.SetParent(character.transform, false);
            mouth.transform.localPosition = new Vector3(0f, 1.6f, 0f);
            return mouth.AddComponent<NpcVoice>();
        }

        /// <summary>Mumble for a stretch that grows a little with the length of the line.</summary>
        internal void Speak(string text) => Speak(text, 1.4f);

        /// <summary>As <see cref="Speak(string)"/>, with a longer cap for shouting whole sentences.</summary>
        internal void Speak(string text, float maxSeconds)
        {
            if (!EnsureSource()) return;
            int len = string.IsNullOrEmpty(text) ? 0 : text.Length;
            float seconds = Mathf.Clamp(0.45f + len * 0.008f, 0.45f, Mathf.Max(0.45f, maxSeconds));
            _speakUntil = Time.time + seconds;
            _source.pitch = _basePitch * Pitch;

            if (!_source.isPlaying)
            {
                // Start somewhere random in the loop, as the game does, so lines don't all sound alike.
                if (_source.clip.length > 0f) _source.time = Random.Range(0f, _source.clip.length);
                _source.Play();
            }
        }

        private void Update()
        {
            if (_source == null) return;

            float target = Time.time < _speakUntil ? _templateVolume * VolumeScale * Loudness : 0f;
            _source.volume = Mathf.MoveTowards(_source.volume, target, Time.deltaTime / Fade * Mathf.Max(0.01f, _templateVolume));
            if (target <= 0f && _source.volume <= 0f && _source.isPlaying) _source.Stop();
        }

        private bool EnsureSource()
        {
            if (_source != null) return true;
            AudioSource template = FindTemplate();
            if (template == null) return false;

            _source = gameObject.AddComponent<AudioSource>();
            _source.clip = template.clip;
            _source.outputAudioMixerGroup = template.outputAudioMixerGroup;
            _source.spatialBlend = template.spatialBlend;
            _source.rolloffMode = template.rolloffMode;
            if (template.rolloffMode == AudioRolloffMode.Custom)
                _source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, template.GetCustomCurve(AudioSourceCurveType.CustomRolloff));
            _source.minDistance = template.minDistance;
            _source.maxDistance = template.maxDistance;
            _source.dopplerLevel = template.dopplerLevel;
            _source.spread = template.spread;
            _basePitch = template.pitch;
            _source.pitch = _basePitch * Pitch;
            _source.priority = template.priority;
            _source.loop = true;
            _source.playOnAwake = false;
            _source.volume = 0f;
            return true;
        }

        /// <summary>Finds a vanilla NPC's mouth source once; NPC prefabs count too, so any island works.</summary>
        private static AudioSource FindTemplate()
        {
            if (_template != null) return _template;
            if (MouthSourceField == null) return null;

            foreach (NPC npc in Resources.FindObjectsOfTypeAll<NPC>())
            {
                var src = MouthSourceField.GetValue(npc) as AudioSource;
                if (src == null || src.clip == null) continue;
                _template = src;
                if (MouthVolField != null) _templateVolume = (float)MouthVolField.GetValue(npc);
                Diag.Info("NpcVoice: borrowing '" + src.clip.name + "' from vanilla NPC '" + npc.name + "' (volume " + _templateVolume.ToString("0.00") + ").");
                return _template;
            }

            if (!_warned)
            {
                _warned = true;
                Diag.Warn("NpcVoice: no vanilla NPC voice found yet; story characters talk silently for now.");
            }
            return null;
        }
    }
}
