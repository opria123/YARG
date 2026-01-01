using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using YARG.Core.Song;
using YARG.Multiplayer;
using YARG.Net.Packets;
using YARG.Networking.Abstraction.Handlers;

namespace YARG.Networking.Abstraction.Managers
{
    /// <summary>
    /// Manages gameplay-related networking operations: song selection, ready states,
    /// gameplay sync, and session phase transitions.
    /// Consolidates gameplay-related logic from LiteNetNetworkingAdapter.
    /// </summary>
    public sealed class NetworkGameplayManager : IDisposable
    {
        #region Fields

        private readonly Func<Dictionary<object, List<NetworkPlayerData>>> _getConnectedPlayers;
        
        private SessionPhase _currentPhase = SessionPhase.Lobby;
        private string _currentSongHash = string.Empty;
        private SongEntry _selectedSong;
        
        // Auto-start tracking
        private bool _autoStartOnAllReady;
        private bool _autoStartPending;
        
        // Gameplay load barrier
        private TaskCompletionSource<bool> _gameplayLoadTcs;
        private readonly object _gameplayLoadLock = new();
        private bool _allPlayersLoadedSignaled;
        
        // Late join tracking
        private bool _isLateJoiner;
        private bool _waitingForLateJoinDecision;
        private LateJoinAction _myLateJoinAction = LateJoinAction.NormalJoin;
        
        // Host-side late join tracking
        private readonly Dictionary<Guid, LateJoinPlayerState> _pendingLateJoiners = new();

        #endregion

        #region Events

        /// <summary>Fired when the session phase changes.</summary>
        public event Action<SessionPhase> OnPhaseChanged;
        
        /// <summary>Fired when all players have loaded and gameplay should start.</summary>
        public event Action OnAllPlayersLoaded;
        
        /// <summary>Fired when auto-start conditions are met.</summary>
        public event Action OnAutoStartTriggered;
        
        /// <summary>Fired when a late join decision is received.</summary>
        public event Action<Guid, LateJoinAction> OnLateJoinDecisionReceived;

        #endregion

        #region Properties

        /// <summary>Current session phase.</summary>
        public SessionPhase CurrentPhase => _currentPhase;
        
        /// <summary>Currently selected song hash.</summary>
        public string CurrentSongHash => _currentSongHash;
        
        /// <summary>Currently selected song.</summary>
        public SongEntry SelectedSong => _selectedSong;
        
        /// <summary>Whether auto-start on all ready is enabled.</summary>
        public bool AutoStartOnAllReady => _autoStartOnAllReady;
        
        /// <summary>Whether we are a late joiner.</summary>
        public bool IsLateJoiner => _isLateJoiner;
        
        /// <summary>Whether we're waiting for a late join decision.</summary>
        public bool WaitingForLateJoinDecision => _waitingForLateJoinDecision;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new NetworkGameplayManager.
        /// </summary>
        public NetworkGameplayManager(Func<Dictionary<object, List<NetworkPlayerData>>> getConnectedPlayers)
        {
            _getConnectedPlayers = getConnectedPlayers ?? throw new ArgumentNullException(nameof(getConnectedPlayers));
        }

        #endregion

        #region Phase Management

        /// <summary>
        /// Sets the current session phase.
        /// </summary>
        public void SetPhase(SessionPhase phase)
        {
            if (_currentPhase != phase)
            {
                var oldPhase = _currentPhase;
                _currentPhase = phase;
                NetworkLogger.Info($"Session phase: {oldPhase} -> {phase}");
                OnPhaseChanged?.Invoke(phase);
            }
        }

        /// <summary>
        /// Checks if we're in the lobby phase.
        /// </summary>
        public bool IsInLobby => _currentPhase == SessionPhase.Lobby;

        /// <summary>
        /// Checks if we're in gameplay.
        /// </summary>
        public bool IsInGameplay => _currentPhase == SessionPhase.PlayingSong || 
                                    _currentPhase == SessionPhase.Countdown ||
                                    _currentPhase == SessionPhase.DifficultySelect;

        #endregion

        #region Song Selection

        /// <summary>
        /// Sets the currently selected song.
        /// </summary>
        public void SetSelectedSong(SongEntry song)
        {
            _selectedSong = song;
            _currentSongHash = song?.Hash.ToString() ?? string.Empty;
            NetworkLogger.Info($"Selected song: {song?.Name ?? "none"} (hash: {_currentSongHash})");
        }

        /// <summary>
        /// Clears the selected song.
        /// </summary>
        public void ClearSelectedSong()
        {
            _selectedSong = null;
            _currentSongHash = string.Empty;
        }

        #endregion

        #region Ready State Checking

        /// <summary>
        /// Checks if all players are ready.
        /// </summary>
        public bool CheckAllPlayersReady()
        {
            var connectedPlayers = _getConnectedPlayers();
            if (connectedPlayers == null) return false;
            
            int totalPlayers = 0;
            int readyPlayers = 0;
            
            foreach (var kvp in connectedPlayers)
            {
                if (kvp.Value == null) continue;
                foreach (var player in kvp.Value)
                {
                    if (player == null) continue;
                    totalPlayers++;
                    if (player.IsReady) readyPlayers++;
                }
            }
            
            bool allReady = totalPlayers > 0 && readyPlayers == totalPlayers;
            NetworkLogger.Verbose($"Ready check: {readyPlayers}/{totalPlayers} ready");
            return allReady;
        }

