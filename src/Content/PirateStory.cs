using Expanded.Quests;

namespace Expanded.Content
{
    /// <summary>
    /// Act 1 of the pirate storyline: the gulls turn, a chart turns up on an albatross, and someone
    /// else wants it badly enough to bring cannons.
    ///
    /// Content rules:
    ///  - Objective keys are prefab names (lowercase), never localised display names.
    ///  - Every quest is offered by a named character in StoryNpcs, matched on Giver.
    ///  - Quests that depend on a module simply stay locked until its flag is raised; nothing in the
    ///    chain can dead-end.
    /// </summary>
    public static class PirateStory
    {
        // Flags shared with other modules. Constants, so a typo is a compile error.
        public const string FlagOmens = "story.omens";
        public const string FlagFlock = "story.flock";
        public const string FlagChart = "story.chart";
        public const string FlagSwarmDefeated = "swarm.defeated";
        public const string FlagCannonBought = "cannon.bought";
        public const string FlagSiteReached = "chart.site.reached";
        public const string FlagPiratesBeaten = "pirates.defeated";

        // Quest ids other modules key off.
        public const string QuestOmens = "act1.omens";
        public const string QuestFlock = "act1.flock";
        public const string QuestBigFish = "act1.bigfish";
        public const string QuestPirates = "act1.pirates";

        // Unlock keys other modules read.
        public const string UnlockCannon = "weapon.cannon";
        public const string UnlockPirateShip = "ship.pirate";
        public const string UnlockChartShop = "shop.charts";

        public static void Register(QuestEngine e)
        {
            e.Register(BadOmens());
            e.Register(TheFlockBreaks());
            e.Register(AFishWorthSelling());
            e.Register(ColoursAtDawn());
        }

        private static QuestDef BadOmens()
        {
            var q = new QuestDef
            {
                Id = QuestOmens,
                Title = "Bad Omens",
                Giver = StoryNpcs.OldSalt,
                Summary = "The gulls here have lost their manners.",
                OfferText = "Gulls been screaming at me all week. Not at the fish. At me.\n" +
                            "Thin 'em out, would you? Three should do it.\n" +
                            "And bring 'em here. I want to see them. Then I want to eat them.",
                ActiveText = "Three gulls. The white ones, with the attitude. Toss 'em to me.",
                DoneText = "Three down and the rest went quiet. That's not better. That's worse."
            };
            q.Steps.Add(new QuestStep("Feed 3 dead seagulls to Old Salt", Objective.Deliver(StoryNpcs.OldSalt, "seagull", 3),
                                      "The gulls fall silent. Something out there noticed."));
            q.Rewards.Add(Reward.Money(150));
            q.Rewards.Add(Reward.Flag(FlagOmens));
            return q;
        }

        /// <summary>Pays off the swarm encounter: surviving it is the story beat.</summary>
        private static QuestDef TheFlockBreaks()
        {
            var q = new QuestDef
            {
                Id = QuestFlock,
                Title = "The Flock Breaks",
                Giver = StoryNpcs.OldSalt,
                Summary = "They came back with a leader. Break the flock.",
                OfferText = "When gulls go quiet it means they're fetching someone bigger.\n" +
                            "Kill five of 'em quick - inside three minutes - and the whole flock comes for you.\n" +
                            "Leading them: an albatross. Big bird. Bigger opinion of himself.\n" +
                            "Shoot him down and the rest scatter. Then bring me whatever he's carrying.",
                ActiveText = "Five gulls inside three minutes. That's what calls the flock.\n" +
                             "When they come, go for the albatross - the one with the health bar. Kill him and it's over.",
                DoneText = "An oilcloth chart, tied to his leg. Birds don't tie knots. Someone sent it."
            };
            q.Requires.Add(FlagOmens);
            q.Steps.Add(new QuestStep("Kill 5 gulls within 3 minutes to call the swarm, then kill the Albatross",
                                      Objective.Flag(FlagSwarmDefeated),
                                      "Something was tangled round the albatross's leg: an oilcloth chart."));
            q.Rewards.Add(Reward.Money(400));
            q.Rewards.Add(Reward.Flag(FlagChart));
            q.Rewards.Add(Reward.Flag(FlagFlock));
            q.Rewards.Add(Reward.ShopStock(UnlockChartShop));
            return q;
        }

        /// <summary>Optional, rewards ordinary fishing rather than combat.</summary>
        private static QuestDef AFishWorthSelling()
        {
            var q = new QuestDef
            {
                Id = QuestBigFish,
                Title = "A Fish Worth Selling",
                Giver = StoryNpcs.OldSalt,
                Summary = "Charts cost money, and money means a catch worth bragging about.",
                OfferText = "Adventure's all well and good, but you still have to eat.\n" +
                            "Bring in a pike of two kilos or better and I'll pay over the odds.",
                ActiveText = "Two kilos of pike. Not two pike of one kilo. I know that trick.",
                DoneText = "Now that's a fish. The buyer will remember your face. Might even be a good thing."
            };
            q.Requires.Add(FlagOmens);
            // 2 kg, in tenths of a gram so no float comparison is involved.
            q.Steps.Add(new QuestStep("Catch a pike of at least 2 kg", Objective.CatchWeight("pike", 20000),
                                      "Now that is a fish."));
            q.Rewards.Add(Reward.Money(300));
            return q;
        }

        /// <summary>
        /// Act 1 finale. Three beats: arm yourself (teaches the shop), sail to the spot the chart
        /// marks (a real, visible place to go), fight whoever is waiting there.
        /// </summary>
        private static QuestDef ColoursAtDawn()
        {
            var q = new QuestDef
            {
                Id = QuestPirates,
                Title = "Colours at Dawn",
                Giver = StoryNpcs.Anne,
                Summary = "The chart marks a wreck. Someone else's chart does too, and they have cannons.",
                OfferText = "Give me that. ...Oh. Oh no.\n" +
                            "This is the Salted Widow's last haul. Her captain's been hunting this chart for a month, " +
                            "and the mark on it is two hundred metres off this very island.\n" +
                            "He's out there now. You can't outrun him in that tub - but you could outgun him.\n" +
                            "The shop sells swivel guns, right next to the motors. Buy one, then go and see what's at the mark.",
                ActiveText = "Gun first, then the mark. Look for a flag bobbing on the water. And don't let him get side-on to you.",
                DoneText = "You sank the Salted Widow. People are going to start telling stories about you. Wrong ones, mostly."
            };
            q.Requires.Add(FlagChart);
            q.Steps.Add(new QuestStep("Buy a swivel gun at the shop (next to the boat motors)",
                                      Objective.Flag(FlagCannonBought),
                                      "Bolted to the bow. It'll kick. Good."));
            q.Steps.Add(new QuestStep("Sail to the flag the chart marks",
                                      Objective.Flag(FlagSiteReached),
                                      "Nothing at the mark but a buoy... and sails on the horizon."));
            q.Steps.Add(new QuestStep("Sink the Salted Widow - or shoot her captain",
                                      Objective.Flag(FlagPiratesBeaten),
                                      "Their colours come down. The Widow is yours."));
            q.Rewards.Add(Reward.Money(1000));
            q.Rewards.Add(Reward.Unlock(UnlockPirateShip));
            return q;
        }
    }
}
