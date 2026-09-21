using System;
using System.Collections.Generic;
using Expanded.Quests;

namespace Expanded.Content
{
    /// <summary>A story character. Pure data, Unity-free, so content can be validated in tests.</summary>
    public sealed class NpcDef
    {
        public string Id = "";
        /// <summary>Display name; quests name their giver with exactly this string.</summary>
        public string Name = "";
        /// <summary>Character model from the characters bundle.</summary>
        public string Model = "";
        /// <summary>Placement slot around the island's landing point; distinct per NPC.</summary>
        public int Slot;
        /// <summary>Flag that must be set before this character appears. Null = always present.</summary>
        public string AppearsAfter;
        /// <summary>Idle chatter, keyed by the story flag that unlocks each line.</summary>
        public List<KeyValuePair<string, string>> Barks = new List<KeyValuePair<string, string>>();

        public NpcDef Bark(string requiresFlag, string line)
        {
            Barks.Add(new KeyValuePair<string, string>(requiresFlag, line));
            return this;
        }
    }

    /// <summary>
    /// The cast of the pirate storyline.
    ///
    /// Old Salt is the through-line: he turns up at the landing on every island, like he is following
    /// you, which is both a running joke and a way to deliver story anywhere without per-island
    /// coordinates. Others join once the story reaches them.
    /// </summary>
    public static class StoryNpcs
    {
        public const string OldSalt = "Old Salt";
        public const string Anne = "Anne";
        public const string Mako = "Mako";

        public static readonly List<NpcDef> All = new List<NpcDef>
        {
            new NpcDef
            {
                Id = "salt", Name = OldSalt, Model = "characters_henry", Slot = 0
            }
            .Bark(null, "Fish don't catch themselves. Well. Mostly.")
            .Bark(null, "I was here before you. I'll be at the next island before you, too. Don't ask.")
            .Bark(PirateStory.FlagOmens, "Hear that? Gulls stopped screaming. Never a good sign.")
            .Bark(PirateStory.FlagChart, "Anne'll know what that chart means. She knows everything that's written down.")
            .Bark(PirateStory.FlagPiratesBeaten, "Heard you sank a ship. Bold. Stupid, but bold."),

            new NpcDef
            {
                Id = "anne", Name = Anne, Model = "characters_anne", Slot = 1,
                AppearsAfter = PirateStory.FlagChart
            }
            .Bark(PirateStory.FlagChart, "Every chart lies a little. This one lies a lot, which means it's worth something.")
            .Bark(PirateStory.FlagPiratesBeaten, "The wreck's still out there. They were never after your boat, you know. They were after that chart."),

            new NpcDef
            {
                Id = "mako", Name = Mako, Model = "characters_mako", Slot = 2,
                AppearsAfter = PirateStory.FlagPiratesBeaten
            }
            .Bark(PirateStory.FlagPiratesBeaten, "Rigged the Widow's hull over your old boat. Same motor underneath. Don't tell anyone.")
            .Bark(PirateStory.FlagPiratesBeaten, "She's got a stern gun as well as the bow. Brace your knees, not your back.")
            .Bark(PirateStory.FlagPiratesBeaten, "Cannons work on fish too. Nobody told you that. Now somebody has.")
            .Bark(PirateStory.FlagPiratesBeaten, "Miss your old tub? Say the word and I'll swap the hulls back."),
        };

        public static NpcDef ById(string id)
        {
            foreach (NpcDef n in All) if (string.Equals(n.Id, id, StringComparison.Ordinal)) return n;
            return null;
        }

        public static bool IsPresent(NpcDef npc, QuestEngine engine)
        {
            if (npc == null) return false;
            if (string.IsNullOrEmpty(npc.AppearsAfter)) return true;
            return engine != null && engine.HasFlag(npc.AppearsAfter);
        }

        /// <summary>Barks the character can say right now, given the story so far.</summary>
        public static List<string> CurrentBarks(NpcDef npc, QuestEngine engine)
        {
            var lines = new List<string>();
            foreach (KeyValuePair<string, string> b in npc.Barks)
                if (b.Key == null || (engine != null && engine.HasFlag(b.Key))) lines.Add(b.Value);
            return lines;
        }
    }
}
