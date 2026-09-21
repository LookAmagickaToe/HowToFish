using System;
using System.Collections.Generic;
using FishNet;
using UnityEngine;
using Random = UnityEngine.Random;

using Expanded;

namespace SeagullSwarm
{
    /// <summary>
    /// Host-side brain for the whole encounter. Attached to the BirdManager GameObject by a
    /// Harmony patch on OnStartServer, so it only ever exists on the server.
    ///
    /// Counts provocation kills and runs the waves, Zombies-style - gulls only, no boss:
    ///   clear every wave             -> victory
    ///   a wave's time limit runs out -> the flock flies off, no victory
    ///   everyone is down             -> the flock flies off, no victory
    /// Each wave is bigger than the last (Waves.Growth), with a short breather in between.
    /// </summary>
    internal class SwarmDirector : MonoBehaviour
    {
        private enum Phase { Idle, Summoning, Running, Cooldown }

        internal static SwarmDirector Active;

        private Phase _phase = Phase.Idle;

        private readonly List<float> _provokeKills = new List<float>();
        private float _cooldownUntil;

        private Vector3 _anchor;
        internal Vector3 Anchor { get { return _anchor; } }

        private int[] _waveSizes;
        private float _waveDeadline;
        private float _waveLimit;
        private bool _inBreak;
        private float _breakUntil;
        private float _nextHudAt;
        private int _waveIndex;
        private bool _wavesDone;
        private int _totalPlanned;
        private int _removedTotal;
        private readonly List<GullAttacker> _birds = new List<GullAttacker>();
        private float _nextGroupAt;
        private float _lastWaveAngle = float.NaN;
        private bool _arrivalScreamPending;

        private readonly List<KeyValuePair<GullAttacker, float>> _fleeing = new List<KeyValuePair<GullAttacker, float>>();

        private Item _gullPrefab;

        private float _attachedAt;
        private bool _autoStartFired;

        // --- diagnostics ---
        private float _encounterStartedAt;
        private float _waveStartedAt;
        private float _nextStatusAt;
        private float _lastGroupAt;
        private int _waveDives, _waveHits, _waveCrashes, _totalDives, _totalHits, _totalCrashes;
        private int _waveKilled, _waveVanished;
        private bool _warnedFar, _warnedStall;

        private bool InEncounter { get { return _phase == Phase.Summoning || _phase == Phase.Running; } }
        internal bool InEncounterPublic { get { return InEncounter; } }

        /// <summary>The swarm no longer has a boss; the game's boss bar is always left to the game.</summary>
        internal bool OwnsBossBar => false;

        private void Awake()
        {
            Active = this;
            _anchor = transform.position;
            _attachedAt = Time.time;
        }

        private void OnDestroy()
        {
            if (InEncounter)
                Diag.Warn("Director destroyed mid-encounter (phase " + _phase + ", wave " + (_waveIndex + 1) +
                          ") - session ended or island changed.");
            else
                Diag.Info("Director detached (session ended or island changed).");

            if (Active == this) Active = null;
        }

        // ------------------------------------------------------------------ provocation

        internal void RegisterProvocationKill()
        {
            if (_phase != Phase.Idle)
            {
                Diag.Debug("Seagull kill ignored (phase " + _phase + ").");
                return;
            }
            // The game restores dead gulls from the save when an island loads, and each one reports a
            // death. Those aren't kills; only count what happens once the island is up and running.
            if (Time.time - _attachedAt < 5f)
            {
                Diag.Info("Seagull death while the island was loading - not counted.");
                return;
            }

            _provokeKills.Add(Time.time);

            float delay = SwarmModule.Cfg.LureReplaceSeconds.Value;
            if (delay > 0f) _replaceAt.Add(Time.time + delay);

            Say("Seagull killed (" + _provokeKills.Count + "/" +
                SwarmModule.Cfg.KillsToProvoke.Value + ")");
        }

        private void PruneProvokeWindow()
        {
            float cutoff = Time.time - SwarmModule.Cfg.ProvokeWindowSeconds.Value;
            int before = _provokeKills.Count;
            for (int i = _provokeKills.Count - 1; i >= 0; i--)
                if (_provokeKills[i] < cutoff) _provokeKills.RemoveAt(i);

            if (_provokeKills.Count < before)
                Diag.Info("Old seagull kill(s) expired from the window; now " + _provokeKills.Count + "/" +
                          SwarmModule.Cfg.KillsToProvoke.Value + ".");
        }

        /// <summary>Called by a bird at the end of each dive cycle.</summary>
        internal void ReportDive(bool hit)
        {
            _waveDives++;
            _totalDives++;
            if (hit) { _waveHits++; _totalHits++; }
        }

        internal void ReportCrash()
        {
            _waveCrashes++;
            _totalCrashes++;
        }

        // ------------------------------------------------------------------ main loop

        private void Update()
        {
            if (!InstanceFinder.IsServerStarted) return;

            try
            {
                Tick();
            }
            catch (Exception e)
            {
                Diag.Exception("SwarmDirector.Update [phase " + _phase + "]", e);
            }
        }

