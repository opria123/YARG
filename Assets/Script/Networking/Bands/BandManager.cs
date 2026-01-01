using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using YARG.Networking.Abstraction;
using YARG.Networking.Settings;

namespace YARG.Networking.Bands
{
    /// <summary>
    /// Manages band assignments for large multiplayer sessions.
    /// When band size is set (e.g., 4), players are split into competing bands.
    /// Only the local player's band is fully simulated; other bands sync final scores.
    /// 
    /// Key rules:
    /// - All local profiles from a single client MUST stay in the same band
    /// - Band names are randomly generated and persist for the session
    /// - Host or any band member can regenerate their band's name
    /// </summary>
    public sealed class BandManager : MonoBehaviour
    {
        public static BandManager Instance { get; private set; }

        [SerializeField] private int defaultBandSize = 4;

        private int _bandSize;
        private int _lobbySeed;
        private bool _isInitialized;
        private string _currentLobbyId; // Track which lobby we're initialized for
        private readonly Dictionary<Guid, int> _playerBandAssignments = new();
        private readonly Dictionary<int, BandInfo> _bands = new();
        private readonly Dictionary<int, List<Guid>> _connectionPlayerGroups = new(); // connectionId -> playerIds
        private int _localPlayerBandId = -1;
        private int _localConnectionId = -1;

        /// <summary>
        /// Gets the current band size. 0 means band system is disabled (all players in one band).
        /// </summary>
        public int BandSize => _bandSize;

        /// <summary>
        /// Gets whether the band system is active (bandSize > 0 and multiple bands exist).
        /// </summary>
        public bool IsBandSystemActive => _bandSize > 0 && _bands.Count > 1;

        /// <summary>
        /// Gets whether bands are enabled (bandSize > 0), even if only one band exists.
        /// </summary>
        public bool AreBandsEnabled => _bandSize > 0;

        /// <summary>
        /// Gets the local player's band ID.
        /// </summary>
        public int LocalPlayerBandId => _localPlayerBandId;

        /// <summary>
        /// Gets the lobby seed used for deterministic name generation.
        /// </summary>
        public int LobbySeed => _lobbySeed;

        /// <summary>
        /// Gets whether the band system has been initialized for the current session.
        /// </summary>
        public bool IsInitialized => _isInitialized;

        /// <summary>
        /// Gets all bands in the session.
        /// </summary>
        public IReadOnlyDictionary<int, BandInfo> Bands => _bands;

        /// <summary>
        /// Fired when band assignments change.
        /// </summary>
        public event Action OnBandsChanged;

        /// <summary>
        /// Fired when band scores are updated.
        /// </summary>
        public event Action<int, long> OnBandScoreUpdated;

        /// <summary>
        /// Fired when a band name is changed/regenerated.
        /// Parameters: bandId, newName
        /// </summary>
        public event Action<int, string> OnBandNameChanged;
        
        /// <summary>
        /// Fired when a band fails during gameplay.
        /// Parameters: bandId
        /// </summary>
        public event Action<int> OnBandFailed;
        
        /// <summary>
        /// Fired when all bands have failed.
        /// </summary>
        public event Action OnAllBandsFailed;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        /// <summary>
        /// Initializes the band system with the given band size.
        /// </summary>
        /// <param name="bandSize">Max players per band. 0 disables the band system.</param>
        /// <param name="lobbySeed">Optional seed for deterministic band name generation. If not provided, generates random seed.</param>
        /// <param name="lobbyId">Optional lobby ID to track which session we're initialized for.</param>
        /// <param name="forceReinitialize">If true, forces full reinitialization even if already initialized.</param>
        public void Initialize(int bandSize, int? lobbySeed = null, string lobbyId = null, bool forceReinitialize = false)
        {
            // Check if we're already initialized for this lobby with the same band size
            if (_isInitialized && !forceReinitialize)
            {
                bool samelobby = string.IsNullOrEmpty(lobbyId) || lobbyId == _currentLobbyId;
                bool sameBandSize = bandSize == _bandSize;
                
                if (samelobby && sameBandSize)
                {
                    Debug.Log($"[BandManager] Already initialized for this lobby with band size {bandSize}, skipping");
                    return;
                }
                
                // Band size changed - we need to reassign but keep the same seed
                if (samelobby && !sameBandSize)
                {
                    Debug.Log($"[BandManager] Band size changed from {_bandSize} to {bandSize}, reassigning players");
                    _bandSize = bandSize;
                    ReassignAllPlayers();
                    return;
                }
            }
            
            _bandSize = bandSize;
            _lobbySeed = lobbySeed ?? UnityEngine.Random.Range(int.MinValue, int.MaxValue);
            _currentLobbyId = lobbyId;
            _playerBandAssignments.Clear();
            _bands.Clear();
            _connectionPlayerGroups.Clear();
            _localPlayerBandId = -1;
            _localConnectionId = -1;
            _isInitialized = true;

            Debug.Log($"[BandManager] Initialized with band size: {bandSize}, lobby seed: {_lobbySeed}, lobbyId: {lobbyId ?? "null"}");
        }
        
