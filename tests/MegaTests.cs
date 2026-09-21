using System;
using System.Collections.Generic;
using System.Linq;
using Expanded.Content;
using Expanded.Megalodon;
using Expanded.Quests;

namespace Expanded.Tests
{
    /// <summary>
    /// The megalodon fight's rules and Old Salt's teeth quest. The fight itself needs the game, but
    /// the numbers that decide who gets eaten do not.
    /// </summary>
    internal static class MegaTests
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

        private static void BoardsGoDownhill()
        {
            Section("every bite costs one board, four bites eat you");
            Board b = Board.Wakeboard;
            var seen = new List<Board> { b };
            for (int i = 0; i < 10; i++) { b = MegaRules.Next(b); seen.Add(b); }
            Check(seen[1] == Board.Door, "first bite: a door");
            Check(seen[2] == Board.Bathtub, "second bite: a bathtub");
            Check(seen[3] == Board.Barefoot, "third bite: bare feet");
            Check(seen[4] == Board.Eaten, "fourth bite: eaten");
            Check(seen.Last() == Board.Eaten, "being eaten is final");
            Check(MegaRules.BitesLeft(Board.Wakeboard) == 3 && MegaRules.BitesLeft(Board.Barefoot) == 0, "bites left counts down to zero");

            float carve = 2f, jump = 2f;
            for (Board t = Board.Wakeboard; t <= Board.Barefoot; t++)
            {
                Check(MegaRules.CarveMultiplier(t) > 0f && MegaRules.CarveMultiplier(t) <= carve, t + " carves no better than the board before");
                Check(MegaRules.JumpMultiplier(t) > 0f && MegaRules.JumpMultiplier(t) <= jump, t + " jumps no higher than the board before");
                carve = MegaRules.CarveMultiplier(t);
                jump = MegaRules.JumpMultiplier(t);
                Check(!string.IsNullOrEmpty(MegaRules.BoardName(t)), t + " has a name for the HUD");
            }
        }

        private static void PhasesFollowHealth()
        {
            Section("phases follow the megalodon's health");
            Check(MegaRules.PhaseFor(1f) == Phase.One, "full health: phase 1");
            Check(MegaRules.PhaseFor(0.5f) == Phase.Two, "half health: phase 2");
            Check(MegaRules.PhaseFor(0.2f) == Phase.Three, "a fifth left: phase 3");
            Check(MegaRules.AttackInterval(Phase.Three) < MegaRules.AttackInterval(Phase.One), "it attacks faster when hurt");
            Check(MegaRules.HazardInterval(Phase.Three) < MegaRules.HazardInterval(Phase.One), "the sea gets busier too");
        }

        private static void AttacksVaryAndEscalate()
        {
            Section("attacks vary and escalate");
            foreach (Phase p in new[] { Phase.One, Phase.Two, Phase.Three })
            {
                Check(MegaRules.Pool(p).Length > 1, p + " has more than one move");
                Attack last = Attack.None;
                for (int i = 0; i < 200; i++)
                {
                    double roll = (i * 0.137) % 1.0;
                    Attack a = MegaRules.PickAttack(p, roll, last, false);
                    Check(a != last || MegaRules.Pool(p).Distinct().Count() == 1, p + " never repeats a move back to back (" + a + ")");
                    Check(MegaRules.Pool(p).Contains(a), p + " only picks from its own pool (" + a + ")");
                    last = a;
                }
                Check(MegaRules.PickAttack(p, 0.999, Attack.None, true) != Attack.PlayDead, p + " skips play-dead once used");
            }
            Check(!MegaRules.Pool(Phase.One).Contains(Attack.SternChomp), "no stern chomps in phase 1");
            Check(MegaRules.Pool(Phase.Three).Contains(Attack.SternChomp), "stern chomps in phase 3");
            Check(MegaRules.Pool(Phase.Three).Contains(Attack.JumpOver), "it jumps over the boat in phase 3");
            Check(MegaRules.Pool(Phase.Two).Contains(Attack.FakeOut), "the fake-out arrives in phase 2");
            Check(MegaRules.Hazards(Phase.Two).Contains(Hazard.FlyingFish), "flying fish from phase 2");
            Check(!MegaRules.Hazards(Phase.One).Contains(Hazard.Jellyfish), "no jellyfish in phase 1");

            Hazard lastH = Hazard.None;
            for (int i = 0; i < 100; i++)
            {
                Hazard h = MegaRules.PickHazard(Phase.Three, (i * 0.311) % 1.0, lastH);
                Check(h != lastH, "hazards never repeat back to back (" + h + ")");
                lastH = h;
            }
        }

        private static void SlowBoatsGetEaten()
        {
            Section("a slow boat lets it close in");
            float fast = MegaRules.PreferredDistance(14f, 8f, 16f, 2f);
            float slow = MegaRules.PreferredDistance(3f, 8f, 16f, 2f);
            Check(Math.Abs(fast - 16f) < 0.01f, "at speed it keeps its distance (" + fast + ")");
            Check(Math.Abs(slow - 2f) < 0.01f, "crawling, it is right behind you (" + slow + ")");
            Check(MegaRules.PreferredDistance(6f, 8f, 16f, 2f) < fast, "in between, in between");

            float prev = -1f;
            for (float d = 30f; d >= 0f; d -= 1f)
            {
                float m = MegaRules.BiteMeter(d, 1.9f, 16f);
                Check(m >= prev - 1e-6f, "bite meter rises as it closes (" + d + " m -> " + m.ToString("0.00") + ")");
                prev = m;
            }
            Check(MegaRules.BiteMeter(1f, 1.9f, 16f) >= 0.999f, "inside bite range the meter is full");
            Check(MegaRules.BiteMeter(40f, 1.9f, 16f) <= 0.001f, "far behind the meter is empty");
        }

