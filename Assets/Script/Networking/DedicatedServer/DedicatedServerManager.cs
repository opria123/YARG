using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using YARG.Networking.Abstraction;
using YARG.Networking.Settings;

namespace YARG.Networking.DedicatedServer
{
    /// <summary>
    /// Manages dedicated server-specific functionality including:
    /// - Host role separation (host vs admin permissions)
    /// - Automatic host promotion on disconnect
    /// - Permission checking for UI elements
    /// </summary>
    public sealed class DedicatedServerManager : IDisposable
    {
        private static DedicatedServerManager _instance;
        
        /// <summary>
        /// Gets the singleton instance of the DedicatedServerManager.
        /// Returns null if not running as a dedicated server.
        /// </summary>
        public static DedicatedServerManager Instance => _instance;
        
        /// <summary>
        /// Returns true if we're running in dedicated server mode with restricted host permissions.
        /// </summary>
        public static bool IsDedicatedMode => _instance != null && DedicatedServerBootstrap.IsRunning;
        
        private readonly DedicatedServerConfig _config;
        private readonly IpBanList _banList;
        private readonly PlayerTimeoutTracker _timeoutTracker;
        private readonly VoteToKickManager _voteToKickManager;
        private Guid _currentHostPlayerId = Guid.Empty;
        private bool _isDisposed;
        private float _lastTimeoutCheckTime;
        
        /// <summary>
        /// Event fired when the host player changes.
        /// </summary>
        public event Action<Guid> OnHostChanged;
        
        /// <summary>
        /// Event fired when a player is kicked for timeout.
        /// Parameters: playerId, playerName, reason
        /// </summary>
        public event Action<Guid, string, string> OnPlayerTimedOut;
        
        /// <summary>
        /// Gets the current host player's network ID.
        /// </summary>
        public Guid CurrentHostPlayerId => _currentHostPlayerId;
        
        /// <summary>
        /// Gets the timeout tracker for player activity monitoring.
        /// </summary>
        public PlayerTimeoutTracker TimeoutTracker => _timeoutTracker;
        
        /// <summary>
        /// Gets the vote-to-kick manager.
        /// </summary>
        public VoteToKickManager VoteToKick => _voteToKickManager;
        
        private DedicatedServerManager(DedicatedServerConfig config, IpBanList banList)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _banList = banList ?? throw new ArgumentNullException(nameof(banList));
            _timeoutTracker = new PlayerTimeoutTracker(config);
            _voteToKickManager = new VoteToKickManager(config);
            
            // Subscribe to timeout events
            _timeoutTracker.OnPlayerTimeout += HandlePlayerTimeout;
            _timeoutTracker.OnPlayerTimeoutWarning += HandleTimeoutWarning;
            
            // Subscribe to vote-to-kick events
            _voteToKickManager.OnKickRequired += HandleVoteKickRequired;
        }
        
        /// <summary>
        /// Initializes the DedicatedServerManager. Called by DedicatedServerBootstrap.
        /// </summary>
        internal static void Initialize(DedicatedServerConfig config, IpBanList banList)
        {
            if (_instance != null)
            {
                Debug.LogWarning("[DedicatedServerManager] Already initialized");
                return;
            }
            
            _instance = new DedicatedServerManager(config, banList);
            _instance.SubscribeToEvents();
            
            Debug.Log("[DedicatedServerManager] Initialized");
        }
        
        /// <summary>
        /// Shuts down the DedicatedServerManager.
        /// </summary>
        internal static void Shutdown()
        {
            _instance?.Dispose();
            _instance = null;
        }
        
        private void SubscribeToEvents()
        {
            var networkService = NetworkingServiceFactory.Instance;
            if (networkService != null)
            {
                networkService.OnPlayerJoined += OnPlayerJoined;
                networkService.OnPlayerLeft += OnPlayerLeft;
                networkService.OnPlayerReadyStateChanged += OnPlayerReadyStateChanged;
                networkService.OnAllPlayersReady += OnAllPlayersReady;
            }
        }
        