        private void Tick()
        {
            UpdateAnchor();
            PruneProvokeWindow();
            HandleTestControls();
            TickFleeing();
            TickLure();
            TickReplacements();

            switch (_phase)
            {
                case Phase.Idle:
                    if (_provokeKills.Count >= SwarmModule.Cfg.KillsToProvoke.Value)
                    {
                        Diag.Info("Kill threshold reached - provoking the flock.");
                        StartEncounter();
                    }
                    break;

                case Phase.Running:
                    TickRunning();
                    break;

                case Phase.Cooldown:
                    if (Time.time >= _cooldownUntil)
                    {
                        _phase = Phase.Idle;
                        _provokeKills.Clear();
                        Diag.Info("Cooldown over - the flock can be provoked again.");
                    }
                    break;
            }

            float interval = SwarmModule.Cfg.StatusIntervalSeconds.Value;
            if (InEncounter && interval > 0f && Time.time >= _nextStatusAt)
            {
                _nextStatusAt = Time.time + interval;
                LogStatus();
            }

            if (InEncounter && Time.time >= _nextHudAt)
            {
                _nextHudAt = Time.time + 0.5f;
                SendHud();
            }
        }

        // ------------------------------------------------------------------ HUD (every player)

        /// <summary>Wave, gulls left and time left, for the swarm bar on every player's screen.</summary>
        private void SendHud()
        {
            bool active = _phase == Phase.Running;
            int wave = _waveIndex + 1;
            int waves = _waveSizes != null ? _waveSizes.Length : 0;
            int left = _birds.Count;
            int size = _waveSizes != null && _waveIndex < _waveSizes.Length ? _waveSizes[_waveIndex] : left;
            bool rest = _inBreak;
            float seconds = Mathf.Max(0f, (rest ? _breakUntil : _waveDeadline) - Time.time);
            float total = rest ? Mathf.Max(1f, SwarmModule.Cfg.WaveBreakSeconds.Value) : Mathf.Max(1f, _waveLimit);
            ModNet.SendToAll(Msg.SwarmStatus, w =>
            {
                w.Write(active);
                w.Write((byte)Mathf.Clamp(wave, 0, 255));
                w.Write((byte)Mathf.Clamp(waves, 0, 255));
                w.Write((ushort)Mathf.Clamp(left, 0, 65535));
                w.Write(seconds);
                w.Write(rest);
                w.Write((ushort)Mathf.Clamp(size, 0, 65535));
                w.Write(total);
            });
        }

        private static void SendHudClosed()
        {
            ModNet.SendToAll(Msg.SwarmStatus, w =>
            {
                w.Write(false); w.Write((byte)0); w.Write((byte)0); w.Write((ushort)0); w.Write(0f); w.Write(false);
                w.Write((ushort)0); w.Write(1f);
            });
        }

        /// <summary>A line in the middle of every player's screen, plus the host's chat log.</summary>
        private static void Banner(string text)
        {
            Say(text);
            try { Expanded.Pirates.PirateModule.Announce(text); }
            catch (Exception e) { Diag.Exception("Swarm banner", e); }
        }

        private void UpdateAnchor()
        {
            List<Player> alive = PlayerManager.AlivePlayers;
            if (alive.Count > 0)
            {
                Vector3 sum = Vector3.zero;
                int n = 0;
                for (int i = 0; i < alive.Count; i++)
                {
                    if (alive[i] == null) continue;
                    sum += alive[i].Transform.position;
                    n++;
                }
                if (n > 0)
                {
                    _anchor = Vector3.Lerp(_anchor, sum / n, Time.deltaTime * 1.5f);
                    return;
                }
            }

            if (BoatManager.Boat != null)
                _anchor = Vector3.Lerp(_anchor, Expanded.Pirates.BoatMount.Frame(BoatManager.Boat).position, Time.deltaTime * 1.5f);
        }

        // ------------------------------------------------------------------ status snapshot

