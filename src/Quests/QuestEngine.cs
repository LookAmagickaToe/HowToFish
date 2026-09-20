using System;
using System.Collections.Generic;

namespace Expanded.Quests
{
    /// <summary>
    /// The progression state machine. Pure C#: no Unity, no networking, no I/O, so it can be
    /// unit-tested headlessly and reasoned about on its own.
    ///
    /// Authority model: only the host runs an engine. Clients get a replicated snapshot for display.
    /// </summary>
    public sealed class QuestEngine
    {
        private readonly Dictionary<string, QuestDef> _defs = new Dictionary<string, QuestDef>(StringComparer.Ordinal);
        private readonly Dictionary<string, QuestProgress> _progress = new Dictionary<string, QuestProgress>(StringComparer.Ordinal);
        private readonly HashSet<string> _flags = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Island the party is currently on; gates which quests become offerable.</summary>
        public int CurrentIsland { get; set; }

        public IEnumerable<QuestDef> Definitions => _defs.Values;
        public IReadOnlyCollection<string> Flags => _flags;

        public void Register(QuestDef def)
        {
            if (def == null) throw new ArgumentNullException(nameof(def));
            if (string.IsNullOrEmpty(def.Id)) throw new ArgumentException("Quest needs an Id.");
            if (def.Steps.Count == 0) throw new ArgumentException("Quest '" + def.Id + "' has no steps.");
            if (_defs.ContainsKey(def.Id)) throw new ArgumentException("Duplicate quest id '" + def.Id + "'.");

            _defs[def.Id] = def;
            if (!_progress.ContainsKey(def.Id))
                _progress[def.Id] = new QuestProgress { Id = def.Id, Status = QuestStatus.Locked };
        }

        public QuestDef Def(string id)
        {
            QuestDef d;
            return _defs.TryGetValue(id, out d) ? d : null;
        }

        public QuestProgress Progress(string id)
        {
            QuestProgress p;
            return _progress.TryGetValue(id, out p) ? p : null;
        }

        public bool HasFlag(string flag) => _flags.Contains(flag);

        // ------------------------------------------------------------------ persistence

        /// <summary>Replaces all progress, e.g. when loading a save. Unknown quest ids are dropped.</summary>
        public void LoadState(IEnumerable<QuestProgress> saved, IEnumerable<string> flags)
        {
            _flags.Clear();
            if (flags != null)
                foreach (string f in flags)
                    if (!string.IsNullOrEmpty(f)) _flags.Add(f);

            foreach (QuestProgress p in _progress.Values)
            {
                p.Status = QuestStatus.Locked;
                p.StepIndex = 0;
                p.Counter = 0;
            }

            if (saved != null)
            {
                foreach (QuestProgress s in saved)
                {
                    if (s == null || string.IsNullOrEmpty(s.Id)) continue;
                    QuestProgress p;
                    if (!_progress.TryGetValue(s.Id, out p)) continue; // quest removed in a newer version

                    QuestDef def = _defs[s.Id];
                    p.Status = s.Status;
                    // Clamp against the current definition: steps may have changed between versions.
                    p.StepIndex = Clamp(s.StepIndex, 0, Math.Max(0, def.Steps.Count - 1));
                    p.Counter = Math.Max(0, s.Counter);
                    if (p.Status == QuestStatus.Completed) { p.StepIndex = def.Steps.Count; p.Counter = 0; }
                }
            }
        }

        public List<QuestProgress> SaveState()
        {
            var list = new List<QuestProgress>(_progress.Count);
            foreach (QuestProgress p in _progress.Values)
                list.Add(new QuestProgress { Id = p.Id, Status = p.Status, StepIndex = p.StepIndex, Counter = p.Counter });
            return list;
        }

        // ------------------------------------------------------------------ driving

        /// <summary>
        /// Recomputes which locked quests are now offerable. Call after flags change, after an
        /// island change, and once after loading. Returns any newly offered quests.
        /// </summary>
        public List<QuestOutcome> RefreshAvailability()
        {
            var outcomes = new List<QuestOutcome>();
            foreach (QuestDef def in _defs.Values)
            {
                QuestProgress p = _progress[def.Id];
                if (p.Status != QuestStatus.Locked) continue;
                if (def.Island != 0 && def.Island != CurrentIsland) continue;
                if (!RequirementsMet(def)) continue;

                p.Status = QuestStatus.Available;
                outcomes.Add(new QuestOutcome
                {
                    Kind = QuestOutcome.Type.Offered,
                    QuestId = def.Id,
                    Text = def.Title
                });
            }
            return outcomes;
        }

        private bool RequirementsMet(QuestDef def)
        {
            for (int i = 0; i < def.Requires.Count; i++)
                if (!_flags.Contains(def.Requires[i])) return false;
            return true;
        }

        /// <summary>Accepts an offered quest. Returns false if it was not offerable.</summary>
        public bool Accept(string questId, List<QuestOutcome> outcomes = null)
        {
            QuestProgress p = Progress(questId);
            if (p == null || p.Status != QuestStatus.Available) return false;

            p.Status = QuestStatus.Active;
            p.StepIndex = 0;
            p.Counter = 0;

            outcomes?.Add(new QuestOutcome
            {
                Kind = QuestOutcome.Type.Started,
                QuestId = questId,
                Text = _defs[questId].Steps[0].Text
            });
            return true;
        }

