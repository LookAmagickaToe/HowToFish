using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using FishNet;
using FishNet.Connection;
using UnityEngine;

namespace Expanded.Quests
{
    /// <summary>
    /// Glue between the pure-C# <see cref="QuestEngine"/> and the running game.
    ///
    /// Authority: the host owns the one engine instance and is the only machine that advances
    /// progress or grants rewards. Clients hold a read-only copy for display, refreshed by snapshot.
    /// Every client action (accepting a quest, talking) is a request the host validates.
    /// </summary>
    internal sealed class QuestModule : ModuleBase
    {
        internal static QuestModule Instance { get; private set; }

        internal override string Id => "Quests";
        internal override string DisplayName => "Quests & Story";
        internal override KeyCode DefaultDebugKey => KeyCode.F3;
        internal override Type[] PatchTypes => new[] { typeof(QuestPatches) };

        private ConfigEntry<bool> _toasts;
        private ConfigEntry<float> _reachSampleSeconds;

        /// <summary>Host only. Null on clients.</summary>
        internal QuestEngine Engine { get; private set; }

        // Client-side view of the journal, refreshed by snapshots.
        private readonly List<string> _journal = new List<string>();
        private float _nextReachSample;
        private int _lastIsland = -1;
        private bool _snapshotDue;

        internal override void Configure(ConfigFile config)
        {
            Instance = this;
            _toasts = config.Bind(Id, "ChatNotifications", true,
                "Announce quest starts, steps and completions in chat.");
            _reachSampleSeconds = config.Bind(Id, "ReachSampleSeconds", 0.5f,
                "How often player positions are checked against 'go to' objectives.");
        }

        internal override void OnEnable()
        {
            Instance = this;
            // Once per game launch: ModNet handlers accumulate, so registering per session would
            // make every message fire once per session played.
            RegisterNetHandlers();
        }

        // ------------------------------------------------------------------ session

        internal override void OnSessionStart(bool asServer)
        {
            if (!asServer)
            {
                Diag.Info("Quests: client mode, waiting for the host's journal.");
                return;
            }

            Engine = new QuestEngine();
            try
            {
                Content.PirateStory.Register(Engine);
            }
            catch (Exception e)
            {
                Diag.Exception("Registering story content", e);
            }

            Engine.LoadState(ModSave.Data.Quests, ModSave.Data.Flags);
            Engine.CurrentIsland = CurrentIsland();
            _lastIsland = Engine.CurrentIsland;

            List<QuestOutcome> offered = Engine.RefreshAvailability();
            Diag.Info("Quests: " + CountByStatus(QuestStatus.Completed) + " done, " +
                      CountByStatus(QuestStatus.Active) + " active, " + offered.Count +
                      " newly offered on island " + Engine.CurrentIsland + ".");

            Apply(offered);
            _snapshotDue = true;
        }

        internal override void OnSessionEnd()
        {
            PersistHostState();
            Engine = null;
            _journal.Clear();
        }

        private int CountByStatus(QuestStatus s)
        {
            int n = 0;
            foreach (QuestDef d in Engine.Definitions)
                if (Engine.Progress(d.Id).Status == s) n++;
            return n;
        }

        // ------------------------------------------------------------------ per-frame

        internal override void Tick()
        {
            if (Engine == null) return; // client: nothing to drive

            try
            {
                int island = CurrentIsland();
                if (island != _lastIsland)
                {
                    _lastIsland = island;
                    Engine.CurrentIsland = island;
                    Diag.Info("Quests: island changed to " + island + ".");
                    Apply(Engine.RefreshAvailability());
                }

                SampleReachObjectives();

                if (_snapshotDue)
                {
                    _snapshotDue = false;
                    BroadcastSnapshot();
                }
            }
            catch (Exception e)
            {
                Diag.Exception("QuestModule.Tick", e);
            }
        }

        /// <summary>Feeds player positions to 'travel to X' objectives.</summary>
        private void SampleReachObjectives()
        {
            if (Time.time < _nextReachSample) return;
            _nextReachSample = Time.time + Mathf.Max(0.1f, _reachSampleSeconds.Value);

            List<Player> alive = PlayerManager.AlivePlayers;
            for (int i = 0; i < alive.Count; i++)
            {
                Player p = alive[i];
                if (p == null) continue;
                Vector3 pos = p.Transform.position;
                Raise(QuestEvent.Moved(_lastIsland, pos.x, pos.z));
            }
        }

        private static int CurrentIsland()
        {
            try { return OnlineIslandManager.CurIsland; }
            catch { return 0; }
        }

        // ------------------------------------------------------------------ host API