        private void LogStatus()
        {
            int approach = 0, circle = 0, climb = 0, hover = 0, dive = 0, peel = 0, missing = 0;
            float farthest = 0f, lowest = float.MaxValue;

            for (int i = 0; i < _birds.Count; i++)
            {
                GullAttacker a = _birds[i];
                if (a == null) { missing++; continue; }

                switch (a.CurrentState)
                {
                    case GullAttacker.State.Approach: approach++; break;
                    case GullAttacker.State.Circle: circle++; break;
                    case GullAttacker.State.Climb: climb++; break;
                    case GullAttacker.State.Hover: hover++; break;
                    case GullAttacker.State.Dive: dive++; break;
                    case GullAttacker.State.PeelOff: peel++; break;
                }

                Vector3 d = a.transform.position - _anchor;
                float flat = new Vector2(d.x, d.z).magnitude;
                if (flat > farthest) farthest = flat;
                if (d.y < lowest) lowest = d.y;
            }
            if (lowest == float.MaxValue) lowest = 0f;

            string timeLeft = _inBreak
                ? "break " + Mathf.Max(0f, _breakUntil - Time.time).ToString("0") + "s"
                : Mathf.Max(0f, _waveDeadline - Time.time).ToString("0") + "s";

            float sinceGroup = _lastGroupAt > 0f ? Time.time - _lastGroupAt : -1f;
            float waterRel = GullAttacker.WaterY() - _anchor.y;

            Diag.Info("STATUS " + _phase +
                      " | wave " + (_waveIndex + 1) + "/" + (_waveSizes != null ? _waveSizes.Length : 0) +
                      (_wavesDone ? " (waves done)" : "") +
                      " | alive " + _birds.Count + " [approach " + approach + ", circle " + circle + ", climb " + climb +
                      ", hover " + hover + ", dive " + dive + ", peel " + peel +
                      (missing > 0 ? ", NULL " + missing : "") + "]" +
                      " | fleeing " + _fleeing.Count +
                      " | removed " + _removedTotal + "/" + _totalPlanned +
                      " | time left " + timeLeft +
                      " | players " + PlayerManager.AlivePlayers.Count + "/" + PlayerManager.Players.Count +
                      " | dives " + _totalDives + " hits " + _totalHits + " crashes " + _totalCrashes +
                      " | last group " + (sinceGroup < 0 ? "never" : sinceGroup.ToString("0.0") + "s ago") +
                      " | farthest " + farthest.ToString("0") + "m, lowest " + lowest.ToString("0.0") +
                      "m (water " + waterRel.ToString("0.0") + "m)");

            if (farthest > 170f && !_warnedFar)
            {
                _warnedFar = true;
                Diag.Warn("A bird is " + farthest.ToString("0") + "m from the party - it may have got lost.");
            }

            bool shouldBeDiving = _phase == Phase.Running && !_wavesDone && circle > 0 &&
                                  PlayerManager.AlivePlayers.Count > 0;
            float stallRef = _lastGroupAt > 0f ? _lastGroupAt : _waveStartedAt;
            if (shouldBeDiving && Time.time - stallRef > 25f && !_warnedStall)
            {
                _warnedStall = true;
                Diag.Warn("No dive launched in " + (Time.time - stallRef).ToString("0") +
                          "s although birds are circling and players are alive - players in cover, " +
                          "or dive scheduling is stuck.");
            }
        }

        // ------------------------------------------------------------------ testing

        private void HandleTestControls()
        {
            SwarmConfig cfg = SwarmModule.Cfg;

            float auto = cfg.AutoStartAfterSeconds.Value;
            if (auto > 0f && !_autoStartFired && _phase == Phase.Idle && Time.time - _attachedAt >= auto)
            {
                _autoStartFired = true;
                Say("Auto-start after " + auto.ToString("0") + "s.");
                ForceStart();
            }

            if (!cfg.HotkeysEnabled.Value || !ExpandedPlugin.DevHotkeys) return;

            // Don't fire while the host is typing in chat or otherwise has input blocked.
            if (ChatManager.IsTyping) return;
            if (Player.LocalPlayer != null && Player.LocalPlayer.BlockInputs) return;

            if (Input.GetKeyDown(cfg.StartKey.Value)) { Diag.Info("Hotkey " + cfg.StartKey.Value + " (start)"); ForceStart(); }
            else if (Input.GetKeyDown(cfg.SkipWaveKey.Value)) { Diag.Info("Hotkey " + cfg.SkipWaveKey.Value + " (skip wave)"); SkipWave(); }
            else if (Input.GetKeyDown(cfg.StopKey.Value)) { Diag.Info("Hotkey " + cfg.StopKey.Value + " (stop)"); ForceStop(); }
        }

        /// <summary>Module debug key: start an encounter, or stop the one that is running.</summary>
        internal void DebugToggleEncounter()
        {
            if (!InstanceFinder.IsServerStarted)
            {
                Say("Only the host can start the swarm.");
                return;
            }
            if (InEncounter) ForceStop(); else ForceStart();
        }

        /// <summary>One-line summary for the debug overlay.</summary>
        internal string DebugStatus()
        {
            switch (_phase)
            {
                case Phase.Idle:
                    return "idle (" + _provokeKills.Count + "/" + SwarmModule.Cfg.KillsToProvoke.Value + " kills)";
                case Phase.Running:
                    return "wave " + (_waveIndex + 1) + "/" + (_waveSizes != null ? _waveSizes.Length : 0) +
                           (_inBreak ? ", break" : ", " + _birds.Count + " birds, " +
                            Mathf.Max(0f, _waveDeadline - Time.time).ToString("0") + "s left");
                case Phase.Cooldown:
                    return "cooldown " + Mathf.Max(0f, _cooldownUntil - Time.time).ToString("0") + "s";
                default:
                    return _phase.ToString();
            }
        }

        private void ForceStart()
        {
            if (InEncounter)
            {
                Say("Encounter already running.");
                return;
            }
            _provokeKills.Clear();
            StartEncounter();
        }

        private void SkipWave()
        {
            if (_phase != Phase.Running || _wavesDone)
            {
                Say("No wave to skip.");
                return;
            }
            Say("Skipping wave " + (_waveIndex + 1) + ".");
            // Reaped on the next frame; counted as removed, so the next wave spawns.
            DespawnBirds(_birds);
        }

