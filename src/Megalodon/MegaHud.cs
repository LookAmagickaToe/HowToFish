using Expanded.Pirates;
using UnityEngine;

namespace Expanded.Megalodon
{
    /// <summary>
    /// On-screen bits: the megalodon's health bar, the bite meter, what the rider is standing on, and
    /// the driver's panel (mines, a stalled engine to restart).
    /// </summary>
    internal static class MegaHud
    {
        private static GUIStyle _title, _small, _big;
        private static float _hpShown;

        private static void Styles()
        {
            if (_title != null) return;
            _title = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 15 };
            _title.normal.textColor = new Color(1f, 0.93f, 0.85f);
            _small = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 14 };
            _small.normal.textColor = new Color(0.92f, 0.92f, 0.92f);
            _big = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 26 };
        }

        internal static void OnGUI()
        {
            Styles();
            bool fight = SharkVisual.Active && SharkVisual.Mode != SharkMode.Gone;
            if (fight) HealthBar();
            if (fight && SharkVisual.Mode != SharkMode.Dead && SharkVisual.Mode != SharkMode.Leaving) BiteMeter();
            if (Wakeboard.Riding) RiderPanel(fight);
            else if (Boat.IsDrivingLocally) DriverPanel(fight);
            if (MegaStomach.Active) StomachHint();
        }

        private static void HealthBar()
        {
            float max = Mathf.Max(1f, SharkVisual.MaxHp);
            _hpShown = Mathf.MoveTowards(_hpShown <= 0f ? SharkVisual.Hp : _hpShown, SharkVisual.Hp, Time.unscaledDeltaTime * max * 0.6f);

            const float w = 460f, h = 20f;
            float x = (Screen.width - w) * 0.5f;
            float y = PirateModule.Instance != null && PirateModule.Instance.ShipFightActive ? 76f : 18f;

            string suffix = SharkVisual.Mode == SharkMode.Dead ? "  - dead" :
                            SharkVisual.Mode == SharkMode.Leaving ? "  - leaving" :
                            SharkVisual.Mode == SharkMode.Finale ? "  - JAWS WIDE OPEN" :
                            SharkVisual.Phase == Phase.Three ? "  - furious" :
                            SharkVisual.Phase == Phase.Two ? "  - angry" : "";
            GUI.Label(new Rect(x, y, w, 22f), "MEGALODON" + suffix, _title);

            Rect back = new Rect(x, y + 24f, w, h);
            PirateModule.DrawRect(back, new Color(0f, 0f, 0f, 0.6f));
            float lag = Mathf.Clamp01(_hpShown / max), now = Mathf.Clamp01(SharkVisual.Hp / max);
            PirateModule.DrawRect(new Rect(back.x + 2, back.y + 2, (back.width - 4) * lag, back.height - 4), new Color(0.95f, 0.85f, 0.5f, 0.9f));
            PirateModule.DrawRect(new Rect(back.x + 2, back.y + 2, (back.width - 4) * now, back.height - 4), new Color(0.25f, 0.45f, 0.6f, 1f));
            // Phase marks at two thirds and one third.
            PirateModule.DrawRect(new Rect(back.x + back.width * (2f / 3f), back.y, 2f, back.height), new Color(1f, 1f, 1f, 0.5f));
            PirateModule.DrawRect(new Rect(back.x + back.width * (1f / 3f), back.y, 2f, back.height), new Color(1f, 1f, 1f, 0.5f));
        }

        private static void BiteMeter()
        {
            float threat = Mathf.Clamp01(MegaFx.Threat);
            const float w = 300f, h = 14f;
            float x = (Screen.width - w) * 0.5f;
            float y = Screen.height - 212f;
            bool red = threat > 0.75f;
            float pulse = red ? 0.6f + 0.4f * Mathf.Abs(Mathf.Sin(Time.time * 10f)) : 1f;

            GUI.Label(new Rect(x, y - 20f, w, 18f), red ? "IT'S RIGHT BEHIND YOU" : "BITE METER", _small);
            PirateModule.DrawRect(new Rect(x, y, w, h), new Color(0f, 0f, 0f, 0.55f));
            Color c = Color.Lerp(new Color(0.3f, 0.8f, 0.4f), new Color(0.9f, 0.1f, 0.05f), threat);
            c.a = pulse;
            PirateModule.DrawRect(new Rect(x + 2f, y + 2f, (w - 4f) * threat, h - 4f), c);
        }

        private static void RiderPanel(bool fight)
        {
            Board b = WakeRig.Tier;
            string board = MegaRules.BoardName(b);
            int left = MegaRules.BitesLeft(b);
            string line1 = "On: " + board + (fight ? (left > 0 ? "   (" + left + " bite" + (left == 1 ? "" : "s") + " left)" : "   (next bite eats you)") : "");
            string line2 = Wakeboard.InRodeo ? "RODEO!   F  plant a charge     Space  jump off"
                          : "A/D  carve     Space  jump (time it on the wake!)     E  let go     G  shout";
            if (!Wakeboard.Planing && !Wakeboard.Airborne) line2 = "Waiting for the boat to pull you up...   E  let go";
            if (MegaBoat.SeenAutopilot) line2 += "\nOld Salt is driving:  hold A or D + G = LEFT/RIGHT,  G alone = FASTER";

            float w = 560f;
            float h = MegaBoat.SeenAutopilot ? 64f : 46f;
            Rect r = new Rect((Screen.width - w) * 0.5f, Screen.height - 168f - (MegaBoat.SeenAutopilot ? 18f : 0f), w, h);
            PirateModule.DrawRect(r, new Color(0f, 0f, 0f, 0.4f));
            _small.normal.textColor = left == 0 && fight ? new Color(1f, 0.5f, 0.4f) : new Color(0.92f, 0.92f, 0.92f);
            GUI.Label(new Rect(r.x, r.y + 2f, w, 20f), line1, _small);
            _small.normal.textColor = new Color(0.8f, 0.85f, 0.9f);
            GUI.Label(new Rect(r.x, r.y + 22f, w, h - 22f), line2, _small);
            _small.normal.textColor = new Color(0.92f, 0.92f, 0.92f);
        }

        private static void DriverPanel(bool fight)
        {
            if (MegaBoat.SeenStalled)
            {
                _big.normal.textColor = Color.Lerp(new Color(1f, 0.4f, 0.3f), Color.white, Mathf.Abs(Mathf.Sin(Time.time * 8f)));
                float w = 620f;
                Rect r = new Rect((Screen.width - w) * 0.5f, Screen.height * 0.62f, w, 70f);
                PirateModule.DrawRect(r, new Color(0f, 0f, 0f, 0.55f));
                GUI.Label(new Rect(r.x, r.y + 4f, w, 34f), "ENGINE STALLED!  MASH SPACE!", _big);
                GUI.Label(new Rect(r.x, r.y + 40f, w, 24f), "Pull cord: " + MegaBoat.SeenPulls + " / " + Mathf.Max(1, MegaBoat.SeenNeeded), _small);
                return;
            }
            if (!fight) return;

            string text = "G  drop a barrel mine (" + MegaBoat.SeenMines + " left)";
            if (Time.time < MegaBoat.SeenDamagedUntil)
                text += "     ENGINE DAMAGED " + Mathf.CeilToInt(MegaBoat.SeenDamagedUntil - Time.time) + "s";
            if (Tow.Speed() < MegaModule.SafeSpeed && WakeRig.RiderId >= 0)
                text += "\nTOO SLOW - it's closing in on your wakeboarder!";
            float ww = 520f;
            Rect rr = new Rect((Screen.width - ww) * 0.5f, Screen.height - 168f, ww, 46f);
            PirateModule.DrawRect(rr, new Color(0f, 0f, 0f, 0.4f));
            GUI.Label(rr, text, _small);
        }

        private static void StomachHint()
        {
            float w = 520f;
            Rect r = new Rect((Screen.width - w) * 0.5f, Screen.height - 168f, w, 30f);
            PirateModule.DrawRect(r, new Color(0f, 0f, 0f, 0.45f));
            GUI.Label(r, "You are inside a megalodon.   E  grab the glowing dentures", _small);
        }

        internal static void Reset() => _hpShown = 0f;
    }
}
