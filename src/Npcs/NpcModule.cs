using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using Expanded.Content;
using Expanded.Pirates;
using Expanded.Quests;
using FishNet;
using FishNet.Connection;
using UnityEngine;

namespace Expanded.Npcs
{
    /// <summary>
    /// Story characters and dialogue.
    ///
    /// Presence and dialogue are host-authoritative: the host decides which characters exist (from
    /// story flags), what they say (from quest state) and what a choice does. The characters
    /// themselves are local on every client and are placed deterministically from the island's own
    /// geometry, so everyone sees them in the same spot without their positions being networked.
    ///
    /// Talking is a request: client presses the interact key near a character, the host answers with
    /// a dialogue page for that client only, the client picks an option, the host acts on it.
    /// </summary>
    internal sealed class NpcModule : ModuleBase
    {
        internal override string Id => "Npcs";
        internal override string DisplayName => "Story Characters";
        internal override KeyCode DefaultDebugKey => KeyCode.F6;

        private enum Marker : byte { None = 0, Offer = 1, Active = 2 }

        private sealed class Instance
        {
            public NpcDef Def;
            public GameObject Go;
            public Marker Marker;
            public float ResumeIdleAt = -1f;
        }

        private sealed class Page
        {
            public string NpcId;
            public string Name;
            public string Text;
            public List<KeyValuePair<string, string>> Options = new List<KeyValuePair<string, string>>();
        }

        private ConfigEntry<float> _talkRange;

        // Client state (every machine, host included).
        private readonly Dictionary<string, Marker> _present = new Dictionary<string, Marker>(StringComparer.Ordinal);
        private readonly Dictionary<string, Instance> _spawned = new Dictionary<string, Instance>(StringComparer.Ordinal);
        private Vector3 _placedFor = new Vector3(float.NaN, 0f, 0f);
        private string _nearId;
        private Page _open;
        private float _nextPlacementCheck;

        // Host state.
        private string _lastPresenceSignature = "";
        private float _nextPresenceCheck;

        internal override void Configure(ConfigFile config)
        {
            _talkRange = config.Bind(Id, "TalkRange", 3f, "How close you must be to talk to a character.");
        }

        internal override void OnEnable() => RegisterNetHandlers();

        internal override void OnSessionStart(bool asServer)
        {
            ModCharacters.Load();
            _lastPresenceSignature = "";
            _nextPresenceCheck = 0f;
        }

        internal override void OnSessionEnd()
        {
            DespawnAll();
            _present.Clear();
            _open = null;
            _placedFor = new Vector3(float.NaN, 0f, 0f);
        }

        internal override void Tick()
        {
            if (IsServer) HostTickPresence();
            ClientTickPlacement();
            ClientTickInteraction();
        }

        // ------------------------------------------------------------------ host: presence

        /// <summary>
        /// Recomputes who should be present and what marker each shows, and broadcasts only when that
        /// actually changed, so a character appears the moment its story beat lands.
        /// </summary>
        private void HostTickPresence()
        {
            if (Time.time < _nextPresenceCheck) return;
            _nextPresenceCheck = Time.time + 1f;

            QuestEngine engine = QuestModule.Instance?.Engine;
            if (engine == null) return;

            var entries = new List<KeyValuePair<string, Marker>>();
            var sig = new System.Text.StringBuilder();
            foreach (NpcDef npc in StoryNpcs.All)
            {
                if (!StoryNpcs.IsPresent(npc, engine)) continue;
                Marker m = MarkerFor(npc, engine);
                entries.Add(new KeyValuePair<string, Marker>(npc.Id, m));
                sig.Append(npc.Id).Append(':').Append((int)m).Append(';');
            }

            string s = sig.ToString();
            if (s == _lastPresenceSignature) return;
            _lastPresenceSignature = s;

            ModNet.SendToAll(Msg.NpcSet, w => WritePresence(w, entries));
            Diag.Info("Npcs: presence " + (s.Length > 0 ? s : "(none)"));
        }

        private static Marker MarkerFor(NpcDef npc, QuestEngine engine)
        {
            Marker best = Marker.None;
            foreach (QuestDef q in engine.Definitions)
            {
                if (!string.Equals(q.Giver, npc.Name, StringComparison.Ordinal)) continue;
                QuestStatus st = engine.Progress(q.Id).Status;
                if (st == QuestStatus.Available) return Marker.Offer;
                if (st == QuestStatus.Active) best = Marker.Active;
            }
            return best;
        }