        private void ForceStop()
        {
            if (!InEncounter)
            {
                Say("No encounter running.");
                return;
            }
            Say("Encounter stopped.");
            EndEncounter("Stopped by hotkey");
            _cooldownUntil = Time.time; // manual stop: allow an immediate restart
        }

        /// <summary>Log line, echoed into the host's own chat box (never sent to other players).</summary>
        private static void Say(string message)
        {
            Diag.Info("[chat] " + message);
            if (!SwarmModule.Cfg.ChatFeedback.Value) return;

            try { ChatManager.ChatMessage("<color=#E0B040>[Swarm]</color> " + message); }
            catch (Exception e) { Diag.Exception("ChatManager.ChatMessage", e); }
        }

        // ------------------------------------------------------------------ encounter

        private void StartEncounter()
        {
            SwarmConfig cfg = SwarmModule.Cfg;

            _encounterStartedAt = Time.time;
            _totalDives = _totalHits = _totalCrashes = 0;
            _lastGroupAt = 0f;
            _warnedFar = _warnedStall = false;
            _nextStatusAt = 0f;
            _wavesDone = false;
            _inBreak = false;
            _lastWaveAngle = float.NaN;

            if (!ResolvePrefabs())
            {
                Diag.Error("Could not resolve the seagull prefab; encounter aborted.");
                Say("ERROR: seagull prefab not found, encounter aborted. See log.");
                EnterCooldown();
                return;
            }

            BuildWavePlan();
            _waveIndex = 0;
            _removedTotal = 0;
            _birds.Clear();

            Banner("The flock has had enough! " + _waveSizes.Length + " waves - survive them all.");
            Diag.Info("Wave plan: " + string.Join(" / ", _waveSizes) + " = " + _totalPlanned +
                      ". Players: " + PlayerManager.AlivePlayers.Count + " alive of " + PlayerManager.Players.Count +
                      ". Anchor " + _anchor.ToString("F1") + ", water " + GullAttacker.WaterY().ToString("0.0") + ".");

            _phase = Phase.Running;
            StartWave(0);
        }

        /// <summary>
        /// Zombies-style: clear a wave within its time limit, catch your breath, next wave. Clear the
        /// last one and the swarm is beaten. Run out of time, or have everyone go down, and the flock
        /// simply leaves - no victory.
        /// </summary>
        private void TickRunning()
        {
            SwarmConfig cfg = SwarmModule.Cfg;

            if (PlayerManager.Players.Count > 0 && PlayerManager.AlivePlayers.Count == 0)
            {
                Banner("Everyone's down - the flock loses interest and leaves.");
                EndEncounter("Defeat: party wiped in wave " + (_waveIndex + 1));
                return;
            }

            if (_inBreak)
            {
                if (Time.time < _breakUntil) return;
                _inBreak = false;
                StartWave(_waveIndex);
                return;
            }

            ReapDeadBirds();

            if (_arrivalScreamPending) TryArrivalScream();

            if (_birds.Count == 0)
            {
                AdvanceWave();
                return;
            }

            if (Time.time >= _waveDeadline)
            {
                Banner("Time's up - wave " + (_waveIndex + 1) + " wasn't broken. The flock flies off.");
                EndEncounter("Defeat: wave " + (_waveIndex + 1) + " timer ran out with " + _birds.Count + " birds left");
                return;
            }

            if (Time.time >= _nextGroupAt) LaunchDiveGroup();
        }

        private void ReapDeadBirds()
        {
            for (int i = _birds.Count - 1; i >= 0; i--)
            {
                GullAttacker a = _birds[i];
                bool vanished = a == null || a.gameObject == null;
                Bird b = vanished ? null : a.GetComponent<Bird>();

                if (!vanished && b != null && b._hp.Value > 0) continue;

                _birds.RemoveAt(i);
                _removedTotal++;
                if (vanished || b == null) _waveVanished++; else _waveKilled++;

                Diag.Debug("Bird " + (vanished || b == null ? "despawned" : "killed") + " - " +
                           _birds.Count + " left in wave " + (_waveIndex + 1) + ".");
            }
        }

        private void AdvanceWave()
        {
            Diag.Info("Wave " + (_waveIndex + 1) + " cleared in " + (Time.time - _waveStartedAt).ToString("0") +
                      "s: " + _waveKilled + " killed (" + _waveCrashes + " crashed), " + _waveVanished +
                      " despawned, " + _waveDives + " dives, " + _waveHits + " hits.");

            _waveIndex++;
            if (_waveIndex < _waveSizes.Length)
            {
                float rest = Mathf.Max(0f, SwarmModule.Cfg.WaveBreakSeconds.Value);
                Banner("Wave " + _waveIndex + " broken! Wave " + (_waveIndex + 1) + " of " + _waveSizes.Length +
                       " in " + rest.ToString("0") + "s...");
                _inBreak = true;
                _breakUntil = Time.time + rest;
                return;
            }

            _waveIndex = _waveSizes.Length - 1;
            Banner("The swarm is broken! Every wave beaten.");
            RaiseStoryVictory();
            EndEncounter("Victory: all waves cleared");
        }

