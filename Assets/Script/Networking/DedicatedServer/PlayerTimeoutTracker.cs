using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using YARG.Networking.Abstraction;
using YARG.Networking.Settings;

namespace YARG.Networking.DedicatedServer
{
    /// <summary>
    /// Tracks player activity and ready-up timeouts for dedicated servers.
    /// Players who exceed timeout thresholds are kicked automatically.
    /// </summary>
    public sealed class PlayerTimeoutTracker : IDisposable
    {
        /// <summary>
        /// Activity state for a tracked player.
        /// </summary>
        private sealed class PlayerActivityState
        {
            public Guid PlayerId { get; set; }
            public string PlayerName { get; set; }
            public DateTime LastActivityTime { get; set; }
            public DateTime? ReadyUpDeadline { get; set; }
            public bool IsReady { get; set; }
            public bool HasBeenWarned { get; set; }
        }
        
        private readonly DedicatedServerConfig _config;
        private readonly Dictionary<Guid, PlayerActivityState> _playerStates = new();
        private readonly object _stateLock = new();
        
        private bool _readyUpPhaseActive;
        private DateTime _readyUpPhaseStartTime;
        private bool _isDisposed;
        
        // Warning threshold (warn at 75% of timeout)
        private const float WARNING_THRESHOLD = 0.75f;
        
        /// <summary>
        /// Event fired when a player should be kicked for timeout.
        /// Parameters: playerId, playerName, reason
        /// </summary>
        public event Action<Guid, string, string> OnPlayerTimeout;
        
        /// <summary>
        /// Event fired when a player is about to timeout (warning).
        /// Parameters: playerId, playerName, secondsRemaining
        /// </summary>
        public event Action<Guid, string, int> OnPlayerTimeoutWarning;
        
        /// <summary>
        /// Gets whether the ready-up phase is currently active.
        /// </summary>
        public bool IsReadyUpPhaseActive => _readyUpPhaseActive;
        
        /// <summary>
        /// Gets the idle timeout in minutes from config (0 = disabled).
        /// </summary>
        public int IdleTimeoutMinutes => _config.Timeouts.IdleMinutes;
        
        /// <summary>
        /// Gets the ready-up timeout in minutes from config (0 = disabled).
        /// </summary>
        public int ReadyUpTimeoutMinutes => _config.Timeouts.ReadyUpMinutes;
        
        public PlayerTimeoutTracker(DedicatedServerConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }
        
        /// <summary>
        /// Registers a player for timeout tracking.
        /// </summary>
        public void RegisterPlayer(Guid playerId, string playerName)
        {
            lock (_stateLock)
            {
                if (_playerStates.ContainsKey(playerId))
                {
                    // Update existing entry
                    _playerStates[playerId].PlayerName = playerName;
                    _playerStates[playerId].LastActivityTime = DateTime.UtcNow;
                    return;
                }
                
                _playerStates[playerId] = new PlayerActivityState
                {
                    PlayerId = playerId,
                    PlayerName = playerName,
                    LastActivityTime = DateTime.UtcNow,
                    ReadyUpDeadline = _readyUpPhaseActive ? DateTime.UtcNow.AddMinutes(ReadyUpTimeoutMinutes) : null,
                    IsReady = false,
                    HasBeenWarned = false
                };
                
                Debug.Log($"[PlayerTimeoutTracker] Registered player: {playerName} ({playerId})");
            }
        }
        
        /// <summary>
        /// Unregisters a player from timeout tracking.
        /// </summary>
        public void UnregisterPlayer(Guid playerId)
        {
            lock (_stateLock)
            {
                if (_playerStates.Remove(playerId))
                {
                    Debug.Log($"[PlayerTimeoutTracker] Unregistered player: {playerId}");
                }
            }
        }
        
        /// <summary>
        /// Records activity for a player, resetting their idle timeout.
        /// Call this when the player does something meaningful (input, chat, menu interaction).
        /// </summary>
        public void RecordActivity(Guid playerId)
        {
            lock (_stateLock)
            {
                if (_playerStates.TryGetValue(playerId, out var state))
                {
                    state.LastActivityTime = DateTime.UtcNow;
                    state.HasBeenWarned = false;
                }
            }
        }
        