        private static void WritePresence(BinaryWriter w, List<KeyValuePair<string, Marker>> entries)
        {
            w.Write((ushort)entries.Count);
            foreach (KeyValuePair<string, Marker> e in entries)
            {
                w.Write(e.Key);
                w.Write((byte)e.Value);
            }
        }

        // ------------------------------------------------------------------ host: dialogue

        private void HostHandleTalk(NetworkConnection conn, string npcId)
        {
            QuestModule qm = QuestModule.Instance;
            QuestEngine engine = qm?.Engine;
            NpcDef npc = StoryNpcs.ById(npcId);
            if (engine == null || npc == null || !StoryNpcs.IsPresent(npc, engine)) return;

            if (!SpeakerInRange(conn, npcId))
            {
                Diag.Debug("Npcs: rejected talk to " + npcId + ", speaker too far.");
                return;
            }

            qm.RaiseTalk(npc.Name);
            Page page = BuildPage(npc, engine);
            ModNet.SendTo(conn, Msg.DialogueOpen, w => WritePage(w, page));
        }

        /// <summary>
        /// What a character says now: an offer if they have one, a reminder if a job of theirs is
        /// running, the wrap-up line once after finishing a job, otherwise idle chatter.
        /// </summary>
        private static Page BuildPage(NpcDef npc, QuestEngine engine)
        {
            var page = new Page { NpcId = npc.Id, Name = npc.Name };

            foreach (QuestDef q in engine.Definitions)
            {
                if (!string.Equals(q.Giver, npc.Name, StringComparison.Ordinal)) continue;
                if (engine.Progress(q.Id).Status != QuestStatus.Available) continue;

                page.Text = q.Offer;
                page.Options.Add(new KeyValuePair<string, string>("accept:" + q.Id, "I'll do it."));
                page.Options.Add(new KeyValuePair<string, string>("close", "Not now."));
                return page;
            }

            foreach (QuestDef q in engine.Definitions)
            {
                if (!string.Equals(q.Giver, npc.Name, StringComparison.Ordinal)) continue;
                QuestProgress p = engine.Progress(q.Id);
                if (p.Status != QuestStatus.Active) continue;

                page.Text = q.Reminder(p.StepIndex);
                page.Options.Add(new KeyValuePair<string, string>("close", "On it."));
                return page;
            }

            // Wrap-up line, once per quest, remembered in the save so it is not repeated forever.
            foreach (QuestDef q in engine.Definitions)
            {
                if (!string.Equals(q.Giver, npc.Name, StringComparison.Ordinal)) continue;
                if (engine.Progress(q.Id).Status != QuestStatus.Completed) continue;

                string key = "npc." + npc.Id + ".done." + q.Id;
                if (ModSave.Counter(key) > 0) continue;
                ModSave.SetCounter(key, 1);

                page.Text = q.Done;
                page.Options.Add(new KeyValuePair<string, string>("close", "Thanks."));
                return page;
            }

            List<string> barks = StoryNpcs.CurrentBarks(npc, engine);
            page.Text = barks.Count > 0 ? barks[UnityEngine.Random.Range(0, barks.Count)] : "...";

            // The shipwright switches the boat between its old hull and the captured pirate ship.
            if (npc.Id == "mako" && SharedState.Has(PirateStory.UnlockPirateShip))
            {
                bool fitted = !SharedState.Has(PirateHull.UnlockDisabled);
                page.Options.Add(fitted
                    ? new KeyValuePair<string, string>("hull:off", "Put my old boat back together.")
                    : new KeyValuePair<string, string>("hull:on", "Rig the Widow's hull again."));
            }

            page.Options.Add(new KeyValuePair<string, string>("close", "Bye."));
            return page;
        }

        private void HostHandleChoice(NetworkConnection conn, string npcId, string optionId)
        {
            if (string.IsNullOrEmpty(optionId) || optionId == "close") return;

            if (optionId == "hull:on" || optionId == "hull:off")
            {
                if (npcId != "mako" || !SharedState.Has(PirateStory.UnlockPirateShip)) return;
                if (!SpeakerInRange(conn, npcId)) return;

                bool on = optionId == "hull:on";
                if (on) SharedState.Revoke(PirateHull.UnlockDisabled);
                else SharedState.Grant(PirateHull.UnlockDisabled);
                PirateModule.Announce(on ? "Mako rigs the Widow's hull onto your boat. She's yours to sail."
                                         : "Mako strips the pirate hull off. Your old boat is back.");
                return;
            }

            const string acceptPrefix = "accept:";
            if (!optionId.StartsWith(acceptPrefix, StringComparison.Ordinal)) return;

            string questId = optionId.Substring(acceptPrefix.Length);
            QuestModule qm = QuestModule.Instance;
            QuestDef def = qm?.Engine?.Def(questId);
            NpcDef npc = StoryNpcs.ById(npcId);

            // The quest must really belong to the character the choice came through.
            if (def == null || npc == null || !string.Equals(def.Giver, npc.Name, StringComparison.Ordinal)) return;
            if (!SpeakerInRange(conn, npcId)) return;

            if (qm.HostAccept(questId))
            {
                Diag.Info("Npcs: '" + questId + "' accepted via " + npc.Name + ".");
                _nextPresenceCheck = 0f; // refresh markers now
            }
        }

