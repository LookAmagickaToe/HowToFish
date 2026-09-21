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
    /// Story characters, played the way the game's own NPCs are:
    ///  - look at them and press the game's interact key ("Talk [E]"), same prompt and outline;
    ///  - what they say appears in the game's speech bubble above their head, one line per press;
    ///  - jobs are taken on simply by listening, there is nothing to choose;
    ///  - things they ask for are fed to them: throw or drop the item at them and they eat it.
    ///
    /// Presence, dialogue and feeding are host-authoritative: the host decides which characters
    /// exist (from story flags), what they say (from quest state) and whether an item counts. The
    /// characters themselves are local on every client and placed deterministically from the
    /// island's own geometry, so everyone sees them in the same spot without networking positions.
    /// </summary>
    internal sealed class NpcModule : ModuleBase
    {
        internal override string Id => "Npcs";
        internal override string DisplayName => "Story Characters";
        internal override KeyCode DefaultDebugKey => KeyCode.F6;
        internal override Type[] PatchTypes => new[] { typeof(NpcBodyPatches) };

        private enum Marker : byte { None = 0, Offer = 1, Active = 2 }

        /// <summary>Game NPC ids for story characters: 200 + slot, far above the game's own.</summary>
        private const int GameIdBase = 200;

        private sealed class Instance
        {
            public NpcDef Def;
            public GameObject Go;
            public Transform Bubble;
            public float Height = 1.6f;
            public float TalkingUntil;
            public NpcVoice Voice;
            public Marker Marker;
            public float ResumeIdleAt = -1f;

            // The conversation in progress on this machine: lines still to click through.
            public readonly List<string> Pending = new List<string>();
            public int PendingIndex;
            public float PendingExpires;
        }

        private static NpcModule _self;

        private ConfigEntry<float> _talkRange;
        private ConfigEntry<float> _feedRadius;
        private static float _textSeconds = 10f;

        // Client state (every machine, host included).
        private readonly Dictionary<string, Marker> _present = new Dictionary<string, Marker>(StringComparer.Ordinal);
        private readonly Dictionary<string, Instance> _spawned = new Dictionary<string, Instance>(StringComparer.Ordinal);
        private Vector3 _placedFor = new Vector3(float.NaN, 0f, 0f);
        private float _nextPlacementCheck;
        private float _lastTalkPress;

        // Host state.
        private string _lastPresenceSignature = "";
        private float _nextPresenceCheck;
        private float _nextFeedCheck;
        private NetworkConnection _swapArmedFor;
        private float _swapArmedUntil;

        private static readonly string[] EatLines =
        {
            "Mm. Crunchy.", "Feathers and all.", "Tastes like spite.", "Another!", "That one screamed at me on Tuesday."
        };

        internal NpcModule() { _self = this; }

        internal override void Configure(ConfigFile config)
        {
            _talkRange = config.Bind(Id, "TalkRange", 3f,
                "How far a conversation carries; walk further than about twice this and it ends.");
            _feedRadius = config.Bind(Id, "FeedRadius", 1.2f,
                "How close to a character an item must land for them to eat it.");

            ConfigEntry<float> textSeconds = config.Bind(Id, "TextSeconds", 10f,
                "How long a line stays in the speech bubble (the game's own NPCs use 5).");
            _textSeconds = Mathf.Max(1f, textSeconds.Value);
            textSeconds.SettingChanged += (s, e) => _textSeconds = Mathf.Max(1f, textSeconds.Value);

            ConfigEntry<float> voice = config.Bind(Id, "VoiceVolume", 1f,
                "Volume of the characters' mumbling, relative to the game's own NPCs (0 = silent).");
            NpcVoice.VolumeScale = Mathf.Max(0f, voice.Value);
            voice.SettingChanged += (s, e) => NpcVoice.VolumeScale = Mathf.Max(0f, voice.Value);

            ConfigEntry<float> glow = config.Bind(Id, "CharacterBrightness", 0.35f,
                "Faint self-glow of the story characters' colours (0-1). Their palette is very dark; " +
                "0 shows it exactly as painted. Takes effect on the next session.");
            ModCharacters.Brightness = glow.Value;
        }

        internal override void OnEnable()
        {
            RegisterNetHandlers();
            StoryNpcInteractable.Talked += OnTalkPressed;
        }

        internal override void OnSessionStart(bool asServer)
        {
            ModCharacters.Load();
            _lastPresenceSignature = "";
            _nextPresenceCheck = 0f;
            _swapArmedFor = null;
        }

        internal override void OnSessionEnd()
        {
            DespawnAll();
            _present.Clear();
            _placedFor = new Vector3(float.NaN, 0f, 0f);
        }

        internal override void Tick()
        {
            if (IsServer)
            {
                HostTickPresence();
                HostTickFeeding();
            }
            ClientTickPlacement();
            ClientTickCharacters();
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
            List<string> lines = BuildLines(npc, engine, qm, conn);
            ModNet.SendTo(conn, Msg.DialogueOpen, w =>
            {
                w.Write(npc.Id);
                ModNet.WriteStringList(w, lines);
            });
        }

        /// <summary>
        /// What a character says now, as lines to click through: a job if they have one (listening is
        /// taking it on), a reminder with progress if a job of theirs is running, the wrap-up line
        /// once after finishing a job, otherwise idle chatter.
        /// </summary>
        private List<string> BuildLines(NpcDef npc, QuestEngine engine, QuestModule qm, NetworkConnection conn)
        {
            var lines = new List<string>();

            foreach (QuestDef q in engine.Definitions)
            {
                if (!string.Equals(q.Giver, npc.Name, StringComparison.Ordinal)) continue;
                if (engine.Progress(q.Id).Status != QuestStatus.Available) continue;

                AddLines(lines, q.Offer);
                if (qm.HostAccept(q.Id))
                {
                    Diag.Info("Npcs: '" + q.Id + "' taken on by talking to " + npc.Name + ".");
                    _nextPresenceCheck = 0f; // refresh markers now
                }
                QuestProgress now = engine.Progress(q.Id);
                if (now.Status == QuestStatus.Active) lines.Add(StepLine(q, now));
                return lines;
            }

            foreach (QuestDef q in engine.Definitions)
            {
                if (!string.Equals(q.Giver, npc.Name, StringComparison.Ordinal)) continue;
                QuestProgress p = engine.Progress(q.Id);
                if (p.Status != QuestStatus.Active) continue;

                AddLines(lines, string.IsNullOrEmpty(q.ActiveText) ? q.Summary : q.ActiveText);
                lines.Add(StepLine(q, p));
                return lines;
            }

            // Wrap-up line, once per quest, remembered in the save so it is not repeated forever.
            foreach (QuestDef q in engine.Definitions)
            {
                if (!string.Equals(q.Giver, npc.Name, StringComparison.Ordinal)) continue;
                if (engine.Progress(q.Id).Status != QuestStatus.Completed) continue;
                if (!ClaimWrapUp(npc, q)) continue;

                AddLines(lines, q.Done);
                return lines;
            }

            // The shipwright swaps the boat between its old hull and the captured pirate ship. There
            // is no menu: he offers, and talking to him again straight away is the yes.
            if (npc.Id == "mako" && SharedState.Has(PirateStory.UnlockPirateShip))
            {
                bool fitted = !SharedState.Has(PirateHull.UnlockDisabled);
                if (_swapArmedFor == conn && Time.time < _swapArmedUntil)
                {
                    _swapArmedFor = null;
                    if (fitted) SharedState.Grant(PirateHull.UnlockDisabled);
                    else SharedState.Revoke(PirateHull.UnlockDisabled);
                    PirateModule.Announce(fitted ? "Mako strips the pirate hull off. Your old boat is back."
                                                 : "Mako rigs the Greedy Gull's hull onto your boat. She's yours to sail.");
                    lines.Add(fitted ? "There. Your old tub, good as it ever was. Which isn't very." :
                                       "Done. The Greedy Gull rides again. Try not to sink her twice.");
                    return lines;
                }

                AddLines(lines, RandomBark(npc, engine));
                lines.Add(fitted ? "Want your old boat back? Talk to me again and I'll swap the hulls."
                                 : "Want the Greedy Gull's hull back on? Talk to me again and I'll rig it.");
                _swapArmedFor = conn;
                _swapArmedUntil = Time.time + 30f;
                return lines;
            }

            AddLines(lines, RandomBark(npc, engine));
            return lines;
        }

        private static string RandomBark(NpcDef npc, QuestEngine engine)
        {
            List<string> barks = StoryNpcs.CurrentBarks(npc, engine);
            return barks.Count > 0 ? barks[UnityEngine.Random.Range(0, barks.Count)] : "...";
        }

        /// <summary>The job itself in brackets, with a counter for "N of something" jobs, like the game's "1/3".</summary>
        private static string StepLine(QuestDef q, QuestProgress p)
        {
            QuestStep step = q.StepAt(p.StepIndex);
            if (step == null) return "";
            string line = "(" + step.Text + ")";
            if (Countable(step)) line += "\n" + p.Counter + "/" + step.Objective.Amount;
            return line;
        }

        private static bool Countable(QuestStep step) =>
            step.Objective != null && step.Objective.Amount > 1 &&
            (step.Objective.Kind == ObjectiveKind.Kill || step.Objective.Kind == ObjectiveKind.Deliver ||
             step.Objective.Kind == ObjectiveKind.Collect);

        /// <summary>One line per sentence break in the text; each is one press of the interact key.</summary>
        private static void AddLines(List<string> lines, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (string part in text.Split('\n'))
            {
                string t = part.Trim();
                if (t.Length > 0) lines.Add(t);
            }
        }

        /// <summary>True the first time a character's wrap-up for a quest is due; persisted.</summary>
        private static bool ClaimWrapUp(NpcDef npc, QuestDef q)
        {
            string key = "npc." + npc.Id + ".done." + q.Id;
            if (ModSave.Counter(key) > 0) return false;
            ModSave.SetCounter(key, 1);
            return true;
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

        // ------------------------------------------------------------------ host: feeding

        /// <summary>
        /// Like the game's NPCs: an item a player has handled, lying or flying loose at a character
        /// who wants it, gets eaten and counts. Only items some running job asks this character for
        /// are touched, so nothing else ever disappears.
        /// </summary>
        private void HostTickFeeding()
        {
            if (Time.time < _nextFeedCheck) return;
            _nextFeedCheck = Time.time + 0.15f;

            QuestModule qm = QuestModule.Instance;
            QuestEngine engine = qm?.Engine;
            if (engine == null || ItemManager.Instance == null) return;

            foreach (Instance inst in _spawned.Values)
            {
                if (inst.Go == null || WantedFrom(engine, inst.Def.Name, null) == null) continue;

                // Feet to head: a gull tossed at his face and one dropped at his boots both count.
                Vector3 feet = inst.Go.transform.position;
                Collider[] hits = Physics.OverlapCapsule(feet + Vector3.up * 0.1f, feet + Vector3.up * 1.9f,
                                                         Mathf.Max(0.3f, _feedRadius.Value), ~0, QueryTriggerInteraction.Ignore);
                foreach (Collider col in hits)
                {
                    Item item = ItemManager.Get(col);
                    if (!Edible(item)) continue;

                    string key = QuestModule.CreatureKey(item.Creature != null ? (Component)item.Creature : item);
                    QuestDef quest = WantedFrom(engine, inst.Def.Name, key);
                    if (quest == null) continue;

                    Feed(inst, item, key, quest, qm, engine);
                    break;   // one per character per check, as the game's mouth does
                }
            }
        }

        private static bool Edible(Item item)
        {
            if (item == null || item.IsDestroying || item.IsDeinitializing || !item.IsInteractable) return false;
            if (item.Holder != null || item.LastHolder == null || !item.HasBeenHeld) return false;   // must have been handed over
            if (item.Creature != null && !item.Creature.IsDead) return false;                     // no live ones
            return true;
        }

        /// <summary>The running quest whose current step asks for this item (or any item, key null) for this character.</summary>
        private static QuestDef WantedFrom(QuestEngine engine, string npcName, string itemKey)
        {
            foreach (QuestDef q in engine.Definitions)
            {
                QuestProgress p = engine.Progress(q.Id);
                if (p.Status != QuestStatus.Active) continue;
                QuestStep step = q.StepAt(p.StepIndex);
                if (step?.Objective == null || step.Objective.Kind != ObjectiveKind.Deliver) continue;

                string k = step.Objective.Key;
                int bar = k.IndexOf('|');
                if (bar < 0 || !string.Equals(k.Substring(0, bar), npcName, StringComparison.OrdinalIgnoreCase)) continue;
                if (itemKey == null) return q;

                string wanted = k.Substring(bar + 1);
                if (wanted == "*" || string.Equals(wanted, itemKey, StringComparison.OrdinalIgnoreCase)) return q;
            }
            return null;
        }

        private void Feed(Instance inst, Item item, string key, QuestDef quest, QuestModule qm, QuestEngine engine)
        {
            int stepBefore = engine.Progress(quest.Id).StepIndex;
            QuestStep step = quest.StepAt(stepBefore);

            item.DestroyItem((byte)DestroyReason.NPC, (byte)(GameIdBase + inst.Def.Slot));
            qm.Raise(QuestEvent.Delivered(inst.Def.Name, key));

            QuestProgress p = engine.Progress(quest.Id);
            string say;
            if (p.Status == QuestStatus.Completed)
            {
                ClaimWrapUp(inst.Def, quest);
                say = quest.Done;
            }
            else if (p.StepIndex != stepBefore)
            {
                say = !string.IsNullOrEmpty(step?.OnComplete) ? step.OnComplete : "That'll do.";
            }
            else
            {
                say = EatLines[UnityEngine.Random.Range(0, EatLines.Length)] +
                      "\n" + p.Counter + "/" + (step != null ? step.Objective.Amount : 1);
            }

            Diag.Info("Npcs: " + inst.Def.Name + " ate a " + key + " for '" + quest.Id + "' (" +
                      (p.Status == QuestStatus.Completed ? "done" : p.Counter + "/" + (step != null ? step.Objective.Amount : 1)) + ").");
            _nextPresenceCheck = 0f;

            string id = inst.Def.Id;
            ModNet.SendToAll(Msg.NpcSay, w => { w.Write(id); w.Write(say); w.Write(true); });
        }

        /// <summary>Where an item fed to a story character flies to, by the game id it was destroyed with.</summary>
        internal static bool TryMouthForGameId(byte gameId, out Vector3 mouth)
        {
            mouth = Vector3.zero;
            if (_self == null || gameId < GameIdBase) return false;
            int slot = gameId - GameIdBase;
            foreach (Instance inst in _self._spawned.Values)
            {
                if (inst.Def.Slot != slot || inst.Go == null) continue;
                mouth = NpcBody.MouthOf(inst.Go, inst.Height);
                return true;
            }
            return false;
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

            // The answer to my own talk: lines to click through, first one shown now.
            ModNet.OnClient(Msg.DialogueOpen, r =>
            {
                string npcId = r.ReadString();
                List<string> lines = ModNet.ReadStringList(r);
                Instance inst;
                if (!_spawned.TryGetValue(npcId, out inst) || lines.Count == 0) return;

                inst.Pending.Clear();
                inst.Pending.AddRange(lines);
                inst.PendingIndex = 0;
                Animate(inst, "Wave", false);
                ShowNextLine(inst);
            });

            // Said out loud to whoever is nearby, e.g. after being fed.
            ModNet.OnClient(Msg.NpcSay, r =>
            {
                string npcId = r.ReadString();
                string text = r.ReadString();
                bool ate = r.ReadBoolean();
                Instance inst;
                if (!_spawned.TryGetValue(npcId, out inst) || inst.Go == null) return;

                if (ate) PlayEatEffects(inst);

                Player me = Player.LocalPlayer;
                if (me == null || Vector3.Distance(me.Transform.position, inst.Go.transform.position) > 10f) return;
                inst.Pending.Clear();
                inst.PendingIndex = 0;
                Say(inst, text);
            });

            ModNet.OnServer(Msg.RequestTalk, (conn, r) => HostHandleTalk(conn, r.ReadString()));

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

            float height = NpcBody.HeightOf(go);
            StoryNpcInteractable talk;
            Transform bubble = NpcBody.AddTalkPoint(go, def.Id, height, out talk);

            var inst = new Instance { Def = def, Go = go, Bubble = bubble, Height = height, Marker = marker, Voice = NpcVoice.Attach(go) };
            _spawned[def.Id] = inst;
            Animate(inst, "Idle", true);
            Diag.Info("Npcs: " + def.Name + " placed at " + pos.ToString("F1") + (talk != null ? "" : " (cannot be talked to)") + ".");
        }

        private void Despawn(string id)
        {
            Instance inst;
            if (!_spawned.TryGetValue(id, out inst)) return;
            if (inst.Go != null) UnityEngine.Object.Destroy(inst.Go);
            _spawned.Remove(id);
        }

        private void DespawnAll()
        {
            foreach (Instance inst in _spawned.Values) if (inst.Go != null) UnityEngine.Object.Destroy(inst.Go);
            _spawned.Clear();
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

        // ------------------------------------------------------------------ client: talking

        private void ClientTickCharacters()
        {
            Player me = Player.LocalPlayer;
            foreach (Instance inst in _spawned.Values)
            {
                if (inst.ResumeIdleAt > 0f && Time.time >= inst.ResumeIdleAt) Animate(inst, "Idle", true);
                if (inst.Go == null || me == null) continue;

                float d = Vector3.Distance(me.Transform.position, inst.Go.transform.position);

                // Characters glance at whoever is closest to them.
                if (d < 6f)
                {
                    Vector3 look = Flat(me.Transform.position - inst.Go.transform.position);
                    inst.Go.transform.rotation = Quaternion.Slerp(inst.Go.transform.rotation,
                        Quaternion.LookRotation(look, Vector3.up), Time.deltaTime * 3f);
                }

                // Walking off, or leaving it too long, drops the rest of the conversation.
                if (inst.Pending.Count > 0 && (d > _talkRange.Value * 3f || Time.time > inst.PendingExpires))
                {
                    inst.Pending.Clear();
                    inst.PendingIndex = 0;
                }
            }
        }

        /// <summary>The game's interact key on a story character: next line, or ask the host what they say.</summary>
        private void OnTalkPressed(string npcId)
        {
            if (Time.time - _lastTalkPress < 0.2f) return;
            _lastTalkPress = Time.time;

            Instance inst;
            if (!_spawned.TryGetValue(npcId, out inst)) return;

            if (inst.PendingIndex < inst.Pending.Count)
            {
                ShowNextLine(inst);
                return;
            }

            inst.Pending.Clear();
            inst.PendingIndex = 0;
            ModNet.SendToServer(Msg.RequestTalk, w => w.Write(npcId));
        }

        private void ShowNextLine(Instance inst)
        {
            if (inst.PendingIndex >= inst.Pending.Count) return;
            Say(inst, inst.Pending[inst.PendingIndex++]);
            inst.PendingExpires = Time.time + 30f;
        }

        /// <summary>The game's own speech bubble over the character's head, with their mumble.</summary>
        private static void Say(Instance inst, string text)
        {
            if (inst?.Go == null || string.IsNullOrEmpty(text)) return;
            try
            {
                NpcBody.ShowBubble(text, inst.Bubble != null ? inst.Bubble : inst.Go.transform, _textSeconds);
            }
            catch (Exception e)
            {
                Diag.Exception("Npcs: speech bubble", e);
            }
            inst.TalkingUntil = Time.time + _textSeconds;
            inst.Voice?.Speak(text);
        }

        private static void PlayEatEffects(Instance inst)
        {
            Vector3 mouth = NpcBody.MouthOf(inst.Go, inst.Height);
            try { AudioManager.PlayClipAt("Swallow", mouth, true, AudioDistance.Short, 1f, 1f); } catch { }
            try { ParticleManager.Play("Spit", mouth, inst.Go.transform.forward); } catch { }
            Animate(inst, "Yes", false);
        }

        // ------------------------------------------------------------------ UI

        private GUIStyle _mark;

        internal override void OnGUI()
        {
            if (_mark == null)
                _mark = new GUIStyle(GUI.skin.label) { fontSize = 34, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            DrawMarkers();
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

                // The "!" and the speech bubble share the space over the head: while they talk,
                // or a conversation is still waiting to be clicked through, the marker steps aside.
                if (Time.time < inst.TalkingUntil || inst.PendingIndex < inst.Pending.Count) continue;

                Vector3 head = inst.Go.transform.position + Vector3.up * (inst.Height + 0.45f);
                Vector3 sp = cam.WorldToScreenPoint(head);
                if (sp.z <= 0f || sp.z > 60f) continue;

                _mark.normal.textColor = inst.Marker == Marker.Offer ? new Color(1f, 0.85f, 0.2f) : new Color(0.75f, 0.75f, 0.75f);
                float bob = Mathf.Sin(Time.time * 3f) * 4f;
                GUI.Label(new Rect(sp.x - 20f, Screen.height - sp.y - 30f + bob, 40f, 40f),
                          inst.Marker == Marker.Offer ? "!" : "?", _mark);
            }
        }

        // ------------------------------------------------------------------ debug

        internal override void OnDebugKey()
        {
            foreach (Instance inst in _spawned.Values)
                Diag.Info("Npcs: " + inst.Def.Name + " at " + (inst.Go != null ? inst.Go.transform.position.ToString("F1") : "?") +
                          ", marker " + inst.Marker + ", " + (inst.Pending.Count - inst.PendingIndex) + " line(s) pending");
            if (_spawned.Count == 0) Diag.Info("Npcs: none placed (present: " + _present.Count + ", characters bundle " + ModCharacters.Available + ").");
        }

        internal override string StatusLine()
        {
            if (!IsEnabled) return "disabled";
            if (!ModCharacters.Available) return "no character bundle";
            return _spawned.Count + " placed of " + _present.Count + " present";
        }
    }
}
