using System;
using BepInEx.Configuration;
using Expanded.Content;
using FishNet;
using FishNet.Connection;
using UnityEngine;

namespace Expanded.Megalodon
{
    /// <summary>
    /// Wakeboarding, and the megalodon that comes for whoever is on the end of the rope.
    ///
    /// Buy a wakeboard and tow line in the shop; it leans by the helm. Grab it (E), have someone drive
    /// you far out, and a fin shows up behind you. From there: a three-phase fight against something
    /// eleven metres long, with a pistol, dynamite, the deck gun, barrel mines off the stern - and
    /// the driver's ability to not drive slowly.
    /// </summary>
    internal sealed class MegaModule : ModuleBase
    {
        internal static MegaModule Instance { get; private set; }
        internal static MegaConfig Cfg { get; private set; }

        private static float _topSpeed;

        /// <summary>
        /// Boat speed that keeps the megalodon at arm's length: the configured value, but never more
        /// than about 70% of what this boat has actually been seen doing. A starter motor must not
        /// doom its wakeboarder just for being a starter motor.
        /// </summary>
        internal static float SafeSpeed
        {
            get
            {
                float cfg = Cfg != null ? Cfg.SafeBoatSpeed.Value : 8f;
                return _topSpeed > 1f ? Mathf.Clamp(_topSpeed * 0.72f, 3.5f, cfg) : cfg;
            }
        }

        internal override string Id => "Megalodon";
        internal override string DisplayName => "Megalodon & Wakeboard";
        internal override KeyCode DefaultDebugKey => KeyCode.F9;
        internal override Type[] PatchTypes => new[] { typeof(MegaPatches) };

        private GameObject _late;

        internal override void Configure(ConfigFile config)
        {
            Instance = this;
            Cfg = new MegaConfig(config);
        }

        internal override void OnEnable()
        {
            Instance = this;
            WakeRig.RegisterHandlers();
            Shouts.RegisterHandlers();
            SharkBrain.RegisterHandlers();
            SharkVisual.RegisterHandlers();
            MegaHazards.RegisterHandlers();
            MegaBoat.RegisterHandlers();
            WakeShop.RegisterServerHandler();

            _late = new GameObject("ExpandedMegalodonLate");
            UnityEngine.Object.DontDestroyOnLoad(_late);
            _late.AddComponent<MegaLateDriver>();
        }

        internal override void OnSessionStart(bool asServer)
        {
            WakeRig.Reset();
            SharkBrain.Reset();
            SharkVisual.Reset();
            MegaHud.Reset();
            WakeShop.SessionStart();
            _topSpeed = 0f;
        }

        internal override void OnSessionEnd()
        {
            if (Wakeboard.Riding) Wakeboard.End("session ended", false);
            MegaStomach.Clear();
            PlayerHold.End(Vector3.zero, "session ended");
            SharkBrain.Reset();
            SharkVisual.Reset();
            MegaHazards.HostClear();
            MegaHazards.ClientClear();
            MegaBoat.Clear();
            WakeRig.ClearVisuals();
            WakeRig.Reset();
            WakeShop.Clear();
            Shouts.Clear();
            MegaFx.Clear();
        }

        internal override void Tick()
        {
            float dt = Time.deltaTime;

            if (IsServer)
            {
                WakeRig.HostTick();
                SharkBrain.HostTick(dt);
                MegaHazards.HostTick();
                MegaBoat.HostTick();
            }

            float speed = Tow.Speed();
            if (speed > _topSpeed && speed < 60f) _topSpeed = speed;

            PlayerHold.FrameUpdate();
            WakeShop.ClientTick();
            MegaStomach.Tick();
            MegaStomach.LateYell();
            MegaBoat.ClientTick();
            MegaFx.Tick();
            HandleKeys();
        }

        /// <summary>After everything has moved this frame: place the board, rope, megalodon and hazards.</summary>
        internal void LateTick()
        {
            if (!IsEnabled || !SessionActive) return;
            try
            {
                WakeRig.LateTick();
                SharkVisual.LateTick();
                MegaHazards.LateTick();
            }
            catch (Exception e)
            {
                Diag.Exception("Megalodon.LateTick", e);
            }
        }