        /// <summary>
        /// Reassigns all currently tracked players to bands based on the current band size.
        /// Called when band size changes but we want to preserve the seed.
        /// </summary>
        private void ReassignAllPlayers()
        {
            // Save current connection groups
            var savedGroups = new Dictionary<int, List<Guid>>(_connectionPlayerGroups);
            bool wasLocalSet = _localConnectionId >= 0;
            int savedLocalConnectionId = _localConnectionId;
            
            // Clear assignments but keep seed and lobby ID
            _playerBandAssignments.Clear();
            _bands.Clear();
            _connectionPlayerGroups.Clear();
            _localPlayerBandId = -1;
            _localConnectionId = -1;
            
            // Re-assign all groups
            foreach (var kvp in savedGroups)
            {
                bool isLocal = wasLocalSet && kvp.Key == savedLocalConnectionId;
                AssignClientPlayersToBand(kvp.Key, kvp.Value, isLocal);
            }
            
            Debug.Log($"[BandManager] Reassigned {savedGroups.Count} connection groups to bands");
            OnBandsChanged?.Invoke();
        }

        /// <summary>
        /// Initializes from a session preset.
        /// </summary>
        public void Initialize(SessionPreset preset, int? lobbySeed = null, string lobbyId = null, bool forceReinitialize = false)
        {
            Initialize(preset?.BandSize ?? 0, lobbySeed, lobbyId, forceReinitialize);
        }

        /// <summary>
        /// Sets the local connection ID (used to identify which players belong to this client).
        /// </summary>
        public void SetLocalConnectionId(int connectionId)
        {
            _localConnectionId = connectionId;
            Debug.Log($"[BandManager] Local connection ID set to: {connectionId}");
        }

        #region Validation

        /// <summary>
        /// Validation result for band configuration.
        /// </summary>
        public struct BandValidationResult
        {
            public bool IsValid;
            public string ErrorMessage;
            public int? ProblematicConnectionId;
            public int? RequiredBandSize;

            public static BandValidationResult Valid() => new() { IsValid = true };
            
            public static BandValidationResult Invalid(string message, int? connectionId = null, int? requiredSize = null) 
                => new() 
                { 
                    IsValid = false, 
                    ErrorMessage = message, 
                    ProblematicConnectionId = connectionId,
                    RequiredBandSize = requiredSize
                };
        }

        /// <summary>
        /// Validates that the current band configuration is valid.
        /// Checks that no client has more local profiles than the band size allows.
        /// </summary>
        /// <returns>Validation result with error details if invalid.</returns>
        public BandValidationResult ValidateBandConfiguration()
        {
            // If bands are disabled, always valid
            if (_bandSize <= 0)
            {
                return BandValidationResult.Valid();
            }

            foreach (var kvp in _connectionPlayerGroups)
            {
                int connectionId = kvp.Key;
                int playerCount = kvp.Value.Count;

                if (playerCount > _bandSize)
                {
                    string message = connectionId == _localConnectionId
                        ? $"You have {playerCount} local players, but band size is {_bandSize}. " +
                          $"Increase band size to at least {playerCount}, or remove {playerCount - _bandSize} local profile(s)."
                        : $"A client has {playerCount} players, but band size is {_bandSize}. " +
                          $"Increase band size to at least {playerCount}.";

                    return BandValidationResult.Invalid(message, connectionId, playerCount);
                }
            }

            return BandValidationResult.Valid();
        }

        /// <summary>
        /// Validates that all bands are ready for game start.
        /// Checks that no band has more players than the band size allows.
        /// This is separate from ValidateBandConfiguration which checks client groups.
        /// </summary>
        /// <returns>Validation result with error details if any band is over capacity.</returns>
        public BandValidationResult ValidateForGameStart()
        {
            // If bands are disabled, always valid
            if (_bandSize <= 0)
            {
                return BandValidationResult.Valid();
            }

            foreach (var kvp in _bands)
            {
                int bandId = kvp.Key;
                var band = kvp.Value;
                int playerCount = band.PlayerIds.Count;

                if (playerCount > _bandSize)
                {
                    string bandName = band.DisplayName ?? $"Band {bandId}";
                    string message = $"{bandName} has {playerCount} players, but band size is {_bandSize}. " +
                                     $"Move {playerCount - _bandSize} player(s) to another band.";
                    return BandValidationResult.Invalid(message, bandId, playerCount);
                }
            }

            return BandValidationResult.Valid();
        }

