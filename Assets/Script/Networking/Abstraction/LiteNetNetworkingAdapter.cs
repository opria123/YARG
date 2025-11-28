using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using YARG.Core.Song;
using YARG.Multiplayer;
using YARG.Net.Runtime;
using YARG.Net.Transport;
using YARG.Net.Sessions;
using YARG.Net.Serialization;
using YARG.Net.Packets.Dispatch;

namespace YARG.Networking.Abstraction
{
    /// <summary>
    /// Adapter that uses YARG.Networking (LiteNetLib) to implement INetworkingService.
    /// This is the experimental networking implementation that will eventually replace Mirror.
    /// </summary>
    public class LiteNetNetworkingAdapter : INetworkingService
    {
        private IClientRuntime _clientRuntime;
        private IServerRuntime _serverRuntime;
        private INetTransport _transport;
        private LobbyStateManager _lobbyStateManager;
        private SessionManager _sessionManager;
        private IPacketDispatcher _packetDispatcher;
        private INetSerializer _serializer;

        private LobbyInfo _currentLobby;
        private string _playerName = "Player";
        private bool _isHosting;
        private bool _isDedicatedServer;
        private bool _isConnected;
        private bool _isJoinInProgress;
        private int _maxPlayers = 32;
        private int _maxLocalPlayersPerClient = 4;
        private int _defaultPort = 7777;

        // Player tracking
        private Dictionary<object, List<NetworkPlayerData>> _connectedPlayers = new();

        #region Properties

        public int MaxPlayers => _maxPlayers;

        public int MaxLocalPlayersPerClient => _maxLocalPlayersPerClient;

        public LobbyInfo CurrentLobby => _currentLobby;

        public string PlayerName => _playerName;

        public bool IsHosting => _isHosting;

        public bool IsDedicatedServer => _isDedicatedServer;

        public bool IsNetworkActive => _isHosting || _isConnected;

        public bool IsConnected => _isConnected;

        public bool IsJoinInProgress => _isJoinInProgress;

        public int DefaultPort => _defaultPort;

        #endregion

        #region Events

        public event Action<LobbyInfo> OnLobbyCreated;
        public event Action<LobbyInfo> OnLobbyJoined;
        public event Action OnLobbyLeft;
        public event Action<List<LobbyInfo>> OnLobbyListUpdated;
        public event Action<NetworkPlayerData> OnPlayerJoined;
        public event Action<NetworkPlayerData> OnPlayerLeft;
        public event Action<string> OnNetworkError;

        #endregion

        #region Initialization

        public void Initialize()
        {
            Debug.Log("[LiteNetNetworkingAdapter] Initializing YARG.Networking (LiteNetLib implementation)");

            try
            {
                // Create the transport layer (LiteNetLib)
                _transport = new LiteNetLibTransport();

                // Create serializer
                _serializer = new NewtonsoftNetSerializer();

                // Create packet dispatcher
                _packetDispatcher = new PacketDispatcher(_serializer);

                // Create session manager
                _sessionManager = new SessionManager();

                // Create client and server runtimes
                _clientRuntime = new DefaultClientRuntime();
                _serverRuntime = new DefaultServerRuntime();

                // Register transport with runtimes
                _clientRuntime.RegisterTransport(_transport);
                _clientRuntime.RegisterPacketDispatcher(_packetDispatcher);

                // Subscribe to client events
                _clientRuntime.Connected += OnClientConnected;
                _clientRuntime.Disconnected += OnClientDisconnected;
                _clientRuntime.HandshakeCompleted += OnClientHandshakeCompleted;

                Debug.Log("[LiteNetNetworkingAdapter] Successfully initialized YARG.Networking");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiteNetNetworkingAdapter] Failed to initialize: {ex.Message}");
                OnNetworkError?.Invoke($"Initialization failed: {ex.Message}");
            }
        }