        private static void ExplosionsFallOff()
        {
            Section("explosions hurt it less the further away they go off");
            int inside = MegaRules.ExplosionDamage(1f, 4f, 100, 1.5f, 1f);
            int edge = MegaRules.ExplosionDamage(5f, 4f, 100, 1.5f, 1f);
            int outside = MegaRules.ExplosionDamage(7f, 4f, 100, 1.5f, 1f);
            Check(inside == 100, "inside the blast: full damage (" + inside + ")");
            Check(edge > 0 && edge < inside, "at the fringe: some damage (" + edge + ")");
            Check(outside == 0, "beyond the reach: nothing (" + outside + ")");
            Check(MegaRules.ExplosionDamage(1f, 4f, 100, 1.5f, 2f) == 200, "multiplier applies (mines hit twice as hard)");
            Check(MegaRules.ExplosionDamage(1f, 4f, 100, 1.5f, 0f) == 0, "zero multiplier disables it");
        }

        private static QuestEngine Story()
        {
            var e = new QuestEngine();
            PirateStory.Register(e);
            MegalodonStory.Register(e);
            return e;
        }

        private static void TeethQuestPlaysThrough()
        {
            Section("Old Salt's Teeth plays through");
            QuestEngine e = Story();
            e.CurrentIsland = 1;
            e.HighestIsland = 1;
            e.RefreshAvailability();
            Check(e.Progress(MegalodonStory.QuestTeeth).Status == QuestStatus.Locked, "not offered before Old Salt mentions his teeth");

            e.Accept(PirateStory.QuestOmens);
            for (int i = 0; i < 3; i++) e.Handle(QuestEvent.Delivered(StoryNpcs.OldSalt, "seagull"));
            Check(e.Progress(MegalodonStory.QuestTeeth).Status == QuestStatus.Available, "offered once Bad Omens is done");
            Check(StoryNpcs.CurrentBarks(StoryNpcs.ById("salt"), e).Exists(l => l.Contains("MORE teeth")),
                  "Old Salt hints at what ate the gull");

            Check(e.Accept(MegalodonStory.QuestTeeth), "accept Old Salt's Teeth");
            Check(e.Progress(MegalodonStory.QuestTeeth).StepIndex == 0, "first: buy the wakeboard");
            e.SetFlag(MegalodonStory.FlagWakeboardBought);
            Check(e.Progress(MegalodonStory.QuestTeeth).StepIndex == 1, "then: go and get bitten");
            e.SetFlag(MegalodonStory.FlagTeethRecovered);
            Check(e.Progress(MegalodonStory.QuestTeeth).StepIndex == 2, "then: hand them back");
            e.Handle(QuestEvent.Talked(StoryNpcs.Anne));
            Check(e.Progress(MegalodonStory.QuestTeeth).Status == QuestStatus.Active, "Anne doesn't want his teeth");
            List<QuestOutcome> done = e.Handle(QuestEvent.Talked(StoryNpcs.OldSalt));
            Check(e.Progress(MegalodonStory.QuestTeeth).Status == QuestStatus.Completed, "talking to Old Salt finishes it");
            Check(done.Any(o => o.Kind == QuestOutcome.Type.RewardGranted && o.Reward.Key == MegalodonStory.UnlockTooth),
                  "the megalodon tooth trophy is granted");
            Check(e.Progress(MegalodonStory.QuestTeeth).Status == QuestStatus.Completed &&
                  e.Definitions.First(d => d.Id == MegalodonStory.QuestTeeth).Done.Contains("sticky"), "and he notices they're sticky");
        }

        private static void TeethQuestOutOfOrder()
        {
            Section("the megalodon doesn't wait for the quest");
            QuestEngine e = Story();
            e.HighestIsland = 3;
            e.RefreshAvailability();
            e.SetFlag(MegalodonStory.FlagWakeboardBought);
            e.SetFlag(MegalodonStory.FlagTeethRecovered);
            e.Accept(PirateStory.QuestOmens);
            for (int i = 0; i < 3; i++) e.Handle(QuestEvent.Delivered(StoryNpcs.OldSalt, "seagull"));
            Check(e.Accept(MegalodonStory.QuestTeeth), "quest still offered after beating the megalodon first");
            Check(e.Progress(MegalodonStory.QuestTeeth).StepIndex == 2, "it skips straight to handing the teeth back");
        }

        internal static int Run()
        {
            Console.WriteLine();
            Console.WriteLine("Megalodon tests");
            BoardsGoDownhill();
            PhasesFollowHealth();
            AttacksVaryAndEscalate();
            SlowBoatsGetEaten();
            ExplosionsFallOff();
            TeethQuestPlaysThrough();
            TeethQuestOutOfOrder();

            Console.WriteLine();
            Console.ForegroundColor = _failed == 0 ? ConsoleColor.Green : ConsoleColor.Red;
            Console.WriteLine(_passed + " passed, " + _failed + (_failed == 0 ? " failed." : " FAILED."));
            Console.ResetColor();
            return _failed == 0 ? 0 : 1;
        }
    }
}