        /// <summary>
        /// Gets a list of all bands that are over capacity.
        /// </summary>
        /// <returns>List of band IDs that have more players than band size allows.</returns>
        public List<int> GetOverCapacityBands()
        {
            var result = new List<int>();
            if (_bandSize <= 0) return result;

            foreach (var kvp in _bands)
            {
                if (kvp.Value.PlayerIds.Count > _bandSize)
                {
                    result.Add(kvp.Key);
                }
            }
            return result;
        }

        /// <summary>
        /// Checks if a specific band is over capacity.
        /// </summary>
        public bool IsBandOverCapacity(int bandId)
        {
            if (_bandSize <= 0) return false;
            if (!_bands.TryGetValue(bandId, out var band)) return false;
            return band.PlayerIds.Count > _bandSize;
        }

        /// <summary>
        /// Checks if a specific player count would fit in the current band size.
        /// </summary>
        public bool CanFitPlayerGroup(int playerCount)
        {
            return _bandSize <= 0 || playerCount <= _bandSize;
        }

        #endregion

        #region Client Group Assignment

        /// <summary>
        /// Registers a group of players from a single client connection.
        /// All players from the same connection must stay in the same band.
        /// If the group exceeds band size, they still get assigned to a new band (over capacity).
        /// The host must reorganize players before starting the game.
        /// </summary>
        /// <param name="connectionId">The network connection ID.</param>
        /// <param name="playerIds">All player IDs from this connection.</param>
        /// <param name="isLocalConnection">Whether this is the local client's connection.</param>
        /// <returns>The assigned band ID.</returns>
        public int AssignClientPlayersToBand(int connectionId, List<Guid> playerIds, bool isLocalConnection = false)
        {
            if (playerIds == null || playerIds.Count == 0)
            {
                Debug.LogWarning($"[BandManager] Cannot assign empty player list for connection {connectionId}");
                return -1;
            }

            // Store the connection -> players mapping
            _connectionPlayerGroups[connectionId] = new List<Guid>(playerIds);

            if (isLocalConnection)
            {
                _localConnectionId = connectionId;
            }

            // Find or create a band for all players
            int bandId;
            if (_bandSize <= 0)
            {
                // Bands disabled - everyone in band 0
                bandId = 0;
                EnsureBandExists(bandId);
            }
            else
            {
                // Check if group exceeds band size - if so, create a dedicated (over capacity) band for them
                if (playerIds.Count > _bandSize)
                {
                    Debug.LogWarning($"[BandManager] Connection {connectionId} has {playerIds.Count} players but band size is {_bandSize}. " +
                                     $"Creating over-capacity band - host must reorganize before starting.");
                    bandId = CreateNewBand();
                }
                else
                {
                    // Find a band with enough space for the entire group, or create new
                    bandId = FindBandWithSpaceForGroup(playerIds.Count) ?? CreateNewBand();
                }
            }

            // Assign all players to the same band
            foreach (var playerId in playerIds)
            {
                AssignToBandInternal(playerId, bandId, connectionId, isLocalConnection);
            }

            Debug.Log($"[BandManager] Assigned {playerIds.Count} players from connection {connectionId} to band {bandId} ({_bands[bandId].DisplayName})");
            OnBandsChanged?.Invoke();

            return bandId;
        }

        /// <summary>
        /// Gets all player IDs associated with a connection.
        /// </summary>
        public List<Guid> GetPlayersForConnection(int connectionId)
        {
            return _connectionPlayerGroups.TryGetValue(connectionId, out var players) 
                ? new List<Guid>(players) 
                : new List<Guid>();
        }

        /// <summary>
        /// Gets the connection ID for a player.
        /// </summary>
        public int GetConnectionForPlayer(Guid playerId)
        {
            foreach (var kvp in _connectionPlayerGroups)
            {
                if (kvp.Value.Contains(playerId))
                {
                    return kvp.Key;
                }
            }
            
            // Debug: Log when player not found in any connection group
            Debug.LogWarning($"[BandManager] GetConnectionForPlayer: Player {playerId} not found in any connection group. " +
                $"Groups: {_connectionPlayerGroups.Count}, Players tracked: {_connectionPlayerGroups.Values.Sum(g => g.Count)}");
            return -1;
        }