        public void Shutdown()
        {
            Debug.Log("[LiteNetNetworkingAdapter] Shutting down");

            try
            {
                // Unsubscribe from events
                if (_clientRuntime != null)
                {
                    _clientRuntime.Connected -= OnClientConnected;
                    _clientRuntime.Disconnected -= OnClientDisconnected;
                    _clientRuntime.HandshakeCompleted -= OnClientHandshakeCompleted;
                }

                // Stop transport
                _transport?.Stop();

                // Clear state
                _isHosting = false;
                _isConnected = false;
                _isJoinInProgress = false;
                _currentLobby = null;
                _connectedPlayers.Clear();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiteNetNetworkingAdapter] Error during shutdown: {ex.Message}");
            }
        }

        public void Update()
        {
            // LiteNetLib polling happens in the transport layer
            // The DefaultClientRuntime and DefaultServerRuntime handle their own update loops
        }

        #endregion

        #region Event Handlers

        private void OnClientConnected(object sender, ClientConnectedEventArgs e)
        {
            Debug.Log($"[LiteNetNetworkingAdapter] Connected to server: {e.Connection?.EndPoint}");
            _isConnected = true;
            _isJoinInProgress = false;
        }

        private void OnClientDisconnected(object sender, ClientDisconnectedEventArgs e)
        {
            Debug.Log($"[LiteNetNetworkingAdapter] Disconnected from server. Reason: {e.Reason}");
            _isConnected = false;
            _isJoinInProgress = false;

            OnLobbyLeft?.Invoke();
        }

        private void OnClientHandshakeCompleted(object sender, ClientHandshakeCompletedEventArgs e)
        {
            Debug.Log($"[LiteNetNetworkingAdapter] Handshake completed with session ID: {e.SessionId}");

            // Handshake is complete, we can now consider ourselves joined
            if (_currentLobby != null)
            {
                OnLobbyJoined?.Invoke(_currentLobby);
            }
        }

        #endregion

        #region Lobby Management

        public LobbyInfo CreateLobby(string lobbyName, int maxPlayers, LobbyPrivacyMode privacyMode, string password = "")
        {
            Debug.Log($"[LiteNetNetworkingAdapter] Creating lobby: {lobbyName} (max: {maxPlayers}, privacy: {privacyMode})");

            try
            {
                // Create lobby configuration
                var config = new LobbyConfiguration
                {
                    MaxPlayers = maxPlayers,
                    LobbyName = lobbyName
                };

                // Create lobby state manager
                _lobbyStateManager = new LobbyStateManager(_sessionManager, config);

                // Start server runtime
                var serverOptions = new ServerRuntimeOptions
                {
                    Transport = _transport,
                    Port = _defaultPort,
                    Address = "0.0.0.0",
                    EnableNatPunchThrough = true
                };

                _serverRuntime.Configure(serverOptions);
                _ = _serverRuntime.StartAsync();

                // Create lobby info
                _currentLobby = new LobbyInfo
                {
                    LobbyId = _lobbyStateManager.LobbyId.ToString(),
                    LobbyName = lobbyName,
                    HostName = _playerName,
                    CurrentPlayers = 0,
                    MaxPlayers = maxPlayers,
                    PrivacyMode = privacyMode,
                    HasPassword = !string.IsNullOrEmpty(password),
                    Password = password,
                    IsActive = true,
                    IpAddress = "127.0.0.1",
                    Port = _defaultPort,
                    PublicPort = _defaultPort,
                    PublicAddress = string.Empty,
                    TransportId = "LiteNetLib",
                    PlayerNames = new[] { _playerName },
                    PlayerInstruments = new int[0]
                };

                _isHosting = true;
                _maxPlayers = maxPlayers;

                OnLobbyCreated?.Invoke(_currentLobby);

                Debug.Log($"[LiteNetNetworkingAdapter] Lobby created successfully: {_currentLobby.LobbyId}");
                return _currentLobby;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiteNetNetworkingAdapter] Failed to create lobby: {ex.Message}");
                OnNetworkError?.Invoke($"Failed to create lobby: {ex.Message}");
                return null;
            }
        }

        public void JoinLobby(string endpoint, string password = "")
        {
            Debug.Log($"[LiteNetNetworkingAdapter] Joining lobby at: {endpoint}");

            try
            {
                _isJoinInProgress = true;

                // Parse endpoint (format: "IP:Port")
                var parts = endpoint.Split(':');
                if (parts.Length != 2 || !int.TryParse(parts[1], out int port))
                {
                    throw new ArgumentException($"Invalid endpoint format: {endpoint}. Expected IP:Port");
                }

                string address = parts[0];

                // Create temporary lobby info for the join attempt
                _currentLobby = new LobbyInfo
                {
                    LobbyId = Guid.NewGuid().ToString(),
                    LobbyName = "Unknown",
                    HostName = "Unknown",
                    CurrentPlayers = 0,
                    MaxPlayers = _maxPlayers,
                    PrivacyMode = LobbyPrivacyMode.Public,
                    HasPassword = !string.IsNullOrEmpty(password),
                    Password = password,
                    IsActive = true,
                    IpAddress = address,
                    Port = port,
                    PublicPort = port,
                    PublicAddress = address,
                    TransportId = "LiteNetLib"
                };

                // Connect to server asynchronously
                _ = ConnectToServerAsync(address, port);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiteNetNetworkingAdapter] Failed to join lobby: {ex.Message}");
                _isJoinInProgress = false;
                OnNetworkError?.Invoke($"Failed to join lobby: {ex.Message}");
            }
        }

        private async Task ConnectToServerAsync(string address, int port)
        {
            try
            {
                await _clientRuntime.ConnectAsync(address, port);
                Debug.Log($"[LiteNetNetworkingAdapter] Successfully connected to {address}:{port}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiteNetNetworkingAdapter] Connection failed: {ex.Message}");
                _isJoinInProgress = false;
                OnNetworkError?.Invoke($"Connection failed: {ex.Message}");
            }
        }

        public void JoinDiscoveredLobby(LobbyInfo lobby, string password = "")
        {
            Debug.Log($"[LiteNetNetworkingAdapter] Joining discovered lobby: {lobby.LobbyName}");
            
            string endpoint = $"{lobby.IpAddress}:{lobby.Port}";
            JoinLobby(endpoint, password);
        }

        public void LeaveLobby()
        {
            Debug.Log("[LiteNetNetworkingAdapter] Leaving lobby");

            try
            {
                if (_isHosting)
                {
                    // Stop server
                    _ = _serverRuntime.StopAsync();
                    _isHosting = false;
                }
                else if (_isConnected)
                {
                    // Disconnect from server
                    _clientRuntime.Disconnect();
                }

                _currentLobby = null;
                _connectedPlayers.Clear();

                OnLobbyLeft?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiteNetNetworkingAdapter] Error leaving lobby: {ex.Message}");
                OnNetworkError?.Invoke($"Error leaving lobby: {ex.Message}");
            }
        }

        public async Task<LobbyInfo?> ProbeLobby(string address, int port)
        {
            Debug.Log($"[LiteNetNetworkingAdapter] Probing lobby at {address}:{port}");

            try
            {
                // TODO: Implement lobby probing
                // This would involve sending a lightweight query packet to get lobby info
                // without fully joining the lobby
                Debug.LogWarning("[LiteNetNetworkingAdapter] ProbeLobby not yet fully implemented for LiteNet");
                
                await Task.Delay(100); // Simulate network delay
                
                return new LobbyInfo
                {
                    LobbyId = Guid.NewGuid().ToString(),
                    LobbyName = "LiteNet Lobby",
                    HostName = "Unknown",
                    CurrentPlayers = 0,
                    MaxPlayers = 32,
                    PrivacyMode = LobbyPrivacyMode.Public,
                    HasPassword = false,
                    IsActive = true,
                    IpAddress = address,
                    Port = port,
                    PublicPort = port,
                    PublicAddress = address,
                    TransportId = "LiteNetLib"
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiteNetNetworkingAdapter] Failed to probe lobby: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Player Management

        public void SetPlayerName(string name)
        {
            Debug.Log($"[LiteNetNetworkingAdapter] Setting player name to: {name}");
            _playerName = name;
        }

        public Dictionary<object, List<NetworkPlayerData>> GetConnectedPlayers()
        {
            return new Dictionary<object, List<NetworkPlayerData>>(_connectedPlayers);
        }

        #endregion

        #region Song Selection and Gameplay

        public void StartSongSelection()
        {
            Debug.Log("[LiteNetNetworkingAdapter] Starting song selection");

            try
            {
                // TODO: Implement song selection start
                // This would trigger the lobby state change to song selection mode
                Debug.LogWarning("[LiteNetNetworkingAdapter] StartSongSelection not yet fully implemented");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiteNetNetworkingAdapter] Failed to start song selection: {ex.Message}");
                OnNetworkError?.Invoke($"Failed to start song selection: {ex.Message}");
            }
        }

        public void StartMultiplayerSong(SongEntry song)
        {
            Debug.Log($"[LiteNetNetworkingAdapter] Starting multiplayer song: {song.Name}");

            try
            {
                // TODO: Implement song start
                // This would send the selected song to all clients
                Debug.LogWarning("[LiteNetNetworkingAdapter] StartMultiplayerSong not yet fully implemented");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiteNetNetworkingAdapter] Failed to start multiplayer song: {ex.Message}");
                OnNetworkError?.Invoke($"Failed to start multiplayer song: {ex.Message}");
            }
        }

        public void StartMultiplayerGameplay()
        {
            Debug.Log("[LiteNetNetworkingAdapter] Starting multiplayer gameplay");

            try
            {
                // TODO: Implement gameplay start
                // This would trigger the countdown and gameplay start for all clients
                Debug.LogWarning("[LiteNetNetworkingAdapter] StartMultiplayerGameplay not yet fully implemented");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiteNetNetworkingAdapter] Failed to start multiplayer gameplay: {ex.Message}");
                OnNetworkError?.Invoke($"Failed to start multiplayer gameplay: {ex.Message}");
            }
        }

        #endregion
    }
}