        /// <summary>
        /// Records activity for a player by name (for backwards compatibility).
        /// </summary>
        public void RecordActivityByName(string playerName)
        {
            lock (_stateLock)
            {
                var state = _playerStates.Values.FirstOrDefault(s => 
                    string.Equals(s.PlayerName, playerName, StringComparison.OrdinalIgnoreCase));
                if (state != null)
                {
                    state.LastActivityTime = DateTime.UtcNow;
                    state.HasBeenWarned = false;
                }
            }
        }
        
        /// <summary>
        /// Updates a player's ready state.
        /// Ready players are exempt from ready-up timeout.
        /// </summary>
        public void SetPlayerReady(Guid playerId, bool isReady)
        {
            lock (_stateLock)
            {
                if (_playerStates.TryGetValue(playerId, out var state))
                {
                    state.IsReady = isReady;
                    state.LastActivityTime = DateTime.UtcNow;
                    
                    if (isReady)
                    {
                        // Clear deadline and warning when player readies up
                        state.ReadyUpDeadline = null;
                        state.HasBeenWarned = false;
                    }
                    else if (_readyUpPhaseActive && ReadyUpTimeoutMinutes > 0)
                    {
                        // Player unreadied during ready-up phase - set new deadline
                        state.ReadyUpDeadline = DateTime.UtcNow.AddMinutes(ReadyUpTimeoutMinutes);
                    }
                }
            }
        }
        
        /// <summary>
        /// Starts the ready-up phase. All non-ready players get a deadline to ready up.
        /// Called when host starts song selection or when waiting for players to start.
        /// </summary>
        public void StartReadyUpPhase()
        {
            if (ReadyUpTimeoutMinutes <= 0)
            {
                Debug.Log("[PlayerTimeoutTracker] Ready-up timeout disabled, not starting phase");
                return;
            }
            
            lock (_stateLock)
            {
                _readyUpPhaseActive = true;
                _readyUpPhaseStartTime = DateTime.UtcNow;
                var deadline = DateTime.UtcNow.AddMinutes(ReadyUpTimeoutMinutes);
                
                foreach (var state in _playerStates.Values)
                {
                    if (!state.IsReady)
                    {
                        state.ReadyUpDeadline = deadline;
                        state.HasBeenWarned = false;
                    }
                }
                
                Debug.Log($"[PlayerTimeoutTracker] Started ready-up phase, deadline in {ReadyUpTimeoutMinutes} minutes");
            }
        }
        
        /// <summary>
        /// Ends the ready-up phase (e.g., when game starts or returns to lobby).
        /// </summary>
        public void EndReadyUpPhase()
        {
            lock (_stateLock)
            {
                _readyUpPhaseActive = false;
                
                foreach (var state in _playerStates.Values)
                {
                    state.ReadyUpDeadline = null;
                    state.HasBeenWarned = false;
                }
                
                Debug.Log("[PlayerTimeoutTracker] Ended ready-up phase");
            }
        }
        
        /// <summary>
        /// Checks all players for timeouts. Call this periodically (e.g., every second).
        /// Returns list of players who have timed out.
        /// </summary>
        public List<(Guid PlayerId, string PlayerName, string Reason)> CheckTimeouts()
        {
            var timedOut = new List<(Guid, string, string)>();
            var warnings = new List<(Guid, string, int)>();
            
            lock (_stateLock)
            {
                var now = DateTime.UtcNow;
                
                foreach (var state in _playerStates.Values.ToList())
                {
                    // Check idle timeout
                    if (IdleTimeoutMinutes > 0)
                    {
                        var idleTime = now - state.LastActivityTime;
                        var idleTimeoutSpan = TimeSpan.FromMinutes(IdleTimeoutMinutes);
                        
                        if (idleTime >= idleTimeoutSpan)
                        {
                            timedOut.Add((state.PlayerId, state.PlayerName, $"Idle for {(int)idleTime.TotalMinutes} minutes"));
                            continue;
                        }
                        
                        // Check for warning
                        var warningThreshold = TimeSpan.FromMinutes(IdleTimeoutMinutes * WARNING_THRESHOLD);
                        if (!state.HasBeenWarned && idleTime >= warningThreshold)
                        {
                            var remaining = (int)(idleTimeoutSpan - idleTime).TotalSeconds;
                            warnings.Add((state.PlayerId, state.PlayerName, remaining));
                            state.HasBeenWarned = true;
                        }
                    }
                    
                    // Check ready-up timeout
                    if (_readyUpPhaseActive && state.ReadyUpDeadline.HasValue && !state.IsReady)
                    {
                        if (now >= state.ReadyUpDeadline.Value)
                        {
                            timedOut.Add((state.PlayerId, state.PlayerName, "Failed to ready up in time"));
                            continue;
                        }
                        
                        // Check for ready-up warning
                        var timeRemaining = state.ReadyUpDeadline.Value - now;
                        var totalTimeout = TimeSpan.FromMinutes(ReadyUpTimeoutMinutes);
                        var warningThreshold = totalTimeout * (1 - WARNING_THRESHOLD);
                        
                        if (!state.HasBeenWarned && timeRemaining <= warningThreshold)
                        {
                            warnings.Add((state.PlayerId, state.PlayerName, (int)timeRemaining.TotalSeconds));
                            state.HasBeenWarned = true;
                        }
                    }
                }
            }
            
            // Fire warning events
            foreach (var (playerId, playerName, secondsRemaining) in warnings)
            {
                OnPlayerTimeoutWarning?.Invoke(playerId, playerName, secondsRemaining);
            }
            
            // Fire timeout events
            foreach (var (playerId, playerName, reason) in timedOut)
            {
                OnPlayerTimeout?.Invoke(playerId, playerName, reason);
            }
            
            return timedOut;
        }
        
