using System;
using System.Collections.Generic;
using FishNet.Connection;

namespace Expanded
{
    /// <summary>
    /// The part of mod progress every player needs to see, not just the host: which rewards the
    /// crew has unlocked (a refitted boat, a deck cannon...).
    ///
    /// The host is the source of truth (it owns the save file). Clients hold a mirror, filled by a
    /// full snapshot when they join and kept current by incremental updates. Without the join-time
    /// snapshot, a friend who joins after the pirates were beaten would never see your cannons.
    /// </summary>
    internal static class SharedState
    {
        private static readonly HashSet<string> Unlocks = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Raised on every machine whenever the unlock set changes.</summary>
        internal static event Action Changed;

        internal static bool Has(string key) => !string.IsNullOrEmpty(key) && Unlocks.Contains(key);

        internal static void RegisterHandlers()
        {
            ModNet.OnClient(Msg.UnlockChanged, r =>
            {
                string key = r.ReadString();
                bool granted = r.ReadBoolean();
                bool changed = granted ? Unlocks.Add(key) : Unlocks.Remove(key);
                if (!changed) return;
                Diag.Info("Unlock " + (granted ? "received" : "revoked") + ": " + key);
                RaiseChanged();
            });

            ModNet.OnClient(Msg.UnlockSnapshot, r =>
            {
                List<string> all = ModNet.ReadStringList(r);
                Unlocks.Clear();
                foreach (string k in all) if (!string.IsNullOrEmpty(k)) Unlocks.Add(k);
                Diag.Info("Unlock snapshot received: " + Unlocks.Count + " unlock(s).");
                RaiseChanged();
            });

            ModNet.OnServer(Msg.Hello, (conn, r) => SendSnapshotTo(conn));
        }

        /// <summary>Host: seed the mirror from the save file when a session starts.</summary>
        internal static void LoadFromSave()
        {
            Unlocks.Clear();
            foreach (string k in ModSave.Data.Unlocks) if (!string.IsNullOrEmpty(k)) Unlocks.Add(k);
            RaiseChanged();
        }

        /// <summary>Host: grant an unlock, persist it and tell everyone. Returns false if already owned.</summary>
        internal static bool Grant(string key)
        {
            if (!ModSave.AddUnlock(key)) return false;
            Unlocks.Add(key);
            ModNet.SendToAll(Msg.UnlockChanged, w => { w.Write(key); w.Write(true); });
            RaiseChanged();
            return true;
        }

        /// <summary>Host: take an unlock away again (used for switches, e.g. old boat / pirate hull).</summary>
        internal static bool Revoke(string key)
        {
            if (string.IsNullOrEmpty(key) || !ModSave.Data.Unlocks.Remove(key)) return false;
            ModSave.MarkDirty();
            Unlocks.Remove(key);
            ModNet.SendToAll(Msg.UnlockChanged, w => { w.Write(key); w.Write(false); });
            RaiseChanged();
            return true;
        }

        private static void SendSnapshotTo(NetworkConnection conn)
        {
            var list = new List<string>(ModSave.Data.Unlocks);
            ModNet.SendTo(conn, Msg.UnlockSnapshot, w => ModNet.WriteStringList(w, list));
            Diag.Info("Sent unlock snapshot (" + list.Count + ") to client " + (conn != null ? conn.ClientId.ToString() : "?") + ".");
        }

        internal static void Clear()
        {
            Unlocks.Clear();
            RaiseChanged();
        }

        private static void RaiseChanged()
        {
            try { Changed?.Invoke(); }
            catch (Exception e) { Diag.Exception("SharedState.Changed", e); }
        }
    }
}
