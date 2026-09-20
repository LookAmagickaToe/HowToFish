using System;
using BepInEx.Configuration;
using UnityEngine;

namespace Expanded
{
    /// <summary>
    /// One self-contained feature of the mod (the seagull swarm, quests, pirates...). Modules are
    /// independent: each can be switched off in the config without affecting the others, and each
    /// gets a debug hotkey for testing in isolation.
    ///
    /// Lifecycle:
    ///   Configure   once at startup, bind config entries
    ///   OnEnable    once at startup if enabled
    ///   SessionStart/SessionEnd  whenever a game session begins or ends (host or client)
    ///   Tick        every frame while a session is running
    /// </summary>
    internal abstract class ModuleBase
    {
        /// <summary>Stable id used for config sections and log lines.</summary>
        internal abstract string Id { get; }

        /// <summary>Human-readable name for the debug overlay and logs.</summary>
        internal virtual string DisplayName => Id;

        /// <summary>Key that triggers this module's debug action. None = no hotkey.</summary>
        internal virtual KeyCode DefaultDebugKey => KeyCode.None;

        /// <summary>Harmony patch containers this module needs. May be empty.</summary>
        internal virtual Type[] PatchTypes => Array.Empty<Type>();

        internal ConfigEntry<bool> EnabledEntry { get; set; }
        internal ConfigEntry<KeyCode> DebugKeyEntry { get; set; }

        internal bool IsEnabled => EnabledEntry == null || EnabledEntry.Value;

        /// <summary>True while a networked session is running and this module was started.</summary>
        internal bool SessionActive { get; private set; }

        /// <summary>True if this machine is the host of the running session.</summary>
        internal bool IsServer { get; private set; }

        internal virtual void Configure(ConfigFile config) { }
        internal virtual void OnEnable() { }

        internal void BeginSession(bool asServer)
        {
            if (SessionActive) return;
            SessionActive = true;
            IsServer = asServer;
            OnSessionStart(asServer);
        }

        internal void FinishSession()
        {
            if (!SessionActive) return;
            SessionActive = false;
            OnSessionEnd();
            IsServer = false;
        }

        internal virtual void OnSessionStart(bool asServer) { }
        internal virtual void OnSessionEnd() { }
        internal virtual void Tick() { }

        /// <summary>Debug hotkey pressed. Host-only actions must check IsServer themselves.</summary>
        internal virtual void OnDebugKey() { }

        /// <summary>One line for the F1 status overlay.</summary>
        internal virtual string StatusLine() => IsEnabled ? "enabled" : "disabled";
    }
}
