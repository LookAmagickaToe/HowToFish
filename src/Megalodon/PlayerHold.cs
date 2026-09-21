using System;
using System.Reflection;
using Expanded.Pirates;
using HarmonyLib;
using UnityEngine;

namespace Expanded.Megalodon
{
    /// <summary>
    /// Takes the local player's body away from the game's walking code and places it wherever the
    /// mod says, every frame: on a wakeboard at the end of a tow rope, or inside a megalodon.
    ///
    /// Same trick the manned deck gun uses: the player's rigidbody goes kinematic, the game's Move
    /// is skipped, and the position is written before the camera places itself (so the view never
    /// lags a frame behind) and again after physics (so nothing drags the body elsewhere). The
    /// player's own client sends the position to everyone as usual, so no extra networking.
    /// </summary>
    internal static class PlayerHold
    {
        private static readonly FieldInfo FRig = AccessTools.Field(typeof(PlayerMovement), "_rig");
        private static readonly FieldInfo FPlayer = AccessTools.Field(typeof(PlayerMovement), "_player");
        private static readonly FieldInfo FOrigHeight = AccessTools.Field(typeof(PlayerMovement), "_origColHeight");

        private static Func<float, Vector3> _source;
        private static Vector3 _pose;
        private static int _frame = -1;
        private static string _who = "";

        internal static bool Active { get; private set; }

        /// <summary>The held pose (the player's body centre, not their feet).</summary>
        internal static Vector3 Pose => _pose;

        /// <summary>
        /// Starts holding. <paramref name="source"/> is called once per frame with the frame's delta
        /// time and returns where the body should be.
        /// </summary>
        internal static bool Begin(string who, Func<float, Vector3> source, Vector3 initial)
        {
            Player me = Player.LocalPlayer;
            if (me == null || source == null) return false;
            _source = source;
            _pose = initial;
            _frame = Time.frameCount;
            _who = who;
            Active = true;
            GunPatches.SetFrozen(me, true);
            Apply();
            Diag.Info("PlayerHold: " + who + " takes the body.");
            return true;
        }

        /// <summary>Hands the body back to the game, moving at <paramref name="velocity"/>.</summary>
        internal static void End(Vector3 velocity, string why)
        {
            if (!Active) return;
            Active = false;
            _source = null;
            Player me = Player.LocalPlayer;
            if (me != null)
            {
                GunPatches.SetFrozen(me, false);
                try
                {
                    PlayerMovement mv = me.Movement;
                    if (mv != null) mv.SetVel(velocity);
                }
                catch (Exception e) { Diag.Exception("PlayerHold.End", e); }
            }
            Diag.Info("PlayerHold: " + _who + " lets go (" + why + ").");
        }

        /// <summary>Advances the pose once per frame. Safe to call from several places.</summary>
        internal static void FrameUpdate()
        {
            if (!Active || _frame == Time.frameCount) return;
            _frame = Time.frameCount;
            try
            {
                _pose = _source(Mathf.Min(Time.deltaTime, 0.1f));
            }
            catch (Exception e)
            {
                Diag.Exception("PlayerHold source (" + _who + ")", e);
                End(Vector3.zero, "error");
                return;
            }
            Apply();
        }

        internal static void Apply()
        {
            if (!Active) return;
            Player me = Player.LocalPlayer;
            if (me == null) return;
            try
            {
                var rig = FRig?.GetValue(me.Movement) as Rigidbody;
                if (rig == null) return;
                if (!rig.isKinematic) { rig.linearVelocity = Vector3.zero; rig.isKinematic = true; }
                rig.position = _pose;
                rig.transform.position = _pose;
            }
            catch (Exception e)
            {
                Diag.Exception("PlayerHold.Apply", e);
            }
        }

        internal static bool IsLocal(PlayerMovement mv)
        {
            try { return FPlayer != null && ReferenceEquals(FPlayer.GetValue(mv), Player.LocalPlayer); }
            catch { return false; }
        }

        /// <summary>Distance from the body's centre down to the soles of the feet, standing.</summary>
        internal static float FeetOffset()
        {
            try
            {
                Player me = Player.LocalPlayer;
                if (me != null && FOrigHeight != null)
                {
                    float h = (float)FOrigHeight.GetValue(me.Movement);
                    if (h > 0.5f && h < 4f) return h * 0.5f;
                }
            }
            catch { }
            return 0.95f;
        }
    }
}
