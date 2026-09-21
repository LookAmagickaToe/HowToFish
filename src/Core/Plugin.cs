using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Expanded
{
    /// <summary>
    /// How to Fish: Expanded. Hosts a set of independent modules (seagull swarm, quests, pirates,
    /// sea life) over shared plumbing: logging, custom networking, a mod-only save file, and a
    /// debug overlay with per-module hotkeys.
    /// </summary>
    [BepInPlugin(Guid, "How to Fish: Expanded", Version)]
    public class ExpandedPlugin : BaseUnityPlugin
    {
        public const string Guid = "dazed.howtofish.expanded";
        public const string Version = "0.2.0";

        internal static ExpandedPlugin Instance { get; private set; }
        internal static ManualLogSource Log { get; private set; }

        private static readonly List<ModuleBase> Modules = new List<ModuleBase>();
        internal static IReadOnlyList<ModuleBase> AllModules => Modules;

        private ConfigEntry<bool> _verbose;
        private ConfigEntry<float> _statusInterval;
        private ConfigEntry<KeyCode> _overlayKey;
        private ConfigEntry<bool> _overlayEnabled;

        private Harmony _harmony;
        private bool _overlayVisible;
        private bool _sessionRunning;
        private bool _helloSent;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            _verbose = Config.Bind("Debug", "Verbose", false,
                "Log every state change from every module. Noisy but invaluable when something misbehaves.");
            _statusInterval = Config.Bind("Debug", "StatusIntervalSeconds", 5f,
                "How often modules write a status snapshot to the log during an encounter. 0 = off.");
            _overlayEnabled = Config.Bind("Debug", "OverlayEnabled", true,
                "Allow the on-screen debug overlay (listing modules and their hotkeys).");
            _overlayKey = Config.Bind("Debug", "OverlayKey", KeyCode.F1,
                "Toggles the debug overlay.");

            Diag.Init(Path.Combine(Paths.BepInExRootPath, "Expanded.log"));
            Diag.VerboseEnabled = _verbose.Value;
            Diag.StatusInterval = _statusInterval.Value;

            Diag.Info("How to Fish: Expanded " + Version + " on Unity " + Application.unityVersion);
            Diag.Info("Log file: " + Diag.FilePath);

            ModSave.Load();
            ModNet.InstallSerializers();
            SharedState.RegisterHandlers();
            ModAssets.Load();

            RegisterModules();
            ConfigureModules();
            PatchModules();

            Diag.Info("Ready. " + _overlayKey.Value + " shows the module overlay.");
        }

        /// <summary>Module order defines overlay order. Add new modules here.</summary>
        private void RegisterModules()
        {
            Modules.Add(new SeagullSwarm.SwarmModule());
            Modules.Add(new Expanded.Quests.QuestModule());
            Modules.Add(new Expanded.Pirates.PirateModule());
            Modules.Add(new Expanded.Npcs.NpcModule());
            Modules.Add(new AssetsModule());
            Modules.Add(new ArmoryModule());
        }

        private void ConfigureModules()
        {
            foreach (ModuleBase m in Modules)
            {
                try
                {
                    m.EnabledEntry = Config.Bind(m.Id, "Enabled", true, "Enable the " + m.DisplayName + " module.");
                    if (m.DefaultDebugKey != KeyCode.None)
                        m.DebugKeyEntry = Config.Bind(m.Id, "DebugKey", m.DefaultDebugKey,
                            "Debug hotkey for " + m.DisplayName + " (host only for host-side actions).");

                    m.Configure(Config);
                    if (m.IsEnabled) m.OnEnable();

                    Diag.Info("  module " + m.Id + ": " + (m.IsEnabled ? "enabled" : "DISABLED") +
                              (m.DebugKeyEntry != null ? ", key " + m.DebugKeyEntry.Value : ""));
                }
                catch (Exception e)
                {
                    Diag.Exception("Configuring module " + m.Id, e);
                }
            }
        }

        private void PatchModules()
        {
            _harmony = new Harmony(Guid);
            int expected = 0, applied = 0;

            foreach (ModuleBase m in Modules)
            {
                if (!m.IsEnabled) continue;
                foreach (Type t in m.PatchTypes)
                {
                    expected += CountPatchMethods(t);
                    try
                    {
                        _harmony.PatchAll(t);
                    }
                    catch (Exception e)
                    {
                        Diag.Exception("Harmony.PatchAll(" + t.Name + ")", e);
                    }
                }
            }

            foreach (MethodBase mb in _harmony.GetPatchedMethods())
            {
                Diag.Info("  hooked " + mb.DeclaringType?.Name + "." + mb.Name);
                applied++;
            }

            if (applied == expected)
                Diag.Info("All " + applied + " game hooks attached.");
            else
                Diag.Error("Only " + applied + " of " + expected + " game hooks attached - some features " +
                           "will not work. A game update may have changed the methods the mod hooks.");
        }

        /// <summary>Counts distinct methods a patch container targets, so we can verify they all landed.</summary>
        private static int CountPatchMethods(Type container)
        {
            var targets = new HashSet<string>();
            foreach (MethodInfo mi in container.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                object[] attrs = mi.GetCustomAttributes(typeof(HarmonyPatch), true);
                foreach (HarmonyPatch p in attrs)
                {
                    if (p.info?.declaringType == null) continue;
                    targets.Add(p.info.declaringType.FullName + "." + p.info.methodName);
                }
            }
            return targets.Count;
        }

        // ------------------------------------------------------------------ loop

        private void Update()
        {
            try
            {
                UpdateSessionState();
                HandleHotkeys();

                if (_sessionRunning)
                {
                    for (int i = 0; i < Modules.Count; i++)
                    {
                        ModuleBase m = Modules[i];
                        if (!m.IsEnabled || !m.SessionActive) continue;
                        try { m.Tick(); }
                        catch (Exception e) { Diag.Exception("Module " + m.Id + ".Tick", e); }
                    }
                }

                ModSave.Tick();
            }
            catch (Exception e)
            {
                Diag.Exception("ExpandedPlugin.Update", e);
            }
        }

        /// <summary>
        /// Detects session start/stop by polling FishNet rather than patching more game methods:
        /// fewer hooks means fewer things a game update can break.
        /// </summary>
        private void UpdateSessionState()
        {
            bool client = FishNet.InstanceFinder.IsClientStarted;
            bool server = FishNet.InstanceFinder.IsServerStarted;
            bool running = client || server;

            // Hooking is checked every frame, not only when the session starts: a host starts its
            // server first and its own client a frame or more later. Hooking only at session start
            // left the host's client deaf to every mod broadcast - no NPCs, no health bar, no
            // cannonballs on the host's own screen. Both calls are idempotent.
            if (server) ModNet.HookServer();
            if (client)
            {
                ModNet.HookClient();

                // Every client - the host's own included - asks the host for everything it missed:
                // unlocks, journal, NPCs, any fight in progress. The host needs this too: its server
                // broadcasts the opening state before its own client is authenticated, and FishNet
                // silently drops broadcasts to unauthenticated connections (Old Salt never appeared).
                // Once per session, as soon as the connection is authenticated.
                FishNet.Connection.NetworkConnection conn = FishNet.InstanceFinder.ClientManager?.Connection;
                if (!_helloSent && conn != null && conn.IsAuthenticated)
                {
                    _helloSent = true;
                    ModNet.SendToServer(Msg.Hello);
                    Diag.Info("ModNet: client authenticated, requested state from host.");
                }
            }

            if (running == _sessionRunning) return;
            _sessionRunning = running;

            if (running)
            {
                Diag.Info("Session started (" + (server ? "host" : "client") + ").");

                // Steam is up by now, so the save file can move to the real per-account path before
                // any module reads progress from it.
                ModSave.RebindToCurrentUser();

                if (server) SharedState.LoadFromSave();

                foreach (ModuleBase m in Modules)
                {
                    if (!m.IsEnabled) continue;
                    try { m.BeginSession(server); }
                    catch (Exception e) { Diag.Exception("Module " + m.Id + ".OnSessionStart", e); }
                }
            }
            else
            {
                Diag.Info("Session ended.");
                foreach (ModuleBase m in Modules)
                {
                    try { m.FinishSession(); }
                    catch (Exception e) { Diag.Exception("Module " + m.Id + ".OnSessionEnd", e); }
                }
                ModNet.Unhook();
                _helloSent = false;
                SharedState.Clear();
                ModSave.SaveIfDirty(true);
            }
        }

        private void HandleHotkeys()
        {
            // Never steal keys while the player is typing in chat or a menu has input.
            if (ChatManager.IsTyping) return;
            if (Player.LocalPlayer != null && Player.LocalPlayer.BlockInputs) return;

            if (_overlayEnabled.Value && Input.GetKeyDown(_overlayKey.Value))
                _overlayVisible = !_overlayVisible;

            foreach (ModuleBase m in Modules)
            {
                if (!m.IsEnabled || m.DebugKeyEntry == null) continue;
                if (!Input.GetKeyDown(m.DebugKeyEntry.Value)) continue;

                Diag.Info("Hotkey " + m.DebugKeyEntry.Value + " -> " + m.Id);
                try { m.OnDebugKey(); }
                catch (Exception e) { Diag.Exception("Module " + m.Id + ".OnDebugKey", e); }
            }
        }

        private void OnApplicationQuit() => ModSave.SaveIfDirty(true);

        // ------------------------------------------------------------------ overlay

        private void OnGUI()
        {
            // Module UI first (health bars, prompts, dialogue), then the debug overlay on top.
            if (_sessionRunning)
            {
                for (int i = 0; i < Modules.Count; i++)
                {
                    ModuleBase m = Modules[i];
                    if (!m.IsEnabled || !m.SessionActive) continue;
                    try { m.OnGUI(); }
                    catch (Exception e) { Diag.Exception("Module " + m.Id + ".OnGUI", e); }
                }
            }

            if (!_overlayVisible || !_overlayEnabled.Value) return;

            const int w = 420;
            int h = 60 + Modules.Count * 20;
            GUI.Box(new Rect(10, 10, w, h), "How to Fish: Expanded " + Version);

            int y = 34;
            GUI.Label(new Rect(20, y, w - 20, 20),
                (_sessionRunning ? (FishNet.InstanceFinder.IsServerStarted ? "hosting" : "client") : "no session") +
                "   |   " + _overlayKey.Value + " closes this");
            y += 22;

            foreach (ModuleBase m in Modules)
            {
                string key = m.DebugKeyEntry != null ? "[" + m.DebugKeyEntry.Value + "] " : "      ";
                string status;
                try { status = m.StatusLine(); }
                catch (Exception e) { status = "error: " + e.Message; }

                GUI.Label(new Rect(20, y, w - 20, 20), key + m.DisplayName + " - " + status);
                y += 20;
            }
        }
    }
}
