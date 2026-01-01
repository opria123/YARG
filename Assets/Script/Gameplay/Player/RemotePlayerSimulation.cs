using UnityEngine;
using YARG.Core.Engine;
using YARG.Core.Engine.Vocals;
using YARG.Networking.Abstraction;

namespace YARG.Gameplay.Player
{
    /// <summary>
    /// Simulates a remote player's track visualization based on network state.
    /// 
    /// Clone Hero-style approach:
    /// - Notes are resolved purely based on LOCAL time (when they pass the strikeline)
    /// - Hit/miss decision uses the RATIO from network state (not exact sync)
    /// - Prioritizes smooth, lag-free visuals over exact accuracy
    /// - Works well at any ping because it doesn't depend on network timing
    /// - Engine stats (score, combo, multiplier, SP) are synced from network for HUD display
    /// </summary>
    public class RemotePlayerSimulation : MonoBehaviour, IRemotePlayerSimulation
    {
        // Timing: resolve notes as they pass the strikeline (time=0 in local space)
        // Use a small negative buffer so notes resolve just as they reach the line
        private const double NOTE_RESOLVE_TIME_OFFSET = 0.0; // Resolve at exact strikeline
        
        // Throttle non-critical updates (sustain, whammy, star power visuals)
        private const float NON_CRITICAL_UPDATE_INTERVAL = 0.05f;
        
        private BasePlayer _player;
        private TrackPlayer _trackPlayer;
        private FiveFretGuitarPlayer _guitarPlayer;
        private VocalsPlayer _vocalsPlayer;
        private NetworkPlayerData _networkPlayerData;
        private bool _isActive;
        
        // Note resolution tracking
        private int _noteCursor;
        private int _localNotesResolved;
        private int _localMissesAssigned;
        
        // Cached network state (updated from snapshots)
        private int _networkNotesHit;
        private int _networkNotesMissed;
        private int _lastNetworkNotesMissed;  // For detecting new misses
        
        // Non-critical state tracking
        private int _lastSustainsHeld;
        private float _lastWhammyValue;
        private bool _lastStarPowerActive;
        private float _lastNonCriticalUpdateTime;
        
        // Solo state tracking
        private int _lastSoloSequence = -1;

        /// <inheritdoc/>
        public NetworkPlayerData NetworkPlayerData => _networkPlayerData;

        /// <inheritdoc/>
        public bool IsActive => _isActive;

        /// <inheritdoc/>
        public void Initialize(BasePlayer player, NetworkPlayerData networkPlayerData)
        {
            _player = player;
            _networkPlayerData = networkPlayerData;
            _isActive = true;
            
            // Cache typed references
            _trackPlayer = player as TrackPlayer;
            _guitarPlayer = player as FiveFretGuitarPlayer;
            _vocalsPlayer = player as VocalsPlayer;
            
            // Initialize state
            _noteCursor = 0;
            _localNotesResolved = 0;
            _localMissesAssigned = 0;
            _networkNotesHit = 0;
            _networkNotesMissed = 0;
            _lastNetworkNotesMissed = 0;
            _lastSustainsHeld = 0;
            _lastWhammyValue = 0f;
            _lastStarPowerActive = false;
            _lastNonCriticalUpdateTime = 0f;

            Debug.Log($"[RemotePlayerSimulation] Initialized for {_networkPlayerData?.PlayerName ?? "Unknown"}" +
                $" (initial fail state: {_networkPlayerData?.HasFailed ?? false}, happiness: {_networkPlayerData?.Happiness ?? 1f:F2})");
            
            // IMPORTANT: Apply initial fail state immediately
            // If the player was already failed before spectating started, we need to sync that state
            // so the track appears lowered from the start, not just when they fail/revive during spectating
            if (_networkPlayerData != null)
            {
                _trackPlayer?.SyncRemoteHappiness(_networkPlayerData.Happiness, _networkPlayerData.HasFailed);
            }
        }

        private void OnDestroy()
        {
            _isActive = false;
        }

        private void Update()
        {
            // ApplyRemoteState is called by BasePlayer each frame
        }

        /// <inheritdoc/>
        public void ApplyNetworkState(int score, int combo, bool isStarPowerActive, float starPowerAmount)
        {
            // Not used - state comes through NetworkPlayerData
        }