        /// <summary>
        /// Checks if a player can be moved individually (only if they're the sole player from their connection).
        /// </summary>
        public bool CanMovePlayerIndividually(Guid playerId)
        {
            int connectionId = GetConnectionForPlayer(playerId);
            if (connectionId < 0) return false;
            
            return _connectionPlayerGroups.TryGetValue(connectionId, out var players) && players.Count == 1;
        }

        /// <summary>
        /// Gets the players that would be moved if moving a specific player.
        /// Returns just the player if they're alone, or all players from their connection.
        /// </summary>
        public List<Guid> GetMovablePlayerGroup(Guid playerId)
        {
            int connectionId = GetConnectionForPlayer(playerId);
            if (connectionId < 0)
            {
                Debug.Log($"[BandManager] GetMovablePlayerGroup: Player {playerId} has no connection, returning single player");
                return new List<Guid> { playerId };
            }
            
            var players = GetPlayersForConnection(connectionId);
            Debug.Log($"[BandManager] GetMovablePlayerGroup: Player {playerId} in connection {connectionId}, found {players.Count} players to move");
            return players;
        }

        #endregion

        #region Band Movement

        /// <summary>
        /// Moves a player (and their connection group if applicable) to a different band.
        /// Note: This allows moves even if it puts a band over capacity - the host
        /// must reorganize bands before starting the game.
        /// </summary>
        /// <param name="playerId">Any player from the group to move.</param>
        /// <param name="targetBandId">The target band ID.</param>
        /// <returns>True if move succeeded.</returns>
        public bool MovePlayerToBand(Guid playerId, int targetBandId)
        {
            var playersToMove = GetMovablePlayerGroup(playerId);
            if (playersToMove.Count == 0)
            {
                Debug.LogWarning($"[BandManager] Cannot find player group for {playerId}");
                return false;
            }

            // Note: We no longer block moves for capacity - host can reorganize freely
            // Validation will block game start if bands are over capacity

            int connectionId = GetConnectionForPlayer(playerId);
            bool isLocal = connectionId == _localConnectionId;

            // Move all players in the group
            foreach (var pid in playersToMove)
            {
                // Remove from old band
                if (_playerBandAssignments.TryGetValue(pid, out int oldBandId))
                {
                    if (_bands.TryGetValue(oldBandId, out var oldBand))
                    {
                        oldBand.PlayerIds.Remove(pid);
                        oldBand.ConnectionGroups.Remove(connectionId);
                        
                        // Clean up empty bands (except band 0 when bands are disabled)
                        if (oldBand.PlayerIds.Count == 0 && (_bandSize > 0 || oldBandId > 0))
                        {
                            _bands.Remove(oldBandId);
                            Debug.Log($"[BandManager] Removed empty band {oldBandId}");
                        }
                    }
                }

                AssignToBandInternal(pid, targetBandId, connectionId, isLocal);
            }

            Debug.Log($"[BandManager] Moved {playersToMove.Count} players to band {targetBandId}");
            OnBandsChanged?.Invoke();

            return true;
        }

        /// <summary>
        /// Gets the available space in a band.
        /// </summary>
        public int GetBandAvailableSpace(int bandId)
        {
            if (_bandSize <= 0) return int.MaxValue;
            
            if (!_bands.TryGetValue(bandId, out var band))
            {
                return _bandSize; // New band would have full space
            }

            return Math.Max(0, _bandSize - band.PlayerIds.Count);
        }

        /// <summary>
        /// Gets all bands that have space for a given number of players.
        /// </summary>
        public List<BandInfo> GetBandsWithSpaceFor(int playerCount)
        {
            if (_bandSize <= 0)
            {
                return _bands.Values.ToList();
            }

            return _bands.Values
                .Where(b => (_bandSize - b.PlayerIds.Count) >= playerCount)
                .ToList();
        }

        #endregion

        #region Band Names

        /// <summary>
        /// Regenerates the name for a band.
        /// </summary>
        /// <param name="bandId">The band to rename.</param>
        /// <returns>The new band name.</returns>
        public string RegenerateBandName(int bandId)
        {
            if (!_bands.TryGetValue(bandId, out var band))
            {
                Debug.LogWarning($"[BandManager] Cannot regenerate name for non-existent band {bandId}");
                return null;
            }

            band.IncrementNameGeneration();
            string newName = BandNameGenerator.Generate(_lobbySeed, bandId, band.NameRegenerationCount);
            band.GeneratedName = newName;

            Debug.Log($"[BandManager] Regenerated band {bandId} name to: {newName}");
            OnBandNameChanged?.Invoke(bandId, newName);
            OnBandsChanged?.Invoke();

            return newName;
        }

