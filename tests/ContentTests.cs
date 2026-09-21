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

        private static void Eq<T>(T actual, T expected, string what) =>
            Check(Equals(actual, expected), what + " (expected " + expected + ", got " + actual + ")");

        private static QuestEngine Story()
        {
            var e = new QuestEngine();
            PirateStory.Register(e);
            MegalodonStory.Register(e);
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
                PirateStory.FlagCannonBought,
                PirateStory.FlagSiteReached,
                PirateStory.FlagPiratesBeaten,
                MegalodonStory.FlagWakeboardBought,
                MegalodonStory.FlagTeethRecovered,
                MegalodonStory.FlagMegalodonBeaten
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
            e.CurrentIsland = 0;
            e.RefreshAvailability();
            Check(e.Progress(PirateStory.QuestOmens).Status == QuestStatus.Locked, "no gull job on the starting island (no gun yet)");
            Check(!StoryNpcs.IsPresent(StoryNpcs.ById("salt"), e), "Old Salt is not on the starting island");

            e.CurrentIsland = 1;
            e.HighestIsland = 1;
            e.RefreshAvailability();

            Check(e.Progress(PirateStory.QuestOmens).Status == QuestStatus.Available, "opening quest offered on the second island");
            Check(StoryNpcs.IsPresent(StoryNpcs.ById("salt"), e), "Old Salt waits on the second island");
            Check(e.Progress(PirateStory.QuestPirates).Status == QuestStatus.Locked, "finale locked at start");
            Check(!StoryNpcs.IsPresent(StoryNpcs.ById("anne"), e), "Anne not present yet");

            Check(e.Accept(PirateStory.QuestOmens), "accept Bad Omens");
            for (int i = 0; i < 3; i++) e.Handle(QuestEvent.Killed("seagull"));
            Check(e.Progress(PirateStory.QuestOmens).Status == QuestStatus.Active, "killing gulls alone is not enough");
            e.Handle(QuestEvent.Delivered(StoryNpcs.Anne, "seagull"));
            Eq(e.Progress(PirateStory.QuestOmens).Counter, 0, "feeding the wrong character does not count");
            e.Handle(QuestEvent.Delivered(StoryNpcs.OldSalt, "seagull"));
            Eq(e.Progress(PirateStory.QuestOmens).Counter, 1, "one gull fed to Old Salt counts 1/3");
            e.Handle(QuestEvent.Delivered(StoryNpcs.OldSalt, "cod"));
            Eq(e.Progress(PirateStory.QuestOmens).Counter, 1, "feeding him a fish does not count");
            for (int i = 0; i < 2; i++) e.Handle(QuestEvent.Delivered(StoryNpcs.OldSalt, "seagull"));
            Check(e.Progress(PirateStory.QuestOmens).Status == QuestStatus.Completed, "three gulls fed complete Bad Omens");
            Check(e.Progress(PirateStory.QuestFlock).Status == QuestStatus.Available, "The Flock Breaks offered");
            Check(e.Progress(PirateStory.QuestBigFish).Status == QuestStatus.Available, "side quest offered alongside");

            Check(e.Accept(PirateStory.QuestFlock), "accept The Flock Breaks");
            e.SetFlag(PirateStory.FlagSwarmDefeated);
            Check(e.Progress(PirateStory.QuestFlock).Status == QuestStatus.Completed, "beating the swarm completes it");
            Check(e.HasFlag(PirateStory.FlagChart), "the chart flag is raised");
            Check(StoryNpcs.IsPresent(StoryNpcs.ById("anne"), e), "Anne appears once the chart turns up");
            Check(e.Progress(PirateStory.QuestPirates).Status == QuestStatus.Locked, "no pirates before the third island");
            Check(StoryNpcs.CurrentBarks(StoryNpcs.ById("anne"), e).Exists(l => l.Contains("third island")),
                  "Anne tells you where the pirates sail from");

            e.CurrentIsland = 2;
            e.HighestIsland = 2;
            e.RefreshAvailability();
            Check(e.Progress(PirateStory.QuestPirates).Status == QuestStatus.Available, "finale offered on the third island");

            e.CurrentIsland = 0;   // sailing back home keeps what was unlocked
            e.RefreshAvailability();
            Check(e.Progress(PirateStory.QuestPirates).Status == QuestStatus.Available, "finale stays offered back home");
            Check(!StoryNpcs.IsPresent(StoryNpcs.ById("anne"), e), "nobody from the story waits on the starting island");
            e.CurrentIsland = 2;

            Check(e.Accept(PirateStory.QuestPirates), "accept Colours at Dawn");
            Check(e.Progress(PirateStory.QuestPirates).StepIndex == 0, "first beat: buy a gun");
            e.SetFlag(PirateStory.FlagCannonBought);
            Check(e.Progress(PirateStory.QuestPirates).StepIndex == 1, "buying the gun moves on to the chart mark");
            e.SetFlag(PirateStory.FlagSiteReached);
            Check(e.Progress(PirateStory.QuestPirates).StepIndex == 2, "reaching the mark starts the fight");

            List<QuestOutcome> outcomes = e.SetFlag(PirateStory.FlagPiratesBeaten);
            Check(e.Progress(PirateStory.QuestPirates).Status == QuestStatus.Completed, "beating the pirates completes act 1");
            Check(outcomes.Any(o => o.Kind == QuestOutcome.Type.RewardGranted && o.Reward.Key == PirateStory.UnlockPirateShip),
                  "the pirate ship is granted");
            Check(!outcomes.Any(o => o.Kind == QuestOutcome.Type.RewardGranted && o.Reward.Key == PirateStory.UnlockCannon),
                  "the cannon is bought, not handed out");
            Check(StoryNpcs.IsPresent(StoryNpcs.ById("mako"), e), "Mako the shipwright appears afterwards");
            Check(!StoryNpcs.CurrentBarks(StoryNpcs.ById("anne"), e).Exists(l => l.Contains("third island")),
                  "Anne stops giving directions once the pirates are beaten");
        }

        /// <summary>Doing things early must never break the chain later.</summary>
        private static void OutOfOrderIsSafe()
        {
            Section("out-of-order events are harmless");
            QuestEngine e = Story();
            e.HighestIsland = 5;
            e.RefreshAvailability();

            // Beating the swarm before being asked to must not complete a quest you never took...
            e.SetFlag(PirateStory.FlagSwarmDefeated);
            Check(e.Progress(PirateStory.QuestFlock).Status != QuestStatus.Completed, "unaccepted quest not completed by early flag");

            // ...but the flag is remembered, so the quest completes the moment it is taken on.
            e.Accept(PirateStory.QuestOmens);
            for (int i = 0; i < 3; i++) e.Handle(QuestEvent.Delivered(StoryNpcs.OldSalt, "seagull"));
            e.Accept(PirateStory.QuestFlock);
            Check(e.Progress(PirateStory.QuestFlock).Status == QuestStatus.Completed,
                  "quest whose goal was already met completes on accept instead of hanging");
            Check(e.HasFlag(PirateStory.FlagChart), "and its rewards still arrive");

            // Buying the gun before Anne asks is the likely case; it must not strand the finale.
            e.SetFlag(PirateStory.FlagCannonBought);
            e.Accept(PirateStory.QuestPirates);
            Check(e.Progress(PirateStory.QuestPirates).StepIndex == 1, "pre-bought gun skips straight to the chart mark");
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
