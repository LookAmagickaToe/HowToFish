using Expanded.Quests;

namespace Expanded.Content
{
    /// <summary>
    /// Act 1 of the pirate storyline: the gulls turn, a wreck chart surfaces, and someone else is
    /// already looking for it.
    ///
    /// Design rules for content here:
    ///  - Objective keys are prefab names (lowercase), never localised display names.
    ///  - Every quest is reachable with vanilla systems, or gated behind a flag a module raises.
    ///  - Quests that depend on an unfinished module simply stay locked; they never break the chain.
    /// </summary>
    internal static class PirateStory
    {
        // Flags shared with other modules. Kept as constants so a typo is a compile error.
        internal const string FlagOmens = "story.omens";
        internal const string FlagFlock = "story.flock";
        internal const string FlagChart = "story.chart";
        internal const string FlagPiratesBeaten = "pirates.defeated";

        // Unlock keys other modules read from ModSave.
        internal const string UnlockCannon = "weapon.cannon";
        internal const string UnlockPirateShip = "ship.pirate";
        internal const string UnlockChartShop = "shop.charts";

        internal static void Register(QuestEngine e)
        {
            e.Register(BadOmens());
            e.Register(TheFlockBreaks());
            e.Register(AFishWorthSelling());
            e.Register(ColoursAtDawn());
        }

        /// <summary>Opening beat: the player notices the gulls are not behaving normally.</summary>
        private static QuestDef BadOmens()
        {
            var q = new QuestDef
            {
                Id = "act1.omens",
                Title = "Bad Omens",
                Giver = "the harbour",
                Summary = "The gulls here have lost their manners. Thin them out and see what happens."
            };
            q.Steps.Add(new QuestStep(
                "Kill 3 seagulls",
                Objective.Kill("seagull", 3),
                "That got their attention. Something out there noticed."));
            q.Rewards.Add(Reward.Money(150));
            q.Rewards.Add(Reward.Flag(FlagOmens));
            return q;
        }

        /// <summary>Pays off the swarm encounter: surviving it is the story beat.</summary>
        private static QuestDef TheFlockBreaks()
        {
            var q = new QuestDef
            {
                Id = "act1.flock",
                Title = "The Flock Breaks",
                Giver = "the harbour",
                Summary = "They came back with a leader. Break the flock and search what it was guarding."
            };
            q.Requires.Add(FlagOmens);
            q.Steps.Add(new QuestStep(
                "Survive the seagull swarm and kill the Albatross",
                Objective.Flag("swarm.defeated"),
                "The albatross goes down. Something was tangled around its leg: an oilcloth chart."));
            q.Rewards.Add(Reward.Money(400));
            q.Rewards.Add(Reward.Flag(FlagChart));
            q.Rewards.Add(Reward.Flag(FlagFlock));
            q.Rewards.Add(Reward.ShopStock(UnlockChartShop));
            return q;
        }

        /// <summary>A soft, optional beat that rewards ordinary fishing rather than combat.</summary>
        private static QuestDef AFishWorthSelling()
        {
            var q = new QuestDef
            {
                Id = "act1.bigfish",
                Title = "A Fish Worth Selling",
                Giver = "the harbour",
                Summary = "Charts cost money, and money means a catch worth bragging about."
            };
            q.Requires.Add(FlagOmens);
            // 2 kg, expressed in tenths of a gram so no float comparison is involved.
            q.Steps.Add(new QuestStep(
                "Catch a pike of at least 2 kg",
                Objective.CatchWeight("pike", 20000),
                "Now that is a fish. The buyer will remember your face."));
            q.Rewards.Add(Reward.Money(300));
            return q;
        }

        /// <summary>
        /// Act 1 finale. Stays locked until the pirate module raises its flag, so the chain is safe
        /// to ship before that module exists.
        /// </summary>
        private static QuestDef ColoursAtDawn()
        {
            var q = new QuestDef
            {
                Id = "act1.pirates",
                Title = "Colours at Dawn",
                Giver = "the chart",
                Summary = "The chart marks a wreck. So does someone else's chart, and they have cannons."
            };
            q.Requires.Add(FlagChart);
            q.Steps.Add(new QuestStep(
                "Drive off the pirate ship",
                Objective.Flag(FlagPiratesBeaten),
                "Their deck is yours. The wreck can wait; this hull will not."));
            q.Rewards.Add(Reward.Money(1000));
            q.Rewards.Add(Reward.Unlock(UnlockPirateShip));
            q.Rewards.Add(Reward.Unlock(UnlockCannon));
            return q;
        }
    }
}