        /// <summary>
        /// Tells the story module the swarm was beaten. Decoupled on purpose: the swarm works
        /// perfectly well with the quest module disabled or absent.
        /// </summary>
        private static void RaiseStoryVictory()
        {
            try { Expanded.Quests.QuestModule.Instance?.SetFlag("swarm.defeated"); }
            catch (Exception e) { Diag.Exception("RaiseStoryVictory", e); }
        }

        /// <summary>Every ending goes through here: surviving gulls flee, then the cooldown starts.</summary>
        private void EndEncounter(string outcome)
        {
            Diag.Info(outcome + " after " + (Time.time - _encounterStartedAt).ToString("0") + "s in wave " +
                      (_waveIndex + 1) + ": " + _totalDives + " dives, " + _totalHits + " hits, " +
                      _totalCrashes + " crashes, " + _removedTotal + "/" + _totalPlanned + " birds removed.");

            FleeAll();
            EnterCooldown();
            SendHudClosed();
        }

        private void EnterCooldown()
        {
            _birds.Clear();
            _provokeKills.Clear();
            _wavesDone = false;
            _inBreak = false;
            _arrivalScreamPending = false;
            _cooldownUntil = Time.time + SwarmModule.Cfg.RetriggerCooldownSeconds.Value;
            _phase = Phase.Cooldown;
            Diag.Info("Cooldown " + SwarmModule.Cfg.RetriggerCooldownSeconds.Value + "s.");
        }

        private void FleeAll()
        {
            float until = Time.time + SwarmModule.Cfg.FleeSeconds.Value;
            int n = 0;
            for (int i = 0; i < _birds.Count; i++)
            {
                GullAttacker a = _birds[i];
                if (a == null) continue;
                Bird b = a.GetComponent<Bird>();
                if (b == null || b._hp.Value <= 0) continue;

                a.Flee();
                _fleeing.Add(new KeyValuePair<GullAttacker, float>(a, until));
                n++;
            }
            _birds.Clear();
            if (n > 0) Diag.Info(n + " gull(s) fleeing.");
        }

        private void TickFleeing()
        {
            if (_fleeing.Count == 0) return;

            List<GullAttacker> due = null;
            for (int i = _fleeing.Count - 1; i >= 0; i--)
            {
                GullAttacker a = _fleeing[i].Key;
                if (a == null) { _fleeing.RemoveAt(i); continue; }
                if (Time.time < _fleeing[i].Value) continue;

                if (due == null) due = new List<GullAttacker>();
                due.Add(a);
                _fleeing.RemoveAt(i);
            }
            if (due != null) DespawnBirds(due);
        }

        private static void DespawnBirds(List<GullAttacker> birds)
        {
            int n = 0;
            for (int i = 0; i < birds.Count; i++)
            {
                if (birds[i] == null) continue;
                try
                {
                    Item item = birds[i].GetComponent<Item>();
                    if (item != null) { item.DestroyItem((byte)DestroyReason.Immediate); n++; }
                }
                catch (Exception e)
                {
                    Diag.Exception("DespawnBirds", e);
                }
            }
            Diag.Debug("Despawned " + n + " bird(s).");
        }

        // ------------------------------------------------------------------ waves

        private void BuildWavePlan()
        {
            SwarmConfig cfg = SwarmModule.Cfg;

            int count = Mathf.Max(1, cfg.WaveCount.Value);
            _waveSizes = new int[count];
            _totalPlanned = 0;

            float size = Mathf.Max(1, cfg.FirstWaveSize.Value);
            for (int i = 0; i < count; i++)
            {
                _waveSizes[i] = Mathf.Max(1, Mathf.RoundToInt(size));
                _totalPlanned += _waveSizes[i];
                size *= cfg.WaveGrowth.Value;
            }
        }

        /// <summary>
        /// The whole wave appears as one flock, far out and low over the water in a single compass
        /// direction, then flies in. Each wave comes from a noticeably different side.
        /// </summary>
        private void StartWave(int index)
        {
            SwarmConfig cfg = SwarmModule.Cfg;

            _waveStartedAt = Time.time;
            _waveDives = _waveHits = _waveCrashes = _waveKilled = _waveVanished = 0;
            _warnedStall = false;

            float angle = PickWaveAngle(cfg.MinDirectionChange.Value * Mathf.Deg2Rad);
            _lastWaveAngle = angle;

            Vector3 dir = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            float waterY = GullAttacker.WaterY();
            Vector3 center = _anchor + dir * cfg.ApproachDistance.Value;
            center.y = Mathf.Max(waterY, _anchor.y - 2f) + cfg.ApproachHeight.Value;

            int n = _waveSizes[index];
            float limit = Mathf.Max(15f, cfg.WaveTimeBaseSeconds.Value + cfg.WaveTimePerBirdSeconds.Value * n);
            _waveLimit = limit;
            _waveDeadline = Time.time + limit;

            Banner("Wave " + (index + 1) + "/" + _waveSizes.Length + ": " + n + " gulls from " +
                   RelativeDirection(center) + "! " + Mathf.RoundToInt(limit) + "s to break them.");
            int ok = 0;
            float spread = cfg.ApproachSpread.Value;

            for (int i = 0; i < n; i++)
            {
                Vector2 r = Random.insideUnitCircle * spread;
                Vector3 pos = center + new Vector3(r.x, Random.Range(-2f, 3f), r.y);
                pos.y = Mathf.Max(pos.y, waterY + 2f);

                try
                {
                    if (SpawnGull(pos)) ok++;
                }
                catch (Exception e)
                {
                    Diag.Exception("SpawnGull", e);
                }
            }

            if (ok == n)
                Diag.Info("Spawned " + ok + "/" + n + " birds for wave " + (index + 1) + " at " +
                          center.ToString("F0") + " (" + (angle * Mathf.Rad2Deg).ToString("0") + " deg).");
            else
                Diag.Warn("Spawned only " + ok + "/" + n + " birds for wave " + (index + 1) + ".");

            // Birds that failed to spawn still count as done, or the wave could never finish.
            _removedTotal += n - ok;

            if (ok == 0)
            {
                Diag.Error("Wave " + (index + 1) + " spawned no birds - aborting encounter.");
                Say("ERROR: no birds could be spawned. See log.");
                EndEncounter("Aborted: spawn failure");
                return;
            }

            _arrivalScreamPending = cfg.ScreamOnArrival.Value;

            // First dives only once the flock has had time to arrive.
            _nextGroupAt = Time.time + cfg.ApproachDistance.Value / Mathf.Max(1f, cfg.ApproachSpeed.Value) * 0.6f;
        }