        /// <summary>Validates the speaker against the host's own copy of the character.</summary>
        private bool SpeakerInRange(NetworkConnection conn, string npcId)
        {
            Instance inst;
            if (!_spawned.TryGetValue(npcId, out inst) || inst.Go == null) return true; // cannot check; allow

            Player p = PlayerFor(conn);
            if (p == null) return false;
            return Vector3.Distance(p.Transform.position, inst.Go.transform.position) <= _talkRange.Value * 2.5f;
        }

        private static Player PlayerFor(NetworkConnection conn)
        {
            if (conn == null) return null;
            foreach (Player p in PlayerManager.Players)
                if (p != null && p.Owner == conn) return p;
            return null;
        }

        private static void WritePage(BinaryWriter w, Page p)
        {
            w.Write(p.NpcId ?? "");
            w.Write(p.Name ?? "");
            w.Write(p.Text ?? "");
            w.Write((byte)Mathf.Min(p.Options.Count, 4));
            for (int i = 0; i < p.Options.Count && i < 4; i++)
            {
                w.Write(p.Options[i].Key);
                w.Write(p.Options[i].Value);
            }
        }

        private static Page ReadPage(BinaryReader r)
        {
            var p = new Page { NpcId = r.ReadString(), Name = r.ReadString(), Text = r.ReadString() };
            int n = r.ReadByte();
            for (int i = 0; i < n; i++)
                p.Options.Add(new KeyValuePair<string, string>(r.ReadString(), r.ReadString()));
            return p;
        }

        // ------------------------------------------------------------------ networking

        private void RegisterNetHandlers()
        {
            ModNet.OnClient(Msg.NpcSet, r =>
            {
                _present.Clear();
                int n = r.ReadUInt16();
                for (int i = 0; i < n; i++)
                {
                    string id = r.ReadString();
                    var m = (Marker)r.ReadByte();
                    _present[id] = m;
                }
                SyncSpawnedToPresence();
            });

            ModNet.OnClient(Msg.DialogueOpen, r =>
            {
                _open = ReadPage(r);
                Instance inst;
                if (_spawned.TryGetValue(_open.NpcId, out inst)) Animate(inst, "Wave", false);
            });

            ModNet.OnServer(Msg.RequestTalk, (conn, r) => HostHandleTalk(conn, r.ReadString()));

            ModNet.OnServer(Msg.DialogueChoice, (conn, r) =>
            {
                string npcId = r.ReadString();
                string option = r.ReadString();
                HostHandleChoice(conn, npcId, option);
            });

            // A late joiner gets the cast immediately.
            ModNet.OnServer(Msg.Hello, (conn, r) =>
            {
                QuestEngine engine = QuestModule.Instance?.Engine;
                if (engine == null) return;
                var entries = new List<KeyValuePair<string, Marker>>();
                foreach (NpcDef npc in StoryNpcs.All)
                    if (StoryNpcs.IsPresent(npc, engine))
                        entries.Add(new KeyValuePair<string, Marker>(npc.Id, MarkerFor(npc, engine)));
                ModNet.SendTo(conn, Msg.NpcSet, w => WritePresence(w, entries));
            });
        }

        // ------------------------------------------------------------------ client: placement

        /// <summary>Re-places everyone when the island changes (the landing point moves).</summary>
        private void ClientTickPlacement()
        {
            if (Time.time < _nextPlacementCheck) return;
            _nextPlacementCheck = Time.time + 1f;

            Vector3 anchor = SpawnManager.PlayerSpawnPos;
            if ((anchor - _placedFor).sqrMagnitude > 1f || float.IsNaN(_placedFor.x))
            {
                _placedFor = anchor;
                DespawnAll();
            }
            SyncSpawnedToPresence();

            foreach (Instance inst in _spawned.Values)
            {
                Marker m;
                inst.Marker = _present.TryGetValue(inst.Def.Id, out m) ? m : Marker.None;
            }
        }

