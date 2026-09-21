using System;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SeagullSwarm
{
    /// <summary>
    /// Shows the swarm on the game's own boss bar: the name line reads "Seagull Swarm - Wave 2/5", the
    /// health bar is the gulls still flying in this wave, the thin time bar and the last-ten-seconds
    /// countdown are the wave's time limit.
    ///
    /// The bar normally follows a boss creature; the swarm has none, so its images and texts are filled
    /// in directly. A real boss always wins: while the game has one, this bar is left alone and the
    /// caller falls back to its own box.
    /// </summary>
    internal static class SwarmBossBar
    {
        private static readonly FieldInfo FUiInstance = AccessTools.Field(typeof(PlayerUI), "_instance");
        private static readonly FieldInfo FBossUi = AccessTools.Field(typeof(PlayerUI), "_bossUI");
        private static readonly FieldInfo FGroup = AccessTools.Field(typeof(BossUI), "_bossCanvasLerped");
        private static readonly FieldInfo FName = AccessTools.Field(typeof(BossUI), "_bossNameText");
        private static readonly FieldInfo FHealth = AccessTools.Field(typeof(BossUI), "_bossHealth");
        private static readonly FieldInfo FHealthLerped = AccessTools.Field(typeof(BossUI), "_bossHealthLerped");
        private static readonly FieldInfo FTime = AccessTools.Field(typeof(BossUI), "_timeLeftImage");
        private static readonly FieldInfo FCountGroup = AccessTools.Field(typeof(BossUI), "_countdownGroup");
        private static readonly FieldInfo FCountText = AccessTools.Field(typeof(BossUI), "_timeCountdownText");

        private static bool _showing;
        private static bool _warned;

        /// <summary>Fills the game's boss bar. False if it can't (a real boss owns it, or the UI changed).</summary>
        internal static bool Show(string title, float gullsFraction, float timeFraction, float secondsLeft)
        {
            if (BossManager.Boss != null) { if (_showing) Hide(); return false; }

            try
            {
                object ui = FUiInstance?.GetValue(null);
                var boss = ui != null ? FBossUi?.GetValue(ui) as BossUI : null;
                if (boss == null) return false;

                var group = FGroup.GetValue(boss) as CanvasGroup;
                var name = FName.GetValue(boss) as TextMeshProUGUI;
                var health = FHealth.GetValue(boss) as Image;
                var lerped = FHealthLerped.GetValue(boss) as Image;
                var time = FTime.GetValue(boss) as Image;
                var countGroup = FCountGroup.GetValue(boss) as CanvasGroup;
                var countText = FCountText.GetValue(boss) as TextMeshProUGUI;
                if (group == null || name == null || health == null) return false;

                if (!_showing)
                {
                    try { health.color = GameInfo.RedColor; } catch { }
                    if (lerped != null) lerped.fillAmount = 1f;
                    _showing = true;
                }

                group.alpha = Mathf.MoveTowards(group.alpha, 1f, Time.unscaledDeltaTime * 2f);
                name.text = title;
                health.fillAmount = Mathf.Clamp01(gullsFraction);
                // The pale "damage taken" bar trails the real one, like it does for a boss.
                if (lerped != null)
                    lerped.fillAmount = Mathf.MoveTowards(lerped.fillAmount, health.fillAmount, Time.unscaledDeltaTime * 0.6f);
                if (time != null) time.fillAmount = Mathf.Clamp01(timeFraction);

                if (countGroup != null && countText != null)
                {
                    bool last = secondsLeft < 10f && secondsLeft > 0f;
                    countGroup.gameObject.SetActive(last);
                    countGroup.alpha = last ? 1f : 0f;
                    if (last) countText.text = Mathf.CeilToInt(secondsLeft).ToString();
                }
                return true;
            }
            catch (Exception e)
            {
                if (!_warned) { _warned = true; Expanded.Diag.Exception("SwarmBossBar.Show", e); }
                return false;
            }
        }

        internal static void Hide()
        {
            if (!_showing) return;
            _showing = false;
            if (BossManager.Boss != null) return;   // a real boss took the bar over; it's theirs now
            try
            {
                object ui = FUiInstance?.GetValue(null);
                var boss = ui != null ? FBossUi?.GetValue(ui) as BossUI : null;
                if (boss == null) return;
                if (FGroup.GetValue(boss) is CanvasGroup group) group.alpha = 0f;
                if (FCountGroup.GetValue(boss) is CanvasGroup countGroup) countGroup.alpha = 0f;
            }
            catch (Exception e)
            {
                Expanded.Diag.Exception("SwarmBossBar.Hide", e);
            }
        }
    }
}