        private float PickWaveAngle(float minChange)
        {
            if (float.IsNaN(_lastWaveAngle)) return Random.Range(0f, Mathf.PI * 2f);

            // Opposite side, +/- whatever keeps us at least minChange away from last time.
            float slack = Mathf.Max(0f, Mathf.PI - minChange);
            return _lastWaveAngle + Mathf.PI + Random.Range(-slack, slack);
        }

        /// <summary>Direction relative to where the host is looking: "your left", "behind you", ...</summary>
        private static string RelativeDirection(Vector3 point)
        {
            Transform view = null;
            try { if (GameInfo.CurCamera != null) view = GameInfo.CurCamera.transform; } catch { }
            if (view == null && Player.LocalPlayer != null) view = Player.LocalPlayer.Transform;
            if (view == null) return "the horizon";

            Vector3 fwd = view.forward; fwd.y = 0f;
            Vector3 to = point - view.position; to.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f || to.sqrMagnitude < 1e-4f) return "the horizon";

            float a = Vector3.SignedAngle(fwd, to, Vector3.up); // + = right
            string[] names = { "AHEAD", "AHEAD-RIGHT", "your RIGHT", "BEHIND-RIGHT", "BEHIND you",
                               "BEHIND-LEFT", "your LEFT", "AHEAD-LEFT" };
            int idx = Mathf.RoundToInt(((a + 360f) % 360f) / 45f) % 8;
            return names[idx];
        }

        /// <summary>Scream once the flock is close enough to actually be heard.</summary>
        private void TryArrivalScream()
        {
            for (int i = 0; i < _birds.Count; i++)
            {
                GullAttacker a = _birds[i];
                if (a == null) continue;
                Vector3 d = a.transform.position - _anchor;
                d.y = 0f;
                if (d.magnitude > 70f) continue;

                _arrivalScreamPending = false;
                try
                {
                    AudioManager.PlayRandomClipAt("Seagull_V", 1, 11, a.transform.position, false, AudioDistance.Long, 1f);
                    AudioManager.PlayRandomClipAt("Seagull_V", 1, 11, a.transform.position + Vector3.up, false, AudioDistance.Long, 1f);
                }
                catch (Exception e) { Diag.Exception("Arrival scream", e); }
                return;
            }
        }

        // ------------------------------------------------------------------ lure

        private readonly List<Item> _lured = new List<Item>();
        private float _nextLureAt;

        /// <summary>
        /// Wild gulls can be scarce, and both opening quests need them (feed three to Old Salt, then
        /// five kills to call the flock). While either is running and no encounter is on, ordinary
        /// gulls - the game's own, flying its own way - keep turning up near the players.
        /// </summary>
        private void TickLure()
        {
            SwarmConfig cfg = SwarmModule.Cfg;
            if (_phase != Phase.Idle || !cfg.LureEnabled.Value || Time.time < _nextLureAt) return;
            _nextLureAt = Time.time + Mathf.Max(3f, cfg.LureIntervalSeconds.Value);

            // During a gull job the sky fills up; otherwise a few gulls are always about (from the island
            // that sells guns on), so the swarm can be called - and called again - at any time.
            int target;
            if (QuestWantsGulls()) target = Mathf.Max(0, cfg.LureMaxGulls.Value);
            else if (OmensDone() && CurrentIsland() >= Expanded.Content.PirateStory.IslandWithGuns) target = Mathf.Max(0, cfg.LureAmbient.Value);
            else return;
            if (_gullPrefab == null && !ResolvePrefabs()) return;
            if (_anchor == Vector3.zero) return;

            // Only gulls near the players count. Wild gulls wander off; those that do are cleared away
            // so they stop taking a place, and fresh ones fly in instead.
            float radius = Mathf.Max(20f, cfg.LureRadius.Value);
            int nearby = 0;
            var strays = new List<Item>();
            for (int i = _lured.Count - 1; i >= 0; i--)
            {
                Item g = _lured[i];
                if (g == null || g.IsDestroying || g.IsDeinitializing || (g.Creature != null && g.Creature.IsDead))
                { _lured.RemoveAt(i); continue; }
                Vector3 d = g.transform.position - _anchor; d.y = 0f;
                if (d.magnitude <= radius) { nearby++; continue; }
                if (d.magnitude > radius * 1.6f) { strays.Add(g); _lured.RemoveAt(i); }
            }
            foreach (Item g in strays)
            {
                try { g.DestroyItem((byte)DestroyReason.Immediate); } catch (Exception e) { Diag.Exception("Lure: clear stray", e); }
            }

            int n = Mathf.Min(4, target - nearby);
            if (n <= 0) return;

            int ok = SpawnLureGulls(n);
            if (ok > 0) Diag.Info("Lure: " + ok + " gull(s) flew in (" + (nearby + ok) + " nearby" +
                                  (strays.Count > 0 ? ", " + strays.Count + " stray(s) cleared" : "") + ").");
        }