        private void SyncSpawnedToPresence()
        {
            // Remove characters who left the story.
            var gone = new List<string>();
            foreach (string id in _spawned.Keys) if (!_present.ContainsKey(id)) gone.Add(id);
            foreach (string id in gone) Despawn(id);

            // Add newcomers.
            foreach (KeyValuePair<string, Marker> kv in _present)
            {
                if (_spawned.ContainsKey(kv.Key)) continue;
                NpcDef def = StoryNpcs.ById(kv.Key);
                if (def == null) continue;
                Spawn(def, kv.Value);
            }
        }

        private void Spawn(NpcDef def, Marker marker)
        {
            if (!ModCharacters.Available) return;

            Vector3 pos, face;
            if (!NpcPlacement.Find(def.Slot, out pos, out face)) return;

            Quaternion rot = Quaternion.LookRotation(Flat(face - pos), Vector3.up);
            GameObject go = ModCharacters.Create(def.Model, pos, rot);
            if (go == null) return;
            go.name = "Npc:" + def.Id;

            var inst = new Instance { Def = def, Go = go, Marker = marker };
            _spawned[def.Id] = inst;
            Animate(inst, "Idle", true);
            Diag.Info("Npcs: " + def.Name + " placed at " + pos.ToString("F1") + ".");
        }

        private void Despawn(string id)
        {
            Instance inst;
            if (!_spawned.TryGetValue(id, out inst)) return;
            if (inst.Go != null) UnityEngine.Object.Destroy(inst.Go);
            _spawned.Remove(id);
            if (_open != null && _open.NpcId == id) _open = null;
        }

        private void DespawnAll()
        {
            foreach (Instance inst in _spawned.Values) if (inst.Go != null) UnityEngine.Object.Destroy(inst.Go);
            _spawned.Clear();
            _nearId = null;
        }

        /// <summary>One-shot gestures (wave, nod) fall back to idle on their own after a moment.</summary>
        private static void Animate(Instance inst, string clip, bool loop)
        {
            if (inst?.Go == null) return;
            if (ModCharacters.Play(inst.Go, clip, loop))
                inst.ResumeIdleAt = loop ? -1f : Time.time + 1.8f;
        }

        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-4f ? v.normalized : Vector3.forward;
        }

        // ------------------------------------------------------------------ client: interaction

        private void ClientTickInteraction()
        {
            foreach (Instance inst in _spawned.Values)
                if (inst.ResumeIdleAt > 0f && Time.time >= inst.ResumeIdleAt) Animate(inst, "Idle", true);

            Player me = Player.LocalPlayer;
            _nearId = null;
            if (me == null || me.Dying.IsDead) { _open = null; return; }

            float best = _talkRange.Value;
            foreach (Instance inst in _spawned.Values)
            {
                if (inst.Go == null) continue;
                float d = Vector3.Distance(me.Transform.position, inst.Go.transform.position);
                if (d <= best) { best = d; _nearId = inst.Def.Id; }

                // Characters glance at whoever is closest to them.
                if (d < 6f)
                {
                    Vector3 look = Flat(me.Transform.position - inst.Go.transform.position);
                    inst.Go.transform.rotation = Quaternion.Slerp(inst.Go.transform.rotation,
                        Quaternion.LookRotation(look, Vector3.up), Time.deltaTime * 3f);
                }
            }

            // Walking away ends the conversation.
            if (_open != null)
            {
                Instance talking;
                if (!_spawned.TryGetValue(_open.NpcId, out talking) || talking.Go == null ||
                    Vector3.Distance(me.Transform.position, talking.Go.transform.position) > _talkRange.Value * 2f)
                    _open = null;
            }

            if (ChatManager.IsTyping || me.BlockInputs) return;

            if (_open != null)
            {
                for (int i = 0; i < _open.Options.Count && i < 4; i++)
                {
                    if (!Input.GetKeyDown(KeyCode.Alpha1 + i) && !Input.GetKeyDown(KeyCode.Keypad1 + i)) continue;
                    Choose(i);
                    return;
                }
                if (Input.GetKeyDown(PirateModule.Cfg.InteractKey.Value)) _open = null;
                return;
            }

            if (_nearId == null || DeckCannonNearby()) return;
            if (!Input.GetKeyDown(PirateModule.Cfg.InteractKey.Value)) return;

            string id = _nearId;
            ModNet.SendToServer(Msg.RequestTalk, w => w.Write(id));
        }

        /// <summary>The cannon shares the interact key; standing at a gun, the gun wins.</summary>
        private static bool DeckCannonNearby() => DeckCannon.PlayerAtGun;