        internal override void OnGUI()
        {
            MegaFx.OnGUI();
            MegaHud.OnGUI();
            Shouts.OnGUI();
        }

        // ------------------------------------------------------------------ keys

        private float _lastG;

        private void HandleKeys()
        {
            Player me = Player.LocalPlayer;
            if (me == null || ChatManager.IsTyping || me.BlockInputs) return;

            // Driver with a stalled engine: yank the cord.
            if (Boat.IsDrivingLocally && MegaBoat.SeenStalled && Input.GetKeyDown(KeyCode.Space))
            {
                ModNet.SendToServer(Msg.CordPull);
                MegaFx.Shake(300f, 1);
                try { AudioManager.PlayGlobalClip("Click", false, 1f, 0.02f, false); } catch { }
            }

            if (!Input.GetKeyDown(KeyCode.G) || Time.time - _lastG < 0.35f) return;
            _lastG = Time.time;

            if (Boat.IsDrivingLocally)
            {
                if (SharkVisual.Active) ModNet.SendToServer(Msg.DropMine);
                return;
            }

            if (Wakeboard.Riding)
            {
                float x = 0f;
                try { x = me.Movement.Input.x; } catch { }
                byte cmd = x < -0.3f ? (byte)1 : x > 0.3f ? (byte)2 : (byte)3;
                Shouts.Yell(cmd == 1 ? "LEFT!" : cmd == 2 ? "RIGHT!" : "FASTER!!!");
                if (MegaBoat.SeenAutopilot) ModNet.SendToServer(Msg.SoloCommand, w => w.Write(cmd));
                return;
            }

            if (SharkVisual.Active) Shouts.Yell(MegaLines.Pick(MegaLines.Deck));
        }

        // ------------------------------------------------------------------ debug

        internal override void OnDebugKey()
        {
            if (!InstanceFinder.IsServerStarted) { Diag.Warn("Megalodon: host-only."); return; }
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);

            if (ctrl)
            {
                // Test helper: the wakeboard for free.
                if (SharedState.Grant(MegalodonStory.UnlockWakeboard)) Announce("Debug: wakeboard unlocked.");
                return;
            }
            if (shift && SharkBrain.Active) { SharkBrain.DebugDamage(0.25f); return; }
            if (alt && SharkBrain.Active)
            {
                Player r = WakeRig.Rider;
                Vector3 pos = r != null ? r.Transform.position : Tow.Centre();
                var all = (Hazard[])Enum.GetValues(typeof(Hazard));
                MegaHazards.HostSpawn(all[UnityEngine.Random.Range(1, all.Length)], pos, Tow.Velocity());
                return;
            }
            SharkBrain.DebugToggle();
        }

        internal override string StatusLine()
        {
            if (!IsEnabled) return "disabled";
            string ride = WakeRig.RiderId >= 0 ? "rider on " + WakeRig.Tier : (WakeRig.Owned ? "board in the boat" : "no board");
            if (!SharkBrain.Active && !SharkVisual.Active) return ride + ", sea calm";
            return ride + ", megalodon " + SharkVisual.Mode + " " + SharkVisual.Hp.ToString("0") + "/" + SharkVisual.MaxHp.ToString("0") +
                   " (phase " + (int)SharkVisual.Phase + ")";
        }

        // ------------------------------------------------------------------ helpers

        internal static Player PlayerFor(NetworkConnection conn)
        {
            if (conn == null) return null;
            foreach (Player p in PlayerManager.Players)
                if (p != null && p.Owner == conn) return p;
            return null;
        }

        internal static Player PlayerByOwner(int owner)
        {
            if (owner < 0) return null;
            foreach (Player p in PlayerManager.Players)
                if (p != null && p.OwnerId == owner) return p;
            return null;
        }

        /// <summary>Host-side announcement in chat, shown once on every machine.</summary>
        internal static void Announce(string text)
        {
            Diag.Info("[sea] " + text);
            if (InstanceFinder.IsServerStarted) ModNet.SendToAll(Msg.EventBanner, w => w.Write("" + (text ?? "")));
        }
    }

    /// <summary>Runs the module's LateTick after every Update has moved things for the frame.</summary>
    internal sealed class MegaLateDriver : MonoBehaviour
    {
        private void LateUpdate() => MegaModule.Instance?.LateTick();
    }
}
