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
    /// Counts provocation kills, runs the five waves, and spawns an Albatross as a real, killable
    /// flock leader. The Albatross owns the vanilla boss bar and timer:
    ///   kill it            -> victory, the game drops its trophy and meat, the gulls flee
    ///   clear all waves    -> only the Albatross remains; kill it to win
    ///   timer runs out     -> it flies off, the gulls scatter, no loot
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

        private BossManager _bossManager;
        private Creature _leader;
        private bool _leaderInPlay;
        private int _leaderLastHp;
        private int _leaderMaxHp;
        private float _summonDeadline;

        private int[] _waveSizes;
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
        private Item _leaderPrefab;

        private float _attachedAt;
        private bool _autoStartFired;

        // --- diagnostics ---
        private float _encounterStartedAt;
        private float _summonStartedAt;
        private float _waveStartedAt;
        private float _nextStatusAt;
        private float _lastGroupAt;
        private int _waveDives, _waveHits, _waveCrashes, _totalDives, _totalHits, _totalCrashes;
        private int _waveKilled, _waveVanished;
        private bool _warnedFar, _warnedStall;

        private bool InEncounter { get { return _phase == Phase.Summoning || _phase == Phase.Running; } }
        internal bool InEncounterPublic { get { return InEncounter; } }

        /// <summary>True while our Albatross owns the vanilla boss bar.</summary>
        internal bool OwnsBossBar
        {
            get { return InEncounter && _leader != null && BossManager.Boss == _leader; }
        }

        private void Awake()
        {
            Active = this;
            _bossManager = FindAnyObjectByType<BossManager>();
            _anchor = transform.position;
            _attachedAt = Time.time;

            if (_bossManager == null)
                Diag.Warn("BossManager not found in this scene - the boss bar will not work here.");
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
            _provokeKills.Add(Time.time);

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

            switch (_phase)
            {
                case Phase.Idle:
                    if (_provokeKills.Count >= SwarmModule.Cfg.KillsToProvoke.Value)
                    {
                        Diag.Info("Kill threshold reached - provoking the flock.");
                        StartEncounter();
                    }
                    break;

                case Phase.Summoning:
                    TickSummoning();
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
                _anchor = Vector3.Lerp(_anchor, BoatManager.Boat.transform.position, Time.deltaTime * 1.5f);
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

            string boss;
            if (BossManager.Boss == null) boss = "none";
            else if (_leader != null && BossManager.Boss == _leader) boss = "ours";
            else boss = "OTHER(" + BossManager.Boss.name + ")";

            string leaderHp = _leaderInPlay ? _leaderLastHp + "/" + _leaderMaxHp : "n/a";

            string timeLeft = "n/a";
            if (BossManager.Boss != null && InstanceFinder.TimeManager != null)
            {
                uint tick = InstanceFinder.TimeManager.Tick;
                uint leaves = BossManager.BossLeavesTick;
                float rate = (int)InstanceFinder.TimeManager.TickRate;
                timeLeft = (leaves > tick ? (leaves - tick) / rate : 0f).ToString("0") + "s";
            }

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
                      " | albatross " + leaderHp + " | boss " + boss + " | time left " + timeLeft +
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

            if (!cfg.HotkeysEnabled.Value) return;

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
                case Phase.Summoning:
                    return "summoning the Albatross";
                case Phase.Running:
                    return "wave " + (_waveIndex + 1) + "/" + (_waveSizes != null ? _waveSizes.Length : 0) +
                           ", " + _birds.Count + " birds, albatross " +
                           (_leaderInPlay ? _leaderLastHp + "/" + _leaderMaxHp : "n/a");
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

            // Send the Albatross away through the same path the vanilla timer uses: no death, no loot.
            if (_leader != null && _leader._hp.Value > 0)
            {
                try { _leader.DestroyItem((byte)DestroyReason.Default); }
                catch (Exception e) { Diag.Exception("ForceStop leader", e); }
            }
            _leader = null;
            _leaderInPlay = false;

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
            _leader = null;
            _leaderInPlay = false;
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

            Say("The flock has had enough. " + _waveSizes.Length + " waves, " + _totalPlanned + " birds.");
            Diag.Info("Wave plan: " + string.Join(" / ", _waveSizes) + " = " + _totalPlanned +
                      ". Players: " + PlayerManager.AlivePlayers.Count + " alive of " + PlayerManager.Players.Count +
                      ". Anchor " + _anchor.ToString("F1") + ", water " + GullAttacker.WaterY().ToString("0.0") + ".");

            if (!cfg.SpawnLeader.Value)
            {
                Diag.Info("Leader disabled in config - no boss bar.");
            }
            else if (_leaderPrefab == null)
            {
                Diag.Warn("Albatross prefab not found - running without leader or boss bar.");
            }
            else if (BossManager.Boss != null)
            {
                Diag.Warn("A vanilla boss is already active (" + BossManager.Boss.name +
                          ") - leader skipped this time.");
            }
            else
            {
                Vector3 pos = _anchor + Vector3.up * cfg.LeaderSpawnHeight.Value;
                Item leader = ItemManager.Instance.SpawnNewItem(_leaderPrefab, pos, Quaternion.identity);
                _leader = leader != null ? leader.GetComponent<Creature>() : null;

                if (_leader == null)
                {
                    Diag.Error("Leader spawn returned nothing - running without it.");
                }
                else
                {
                    Diag.Info("Leader '" + _leader.name + "' spawned at " + pos.ToString("F1") +
                              ", waiting for it to take the boss bar...");
                    _summonStartedAt = Time.time;
                    _summonDeadline = Time.time + 5f;
                    _phase = Phase.Summoning;
                    return;
                }
            }

            // No leader (disabled, missing, or a vanilla boss already owns the bar): run bare.
            _leader = null;
            BeginWaves();
        }

        private void TickSummoning()
        {
            // Creature.OnStartClient is what assigns BossManager.Boss, so wait for it to land
            // before touching the bar.
            if (_leader != null && BossManager.Boss == _leader)
            {
                Diag.Info("Leader took the boss bar after " + (Time.time - _summonStartedAt).ToString("0.00") + "s.");
                ConfigureLeader();
                BeginWaves();
                return;
            }

            if (Time.time >= _summonDeadline)
            {
                Diag.Warn("Leader never took over the boss bar within 5s (BossManager.Boss = " +
                          (BossManager.Boss == null ? "null" : BossManager.Boss.name) +
                          ", leader " + (_leader == null ? "destroyed" : "alive") + "); running without it.");
                Say("Albatross unavailable - running the waves without it.");
                _leader = null;
                BeginWaves();
            }
        }

        private void ConfigureLeader()
        {
            SwarmConfig cfg = SwarmModule.Cfg;

            if (_bossManager == null) _bossManager = FindAnyObjectByType<BossManager>();
            if (_bossManager == null || _leader == null)
            {
                Diag.Error("Cannot configure leader: BossManager " + (_bossManager == null ? "missing" : "ok") +
                           ", leader " + (_leader == null ? "missing" : "ok") + ".");
                return;
            }

            int baseHp = Mathf.Max(1, cfg.LeaderHp.Value);
            _leaderMaxHp = cfg.LeaderHpScalesWithPlayers.Value
                ? BossManager.GetBossMaxHp(baseHp, _leader.BossHpMultiplier)
                : baseHp;

            // Max first, then HP, so each client's bar computes hp/max against the new maximum.
            _bossManager._bossMaxHp.Value = _leaderMaxHp;
            _leader._hp.Value = _leaderMaxHp;
            _leaderLastHp = _leaderMaxHp;
            _leaderInPlay = true;

            Diag.Info("Albatross ready: " + _leaderMaxHp + " HP (base " + baseHp + ", " +
                      PlayerManager.Players.Count + " player(s)), timer " + _leader.BossTimeInSeconds +
                      "s, immortal " + BossManager.IsImmortal + ".");
            Say("The Albatross leads them - " + _leaderMaxHp + " HP. Kill it to break the flock!");
        }

        private void BeginWaves()
        {
            _phase = Phase.Running;
            StartWave(0);
        }

        private void TickRunning()
        {
            SwarmConfig cfg = SwarmModule.Cfg;

            if (_leaderInPlay && HandleLeaderState(cfg)) return;

            if (_wavesDone) return; // only the Albatross is left; nothing else to run

            ReapDeadBirds();

            if (_arrivalScreamPending) TryArrivalScream();

            if (_birds.Count == 0)
            {
                AdvanceWave();
                return;
            }

            if (Time.time >= _nextGroupAt) LaunchDiveGroup();
        }

        /// <summary>Returns true if the encounter ended this frame.</summary>
        private bool HandleLeaderState(SwarmConfig cfg)
        {
            // Unity's null check turns true once the object is destroyed, so keep the last HP we saw
            // to tell "killed" (HP 0) from "left" (timer, stop, party wiped).
            if (_leader != null) _leaderLastHp = _leader._hp.Value;
            bool gone = _leader == null || BossManager.Boss != _leader;

            if (_leaderLastHp <= 0)
            {
                _leaderInPlay = false;
                _leader = null;

                Diag.Info("Albatross slain after " + (Time.time - _encounterStartedAt).ToString("0") +
                          "s, in wave " + (_waveIndex + 1) + (_wavesDone ? " (all waves cleared)" : "") + ".");

                if (cfg.LeaderDeathEndsSwarm.Value || _wavesDone)
                {
                    Say("The Albatross is dead - the flock breaks and flees!");
                    RaiseStoryVictory();
                    EndEncounter("Victory: Albatross slain");
                    return true;
                }

                Say("The Albatross is dead! Finish off the remaining gulls.");
                return false;
            }

            if (gone)
            {
                _leaderInPlay = false;
                _leader = null;

                if (PlayerManager.AlivePlayers.Count == 0)
                    Say("Everyone died - the flock scatters.");
                else
                    Say("Time's up - the Albatross flies off and the flock scatters.");

                EndEncounter("Defeat: Albatross left (" + (PlayerManager.AlivePlayers.Count == 0 ? "party wiped" : "timer") + ")");
                return true;
            }

            return false;
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
                StartWave(_waveIndex);
                return;
            }

            _waveIndex = _waveSizes.Length - 1;

            if (_leaderInPlay)
            {
                _wavesDone = true;
                Say("All waves cleared - only the Albatross remains!");
                return;
            }

            Say("Swarm defeated!");
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
        }

        private void EnterCooldown()
        {
            _birds.Clear();
            _provokeKills.Clear();
            _wavesDone = false;
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

            Say("Wave " + (index + 1) + "/" + _waveSizes.Length + ": " + _waveSizes[index] +
                " birds incoming from " + RelativeDirection(center) + "!");

            int n = _waveSizes[index];
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
            if (_leaderPrefab == null) _leaderPrefab = FindPrefabWith<Albatross>("albatross");

            Diag.Info("Prefabs: seagull " + (_gullPrefab != null ? "'" + _gullPrefab.name + "'" : "MISSING") +
                      ", albatross " + (_leaderPrefab != null ? "'" + _leaderPrefab.name + "'" : "MISSING") + ".");
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
