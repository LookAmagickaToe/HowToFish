using Expanded.Quests;

namespace Expanded.Content
{
    /// <summary>
    /// Old Salt's teeth. The gull that stole them (Bad Omens) got eaten by something far bigger, and
    /// that something only comes up for whatever dangles off the back of a boat - a wakeboarder.
    ///
    /// The megalodon itself is not gated by this quest: buy the wakeboard, ride far enough out and it
    /// comes, quest or no quest. The quest just gives it a reason and a punchline.
    /// </summary>
    public static class MegalodonStory
    {
        // Flags raised by the Megalodon module.
        public const string FlagWakeboardBought = "wake.bought";
        public const string FlagTeethRecovered = "megalodon.teeth";
        public const string FlagMegalodonBeaten = "megalodon.beaten";

        public const string QuestTeeth = "act2.teeth";

        // Unlocks other modules read.
        public const string UnlockWakeboard = "gear.wakeboard";
        public const string UnlockTooth = "trophy.megatooth";

        public static void Register(QuestEngine e)
        {
            e.Register(OldSaltsTeeth());
        }

        private static QuestDef OldSaltsTeeth()
        {
            var q = new QuestDef
            {
                Id = QuestTeeth,
                Title = "Old Salt's Teeth",
                Giver = StoryNpcs.OldSalt,
                Summary = "The gull with Old Salt's teeth got eaten. By a fish. A BIG fish.",
                OfferText = "Remember my teeth? The gull that nicked 'em got eaten. By a fish.\n" +
                            "A BIG fish. Big as a church. Teeth like... well. Like mine, but more of 'em.\n" +
                            "It only comes up for things dangling off the back of a boat. Wriggly things. Like you on a plank.\n" +
                            "The shop sells a wakeboard and a tow line. Buy it, have your mate drive, and go far out. Very far out.\n" +
                            "Get my teeth back. Try to keep your legs.",
                ActiveText = "Wakeboard, tow line, open water, something dangling. It'll come.\n" +
                             "And don't let your mate drive slow. It likes slow.",
                DoneText = "...Passt. Fits like the day I lost 'em.\n" +
                           "Why are they sticky? ...No. Don't tell me. Don't ever tell me."
            };
            q.Requires.Add(PirateStory.FlagOmens);
            q.Steps.Add(new QuestStep("Buy a wakeboard and tow line at the shop (next to the boat motors)",
                                      Objective.Flag(FlagWakeboardBought),
                                      "It leans by the helm. Press E at it to grab the rope. Then scream."));
            q.Steps.Add(new QuestStep("Wakeboard far out at sea until something bites - then get the teeth back from the megalodon",
                                      Objective.Flag(FlagTeethRecovered),
                                      "Teeth: recovered. Legs: hopefully also recovered."));
            q.Steps.Add(new QuestStep("Give Old Salt his teeth back", Objective.Talk(StoryNpcs.OldSalt)));
            q.Rewards.Add(Reward.Money(400));
            q.Rewards.Add(Reward.Unlock(UnlockTooth));
            return q;
        }
    }
}
