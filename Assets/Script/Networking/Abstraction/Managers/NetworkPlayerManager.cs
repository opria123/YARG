using System;
using System.Collections.Generic;
using System.Linq;
using YARG.Multiplayer;
using YARG.Net.Transport;
using YARG.Networking.Abstraction.Handlers;
using YARG.Networking.Helpers;

namespace YARG.Networking.Abstraction.Managers
{
    /// <summary>
    /// Manages player tracking, state, and queries across the networking layer.
    /// Consolidates player-related logic from LiteNetNetworkingAdapter.
    /// </summary>
    public sealed class NetworkPlayerManager : IDisposable
    {
        #region Fields

        // Player tracking - keyed by connection identifier
        private readonly Dictionary<object, List<NetworkPlayerData>> _connectedPlayers = new();
        private readonly Dictionary<Guid, List<NetworkPlayerData>> _connectedPlayersByConnection = new();
        
        // Connection tracking
        private readonly Dictionary<Guid, INetConnection> _connectionMap = new();
        private readonly Dictionary<Guid, INetConnection> _pendingConnections = new();
        
        // Remote player ID to name mapping (client-side, for band assignment)
        private readonly Dictionary<Guid, string> _remotePlayerIdToName = new();
        
        // Host player ID tracking (client-side)
        private readonly List<Guid> _expectedHostPlayerIds = new();
        private int _hostPlayerIdAssignmentIndex = 0;
        
        // Well-known ConnectionId for the host's players
        private static readonly Guid HostConnectionId = new Guid("00000000-0000-0000-0000-000000000001");
        
        // Delegate for creating player data
        private readonly Func<string, bool, bool, int, int, Guid, NetworkPlayerData> _createPlayerDataFunc;

        #endregion

        #region Events

        /// <summary>Fired when a player joins.</summary>
        public event Action<NetworkPlayerData> OnPlayerJoined;
        
        /// <summary>Fired when a player leaves.</summary>
        public event Action<NetworkPlayerData> OnPlayerLeft;
        
        /// <summary>Fired when a player's ready state changes.</summary>
        public event Action<string, bool> OnPlayerReadyStateChanged;
        
        /// <summary>Fired when all players are ready.</summary>
        public event Action OnAllPlayersReady;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new NetworkPlayerManager.
        /// </summary>
        /// <param name="createPlayerDataFunc">
        /// Factory function to create NetworkPlayerData instances.
        /// Parameters: name, isHost, isLocal, instrument, difficulty, connectionId
        /// </param>
        public NetworkPlayerManager(Func<string, bool, bool, int, int, Guid, NetworkPlayerData> createPlayerDataFunc)
        {
            _createPlayerDataFunc = createPlayerDataFunc ?? throw new ArgumentNullException(nameof(createPlayerDataFunc));
        }

        #endregion

        #region Player Access

        /// <summary>
        /// Gets all connected players across all connections.
        /// </summary>
        public IEnumerable<NetworkPlayerData> GetAllPlayers()
        {
            return _connectedPlayers.GetAllPlayersSafe();
        }

        /// <summary>
        /// Gets all local players.
        /// </summary>
        public IEnumerable<NetworkPlayerData> GetLocalPlayers()
        {
            return _connectedPlayers.GetLocalPlayers();
        }

        /// <summary>
        /// Gets all remote players.
        /// </summary>
        public IEnumerable<NetworkPlayerData> GetRemotePlayers()
        {
            return _connectedPlayers.GetRemotePlayers();
        }

        /// <summary>
        /// Gets the connected players dictionary (for backward compatibility).
        /// </summary>
        public Dictionary<object, List<NetworkPlayerData>> GetConnectedPlayers()
        {
            return _connectedPlayers;
        }

        /// <summary>
        /// Gets the connection map.
        /// </summary>
        public Dictionary<Guid, INetConnection> GetConnectionMap()
        {
            return _connectionMap;
        }

        /// <summary>
        /// Gets players from a specific connection.
        /// </summary>
        public IEnumerable<NetworkPlayerData> GetPlayersFromConnection(object connectionKey)
        {
            return _connectedPlayers.GetPlayersFromConnection(connectionKey);
        }

        /// <summary>
        /// Finds a player by their network ID.
        /// </summary>
        public NetworkPlayerData FindByNetworkId(Guid networkPlayerId)
        {
            return _connectedPlayers.FindByNetworkId(networkPlayerId);
        }

        /// <summary>
        /// Finds a player by name.
        /// </summary>
        public NetworkPlayerData FindByName(string playerName)
        {
            return _connectedPlayers.FindByName(playerName);
        }

        /// <summary>
        /// Gets the total player count.
        /// </summary>
        public int GetPlayerCount()
        {
            return _connectedPlayers.GetPlayerCount();
        }

