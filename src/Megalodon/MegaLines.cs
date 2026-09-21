using UnityEngine;

namespace Expanded.Megalodon
{
    /// <summary>
    /// Everything people yell. Each moment has a sweary pool and a family-edition pool
    /// (config Megalodon.Extras / Swearing).
    /// </summary>
    internal static class MegaLines
    {
        internal static readonly string[] NearMiss =
        {
            "AAAHH FUCK FUCK FUCK", "FUUUUUCK!", "NOPE NOPE NOPE NOPE", "MY LEGS! I STILL HAVE LEGS!",
            "THAT WAS MY SHORTS!", "FUCK! FUCK! GAS!!", "I FELT ITS BREATH. IT SMELLS LIKE GULLS."
        };
        internal static readonly string[] NearMissClean =
        {
            "AAAAAAAHHH!", "NOPE NOPE NOPE NOPE", "MY LEGS! I STILL HAVE LEGS!", "THAT WAS MY SHORTS!",
            "MUM! MUUUM!", "I FELT ITS BREATH. IT SMELLS LIKE GULLS."
        };

        internal static readonly string[] Bitten = { "FUUUUUUUCK!", "IT ATE MY BOARD!", "WHY IS IT ALWAYS ME", "FUCK THIS FISH!" };
        internal static readonly string[] BittenClean = { "AAAAAARGH!", "IT ATE MY BOARD!", "WHY IS IT ALWAYS ME", "BAD FISH! BAD!" };

        internal static string NewBoard(Board b)
        {
            switch (b)
            {
                case Board.Door: return "I'M SURFING ON A DOOR?!";
                case Board.Bathtub: return "WHO PUT A BATHTUB IN THE BOAT?!";
                case Board.Barefoot: return "BAREFOOT! BAREFOOT!! AAAAAH";
                default: return "...";
            }
        }

        internal static readonly string[] Telegraph = { "BUBBLES! WHY ARE THERE BUBBLES?!", "HE'S JUMPING!!", "IT WENT UNDER! IT WENT UNDER!", "OH NO. OH NO NO NO." };
        internal static readonly string[] TooSlow = { "GAS! GAS! GAS!", "FASTER!!!", "WHY ARE WE SLOWING DOWN?!", "IT'S RIGHT BEHIND ME!!" };
        internal static readonly string[] JumpOver = { "DUCK!!", "WHAT THE FU—", "IT'S FLYING!", "NOPE.", "IS IT RAINING SHARK?!" };
        internal static readonly string[] JumpOverClean = { "DUCK!!", "WHAT THE—", "IT'S FLYING!", "NOPE.", "IS IT RAINING SHARK?!" };
        internal static readonly string[] FakeOut = { "WHERE DID IT GO?", "...it's gone. right? RIGHT?", "IN FRONT! IN FRONT!!" };
        internal static readonly string[] Stalled = { "COME ON COME ON COME ON", "NOT NOW!!", "START, YOU STUPID ENGINE!", "PULL! PULL! PULL!" };
        internal static readonly string[] Rodeo = { "YEEHAW!", "I'M RIDING IT! I'M RIDING IT!!", "GIDDY UP, FISHY!" };
        internal static readonly string[] FishFace = { "MMMPHH!", "GET IT OFF!! IT'S IN MY MOUTH!", "FISH! FACE! FISH IN FACE!" };
        internal static readonly string[] Jelly = { "BZZZT— OW!", "IT STUNG MY EVERYTHING!", "JELLY! NOT THE GOOD KIND!" };
        internal static readonly string[] Slingshot = { "WHEEEEEEE—", "BUOY BOOST!", "I DID THAT ON PURPOSE" };
        internal static readonly string[] Ramp = { "SEND IT!", "RAMP!!", "LOOK MUM, NO BRAIN!" };
        internal static readonly string[] Wipeout = { "MY SHINS!", "OW OW OW OW", "WHO PUT THAT THERE?!" };
        internal static readonly string[] Victory = { "WE DID IT!", "TOOTH! I GOT A TOOTH!", "SUSHI FOR A YEAR!", "WHO'S THE APEX PREDATOR NOW?!" };
        internal static readonly string[] Deck = { "BEHIND YOU!!", "SHOOT IT! SHOOT IT!", "HOLD ON!!", "DON'T LOOK BACK! ...LOOK BACK!" };
        internal static readonly string[] MineAway = { "MINE AWAY!", "EAT THIS!", "BARREL OF DOOM, INCOMING!" };
        internal const string Back = "...I'm back.";

        // Old Salt at the helm (solo). He can't see without his teeth.
        internal static readonly string[] SaltDriving =
        {
            "I CAN'T SEE WITHOUT MY TEETH!", "Is it behind us? Don't tell me. ...Tell me.",
            "Back in my day sharks were THIS big. ...Oh.", "FULL STEAM! ...Is that the brake?",
            "Hold on, checking my mirrors. I don't have mirrors.", "I've driven worse. I can't remember when."
        };
        internal static readonly string[] SaltWrongWay = { "Left? Your left or my left?", "RIGHT! ...No, the other right.", "Which way's port again?" };
        internal static readonly string[] SaltIgnore = { "What?", "Speak up, I'm old!", "Mm-hm. Mm-hm. No." };
        internal static readonly string[] SaltBrake = { "Hang on, dropped my hat.", "Oops.", "Wait— wait— which one's the gas?" };

        internal static string Pick(string[] pool) => pool[Random.Range(0, pool.Length)];

        /// <summary>The sweary pool, or the clean one if swearing is switched off.</summary>
        internal static string Pick(string[] swear, string[] clean)
        {
            bool sweary = MegaModule.Cfg == null || MegaModule.Cfg.Swearing.Value;
            return Pick(sweary ? swear : clean);
        }
    }
}
