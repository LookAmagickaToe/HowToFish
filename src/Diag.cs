using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SeagullSwarm
{
    /// <summary>
    /// Diagnostics. Everything goes to the BepInEx log as usual, and additionally to a dedicated,
    /// timestamped BepInEx\SeagullSwarm.log that is truncated on every launch, so one file is
    /// exactly one play session.
    ///
    /// Exceptions are rate-limited per call site: the first occurrence is logged with its full
    /// stack trace, repeats are counted and summarised every few seconds instead of spamming a
    /// line per frame.
    /// </summary>
    internal static class Diag
    {
        private const float RepeatSummarySeconds = 10f;

        private static readonly object Gate = new object();
        private static StreamWriter _file;
        private static bool _inUnityHook;

        private static readonly HashSet<string> SeenErrors = new HashSet<string>();
        private static readonly Dictionary<string, int> Repeats = new Dictionary<string, int>();
        private static readonly Dictionary<string, float> NextSummary = new Dictionary<string, float>();

        internal static string FilePath { get; private set; }

        internal static void Init(string path)
        {
            FilePath = path;
            try
            {
                _file = new StreamWriter(path, false) { AutoFlush = true };
            }
            catch (Exception e)
            {
                SeagullSwarmPlugin.Log.LogWarning("Could not open " + path + ": " + e.Message);
            }

            Application.logMessageReceived += OnUnityLog;
        }

        internal static void Info(string msg)
        {
            SeagullSwarmPlugin.Log.LogInfo(msg);
            Write("INFO ", msg);
        }

        internal static void Warn(string msg)
        {
            SeagullSwarmPlugin.Log.LogWarning(msg);
            Write("WARN ", msg);
        }

        internal static void Error(string msg)
        {
            SeagullSwarmPlugin.Log.LogError(msg);
            Write("ERROR", msg);
        }

        /// <summary>Per-bird detail. Only written when Debug/Verbose is on.</summary>
        internal static void Debug(string msg)
        {
            if (SeagullSwarmPlugin.Cfg == null || !SeagullSwarmPlugin.Cfg.Verbose.Value) return;
            SeagullSwarmPlugin.Log.LogDebug(msg);
            Write("DEBUG", msg);
        }

        internal static void Exception(string where, Exception e)
        {
            string key = where + "|" + e.GetType().Name + "|" + e.Message;

            if (SeenErrors.Add(key))
            {
                Error("EXCEPTION in " + where + ": " + e);
                NextSummary[key] = Time.realtimeSinceStartup + RepeatSummarySeconds;
                return;
            }

            int n;
            Repeats.TryGetValue(key, out n);
            Repeats[key] = ++n;

            float next;
            NextSummary.TryGetValue(key, out next);
            if (Time.realtimeSinceStartup < next) return;

            Error("EXCEPTION in " + where + " repeated " + n + "x in the last " + RepeatSummarySeconds +
                  "s: " + e.GetType().Name + ": " + e.Message);
            Repeats[key] = 0;
            NextSummary[key] = Time.realtimeSinceStartup + RepeatSummarySeconds;
        }

        /// <summary>
        /// Captures errors the game itself reports, e.g. an exception deep inside vanilla code that
        /// our spawning triggered. File-only: forwarding into BepInEx could loop straight back here,
        /// because BepInEx mirrors its own log into Unity's.
        /// </summary>
        private static void OnUnityLog(string condition, string stack, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            if (_inUnityHook) return;

            _inUnityHook = true;
            try
            {
                string key = "unity|" + condition;
                if (SeenErrors.Add(key))
                {
                    Write("UNITY", type + ": " + condition + (string.IsNullOrEmpty(stack) ? "" : "\n" + stack.TrimEnd()));
                    NextSummary[key] = Time.realtimeSinceStartup + RepeatSummarySeconds;
                    return;
                }

                int n;
                Repeats.TryGetValue(key, out n);
                Repeats[key] = ++n;

                float next;
                NextSummary.TryGetValue(key, out next);
                if (Time.realtimeSinceStartup < next) return;

                Write("UNITY", type + " repeated " + n + "x: " + condition);
                Repeats[key] = 0;
                NextSummary[key] = Time.realtimeSinceStartup + RepeatSummarySeconds;
            }
            finally
            {
                _inUnityHook = false;
            }
        }

        private static void Write(string level, string msg)
        {
            if (_file == null) return;

            string line = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + " t=" +
                          Time.time.ToString("0.00") + "] " + level + " " + msg;
            lock (Gate)
            {
                try { _file.WriteLine(line); }
                catch { /* disk trouble must never break gameplay */ }
            }
        }
    }
}