        /// <summary>
        /// Sets a custom name for a band (used for network sync).
        /// </summary>
        public void SetBandName(int bandId, string name, int regenerationCount = 0)
        {
            if (!_bands.TryGetValue(bandId, out var band))
            {
                Debug.LogWarning($"[BandManager] Cannot set name for non-existent band {bandId}");
                return;
            }

            band.GeneratedName = name;
            band.SetNameRegenerationCount(regenerationCount);
            
            OnBandNameChanged?.Invoke(bandId, name);
        }

        /// <summary>
        /// Checks if a player can regenerate a band's name (must be in the band or be host).
        /// </summary>
        public bool CanRegenerateBandName(Guid playerId, int bandId, bool isHost)
        {
            if (isHost) return true;
            
            if (!_playerBandAssignments.TryGetValue(playerId, out int playerBandId))
            {
                return false;
            }

            return playerBandId == bandId;
        }
        
        /// <summary>
        /// Updates a band's name from host (client-side).
        /// This is called when the client receives a name change broadcast from the host.
        /// </summary>
        public void UpdateBandNameFromHost(int bandId, string newName, int regenerationCount)
        {
            if (!_bands.TryGetValue(bandId, out var band))
            {
                Debug.LogWarning($"[BandManager] UpdateBandNameFromHost: Band {bandId} not found");
                return;
            }
            
            band.GeneratedName = newName;
            band.SetNameRegenerationCount(regenerationCount);
            
            Debug.Log($"[BandManager] Updated band {bandId} name from host: '{newName}' (regen={regenerationCount})");
            OnBandNameChanged?.Invoke(bandId, newName);
        }

        #endregion

        /// <summary>
        /// Removes a player from their band.
        /// If the player is part of a connection group, removes all players from that connection.
        /// </summary>
        public void RemovePlayer(Guid playerId)
        {
            int connectionId = GetConnectionForPlayer(playerId);
            var playersToRemove = connectionId >= 0 
                ? GetPlayersForConnection(connectionId) 
                : new List<Guid> { playerId };

            foreach (var pid in playersToRemove)
            {
                if (_playerBandAssignments.TryGetValue(pid, out int bandId))
                {
                    _playerBandAssignments.Remove(pid);

                    if (_bands.TryGetValue(bandId, out var band))
                    {
                        band.PlayerIds.Remove(pid);
                        
                        // Remove empty bands (except band 0 when bands disabled)
                        if (band.PlayerIds.Count == 0 && (_bandSize > 0 || bandId > 0))
                        {
                            _bands.Remove(bandId);
                            Debug.Log($"[BandManager] Removed empty band {bandId} ({band.DisplayName})");
                        }
                    }
                }
            }

            // Remove connection group
            if (connectionId >= 0)
            {
                _connectionPlayerGroups.Remove(connectionId);
            }

            OnBandsChanged?.Invoke();
        }

        /// <summary>
        /// Removes all players from a specific connection.
        /// </summary>
        public void RemoveConnection(int connectionId)
        {
            if (!_connectionPlayerGroups.TryGetValue(connectionId, out var players))
            {
                return;
            }

            foreach (var playerId in players.ToList())
            {
                if (_playerBandAssignments.TryGetValue(playerId, out int bandId))
                {
                    _playerBandAssignments.Remove(playerId);

                    if (_bands.TryGetValue(bandId, out var band))
                    {
                        band.PlayerIds.Remove(playerId);
                        band.ConnectionGroups.Remove(connectionId);

                        if (band.PlayerIds.Count == 0 && (_bandSize > 0 || bandId > 0))
                        {
                            _bands.Remove(bandId);
                            Debug.Log($"[BandManager] Removed empty band {bandId}");
                        }
                    }
                }
            }

            _connectionPlayerGroups.Remove(connectionId);
            OnBandsChanged?.Invoke();
        }

        /// <summary>
        /// Gets the band ID for a player.
        /// </summary>
        public int GetPlayerBandId(Guid playerId)
        {
            return _playerBandAssignments.TryGetValue(playerId, out int bandId) ? bandId : -1;
        }

        /// <summary>
        /// Gets the band info for a player.
        /// </summary>
        public BandInfo? GetPlayerBand(Guid playerId)
        {
            if (_playerBandAssignments.TryGetValue(playerId, out int bandId))
            {
                return _bands.TryGetValue(bandId, out var band) ? band : null;
            }
            return null;
        }

        /// <summary>
        /// Gets all players in the local player's band.
        /// </summary>
        public IEnumerable<Guid> GetLocalBandPlayers()
        {
            if (_localPlayerBandId < 0)
                return Enumerable.Empty<Guid>();

            return _bands.TryGetValue(_localPlayerBandId, out var band)
                ? band.PlayerIds
                : Enumerable.Empty<Guid>();
        }