        /// <summary>
        /// Gets the first local player, or null if none.
        /// </summary>
        public NetworkPlayerData GetLocalPlayer()
        {
            return GetLocalPlayers().FirstOrDefault();
        }

        #endregion

        #region Player Management

        /// <summary>
        /// Adds a host player.
        /// </summary>
        public NetworkPlayerData AddHostPlayer(string playerName, int instrument = -1, int difficulty = 0)
        {
            var player = _createPlayerDataFunc(playerName, true, true, instrument, difficulty, HostConnectionId);
            
            if (!_connectedPlayers.ContainsKey("host"))
            {
                _connectedPlayers["host"] = new List<NetworkPlayerData>();
            }
            _connectedPlayers["host"].Add(player);
            
            NetworkLogger.Info($"Added host player '{playerName}'");
            OnPlayerJoined?.Invoke(player);
            return player;
        }

        /// <summary>
        /// Adds a local (client) player.
        /// </summary>
        public NetworkPlayerData AddLocalPlayer(string playerName, int instrument = -1, int difficulty = 0)
        {
            var player = _createPlayerDataFunc(playerName, false, true, instrument, difficulty, Guid.Empty);
            
            if (!_connectedPlayers.ContainsKey("local"))
            {
                _connectedPlayers["local"] = new List<NetworkPlayerData>();
            }
            _connectedPlayers["local"].Add(player);
            
            NetworkLogger.Info($"Added local player '{playerName}'");
            OnPlayerJoined?.Invoke(player);
            return player;
        }

        /// <summary>
        /// Adds a remote player from a connection.
        /// </summary>
        public NetworkPlayerData AddRemotePlayer(string playerName, Guid connectionId, Guid networkPlayerId, 
            bool isHost = false, int instrument = -1, int difficulty = 0)
        {
            var player = _createPlayerDataFunc(playerName, isHost, false, instrument, difficulty, connectionId);
            player.NetworkPlayerId = networkPlayerId;
            
            string connectionKey = connectionId.ToString();
            if (!_connectedPlayers.ContainsKey(connectionKey))
            {
                _connectedPlayers[connectionKey] = new List<NetworkPlayerData>();
            }
            _connectedPlayers[connectionKey].Add(player);
            
            // Also track by connection for efficient lookups
            if (!_connectedPlayersByConnection.ContainsKey(connectionId))
            {
                _connectedPlayersByConnection[connectionId] = new List<NetworkPlayerData>();
            }
            _connectedPlayersByConnection[connectionId].Add(player);
            
            NetworkLogger.Info($"Added remote player '{playerName}' from connection {connectionId}");
            OnPlayerJoined?.Invoke(player);
            return player;
        }

        /// <summary>
        /// Removes a player.
        /// </summary>
        public bool RemovePlayer(NetworkPlayerData player)
        {
            if (player == null) return false;
            
            bool removed = false;
            foreach (var kvp in _connectedPlayers.ToList())
            {
                if (kvp.Value.Remove(player))
                {
                    removed = true;
                    if (kvp.Value.Count == 0)
                    {
                        _connectedPlayers.Remove(kvp.Key);
                    }
                    break;
                }
            }
            
            // Also remove from connection-based tracking
            foreach (var kvp in _connectedPlayersByConnection.ToList())
            {
                kvp.Value.Remove(player);
                if (kvp.Value.Count == 0)
                {
                    _connectedPlayersByConnection.Remove(kvp.Key);
                }
            }
            
            if (removed)
            {
                NetworkLogger.Info($"Removed player '{player.PlayerName}'");
                OnPlayerLeft?.Invoke(player);
            }
            
            return removed;
        }

        /// <summary>
        /// Removes all players from a connection.
        /// </summary>
        public List<NetworkPlayerData> RemovePlayersFromConnection(Guid connectionId)
        {
            string connectionKey = connectionId.ToString();
            var removedPlayers = new List<NetworkPlayerData>();
            
            if (_connectedPlayers.TryGetValue(connectionKey, out var players))
            {
                removedPlayers.AddRange(players);
                _connectedPlayers.Remove(connectionKey);
            }
            
            _connectedPlayersByConnection.Remove(connectionId);
            
            foreach (var player in removedPlayers)
            {
                NetworkLogger.Info($"Removed player '{player?.PlayerName}' (connection {connectionId})");
                OnPlayerLeft?.Invoke(player);
            }
            
            return removedPlayers;
        }

        #endregion

        #region Connection Management

        /// <summary>
        /// Adds a connection to the connection map.
        /// </summary>
        public void AddConnection(INetConnection connection)
        {
            _connectionMap[connection.Id] = connection;
            NetworkLogger.Verbose($"Added connection {connection.Id}");
        }

