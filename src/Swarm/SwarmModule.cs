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

        internal override void Configure(ConfigFile config)
        {
            Cfg = new SwarmConfig(config);
        }

        internal override void OnSessionEnd()
        {
            // The director lives on a game object destroyed with the scene; nothing to clean up here,
            // but make sure a stale static never leaks into the next session.
            SwarmDirector.Active = null;
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