        private void UnsubscribeFromEvents()
        {
            var networkService = NetworkingServiceFactory.Instance;
            if (networkService != null)
            {
                networkService.OnPlayerJoined -= OnPlayerJoined;
                networkService.OnPlayerLeft -= OnPlayerLeft;
                networkService.OnPlayerReadyStateChanged -= OnPlayerReadyStateChanged;
                networkService.OnAllPlayersReady -= OnAllPlayersReady;
            }
        }
        
        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            
            UnsubscribeFromEvents();
            
            // Cleanup timeout tracker
            if (_timeoutTracker != null)
            {
                _timeoutTracker.OnPlayerTimeout -= HandlePlayerTimeout;
                _timeoutTracker.OnPlayerTimeoutWarning -= HandleTimeoutWarning;
                _timeoutTracker.Dispose();
            }
            
            // Cleanup vote-to-kick manager
            if (_voteToKickManager != null)
            {
                _voteToKickManager.OnKickRequired -= HandleVoteKickRequired;
                _voteToKickManager.Dispose();
            }
        }
        
        #region Permission Checking
        
        /// <summary>
        /// Checks if the host player is allowed to kick other players.
        /// In dedicated server mode, only the admin can kick (via web UI).
        /// </summary>
        public bool CanHostKickPlayers()
        {
            // In dedicated mode, host cannot kick - only admin via web UI
            return !IsDedicatedMode;
        }
        
        /// <summary>
        /// Checks if the host player is allowed to change session settings.
        /// In dedicated server mode, only the admin can change settings (via web UI).
        /// </summary>
        public bool CanHostChangeSettings()
        {
            // In dedicated mode, host cannot change settings - only admin via web UI
            return !IsDedicatedMode;
        }
        
        /// <summary>
        /// Checks if the host player is allowed to move players between bands.
        /// In dedicated server mode, only the admin can move players (via web UI).
        /// </summary>
        public bool CanHostMovePlayers()
        {
            // In dedicated mode, host cannot move players - only admin via web UI
            return !IsDedicatedMode;
        }
        
        /// <summary>
        /// Checks if the host player is allowed to browse/select songs.
        /// This is always allowed for the host, even in dedicated mode.
        /// </summary>
        public bool CanHostBrowseSongs()
        {
            // Host can always browse/select songs, even in dedicated mode
            return true;
        }
        
        /// <summary>
        /// Checks if the host player is allowed to start the game.
        /// This is always allowed for the host when all players are ready.
        /// </summary>
        public bool CanHostStartGame()
        {
            // Host can always start the game when ready
            return true;
        }
        
        /// <summary>
        /// Returns true if the session settings panel should be shown in view-only mode.
        /// In dedicated server mode, even the host sees settings as view-only.
        /// </summary>
        public bool ShouldShowSettingsAsViewOnly()
        {
            return IsDedicatedMode;
        }
        
        #endregion
        
        #region Host Management
        
        /// <summary>
        /// Assigns the first connected player as the host.
        /// Called when the first player joins a dedicated server.
        /// </summary>
        private void AssignFirstPlayerAsHost(NetworkPlayerData player)
        {
            if (_currentHostPlayerId != Guid.Empty)
            {
                Debug.Log($"[DedicatedServerManager] Host already assigned: {_currentHostPlayerId}");
                return;
            }
            
            SetHost(player);
        }
        
        /// <summary>
        /// Promotes a player to host.
        /// </summary>
        /// <param name="playerId">The network player ID to promote.</param>
        /// <returns>True if promotion was successful.</returns>
        public bool PromoteToHost(Guid playerId)
        {
            var networkService = NetworkingServiceFactory.Instance;
            if (networkService == null)
            {
                Debug.LogWarning("[DedicatedServerManager] Cannot promote host - network service not available");
                return false;
            }
            
            var allPlayers = networkService.GetAllPlayers();
            var player = allPlayers.FirstOrDefault(p => p.NetworkPlayerId == playerId);
            
            if (player == null)
            {
                Debug.LogWarning($"[DedicatedServerManager] Cannot promote host - player {playerId} not found");
                return false;
            }
            
            SetHost(player);
            return true;
        }
        
        /// <summary>
        /// Sets a player as the current host and updates all related state.
        /// </summary>
        private void SetHost(NetworkPlayerData player)
        {
            var networkService = NetworkingServiceFactory.Instance;
            if (networkService == null) return;
            
            var previousHostId = _currentHostPlayerId;
            
            // Clear previous host flag
            if (previousHostId != Guid.Empty)
            {
                var allPlayers = networkService.GetAllPlayers();
                var previousHost = allPlayers.FirstOrDefault(p => p.NetworkPlayerId == previousHostId);
                if (previousHost != null)
                {
                    previousHost.IsHost = false;
                }
            }
            
            // Set new host
            _currentHostPlayerId = player.NetworkPlayerId;
            player.IsHost = true;
            
            Debug.Log($"[DedicatedServerManager] Host set to: {player.PlayerName} ({player.NetworkPlayerId})");
            
            // Broadcast host change to all clients
            if (networkService is LiteNetNetworkingAdapter adapter)
            {
                adapter.BroadcastHostChange(player.NetworkPlayerId);
            }
            
            OnHostChanged?.Invoke(_currentHostPlayerId);
        }
        
        /// <summary>
        /// Promotes the next eligible player to host when the current host disconnects.
        /// </summary>
        private void PromoteNextHost()
        {
            var networkService = NetworkingServiceFactory.Instance;
            if (networkService == null) return;
            
            var allPlayers = networkService.GetAllPlayers()
                .Where(p => p.NetworkPlayerId != _currentHostPlayerId)
                .OrderBy(p => p.JoinTime)
                .ToList();
            
            if (allPlayers.Count == 0)
            {
                Debug.Log("[DedicatedServerManager] No players left to promote to host");
                _currentHostPlayerId = Guid.Empty;
                OnHostChanged?.Invoke(Guid.Empty);
                return;
            }
            
            // Promote the player who joined earliest
            var newHost = allPlayers.First();
            SetHost(newHost);
        }
        
        #endregion
        
        #region Ban Checking
        
        /// <summary>
        /// Checks if an IP address is banned.
        /// </summary>
        public bool IsIpBanned(string ipAddress)
        {
            return _banList?.IsBanned(ipAddress) ?? false;
        }
        
        /// <summary>
        /// Gets the ban entry for an IP address if it exists.
        /// </summary>
        public IpBanList.BannedIpEntry GetBanEntry(string ipAddress)
        {
            return _banList?.BannedIps.FirstOrDefault(b => b.IpAddress == ipAddress);
        }
        
        #endregion
        
        #region Event Handlers
        
        private void OnPlayerJoined(NetworkPlayerData player)
        {
            if (player == null) return;
            
            // Register player for timeout tracking
            _timeoutTracker?.RegisterPlayer(player.NetworkPlayerId, player.PlayerName);
            
            // If there's no current host, make this player the host
            // Use _currentHostPlayerId as the authoritative source - don't rely on player.IsHost
            // as that flag may not be synchronized across all player instances yet
            if (_currentHostPlayerId == Guid.Empty)
            {
                Debug.Log($"[DedicatedServerManager] No host assigned, making {player.PlayerName} the host");
                AssignFirstPlayerAsHost(player);
            }
            else
            {
                Debug.Log($"[DedicatedServerManager] Player {player.PlayerName} joined. Current host is: {_currentHostPlayerId}");
            }
        }
        
        private void OnPlayerLeft(NetworkPlayerData player)
        {
            if (player == null) return;
            
            // Unregister from timeout tracking
            _timeoutTracker?.UnregisterPlayer(player.NetworkPlayerId);
            
            // Notify vote-to-kick manager
            _voteToKickManager?.OnPlayerLeft(player.NetworkPlayerId);
            
            // If the host left, promote the next player
            if (player.NetworkPlayerId == _currentHostPlayerId)
            {
                Debug.Log($"[DedicatedServerManager] Host {player.PlayerName} left, promoting next player");
                _currentHostPlayerId = Guid.Empty;
                PromoteNextHost();
            }
        }
        
        private void OnPlayerReadyStateChanged(string playerName, bool isReady)
        {
            // Find the player by name and update their ready state in the timeout tracker
            var networkService = NetworkingServiceFactory.Instance;
            if (networkService == null) return;
            
            var player = networkService.GetAllPlayers()
                .FirstOrDefault(p => string.Equals(p.PlayerName, playerName, StringComparison.OrdinalIgnoreCase));
            
            if (player != null)
            {
                _timeoutTracker?.SetPlayerReady(player.NetworkPlayerId, isReady);
            }
        }
        
        /// <summary>
        /// Called when all players in the lobby are ready.
        /// The networking adapter now handles auto-start internally via SetAutoStartOnAllReady().
        /// This callback is kept for logging and any future dedicated-server-specific handling.
        /// </summary>
        private void OnAllPlayersReady()
        {
            Debug.Log("[DedicatedServerManager] All players are ready! (Auto-start handled by networking adapter)");
            
            // Auto-start is now handled by the networking adapter's unified auto-start capability.
            // See LiteNetNetworkingAdapter.SetAutoStartOnAllReady() which is enabled for dedicated servers.
        }
        
        #endregion
        
        #region Timeout Handling
        
        /// <summary>
        /// Called every frame to check for player timeouts.
        /// Should be called from DedicatedServerBootstrap's Update method.
        /// </summary>
        internal void Update()
        {
            if (_isDisposed) return;
            
            // Check timeouts every second
            float now = Time.realtimeSinceStartup;
            if (now - _lastTimeoutCheckTime < 1.0f) return;
            _lastTimeoutCheckTime = now;
            
            _timeoutTracker?.CheckTimeouts();
            _voteToKickManager?.Update();
        }
        
        /// <summary>
        /// Records player activity. Call this when a player performs an action.
        /// </summary>
        public void RecordPlayerActivity(Guid playerId)
        {
            _timeoutTracker?.RecordActivity(playerId);
        }
        
        /// <summary>
        /// Updates a player's ready state for timeout tracking.
        /// </summary>
        public void SetPlayerReady(Guid playerId, bool isReady)
        {
            _timeoutTracker?.SetPlayerReady(playerId, isReady);
        }
        
        /// <summary>
        /// Starts the ready-up countdown phase.
        /// Call when the host has selected a song and waiting begins.
        /// </summary>
        public void StartReadyUpPhase()
        {
            _timeoutTracker?.StartReadyUpPhase();
        }
        
        /// <summary>
        /// Ends the ready-up countdown phase.
        /// Call when the game starts or returns to song browse.
        /// </summary>
        public void EndReadyUpPhase()
        {
            _timeoutTracker?.EndReadyUpPhase();
        }
        
        private void HandlePlayerTimeout(Guid playerId, string playerName, string reason)
        {
            Debug.Log($"[DedicatedServerManager] Player timed out: {playerName} - {reason}");
            
            // Kick the player
            KickPlayerById(playerId, $"Kicked: {reason}");
            
            // Raise event for logging/admin notification
            OnPlayerTimedOut?.Invoke(playerId, playerName, reason);
        }
        
        private void HandleTimeoutWarning(Guid playerId, string playerName, int secondsRemaining)
        {
            Debug.Log($"[DedicatedServerManager] Timeout warning for {playerName}: {secondsRemaining}s remaining");
            
            // NOTE: Requires in-game notification/chat system to send warnings to players.
            // For now, the warning is only logged server-side.
        }
        
        private void HandleVoteKickRequired(Guid playerId, string playerName, string reason)
        {
            Debug.Log($"[DedicatedServerManager] Vote kick passed for {playerName}: {reason}");
            
            // Kick the player
            KickPlayerById(playerId, reason);
        }
        
        /// <summary>
        /// Kicks a player by their network ID with a reason.
        /// </summary>
        public void KickPlayerById(Guid playerId, string reason)
        {
            var networkService = NetworkingServiceFactory.Instance;
            if (networkService == null) return;
            
            var player = networkService.GetAllPlayers()
                .FirstOrDefault(p => p.NetworkPlayerId == playerId);
            
            if (player == null)
            {
                Debug.LogWarning($"[DedicatedServerManager] Cannot kick - player {playerId} not found");
                return;
            }
            
            Debug.Log($"[DedicatedServerManager] Kicking player: {player.PlayerName} - {reason}");
            networkService.KickPlayer(player);
        }
        
        #endregion
    }
}