        /// <summary>
        /// Gets a summary of ready states.
        /// </summary>
        public (int total, int ready) GetReadyStateSummary()
        {
            var connectedPlayers = _getConnectedPlayers();
            int total = 0;
            int ready = 0;
            
            if (connectedPlayers != null)
            {
                foreach (var kvp in connectedPlayers)
                {
                    if (kvp.Value == null) continue;
                    foreach (var player in kvp.Value)
                    {
                        if (player != null)
                        {
                            total++;
                            if (player.IsReady) ready++;
                        }
                    }
                }
            }
            
            return (total, ready);
        }

        #endregion

        #region Auto-Start

        /// <summary>
        /// Enables or disables auto-start when all players are ready.
        /// </summary>
        public void SetAutoStartOnAllReady(bool enabled)
        {
            _autoStartOnAllReady = enabled;
            NetworkLogger.Info($"Auto-start on all ready: {enabled}");
        }

        /// <summary>
        /// Handles potential auto-start when ready states change.
        /// </summary>
        public void CheckAutoStart(bool isHosting)
        {
            if (!_autoStartOnAllReady || !isHosting || _autoStartPending)
            {
                return;
            }
            
            if (CheckAllPlayersReady())
            {
                _autoStartPending = true;
                NetworkLogger.Info("All players ready - triggering auto-start");
                OnAutoStartTriggered?.Invoke();
            }
        }

        /// <summary>
        /// Resets auto-start pending state.
        /// </summary>
        public void ResetAutoStartPending()
        {
            _autoStartPending = false;
        }

        #endregion

        #region Gameplay Load Barrier

        /// <summary>
        /// Waits for all players to signal they've loaded.
        /// </summary>
        public Task<bool> WaitForAllPlayersLoaded()
        {
            lock (_gameplayLoadLock)
            {
                if (_allPlayersLoadedSignaled)
                {
                    return Task.FromResult(true);
                }
                
                _gameplayLoadTcs ??= new TaskCompletionSource<bool>();
                return _gameplayLoadTcs.Task;
            }
        }

        /// <summary>
        /// Signals that all players have loaded (host calls this after receiving all load-ready messages).
        /// </summary>
        public void SignalAllPlayersLoaded()
        {
            lock (_gameplayLoadLock)
            {
                _allPlayersLoadedSignaled = true;
                _gameplayLoadTcs?.TrySetResult(true);
                NetworkLogger.Info("All players loaded signal received");
                OnAllPlayersLoaded?.Invoke();
            }
        }

        /// <summary>
        /// Resets the gameplay load barrier for a new song.
        /// </summary>
        public void ResetGameplayLoadBarrier()
        {
            lock (_gameplayLoadLock)
            {
                _allPlayersLoadedSignaled = false;
                _gameplayLoadTcs = null;
            }
        }

        #endregion

        #region Late Join

        /// <summary>
        /// Marks this client as a late joiner.
        /// </summary>
        public void SetLateJoiner(bool isLateJoiner, LateJoinAction action = LateJoinAction.NormalJoin)
        {
            _isLateJoiner = isLateJoiner;
            _myLateJoinAction = action;
            NetworkLogger.Info($"Late joiner: {isLateJoiner}, action: {action}");
        }

        /// <summary>
        /// Sets whether we're waiting for a late join decision from the host.
        /// </summary>
        public void SetWaitingForLateJoinDecision(bool waiting)
        {
            _waitingForLateJoinDecision = waiting;
        }

        /// <summary>
        /// Adds a pending late joiner (host-side).
        /// </summary>
        public void AddPendingLateJoiner(Guid playerId, LateJoinPlayerState state)
        {
            _pendingLateJoiners[playerId] = state;
            NetworkLogger.Info($"Added pending late joiner: {playerId}");
        }

        /// <summary>
        /// Gets a pending late joiner state.
        /// </summary>
        public LateJoinPlayerState GetPendingLateJoiner(Guid playerId)
        {
            return _pendingLateJoiners.TryGetValue(playerId, out var state) ? state : null;
        }

        /// <summary>
        /// Removes a pending late joiner.
        /// </summary>
        public void RemovePendingLateJoiner(Guid playerId)
        {
            _pendingLateJoiners.Remove(playerId);
        }

        /// <summary>
        /// Clears all pending late joiners.
        /// </summary>
        public void ClearPendingLateJoiners()
        {
            _pendingLateJoiners.Clear();
        }

        #endregion

        #region Cleanup

        /// <summary>
        /// Resets all gameplay state.
        /// </summary>
        public void Reset()
        {
            _currentPhase = SessionPhase.Lobby;
            _currentSongHash = string.Empty;
            _selectedSong = null;
            _autoStartPending = false;
            _isLateJoiner = false;
            _waitingForLateJoinDecision = false;
            _myLateJoinAction = LateJoinAction.NormalJoin;
            _pendingLateJoiners.Clear();
            ResetGameplayLoadBarrier();
            
            NetworkLogger.Info("Reset gameplay state");
        }

        public void Dispose()
        {
            Reset();
        }

        #endregion
    }

    /// <summary>
    /// State for a player who is late joining.
    /// </summary>
    public class LateJoinPlayerState
    {
        public Guid PlayerId { get; set; }
        public string PlayerName { get; set; }
        public LateJoinAction RequestedAction { get; set; }
        public DateTime RequestTime { get; set; }
        public bool DecisionSent { get; set; }
    }
}
