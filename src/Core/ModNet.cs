using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FishNet;
using FishNet.Broadcast;
using FishNet.Connection;
using FishNet.Serializing;
using FishNet.Transporting;

namespace Expanded
{
    /// <summary>
    /// Custom networking for the mod.
    ///
    /// The game's own messages are generated at build time by FishNet's code generator, which never
    /// runs over a mod assembly. So instead of defining many message types, the mod defines exactly
    /// one - a bag of bytes - registers a serializer for it at runtime, and hand-packs every mod
    /// message inside it. That keeps us off FishNet's code generation entirely, and makes the wire
    /// format ours to version.
    ///
    /// Payloads use plain BinaryWriter/BinaryReader rather than FishNet's writers, so the format is
    /// stable and independent of the FishNet version the game ships.
    /// </summary>
    internal static class ModNet
    {
        /// <summary>Bumped when the wire format changes incompatibly; mismatched peers are warned.</summary>
        internal const int Protocol = 2;   // 2: UnlockChanged carries granted/revoked

        internal struct ModPacket : IBroadcast
        {
            public byte[] Data;
        }

        private static bool _serializersInstalled;
        private static bool _serverHooked, _clientHooked;

        // Several modules may listen to the same message (e.g. Hello), so each id holds a list.
        // Handlers are registered once per game launch (in OnEnable), never per session, so the
        // lists cannot accumulate duplicates across sessions.
        private static readonly Dictionary<byte, List<Action<NetworkConnection, BinaryReader>>> ServerHandlers =
            new Dictionary<byte, List<Action<NetworkConnection, BinaryReader>>>();
        private static readonly Dictionary<byte, List<Action<BinaryReader>>> ClientHandlers =
            new Dictionary<byte, List<Action<BinaryReader>>>();

        internal static bool ServerReady => InstanceFinder.IsServerStarted;
        internal static bool ClientReady => InstanceFinder.IsClientStarted;

        // ------------------------------------------------------------------ setup

        /// <summary>
        /// Registers the byte-bag serializer. Safe to call repeatedly; only the first call does work.
        /// Must run before any broadcast is sent or received.
        /// </summary>
        internal static void InstallSerializers()
        {
            if (_serializersInstalled) return;

            GenericWriter<ModPacket>.SetWrite((Writer w, ModPacket p) => w.WriteUInt8ArrayAndSize(p.Data));
            GenericReader<ModPacket>.SetRead((Reader r) => new ModPacket { Data = r.ReadUInt8ArrayAndSizeAllocated() });

            _serializersInstalled = true;
            Diag.Info("ModNet: serializers registered (protocol " + Protocol + ").");
        }

        internal static void HookServer()
        {
            if (_serverHooked || InstanceFinder.ServerManager == null) return;
            InstallSerializers();
            InstanceFinder.ServerManager.RegisterBroadcast<ModPacket>(OnServerPacket, true);
            _serverHooked = true;
            Diag.Info("ModNet: server listening.");
        }

        internal static void HookClient()
        {
            if (_clientHooked || InstanceFinder.ClientManager == null) return;
            InstallSerializers();
            InstanceFinder.ClientManager.RegisterBroadcast<ModPacket>(OnClientPacket);
            _clientHooked = true;
            Diag.Info("ModNet: client listening.");
        }

        internal static void Unhook()
        {
            try
            {
                if (_serverHooked && InstanceFinder.ServerManager != null)
                    InstanceFinder.ServerManager.UnregisterBroadcast<ModPacket>(OnServerPacket);
                if (_clientHooked && InstanceFinder.ClientManager != null)
                    InstanceFinder.ClientManager.UnregisterBroadcast<ModPacket>(OnClientPacket);
            }
            catch (Exception e)
            {
                Diag.Exception("ModNet.Unhook", e);
            }
            _serverHooked = _clientHooked = false;
        }

        // ------------------------------------------------------------------ handler registration

