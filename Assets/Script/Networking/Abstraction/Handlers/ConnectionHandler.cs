using System;
using System.Collections.Generic;
using UnityEngine;
using YARG.Multiplayer;
using YARG.Net;
using YARG.Net.Packets;
using YARG.Net.Transport;

namespace YARG.Networking.Abstraction.Handlers
{
    /// <summary>
    /// Handles connection lifecycle events and player management.
    /// Manages pending connections, player tracking, and connection state.
    /// </summary>
    public sealed class ConnectionHandler : IDisposable
    {
        private readonly Dictionary<Guid, INetConnection> _pendingConnections = new();
        private readonly Dictionary<Guid, INetConnection> _connectionMap = new();
        private readonly Dictionary<object, List<NetworkPlayerData>> _connectedPlayers = new();
        
        private bool _isHosting;
        private bool _isConnected;
        private bool _isJoinInProgress;
        private LobbyInfo _currentLobby;
        
        /// <summary>
        /// Fired when a new player successfully connects and is identified.
        /// </summary>
        public event Action<NetworkPlayerData> OnPlayerJoined;
        
        /// <summary>
        /// Fired when a player disconnects.
        /// </summary>
        public event Action<NetworkPlayerData> OnPlayerLeft;
        
        /// <summary>
        /// Fired when a client needs to send authentication (password-protected lobby).
        /// </summary>
        public event Action<INetConnection> OnAuthenticationRequired;
        
        /// <summary>
        /// Fired when a client should send their player identity.
        /// </summary>
        public event Action<INetConnection> OnSendPlayerIdentity;
        
        /// <summary>
        /// Fired when a new client connects and needs state sync (host only).
        /// </summary>
        public event Action<INetConnection, NetworkPlayerData> OnClientNeedsStateSync;
        
        /// <summary>
        /// Fired when connection state changes.
        /// </summary>
        public event Action<bool> OnConnectionStateChanged;
        
        /// <summary>
        /// Fired when lobby is left.
        /// </summary>
        public event Action OnLobbyLeft;

        #region Properties

        public bool IsHosting => _isHosting;
        public bool IsConnected => _isConnected;
        public bool IsJoinInProgress => _isJoinInProgress;
        public LobbyInfo CurrentLobby => _currentLobby;
        
        public IReadOnlyDictionary<Guid, INetConnection> ConnectionMap => _connectionMap;
        public IReadOnlyDictionary<object, List<NetworkPlayerData>> ConnectedPlayers => _connectedPlayers;
        
        #endregion

        #region State Management

        public void SetHostingState(bool isHosting)
        {
            _isHosting = isHosting;
            NetworkLogger.Info("Hosting state: {0}", isHosting);
        }

        public void SetConnectedState(bool isConnected)
        {
            bool wasConnected = _isConnected;
            _isConnected = isConnected;
            
            if (wasConnected != isConnected)
            {
                NetworkLogger.Connection("Connected state changed: {0}", isConnected);
                OnConnectionStateChanged?.Invoke(isConnected);
            }
        }

        public void SetJoinInProgress(bool inProgress)
        {
            _isJoinInProgress = inProgress;
            NetworkLogger.Verbose("Join in progress: {0}", inProgress);
        }

        public void SetCurrentLobby(LobbyInfo lobby)
        {
            _currentLobby = lobby;
            NetworkLogger.Verbose("Current lobby set: {0}", lobby?.LobbyName ?? "null");
        }

        #endregion

        #region Connection Events

        /// <summary>
        /// Handle a new peer connection from the transport layer.
        /// </summary>
        public void HandlePeerConnected(INetConnection connection, bool lobbyHasPassword)
        {
            // Store connection for latency updates (both client and server)
            _connectionMap[connection.Id] = connection;
            
            if (_isHosting)
            {
                HandleServerPeerConnected(connection);
            }
            else
            {
                HandleClientPeerConnected(connection, lobbyHasPassword);
            }
        }

        private void HandleServerPeerConnected(INetConnection connection)
        {
            NetworkLogger.Server("Client connected - {0}", connection.EndPoint);
            
            // Store connection as pending - we'll create player data when we receive their name
            _pendingConnections[connection.Id] = connection;
            NetworkLogger.Verbose("Stored pending connection {0}, waiting for player name", connection.Id);
            
            // Update player count
            if (_currentLobby != null)
            {
                _currentLobby.CurrentPlayers++;
                NetworkLogger.Verbose("Player count is now {0}", _currentLobby.CurrentPlayers);
            }
        }

        private void HandleClientPeerConnected(INetConnection connection, bool lobbyHasPassword)
        {
            if (lobbyHasPassword)
            {
                // Send authentication request first
                NetworkLogger.Client("Lobby has password, requesting authentication");
                OnAuthenticationRequired?.Invoke(connection);
            }
            else
            {
                // No password required - send player identity directly
                NetworkLogger.Client("Sending player identity to server");
                OnSendPlayerIdentity?.Invoke(connection);
            }
        }