        /// <inheritdoc/>
        public void ApplyRemoteState(double localTime)
        {
            if (_player == null || _networkPlayerData == null || !_isActive)
                return;

            // Get latest network state from snapshots
            _networkNotesHit = _networkPlayerData.NotesHit;
            _networkNotesMissed = _networkPlayerData.NotesMissed;
            
            // Sync engine stats from network (score, combo, multiplier, star power)
            // This ensures HUD displays are accurate even if note resolution timing differs
            SyncEngineStatsFromNetwork();
            
            // Sync happiness and fail state from authoritative source
            // This is critical for fail/revival state so don't throttle it
            _trackPlayer?.SyncRemoteHappiness(_networkPlayerData.Happiness, _networkPlayerData.HasFailed);
            
            // Resolve notes based on local time only
            if (_trackPlayer != null)
            {
                ResolveNotes(localTime);
            }
            
            // Check if remote player missed a note while we had a sustain held
            // If so, we need to drop the sustain
            CheckSustainDrop();
            
            // Track for next frame
            _lastNetworkNotesMissed = _networkNotesMissed;
            
            // Throttled non-critical updates
            float currentTime = Time.time;
            if ((currentTime - _lastNonCriticalUpdateTime) >= NON_CRITICAL_UPDATE_INTERVAL)
            {
                _lastNonCriticalUpdateTime = currentTime;
                UpdateNonCriticalState();
            }
        }

        /// <summary>
        /// Syncs the engine's BaseStats with network state so HUD displays (combo, multiplier, score, SP, stars) are accurate.
        /// </summary>
        private void SyncEngineStatsFromNetwork()
        {
            if (_player?.BaseEngine?.BaseStats == null || _networkPlayerData == null)
                return;
            
            var stats = _player.BaseEngine.BaseStats;
            var engine = _player.BaseEngine;
            
            // Sync score - use network score directly
            stats.CommittedScore = _networkPlayerData.CurrentScore;
            
            // Sync combo from network
            stats.Combo = _networkPlayerData.CurrentCombo;
            
            // Track max combo
            if (stats.Combo > stats.MaxCombo)
            {
                stats.MaxCombo = stats.Combo;
            }
            else if (_networkPlayerData.MaxCombo > stats.MaxCombo)
            {
                stats.MaxCombo = _networkPlayerData.MaxCombo;
            }
            
            // Sync multiplier - formula differs between instruments
            int maxMultiplier = _player.BaseParameters?.MaxMultiplier ?? 4;
            
            if (_vocalsPlayer != null)
            {
                // Vocals: multiplier = Combo + 1, capped at MaxMultiplier
                // (from VocalsEngine.UpdateMultiplier: Math.Min(Combo + 1, 4))
                stats.ScoreMultiplier = Mathf.Min(stats.Combo + 1, maxMultiplier);
            }
            else
            {
                // Instruments: multiplier = (Combo / 10) + 1, capped at MaxMultiplier
                // (from BaseEngine.UpdateMultiplier)
                stats.ScoreMultiplier = Mathf.Min((stats.Combo / 10) + 1, maxMultiplier);
            }
            
            // Sync star power state from network
            stats.IsStarPowerActive = _networkPlayerData.IsStarPowerActive;
            
            // Sync star power amount (convert 0-1 float to tick amount)
            // The network sends StarPowerAmount as 0-1 (from TickAmount / TicksPerFullSpBar)
            // We need to convert back using the engine's actual TicksPerFullSpBar value
            float spAmount = _networkPlayerData.StarPowerAmount;
            uint ticksPerFullBar = engine.TicksPerFullSpBar;
            stats.StarPowerTickAmount = (uint)(spAmount * ticksPerFullBar);
            
            // Double multiplier during star power (same as BaseEngine.UpdateMultiplier())
            if (stats.IsStarPowerActive)
            {
                stats.ScoreMultiplier *= 2;
            }
            
            // Sync notes hit/missed
            stats.NotesHit = _networkNotesHit;
            
            // Sync stars progress from network
            stats.Stars = _networkPlayerData.Stars;
            
            // Sync vocals-specific phrase progress for the HUD meter
            if (_vocalsPlayer != null)
            {
                _vocalsPlayer.ApplyRemotePhraseProgress(
                    _networkPlayerData.VocalsPhraseTicksHit,
                    _networkPlayerData.VocalsPhraseTicksTotal);
                
                // Sync vocals TicksHit/TicksMissed for accurate percentage on score screen
                // Without this, VocalsStats.Percent returns 100% when TotalTicks == 0
                if (stats is VocalsStats vocalStats)
                {
                    vocalStats.TicksHit = (uint)Mathf.Max(0, _networkPlayerData.VocalsTicksHit);
                    vocalStats.TicksMissed = (uint)Mathf.Max(0, _networkPlayerData.VocalsTicksMissed);
                }
            }
        }