        /// <summary>Feeds a gameplay event to the engine. Safe to call from anywhere; host-only effect.</summary>
        internal void Raise(QuestEvent ev)
        {
            if (Engine == null || !InstanceFinder.IsServerStarted) return;
            try
            {
                List<QuestOutcome> outcomes = Engine.Handle(ev);
                if (outcomes.Count > 0) Apply(outcomes);
            }
            catch (Exception e)
            {
                Diag.Exception("QuestModule.Raise(" + ev.Kind + " " + ev.Key + ")", e);
            }
        }

        /// <summary>Raises a story flag from module code, e.g. when the pirates are beaten.</summary>
        internal void SetFlag(string flag)
        {
            if (Engine == null || !InstanceFinder.IsServerStarted) return;
            Apply(Engine.SetFlag(flag));
        }

        /// <summary>Host: accept an offered quest on the player's behalf (from dialogue).</summary>
        internal bool HostAccept(string questId)
        {
            if (Engine == null || !InstanceFinder.IsServerStarted) return false;
            var outcomes = new List<QuestOutcome>();
            if (!Engine.Accept(questId, outcomes)) return false;
            Apply(outcomes);
            return true;
        }

        /// <summary>Host: someone spoke to a story character; feeds "talk to X" objectives.</summary>
        internal void RaiseTalk(string npcName) => Raise(QuestEvent.Talked(npcName));

        internal void OnCreatureKilled(Creature creature)
        {
            if (Engine == null || creature == null) return;
            Raise(QuestEvent.Killed(CreatureKey(creature)));
        }

        /// <summary>
        /// Locale-independent identity for a creature: its prefab name, lowercased, minus Unity's
        /// "(Clone)" suffix. Display names are localised and must never be used as quest keys.
        /// </summary>
        internal static string CreatureKey(Component c)
        {
            if (c == null) return "";
            string n = c.gameObject.name;
            int idx = n.IndexOf("(Clone)", StringComparison.Ordinal);
            if (idx >= 0) n = n.Substring(0, idx);
            return n.Trim().ToLowerInvariant();
        }

        // ------------------------------------------------------------------ outcomes

        private void Apply(List<QuestOutcome> outcomes)
        {
            if (outcomes == null || outcomes.Count == 0) return;

            for (int i = 0; i < outcomes.Count; i++)
            {
                QuestOutcome o = outcomes[i];
                switch (o.Kind)
                {
                    case QuestOutcome.Type.Offered:
                        QuestDef def = Engine.Def(o.QuestId);
                        Announce("New job available" + (def != null && def.Giver.Length > 0 ? " from " + def.Giver : "") +
                                 ": " + o.Text);
                        break;

                    case QuestOutcome.Type.Started:
                        Announce("Objective: " + o.Text);
                        break;

                    case QuestOutcome.Type.StepCompleted:
                        if (!string.IsNullOrEmpty(o.Text)) Announce(o.Text);
                        break;

                    case QuestOutcome.Type.QuestCompleted:
                        Announce("Quest complete: " + o.Text);
                        break;

                    case QuestOutcome.Type.RewardGranted:
                        GrantReward(o.Reward);
                        break;
                }
            }

            PersistHostState();
            _snapshotDue = true;
        }

        private void GrantReward(Reward r)
        {
            if (r == null) return;
            try
            {
                switch (r.Kind)
                {
                    case RewardKind.Money:
                        MoneyManager.AddMoney(r.Amount, Player.LocalPlayer);
                        Announce("Earned " + r.Amount + " coins.");
                        break;

                    case RewardKind.Item:
                        SpawnRewardItem(r.Key, Mathf.Max(1, r.Amount));
                        break;

                    case RewardKind.ShopStock:
                    case RewardKind.Unlock:
                        if (SharedState.Grant(r.Key)) Announce("Unlocked: " + Pretty(r.Key));
                        break;

                    case RewardKind.Flag:
                        // Already applied inside the engine; nothing to do in the world.
                        break;
                }
            }
            catch (Exception e)
            {
                Diag.Exception("GrantReward(" + r.Kind + " " + r.Key + ")", e);
            }
        }

        private void SpawnRewardItem(string itemKey, int count)
        {
            Item prefab = GameInfo.GetSpawnable(itemKey);
            if (prefab == null)
            {
                Diag.Warn("Quest reward item '" + itemKey + "' not found; skipped.");
                return;
            }
            if (ItemManager.Instance == null) return;

            Vector3 at = Player.LocalPlayer != null
                ? Player.LocalPlayer.Transform.position + Vector3.up * 1.2f
                : Vector3.up * 2f;

            for (int i = 0; i < count; i++)
                ItemManager.Instance.SpawnNewItem(prefab, at + UnityEngine.Random.insideUnitSphere * 0.4f,
                                                  Quaternion.identity);

            Announce("Received " + (count > 1 ? count + "x " : "") + Pretty(itemKey) + ".");
        }

        private static string Pretty(string key) =>
            string.IsNullOrEmpty(key) ? "" : key.Replace('.', ' ').Replace('_', ' ');