        /// <summary>Ordinary gulls fly in together from one direction, out at sea, within gun range.</summary>
        private int SpawnLureGulls(int n)
        {
            if (_gullPrefab == null && !ResolvePrefabs()) return 0;
            if (_anchor == Vector3.zero) return 0;

            float water = 0f;
            try { water = WaterManager.WaterHeight; } catch { }

            float bearing = Random.Range(0f, 360f);
            int ok = 0;
            for (int k = 0; k < n; k++)
            {
                Vector3 dir = Quaternion.Euler(0f, bearing + Random.Range(-25f, 25f), 0f) * Vector3.forward;
                Vector3 pos = _anchor + dir * Random.Range(22f, 38f);
                pos.y = Mathf.Max(water, _anchor.y) + Random.Range(7f, 13f);
                try
                {
                    Item gull = Instantiate(_gullPrefab, pos, Quaternion.LookRotation(-dir), Server.Instance.DynamicObjectsHolder);
                    InstanceFinder.ServerManager.Spawn(gull.gameObject);
                    _lured.Add(gull);
                    ok++;
                }
                catch (Exception e)
                {
                    Diag.Exception("Lure gull", e);
                    break;
                }
            }
            return ok;
        }

        // ------------------------------------------------------------------ replacement after a kill

        private readonly List<float> _replaceAt = new List<float>();

        /// <summary>
        /// Calling the swarm takes 5 kills inside 3 minutes. Wild gulls alone can run dry, so every gull
        /// shot down (outside an encounter) is replaced by a fresh one shortly after. With the default
        /// 15 s the next target is always there well inside the 36 s per kill the window allows.
        /// </summary>
        private void TickReplacements()
        {
            if (_replaceAt.Count == 0) return;
            if (_phase != Phase.Idle) { _replaceAt.Clear(); return; }

            int due = 0;
            for (int i = _replaceAt.Count - 1; i >= 0; i--)
                if (Time.time >= _replaceAt[i]) { _replaceAt.RemoveAt(i); due++; }
            if (due == 0) return;

            int ok = SpawnLureGulls(due);
            if (ok > 0) Diag.Info("Lure: " + ok + " replacement gull(s) flew in after a kill.");
        }

        /// <summary>
        /// True once Bad Omens is done (or there is no story running at all). The swarm itself always
        /// punishes too many kills; this only decides when extra gulls start hanging around - after
        /// Old Salt has warned the players about the flock.
        /// </summary>
        private static bool OmensDone()
        {
            Expanded.Quests.QuestEngine e = Expanded.Quests.QuestModule.Instance?.Engine;
            return e == null || e.HasFlag(Expanded.Content.PirateStory.FlagOmens);
        }

        private static int CurrentIsland()
        {
            try { return OnlineIslandManager.CurIsland; } catch { return 0; }
        }

        private static bool QuestWantsGulls()
        {
            Expanded.Quests.QuestEngine e = Expanded.Quests.QuestModule.Instance?.Engine;
            if (e == null) return false;
            // Offered counts too: players often start shooting before they've talked to Old Salt.
            return Wants(e, Expanded.Content.PirateStory.QuestOmens) || Wants(e, Expanded.Content.PirateStory.QuestFlock);
        }

        private static bool Wants(Expanded.Quests.QuestEngine e, string questId)
        {
            Expanded.Quests.QuestStatus s = e.Progress(questId).Status;
            return s == Expanded.Quests.QuestStatus.Active || s == Expanded.Quests.QuestStatus.Available;
        }

        private bool SpawnGull(Vector3 pos)
        {
            Vector3 look = _anchor - pos;
            look.y = 0f;
            Quaternion rot = look.sqrMagnitude > 0.01f ? Quaternion.LookRotation(look.normalized) : Quaternion.identity;

            Item item = Instantiate(_gullPrefab, pos, rot, Server.Instance.DynamicObjectsHolder);
            Bird bird = item.GetComponent<Bird>();
            if (bird == null)
            {
                Diag.Error("Spawned seagull prefab has no Bird component.");
                Destroy(item.gameObject);
                return false;
            }

            // Attach before the network spawn so the SimulateBird prefix never sees an unbound bird.
            GullAttacker attacker = bird.gameObject.AddComponent<GullAttacker>();
            attacker.Bind(this, bird);

            InstanceFinder.ServerManager.Spawn(item.gameObject);
            _birds.Add(attacker);
            return true;
        }

