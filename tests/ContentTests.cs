using System;
using System.Collections.Generic;
using System.Linq;
using Expanded.Content;
using Expanded.Pirates;
using Expanded.Quests;

namespace Expanded.Tests
{
    /// <summary>
    /// Validates the story content and the cannon maths. Content bugs - a quest nobody can give, a
    /// flag nothing raises - never crash; they silently strand the player. These tests catch them.
    /// </summary>
    internal static class ContentTests
    {
        private static int _passed, _failed;

        private static void Check(bool ok, string what)
        {
            if (ok) { _passed++; return; }
            _failed++;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("  FAIL  " + what);
            Console.ResetColor();
        }

        private static void Section(string s) { Console.WriteLine(); Console.WriteLine("== " + s); }

        private static QuestEngine Story()
        {
            var e = new QuestEngine();
            PirateStory.Register(e);
            return e;
        }

        // ------------------------------------------------------------------ content integrity

        private static void EveryQuestHasAGiver()
        {
            Section("every quest is offered by an existing character");
            var names = new HashSet<string>(StoryNpcs.All.Select(n => n.Name));
            foreach (QuestDef q in Story().Definitions)
                Check(names.Contains(q.Giver), "quest '" + q.Id + "' giver '" + q.Giver + "' exists in StoryNpcs");
        }

        private static void CastIsWellFormed()
        {
            Section("cast is well formed");
            var ids = new HashSet<string>();
            var slots = new HashSet<int>();
            foreach (NpcDef n in StoryNpcs.All)
            {
                Check(!string.IsNullOrEmpty(n.Id) && ids.Add(n.Id), "npc id '" + n.Id + "' is unique and non-empty");
                Check(slots.Add(n.Slot), "npc '" + n.Id + "' has its own placement slot");
                Check(!string.IsNullOrEmpty(n.Model), "npc '" + n.Id + "' has a model");
                Check(n.Barks.Count > 0, "npc '" + n.Id + "' has something to say");
            }
        }

        /// <summary>Every flag the story waits on must be raised by something, or that content is unreachable.</summary>
        private static void EveryRequiredFlagIsProduced()
        {
            Section("every flag the story waits on is produced somewhere");
            QuestEngine e = Story();

            var produced = new HashSet<string>
            {
                // Raised by modules rather than quests.
                PirateStory.FlagSwarmDefeated,
                PirateStory.FlagPiratesBeaten
            };
            foreach (QuestDef q in e.Definitions)
            {
                produced.Add(q.Id + ".done");
                foreach (Reward r in q.Rewards) if (r.Kind == RewardKind.Flag) produced.Add(r.Key);
            }

            foreach (QuestDef q in e.Definitions)
                foreach (string f in q.Requires)
                    Check(produced.Contains(f), "flag '" + f + "' required by '" + q.Id + "' is produced");

            foreach (NpcDef n in StoryNpcs.All)
                if (n.AppearsAfter != null)
                    Check(produced.Contains(n.AppearsAfter), "flag '" + n.AppearsAfter + "' that brings in '" + n.Id + "' is produced");
        }

        private static void DialogueNeverEmpty()
        {
            Section("dialogue always has text");
            foreach (QuestDef q in Story().Definitions)
            {
                Check(!string.IsNullOrWhiteSpace(q.Offer), "'" + q.Id + "' has an offer line");
                Check(!string.IsNullOrWhiteSpace(q.Reminder(0)), "'" + q.Id + "' has a reminder line");
                Check(!string.IsNullOrWhiteSpace(q.Done), "'" + q.Id + "' has a wrap-up line");
            }
        }

        // ------------------------------------------------------------------ play-through