        private void PersistHostState()
        {
            if (Engine == null) return;
            ModSave.Data.Quests = Engine.SaveState();
            ModSave.Data.Flags = new List<string>(Engine.Flags);
            ModSave.MarkDirty();
        }

        // ------------------------------------------------------------------ networking

        private void RegisterNetHandlers()
        {
            ModNet.OnClient(Msg.QuestSnapshot, r =>
            {
                _journal.Clear();
                _journal.AddRange(ModNet.ReadStringList(r));
            });

            ModNet.OnClient(Msg.QuestToast, r =>
            {
                string text = r.ReadString();
                ShowToast(text);
            });

            // A late joiner gets the current journal immediately instead of waiting for the next change.
            ModNet.OnServer(Msg.Hello, (conn, r) =>
            {
                if (Engine == null) return;
                List<string> lines = BuildJournal();
                ModNet.SendTo(conn, Msg.QuestSnapshot, w => ModNet.WriteStringList(w, lines));
            });

            ModNet.OnServer(Msg.RequestAccept, (conn, r) =>
            {
                string questId = r.ReadString();
                if (Engine == null) return;

                var outcomes = new List<QuestOutcome>();
                if (Engine.Accept(questId, outcomes)) Apply(outcomes);
                else Diag.Debug("Quests: rejected accept '" + questId + "' from " + Describe(conn) + ".");
            });
        }

        private static string Describe(NetworkConnection c) => c == null ? "?" : "client " + c.ClientId;

        /// <summary>
        /// Host-side announcement. Sent as a broadcast, which the host's own client also receives,
        /// so it appears exactly once on every screen - showing it locally as well would double it
        /// on the host.
        /// </summary>
        private void Announce(string text)
        {
            Diag.Info("[quest] " + text);
            if (InstanceFinder.IsServerStarted) ModNet.SendToAll(Msg.QuestToast, w => w.Write(text ?? ""));
            else ShowToast(text);
        }

        private void ShowToast(string text)
        {
            if (_toasts == null || !_toasts.Value) return;
            try { ChatManager.ChatMessage("<color=#7FD9FF>[Quest]</color> " + text); }
            catch (Exception e) { Diag.Exception("Quest toast", e); }
        }

        private void BroadcastSnapshot()
        {
            if (Engine == null) return;
            List<string> lines = BuildJournal();
            _journal.Clear();
            _journal.AddRange(lines);
            ModNet.SendToAll(Msg.QuestSnapshot, w => ModNet.WriteStringList(w, lines));
        }

        /// <summary>Player-facing journal lines, newest-relevant first.</summary>
        private List<string> BuildJournal()
        {
            var lines = new List<string>();
            foreach (QuestDef d in Engine.Definitions)
            {
                QuestProgress p = Engine.Progress(d.Id);
                switch (p.Status)
                {
                    case QuestStatus.Active:
                        QuestStep step = d.StepAt(p.StepIndex);
                        string progress = step != null && step.Objective.Amount > 1
                            ? " (" + p.Counter + "/" + step.Objective.Amount + ")"
                            : "";
                        lines.Add("* " + d.Title + ": " + (step != null ? step.Text : "?") + progress);
                        break;
                    case QuestStatus.Available:
                        lines.Add("! " + d.Title + " - available from " + d.Giver);
                        break;
                }
            }
            if (lines.Count == 0) lines.Add("(no active quests)");
            return lines;
        }

        // ------------------------------------------------------------------ debug

        internal override void OnDebugKey()
        {
            if (Engine == null)
            {
                ShowToast("Quest journal is host-side; you are a client.");
                foreach (string l in _journal) ShowToast(l);
                return;
            }

            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (!shift)
            {
                foreach (string l in BuildJournal()) ShowToast(l);
                ShowToast("(Shift+" + DebugKeyEntry.Value + " force-completes the first active quest)");
                return;
            }

            foreach (QuestDef d in Engine.Definitions)
            {
                QuestProgress p = Engine.Progress(d.Id);
                if (p.Status != QuestStatus.Active && p.Status != QuestStatus.Available) continue;
                Diag.Warn("Quests: debug force-completing '" + d.Id + "'.");
                Apply(Engine.ForceComplete(d.Id));
                return;
            }
            ShowToast("Nothing to force-complete.");
        }

        internal override string StatusLine()
        {
            if (!IsEnabled) return "disabled";
            if (Engine == null) return _journal.Count > 0 ? "client view: " + _journal.Count + " entries" : "client";

            int active = 0, avail = 0, done = 0;
            foreach (QuestDef d in Engine.Definitions)
            {
                switch (Engine.Progress(d.Id).Status)
                {
                    case QuestStatus.Active: active++; break;
                    case QuestStatus.Available: avail++; break;
                    case QuestStatus.Completed: done++; break;
                }
            }
            return active + " active, " + avail + " offered, " + done + " done";
        }
    }
}
