using System;
using System.Collections.Generic;

namespace Expanded.Quests
{
    /// <summary>
    /// Quest data model. Deliberately free of UnityEngine so the whole progression system can be
    /// unit-tested headlessly (see tests/). Anything that needs the game lives in QuestModule.
    /// </summary>
    public enum ObjectiveKind
    {
        /// <summary>Kill creatures of a given creature id (or "*" for any).</summary>
        Kill,
        /// <summary>Have N of an item id in the world/inventory (counted by the game side).</summary>
        Collect,
        /// <summary>Hand N of an item id to a named NPC.</summary>
        Deliver,
        /// <summary>Catch a fish of at least a given weight (Amount = decigrams to avoid floats).</summary>
        CatchWeight,
        /// <summary>Be within Radius of a world point on a given island.</summary>
        Reach,
        /// <summary>Talk to a named NPC.</summary>
        Talk,
        /// <summary>A scripted flag raised by a module, e.g. "pirates.defeated".</summary>
        Flag
    }

    public enum RewardKind
    {
        Money,
        Item,
        /// <summary>Unlocks stock in the mod shop, e.g. "shop.cannon".</summary>
        ShopStock,
        /// <summary>Unlocks a capability, e.g. "ship.pirate" or "weapon.frontloader".</summary>
        Unlock,
        /// <summary>Raises a story flag other quests can require.</summary>
        Flag
    }

    public sealed class Objective
    {
        public ObjectiveKind Kind;
        /// <summary>Creature/item id, NPC name, flag name - meaning depends on Kind.</summary>
        public string Key = "";
        public int Amount = 1;

        // Only for Reach.
        public int Island;
        public float X, Z, Radius;

        public static Objective Kill(string creature, int count) =>
            new Objective { Kind = ObjectiveKind.Kill, Key = creature, Amount = count };

        public static Objective Collect(string item, int count) =>
            new Objective { Kind = ObjectiveKind.Collect, Key = item, Amount = count };

        public static Objective Deliver(string npc, string item, int count) =>
            new Objective { Kind = ObjectiveKind.Deliver, Key = npc + "|" + item, Amount = count };

        public static Objective CatchWeight(string creature, int decigrams) =>
            new Objective { Kind = ObjectiveKind.CatchWeight, Key = creature, Amount = decigrams };

        public static Objective Reach(int island, float x, float z, float radius) =>
            new Objective { Kind = ObjectiveKind.Reach, Island = island, X = x, Z = z, Radius = radius, Amount = 1 };

        public static Objective Talk(string npc) =>
            new Objective { Kind = ObjectiveKind.Talk, Key = npc, Amount = 1 };

        public static Objective Flag(string flag) =>
            new Objective { Kind = ObjectiveKind.Flag, Key = flag, Amount = 1 };
    }

    public sealed class Reward
    {
        public RewardKind Kind;
        public string Key = "";
        public int Amount;

        public static Reward Money(int amount) => new Reward { Kind = RewardKind.Money, Amount = amount };
        public static Reward Item(string item, int count = 1) =>
            new Reward { Kind = RewardKind.Item, Key = item, Amount = count };
        public static Reward ShopStock(string key) => new Reward { Kind = RewardKind.ShopStock, Key = key };
        public static Reward Unlock(string key) => new Reward { Kind = RewardKind.Unlock, Key = key };
        public static Reward Flag(string key) => new Reward { Kind = RewardKind.Flag, Key = key };
    }

    public sealed class QuestStep
    {
        /// <summary>Shown in the journal while this step is active.</summary>
        public string Text = "";
        public Objective Objective;
        /// <summary>Optional line the giver says when this step completes.</summary>
        public string OnComplete;

        public QuestStep(string text, Objective objective, string onComplete = null)
        {
            Text = text;
            Objective = objective;
            OnComplete = onComplete;
        }
    }

    public sealed class QuestDef
    {
        public string Id = "";
        public string Title = "";
        public string Giver = "";
        public string Summary = "";
        /// <summary>Island the quest becomes offerable on; 0 = anywhere.</summary>
        public int Island;
        /// <summary>Story flags that must all be set before this quest is offered.</summary>
        public List<string> Requires = new List<string>();
        public List<QuestStep> Steps = new List<QuestStep>();
        public List<Reward> Rewards = new List<Reward>();

        // What the giver says, depending on where the player is with this quest. Each falls back
        // to something sensible so a quest without dialogue still reads correctly.
        public string OfferText;
        public string ActiveText;
        public string DoneText;

        public QuestStep StepAt(int index) =>
            index >= 0 && index < Steps.Count ? Steps[index] : null;

        public string Offer => string.IsNullOrEmpty(OfferText) ? Summary : OfferText;
        public string Reminder(int stepIndex)
        {
            QuestStep s = StepAt(stepIndex);
            string step = s != null ? s.Text : "";
            return string.IsNullOrEmpty(ActiveText) ? step : ActiveText + (step.Length > 0 ? "\n\n(" + step + ")" : "");
        }
        public string Done => string.IsNullOrEmpty(DoneText) ? "Good work on \"" + Title + "\"." : DoneText;
    }

    public enum QuestStatus { Locked, Available, Active, Completed }

    /// <summary>Per-quest progress. Serialised verbatim into the mod save file.</summary>
    public sealed class QuestProgress
    {
        public string Id = "";
        public QuestStatus Status = QuestStatus.Locked;
        public int StepIndex;
        /// <summary>Progress toward the current step's Amount.</summary>
        public int Counter;
    }

    /// <summary>A gameplay event the engine can consume. Pure data, no Unity types.</summary>
    public struct QuestEvent
    {
        public ObjectiveKind Kind;
        public string Key;
        public int Amount;
        public int Island;
        public float X, Z;

        public static QuestEvent Killed(string creature, int count = 1) =>
            new QuestEvent { Kind = ObjectiveKind.Kill, Key = creature, Amount = count };

        public static QuestEvent Collected(string item, int count) =>
            new QuestEvent { Kind = ObjectiveKind.Collect, Key = item, Amount = count };

        public static QuestEvent Delivered(string npc, string item, int count = 1) =>
            new QuestEvent { Kind = ObjectiveKind.Deliver, Key = npc + "|" + item, Amount = count };

        public static QuestEvent Caught(string creature, int decigrams) =>
            new QuestEvent { Kind = ObjectiveKind.CatchWeight, Key = creature, Amount = decigrams };

        public static QuestEvent Moved(int island, float x, float z) =>
            new QuestEvent { Kind = ObjectiveKind.Reach, Island = island, X = x, Z = z, Amount = 1 };

        public static QuestEvent Talked(string npc) =>
            new QuestEvent { Kind = ObjectiveKind.Talk, Key = npc, Amount = 1 };

        public static QuestEvent Flagged(string flag) =>
            new QuestEvent { Kind = ObjectiveKind.Flag, Key = flag, Amount = 1 };
    }

    /// <summary>Something the game side must act on after the engine processed an event.</summary>
    public struct QuestOutcome
    {
        public enum Type { Offered, Started, StepCompleted, QuestCompleted, RewardGranted }

        public Type Kind;
        public string QuestId;
        public string Text;
        public Reward Reward;

        public override string ToString() => Kind + "(" + QuestId + (Text != null ? ": " + Text : "") + ")";
    }
}