        /// <summary>
        /// Checks if a player is in the same band as the local player.
        /// </summary>
        public bool IsInLocalBand(Guid playerId)
        {
            if (_localPlayerBandId < 0)
                return true; // Band system not active

            return _playerBandAssignments.TryGetValue(playerId, out int bandId) && bandId == _localPlayerBandId;
        }

        /// <summary>
        /// Updates the total score for a band (called during gameplay sync).
        /// </summary>
        public void UpdateBandScore(int bandId, long totalScore)
        {
            if (_bands.TryGetValue(bandId, out var band))
            {
                band.TotalScore = totalScore;
                OnBandScoreUpdated?.Invoke(bandId, totalScore);
            }
        }

        /// <summary>
        /// Gets bands sorted by score (for leaderboard display).
        /// </summary>
        public IEnumerable<BandInfo> GetBandsByScore()
        {
            return _bands.Values.OrderByDescending(b => b.TotalScore);
        }

        /// <summary>
        /// Resets all band scores (call at start of new song).
        /// </summary>
        public void ResetScores()
        {
            foreach (var band in _bands.Values)
            {
                band.TotalScore = 0;
            }
        }
        
        /// <summary>
        /// Resets all band failure states (call at start of new song).
        /// </summary>
        public void ResetFailureStates()
        {
            foreach (var band in _bands.Values)
            {
                band.HasFailed = false;
            }
        }
        
        /// <summary>
        /// Marks a band as failed and fires the appropriate events.
        /// </summary>
        /// <param name="bandId">The band ID that failed.</param>
        public void MarkBandAsFailed(int bandId)
        {
            if (!_bands.TryGetValue(bandId, out var band))
            {
                Debug.LogWarning($"[BandManager] Cannot mark band {bandId} as failed - band not found");
                return;
            }
            
            if (band.HasFailed)
            {
                return; // Already failed
            }
            
            band.HasFailed = true;
            Debug.Log($"[BandManager] Band {bandId} ({band.DisplayName}) has failed");
            
            OnBandFailed?.Invoke(bandId);
            
            // Check if all bands have now failed
            if (AreAllBandsFailed())
            {
                Debug.Log("[BandManager] All bands have failed!");
                OnAllBandsFailed?.Invoke();
            }
        }
        
        /// <summary>
        /// Checks if a specific band has failed.
        /// </summary>
        public bool HasBandFailed(int bandId)
        {
            return _bands.TryGetValue(bandId, out var band) && band.HasFailed;
        }
        
        /// <summary>
        /// Checks if the local player's band has failed.
        /// </summary>
        public bool HasLocalBandFailed()
        {
            return _localPlayerBandId >= 0 && HasBandFailed(_localPlayerBandId);
        }
        
        /// <summary>
        /// Checks if all bands have failed.
        /// </summary>
        public bool AreAllBandsFailed()
        {
            if (_bands.Count == 0)
                return false;
            
            return _bands.Values.All(b => b.HasFailed);
        }
        
        /// <summary>
        /// Gets the number of bands still alive (not failed).
        /// </summary>
        public int GetAliveBandCount()
        {
            return _bands.Values.Count(b => !b.HasFailed);
        }
        
        /// <summary>
        /// Gets the first alive band that is not the local band, for spectate mode.
        /// </summary>
        /// <returns>The band ID to spectate, or -1 if no other bands are alive.</returns>
        public int GetNextAliveBandToSpectate()
        {
            // Try to find the next alive band after local band
            foreach (var band in _bands.Values.OrderBy(b => b.BandId))
            {
                if (!band.HasFailed && band.BandId != _localPlayerBandId)
                {
                    return band.BandId;
                }
            }
            
            return -1;
        }

        /// <summary>
        /// Clears all band data and resets initialization state.
        /// </summary>
        public void Clear()
        {
            _playerBandAssignments.Clear();
            _bands.Clear();
            _connectionPlayerGroups.Clear();
            _localPlayerBandId = -1;
            _localConnectionId = -1;
            _isInitialized = false;
            _currentLobbyId = null;
            OnBandsChanged?.Invoke();
        }