        /// <summary>
        /// Gets the time remaining before a player times out (idle or ready-up).
        /// Returns null if player has no active timeout.
        /// </summary>
        public TimeSpan? GetTimeRemaining(Guid playerId)
        {
            lock (_stateLock)
            {
                if (!_playerStates.TryGetValue(playerId, out var state))
                    return null;
                
                var now = DateTime.UtcNow;
                TimeSpan? idleRemaining = null;
                TimeSpan? readyUpRemaining = null;
                
                if (IdleTimeoutMinutes > 0)
                {
                    var idleDeadline = state.LastActivityTime.AddMinutes(IdleTimeoutMinutes);
                    idleRemaining = idleDeadline - now;
                    if (idleRemaining < TimeSpan.Zero) idleRemaining = TimeSpan.Zero;
                }
                
                if (_readyUpPhaseActive && state.ReadyUpDeadline.HasValue && !state.IsReady)
                {
                    readyUpRemaining = state.ReadyUpDeadline.Value - now;
                    if (readyUpRemaining < TimeSpan.Zero) readyUpRemaining = TimeSpan.Zero;
                }
                
                // Return the shorter of the two timeouts
                if (idleRemaining.HasValue && readyUpRemaining.HasValue)
                    return idleRemaining.Value < readyUpRemaining.Value ? idleRemaining : readyUpRemaining;
                
                return idleRemaining ?? readyUpRemaining;
            }
        }
        
        /// <summary>
        /// Gets status information for all tracked players (for admin dashboard).
        /// </summary>
        public List<PlayerTimeoutStatus> GetAllPlayerStatus()
        {
            var result = new List<PlayerTimeoutStatus>();
            
            lock (_stateLock)
            {
                var now = DateTime.UtcNow;
                
                foreach (var state in _playerStates.Values)
                {
                    var idleSeconds = (int)(now - state.LastActivityTime).TotalSeconds;
                    int? readyUpSecondsRemaining = null;
                    
                    if (_readyUpPhaseActive && state.ReadyUpDeadline.HasValue && !state.IsReady)
                    {
                        var remaining = state.ReadyUpDeadline.Value - now;
                        readyUpSecondsRemaining = Math.Max(0, (int)remaining.TotalSeconds);
                    }
                    
                    result.Add(new PlayerTimeoutStatus
                    {
                        PlayerId = state.PlayerId,
                        PlayerName = state.PlayerName,
                        IdleSeconds = idleSeconds,
                        IsReady = state.IsReady,
                        ReadyUpSecondsRemaining = readyUpSecondsRemaining,
                        IdleTimeoutSeconds = IdleTimeoutMinutes * 60,
                        ReadyUpTimeoutSeconds = ReadyUpTimeoutMinutes * 60
                    });
                }
            }
            
            return result;
        }
        
        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            
            lock (_stateLock)
            {
                _playerStates.Clear();
            }
        }
    }
    
    /// <summary>
    /// Status information for a player's timeout state.
    /// </summary>
    public sealed class PlayerTimeoutStatus
    {
        public Guid PlayerId { get; set; }
        public string PlayerName { get; set; }
        public int IdleSeconds { get; set; }
        public bool IsReady { get; set; }
        public int? ReadyUpSecondsRemaining { get; set; }
        public int IdleTimeoutSeconds { get; set; }
        public int ReadyUpTimeoutSeconds { get; set; }
    }
}