        /// <summary>Plays Act 1 start to finish through the engine exactly as the game would drive it.</summary>
        private static void ActOnePlaysThrough()
        {
            Section("act 1 plays through end to end");
            QuestEngine e = Story();
            e.CurrentIsland = 1;
            e.RefreshAvailability();

            Check(e.Progress(PirateStory.QuestOmens).Status == QuestStatus.Available, "opening quest offered at start");
            Check(e.Progress(PirateStory.QuestPirates).Status == QuestStatus.Locked, "finale locked at start");
            Check(!StoryNpcs.IsPresent(StoryNpcs.ById("anne"), e), "Anne not present yet");

            Check(e.Accept(PirateStory.QuestOmens), "accept Bad Omens");
            for (int i = 0; i < 3; i++) e.Handle(QuestEvent.Killed("seagull"));
            Check(e.Progress(PirateStory.QuestOmens).Status == QuestStatus.Completed, "three gulls complete Bad Omens");
            Check(e.Progress(PirateStory.QuestFlock).Status == QuestStatus.Available, "The Flock Breaks offered");
            Check(e.Progress(PirateStory.QuestBigFish).Status == QuestStatus.Available, "side quest offered alongside");

            Check(e.Accept(PirateStory.QuestFlock), "accept The Flock Breaks");
            e.SetFlag(PirateStory.FlagSwarmDefeated);
            Check(e.Progress(PirateStory.QuestFlock).Status == QuestStatus.Completed, "beating the swarm completes it");
            Check(e.HasFlag(PirateStory.FlagChart), "the chart flag is raised");
            Check(StoryNpcs.IsPresent(StoryNpcs.ById("anne"), e), "Anne appears once the chart turns up");
            Check(e.Progress(PirateStory.QuestPirates).Status == QuestStatus.Available, "finale offered");

            Check(e.Accept(PirateStory.QuestPirates), "accept Colours at Dawn");
            List<QuestOutcome> outcomes = e.SetFlag(PirateStory.FlagPiratesBeaten);
            Check(e.Progress(PirateStory.QuestPirates).Status == QuestStatus.Completed, "sinking the ship completes act 1");
            Check(outcomes.Any(o => o.Kind == QuestOutcome.Type.RewardGranted && o.Reward.Key == PirateStory.UnlockPirateShip),
                  "the pirate refit is granted");
            Check(outcomes.Any(o => o.Kind == QuestOutcome.Type.RewardGranted && o.Reward.Key == PirateStory.UnlockCannon),
                  "the cannon is granted");
            Check(StoryNpcs.IsPresent(StoryNpcs.ById("mako"), e), "Mako the shipwright appears afterwards");
        }

        /// <summary>Doing things early must never break the chain later.</summary>
        private static void OutOfOrderIsSafe()
        {
            Section("out-of-order events are harmless");
            QuestEngine e = Story();
            e.RefreshAvailability();

            // Beating the swarm before being asked to must not complete a quest you never took...
            e.SetFlag(PirateStory.FlagSwarmDefeated);
            Check(e.Progress(PirateStory.QuestFlock).Status != QuestStatus.Completed, "unaccepted quest not completed by early flag");

            // ...but the flag is remembered, so the quest completes the moment it is taken on.
            e.Accept(PirateStory.QuestOmens);
            for (int i = 0; i < 3; i++) e.Handle(QuestEvent.Killed("seagull"));
            e.Accept(PirateStory.QuestFlock);
            Check(e.HasFlag(PirateStory.FlagSwarmDefeated), "earlier victory still recorded");
        }

        // ------------------------------------------------------------------ ballistics

        private static void BallisticsHitsWhatItAimsAt()
        {
            Section("cannon ballistics");
            const double g = 9.81, v = 42.0;

            foreach (double[] tgt in new[] { new[] { 20.0, 0.0 }, new[] { 35.0, -2.0 }, new[] { 60.0, 1.5 }, new[] { 120.0, 0.0 } })
            {
                double dx = tgt[0], dy = tgt[1], angle;
                bool ok = Ballistics.SolveLowArc(dx, dy, v, g, out angle);
                Check(ok, "solution exists for " + dx + "m / " + dy + "m");
                double t = Ballistics.FlightTime(dx, v, angle);
                double h = Ballistics.HeightAt(t, v, angle, g);
                Check(Math.Abs(h - dy) < 0.05, "shot at " + dx + "m lands within 5 cm of the target height (off by " + (h - dy).ToString("0.000") + ")");
                Check(angle < Math.PI / 4, "low arc chosen for " + dx + "m (flat broadside, not a lob)");
            }

            double far;
            Check(!Ballistics.SolveLowArc(500, 0, v, g, out far), "500 m is out of range at 42 m/s");
            Check(Math.Abs(far - Math.PI / 4) < 1e-9, "out of range falls back to 45 degrees for maximum reach");

            double up;
            Check(Ballistics.SolveLowArc(0, 10, v, g, out up) && Math.Abs(up - Math.PI / 2) < 1e-9, "target straight above aims straight up");
            double bad;
            Check(!Ballistics.SolveLowArc(10, 0, 0, g, out bad), "zero muzzle velocity is rejected");
        }

        internal static int Run()
        {
            Console.WriteLine();
            Console.WriteLine("Content and ballistics tests");
            EveryQuestHasAGiver();
            CastIsWellFormed();
            EveryRequiredFlagIsProduced();
            DialogueNeverEmpty();
            ActOnePlaysThrough();
            OutOfOrderIsSafe();
            BallisticsHitsWhatItAimsAt();

            Console.WriteLine();
            Console.ForegroundColor = _failed == 0 ? ConsoleColor.Green : ConsoleColor.Red;
            Console.WriteLine(_passed + " passed, " + _failed + (_failed == 0 ? " failed." : " FAILED."));
            Console.ResetColor();
            return _failed == 0 ? 0 : 1;
        }
    }
}