        // ------------------------------------------------------------------ dive groups

        private void LaunchDiveGroup()
        {
            SwarmConfig cfg = SwarmModule.Cfg;

            float t = _waveSizes.Length <= 1 ? 1f : (float)_waveIndex / (_waveSizes.Length - 1);
            float interval = Mathf.Lerp(cfg.GroupIntervalStart.Value, cfg.GroupIntervalEnd.Value, t);
            int groupSize = Mathf.Max(1, Mathf.RoundToInt(
                Mathf.Lerp(cfg.GroupSizeStart.Value, cfg.GroupSizeEnd.Value, t)));

            _nextGroupAt = Time.time + interval;

            List<Player> alive = PlayerManager.AlivePlayers;
            if (alive.Count == 0)
            {
                Diag.Debug("Dive group skipped: no living players.");
                return;
            }

            List<GullAttacker> ready = new List<GullAttacker>();
            for (int i = 0; i < _birds.Count; i++)
                if (_birds[i] != null && _birds[i].ReadyToDive) ready.Add(_birds[i]);

            if (ready.Count == 0)
            {
                Diag.Debug("Dive group skipped: no bird ready (flying in, busy or cooling down).");
                return;
            }

            Shuffle(ready);

            // One shared commit moment for the group, then each further bird a little later: the flight
            // still pauses and tips over together, but hits land outside the game's 0.25s
            // post-hit invulnerability instead of being swallowed by it.
            float climbAllowance = (cfg.ClimbHeight.Value + 4f) / Mathf.Max(1f, cfg.ClimbSpeed.Value);
            float hover = Random.Range(cfg.HoverMinSeconds.Value, cfg.HoverMaxSeconds.Value);
            float commitAt = Time.time + climbAllowance + hover;

            Dictionary<Player, int> assigned = new Dictionary<Player, int>();
            HashSet<Player> covered = new HashSet<Player>();

            int launched = 0;
            for (int i = 0; i < ready.Count && launched < groupSize; i++)
            {
                GullAttacker a = ready[i];
                Player target = PickTarget(a.transform.position, alive, assigned, covered);
                if (target == null) break; // everyone is in cover

                if (!a.BeginDive(target, commitAt))
                {
                    covered.Add(target);
                    continue;
                }

                int c;
                assigned.TryGetValue(target, out c);
                assigned[target] = c + 1;
                launched++;
                commitAt += Random.Range(cfg.StaggerMin.Value, cfg.StaggerMax.Value);
            }

            if (launched > 0) _lastGroupAt = Time.time;

            Diag.Debug("Dive group: " + launched + "/" + groupSize + " launched from " + ready.Count +
                       " ready, " + assigned.Count + " target(s), " + covered.Count + " in cover; first commit in " +
                       (climbAllowance + hover).ToString("0.00") + "s.");
        }

        /// <summary>
        /// Spread a group across the party: prefer whoever has the fewest birds assigned so far,
        /// then whoever is nearest. Players found to be fully in cover are skipped.
        /// </summary>
        private static Player PickTarget(Vector3 from, List<Player> alive, Dictionary<Player, int> assigned,
                                         HashSet<Player> covered)
        {
            Player best = null;
            float bestScore = float.MaxValue;

            for (int i = 0; i < alive.Count; i++)
            {
                Player p = alive[i];
                if (p == null || p.Dying.IsDead || covered.Contains(p)) continue;

                int load;
                assigned.TryGetValue(p, out load);
                float score = load * 100000f + (p.Transform.position - from).sqrMagnitude;
                if (score < bestScore) { bestScore = score; best = p; }
            }
            return best;
        }

        private static void Shuffle<T>(List<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                T tmp = list[i];
                list[i] = list[j];
                list[j] = tmp;
            }
        }

        // ------------------------------------------------------------------ prefabs

        private bool ResolvePrefabs()
        {
            if (_gullPrefab == null) _gullPrefab = FindPrefabWith<Bird>("seagull");
            Diag.Info("Prefabs: seagull " + (_gullPrefab != null ? "'" + _gullPrefab.name + "'" : "MISSING") + ".");
            return _gullPrefab != null;
        }

        /// <summary>
        /// Name lookup first (GameInfo keys every Resources/Items prefab by lowercased name), then
        /// a component scan as a fallback so a rename in a game patch cannot break us.
        /// </summary>
        private static Item FindPrefabWith<T>(string name) where T : Component
        {
            Item byName = GameInfo.GetSpawnable(name);
            if (byName != null && byName.GetComponent<T>() != null) return byName;

            Diag.Warn("Prefab '" + name + "' not found by name; scanning Resources/Items for a " + typeof(T).Name + ".");
            Item[] all = Resources.LoadAll<Item>("Items");
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].GetComponent<T>() != null) return all[i];

            return null;
        }
    }
}
