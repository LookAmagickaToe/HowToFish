using System.Collections.Generic;
using UnityEngine;

namespace Expanded.Pirates
{
    /// <summary>
    /// Speech bubbles over the Gull Pirates: Captain Squawk announces himself when they arrive, and
    /// the crew squawk about food through the fight.
    ///
    /// Purely local and cosmetic - every client picks its own lines from the ship's replicated state
    /// (stance, health, who is dead), so nothing about it is networked.
    /// </summary>
    internal static class PirateChatter
    {
        private static readonly string[] Arrival =
        {
            "SQUAWK! I am Captain Squawk of the Greedy Gull!",
            "I smell... GRILLED. Hand over the snacks!",
            "Nobody out-eats the Gull Pirates! Nobody!"
        };

        private static readonly string[] Fight =
        {
            "Chips! I want chips!", "Squawk! SQUAWK!", "That's MY sandwich!", "Fire the crumbs!",
            "Aim for the lunchbox!", "Mine! Mine! Mine!", "Is that a grilled herring?! GET IT!",
            "I haven't eaten in three whole minutes!", "Give us the food and nobody gets pecked!",
            "Load the cannon! With what? ...With FURY!", "Arr! Also: caw!"
        };

        private static readonly string[] Hurt =
        {
            "We're taking on water - and gravy!", "Save the snacks first!", "Captain, I'm too hungry to die!",
            "This is fine. This is all fine. Squawk."
        };

        private static readonly string[] Captain =
        {
            "Bring me their barbecue!", "Broadside! Then lunch!", "I didn't sail all this way for SALAD!",
            "Steady, lads. Snacks await."
        };

        private const string Leaving = "We'll be back... for dessert!";
        private const string Surrender = "We give up! We'll share! ...No we won't. But we give up.";
        private const string Sinking = "Abandon ship! GRAB THE CHIPS!";

        private sealed class Bubble
        {
            public Transform Anchor;
            public string Text;
            public float Until;
        }

        private static readonly List<Bubble> Bubbles = new List<Bubble>();
        private static PirateShip _ship;
        private static float _arrivedAt;
        private static int _arrivalLine;
        private static float _nextLine;
        private static PirateShip.Stance _lastStance;
        private static GUIStyle _style;

        internal static void ClientTick()
        {
            PirateShip ship = PirateShip.Current;
            if (ship == null || PirateModule.Cfg == null || !PirateModule.Cfg.CrewChatter.Value)
            {
                _ship = null;
                Bubbles.Clear();
                return;
            }

            if (ship != _ship)
            {
                _ship = ship;
                _arrivedAt = Time.time;
                _arrivalLine = 0;
                _nextLine = Time.time + 1f;
                _lastStance = PirateShip.Stance.Closing;
                Bubbles.Clear();
            }

            Bubbles.RemoveAll(b => b.Anchor == null || Time.time > b.Until);

            PirateShip.Stance stance = PirateModule.Instance != null ? PirateModule.Instance.ReplicatedStance : PirateShip.Stance.Closing;
            if (stance != _lastStance)
            {
                _lastStance = stance;
                if (stance == PirateShip.Stance.Surrendered) { Say(Any(ship, true), Surrender, 7f); return; }
                if (stance == PirateShip.Stance.Sinking) { Say(Any(ship, true), Sinking, 6f); return; }
                if (stance == PirateShip.Stance.Leaving) { Say(Pick(ship, PirateCrew.Role.Captain), Leaving, 6f); return; }
            }
            if (stance == PirateShip.Stance.Surrendered || stance == PirateShip.Stance.Sinking) return;
            if (Time.time < _nextLine) return;

            // The captain introduces himself first, then the crew take turns.
            if (_arrivalLine < Arrival.Length)
            {
                Say(Pick(ship, PirateCrew.Role.Captain), Arrival[_arrivalLine++], 4.5f);
                _nextLine = Time.time + 4.8f;
                return;
            }

            bool hurt = PirateModule.Instance != null && PirateModule.Instance.ReplicatedHealthFraction < 0.35f;
            CrewMember who = Any(ship, false);
            string[] pool = hurt ? Hurt : (who != null && who.Role == PirateCrew.Role.Captain ? Captain : Fight);
            Say(who, pool[Random.Range(0, pool.Length)], 3.5f);
            _nextLine = Time.time + Random.Range(4.5f, 8f);
        }

        private static CrewMember Pick(PirateShip ship, PirateCrew.Role role)
        {
            foreach (CrewMember c in ship.GetComponentsInChildren<CrewMember>(false))
                if (!c.Dead && c.Role == role) return c;
            return Any(ship, false);
        }

        private static CrewMember Any(PirateShip ship, bool deadToo)
        {
            var alive = new List<CrewMember>();
            foreach (CrewMember c in ship.GetComponentsInChildren<CrewMember>(false))
                if (deadToo || !c.Dead) alive.Add(c);
            return alive.Count > 0 ? alive[Random.Range(0, alive.Count)] : null;
        }

        private static void Say(CrewMember who, string text, float seconds)
        {
            if (who == null) return;
            Bubbles.RemoveAll(b => b.Anchor == who.transform);
            Bubbles.Add(new Bubble { Anchor = who.transform, Text = text, Until = Time.time + seconds });
        }

        internal static void OnGUI()
        {
            if (Bubbles.Count == 0) return;
            Camera cam = null;
            try { cam = GameInfo.CurCamera; } catch { }
            if (cam == null) return;

            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = true, fontStyle = FontStyle.Bold };
                _style.normal.textColor = new Color(0.12f, 0.1f, 0.08f);
            }

            foreach (Bubble b in Bubbles)
            {
                if (b.Anchor == null) continue;
                Vector3 head = b.Anchor.position + Vector3.up * 2.3f;
                Vector3 sp = cam.WorldToScreenPoint(head);
                if (sp.z <= 0f || sp.z > 160f) continue;

                // Bigger when close, still readable at cannon range.
                int size = Mathf.RoundToInt(Mathf.Lerp(20f, 13f, Mathf.InverseLerp(15f, 120f, sp.z)));
                _style.fontSize = size;
                float w = Mathf.Lerp(300f, 200f, Mathf.InverseLerp(15f, 120f, sp.z));
                float h = _style.CalcHeight(new GUIContent(b.Text), w - 16f) + 12f;
                var r = new Rect(sp.x - w * 0.5f, Screen.height - sp.y - h, w, h);

                // Fade out over the last half second.
                float a = Mathf.Clamp01((b.Until - Time.time) / 0.5f);
                PirateModule.DrawRect(new Rect(r.x - 2f, r.y - 2f, r.width + 4f, r.height + 4f), new Color(0.1f, 0.08f, 0.06f, 0.85f * a));
                PirateModule.DrawRect(r, new Color(1f, 0.97f, 0.9f, 0.95f * a));
                Color c = _style.normal.textColor; c.a = a; _style.normal.textColor = c;
                GUI.Label(new Rect(r.x + 8f, r.y + 6f, r.width - 16f, r.height - 12f), b.Text, _style);
            }
        }
    }
}