        /// <summary>
        /// Checks if the remote player's sustain state changed.
        /// If they dropped a sustain, we should drop it on our end too.
        /// Also syncs sustain visual state to catch any mismatches from prediction errors.
        /// </summary>
        private void CheckSustainDrop()
        {
            if (_guitarPlayer == null || _networkPlayerData == null)
                return;
                
            int networkSustainsHeld = _networkPlayerData.SustainsHeld;
            
            // Check if sustain state changed (sustain dropped)
            if (networkSustainsHeld != _lastSustainsHeld)
            {
                // Sync sustain state to match network
                _guitarPlayer.ApplyRemoteSustainState(_lastSustainsHeld, networkSustainsHeld);
                _lastSustainsHeld = networkSustainsHeld;
            }
            
            // Also sync sustain visuals to catch any mismatches
            // This handles the case where local simulation resolved a note as "hit"
            // but the remote player actually missed or dropped it
            _guitarPlayer.SyncSustainVisualsWithNetwork(networkSustainsHeld);
        }

        /// <summary>
        /// Resolves notes as they pass the strikeline (local time).
        /// Hit/miss is decided by comparing our resolved miss count to network's.
        /// </summary>
        private void ResolveNotes(double localTime)
        {
            if (_trackPlayer == null)
                return;
            
            // Process all notes that have reached or passed the strikeline
            while (true)
            {
                // Determine hit/miss based on network ratio
                bool shouldHit = ShouldNextNoteBeHit();
                
                // Try to resolve with TrackPlayer
                // Use a very small lookahead - we want notes resolved as they hit the line
                bool success = _trackPlayer.ResolveRemoteNote(
                    ref _noteCursor,
                    shouldHit,
                    localTime,
                    localTime,  // Use local time for both - no remote timing dependency
                    NOTE_RESOLVE_TIME_OFFSET,
                    NOTE_RESOLVE_TIME_OFFSET,
                    out int weight);
                
                if (!success)
                    break; // No more notes ready
                
                // Track resolution
                _localNotesResolved += weight;
                if (!shouldHit)
                {
                    _localMissesAssigned += weight;
                }
            }
        }

        /// <summary>
        /// Decides if the next note should be hit or missed.
        /// Uses the network miss count as a target - we assign misses until we catch up.
        /// </summary>
        private bool ShouldNextNoteBeHit()
        {
            // If our local miss count is less than network's, this note should miss
            if (_localMissesAssigned < _networkNotesMissed)
            {
                return false;
            }
            
            // Otherwise, hit
            return true;
        }

        /// <summary>
        /// Updates non-critical visual state (whammy, star power, solo).
        /// Sustain state is handled separately in CheckSustainDrop() every frame.
        /// </summary>
        private void UpdateNonCriticalState()
        {
            if (_networkPlayerData == null)
                return;
            
            // Whammy (only on significant change)
            float currentWhammy = _networkPlayerData.WhammyValue;
            if (_guitarPlayer != null && Mathf.Abs(currentWhammy - _lastWhammyValue) > 0.1f)
            {
                _guitarPlayer.ApplyRemoteWhammyValue(currentWhammy);
                _lastWhammyValue = currentWhammy;
            }
            
            // Star power
            bool currentStarPower = _networkPlayerData.IsStarPowerActive;
            if (currentStarPower != _lastStarPowerActive)
            {
                _trackPlayer?.ApplyRemoteStarPowerState(currentStarPower);
                _lastStarPowerActive = currentStarPower;
            }
            
            // Countdown
            _trackPlayer?.UpdateRemoteCountdown();
            
            // Solo display - update every frame for smooth progress display
            UpdateSoloDisplay();
        }
        
        /// <summary>
        /// Updates the solo display based on network state.
        /// </summary>
        private void UpdateSoloDisplay()
        {
            if (_trackPlayer?.TrackView == null || _networkPlayerData == null)
                return;
            
            _trackPlayer.TrackView.UpdateRemoteSolo(
                _networkPlayerData.SoloActive,
                _networkPlayerData.SoloSequence,
                _networkPlayerData.SoloNoteCount,
                _networkPlayerData.SoloNotesHit,
                _networkPlayerData.SoloLastBonus
            );
        }
    }
}
