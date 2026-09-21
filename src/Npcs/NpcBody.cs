using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Expanded.Npcs
{
    /// <summary>
    /// The game's own NPC interactable, so a story character behaves exactly like the vanilla ones:
    /// the same localised "Talk [E]" prompt when you look at them, the same outline, the same key.
    /// Pressing it hands over to the module, which decides what is said.
    /// </summary>
    internal sealed class StoryNpcInteractable : NPCInteractable
    {
        internal string NpcId;
        internal static event Action<string> Talked;

        public override void Interact(Player player)
        {
            try
            {
                base.Interact(player);
                if (player != null && player == Player.LocalPlayer) Talked?.Invoke(NpcId);
            }
            catch (Exception e)
            {
                Diag.Exception("StoryNpcInteractable.Interact", e);
            }
        }
    }

    /// <summary>
    /// Builds the parts of a story character the game interacts with: the talk collider the look-at
    /// system finds, the anchor the speech bubble follows, and the mouth that items fly into.
    /// </summary>
    internal static class NpcBody
    {
        // Filled in the editor for vanilla interactables; must be set before Awake, so the object
        // is built inactive and switched on afterwards.
        private static readonly FieldInfo InteractCol = AccessTools.Field(typeof(Interactable), "_interactCol");
        private static readonly FieldInfo TextTargetField = AccessTools.Field(typeof(Interactable), "_textTarget");
        private static readonly FieldInfo Outline = AccessTools.Field(typeof(Interactable), "_modelsToOutline");

        /// <summary>Adds the talk point. Returns the speech-bubble anchor, or null if the game changed.</summary>
        /// <summary>How tall the character stands, from its meshes; the Quaternius figures are short and stocky.</summary>
        internal static float HeightOf(GameObject character)
        {
            float top = float.MinValue;
            foreach (Renderer r in character.GetComponentsInChildren<Renderer>(true))
                if (r != null) top = Mathf.Max(top, r.bounds.max.y);
            float h = top - character.transform.position.y;
            return h > 0.5f && h < 4f ? h : 1.6f;
        }

        internal static Transform AddTalkPoint(GameObject character, string npcId, float height, out StoryNpcInteractable talk)
        {
            talk = null;

            // Just above the head, like the game's own speech bubbles.
            var bubble = new GameObject("SpeechAnchor").transform;
            bubble.SetParent(character.transform, false);
            bubble.position = character.transform.position + Vector3.up * (height + 0.3f);

            if (InteractCol == null || TextTargetField == null || Outline == null)
            {
                Diag.Error("NpcBody: the game's Interactable fields changed; story characters cannot be talked to.");
                return bubble;
            }

            try
            {
                // The component sits at chest height: the game checks line of sight from the camera to
                // the interactable's position, and a point at the feet would be hidden by the ground.
                var point = new GameObject("TalkPoint");
                point.SetActive(false);
                point.transform.SetParent(character.transform, false);
                point.transform.position = character.transform.position + Vector3.up * (height * 0.55f);
                point.layer = LayerMask.NameToLayer("Interactable");
                point.tag = "Interactable";

                CapsuleCollider col = point.AddComponent<CapsuleCollider>();
                col.radius = 0.4f;
                col.height = Mathf.Max(1f, height);
                col.center = Vector3.zero;

                talk = point.AddComponent<StoryNpcInteractable>();
                talk.NpcId = npcId;
                InteractCol.SetValue(talk, col);
                TextTargetField.SetValue(talk, bubble);
                Outline.SetValue(talk, OutlineTargets(character));

                point.SetActive(true);
            }
            catch (Exception e)
            {
                Diag.Exception("NpcBody.AddTalkPoint", e);
            }
            return bubble;
        }

        private static GameObject[] OutlineTargets(GameObject character)
        {
            var list = new List<GameObject>();
            foreach (Renderer r in character.GetComponentsInChildren<Renderer>(true))
                if (r != null && !list.Contains(r.gameObject)) list.Add(r.gameObject);
            return list.ToArray();
        }

        internal static Vector3 MouthOf(GameObject character, float height) =>
            character.transform.position + Vector3.up * (height * 0.8f) + character.transform.forward * 0.15f;

        // The game hides its speech bubble after a fixed time read from NpcUI when a line is shown.
        // Story lines are longer than the game's, so they get more time: the value is raised just
        // for our call and put back straight after, leaving vanilla NPCs untouched.
        private static readonly FieldInfo PlayerUIInstance = AccessTools.Field(typeof(PlayerUI), "_instance");
        private static readonly FieldInfo PlayerUINpcUI = AccessTools.Field(typeof(PlayerUI), "_npcUI");
        private static readonly FieldInfo NpcUIShowTime = AccessTools.Field(typeof(NpcUI), "_showNpcTextTime");

        /// <summary>Shows a line in the game's speech bubble for the given number of seconds.</summary>
        internal static void ShowBubble(string text, Transform target, float seconds)
        {
            object npcUI = null;
            object before = null;
            try
            {
                object ui = PlayerUIInstance?.GetValue(null);
                npcUI = ui != null ? PlayerUINpcUI?.GetValue(ui) : null;
                if (npcUI != null && NpcUIShowTime != null && seconds > 0f)
                {
                    before = NpcUIShowTime.GetValue(npcUI);
                    NpcUIShowTime.SetValue(npcUI, seconds);
                }
            }
            catch (Exception e)
            {
                Diag.Debug("NpcBody: could not lengthen the speech bubble (" + e.Message + ").");
            }

            try
            {
                PlayerUI.SetNpcText(text, target);
            }
            finally
            {
                if (npcUI != null && before != null)
                {
                    try { NpcUIShowTime.SetValue(npcUI, before); } catch { }
                }
            }
        }
    }

    /// <summary>
    /// Items fed to a vanilla NPC are destroyed with that NPC's id and fly into its mouth. Story
    /// characters use ids the game has no NPC for, which the game would fail to look up on every
    /// client; this catches those ids and plays the same animation towards our character instead.
    /// </summary>
    internal static class NpcBodyPatches
    {
        private static readonly MethodInfo DespawnOnServer = AccessTools.Method(typeof(Item), "DespawnItemOnServer");

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Item), "DestroyByNpc")]
        private static bool Item_DestroyByNpc(Item __instance, byte npcID)
        {
            try
            {
                Vector3 mouth;
                if (!NpcModule.TryMouthForGameId(npcID, out mouth)) return true;   // a vanilla NPC

                if (__instance.RigidbodySync != null) __instance.RigidbodySync.SetKinematic(true);
                ItemExtraRigidbody[] extra = __instance.ExtraRigs;
                if (extra != null)
                    foreach (ItemExtraRigidbody x in extra)
                        if (x != null && x.Rig != null) x.Rig.isKinematic = true;

                EatenBy.Start(__instance, mouth);
                return false;
            }
            catch (Exception e)
            {
                Diag.Exception("Patch Item.DestroyByNpc (story NPC)", e);
                return true;
            }
        }

        /// <summary>The vanilla shrink-into-the-mouth animation, without depending on LeanTween.</summary>
        private sealed class EatenBy : MonoBehaviour
        {
            private const float Duration = 0.25f;
            private Item _item;
            private Vector3 _from, _to, _scale;
            private float _t;

            internal static void Start(Item item, Vector3 mouth)
            {
                var anim = item.gameObject.AddComponent<EatenBy>();
                anim._item = item;
                anim._from = item.transform.position;
                anim._to = mouth;
                anim._scale = item.transform.localScale;
            }

            private void Update()
            {
                _t += Time.deltaTime / Duration;
                float k = 1f - (1f - Mathf.Clamp01(_t)) * (1f - Mathf.Clamp01(_t));   // ease-out quad
                transform.position = Vector3.Lerp(_from, _to, k);
                transform.localScale = Vector3.Lerp(_scale, Vector3.zero, k);
                if (_t < 1f) return;

                try { DespawnOnServer?.Invoke(_item, null); }
                catch (Exception e) { Diag.Exception("Story NPC despawn eaten item", e); }
                Destroy(this);   // items may be pooled; leave nothing behind
            }
        }
    }
}
