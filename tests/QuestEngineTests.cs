using System;
using System.Collections.Generic;
using System.Linq;
using Expanded.Quests;

namespace Expanded.Tests
{
    /// <summary>
    /// Headless tests for the quest engine. No Unity, no game, no network: run with tests/run-tests.ps1.
    /// </summary>
    internal static class QuestEngineTests
    {
        private static int _passed, _failed;

        private static void Check(bool condition, string what)
        {
            if (condition) { _passed++; return; }
            _failed++;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("  FAIL  " + what);
            Console.ResetColor();
        }

        private static void Eq<T>(T actual, T expected, string what)
        {
            bool ok = EqualityComparer<T>.Default.Equals(actual, expected);
            if (!ok) what += "  (expected " + expected + ", got " + actual + ")";
            Check(ok, what);
        }

        private static void Section(string name)
        {
            Console.WriteLine();
            Console.WriteLine("== " + name);
        }

        // ------------------------------------------------------------------ fixtures

        private static QuestDef Simple(string id, params QuestStep[] steps)
        {
            var d = new QuestDef { Id = id, Title = id, Giver = "Salty", Summary = "s" };
            d.Steps.AddRange(steps);
            return d;
        }

        private static QuestEngine EngineWithChain()
        {
            var e = new QuestEngine();

            var first = Simple("bottle",
                new QuestStep("Find the bottle", Objective.Collect("bottle", 1), "Got it."),
                new QuestStep("Bring it to Salty", Objective.Deliver("Salty", "bottle", 1), "Thanks."));
            first.Rewards.Add(Reward.Money(250));
            first.Rewards.Add(Reward.Flag("story.bottle"));
            e.Register(first);

            var second = Simple("wreck", new QuestStep("Dive the wreck", Objective.Reach(2, 100f, 50f, 10f)));
            second.Requires.Add("story.bottle");
            second.Island = 2;
            second.Rewards.Add(Reward.Unlock("ship.pirate"));
            e.Register(second);

            return e;
        }

        // ------------------------------------------------------------------ tests

        private static void Registration()
        {
            Section("registration");
            var e = new QuestEngine();
            e.Register(Simple("a", new QuestStep("x", Objective.Talk("n"))));

            Check(e.Def("a") != null, "registered quest is retrievable");
            Eq(e.Progress("a").Status, QuestStatus.Locked, "new quest starts Locked");

            bool threwDuplicate = false;
            try { e.Register(Simple("a", new QuestStep("x", Objective.Talk("n")))); }
            catch (ArgumentException) { threwDuplicate = true; }
            Check(threwDuplicate, "duplicate quest id is rejected");

            bool threwEmpty = false;
            try { e.Register(new QuestDef { Id = "empty" }); }
            catch (ArgumentException) { threwEmpty = true; }
            Check(threwEmpty, "quest without steps is rejected");

            bool threwNoId = false;
            try { e.Register(Simple("", new QuestStep("x", Objective.Talk("n")))); }
            catch (ArgumentException) { threwNoId = true; }
            Check(threwNoId, "quest without id is rejected");
        }

        private static void Availability()
        {
            Section("availability gating");
            var e = EngineWithChain();

            var offered = e.RefreshAvailability();
            Eq(offered.Count, 1, "only the unlocked quest is offered");
            Eq(offered[0].QuestId, "bottle", "it is the one with no requirements");
            Eq(e.Progress("wreck").Status, QuestStatus.Locked, "flag-gated quest stays locked");

            // Island gating: satisfy the flag but stay on the wrong island.
            e.SetFlag("story.bottle");
            e.CurrentIsland = 1;
            e.RefreshAvailability();
            Eq(e.Progress("wreck").Status, QuestStatus.Locked, "island 2 quest stays locked on island 1");

            e.CurrentIsland = 2;
            var nowOffered = e.RefreshAvailability();
            Eq(e.Progress("wreck").Status, QuestStatus.Available, "offered once on the right island");
            Check(nowOffered.Any(o => o.QuestId == "wreck"), "offering is reported");

            Eq(e.RefreshAvailability().Count, 0, "refreshing twice does not re-offer");
        }

