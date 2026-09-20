using System;
using System.Collections.Generic;
using System.IO;
using Expanded.Quests;
using Newtonsoft.Json;
using Steamworks;
using UnityEngine;

namespace Expanded
{
    /// <summary>Everything the mod persists. Serialised as JSON; unknown fields are tolerated.</summary>
    internal sealed class ModSaveData
    {
        public int Version = 1;
        public List<QuestProgress> Quests = new List<QuestProgress>();
        public List<string> Flags = new List<string>();
        public List<string> Unlocks = new List<string>();
        /// <summary>Free-form counters modules can use, e.g. pirate raids survived.</summary>
        public Dictionary<string, int> Counters = new Dictionary<string, int>();
    }

    /// <summary>
    /// Mod progress lives in its own file next to the game's save, never inside it. Deleting the mod
    /// therefore leaves the vanilla save untouched, and a corrupt mod save can never break the game.
    ///
    /// Writes go through a temp file and a backup, because players alt-F4 mid-save.
    /// </summary>
    internal static class ModSave
    {
        private const string FileName = "expanded.json";

        private static string _path;
        private static ModSaveData _data = new ModSaveData();
        private static bool _dirty;
        private static float _nextAutoSave;

        internal static ModSaveData Data => _data;

        internal static string Path_ => _path;

        internal static void MarkDirty() => _dirty = true;

        /// <summary>
        /// Resolves the save path for the current Steam user, mirroring where the game puts its own
        /// saves so a player's mod progress travels with their profile.
        /// </summary>
        private static string ResolvePath()
        {
            string id = "local";
            try
            {
                // Mirrors how the game names its own save folder. If Steam is not up yet we fall
                // back to a shared folder rather than failing to save at all.
                ulong steamId = SteamUser.GetSteamID().m_SteamID;
                if (steamId != 0UL) id = steamId.ToString();
            }
            catch (Exception e)
            {
                Diag.Debug("ModSave: Steam id unavailable (" + e.Message + "), using 'local'.");
            }

            string dir = System.IO.Path.Combine(
                System.IO.Path.Combine(Application.persistentDataPath, "Saves"), id);
            Directory.CreateDirectory(dir);
            return System.IO.Path.Combine(dir, FileName);
        }

        internal static void Load()
        {
            try
            {
                _path = ResolvePath();
            }
            catch (Exception e)
            {
                Diag.Exception("ModSave.ResolvePath", e);
                _data = new ModSaveData();
                return;
            }

            _data = ReadFile(_path) ?? ReadFile(_path + ".backup") ?? new ModSaveData();
            _dirty = false;
            Diag.Info("ModSave: loaded " + _data.Quests.Count + " quest record(s), " + _data.Flags.Count +
                      " flag(s), " + _data.Unlocks.Count + " unlock(s) from " + _path);
        }

        private static ModSaveData ReadFile(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                string json = File.ReadAllText(path);
                if (string.IsNullOrEmpty(json)) return null;

                ModSaveData d = JsonConvert.DeserializeObject<ModSaveData>(json);
                if (d == null) return null;

                // Defend against a hand-edited or truncated file.
                d.Quests ??= new List<QuestProgress>();
                d.Flags ??= new List<string>();
                d.Unlocks ??= new List<string>();
                d.Counters ??= new Dictionary<string, int>();
                return d;
            }
            catch (Exception e)
            {
                Diag.Warn("ModSave: could not read " + path + " (" + e.Message + ").");
                return null;
            }
        }

        /// <summary>Writes only if something changed. Call freely.</summary>
        internal static void SaveIfDirty(bool force = false)
        {
            if (!_dirty && !force) return;
            if (string.IsNullOrEmpty(_path)) return;

            try
            {
                string json = JsonConvert.SerializeObject(_data, Formatting.Indented);
                string tmp = _path + ".tmp";

                File.WriteAllText(tmp, json);

                // Keep the previous good file as a backup, then swap the new one in.
                if (File.Exists(_path))
                {
                    string backup = _path + ".backup";
                    File.Copy(_path, backup, true);
                }
                File.Copy(tmp, _path, true);
                File.Delete(tmp);

                _dirty = false;
                Diag.Debug("ModSave: written to " + _path);
            }
            catch (Exception e)
            {
                Diag.Exception("ModSave.Save", e);
            }
        }

        /// <summary>Debounced autosave, called from the plugin's update loop.</summary>
        internal static void Tick()
        {
            if (!_dirty || Time.unscaledTime < _nextAutoSave) return;
            _nextAutoSave = Time.unscaledTime + 5f;
            SaveIfDirty();
        }

        // ------------------------------------------------------------------ convenience

        internal static bool HasUnlock(string key) => _data.Unlocks.Contains(key);

        internal static bool AddUnlock(string key)
        {
            if (string.IsNullOrEmpty(key) || _data.Unlocks.Contains(key)) return false;
            _data.Unlocks.Add(key);
            MarkDirty();
            return true;
        }

        internal static int Counter(string key)
        {
            int v;
            return _data.Counters.TryGetValue(key, out v) ? v : 0;
        }

        internal static void SetCounter(string key, int value)
        {
            _data.Counters[key] = value;
            MarkDirty();
        }

        internal static void AddCounter(string key, int delta) => SetCounter(key, Counter(key) + delta);

        /// <summary>Wipes mod progress only. The game's own save is never touched.</summary>
        internal static void ResetAll()
        {
            _data = new ModSaveData();
            MarkDirty();
            SaveIfDirty(true);
            Diag.Warn("ModSave: mod progress reset.");
        }
    }
}