        /// <summary>
        /// Handles a message sent by a client, on the host. Call once per game launch (OnEnable):
        /// handlers accumulate, they are not replaced.
        /// </summary>
        internal static void OnServer(byte messageId, Action<NetworkConnection, BinaryReader> handler)
        {
            List<Action<NetworkConnection, BinaryReader>> list;
            if (!ServerHandlers.TryGetValue(messageId, out list))
                ServerHandlers[messageId] = list = new List<Action<NetworkConnection, BinaryReader>>();
            list.Add(handler);
        }

        /// <summary>
        /// Handles a message sent by the host, on a client (the host receives these too). Call once
        /// per game launch; handlers accumulate.
        /// </summary>
        internal static void OnClient(byte messageId, Action<BinaryReader> handler)
        {
            List<Action<BinaryReader>> list;
            if (!ClientHandlers.TryGetValue(messageId, out list))
                ClientHandlers[messageId] = list = new List<Action<BinaryReader>>();
            list.Add(handler);
        }

        // ------------------------------------------------------------------ sending

        /// <summary>Client -> host.</summary>
        internal static void SendToServer(byte messageId, Action<BinaryWriter> write = null)
        {
            if (!ClientReady) { Diag.Debug("ModNet: dropped " + messageId + " to server (client not started)."); return; }
            try
            {
                InstanceFinder.ClientManager.Broadcast(new ModPacket { Data = Pack(messageId, write) }, Channel.Reliable);
            }
            catch (Exception e)
            {
                Diag.Exception("ModNet.SendToServer(" + messageId + ")", e);
            }
        }

        /// <summary>Host -> every client (including the host's own client).</summary>
        internal static void SendToAll(byte messageId, Action<BinaryWriter> write = null)
        {
            if (!ServerReady) { Diag.Debug("ModNet: dropped " + messageId + " to all (server not started)."); return; }
            try
            {
                InstanceFinder.ServerManager.Broadcast(new ModPacket { Data = Pack(messageId, write) }, true, Channel.Reliable);
            }
            catch (Exception e)
            {
                Diag.Exception("ModNet.SendToAll(" + messageId + ")", e);
            }
        }

        /// <summary>Host -> one client.</summary>
        internal static void SendTo(NetworkConnection conn, byte messageId, Action<BinaryWriter> write = null)
        {
            if (!ServerReady || conn == null || !conn.IsValid) return;
            try
            {
                InstanceFinder.ServerManager.Broadcast(conn, new ModPacket { Data = Pack(messageId, write) }, true, Channel.Reliable);
            }
            catch (Exception e)
            {
                Diag.Exception("ModNet.SendTo(" + messageId + ")", e);
            }
        }

        // ------------------------------------------------------------------ plumbing

        private static byte[] Pack(byte messageId, Action<BinaryWriter> write)
        {
            using (var ms = new MemoryStream(64))
            using (var bw = new BinaryWriter(ms, Encoding.UTF8))
            {
                bw.Write((byte)Protocol);
                bw.Write(messageId);
                write?.Invoke(bw);
                bw.Flush();
                return ms.ToArray();
            }
        }

        private static void OnServerPacket(NetworkConnection conn, ModPacket packet, Channel channel)
        {
            byte id;
            if (!Validate(packet, "server", out id)) return;

            List<Action<NetworkConnection, BinaryReader>> list;
            if (!ServerHandlers.TryGetValue(id, out list) || list.Count == 0)
            {
                Diag.Debug("ModNet: no server handler for message " + id + ".");
                return;
            }

            // Every handler gets its own reader over the same bytes, so one handler reading more
            // or less than expected cannot corrupt what the next one sees.
            for (int i = 0; i < list.Count; i++)
                Invoke(packet, id, "server", br => list[i](conn, br));
        }

        private static void OnClientPacket(ModPacket packet, Channel channel)
        {
            byte id;
            if (!Validate(packet, "client", out id)) return;

            List<Action<BinaryReader>> list;
            if (!ClientHandlers.TryGetValue(id, out list) || list.Count == 0)
            {
                Diag.Debug("ModNet: no client handler for message " + id + ".");
                return;
            }

            for (int i = 0; i < list.Count; i++)
                Invoke(packet, id, "client", br => list[i](br));
        }