        /// <summary>
        /// Feeds a gameplay event to every active quest. Returns everything the game side must
        /// react to: step completions, quest completions and rewards, in order.
        /// </summary>
        public List<QuestOutcome> Handle(QuestEvent ev)
        {
            var outcomes = new List<QuestOutcome>();

            // Copy: granting rewards can raise flags, which can offer further quests.
            var activeIds = new List<string>();
            foreach (var kv in _progress)
                if (kv.Value.Status == QuestStatus.Active) activeIds.Add(kv.Key);

            for (int i = 0; i < activeIds.Count; i++)
                Advance(activeIds[i], ev, outcomes);

            if (outcomes.Count > 0) outcomes.AddRange(RefreshAvailability());
            return outcomes;
        }

        private void Advance(string questId, QuestEvent ev, List<QuestOutcome> outcomes)
        {
            QuestProgress p = _progress[questId];
            QuestDef def = _defs[questId];
            QuestStep step = def.StepAt(p.StepIndex);
            if (step == null || step.Objective == null) return;

            int gain = Match(step.Objective, ev);
            if (gain <= 0) return;

            p.Counter += gain;
            if (p.Counter < step.Objective.Amount) return;

            // Step done.
            p.Counter = 0;
            p.StepIndex++;
            outcomes.Add(new QuestOutcome
            {
                Kind = QuestOutcome.Type.StepCompleted,
                QuestId = questId,
                Text = step.OnComplete
            });

            if (p.StepIndex < def.Steps.Count)
            {
                outcomes.Add(new QuestOutcome
                {
                    Kind = QuestOutcome.Type.Started,
                    QuestId = questId,
                    Text = def.Steps[p.StepIndex].Text
                });
                return;
            }

            // Quest done: grant rewards, then raise flags so follow-ups can unlock.
            p.Status = QuestStatus.Completed;
            outcomes.Add(new QuestOutcome
            {
                Kind = QuestOutcome.Type.QuestCompleted,
                QuestId = questId,
                Text = def.Title
            });

            for (int i = 0; i < def.Rewards.Count; i++)
            {
                Reward r = def.Rewards[i];
                outcomes.Add(new QuestOutcome
                {
                    Kind = QuestOutcome.Type.RewardGranted,
                    QuestId = questId,
                    Reward = r,
                    Text = r.Key
                });
                if (r.Kind == RewardKind.Flag) _flags.Add(r.Key);
            }

            // Completing a quest always raises "<id>.done" so follow-ups can depend on it.
            _flags.Add(questId + ".done");
        }

        /// <summary>How much progress this event contributes to the objective. 0 = no match.</summary>
        private int Match(Objective o, QuestEvent ev)
        {
            if (o.Kind != ev.Kind) return 0;

            switch (o.Kind)
            {
                case ObjectiveKind.Kill:
                case ObjectiveKind.Collect:
                case ObjectiveKind.Deliver:
                    if (o.Key != "*" && !KeyEquals(o.Key, ev.Key)) return 0;
                    return Math.Max(0, ev.Amount);

                case ObjectiveKind.Talk:
                case ObjectiveKind.Flag:
                    return KeyEquals(o.Key, ev.Key) ? 1 : 0;

                case ObjectiveKind.CatchWeight:
                    // Amount carries the weight, not a count: one qualifying catch completes it.
                    if (o.Key != "*" && !KeyEquals(o.Key, ev.Key)) return 0;
                    return ev.Amount >= o.Amount ? o.Amount : 0;

                case ObjectiveKind.Reach:
                    if (o.Island != 0 && o.Island != ev.Island) return 0;
                    float dx = ev.X - o.X, dz = ev.Z - o.Z;
                    return (dx * dx + dz * dz) <= o.Radius * o.Radius ? 1 : 0;

                default:
                    return 0;
            }
        }

        private static bool KeyEquals(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>Raises a story flag directly (module scripting) and re-checks availability.</summary>
        public List<QuestOutcome> SetFlag(string flag)
        {
            var outcomes = new List<QuestOutcome>();
            if (string.IsNullOrEmpty(flag) || !_flags.Add(flag)) return outcomes;

            outcomes.AddRange(Handle(QuestEvent.Flagged(flag)));
            outcomes.AddRange(RefreshAvailability());
            return outcomes;
        }

        /// <summary>Debug/testing: force a quest to completion, granting its rewards.</summary>
        public List<QuestOutcome> ForceComplete(string questId)
        {
            var outcomes = new List<QuestOutcome>();
            QuestProgress p = Progress(questId);
            QuestDef def = Def(questId);
            if (p == null || def == null || p.Status == QuestStatus.Completed) return outcomes;

            if (p.Status != QuestStatus.Active)
            {
                p.Status = QuestStatus.Available;
                Accept(questId, outcomes);
            }

            // Walk the remaining steps deterministically rather than duplicating completion logic.
            int guard = 0;
            while (p.Status == QuestStatus.Active && guard++ < 128)
            {
                QuestStep step = def.StepAt(p.StepIndex);
                if (step == null) break;
                p.Counter = 0;
                Advance(questId, Synthetic(step.Objective), outcomes);
            }
            outcomes.AddRange(RefreshAvailability());
            return outcomes;
        }

        /// <summary>An event guaranteed to satisfy the given objective, used by ForceComplete.</summary>
        private static QuestEvent Synthetic(Objective o)
        {
            var ev = new QuestEvent { Kind = o.Kind, Key = o.Key, Amount = Math.Max(1, o.Amount) };
            if (o.Kind == ObjectiveKind.Reach) { ev.Island = o.Island; ev.X = o.X; ev.Z = o.Z; }
            return ev;
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