        /// <summary>
        /// Adds a pending connection.
        /// </summary>
        public void AddPendingConnection(INetConnection connection)
        {
            _pendingConnections[connection.Id] = connection;
            NetworkLogger.Verbose($"Added pending connection {connection.Id}");
        }

        /// <summary>
        /// Removes a pending connection and returns it.
        /// </summary>
        public INetConnection RemovePendingConnection(Guid connectionId)
        {
            if (_pendingConnections.TryGetValue(connectionId, out var connection))
            {
                _pendingConnections.Remove(connectionId);
                return connection;
            }
            return null;
        }

        /// <summary>
        /// Removes a connection from all tracking.
        /// </summary>
        public void RemoveConnection(Guid connectionId)
        {
            _connectionMap.Remove(connectionId);
            _pendingConnections.Remove(connectionId);
            NetworkLogger.Verbose($"Removed connection {connectionId}");
        }

        /// <summary>
        /// Gets a connection by ID.
        /// </summary>
        public INetConnection GetConnection(Guid connectionId)
        {
            _connectionMap.TryGetValue(connectionId, out var connection);
            return connection;
        }

        #endregion

        #region Ready State

        /// <summary>
        /// Sets a player's ready state.
        /// </summary>
        public void SetPlayerReady(NetworkPlayerData player, bool isReady, bool sittingOut = false)
        {
            if (player == null) return;
            
            bool wasReady = player.IsReady;
            player.IsReady = isReady;
            player.SittingOut = sittingOut;
            
            if (wasReady != isReady)
            {
                NetworkLogger.Info($"Player '{player.PlayerName}' ready state: {isReady} (sitting out: {sittingOut})");
                OnPlayerReadyStateChanged?.Invoke(player.PlayerName, isReady);
            }
        }

        /// <summary>
        /// Checks if all players are ready.
        /// </summary>
        public bool AreAllPlayersReady()
        {
            var allPlayers = GetAllPlayers().ToList();
            if (allPlayers.Count == 0) return false;
            
            return allPlayers.All(p => p.IsReady);
        }

        /// <summary>
        /// Resets all players' ready states to false.
        /// </summary>
        public void ResetAllReadyStates()
        {
            _connectedPlayers.ResetAllReadyStates();
            NetworkLogger.Info("Reset all players' ready states");
        }

        /// <summary>
        /// Checks if all players are ready and fires the OnAllPlayersReady event if so.
        /// </summary>
        public void CheckAndNotifyAllPlayersReady()
        {
            if (AreAllPlayersReady())
            {
                NetworkLogger.Info("All players are ready!");
                OnAllPlayersReady?.Invoke();
            }
        }

        #endregion

        #region Remote Player ID Mapping

        /// <summary>
        /// Stores a mapping from remote player ID to name.
        /// </summary>
        public void SetRemotePlayerIdMapping(Guid playerId, string playerName)
        {
            _remotePlayerIdToName[playerId] = playerName;
        }

        /// <summary>
        /// Gets the name for a remote player ID.
        /// </summary>
        public string GetRemotePlayerName(Guid playerId)
        {
            return _remotePlayerIdToName.TryGetValue(playerId, out var name) ? name : null;
        }

        /// <summary>
        /// Clears all remote player ID mappings.
        /// </summary>
        public void ClearRemotePlayerIdMappings()
        {
            _remotePlayerIdToName.Clear();
        }

        #endregion

        #region Host Player ID Assignment (Client-side)

        /// <summary>
        /// Sets the expected host player IDs from track order.
        /// </summary>
        public void SetExpectedHostPlayerIds(List<Guid> playerIds)
        {
            _expectedHostPlayerIds.Clear();
            _expectedHostPlayerIds.AddRange(playerIds);
            _hostPlayerIdAssignmentIndex = 0;
        }

        /// <summary>
        /// Gets the next expected host player ID for assignment.
        /// </summary>
        public Guid? GetNextExpectedHostPlayerId()
        {
            if (_hostPlayerIdAssignmentIndex < _expectedHostPlayerIds.Count)
            {
                return _expectedHostPlayerIds[_hostPlayerIdAssignmentIndex++];
            }
            return null;
        }

        #endregion

        #region Cleanup

        /// <summary>
        /// Clears all player and connection state.
        /// </summary>
        public void Clear()
        {
            _connectedPlayers.Clear();
            _connectedPlayersByConnection.Clear();
            _connectionMap.Clear();
            _pendingConnections.Clear();
            _remotePlayerIdToName.Clear();
            _expectedHostPlayerIds.Clear();
            _hostPlayerIdAssignmentIndex = 0;
            
            NetworkLogger.Info("Cleared all player state");
        }

        public void Dispose()
        {
            Clear();
        }

        #endregion
    }
}