        private static bool Validate(ModPacket packet, string side, out byte id)
        {
            id = 0;
            if (packet.Data == null || packet.Data.Length < 2)
            {
                Diag.Warn("ModNet: ignored empty packet on " + side + ".");
                return false;
            }

            byte protocol = packet.Data[0];
            id = packet.Data[1];
            if (protocol != Protocol)
            {
                ProtocolMismatch(protocol, side);
                return false;
            }
            return true;
        }

        /// <summary>
        /// A malformed or hostile packet must never take down the game, so every failure is contained
        /// and logged rather than thrown into FishNet's receive loop.
        /// </summary>
        private static void Invoke(ModPacket packet, byte id, string side, Action<BinaryReader> handler)
        {
            try
            {
                using (var ms = new MemoryStream(packet.Data, 2, packet.Data.Length - 2, false))
                using (var br = new BinaryReader(ms, Encoding.UTF8))
                {
                    handler(br);
                }
            }
            catch (Exception e)
            {
                Diag.Exception("ModNet handler for message " + id + " on " + side, e);
            }
        }

        private static bool _warnedMismatch;

        private static void ProtocolMismatch(byte theirs, string side)
        {
            if (_warnedMismatch) return;
            _warnedMismatch = true;
            Diag.Error("ModNet: protocol mismatch on " + side + " - this game speaks " + Protocol +
                       ", the other side speaks " + theirs + ". Everyone must run the same mod version. " +
                       "Mod features will not work correctly in this session.");
        }

        // ------------------------------------------------------------------ payload helpers

        internal static void WriteString(BinaryWriter w, string s) => w.Write(s ?? "");
        internal static string ReadString(BinaryReader r) => r.ReadString();

        internal static void WriteStringList(BinaryWriter w, IList<string> items)
        {
            int n = items?.Count ?? 0;
            w.Write((ushort)n);
            for (int i = 0; i < n; i++) w.Write(items[i] ?? "");
        }

        internal static List<string> ReadStringList(BinaryReader r)
        {
            int n = r.ReadUInt16();
            var list = new List<string>(n);
            for (int i = 0; i < n; i++) list.Add(r.ReadString());
            return list;
        }
    }

    /// <summary>Message ids. Keep stable: changing a number is a wire-format break.</summary>
    internal static class Msg
    {
        // host -> clients
        internal const byte QuestSnapshot = 1;   // full journal state
        internal const byte QuestToast = 2;      // "quest started/completed" notification
        internal const byte DialogueOpen = 3;    // show a dialogue page
        internal const byte UnlockChanged = 4;   // capability unlocked/revoked
        internal const byte EventBanner = 5;     // world event announcement
        internal const byte CannonFired = 6;     // a ball left a muzzle: clients animate it
        internal const byte CannonImpact = 7;    // a ball landed: clients remove their copy
        internal const byte ShipStatus = 8;      // pirate ship health/state for the health bar
        internal const byte UnlockSnapshot = 9;  // every unlock the host knows, for late joiners
        internal const byte NpcSet = 10;         // which story NPCs are present, with quest markers
        internal const byte CrewDied = 11;       // a pirate crew member was killed
        internal const byte SiteMarker = 12;     // where the chart's mark is (or that it is gone)

        // clients -> host
        internal const byte RequestAccept = 20;  // accept an offered quest
        internal const byte DialogueChoice = 21; // picked a dialogue option
        internal const byte RequestTalk = 22;    // interacted with a mod NPC
        internal const byte RequestBuy = 23;     // buy from the mod shop
        internal const byte ShipHit = 24;        // my bullet hit the pirate ship
        internal const byte RequestFire = 25;    // fire the deck cannon I am standing at
        internal const byte Hello = 26;          // I just joined: send me the current state
        internal const byte CrewHit = 27;        // my bullet hit a pirate crew member
    }
}