        private static void AcceptAndProgress()
        {
            Section("accepting and step progression");
            var e = EngineWithChain();
            e.RefreshAvailability();

            Check(!e.Accept("nonexistent"), "accepting an unknown quest fails");
            Check(e.Accept("bottle"), "accepting an offered quest works");
            Check(!e.Accept("bottle"), "accepting twice fails");
            Eq(e.Progress("bottle").Status, QuestStatus.Active, "quest is active");

            // Wrong item does nothing.
            var none = e.Handle(QuestEvent.Collected("driftwood", 1));
            Eq(none.Count, 0, "unrelated event is ignored");
            Eq(e.Progress("bottle").Counter, 0, "counter untouched");

            var step1 = e.Handle(QuestEvent.Collected("bottle", 1));
            Check(step1.Any(o => o.Kind == QuestOutcome.Type.StepCompleted), "step 1 completes");
            Eq(e.Progress("bottle").StepIndex, 1, "advanced to step 2");

            // Delivering to the wrong NPC must not count.
            e.Handle(QuestEvent.Delivered("Grillmaster", "bottle", 1));
            Eq(e.Progress("bottle").StepIndex, 1, "delivery to wrong NPC ignored");

            var done = e.Handle(QuestEvent.Delivered("Salty", "bottle", 1));
            Check(done.Any(o => o.Kind == QuestOutcome.Type.QuestCompleted), "quest completes");
            Eq(e.Progress("bottle").Status, QuestStatus.Completed, "status is Completed");

            var rewards = done.Where(o => o.Kind == QuestOutcome.Type.RewardGranted).ToList();
            Eq(rewards.Count, 2, "both rewards granted");
            Check(rewards.Any(r => r.Reward.Kind == RewardKind.Money && r.Reward.Amount == 250), "money reward");
            Check(e.HasFlag("story.bottle"), "reward flag raised");
            Check(e.HasFlag("bottle.done"), "implicit <id>.done flag raised");

            // Completing the first quest must chain-offer the follow-up on the right island.
            e.CurrentIsland = 2;
            e.RefreshAvailability();
            Eq(e.Progress("wreck").Status, QuestStatus.Available, "follow-up unlocked by the flag");

            // A completed quest ignores further events.
            var after = e.Handle(QuestEvent.Delivered("Salty", "bottle", 1));
            Check(!after.Any(o => o.QuestId == "bottle"), "completed quest ignores events");
        }

        private static void CountersAndPartials()
        {
            Section("counters and partial progress");
            var e = new QuestEngine();
            var q = Simple("hunt", new QuestStep("Kill 3 piranhas", Objective.Kill("piranha", 3)));
            e.Register(q);
            e.RefreshAvailability();
            e.Accept("hunt");

            e.Handle(QuestEvent.Killed("piranha"));
            Eq(e.Progress("hunt").Counter, 1, "counter increments");
            e.Handle(QuestEvent.Killed("cod"));
            Eq(e.Progress("hunt").Counter, 1, "wrong creature does not count");
            e.Handle(QuestEvent.Killed("piranha", 2));
            Eq(e.Progress("hunt").Status, QuestStatus.Completed, "bulk event completes the step");

            // Wildcard objectives.
            var e2 = new QuestEngine();
            e2.Register(Simple("any", new QuestStep("Kill anything", Objective.Kill("*", 2))));
            e2.RefreshAvailability();
            e2.Accept("any");
            e2.Handle(QuestEvent.Killed("cod"));
            e2.Handle(QuestEvent.Killed("eel"));
            Eq(e2.Progress("any").Status, QuestStatus.Completed, "wildcard matches any creature");
        }

        private static void WeightAndArea()
        {
            Section("catch weight and reach radius");
            var e = new QuestEngine();
            e.Register(Simple("big", new QuestStep("Catch a 5kg pike", Objective.CatchWeight("pike", 50000))));
            e.Register(Simple("spot", new QuestStep("Sail there", Objective.Reach(2, 100f, 50f, 10f))));
            e.RefreshAvailability();
            e.Accept("big");
            e.Accept("spot");

            e.Handle(QuestEvent.Caught("pike", 49999));
            Eq(e.Progress("big").Status, QuestStatus.Active, "underweight catch does not count");
            e.Handle(QuestEvent.Caught("cod", 90000));
            Eq(e.Progress("big").Status, QuestStatus.Active, "wrong species does not count");
            e.Handle(QuestEvent.Caught("pike", 50000));
            Eq(e.Progress("big").Status, QuestStatus.Completed, "exact threshold counts");

            e.Handle(QuestEvent.Moved(2, 111f, 50f));
            Eq(e.Progress("spot").Status, QuestStatus.Active, "outside radius does not count");
            e.Handle(QuestEvent.Moved(1, 100f, 50f));
            Eq(e.Progress("spot").Status, QuestStatus.Active, "right spot on wrong island does not count");
            e.Handle(QuestEvent.Moved(2, 106f, 53f));
            Eq(e.Progress("spot").Status, QuestStatus.Completed, "inside radius counts");
        }

        private static void SaveRoundTrip()
        {
            Section("save / load");
            var e = EngineWithChain();
            e.RefreshAvailability();
            e.Accept("bottle");
            e.Handle(QuestEvent.Collected("bottle", 1)); // mid-quest: on step 2

            List<QuestProgress> saved = e.SaveState();
            var flags = e.Flags.ToList();

            var reloaded = EngineWithChain();
            reloaded.LoadState(saved, flags);
            Eq(reloaded.Progress("bottle").Status, QuestStatus.Active, "status restored");
            Eq(reloaded.Progress("bottle").StepIndex, 1, "step index restored");

            var done = reloaded.Handle(QuestEvent.Delivered("Salty", "bottle", 1));
            Check(done.Any(o => o.Kind == QuestOutcome.Type.QuestCompleted), "restored quest can still be finished");

            // Unknown ids in an old save must not crash or resurrect.
            var stale = new List<QuestProgress> { new QuestProgress { Id = "removed_quest", Status = QuestStatus.Active } };
            var e3 = EngineWithChain();
            e3.LoadState(stale, null);
            Check(e3.Progress("removed_quest") == null, "unknown saved quest is dropped");

            // A save from a version where the quest had more steps must clamp, not crash.
            var future = new List<QuestProgress> { new QuestProgress { Id = "bottle", Status = QuestStatus.Active, StepIndex = 99 } };
            var e4 = EngineWithChain();
            e4.LoadState(future, null);
            Eq(e4.Progress("bottle").StepIndex, 1, "out-of-range step clamped to last step");

            // Completed quests survive a step-count change.
            var completed = new List<QuestProgress> { new QuestProgress { Id = "bottle", Status = QuestStatus.Completed, StepIndex = 0 } };
            var e5 = EngineWithChain();
            e5.LoadState(completed, new[] { "story.bottle" });
            Eq(e5.Progress("bottle").Status, QuestStatus.Completed, "completed stays completed");
            Check(e5.HasFlag("story.bottle"), "flags restored");
        }