        private void Choose(int index)
        {
            if (_open == null || index >= _open.Options.Count) return;
            string npcId = _open.NpcId;
            string option = _open.Options[index].Key;

            Instance inst;
            if (option.StartsWith("accept:", StringComparison.Ordinal) && _spawned.TryGetValue(npcId, out inst))
                Animate(inst, "Yes", false);

            _open = null;
            ModNet.SendToServer(Msg.DialogueChoice, w => { w.Write(npcId); w.Write(option); });
        }

        // ------------------------------------------------------------------ UI

        private GUIStyle _name, _body, _opt, _mark, _prompt;

        private void EnsureStyles()
        {
            if (_name != null) return;
            _name = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold };
            _name.normal.textColor = new Color(1f, 0.85f, 0.55f);
            _body = new GUIStyle(GUI.skin.label) { fontSize = 16, wordWrap = true };
            _body.normal.textColor = Color.white;
            _opt = new GUIStyle(GUI.skin.label) { fontSize = 16 };
            _opt.normal.textColor = new Color(0.8f, 0.95f, 1f);
            _mark = new GUIStyle(GUI.skin.label) { fontSize = 34, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _prompt = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _prompt.normal.textColor = Color.white;
        }

        internal override void OnGUI()
        {
            EnsureStyles();
            DrawMarkers();

            if (_open != null) { DrawDialogue(); return; }

            if (_nearId != null && !DeckCannonNearby())
            {
                Instance inst;
                if (_spawned.TryGetValue(_nearId, out inst))
                {
                    Rect r = new Rect((Screen.width - 340f) * 0.5f, Screen.height * 0.62f, 340f, 34f);
                    PirateModule.DrawRect(r, new Color(0f, 0f, 0f, 0.45f));
                    GUI.Label(r, "[" + PirateModule.Cfg.InteractKey.Value + "] Talk to " + inst.Def.Name, _prompt);
                }
            }
        }

        /// <summary>"!" over characters with a job for you, "?" over those whose job you are on.</summary>
        private void DrawMarkers()
        {
            Camera cam = null;
            try { cam = GameInfo.CurCamera; } catch { }
            if (cam == null) return;

            foreach (Instance inst in _spawned.Values)
            {
                if (inst.Go == null || inst.Marker == Marker.None) continue;
                Vector3 head = inst.Go.transform.position + Vector3.up * 2.4f;
                Vector3 sp = cam.WorldToScreenPoint(head);
                if (sp.z <= 0f || sp.z > 60f) continue;

                _mark.normal.textColor = inst.Marker == Marker.Offer ? new Color(1f, 0.85f, 0.2f) : new Color(0.75f, 0.75f, 0.75f);
                float bob = Mathf.Sin(Time.time * 3f) * 4f;
                GUI.Label(new Rect(sp.x - 20f, Screen.height - sp.y - 30f + bob, 40f, 40f),
                          inst.Marker == Marker.Offer ? "!" : "?", _mark);
            }
        }

        private void DrawDialogue()
        {
            const float w = 680f;
            float x = (Screen.width - w) * 0.5f;
            float textH = _body.CalcHeight(new GUIContent(_open.Text), w - 40f);
            float h = 60f + textH + 16f + _open.Options.Count * 26f + 20f;
            float y = Screen.height - h - 40f;

            PirateModule.DrawRect(new Rect(x, y, w, h), new Color(0.05f, 0.04f, 0.03f, 0.85f));
            GUI.Label(new Rect(x + 20f, y + 14f, w - 40f, 26f), _open.Name, _name);
            GUI.Label(new Rect(x + 20f, y + 46f, w - 40f, textH), _open.Text, _body);

            float oy = y + 46f + textH + 16f;
            for (int i = 0; i < _open.Options.Count; i++)
            {
                GUI.Label(new Rect(x + 30f, oy, w - 60f, 24f), (i + 1) + ")  " + _open.Options[i].Value, _opt);
                oy += 26f;
            }
        }

        // ------------------------------------------------------------------ debug

        internal override void OnDebugKey()
        {
            foreach (Instance inst in _spawned.Values)
                Diag.Info("Npcs: " + inst.Def.Name + " at " + (inst.Go != null ? inst.Go.transform.position.ToString("F1") : "?") +
                          ", marker " + inst.Marker);
            if (_spawned.Count == 0) Diag.Info("Npcs: none placed (present: " + _present.Count + ", characters bundle " + ModCharacters.Available + ").");
        }

        internal override string StatusLine()
        {
            if (!IsEnabled) return "disabled";
            if (!ModCharacters.Available) return "no character bundle";
            return _spawned.Count + " placed of " + _present.Count + " present" + (_open != null ? ", talking" : "");
        }
    }
}
