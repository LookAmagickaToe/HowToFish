using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace Expanded.Pirates
{
    /// <summary>
    /// Puts the chart mark on the game's own radar - the boat radar and the handheld map - as one
    /// more dot, pinged by the sweep exactly like the island dots, but in its own colour.
    ///
    /// Each radar gets a copy of one of its island dots (same sprite, same parent, same scale), driven
    /// through the game's own MapDot so position, clamping at the rim and the ping fade all behave
    /// like the rest of the radar.
    /// </summary>
    internal static class ChartRadar
    {
        private static readonly FieldInfo FIslandDots = AccessTools.Field(typeof(RadarUI), "_islandDots");
        private static readonly FieldInfo FIsOn = AccessTools.Field(typeof(RadarUI), "_isOn");
        private static readonly FieldInfo FPlayerPos = AccessTools.Field(typeof(RadarUI), "_localPlayerPos");
        private static readonly FieldInfo FMapScale = AccessTools.Field(typeof(RadarUI), "_mapScale");
        private static readonly FieldInfo FZoomMul = AccessTools.Field(typeof(RadarUI), "_zoomMultiplier");
        private static readonly FieldInfo FMaxDist = AccessTools.Field(typeof(RadarUI), "_maxPosDist");
        private static readonly FieldInfo FSweep = AccessTools.Field(typeof(RadarUI), "_radarSweepDir");
        private static readonly FieldInfo FPingAngle = AccessTools.Field(typeof(RadarUI), "_angleForPing");
        private static readonly FieldInfo FDotImage = AccessTools.Field(typeof(MapDot), "_dot");

        internal static readonly Color MarkColour = new Color(1f, 0.25f, 0.2f, 0f);

        private sealed class Entry
        {
            public RadarUI Radar;
            public MapDot Dot;
            public Image Image;
        }

        private static readonly List<Entry> Entries = new List<Entry>();
        private static float _nextScan;
        private static bool _warned;

        /// <summary>True if a radar is switched on right now (boat radar or map in hand).</summary>
        internal static bool AnyRadarOn { get; private set; }

        internal static bool Available =>
            FIslandDots != null && FIsOn != null && FPlayerPos != null && FMapScale != null && FZoomMul != null &&
            FMaxDist != null && FSweep != null && FPingAngle != null && FDotImage != null;

        /// <summary>Call every frame while the mark is shown.</summary>
        internal static void Tick(Vector3 mark)
        {
            if (!Available)
            {
                if (!_warned) { _warned = true; Diag.Warn("ChartRadar: the game's radar changed; the mark stays off the radar."); }
                return;
            }

            if (Time.time >= _nextScan)
            {
                _nextScan = Time.time + 2f;
                Scan();
            }

            bool anyOn = false;
            Vector2 world = new Vector2(mark.x, mark.z);
            for (int i = Entries.Count - 1; i >= 0; i--)
            {
                Entry e = Entries[i];
                if (e.Radar == null || e.Image == null) { Entries.RemoveAt(i); continue; }
                if (!(bool)FIsOn.GetValue(e.Radar) || !e.Radar.isActiveAndEnabled) continue;
                anyOn = true;

                try
                {
                    e.Dot.Move(world, (Vector2)FPlayerPos.GetValue(e.Radar), (float)FMapScale.GetValue(e.Radar),
                               (float)FZoomMul.GetValue(e.Radar), (float)FMaxDist.GetValue(e.Radar));

                    // Same rule as the game's own dots: light up when the sweep passes over.
                    Vector2 sweep = (Vector2)FSweep.GetValue(e.Radar);
                    float pingAngle = (float)FPingAngle.GetValue(e.Radar);
                    if (!e.Dot.RecentlyPinged && Vector2.Angle(e.Dot.RealtimeLocalPos, sweep) <= pingAngle * 2f)
                        e.Dot.TriggerPing();
                }
                catch (Exception ex)
                {
                    Diag.Exception("ChartRadar.Tick", ex);
                    Entries.RemoveAt(i);
                }
            }
            AnyRadarOn = anyOn;
        }

        /// <summary>Finds radars without a chart dot yet and gives them one.</summary>
        private static void Scan()
        {
            foreach (RadarUI radar in Resources.FindObjectsOfTypeAll<RadarUI>())
            {
                if (radar == null || !radar.gameObject.scene.IsValid()) continue;   // skip prefabs
                if (Entries.Exists(x => x.Radar == radar)) continue;

                try
                {
                    var islandDots = FIslandDots.GetValue(radar) as MapDot[];
                    Image template = null;
                    if (islandDots != null)
                        foreach (MapDot d in islandDots)
                            if (d != null && (template = FDotImage.GetValue(d) as Image) != null) break;
                    if (template == null) continue;

                    GameObject copy = UnityEngine.Object.Instantiate(template.gameObject, template.transform.parent, false);
                    copy.name = "ExpandedChartMark";
                    copy.transform.localScale = template.transform.localScale * 1.6f;
                    Image img = copy.GetComponent<Image>();
                    img.color = MarkColour;   // starts hidden, like every dot, until the sweep finds it

                    var dot = new MapDot();
                    FDotImage.SetValue(dot, img);
                    Entries.Add(new Entry { Radar = radar, Dot = dot, Image = img });
                    Diag.Info("ChartRadar: chart mark added to radar '" + radar.name + "'.");
                }
                catch (Exception e)
                {
                    Diag.Exception("ChartRadar.Scan", e);
                }
            }
        }

        /// <summary>Removes the mark from every radar (quest over, session ended).</summary>
        internal static void Clear()
        {
            foreach (Entry e in Entries)
                if (e.Image != null) UnityEngine.Object.Destroy(e.Image.gameObject);
            Entries.Clear();
            AnyRadarOn = false;
            _nextScan = 0f;
        }
    }
}