        private static void ForceCompleteWorks()
        {
            Section("debug force-complete");
            var e = EngineWithChain();
            e.RefreshAvailability();

            var outcomes = e.ForceComplete("bottle");
            Eq(e.Progress("bottle").Status, QuestStatus.Completed, "forced quest completes from Available");
            Check(outcomes.Any(o => o.Kind == QuestOutcome.Type.RewardGranted), "rewards still granted");
            Check(e.HasFlag("story.bottle"), "flags still raised");

            // Multi-step quest, forced from the middle, must terminate.
            var e2 = EngineWithChain();
            e2.RefreshAvailability();
            e2.Accept("bottle");
            e2.Handle(QuestEvent.Collected("bottle", 1));
            e2.ForceComplete("bottle");
            Eq(e2.Progress("bottle").Status, QuestStatus.Completed, "forced from mid-quest completes");

            Eq(e2.ForceComplete("bottle").Count, 0, "forcing an already completed quest is a no-op");
            Eq(e2.ForceComplete("nope").Count, 0, "forcing an unknown quest is a no-op");
        }

        private static void FlagDrivenChaining()
        {
            Section("flag-driven chaining");
            var e = new QuestEngine();
            var a = Simple("a", new QuestStep("wait", Objective.Flag("pirates.defeated")));
            a.Rewards.Add(Reward.Flag("act1.done"));
            e.Register(a);

            var b = Simple("b", new QuestStep("talk", Objective.Talk("Salty")));
            b.Requires.Add("act1.done");
            e.Register(b);

            e.RefreshAvailability();
            e.Accept("a");

            var outcomes = e.SetFlag("pirates.defeated");
            Eq(e.Progress("a").Status, QuestStatus.Completed, "flag event completes the quest");
            Eq(e.Progress("b").Status, QuestStatus.Available, "follow-up offered in the same pass");
            Check(outcomes.Any(o => o.Kind == QuestOutcome.Type.Offered && o.QuestId == "b"), "chained offer reported");

            Eq(e.SetFlag("pirates.defeated").Count, 0, "setting the same flag twice does nothing");
        }

        private static void PreSatisfiedFlagSteps()
        {
            Section("flag steps already satisfied");
            var e = new QuestEngine();
            var q = Simple("chain",
                new QuestStep("buy a gun", Objective.Flag("gun.bought")),
                new QuestStep("reach the mark", Objective.Flag("mark.reached")),
                new QuestStep("sink her", Objective.Flag("ship.sunk")));
            e.Register(q);
            e.RefreshAvailability();

            // Bought the gun before anyone asked.
            e.SetFlag("gun.bought");
            e.Accept("chain");
            Eq(e.Progress("chain").StepIndex, 1, "first step skips itself on accept when its flag is already set");

            // Reached the mark early too: advancing into that step must also skip it.
            e.SetFlag("mark.reached");
            Eq(e.Progress("chain").StepIndex, 2, "reaching a satisfied step on the way skips it as well");

            e.SetFlag("ship.sunk");
            Eq(e.Progress("chain").Status, QuestStatus.Completed, "final flag completes the quest");

            // Non-flag steps must never be skipped by this rule.
            var e2 = new QuestEngine();
            e2.Register(Simple("kill", new QuestStep("kill one", Objective.Kill("cod", 1))));
            e2.RefreshAvailability();
            e2.Accept("kill");
            Eq(e2.Progress("kill").Status, QuestStatus.Active, "kill step is not auto-completed");
        }

        // ------------------------------------------------------------------ entry point

        internal static int Run()
        {
            Console.WriteLine("Quest engine tests");
            Registration();
            Availability();
            AcceptAndProgress();
            CountersAndPartials();
            WeightAndArea();
            SaveRoundTrip();
            ForceCompleteWorks();
            FlagDrivenChaining();
            PreSatisfiedFlagSteps();

            Console.WriteLine();
            if (_failed == 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(_passed + " passed, 0 failed.");
                Console.ResetColor();
                return 0;
            }
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(_passed + " passed, " + _failed + " FAILED.");
            Console.ResetColor();
            return 1;
        }

        private static int Main()
        {
            int a = Run();
            int b = ContentTests.Run();
            return a | b;
        }
    }
}