        /// <summary>
        /// Handle a peer disconnection from the transport layer.
        /// </summary>
        public void HandlePeerDisconnected(INetConnection connection, Action<string> broadcastPlayerLeft = null)
        {
            if (!_isHosting) return;

            NetworkLogger.Server("Client disconnected - {0}", connection.EndPoint);
            
            string clientId = connection.Id.ToString();
            
            // Remove from connection map
            _connectionMap.Remove(connection.Id);
            _pendingConnections.Remove(connection.Id);
            
            // Handle player removal and get player count for CurrentPlayers update
            int playerCount = 0;
            if (_connectedPlayers.TryGetValue(clientId, out var players))
            {
                playerCount = players.Count;
                foreach (var player in players)
                {
                    NetworkLogger.Info("Player disconnected: {0}", player?.PlayerName ?? "unknown");
                    
                    // Notify about player leaving during gameplay
                    if (broadcastPlayerLeft != null && player != null)
                    {
                        broadcastPlayerLeft(player.PlayerName);
                    }
                    
                    OnPlayerLeft?.Invoke(player);
                }
                _connectedPlayers.Remove(clientId);
            }
            
            // Update player count based on number of players from this connection
            if (_currentLobby != null && playerCount > 0)
            {
                _currentLobby.CurrentPlayers = System.Math.Max(1, _currentLobby.CurrentPlayers - playerCount);
                NetworkLogger.Verbose("Player count is now {0}", _currentLobby.CurrentPlayers);
            }
        }

        #endregion

        #region Player Identity Handling

        /// <summary>
        /// Handle a player identity message from a connecting client (host only).
        /// </summary>
        public void HandlePlayerIdentity(INetConnection connection, NetworkPlayerIdentity identity, 
            Func<string, bool, bool, NetworkPlayerData> createPlayerData)
        {
            if (!_isHosting)
            {
                NetworkLogger.Warn("Received player identity but not hosting");
                return;
            }

            NetworkLogger.Server("Received player '{0}' (ID: {1}) from {2}", 
                identity.DisplayName, identity.PlayerId, connection.EndPoint);
            
            // Remove from pending connections
            _pendingConnections.Remove(connection.Id);
            
            // Create player data
            string clientId = connection.Id.ToString();
            var playerData = createPlayerData(identity.DisplayName, false, false);
            playerData.NetworkPlayerId = identity.PlayerId;
            
            if (!_connectedPlayers.ContainsKey(clientId))
            {
                _connectedPlayers[clientId] = new List<NetworkPlayerData>();
            }
            _connectedPlayers[clientId].Add(playerData);
            
            NetworkLogger.Server("Added player '{0}' to connected players (total groups: {1})", 
                identity.DisplayName, _connectedPlayers.Count);
            
            // Notify that client needs state sync
            OnClientNeedsStateSync?.Invoke(connection, playerData);
            
            // Fire player joined event
            OnPlayerJoined?.Invoke(playerData);
        }

        /// <summary>
        /// Add a host player to the connected players (for non-dedicated servers).
        /// </summary>
        public void AddHostPlayer(NetworkPlayerData hostPlayerData)
        {
            _connectedPlayers["host"] = new List<NetworkPlayerData> { hostPlayerData };
            NetworkLogger.Info("Added host player '{0}' to connected players", hostPlayerData.PlayerName);
        }

        /// <summary>
        /// Add a local client player (when joining as client).
        /// </summary>
        public void AddLocalPlayer(NetworkPlayerData localPlayerData, string key = "local")
        {
            _connectedPlayers[key] = new List<NetworkPlayerData> { localPlayerData };
            NetworkLogger.Info("Added local player '{0}' to connected players", localPlayerData.PlayerName);
        }

        #endregion

        #region Query Methods

        /// <summary>
        /// Get all connected player names.
        /// </summary>
        public List<string> GetAllPlayerNames()
        {
            var names = new List<string>();
            foreach (var kvp in _connectedPlayers)
            {
                foreach (var player in kvp.Value)
                {
                    if (player != null && !string.IsNullOrEmpty(player.PlayerName))
                    {
                        names.Add(player.PlayerName);
                    }
                }
            }
            return names;
        }

        /// <summary>
        /// Find a player by name.
        /// </summary>
        public NetworkPlayerData FindPlayerByName(string playerName)
        {
            foreach (var kvp in _connectedPlayers)
            {
                foreach (var player in kvp.Value)
                {
                    if (player?.PlayerName == playerName)
                    {
                        return player;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Get a shallow copy of connected players dictionary.
        /// </summary>
        public Dictionary<object, List<NetworkPlayerData>> GetConnectedPlayersCopy()
        {
            return new Dictionary<object, List<NetworkPlayerData>>(_connectedPlayers);
        }

        /// <summary>
        /// Get the mutable connected players dictionary for direct manipulation.
        /// </summary>
        public Dictionary<object, List<NetworkPlayerData>> GetConnectedPlayersMutable()
        {
            return _connectedPlayers;
        }

        /// <summary>
        /// Get the mutable connection map for direct manipulation.
        /// </summary>
        public Dictionary<Guid, INetConnection> GetConnectionMapMutable()
        {
            return _connectionMap;
        }

        #endregion

        #region Cleanup

        /// <summary>
        /// Clear all connection state when leaving lobby.
        /// </summary>
        public void ClearAllConnections(Action<NetworkPlayerData> destroyPlayer = null)
        {
            // Clean up player GameObjects
            foreach (var kvp in _connectedPlayers)
            {
                foreach (var player in kvp.Value)
                {
                    destroyPlayer?.Invoke(player);
                }
            }

            _currentLobby = null;
            _connectedPlayers.Clear();
            _pendingConnections.Clear();
            _connectionMap.Clear();
            _isJoinInProgress = false;
            _isConnected = false;
            _isHosting = false;

            NetworkLogger.Info("Cleared all connection state");
            OnLobbyLeft?.Invoke();
        }

        public void Dispose()
        {
            ClearAllConnections();
        }

        #endregion
    }
}
