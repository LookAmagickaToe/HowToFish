using Expanded.Quests;

namespace Expanded.Content
{
    /// <summary>
    /// Act 1 of the pirate storyline: the gulls turn, a chart falls out of the beaten flock, and someone
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

        // Islands are 0-based: 0 is where a new game starts. Guns are sold from the second island,
        // and the pirates sail from the third.
        public const int IslandWithGuns = 1;
        public const int IslandOfPirates = 2;

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
                OfferText = "You've got something that goes bang now. Good. I've got a gull problem.\n" +
                            "They steal chips. They steal hats. One of 'em stole my teeth and I want them back.\n" +
                            "Shoot three and bring 'em here. I'll check their pockets. Then I'll eat the evidence.",
                ActiveText = "Three gulls. The white ones, with the attitude. Toss 'em to me, I'm peckish.",
                DoneText = "No teeth. But they tasted of chips, so somebody's still feeding them. Somebody organised.\n" +
                           "And a warning, since I like you: don't shoot too many at once. Five in a few minutes and the " +
                           "whole flock comes for you. Wave after wave."
            };
            q.MinIsland = IslandWithGuns;
            q.Steps.Add(new QuestStep("Feed 3 dead seagulls to Old Salt", Objective.Deliver(StoryNpcs.OldSalt, "seagull", 3),
                                      "The gulls fall silent. Something out there noticed."));
            q.Rewards.Add(Reward.Money(50));
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
                OfferText = "When gulls go quiet it means they're gathering. All of them.\n" +
                            "Kill five of 'em quick - inside three minutes - and the whole flock comes for you.\n" +
                            "They come in waves, each bigger than the last, and each one's got to be broken before it gives up on you.\n" +
                            "Last all the waves and they're done. Go down, or take too long, and they just leave. Laughing.",
                ActiveText = "Five gulls inside three minutes. That's what calls the flock.\n" +
                             "Then break every wave before its time runs out. Watch the bar at the top.",
                DoneText = "Something fell out of that last flock. A chart. With a gull on it. Wearing an eyepatch.\n" +
                           "Gulls don't draw charts. Gull PIRATES do. Take it to Anne, she reads."
            };
            q.Requires.Add(FlagOmens);
            q.Steps.Add(new QuestStep("Kill 5 gulls within 3 minutes to call the swarm, then survive all its waves",
                                      Objective.Flag(FlagSwarmDefeated),
                                      "The last gull dropped something as it fell: a chart with an eyepatched gull on it."));
            q.Rewards.Add(Reward.Money(150));
            q.Rewards.Add(Reward.Flag(FlagChart));
            q.Rewards.Add(Reward.Flag(FlagFlock));
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
            q.Rewards.Add(Reward.Money(100));
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
                Summary = "The chart belongs to the Gull Pirates. They want it back. They also want your lunch.",
                OfferText = "Give me that. ...Oh. Oh no. The eyepatched gull.\n" +
                            "That's the mark of the Gull Pirates. Humans, technically. They just live like gulls: " +
                            "screaming, stealing, eating chips off strangers.\n" +
                            "Captain Squawk sails the Greedy Gull. This chart marks their favourite snack spot, just off this island.\n" +
                            "They've got cannons, so you'll want one too. The shop sells swivel guns, next to the motors.\n" +
                            "Then go and see who's at the mark. Tip: don't bring a barbecue. They can smell it for miles.",
                ActiveText = "Gun first, then the mark - it's the red dot on your radar. And don't let them get side-on to you.",
                DoneText = "You beat the Gull Pirates! Squawk will be furious. He's always furious. It's mostly the diet.\n" +
                           "Word of warning: they'll be back whenever you sail out with a boat full of grilled food."
            };
            q.Requires.Add(FlagChart);
            q.MinIsland = IslandOfPirates;
            q.Steps.Add(new QuestStep("Buy a swivel gun at the shop (next to the boat motors)",
                                      Objective.Flag(FlagCannonBought),
                                      "Bolted to the bow. Press E at it to man it. It'll kick. Good."));
            q.Steps.Add(new QuestStep("Sail to the red dot on your radar (the chart's mark)",
                                      Objective.Flag(FlagSiteReached),
                                      "Nothing at the mark but a buoy... and a sail with a gull on it."));
            q.Steps.Add(new QuestStep("Sink the Greedy Gull - or shoot Captain Squawk",
                                      Objective.Flag(FlagPiratesBeaten),
                                      "Their colours come down. The Greedy Gull is yours."));
            q.Rewards.Add(Reward.Money(300));
            q.Rewards.Add(Reward.Unlock(UnlockPirateShip));
            return q;
        }
    }
}