        /// <summary>
        /// Exports current state for network sync.
        /// </summary>
        public BandSyncData ExportSyncData()
        {
            // Build player name map from network service
            var playerNames = new Dictionary<Guid, string>();
            var networkService = YARG.Networking.Abstraction.NetworkingServiceFactory.Instance;
            if (networkService != null && networkService.IsNetworkActive)
            {
                foreach (var player in networkService.GetAllPlayers())
                {
                    if (player != null && player.NetworkPlayerId != Guid.Empty)
                    {
                        playerNames[player.NetworkPlayerId] = player.PlayerName ?? "Unknown";
                    }
                }
            }
            
            return new BandSyncData
            {
                BandSize = _bandSize,
                LobbySeed = _lobbySeed,
                PlayerAssignments = new Dictionary<Guid, int>(_playerBandAssignments),
                BandNames = _bands.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.GeneratedName),
                BandNameRegenerations = _bands.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.NameRegenerationCount),
                ConnectionGroups = _connectionPlayerGroups.ToDictionary(
                    kvp => kvp.Key, 
                    kvp => new List<Guid>(kvp.Value)),
                PlayerNames = playerNames
            };
        }

        /// <summary>
        /// Imports state from network sync.
        /// </summary>
        public void ImportSyncData(BandSyncData data, int localConnectionId)
        {
            _bandSize = data.BandSize;
            _lobbySeed = data.LobbySeed;
            _localConnectionId = localConnectionId;
            _playerBandAssignments.Clear();
            _bands.Clear();
            _connectionPlayerGroups.Clear();

            // Restore connection groups
            foreach (var kvp in data.ConnectionGroups)
            {
                _connectionPlayerGroups[kvp.Key] = new List<Guid>(kvp.Value);
            }

            // Restore player assignments and bands
            foreach (var kvp in data.PlayerAssignments)
            {
                Guid playerId = kvp.Key;
                int bandId = kvp.Value;
                int connectionId = -1;

                // Find connection for this player
                foreach (var cg in _connectionPlayerGroups)
                {
                    if (cg.Value.Contains(playerId))
                    {
                        connectionId = cg.Key;
                        break;
                    }
                }

                bool isLocal = connectionId == localConnectionId;
                AssignToBandInternal(playerId, bandId, connectionId, isLocal);
            }

            // Restore band names
            foreach (var kvp in data.BandNames)
            {
                if (_bands.TryGetValue(kvp.Key, out var band))
                {
                    band.GeneratedName = kvp.Value;
                    if (data.BandNameRegenerations.TryGetValue(kvp.Key, out int regenCount))
                    {
                        band.SetNameRegenerationCount(regenCount);
                    }
                }
            }

            Debug.Log($"[BandManager] Imported sync data: {_bands.Count} bands, {_playerBandAssignments.Count} players");
            OnBandsChanged?.Invoke();
        }

        /// <summary>
        /// Exports current band assignments for network sync (legacy).
        /// </summary>
        [Obsolete("Use ExportSyncData() for full sync including band names")]
        public Dictionary<Guid, int> ExportAssignments()
        {
            return new Dictionary<Guid, int>(_playerBandAssignments);
        }

        /// <summary>
        /// Imports band assignments from network sync (legacy).
        /// </summary>
        [Obsolete("Use ImportSyncData() for full sync including band names")]
        public void ImportAssignments(Dictionary<Guid, int> assignments, Guid localPlayerId)
        {
            _playerBandAssignments.Clear();
            _bands.Clear();

            foreach (var kvp in assignments)
            {
                bool isLocal = kvp.Key == localPlayerId;
                AssignToBandInternal(kvp.Key, kvp.Value, -1, isLocal);
            }

            OnBandsChanged?.Invoke();
        }

        #region Internal Methods

        private void AssignToBandInternal(Guid playerId, int bandId, int connectionId, bool isLocalPlayer)
        {
            // Remove from old band if reassigning
            if (_playerBandAssignments.TryGetValue(playerId, out int oldBandId) && oldBandId != bandId)
            {
                if (_bands.TryGetValue(oldBandId, out var oldBand))
                {
                    oldBand.PlayerIds.Remove(playerId);
                    if (connectionId >= 0)
                    {
                        oldBand.ConnectionGroups.Remove(connectionId);
                    }
                }
            }

            _playerBandAssignments[playerId] = bandId;

            // Ensure band exists
            EnsureBandExists(bandId);
            var band = _bands[bandId];

            if (!band.PlayerIds.Contains(playerId))
            {
                band.PlayerIds.Add(playerId);
            }

            if (connectionId >= 0 && !band.ConnectionGroups.ContainsKey(connectionId))
            {
                band.ConnectionGroups[connectionId] = _connectionPlayerGroups.TryGetValue(connectionId, out var players)
                    ? players.Count
                    : 1;
            }

            if (isLocalPlayer)
            {
                _localPlayerBandId = bandId;
            }
        }

        private void EnsureBandExists(int bandId)
        {
            if (!_bands.ContainsKey(bandId))
            {
                var band = new BandInfo(bandId);
                band.GeneratedName = BandNameGenerator.Generate(_lobbySeed, bandId, 0);
                _bands[bandId] = band;
                Debug.Log($"[BandManager] Created band {bandId}: {band.DisplayName}");
            }
        }

        private int? FindBandWithSpaceForGroup(int groupSize)
        {
            foreach (var kvp in _bands.OrderBy(b => b.Key))
            {
                int available = _bandSize - kvp.Value.PlayerIds.Count;
                if (available >= groupSize)
                {
                    return kvp.Key;
                }
            }
            return null;
        }

        private int? FindBandWithSpace()
        {
            return FindBandWithSpaceForGroup(1);
        }

        private int CreateNewBand()
        {
            int newId = _bands.Count > 0 ? _bands.Keys.Max() + 1 : 0;
            EnsureBandExists(newId);
            return newId;
        }
        
        /// <summary>
        /// Creates a new band and moves the specified player (and their connection group) to it.
        /// </summary>
        /// <param name="playerId">The player to move to the new band.</param>
        /// <returns>The new band ID, or -1 if creation failed.</returns>
        public int CreateNewBandAndMovePlayer(Guid playerId)
        {
            int newBandId = CreateNewBand();
            Debug.Log($"[BandManager] Created new band {newBandId} ({_bands[newBandId].DisplayName}) for player {playerId}");
            
            bool success = MovePlayerToBand(playerId, newBandId);
            if (!success)
            {
                // Clean up the empty band if move failed
                if (_bands.TryGetValue(newBandId, out var band) && band.PlayerIds.Count == 0)
                {
                    _bands.Remove(newBandId);
                    Debug.LogWarning($"[BandManager] Removed empty band {newBandId} after failed move");
                }
                return -1;
            }
            
            return newBandId;
        }

        #endregion
    }

    /// <summary>
    /// Data structure for syncing band state across network.
    /// </summary>
    public class BandSyncData
    {
        public int BandSize { get; set; }
        public int LobbySeed { get; set; }
        public Dictionary<Guid, int> PlayerAssignments { get; set; } = new();
        public Dictionary<int, string> BandNames { get; set; } = new();
        public Dictionary<int, int> BandNameRegenerations { get; set; } = new();
        public Dictionary<int, List<Guid>> ConnectionGroups { get; set; } = new();
        public Dictionary<Guid, string> PlayerNames { get; set; } = new();
    }

    /// <summary>
    /// Information about a band in a multiplayer session.
    /// </summary>
    public sealed class BandInfo
    {
        /// <summary>
        /// Unique band identifier within the session.
        /// </summary>
        public int BandId { get; }

        /// <summary>
        /// Generated fun name for the band (e.g., "Electric Gorillas").
        /// </summary>
        public string GeneratedName { get; set; }

        /// <summary>
        /// How many times the name has been regenerated.
        /// Used for deterministic generation across network.
        /// </summary>
        public int NameRegenerationCount { get; private set; }

        /// <summary>
        /// Player IDs in this band.
        /// </summary>
        public List<Guid> PlayerIds { get; } = new();

        /// <summary>
        /// Tracks which connections have players in this band.
        /// Key: connectionId, Value: number of players from that connection.
        /// </summary>
        public Dictionary<int, int> ConnectionGroups { get; } = new();

        /// <summary>
        /// Total band score (sum of all player scores).
        /// </summary>
        public long TotalScore { get; set; }
        
        /// <summary>
        /// Whether this band has failed during gameplay.
        /// </summary>
        public bool HasFailed { get; set; }

        /// <summary>
        /// Display name for the band.
        /// Returns the generated name if available, otherwise a default.
        /// </summary>
        public string DisplayName => !string.IsNullOrEmpty(GeneratedName) 
            ? GeneratedName 
            : $"Band {BandId + 1}";

        /// <summary>
        /// Number of players in the band.
        /// </summary>
        public int PlayerCount => PlayerIds.Count;

        /// <summary>
        /// Number of unique connections (clients) in this band.
        /// </summary>
        public int ConnectionCount => ConnectionGroups.Count;

        public BandInfo(int bandId)
        {
            BandId = bandId;
        }

        /// <summary>
        /// Increments the name regeneration counter.
        /// </summary>
        public void IncrementNameGeneration()
        {
            NameRegenerationCount++;
        }

        /// <summary>
        /// Sets the name regeneration count (used for network sync).
        /// </summary>
        public void SetNameRegenerationCount(int count)
        {
            NameRegenerationCount = count;
        }
    }
}
