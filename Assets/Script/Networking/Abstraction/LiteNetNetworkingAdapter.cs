using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using LiteNetLib;
using UnityEngine;
using YARG.Core.Song;
using YARG.Menu;
using YARG.Menu.MusicLibrary;
using YARG.Multiplayer;
using YARG.Net;
using YARG.Net.Packets;
using YARG.Net.Runtime;
using YARG.Net.Transport;
using YARG.Net.Sessions;
using YARG.Net.Serialization;
using YARG.Net.Utilities;
using YARG.Net.Packets;
using YARG.Net.Packets.Dispatch;
using YARG.Net.Handlers.Client;
using YARG.Player;
using YARG.Song;

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
        private LiteNetLibTransport _liteNetTransport; // Typed reference for discovery
        private LiteNetDiscovery _discovery;
        private LobbyStateManager _lobbyStateManager;
        private SessionManager _sessionManager;
        private IPacketDispatcher _packetDispatcher;
        private INetSerializer _serializer;
        private ServerPacketSender _packetSender;
        
        // YARG.Net managers (shared logic for client/server/dedicated server)
        private SetlistManager _setlistManager;
        private SharedSongLibraryManager _sharedSongLibraryManager;
        private LobbyAuthenticator _lobbyAuthenticator;
        private UnisonCoordinator _unisonCoordinator;
        private ScoreResultsManager _scoreResultsManager;

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
        
        // Pending connections waiting for name handshake
        private Dictionary<Guid, INetConnection> _pendingConnections = new();
        
        // Connection ID to connection mapping for latency updates
        private Dictionary<Guid, INetConnection> _connectionMap = new();
        
        // Lobby state tracking (host only)
        private bool _isBrowsingSongs = false;
        
        // Public endpoint resolution (STUN)
        private PublicEndpointResolver _publicEndpointResolver;
        
        // Song Library Sync (Client-side only - server uses SharedSongLibraryManager)
        private bool _songLibraryUploaded = false;
        private int _lastUploadedSongVersion = -1;
        
        // Password for joining (client-side)
        private string _pendingPassword = null;

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
        
        public int DiscoveryPort => _discovery?.DiscoveryPort ?? _defaultPort;
        
        /// <summary>
        /// Whether the host is currently in the song browsing state.
        /// Clients can use this to know if they should navigate to the music library.
        /// </summary>
        public bool IsBrowsingSongs => _isBrowsingSongs;
        
        /// <summary>
        /// Gets the current setlist song hashes.
        /// </summary>
        public IReadOnlyList<string> SetlistSongHashes => _setlistManager?.SongHashes ?? Array.Empty<string>();

        #endregion

        #region Events

        public event Action<LobbyInfo> OnLobbyCreated;
        public event Action<LobbyInfo> OnLobbyJoined;
        public event Action OnLobbyLeft;
        public event Action<List<LobbyInfo>> OnLobbyListUpdated;
        public event Action<NetworkPlayerData> OnPlayerJoined;
        public event Action<NetworkPlayerData> OnPlayerLeft;
        public event Action<string> OnNetworkError;
        
        /// <summary>
        /// Fired when the lobby state changes (e.g., host starts/stops browsing songs).
        /// Parameter indicates whether the host is now browsing songs.
        /// </summary>
        public event Action<bool> OnBrowsingStateChanged;
        
        /// <summary>
        /// Fired when the setlist is updated (song added/removed/synced).
        /// </summary>
        public event Action OnSetlistUpdated;
        
        /// <summary>
        /// Fired when a song is added to the setlist.
        /// Parameters: playerName, songName, songArtist
        /// </summary>
        public event Action<string, string, string> OnSetlistSongAdded;
        
        /// <summary>
        /// Fired when a song is removed from the setlist.
        /// Parameters: playerName, songName, songArtist
        /// </summary>
        public event Action<string, string, string> OnSetlistSongRemoved;
        
        /// <summary>
        /// Fired when the host starts the show.
        /// </summary>
        public event Action OnShowStarted;
        
        /// <summary>
        /// Fired when a player's ready state changes.
        /// Parameters: playerName, isReady
        /// </summary>
        public event Action<string, bool> OnPlayerReadyStateChanged;
        
        /// <summary>
        /// Fired when all players are ready (host only).
        /// </summary>
        public event Action OnAllPlayersReady;
        
        /// <summary>
        /// Fired when gameplay should start (broadcast by host).
        /// </summary>
        public event Action OnStartGameplay;
        
        /// <summary>
        /// Fired when score screen results are received from a remote player.
        /// Parameters: playerName, isHighScore, isFullCombo, score, maxCombo, notesHit, notesMissed
        /// </summary>
        public event Action<string, bool, bool, int, int, int, int> OnScoreResultsReceived;
        
        /// <summary>
        /// Fired when a unison bonus should be awarded (received from host).
        /// Parameter: phraseTime - the time of the unison phrase
        /// </summary>
        public event Action<double> OnUnisonBonusAwarded;
        
        /// <summary>
        /// Fired when the host requests a gameplay restart.
        /// Clients should restart the current song.
        /// </summary>
        public event Action OnRestartGameplayRequested;
        
        /// <summary>
        /// Fired when a player leaves during gameplay.
        /// Parameter: playerName - the name of the player who left
        /// </summary>
        public event Action<string> OnPlayerLeftDuringGameplay;
        
        /// <summary>
        /// Fired when the host quits gameplay and all players should return to music library.
        /// </summary>
        public event Action OnQuitToLibraryRequested;
        
        /// <summary>
        /// Fired when shared song sync state changes.
        /// Parameter: true if sync is complete, false if still syncing.
        /// </summary>
        public event Action<bool> OnSharedSongSyncStateChanged;
        
        /// <summary>
        /// Fired when authentication fails.
        /// Parameter: error message describing the failure.
        /// </summary>
        public event Action<string> OnAuthenticationFailed;

        #endregion

        #region Initialization

        public void Initialize()
        {
            Debug.Log("[LiteNetNetworkingAdapter] Initializing YARG.Networking (LiteNetLib implementation)");

            try
            {
                // Create the transport layer (LiteNetLib)
                _liteNetTransport = new LiteNetLibTransport();
                _transport = _liteNetTransport;
                
                // Create discovery system
                _discovery = new LiteNetDiscovery();
                _discovery.SetDiscoveryPort(_defaultPort);
                _discovery.OnLobbyDiscovered += HandleLobbyDiscovered;
                _discovery.OnLobbyLost += HandleLobbyLost;
                
                // Hook transport's unconnected messages for discovery
                _liteNetTransport.OnUnconnectedMessage += HandleUnconnectedMessage;

                // Create serializer
                _serializer = new NewtonsoftNetSerializer();

                // Create packet dispatcher
                _packetDispatcher = new PacketDispatcher(_serializer);

                // Create session manager
                _sessionManager = new SessionManager();

                // Create client and server runtimes
                _clientRuntime = new DefaultClientRuntime();
                _serverRuntime = new DefaultServerRuntime();
                
                // Create YARG.Net managers (shared logic for client/server/dedicated server)
                _setlistManager = new SetlistManager();
                _sharedSongLibraryManager = new SharedSongLibraryManager();
                _lobbyAuthenticator = new LobbyAuthenticator();
                _unisonCoordinator = new UnisonCoordinator();
                _scoreResultsManager = new ScoreResultsManager();
                
                // Wire up manager events to adapter events
                _setlistManager.SongAdded += OnSetlistManagerSongAdded;
                _setlistManager.SongRemoved += OnSetlistManagerSongRemoved;
                _setlistManager.SetlistCleared += OnSetlistManagerCleared;
                _sharedSongLibraryManager.SyncStateChanged += OnSharedSongSyncStateChanged;
                _unisonCoordinator.UnisonBonusAwarded += OnUnisonCoordinatorBonusAwarded;
                _scoreResultsManager.ResultReceived += OnScoreResultsManagerResultReceived;

                // Register transport with runtimes
                _clientRuntime.RegisterTransport(_transport);

                // Subscribe to client events
                _clientRuntime.Connected += OnClientConnected;
                _clientRuntime.Disconnected += OnClientDisconnected;
                _clientRuntime.HandshakeCompleted += OnClientHandshakeCompleted;
                
                // Subscribe to transport events for server-side peer tracking
                _transport.OnPeerConnected += OnTransportPeerConnected;
                _transport.OnPeerDisconnected += OnTransportPeerDisconnected;
                _transport.OnPayloadReceived += OnTransportPayloadReceived;
                
                // Subscribe to latency updates for ping tracking
                _liteNetTransport.OnLatencyUpdate += OnTransportLatencyUpdate;

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
                // Cancel any pending STUN resolution
                CancelPublicEndpointResolution();
                
                // Unsubscribe from events
                if (_clientRuntime != null)
                {
                    _clientRuntime.Connected -= OnClientConnected;
                    _clientRuntime.Disconnected -= OnClientDisconnected;
                    _clientRuntime.HandshakeCompleted -= OnClientHandshakeCompleted;
                }
                
                if (_transport != null)
                {
                    _transport.OnPeerConnected -= OnTransportPeerConnected;
                    _transport.OnPeerDisconnected -= OnTransportPeerDisconnected;
                    _transport.OnPayloadReceived -= OnTransportPayloadReceived;
                }
                
                // Unsubscribe from latency updates
                if (_liteNetTransport != null)
                {
                    _liteNetTransport.OnLatencyUpdate -= OnTransportLatencyUpdate;
                    _liteNetTransport.OnUnconnectedMessage -= HandleUnconnectedMessage;
                }
                
                // Unsubscribe from manager events
                if (_setlistManager != null)
                {
                    _setlistManager.SongAdded -= OnSetlistManagerSongAdded;
                    _setlistManager.SongRemoved -= OnSetlistManagerSongRemoved;
                    _setlistManager.SetlistCleared -= OnSetlistManagerCleared;
                }
                if (_sharedSongLibraryManager != null)
                {
                    _sharedSongLibraryManager.SyncStateChanged -= OnSharedSongSyncStateChanged;
                }
                if (_unisonCoordinator != null)
                {
                    _unisonCoordinator.UnisonBonusAwarded -= OnUnisonCoordinatorBonusAwarded;
                }
                if (_scoreResultsManager != null)
                {
                    _scoreResultsManager.ResultReceived -= OnScoreResultsManagerResultReceived;
                }
                
                if (_discovery != null)
                {
                    _discovery.OnLobbyDiscovered -= HandleLobbyDiscovered;
                    _discovery.OnLobbyLost -= HandleLobbyLost;
                    _discovery.Dispose();
                    _discovery = null;
                }
                
                // Clean up public endpoint resolver
                if (_publicEndpointResolver != null)
                {
                    _publicEndpointResolver.EndpointResolved -= OnPublicEndpointResolved;
                    _publicEndpointResolver.ResolutionFailed -= OnPublicEndpointFailed;
                    _publicEndpointResolver.Dispose();
                    _publicEndpointResolver = null;
                }

                // Stop transport
                _transport?.Shutdown();

                // Clear state
                _isHosting = false;
                _isConnected = false;
                _isJoinInProgress = false;
                _currentLobby = null;
                _connectedPlayers.Clear();
                
                // Reset managers
                _setlistManager?.Clear();
                _sharedSongLibraryManager?.Clear();
                _lobbyAuthenticator?.Clear();
                _unisonCoordinator?.Reset();
                _scoreResultsManager?.Clear();
                
                _pendingPassword = null;
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
            // Skip this handler when we're hosting - the transport fires this for all peer connections
            // but we only want to handle it when we're a client connecting to a server
            if (_isHosting)
            {
                Debug.Log($"[LiteNetNetworkingAdapter] Ignoring client Connected event while hosting (peer: {e.Connection?.EndPoint})");
                return;
            }
            
            Debug.Log($"[LiteNetNetworkingAdapter] Connected to server: {e.Connection?.EndPoint}");
            _isConnected = true;
            _isJoinInProgress = false;
            
            // For now, treat connection as "joined" since we don't have a handshake protocol yet
            // TODO: Implement proper handshake where server sends lobby info to client
            if (_currentLobby != null)
            {
                // Dispatch to main thread for GameObject creation
                UnityMainThreadDispatcher.EnqueueAction(() =>
                {
                    _connectedPlayers.Clear();
                    
                    // Add the host player first (we know there's a host from the lobby info)
                    // Use the lobby's HostName if available, otherwise use a placeholder
                    string hostName = !string.IsNullOrEmpty(_currentLobby.HostName) ? _currentLobby.HostName : "Host";
                    var hostPlayerData = CreateLiteNetPlayerData(hostName, isHost: true, isLocal: false);
                    _connectedPlayers["host"] = new List<NetworkPlayerData> { hostPlayerData };
                    Debug.Log($"[LiteNetNetworkingAdapter] Added host player '{hostName}' to connected players");
                    
                    // Add local player (the client connecting)
                    string localPlayerName = GetPlayerNameFromProfile();
                    var localPlayerData = CreateLiteNetPlayerData(localPlayerName, isHost: false, isLocal: true);
                    _connectedPlayers["local"] = new List<NetworkPlayerData> { localPlayerData };
                    Debug.Log($"[LiteNetNetworkingAdapter] Added local client player '{localPlayerName}' to connected players");
                    
                    Debug.Log($"[LiteNetNetworkingAdapter] Client connected, firing OnLobbyJoined for lobby: {_currentLobby.LobbyName}");
                    OnLobbyJoined?.Invoke(_currentLobby);
                    
                    // Upload song library after joining
                    UploadSongLibrary();
                });
            }
        }

        private void OnClientDisconnected(object sender, ClientDisconnectedEventArgs e)
        {
            // Skip this handler when we're hosting - we only care about client disconnects when we ARE a client
            if (_isHosting)
            {
                Debug.Log($"[LiteNetNetworkingAdapter] Ignoring client Disconnected event while hosting (connection: {e.Connection?.Id})");
                return;
            }
            
            Debug.Log($"[LiteNetNetworkingAdapter] Disconnected from server. Connection: {e.Connection.Id}");
            _isConnected = false;
            _isJoinInProgress = false;
            
            // Stop the client runtime to ensure transport is properly shutdown
            // This allows reconnection attempts to work
            try
            {
                _clientRuntime.DisconnectAsync().Wait(TimeSpan.FromMilliseconds(500));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LiteNetNetworkingAdapter] Error stopping client runtime: {ex.Message}");
            }
            
            // Clean up player GameObjects on main thread
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                foreach (var kvp in _connectedPlayers)
                {
                    foreach (var player in kvp.Value)
                    {
                        if (player != null && player.gameObject != null)
                        {
                            GameObject.Destroy(player.gameObject);
                        }
                    }
                }
                _connectedPlayers.Clear();
                _connectionMap.Clear();
                _currentLobby = null;
                
                OnLobbyLeft?.Invoke();
            });
        }

        private void OnClientHandshakeCompleted(object sender, ClientHandshakeCompletedEventArgs e)
        {
            Debug.Log($"[LiteNetNetworkingAdapter] Handshake completed with session ID: {e.SessionId}");

            // Handshake is complete, we can now consider ourselves joined
            if (_currentLobby != null)
            {
                // Dispatch to main thread since this is called from LiteNetLib's network thread
                var lobby = _currentLobby;
                UnityMainThreadDispatcher.EnqueueAction(() => OnLobbyJoined?.Invoke(lobby));
            }
        }
        
        private void OnTransportPeerConnected(INetConnection connection)
        {
            // Store connection for latency updates (both client and server)
            _connectionMap[connection.Id] = connection;
            
            // Only handle this when we're hosting (server mode)
            if (!_isHosting)
            {
                // Client side: check if we need to authenticate
                bool needsAuth = _currentLobby != null && _currentLobby.HasPassword;
                
                if (needsAuth)
                {
                    // Send authentication request first
                    SendAuthRequest(connection);
                }
                else
                {
                    // No password required - send player identity directly
                    SendPlayerIdentityToServer(connection);
                }
                return;
            }
            
            Debug.Log($"[LiteNetNetworkingAdapter] Server: Client connected - {connection.EndPoint}");
            
            // Store connection as pending - we'll create player data when we receive their name
            _pendingConnections[connection.Id] = connection;
            Debug.Log($"[LiteNetNetworkingAdapter] Server: Stored pending connection {connection.Id}, waiting for player name");
            
            // Update player count
            if (_currentLobby != null)
            {
                _currentLobby.CurrentPlayers++;
                Debug.Log($"[LiteNetNetworkingAdapter] Server: Player count is now {_currentLobby.CurrentPlayers}");
            }
        }
        
        private void SendPlayerIdentityToServer(INetConnection connection)
        {
            // Dispatch to main thread since we need Unity APIs for profile access
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                // Get or create player identity
                var identity = GetLocalPlayerIdentity();
                Debug.Log($"[LiteNetNetworkingAdapter] Client: Sending player identity '{identity.DisplayName}' (ID: {identity.PlayerId:N}) to server");
                
                try
                {
                    byte[] message = HandshakeBinaryPackets.BuildRequestPacket(identity);
                    connection.Send(message, ChannelType.ReliableOrdered);
                    Debug.Log($"[LiteNetNetworkingAdapter] Client: Sent {message.Length} bytes to server");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[LiteNetNetworkingAdapter] Client: Failed to send player identity: {ex.Message}");
                }
            });
        }
        
        private void OnTransportPayloadReceived(INetConnection connection, ReadOnlyMemory<byte> payload, ChannelType channel)
        {
            if (payload.Length < 1) return;
            
            var packetType = (PacketType)payload.Span[0];
            
            switch (packetType)
            {
                case PacketType.HandshakeRequest when _isHosting:
                    HandlePlayerIdentityMessage(connection, payload);
                    break;
                case PacketType.HostDisconnect when !_isHosting:
                    HandleHostDisconnectMessage();
                    break;
                case PacketType.NavigateToMenu when !_isHosting:
                    HandleNavigateToMenuMessage(payload);
                    break;
                case PacketType.LobbyState when !_isHosting:
                    HandleLobbyStateMessage(payload);
                    break;
                case PacketType.SetlistAdd:
                    HandleSetlistAddMessage(connection, payload);
                    break;
                case PacketType.SetlistRemove:
                    HandleSetlistRemoveMessage(connection, payload);
                    break;
                case PacketType.SetlistSync when !_isHosting:
                    HandleSetlistSyncMessage(payload);
                    break;
                case PacketType.SetlistStart when !_isHosting:
                    HandleSetlistStartMessage(payload);
                    break;
                case PacketType.LobbyReadyState:
                    HandlePlayerReadyMessage(connection, payload);
                    break;
                case PacketType.AllPlayersReady when !_isHosting:
                    HandleAllPlayersReadyMessage();
                    break;
                case PacketType.GameplayStart when !_isHosting:
                    HandleStartGameplayMessage();
                    break;
                case PacketType.GameplayState:
                    HandleGameplaySnapshotMessage(connection, payload);
                    break;
                case PacketType.ScoreScreenAdvance when !_isHosting:
                    HandleScoreScreenAdvanceMessage(payload);
                    break;
                case PacketType.ScoreResults:
                    HandleScoreResultsMessage(connection, payload);
                    break;
                case PacketType.UnisonPhraseHit:
                    HandleUnisonPhraseHitMessage(connection, payload);
                    break;
                case PacketType.UnisonBonusAward when !_isHosting:
                    HandleUnisonBonusAwardMessage(payload);
                    break;
                case PacketType.GameplayRestart when !_isHosting:
                    HandleRestartGameplayMessage();
                    break;
                case PacketType.PlayerLeftGameplay when !_isHosting:
                    HandlePlayerLeftGameplayMessage(payload);
                    break;
                case PacketType.QuitToLibrary when !_isHosting:
                    HandleQuitToLibraryMessage();
                    break;
                case PacketType.SongLibraryChunk when _isHosting:
                    HandleSongLibraryChunkMessage(connection, payload);
                    break;
                case PacketType.SharedSongsChunk when !_isHosting:
                    HandleSharedSongsChunkMessage(payload);
                    break;
                case PacketType.ClearSharedSongs when !_isHosting:
                    HandleClearSharedSongsMessage();
                    break;
                case PacketType.AuthRequest when _isHosting:
                    HandleAuthRequestMessage(connection, payload);
                    break;
                case PacketType.AuthResponse when !_isHosting:
                    HandleAuthResponseMessage(connection, payload);
                    break;
            }
        }
        
        private void HandlePlayerIdentityMessage(INetConnection connection, ReadOnlyMemory<byte> payload)
        {
            if (!HandshakeBinaryPackets.TryParseRequestPacket(payload.Span, out NetworkPlayerIdentity identity))
            {
                Debug.LogWarning($"[LiteNetNetworkingAdapter] Server: Invalid player identity message");
                return;
            }
            
            try
            {
                Debug.Log($"[LiteNetNetworkingAdapter] Server: Received player '{identity.DisplayName}' (ID: {identity.PlayerId:N}) from {connection.EndPoint}");
                
                // Remove from pending connections
                _pendingConnections.Remove(connection.Id);
                
                // Create player data on main thread
                string clientId = connection.Id.ToString();
                var adapter = this;
                var clientConnection = connection; // Capture for closure
                var playerIdentity = identity; // Capture for closure
                
                UnityMainThreadDispatcher.EnqueueAction(() =>
                {
                    Debug.Log($"[LiteNetNetworkingAdapter] Server: Creating player data for '{playerIdentity.DisplayName}'");
                    
                    var playerData = adapter.CreateLiteNetPlayerData(playerIdentity.DisplayName, isHost: false, isLocal: false);
                    
                    // Store the player ID in the player data for later lookup
                    playerData.NetworkPlayerId = playerIdentity.PlayerId;
                    
                    if (!adapter._connectedPlayers.ContainsKey(clientId))
                    {
                        adapter._connectedPlayers[clientId] = new List<NetworkPlayerData>();
                    }
                    adapter._connectedPlayers[clientId].Add(playerData);
                    
                    // Update PlayerNames in current lobby for discovery responses
                    adapter.UpdateLobbyPlayerNames();
                    
                    Debug.Log($"[LiteNetNetworkingAdapter] Server: Added player '{playerIdentity.DisplayName}' to connected players (total groups: {adapter._connectedPlayers.Count})");
                    
                    // Send current lobby state to the new client
                    adapter.SendLobbyStateToClient(clientConnection);
                    
                    // Send current setlist to the new client
                    adapter.SendSetlistSyncToClient(clientConnection);
                    
                    // Fire player joined event
                    adapter.OnPlayerJoined?.Invoke(playerData);
                });
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiteNetNetworkingAdapter] Server: Failed to parse player identity: {ex.Message}");
            }
        }
        
        private void HandleHostDisconnectMessage()
        {
            Debug.Log($"[LiteNetNetworkingAdapter] Client: Received host disconnect notification");
            
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                _isConnected = false;
                _currentLobby = null;
                _connectedPlayers.Clear();
                _isBrowsingSongs = false;
                
                OnLobbyLeft?.Invoke();
            });
        }
        
        private void HandleLobbyStateMessage(ReadOnlyMemory<byte> payload)
        {
            if (!NavigationBinaryPackets.TryParseLobbyStatePacket(payload.Span, out bool isBrowsing))
            {
                Debug.LogWarning("[LiteNetNetworkingAdapter] Client: Invalid lobby state message");
                return;
            }
            
            Debug.Log($"[LiteNetNetworkingAdapter] Client: Received lobby state, browsing={isBrowsing}");
            
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                if (_isBrowsingSongs != isBrowsing)
                {
                    _isBrowsingSongs = isBrowsing;
                    OnBrowsingStateChanged?.Invoke(isBrowsing);
                }
            });
        }
        
        private void HandleNavigateToMenuMessage(ReadOnlyMemory<byte> payload)
        {
            if (!NavigationBinaryPackets.TryParseNavigatePacket(payload.Span, out var menuTarget))
            {
                Debug.LogWarning("[LiteNetNetworkingAdapter] Client: Invalid navigate message");
                return;
            }
            
            Debug.Log($"[LiteNetNetworkingAdapter] Client: Received navigate to menu command, target={menuTarget}");
            
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                switch (menuTarget)
                {
                    case MenuTarget.MusicLibrary:
                        Debug.Log("[LiteNetNetworkingAdapter] Client: Navigating to Music Library");
                        // Clear the setlist when navigating back to music library
                        if (_setlistManager.Count > 0)
                        {
                            _setlistManager.Clear();
                            OnSetlistUpdated?.Invoke();
                            Debug.Log("[LiteNetNetworkingAdapter] Client: Cleared setlist when navigating to music library");
                        }
                        // Also clear the global show state
                        GlobalVariables.State.PlayingAShow = false;
                        GlobalVariables.State.ShowSongs?.Clear();
                        GlobalVariables.State.ShowIndex = 0;
                        
                        // Ensure we're not in practice mode when in multiplayer
                        GlobalVariables.State.IsPractice = false;
                        
                        // Reset library mode to QuickPlay for multiplayer
                        MusicLibraryMenu.LibraryMode = MusicLibraryMode.QuickPlay;
                        
                        MenuManager.Instance.PushMenu(MenuManager.Menu.MusicLibrary);
                        break;
                    case MenuTarget.LobbyRoom:
                        Debug.Log("[LiteNetNetworkingAdapter] Client: Navigating to Lobby Room");
                        MenuManager.Instance.PushMenu(MenuManager.Menu.LobbyRoom);
                        break;
                    default:
                        Debug.LogWarning($"[LiteNetNetworkingAdapter] Client: Unknown menu target {menuTarget}");
                        break;
                }
            });
        }
        
        /// <summary>
        /// Broadcasts a navigation command to all connected clients.
        /// Only works when hosting.
        /// </summary>
        public void BroadcastNavigateToMenu(MenuTarget menuTarget)
        {
            if (!_isHosting)
            {
                Debug.LogWarning("[LiteNetNetworkingAdapter] Cannot broadcast navigation - not hosting");
                return;
            }
            
            if (_transport == null)
            {
                Debug.LogWarning("[LiteNetNetworkingAdapter] Cannot broadcast navigation - transport is null");
                return;
            }
            
            byte[] message = NavigationBinaryPackets.BuildNavigatePacket(menuTarget);
            Debug.Log($"[LiteNetNetworkingAdapter] Host: Broadcasting navigate to menu {menuTarget} to all clients");
            BroadcastPacketToClients(message, $"navigation to {menuTarget}");
        }
        
        /// <summary>
        /// Broadcasts a command to navigate all clients to the Music Library.
        /// Also sets the browsing state to true and clears the setlist.
        /// </summary>
        public void BroadcastNavigateToMusicLibrary()
        {
            // Clear the setlist when navigating back to music library (e.g., host backing out of gameplay)
            if (_setlistManager.Count > 0)
            {
                _setlistManager.Clear();
                OnSetlistUpdated?.Invoke();
                Debug.Log("[LiteNetNetworkingAdapter] Cleared setlist when navigating to music library");
            }
            
            // Also clear the global show state
            GlobalVariables.State.PlayingAShow = false;
            GlobalVariables.State.ShowSongs?.Clear();
            GlobalVariables.State.ShowIndex = 0;
            
            // Ensure we're not in practice mode when in multiplayer
            GlobalVariables.State.IsPractice = false;
            
            // Reset library mode to QuickPlay for multiplayer
            MusicLibraryMenu.LibraryMode = MusicLibraryMode.QuickPlay;
            
            SetBrowsingState(true);
            BroadcastNavigateToMenu(MenuTarget.MusicLibrary);
        }
        
        /// <summary>
        /// Broadcasts a command to navigate all clients to the Lobby Room.
        /// Also sets the browsing state to false.
        /// </summary>
        public void BroadcastNavigateToLobbyRoom()
        {
            SetBrowsingState(false);
            BroadcastNavigateToMenu(MenuTarget.LobbyRoom);
        }
        
        /// <summary>
        /// Broadcasts a restart gameplay command to all clients.
        /// The host should call this when they want all players to restart the current song.
        /// </summary>
        public void BroadcastRestartGameplay()
        {
            if (!_isHosting)
            {
                Debug.LogWarning("[LiteNetNetworkingAdapter] Only host can broadcast restart gameplay");
                return;
            }
            
            byte[] message = GameplayBinaryPackets.BuildRestartPacket();
            Debug.Log("[LiteNetNetworkingAdapter] Host: Broadcasting restart gameplay to all clients");
            BroadcastPacketToClients(message, "restart gameplay");
        }
        
        /// <summary>
        /// Broadcasts to all clients that a player has left during gameplay.
        /// </summary>
        public void BroadcastPlayerLeftGameplay(string playerName)
        {
            if (!_isHosting)
            {
                Debug.LogWarning("[LiteNetNetworkingAdapter] Only host can broadcast player left");
                return;
            }
            
            byte[] message = GameplayBinaryPackets.BuildPlayerLeftPacket(playerName);
            Debug.Log($"[LiteNetNetworkingAdapter] Host: Broadcasting player '{playerName}' left gameplay to all clients");
            BroadcastPacketToClients(message, "player left gameplay");
        }
        
        /// <summary>
        /// Broadcasts a quit to library command to all clients.
        /// This is used when the host exits gameplay and all players should return to music library.
        /// </summary>
        public void BroadcastQuitToLibrary()
        {
            if (!_isHosting)
            {
                Debug.LogWarning("[LiteNetNetworkingAdapter] Only host can broadcast quit to library");
                return;
            }
            
            byte[] message = GameplayBinaryPackets.BuildQuitToLibraryPacket();
            Debug.Log("[LiteNetNetworkingAdapter] Host: Broadcasting quit to library to all clients");
            BroadcastPacketToClients(message, "quit to library");
        }
        
        /// <summary>
        /// Broadcasts a pre-built packet to all connected clients.
        /// </summary>
        /// <param name="packet">The packet data to send.</param>
        /// <param name="description">Description for logging purposes.</param>
        /// <param name="channel">The channel type to use.</param>
        private void BroadcastPacketToClients(byte[] packet, string description, ChannelType channel = ChannelType.ReliableOrdered)
        {
            foreach (var kvp in _connectionMap)
            {
                try
                {
                    kvp.Value.Send(packet, channel);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[LiteNetNetworkingAdapter] Host: Failed to send {description} to {kvp.Key}: {ex.Message}");
                }
            }
        }
        
        /// <summary>
        /// Broadcasts a pre-built packet to all connected clients except one.
        /// </summary>
        /// <param name="packet">The packet data to send.</param>
        /// <param name="excludeConnectionId">The connection ID to exclude.</param>
        /// <param name="description">Description for logging purposes.</param>
        /// <param name="channel">The channel type to use.</param>
        private void BroadcastPacketToClientsExcept(byte[] packet, Guid excludeConnectionId, string description, ChannelType channel = ChannelType.ReliableOrdered)
        {
            foreach (var kvp in _connectionMap)
            {
                if (kvp.Key == excludeConnectionId)
                    continue;
                    
                try
                {
                    kvp.Value.Send(packet, channel);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[LiteNetNetworkingAdapter] Host: Failed to send {description} to {kvp.Key}: {ex.Message}");
                }
            }
        }
        
        /// <summary>
        /// Handles the restart gameplay message from host.
        /// </summary>
        private void HandleRestartGameplayMessage()
        {
            Debug.Log("[LiteNetNetworkingAdapter] Client: Received restart gameplay command from host");
            
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                OnRestartGameplayRequested?.Invoke();
            });
        }
        
        /// <summary>
        /// Handles the player left gameplay message from host.
        /// </summary>
        private void HandlePlayerLeftGameplayMessage(ReadOnlyMemory<byte> payload)
        {
            if (!GameplayBinaryPackets.TryParsePlayerLeftPacket(payload.Span, out string playerName))
            {
                Debug.LogWarning("[LiteNetNetworkingAdapter] Client: Invalid player left message");
                return;
            }
            
            Debug.Log($"[LiteNetNetworkingAdapter] Client: Received player '{playerName}' left gameplay");
            
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                OnPlayerLeftDuringGameplay?.Invoke(playerName);
            });
        }
        
        /// <summary>
        /// Handles the quit to library message from host.
        /// </summary>
        private void HandleQuitToLibraryMessage()
        {
            Debug.Log("[LiteNetNetworkingAdapter] Client: Received quit to library command from host");
            
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                OnQuitToLibraryRequested?.Invoke();
            });
        }
        
        #region Song Library Sync
        
        /// <summary>
        /// Handles song library chunk from a client (server-side).
        /// </summary>
        private void HandleSongLibraryChunkMessage(INetConnection connection, ReadOnlyMemory<byte> payload)
        {
            if (!_isHosting || payload.Length < 4)
            {
                Debug.LogWarning("[LiteNetNetworkingAdapter] Server: Invalid song library chunk (too short)");
                return;
            }
            
            try
            {
                var span = payload.Span;
                bool isFirstChunk = span[1] != 0;
                bool isFinalChunk = span[2] != 0;
                int dataLength = (span[3] << 8) | span[4];
                
                if (payload.Length < 5 + dataLength)
                {
                    Debug.LogWarning($"[LiteNetNetworkingAdapter] Server: Song library chunk truncated (expected {dataLength} bytes)");
                    return;
                }
                
                var connectionId = connection.Id;
                
                // Initialize or reset library for this player on first chunk
                if (isFirstChunk || !_playerSongLibraries.ContainsKey(connectionId))
                {
                    _playerSongLibraries[connectionId] = new HashSet<HashWrapper>();
                    _playersPendingSongSync.Add(connectionId);
                    UpdateSharedSongSyncState();
                    Debug.Log($"[LiteNetNetworkingAdapter] Server: Started receiving song library from {connectionId}");
                }
                
                var library = _playerSongLibraries[connectionId];
                
                // Parse hashes from payload
                int hashSize = HashWrapper.HASH_SIZE_IN_BYTES;
                int offset = 5;
                int processedHashes = 0;
                
                while (offset + hashSize <= 5 + dataLength)
                {
                    var hash = HashWrapper.Create(span.Slice(offset, hashSize));
                    library.Add(hash);
                    offset += hashSize;
                    processedHashes++;
                }
                
                Debug.Log($"[LiteNetNetworkingAdapter] Server: Processed {processedHashes} hashes from {connectionId} (total: {library.Count})");
                
                if (isFinalChunk)
                {
                    _playersPendingSongSync.Remove(connectionId);
                    Debug.Log($"[LiteNetNetworkingAdapter] Server: Completed song library sync for {connectionId} ({library.Count} songs)");
                    RecalculateSharedSongs();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiteNetNetworkingAdapter] Server: Error processing song library chunk: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Handles shared songs chunk from server (client-side).
        /// </summary>
        private void HandleSharedSongsChunkMessage(ReadOnlyMemory<byte> payload)
        {
            if (payload.Length < 4)
            {
                Debug.LogWarning("[LiteNetNetworkingAdapter] Client: Invalid shared songs chunk (too short)");
                return;
            }
            
            try
            {
                var span = payload.Span;
                bool isFirstChunk = span[1] != 0;
                bool isFinalChunk = span[2] != 0;
                int dataLength = (span[3] << 8) | span[4];
                
                if (isFirstChunk)
                {
                    MultiplayerSongFilter.BeginSharedSongsUpload();
                }
                
                if (dataLength > 0 && payload.Length >= 5 + dataLength)
                {
                    byte[] chunk = span.Slice(5, dataLength).ToArray();
                    MultiplayerSongFilter.AppendSharedSongsChunk(chunk);
                }
                
                if (isFinalChunk)
                {
                    MultiplayerSongFilter.CommitSharedSongsUpload();
                    Debug.Log("[LiteNetNetworkingAdapter] Client: Shared songs sync complete");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiteNetNetworkingAdapter] Client: Error processing shared songs chunk: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Handles clear shared songs message from server (client-side).
        /// </summary>
        private void HandleClearSharedSongsMessage()
        {
            Debug.Log("[LiteNetNetworkingAdapter] Client: Received clear shared songs command");
            MultiplayerSongFilter.ClearSharedSongs();
        }
        
        /// <summary>
        /// Recalculates the intersection of all player song libraries and broadcasts to clients.
        /// </summary>
        private void RecalculateSharedSongs()
        {
            if (!_isHosting)
                return;
            
            // Wait until all players have finished uploading
            if (_playersPendingSongSync.Count > 0)
            {
                Debug.Log($"[LiteNetNetworkingAdapter] Server: Skipping shared song calculation ({_playersPendingSongSync.Count} players still syncing)");
                return;
            }
            
            int playerCount = _playerSongLibraries.Count;
            
            if (playerCount == 0)
            {
                _sharedSongHashes = null;
                BroadcastClearSharedSongs();
                UpdateSharedSongSyncState();
                Debug.Log("[LiteNetNetworkingAdapter] Server: No player libraries, clearing shared songs");
                return;
            }
            
            // Compute intersection
            HashSet<HashWrapper> intersection = null;
            foreach (var library in _playerSongLibraries.Values)
            {
                if (intersection == null)
                {
                    intersection = new HashSet<HashWrapper>(library);
                }
                else
                {
                    intersection.IntersectWith(library);
                }
                
                // Early exit if no common songs
                if (intersection.Count == 0)
                    break;
            }
            
            _sharedSongHashes = intersection ?? new HashSet<HashWrapper>();
            
            Debug.Log($"[LiteNetNetworkingAdapter] Server: Computed shared songs intersection for {playerCount} players: {_sharedSongHashes.Count} songs");
            
            BroadcastSharedSongs();
            UpdateSharedSongSyncState();
        }
        
        /// <summary>
        /// Broadcasts the shared songs to all clients.
        /// </summary>
        private void BroadcastSharedSongs()
        {
            if (!_isHosting || _transport == null)
                return;
            
            if (_sharedSongHashes == null || _sharedSongHashes.Count == 0)
            {
                BroadcastClearSharedSongs();
                return;
            }
            
            // Build chunks
            var chunks = BuildSharedSongChunks(_sharedSongHashes);
            
            Debug.Log($"[LiteNetNetworkingAdapter] Server: Broadcasting {_sharedSongHashes.Count} shared songs in {chunks.Count} chunks");
            
            foreach (var kvp in _connectionMap)
            {
                try
                {
                    bool isFirst = true;
                    for (int i = 0; i < chunks.Count; i++)
                    {
                        bool isFinal = i == chunks.Count - 1;
                        SendSharedSongsChunk(kvp.Value, chunks[i], isFirst, isFinal);
                        isFirst = false;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[LiteNetNetworkingAdapter] Server: Failed to send shared songs to {kvp.Key}: {ex.Message}");
                }
            }
            
            // Also update local filter for host
            MultiplayerSongFilter.SetSharedSongs(_sharedSongHashes);
        }
        
        private void SendSharedSongsChunk(INetConnection connection, byte[] hashData, bool isFirstChunk, bool isFinalChunk)
        {
            // Format: [PacketType][isFirst][isFinal][length (2 bytes)][data]
            int dataLen = hashData?.Length ?? 0;
            byte[] message = new byte[5 + dataLen];
            message[0] = (byte)PacketType.SharedSongsChunk;
            message[1] = (byte)(isFirstChunk ? 1 : 0);
            message[2] = (byte)(isFinalChunk ? 1 : 0);
            message[3] = (byte)(dataLen >> 8);
            message[4] = (byte)(dataLen & 0xFF);
            
            if (dataLen > 0)
                hashData.CopyTo(message, 5);
            
            connection.Send(message, ChannelType.ReliableOrdered);
        }
        
        private void BroadcastClearSharedSongs()
        {
            if (!_isHosting || _transport == null)
                return;
            
            byte[] message = new byte[] { (byte)PacketType.ClearSharedSongs };
            BroadcastPacketToClients(message, "clear shared songs");
            
            // Also clear local filter for host
            MultiplayerSongFilter.ClearSharedSongs();
        }
        
        private List<byte[]> BuildSharedSongChunks(HashSet<HashWrapper> hashes)
        {
            int hashSize = HashWrapper.HASH_SIZE_IN_BYTES;
            int hashesPerChunk = Math.Max(1, MAX_SHARED_SONG_CHUNK_BYTES / hashSize);
            
            if (hashes.Count == 0)
            {
                return new List<byte[]> { Array.Empty<byte>() };
            }
            
            var hashArray = hashes.ToArray();
            var chunks = new List<byte[]>();
            int index = 0;
            
            while (index < hashArray.Length)
            {
                int chunkCount = Math.Min(hashesPerChunk, hashArray.Length - index);
                using var stream = new System.IO.MemoryStream(chunkCount * hashSize);
                for (int i = 0; i < chunkCount; i++)
                {
                    hashArray[index + i].Serialize(stream);
                }
                
                chunks.Add(stream.ToArray());
                index += chunkCount;
            }
            
            return chunks;
        }
        
        private void UpdateSharedSongSyncState()
        {
            bool isComplete = _playersPendingSongSync.Count == 0;
            
            if (isComplete != _sharedSongSyncComplete)
            {
                _sharedSongSyncComplete = isComplete;
                OnSharedSongSyncStateChanged?.Invoke(isComplete);
            }
        }
        
        /// <summary>
        /// Uploads the local song library to the server.
        /// Called automatically when connecting to a lobby.
        /// </summary>
        public void UploadSongLibrary()
        {
            if (_isHosting)
            {
                // Host uploads to self
                UploadSongLibraryAsHost();
                return;
            }
            
            if (!_isConnected || _transport == null)
            {
                Debug.LogWarning("[LiteNetNetworkingAdapter] Cannot upload song library - not connected");
                return;
            }
            
            int currentVersion = SongContainer.RefreshVersion;
            if (_lastUploadedSongVersion == currentVersion && _songLibraryUploaded)
            {
                Debug.Log("[LiteNetNetworkingAdapter] Song library already uploaded for this version");
                return;
            }
            
            var hashList = SongContainer.SongHashes;
            int totalSongs = hashList.Count;
            
            Debug.Log($"[LiteNetNetworkingAdapter] Client: Uploading song library ({totalSongs} songs)");
            
            // Find server connection
            INetConnection serverConnection = null;
            foreach (var kvp in _connectionMap)
            {
                serverConnection = kvp.Value;
                break;
            }
            
            if (serverConnection == null)
            {
                Debug.LogWarning("[LiteNetNetworkingAdapter] No server connection found for song library upload");
                return;
            }
            
            SendSongLibraryChunks(serverConnection, hashList);
            
            _lastUploadedSongVersion = currentVersion;
            _songLibraryUploaded = true;
        }
        
        private void UploadSongLibraryAsHost()
        {
            // Host registers its own song library directly
            var hostId = Guid.Empty; // Use empty GUID to represent host
            var hashList = SongContainer.SongHashes;
            
            _playerSongLibraries[hostId] = new HashSet<HashWrapper>(hashList);
            
            Debug.Log($"[LiteNetNetworkingAdapter] Host: Registered own song library ({hashList.Count} songs)");
            
            RecalculateSharedSongs();
        }
        
        private void SendSongLibraryChunks(INetConnection connection, IReadOnlyList<HashWrapper> hashList)
        {
            int hashSize = HashWrapper.HASH_SIZE_IN_BYTES;
            int totalSongs = hashList.Count;
            
            if (totalSongs == 0)
            {
                // Send empty chunk
                SendSongLibraryChunk(connection, Array.Empty<byte>(), true, true);
                return;
            }
            
            // Build all hash bytes
            byte[] allHashes = new byte[totalSongs * hashSize];
            for (int i = 0; i < totalSongs; i++)
            {
                var hashSpan = System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                    System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref System.Runtime.CompilerServices.Unsafe.AsRef(in hashList[i]), 1));
                hashSpan.Slice(0, hashSize).CopyTo(allHashes.AsSpan(i * hashSize, hashSize));
            }
            
            // Send in chunks
            int offset = 0;
            int chunkIndex = 0;
            int bytesPerChunk = SONG_HASHES_PER_CHUNK * hashSize;
            
            while (offset < allHashes.Length)
            {
                int remaining = allHashes.Length - offset;
                int chunkSize = Math.Min(bytesPerChunk, remaining);
                
                byte[] chunk = new byte[chunkSize];
                Array.Copy(allHashes, offset, chunk, 0, chunkSize);
                
                bool isFirst = chunkIndex == 0;
                bool isFinal = offset + chunkSize >= allHashes.Length;
                
                SendSongLibraryChunk(connection, chunk, isFirst, isFinal);
                
                offset += chunkSize;
                chunkIndex++;
            }
            
            Debug.Log($"[LiteNetNetworkingAdapter] Client: Sent {chunkIndex} song library chunks ({totalSongs} songs)");
        }
        
        private void SendSongLibraryChunk(INetConnection connection, byte[] hashData, bool isFirstChunk, bool isFinalChunk)
        {
            // Format: [PacketType][isFirst][isFinal][length (2 bytes)][data]
            int dataLen = hashData?.Length ?? 0;
            byte[] message = new byte[5 + dataLen];
            message[0] = (byte)PacketType.SongLibraryChunk;
            message[1] = (byte)(isFirstChunk ? 1 : 0);
            message[2] = (byte)(isFinalChunk ? 1 : 0);
            message[3] = (byte)(dataLen >> 8);
            message[4] = (byte)(dataLen & 0xFF);
            
            if (dataLen > 0)
                hashData.CopyTo(message, 5);
            
            connection.Send(message, ChannelType.ReliableOrdered);
        }
        
        private void RemoveSongLibraryForPlayer(Guid connectionId)
        {
            _playerSongLibraries.Remove(connectionId);
            _playersPendingSongSync.Remove(connectionId);
            UpdateSharedSongSyncState();
        }
        
        private void ResetSharedSongState()
        {
            _playerSongLibraries.Clear();
            _playersPendingSongSync.Clear();
            _sharedSongHashes = null;
            _songLibraryUploaded = false;
            _lastUploadedSongVersion = -1;
            MultiplayerSongFilter.ClearSharedSongs();
            UpdateSharedSongSyncState();
        }
        
        #endregion
        
        #region Password Authentication
        
        /// <summary>
        /// Sets the lobby password. Set to null or empty for no password.
        /// </summary>
        public void SetLobbyPassword(string password)
        {
            _lobbyPassword = string.IsNullOrEmpty(password) ? null : password;
            
            // Update authenticator
            _lobbyAuthenticator.SetPassword(_lobbyPassword);
            
            if (_currentLobby != null)
            {
                _currentLobby.HasPassword = !string.IsNullOrEmpty(_lobbyPassword);
            }
        }
        
        /// <summary>
        /// Sets the password to use when joining a lobby.
        /// </summary>
        public void SetJoinPassword(string password)
        {
            _pendingPassword = password;
        }
        
        /// <summary>
        /// Handles authentication request from client (server-side).
        /// </summary>
        private void HandleAuthRequestMessage(INetConnection connection, ReadOnlyMemory<byte> payload)
        {
            if (!_isHosting)
                return;
            
            try
            {
                string providedPassword = "";
                if (!AuthBinaryPackets.TryParseRequestPacket(payload.Span, out providedPassword))
                {
                    providedPassword = "";
                }
                
                // Update authenticator with current lobby state
                if (_currentLobby != null)
                {
                    _lobbyAuthenticator.SetCapacity(_currentLobby.MaxPlayers, _currentLobby.CurrentPlayers);
                }
                
                // Process via manager
                var result = _lobbyAuthenticator.ProcessAuthRequest(connection.Id, providedPassword);
                bool success = result == AuthResult.Success;
                
                Debug.Log($"[LiteNetNetworkingAdapter] Server: Auth {(success ? "success" : "failed")} for {connection.EndPoint} - {result}");
                
                // Send response using AuthBinaryPackets
                byte[] response = AuthBinaryPackets.BuildResponsePacket(success);
                connection.Send(response, ChannelType.ReliableOrdered);
                
                // Disconnect if auth failed
                if (!success)
                {
                    // Give time for the response to be sent before disconnecting
                    Task.Delay(100).ContinueWith(_ =>
                    {
                        try
                        {
                            connection.Disconnect();
                        }
                        catch { }
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiteNetNetworkingAdapter] Server: Error handling auth request: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Handles authentication response from server (client-side).
        /// </summary>
        private void HandleAuthResponseMessage(INetConnection connection, ReadOnlyMemory<byte> payload)
        {
            if (_isHosting)
                return;
            
            if (!AuthBinaryPackets.TryParseResponsePacket(payload.Span, out bool success))
            {
                Debug.LogWarning("[LiteNetNetworkingAdapter] Client: Invalid auth response message");
                return;
            }
            
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                if (success)
                {
                    Debug.Log("[LiteNetNetworkingAdapter] Client: Authentication successful");
                    // Continue with normal connection flow - send player identity
                    SendPlayerIdentityToServer(connection);
                }
                else
                {
                    Debug.Log("[LiteNetNetworkingAdapter] Client: Authentication failed");
                    OnAuthenticationFailed?.Invoke("Authentication failed");
                    OnNetworkError?.Invoke("Authentication failed");
                }
            });
        }
        
        /// <summary>
        /// Sends authentication request to server when connecting to a password-protected lobby.
        /// </summary>
        private void SendAuthRequest(INetConnection connection)
        {
            string password = _pendingPassword ?? "";
            
            byte[] message = AuthBinaryPackets.BuildRequestPacket(password);
            
            connection.Send(message, ChannelType.ReliableOrdered);
            Debug.Log("[LiteNetNetworkingAdapter] Client: Sent authentication request");
        }
        
        #endregion

        /// <summary>
        /// Sets the browsing state and broadcasts it to all clients.
        /// </summary>
        public void SetBrowsingState(bool isBrowsing)
        {
            if (!_isHosting)
            {
                // Client can update local state from server message
                if (_isBrowsingSongs != isBrowsing)
                {
                    _isBrowsingSongs = isBrowsing;
                    Debug.Log($"[LiteNetNetworkingAdapter] Client: Browsing state updated to {isBrowsing}");
                    OnBrowsingStateChanged?.Invoke(isBrowsing);
                }
                return;
            }
            
            _isBrowsingSongs = isBrowsing;
            Debug.Log($"[LiteNetNetworkingAdapter] Host: Setting browsing state to {isBrowsing}");
            
            // Broadcast state to all clients
            BroadcastLobbyState();
            
            OnBrowsingStateChanged?.Invoke(isBrowsing);
        }
        
        /// <summary>
        /// Broadcasts the current lobby state to all connected clients.
        /// </summary>
        private void BroadcastLobbyState()
        {
            if (!_isHosting || _transport == null)
                return;
            
            byte[] message = NavigationBinaryPackets.BuildLobbyStatePacket(_isBrowsingSongs);
            Debug.Log($"[LiteNetNetworkingAdapter] Host: Broadcasting lobby state (browsing={_isBrowsingSongs}) to all clients");
            BroadcastPacketToClients(message, "lobby state");
        }
        
        /// <summary>
        /// Sends the current lobby state to a specific client connection.
        /// </summary>
        private void SendLobbyStateToClient(INetConnection connection)
        {
            if (!_isHosting)
                return;
            
            byte[] message = NavigationBinaryPackets.BuildLobbyStatePacket(_isBrowsingSongs);
            
            Debug.Log($"[LiteNetNetworkingAdapter] Host: Sending lobby state (browsing={_isBrowsingSongs}) to new client {connection.EndPoint}");
            
            try
            {
                connection.Send(message, ChannelType.ReliableOrdered);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiteNetNetworkingAdapter] Host: Failed to send lobby state to new client: {ex.Message}");
            }
        }
        
        #region Setlist Management
        
        /// <summary>
        /// Checks if a song is in the setlist.
        /// </summary>
        public bool IsInSetlist(string songHash)
        {
            return _setlistManager?.Contains(songHash) ?? false;
        }
        
        /// <summary>
        /// Requests to add a song to the setlist. Works for both host and client.
        /// </summary>
        public void RequestAddToSetlist(string songHash, string playerName, string songName, string songArtist)
        {
            Debug.Log($"[LiteNetNetworkingAdapter] RequestAddToSetlist: {songHash} from {playerName}");
            
            if (_isHosting)
            {
                // Host adds directly using manager and broadcasts
                AddToSetlistLocal(songHash, playerName, songName, songArtist);
            }
            else
            {
                // Client sends request to host
                SendSetlistAddRequest(songHash, playerName, songName, songArtist);
            }
        }
        
        /// <summary>
        /// Requests to remove a song from the setlist. Works for both host and client.
        /// </summary>
        public void RequestRemoveFromSetlist(string songHash, string playerName, string songName, string songArtist)
        {
            Debug.Log($"[LiteNetNetworkingAdapter] RequestRemoveFromSetlist: {songHash} from {playerName}");
            
            if (_isHosting)
            {
                // Host removes directly using manager and broadcasts
                RemoveFromSetlistLocal(songHash, playerName, songName, songArtist);
            }
            else
            {
                // Client sends request to host
                SendSetlistRemoveRequest(songHash, playerName, songName, songArtist);
            }
        }
        
        /// <summary>
        /// Host only: Starts the show with the current setlist.
        /// </summary>
        public void StartShow()
        {
            if (!_isHosting)
            {
                Debug.LogWarning("[LiteNetNetworkingAdapter] Only host can start show");
                return;
            }
            
            if (_setlistManager == null || _setlistManager.IsEmpty)
            {
                Debug.LogWarning("[LiteNetNetworkingAdapter] Cannot start show with empty setlist");
                return;
            }
            
            Debug.Log($"[LiteNetNetworkingAdapter] Host: Starting show with {_setlistManager.Count} songs");
            
            // Broadcast start show to all clients
            BroadcastStartShow();
            
            // Fire event locally for host
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                ApplySetlistToGlobalState();
                OnShowStarted?.Invoke();
                
                // Navigate to difficulty select
                MenuManager.Instance.PushMenu(MenuManager.Menu.DifficultySelect);
            });
        }
        
        private void AddToSetlistLocal(string songHash, string playerName, string songName, string songArtist)
        {
            if (_setlistManager == null)
                return;
                
            if (!_setlistManager.TryAdd(songHash, songName, songArtist, playerName, out var entry))
            {
                Debug.Log($"[LiteNetNetworkingAdapter] Song {songHash} already in setlist or setlist full");
                return;
            }
            
            Debug.Log($"[LiteNetNetworkingAdapter] Added {songHash} to setlist (now {_setlistManager.Count} songs)");
            
            // Broadcast to all clients
            if (_isHosting)
            {
                BroadcastSetlistAdd(songHash, playerName, songName, songArtist);
            }
            // Note: OnSetlistSongAdded event is fired by the manager bridge (OnSetlistManagerSongAdded)
        }
        
        private void RemoveFromSetlistLocal(string songHash, string playerName, string songName, string songArtist)
        {
            if (_setlistManager == null)
                return;
                
            if (!_setlistManager.TryRemove(songHash, out var removedEntry))
            {
                Debug.Log($"[LiteNetNetworkingAdapter] Song {songHash} not in setlist");
                return;
            }
            
            Debug.Log($"[LiteNetNetworkingAdapter] Removed {songHash} from setlist (now {_setlistManager.Count} songs)");
            
            // Broadcast to all clients
            if (_isHosting)
            {
                BroadcastSetlistRemove(songHash, playerName, songName, songArtist);
            }
            // Note: OnSetlistSongRemoved event is fired by the manager bridge (OnSetlistManagerSongRemoved)
        }
        
        private void SendSetlistAddRequest(string songHash, string playerName, string songName, string songArtist)
        {
            if (_transport == null || _isHosting)
                return;
            
            var message = SetlistBinaryPackets.BuildAddPacket(songHash, playerName, songName, songArtist);
            
            // Find server connection
            foreach (var kvp in _connectionMap)
            {
                try
                {
                    kvp.Value.Send(message, ChannelType.ReliableOrdered);
                    Debug.Log($"[LiteNetNetworkingAdapter] Client: Sent setlist add request for {songHash}");
                    break;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[LiteNetNetworkingAdapter] Client: Failed to send setlist add: {ex.Message}");
                }
            }
        }
        
        private void SendSetlistRemoveRequest(string songHash, string playerName, string songName, string songArtist)
        {
            if (_transport == null || _isHosting)
                return;
            
            var message = SetlistBinaryPackets.BuildRemovePacket(songHash, playerName, songName, songArtist);
            
            // Find server connection
            foreach (var kvp in _connectionMap)
            {
                try
                {
                    kvp.Value.Send(message, ChannelType.ReliableOrdered);
                    Debug.Log($"[LiteNetNetworkingAdapter] Client: Sent setlist remove request for {songHash}");
                    break;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[LiteNetNetworkingAdapter] Client: Failed to send setlist remove: {ex.Message}");
                }
            }
        }
        
        private void BroadcastSetlistAdd(string songHash, string playerName, string songName, string songArtist)
        {
            if (!_isHosting || _transport == null)
                return;
            
            var message = SetlistBinaryPackets.BuildAddPacket(songHash, playerName, songName, songArtist);
            BroadcastPacketToClients(message, "setlist add");
        }
        
        private void BroadcastSetlistRemove(string songHash, string playerName, string songName, string songArtist)
        {
            if (!_isHosting || _transport == null)
                return;
            
            var message = SetlistBinaryPackets.BuildRemovePacket(songHash, playerName, songName, songArtist);
            BroadcastPacketToClients(message, "setlist remove");
        }
        
        private void BroadcastStartShow()
        {
            if (!_isHosting || _transport == null)
                return;
            
            // Build setlist start packet with song hashes
            var songHashes = _setlistManager.SongHashes.ToList();
            var message = SetlistBinaryPackets.BuildStartPacket(songHashes);
            
            Debug.Log($"[LiteNetNetworkingAdapter] Host: Broadcasting start show with {songHashes.Count} songs");
            BroadcastPacketToClients(message, "start show");
        }
        
        /// <summary>
        /// Sends the full setlist to a specific client (used when client joins).
        /// </summary>
        private void SendSetlistSyncToClient(INetConnection connection)
        {
            if (!_isHosting)
                return;
            
            // Build setlist sync packet with all entries
            var entries = _setlistManager.Entries
                .Select(e => new SetlistEntry(e.SongHash, e.SongName, e.SongArtist, e.AddedByPlayerName))
                .ToList();
            var message = SetlistBinaryPackets.BuildSyncPacket(entries);
            
            Debug.Log($"[LiteNetNetworkingAdapter] Host: Sending setlist sync ({_setlistManager.Count} songs) to {connection.EndPoint}");
            
            try
            {
                connection.Send(message, ChannelType.ReliableOrdered);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiteNetNetworkingAdapter] Host: Failed to send setlist sync: {ex.Message}");
            }
        }
        
        private void HandleSetlistAddMessage(INetConnection connection, ReadOnlyMemory<byte> payload)
        {
            if (!SetlistBinaryPackets.TryParseAddOrRemove(payload.Span, out var entry))
            {
                Debug.LogWarning("[LiteNetNetworkingAdapter] Invalid setlist add message");
                return;
            }
            
            string songHash = entry.SongHash;
            string playerName = entry.PlayerName;
            string songName = entry.SongName;
            string songArtist = entry.ArtistName;
            
            Debug.Log($"[LiteNetNetworkingAdapter] Received setlist add: {songHash} from {playerName}");
            
            if (_isHosting)
            {
                // Host: add and broadcast to all
                AddToSetlistLocal(songHash, playerName, songName, songArtist);
            }
            else
            {
                // Client: just update local
                UnityMainThreadDispatcher.EnqueueAction(() =>
                {
                    if (_setlistManager.TryAdd(songHash, songName, songArtist, playerName, out _))
                    {
                        OnSetlistSongAdded?.Invoke(playerName, songName, songArtist);
                        OnSetlistUpdated?.Invoke();
                    }
                });
            }
        }
        
        private void HandleSetlistRemoveMessage(INetConnection connection, ReadOnlyMemory<byte> payload)
        {
            if (!SetlistBinaryPackets.TryParseAddOrRemove(payload.Span, out var entry))
            {
                Debug.LogWarning("[LiteNetNetworkingAdapter] Invalid setlist remove message");
                return;
            }
            
            string songHash = entry.SongHash;
            string playerName = entry.PlayerName;
            string songName = entry.SongName;
            string songArtist = entry.ArtistName;
            
            Debug.Log($"[LiteNetNetworkingAdapter] Received setlist remove: {songHash} from {playerName}");
            
            if (_isHosting)
            {
                // Host: remove and broadcast to all
                RemoveFromSetlistLocal(songHash, playerName, songName, songArtist);
            }
            else
            {
                // Client: just update local
                UnityMainThreadDispatcher.EnqueueAction(() =>
                {
                    if (_setlistManager.TryRemove(songHash, out _))
                    {
                        OnSetlistSongRemoved?.Invoke(playerName, songName, songArtist);
                        OnSetlistUpdated?.Invoke();
                    }
                });
            }
        }
        
        private void HandleSetlistSyncMessage(ReadOnlyMemory<byte> payload)
        {
            if (!SetlistBinaryPackets.TryParseSyncPacket(payload.Span, out var entries))
            {
                Debug.LogWarning("[LiteNetNetworkingAdapter] Invalid setlist sync message");
                return;
            }
            
            Debug.Log($"[LiteNetNetworkingAdapter] Client: Received setlist sync: {entries.Count} entries");
            
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                _setlistManager.Clear();
                foreach (var entry in entries)
                {
                    _setlistManager.TryAdd(entry.SongHash, entry.SongName, entry.ArtistName, entry.PlayerName, out _);
                }
                Debug.Log($"[LiteNetNetworkingAdapter] Client: Setlist synced with {_setlistManager.Count} songs");
                OnSetlistUpdated?.Invoke();
            });
        }
        
        private void HandleSetlistStartMessage(ReadOnlyMemory<byte> payload)
        {
            if (!SetlistBinaryPackets.TryParseStartPacket(payload.Span, out var songHashes))
            {
                Debug.LogWarning("[LiteNetNetworkingAdapter] Invalid setlist start message");
                return;
            }
            
            Debug.Log($"[LiteNetNetworkingAdapter] Client: Received start show with {songHashes.Count} songs");
            
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                // Update setlist using the song hashes received
                // Note: The client's setlist should already be synced, so we just use what we have
                // The song hashes are used to set up GlobalVariables.State.ShowSongs
                
                ApplySetlistToGlobalState();
                OnShowStarted?.Invoke();
                
                // Navigate to difficulty select
                MenuManager.Instance.PushMenu(MenuManager.Menu.DifficultySelect);
            });
        }
        
        private void ApplySetlistToGlobalState()
        {
            var showSongs = new List<SongEntry>();
            foreach (var hash in _setlistManager.SongHashes)
            {
                var hashWrapper = YARG.Core.Song.HashWrapper.FromString(hash);
                if (SongContainer.SongsByHash.TryGetValue(hashWrapper, out var songList) && songList.Count > 0)
                {
                    showSongs.Add(songList[0]);
                }
            }
            
            GlobalVariables.State.ShowSongs = showSongs;
            GlobalVariables.State.PlayingAShow = true;
            GlobalVariables.State.ShowIndex = 0;
            
            if (showSongs.Count > 0)
            {
                GlobalVariables.State.CurrentSong = showSongs[0];
            }
            
            Debug.Log($"[LiteNetNetworkingAdapter] Applied setlist to GlobalState: {showSongs.Count} songs");
        }
        
        #region Ready State Messages
        
        /// <summary>
        /// Sets the local player's ready state and broadcasts it.
        /// </summary>
        public void SetPlayerReady(bool isReady)
        {
            string playerName = GetPlayerNameFromProfile();
            
            // Get instrument and difficulty from local player
            int instrumentValue = 0;
            int difficultyValue = 0;
            if (PlayerContainer.Players.Count > 0)
            {
                var localPlayer = PlayerContainer.Players[0];
                instrumentValue = (int)localPlayer.Profile.CurrentInstrument;
                difficultyValue = (int)localPlayer.Profile.CurrentDifficulty;
            }
            
            Debug.Log($"[LiteNetNetworkingAdapter] Setting player '{playerName}' ready state to {isReady} (instrument: {instrumentValue}, difficulty: {difficultyValue})");
            
            // Update local player data
            UpdateLocalPlayerReadyState(isReady, instrumentValue, difficultyValue);
            
            if (_isHosting)
            {
                // Host: update directly and broadcast to all clients
                // Use "host" as the source connection key - this tells clients it's NOT their local player
                BroadcastPlayerReadyStateTargeted("host", playerName, isReady, instrumentValue, difficultyValue);
                
                // Fire local event on host
                OnPlayerReadyStateChanged?.Invoke(playerName, isReady);
                
                // Check if all players are ready
                CheckAllPlayersReady();
            }
            else if (_transport != null)
            {
                // Client: send to host
                SendPlayerReadyToHost(playerName, isReady, instrumentValue, difficultyValue);
            }
        }
        
        private void UpdateLocalPlayerReadyState(bool isReady, int instrument = -1, int difficulty = -1)
        {
            // Find local player and update ready state
            foreach (var kvp in _connectedPlayers)
            {
                foreach (var player in kvp.Value)
                {
                    // Find the local player (not host, or host's own player)
                    bool isLocalPlayer = false;
                    if (_isHosting && kvp.Key.ToString() == "host")
                    {
                        isLocalPlayer = true;
                    }
                    else if (!_isHosting && kvp.Key.ToString() == "local")
                    {
                        isLocalPlayer = true;
                    }
                    
                    if (isLocalPlayer)
                    {
                        SetPlayerReadyState(player, isReady);
                        if (instrument >= 0 && difficulty >= 0)
                        {
                            SetPlayerInstrumentAndDifficulty(player, instrument, difficulty);
                        }
                        return;
                    }
                }
            }
        }
        
        private void SetPlayerReadyState(NetworkPlayerData player, bool isReady)
        {
            if (player == null) return;
            
            var isReadyField = typeof(NetworkPlayerData).GetField("isReady", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            if (isReadyField != null)
            {
                isReadyField.SetValue(player, isReady);
                Debug.Log($"[LiteNetNetworkingAdapter] Set player '{player.PlayerName}' isReady to {isReady}");
                
                // Fire the event on the player data
                var eventField = typeof(NetworkPlayerData).GetField("OnReadyStateChangedEvent", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (eventField != null)
                {
                    var eventDelegate = eventField.GetValue(player) as Action<bool>;
                    eventDelegate?.Invoke(isReady);
                }
            }
        }
        
        /// <summary>
        /// Resets all players' ready states to false without firing events.
        /// Used when transitioning to gameplay so ready states don't persist to score screen.
        /// </summary>
        private void ResetAllPlayerReadyStates()
        {
            var isReadyField = typeof(NetworkPlayerData).GetField("isReady", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            if (isReadyField == null)
            {
                Debug.LogWarning("[LiteNetNetworkingAdapter] Could not find isReady field for reset");
                return;
            }
            
            foreach (var kvp in _connectedPlayers)
            {
                foreach (var player in kvp.Value)
                {
                    if (player != null)
                    {
                        isReadyField.SetValue(player, false);
                        Debug.Log($"[LiteNetNetworkingAdapter] Reset ready state for player '{player.PlayerName}'");
                    }
                }
            }
        }
        
        /// <summary>
        /// Public interface method to reset all players' ready states without firing events.
        /// Used when entering score screen to ensure clean state before subscribing to events.
        /// </summary>
        public void ResetAllPlayersReadyState()
        {
            Debug.Log("[LiteNetNetworkingAdapter] ResetAllPlayersReadyState called (public interface method)");
            ResetAllPlayerReadyStates();
        }
        
        /// <summary>
        /// Resets all players' gameplay state (score, combo, etc.) for starting a new song.
        /// </summary>
        private void ResetAllPlayersGameState()
        {
            Debug.Log("[LiteNetNetworkingAdapter] Resetting all players' gameplay state for new song");
            
            foreach (var kvp in _connectedPlayers)
            {
                foreach (var player in kvp.Value)
                {
                    if (player != null)
                    {
                        player.ResetGameState();
                        Debug.Log($"[LiteNetNetworkingAdapter] Reset gameplay state for player '{player.PlayerName}'");
                    }
                }
            }
        }
        
        private void SetPlayerInstrumentAndDifficulty(NetworkPlayerData player, int instrument, int difficulty)
        {
            if (player == null) return;
            
            var instrumentField = typeof(NetworkPlayerData).GetField("instrument", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var difficultyField = typeof(NetworkPlayerData).GetField("difficulty", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            if (instrumentField != null)
            {
                instrumentField.SetValue(player, instrument);
            }
            if (difficultyField != null)
            {
                difficultyField.SetValue(player, difficulty);
            }
            
            Debug.Log($"[LiteNetNetworkingAdapter] Set player '{player.PlayerName}' instrument to {instrument}, difficulty to {difficulty}");
            
            // Fire the events
            var instrumentEventField = typeof(NetworkPlayerData).GetField("OnInstrumentChangedEvent", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (instrumentEventField != null)
            {
                var eventDelegate = instrumentEventField.GetValue(player) as Action<int, int>;
                eventDelegate?.Invoke(instrument, difficulty);
            }
        }
        
        private void SendPlayerReadyToHost(string playerName, bool isReady, int instrument, int difficulty)
        {
            if (_transport == null) return;
            
            // Message format: [PacketType (1 byte)][isReady (1 byte)][nameLen (2 bytes)][name (utf8)][instrument (1 byte)][difficulty (1 byte)]
            byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(playerName);
            byte[] message = new byte[1 + 1 + 2 + nameBytes.Length + 2];
            message[0] = (byte)PacketType.LobbyReadyState;
            message[1] = isReady ? (byte)1 : (byte)0;
            message[2] = (byte)(nameBytes.Length >> 8);
            message[3] = (byte)(nameBytes.Length & 0xFF);
            Array.Copy(nameBytes, 0, message, 4, nameBytes.Length);
            message[4 + nameBytes.Length] = (byte)instrument;
            message[4 + nameBytes.Length + 1] = (byte)difficulty;
            
            // Send to server (first connection in the map when not hosting)
            foreach (var conn in _connectionMap.Values)
            {
                conn.Send(message, ChannelType.ReliableOrdered);
                Debug.Log($"[LiteNetNetworkingAdapter] Client: Sent ready state ({isReady}, instrument: {instrument}, difficulty: {difficulty}) to host");
                break;
            }
        }
        
        private void HandlePlayerReadyMessage(INetConnection connection, ReadOnlyMemory<byte> payload)
        {
            if (payload.Length < 4)
                return;
            
            var span = payload.Span;
            bool isReady = span[1] == 1;
            int nameLen = (span[2] << 8) | span[3];
            
            if (payload.Length < 4 + nameLen)
                return;
            
            string playerName = System.Text.Encoding.UTF8.GetString(span.Slice(4, nameLen));
            
            // Parse instrument and difficulty if present (new format adds 2 bytes after name)
            int instrument = 0;
            int difficulty = 0;
            bool hasInstrumentData = false;
            if (payload.Length >= 4 + nameLen + 2)
            {
                instrument = span[4 + nameLen];
                difficulty = span[4 + nameLen + 1];
                hasInstrumentData = true;
            }
            
            if (_isHosting)
            {
                // Host: update player state using the CONNECTION ID to find the correct player
                // This is important when multiple players have the same name
                string connectionKey = connection.Id.ToString();
                Debug.Log($"[LiteNetNetworkingAdapter] Host: Received ready state for '{playerName}' from connection {connectionKey}: {isReady} (instrument: {instrument}, difficulty: {difficulty})");
                
                UnityMainThreadDispatcher.EnqueueAction(() =>
                {
                    // Find the player by connection ID, not by name
                    NetworkPlayerData targetPlayer = null;
                    
                    if (_connectedPlayers.TryGetValue(connectionKey, out var players))
                    {
                        // Find the player in this connection's player list
                        foreach (var player in players)
                        {
                            if (player.PlayerName == playerName)
                            {
                                targetPlayer = player;
                                break;
                            }
                        }
                        
                        // If no exact name match, just use the first player from this connection
                        if (targetPlayer == null && players.Count > 0)
                        {
                            targetPlayer = players[0];
                            Debug.Log($"[LiteNetNetworkingAdapter] Host: No exact name match for '{playerName}' in connection {connectionKey}, using first player: {targetPlayer.PlayerName}");
                        }
                    }
                    
                    if (targetPlayer != null)
                    {
                        Debug.Log($"[LiteNetNetworkingAdapter] Host: Setting ready state for player '{targetPlayer.PlayerName}' (connection: {connectionKey}) to {isReady}");
                        SetPlayerReadyState(targetPlayer, isReady);
                        
                        // Also update instrument/difficulty if we received that data
                        if (hasInstrumentData)
                        {
                            SetPlayerInstrumentAndDifficulty(targetPlayer, instrument, difficulty);
                        }
                        
                        // Broadcast to all clients - each client needs to know if it's about their local player
                        BroadcastPlayerReadyStateTargeted(connectionKey, targetPlayer.PlayerName, isReady, instrument, difficulty);
                        
                        // Fire local event on host
                        OnPlayerReadyStateChanged?.Invoke(targetPlayer.PlayerName, isReady);
                    }
                    else
                    {
                        Debug.LogWarning($"[LiteNetNetworkingAdapter] Host: Could not find player for connection {connectionKey}");
                    }
                    
                    // Check if all players are ready
                    CheckAllPlayersReady();
                });
            }
            else
            {
                // Client: receiving a broadcast from host about a player's ready state
                // Format with instrument data: [isLocalPlayer (1 byte)][instrument (1 byte)][difficulty (1 byte)]
                // Legacy format: [isLocalPlayer (1 byte)]
                bool isLocalPlayer = false;
                int broadcastInstrument = 0;
                int broadcastDifficulty = 0;
                
                int extraOffset = 4 + nameLen;
                if (payload.Length >= extraOffset + 3)
                {
                    // New format with instrument data
                    isLocalPlayer = span[extraOffset] == 1;
                    broadcastInstrument = span[extraOffset + 1];
                    broadcastDifficulty = span[extraOffset + 2];
                }
                else if (payload.Length >= extraOffset + 1)
                {
                    // Legacy format without instrument data
                    isLocalPlayer = span[extraOffset] == 1;
                }
                
                Debug.Log($"[LiteNetNetworkingAdapter] Client: Received ready state for '{playerName}': {isReady}, isLocalPlayer: {isLocalPlayer}, instrument: {broadcastInstrument}, difficulty: {broadcastDifficulty}");
                
                UnityMainThreadDispatcher.EnqueueAction(() =>
                {
                    // Determine which player entry to update
                    string targetKey = isLocalPlayer ? "local" : "host";
                    
                    if (_connectedPlayers.TryGetValue(targetKey, out var players))
                    {
                        foreach (var player in players)
                        {
                            // Match by name or just take first player if names differ
                            if (player.PlayerName == playerName || players.Count == 1)
                            {
                                Debug.Log($"[LiteNetNetworkingAdapter] Client: Updating ready state for '{player.PlayerName}' (key: {targetKey}) to {isReady}");
                                SetPlayerReadyState(player, isReady);
                                
                                // Also update instrument/difficulty for remote player (host)
                                if (!isLocalPlayer && broadcastInstrument >= 0)
                                {
                                    SetPlayerInstrumentAndDifficulty(player, broadcastInstrument, broadcastDifficulty);
                                }
                                break;
                            }
                        }
                    }
                    else
                    {
                        // Fallback: search all players
                        Debug.Log($"[LiteNetNetworkingAdapter] Client: Key '{targetKey}' not found, searching all players");
                        foreach (var kvp in _connectedPlayers)
                        {
                            foreach (var player in kvp.Value)
                            {
                                if (player.PlayerName == playerName)
                                {
                                    SetPlayerReadyState(player, isReady);
                                    break;
                                }
                            }
                        }
                    }
                    
                    OnPlayerReadyStateChanged?.Invoke(playerName, isReady);
                });
            }
        }
        
        /// <summary>
        /// Broadcasts ready state to all clients, telling each client whether the update is about their local player.
        /// </summary>
        private void BroadcastPlayerReadyStateTargeted(string sourceConnectionKey, string playerName, bool isReady, int instrument = 0, int difficulty = 0)
        {
            if (_transport == null || !_isHosting) return;
            
            byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(playerName);
            
            // Message format: [PacketType (1 byte)][isReady (1 byte)][nameLen (2 bytes)][name (utf8)][isLocalPlayer (1 byte)][instrument (1 byte)][difficulty (1 byte)]
            byte[] message = new byte[1 + 1 + 2 + nameBytes.Length + 3];
            
            message[0] = (byte)PacketType.LobbyReadyState;
            message[1] = isReady ? (byte)1 : (byte)0;
            message[2] = (byte)(nameBytes.Length >> 8);
            message[3] = (byte)(nameBytes.Length & 0xFF);
            Array.Copy(nameBytes, 0, message, 4, nameBytes.Length);
            // isLocalPlayer byte will be set per-client
            message[4 + nameBytes.Length + 1] = (byte)instrument;
            message[4 + nameBytes.Length + 2] = (byte)difficulty;
            
            // Broadcast to all clients, but customize the isLocalPlayer flag for each
            foreach (var kvp in _connectionMap)
            {
                string clientConnKey = kvp.Key.ToString();  // Convert Guid to string for comparison
                var conn = kvp.Value;
                
                // Is this broadcast about this client's local player?
                bool isAboutThisClient = (clientConnKey == sourceConnectionKey);
                message[4 + nameBytes.Length] = isAboutThisClient ? (byte)1 : (byte)0;
                
                conn.Send(message, ChannelType.ReliableOrdered);
                Debug.Log($"[LiteNetNetworkingAdapter] Host: Sent ready state to client {clientConnKey}: player='{playerName}', ready={isReady}, isLocal={isAboutThisClient}, instrument={instrument}, difficulty={difficulty}");
            }
        }
        
        private void HandleAllPlayersReadyMessage()
        {
            Debug.Log("[LiteNetNetworkingAdapter] Client: All players are ready!");
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                OnAllPlayersReady?.Invoke();
            });
        }
        
        private void HandleStartGameplayMessage()
        {
            Debug.Log("[LiteNetNetworkingAdapter] Client: Starting gameplay!");
            
            // Clear cached score results from previous song
            ClearCachedScoreResults();
            
            // Reset all players' ready states (they'll need to ready up again on score screen)
            ResetAllPlayerReadyStates();
            
            // Reset all players' gameplay state (score, combo, etc.) for new song
            ResetAllPlayersGameState();
            
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                // Ensure we're not in practice mode when in multiplayer gameplay
                GlobalVariables.State.IsPractice = false;
                
                OnStartGameplay?.Invoke();
                GlobalVariables.Instance.LoadScene(SceneIndex.Gameplay);
            });
        }
        
        #region Gameplay Snapshot Sync
        
        // Snapshot sequence counter for ordering
        private uint _localSnapshotSequence = 0;
        
        /// <summary>
        /// Sends a gameplay snapshot to all other players (host broadcasts to clients, clients send to host who relays).
        /// </summary>
        public void SendGameplaySnapshot(
            int score, int combo, int streak, bool starPowerActive, float starPowerAmount,
            int starPowerPhrasesHit, int totalStarPowerPhrases,
            int notesHit, int notesMissed,
            int overstrums, int hoposStrummed, int overhits, int ghostInputs,
            int ghostsHit, int accentsHit, int dynamicsBonus, int bandBonusScore,
            int vocalsTicksHit, int vocalsTicksMissed, float vocalsPhraseTicksHit, int vocalsPhraseTicksTotal,
            bool soloActive, int soloSequence, int soloNoteCount, int soloNotesHit, int soloLastBonus, int soloTotalBonus,
            int sustainsHeld, float whammyValue,
            double songTime)
        {
            if (_transport == null || !IsNetworkActive)
            {
                Debug.LogWarning($"[LiteNetNetworkingAdapter] SendGameplaySnapshot skipped - transport={_transport != null}, IsNetworkActive={IsNetworkActive}");
                return;
            }
            
            if (_connectionMap.Count == 0)
            {
                Debug.LogWarning("[LiteNetNetworkingAdapter] SendGameplaySnapshot - no connections in _connectionMap");
                return;
            }
            
            _localSnapshotSequence++;
            
            // Log first snapshot and then periodically
            if (_localSnapshotSequence == 1 || _localSnapshotSequence % 60 == 0)
            {
                Debug.Log($"[LiteNetNetworkingAdapter] SendGameplaySnapshot #{_localSnapshotSequence} - connections={_connectionMap.Count}, score={score}, combo={combo}");
            }
            
            // Build the message
            // Format: [PacketType (1)][Sequence (4)][Score (4)][Combo (4)][Streak (4)][StarPowerActive (1)]
            //         [StarPowerAmount (4)][StarPowerPhrasesHit (4)][TotalStarPowerPhrases (4)]
            //         [NotesHit (4)][NotesMissed (4)][Overstrums (4)][HoposStrummed (4)]
            //         [Overhits (4)][GhostInputs (4)][GhostsHit (4)][AccentsHit (4)][DynamicsBonus (4)]
            //         [BandBonusScore (4)][VocalsTicksHit (4)][VocalsTicksMissed (4)]
            //         [VocalsPhraseTicksHit (4)][VocalsPhraseTicksTotal (4)]
            //         [SoloActive (1)][SoloSequence (4)][SoloNoteCount (4)][SoloNotesHit (4)]
            //         [SoloLastBonus (4)][SoloTotalBonus (4)][SustainsHeld (4)][WhammyValue (4)]
            //         [SongTime (8)][PlayerNameLen (2)][PlayerName (utf8)]
            
            string playerName = GetPlayerNameFromProfile();
            byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(playerName);
            
            // Total size: 1 + 4 + (4*20) + 1 + (4*6) + 4 + 4 + 8 + 1 + 2 + nameBytes.Length = 127 + nameBytes.Length
            byte[] message = new byte[127 + nameBytes.Length];
            int offset = 0;
            
            message[offset++] = (byte)PacketType.GameplayState;
            
            // Sequence
            WriteInt32(message, ref offset, (int)_localSnapshotSequence);
            
            // Score, combo, streak
            WriteInt32(message, ref offset, score);
            WriteInt32(message, ref offset, combo);
            WriteInt32(message, ref offset, streak);
            
            // Star power
            message[offset++] = starPowerActive ? (byte)1 : (byte)0;
            WriteFloat(message, ref offset, starPowerAmount);
            WriteInt32(message, ref offset, starPowerPhrasesHit);
            WriteInt32(message, ref offset, totalStarPowerPhrases);
            
            // Notes
            WriteInt32(message, ref offset, notesHit);
            WriteInt32(message, ref offset, notesMissed);
            
            // Guitar stats
            WriteInt32(message, ref offset, overstrums);
            WriteInt32(message, ref offset, hoposStrummed);
            WriteInt32(message, ref offset, overhits);
            WriteInt32(message, ref offset, ghostInputs);
            
            // Drums stats
            WriteInt32(message, ref offset, ghostsHit);
            WriteInt32(message, ref offset, accentsHit);
            WriteInt32(message, ref offset, dynamicsBonus);
            
            // Band bonus
            WriteInt32(message, ref offset, bandBonusScore);
            
            // Vocals
            WriteInt32(message, ref offset, vocalsTicksHit);
            WriteInt32(message, ref offset, vocalsTicksMissed);
            WriteFloat(message, ref offset, vocalsPhraseTicksHit);
            WriteInt32(message, ref offset, vocalsPhraseTicksTotal);
            
            // Solo
            message[offset++] = soloActive ? (byte)1 : (byte)0;
            WriteInt32(message, ref offset, soloSequence);
            WriteInt32(message, ref offset, soloNoteCount);
            WriteInt32(message, ref offset, soloNotesHit);
            WriteInt32(message, ref offset, soloLastBonus);
            WriteInt32(message, ref offset, soloTotalBonus);
            
            // Sustain and whammy
            WriteInt32(message, ref offset, sustainsHeld);
            WriteFloat(message, ref offset, whammyValue);
            
            // Song time
            WriteDouble(message, ref offset, songTime);
            
            // Player name
            message[offset++] = (byte)(nameBytes.Length >> 8);
            message[offset++] = (byte)(nameBytes.Length & 0xFF);
            Array.Copy(nameBytes, 0, message, offset, nameBytes.Length);
            
            // Send to all connections (unreliable for performance since snapshots are sent frequently)
            int sentCount = 0;
            foreach (var conn in _connectionMap.Values)
            {
                try
                {
                    conn.Send(message, ChannelType.Unreliable);
                    sentCount++;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[LiteNetNetworkingAdapter] Failed to send gameplay snapshot: {ex.Message}");
                }
            }
            
            // Log periodically (every 60 snapshots = ~1 second at 60fps)
            if (_localSnapshotSequence % 60 == 0)
            {
                Debug.Log($"[LiteNetNetworkingAdapter] Sent gameplay snapshot #{_localSnapshotSequence} to {sentCount} connections (score={score}, combo={combo})");
            }
        }
        
        private void HandleGameplaySnapshotMessage(INetConnection connection, ReadOnlyMemory<byte> payload)
        {
            if (payload.Length < 127) // Minimum size without player name (increased from 119 to 127)
            {
                Debug.LogWarning($"[LiteNetNetworkingAdapter] Gameplay snapshot too short: {payload.Length} bytes");
                return;
            }
            
            var span = payload.Span;
            int offset = 1; // Skip message type
            
            // Parse all the fields
            uint sequence = (uint)ReadInt32(span, ref offset);
            int score = ReadInt32(span, ref offset);
            int combo = ReadInt32(span, ref offset);
            int streak = ReadInt32(span, ref offset);
            
            bool starPowerActive = span[offset++] == 1;
            float starPowerAmount = ReadFloat(span, ref offset);
            int starPowerPhrasesHit = ReadInt32(span, ref offset);
            int totalStarPowerPhrases = ReadInt32(span, ref offset);
            
            int notesHit = ReadInt32(span, ref offset);
            int notesMissed = ReadInt32(span, ref offset);
            
            int overstrums = ReadInt32(span, ref offset);
            int hoposStrummed = ReadInt32(span, ref offset);
            int overhits = ReadInt32(span, ref offset);
            int ghostInputs = ReadInt32(span, ref offset);
            
            int ghostsHit = ReadInt32(span, ref offset);
            int accentsHit = ReadInt32(span, ref offset);
            int dynamicsBonus = ReadInt32(span, ref offset);
            
            int bandBonusScore = ReadInt32(span, ref offset);
            
            int vocalsTicksHit = ReadInt32(span, ref offset);
            int vocalsTicksMissed = ReadInt32(span, ref offset);
            float vocalsPhraseTicksHit = ReadFloat(span, ref offset);
            int vocalsPhraseTicksTotal = ReadInt32(span, ref offset);
            
            bool soloActive = span[offset++] == 1;
            int soloSequence = ReadInt32(span, ref offset);
            int soloNoteCount = ReadInt32(span, ref offset);
            int soloNotesHit = ReadInt32(span, ref offset);
            int soloLastBonus = ReadInt32(span, ref offset);
            int soloTotalBonus = ReadInt32(span, ref offset);
            
            // Sustain and whammy
            int sustainsHeld = ReadInt32(span, ref offset);
            float whammyValue = ReadFloat(span, ref offset);
            
            double songTime = ReadDouble(span, ref offset);
            
            // Player name
            if (payload.Length < offset + 2)
                return;
            int nameLen = (span[offset] << 8) | span[offset + 1];
            offset += 2;
            if (payload.Length < offset + nameLen)
                return;
            string playerName = System.Text.Encoding.UTF8.GetString(span.Slice(offset, nameLen));
            
            // Find the NetworkPlayerData for this player and update it
            string connectionKey = connection.Id.ToString();
            
            // Log first and then periodically to avoid spam
            bool shouldLog = (sequence == 1 || sequence % 60 == 0);
            if (shouldLog)
            {
                Debug.Log($"[LiteNetNetworkingAdapter] Received gameplay snapshot from '{playerName}' (seq={sequence}, score={score}, combo={combo}, isHosting={_isHosting})");
            }
            
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                NetworkPlayerData targetPlayer = null;
                
                // If we're hosting, find the player by connection ID
                if (_isHosting && _connectedPlayers.TryGetValue(connectionKey, out var players))
                {
                    foreach (var player in players)
                    {
                        if (player.PlayerName == playerName || players.Count == 1)
                        {
                            targetPlayer = player;
                            break;
                        }
                    }
                }
                // If we're a client, the snapshot is from the host
                else if (!_isHosting)
                {
                    // Find the host player
                    foreach (var kvp in _connectedPlayers)
                    {
                        foreach (var player in kvp.Value)
                        {
                            if (player != null && (player.PlayerName == playerName || player.IsHost))
                            {
                                targetPlayer = player;
                                break;
                            }
                        }
                        if (targetPlayer != null) break;
                    }
                }
                
                if (targetPlayer == null)
                {
                    // Player not found - this might be expected during scene transitions
                    // Log occasionally to help debug
                    if (shouldLog)
                    {
                        Debug.LogWarning($"[LiteNetNetworkingAdapter] Target player not found for snapshot from '{playerName}'. IsHosting={_isHosting}, connKey={connectionKey}");
                    }
                    return;
                }
                
                if (shouldLog)
                {
                    Debug.Log($"[LiteNetNetworkingAdapter] Applying snapshot to player '{targetPlayer.PlayerName}' (IsLocalUser={targetPlayer.IsLocalUser})");
                }
                
                // Update the NetworkPlayerData using reflection (same as Mirror's SyncVars)
                ApplySnapshotToPlayerData(targetPlayer, sequence, score, combo, streak,
                    starPowerActive, starPowerAmount, starPowerPhrasesHit, totalStarPowerPhrases,
                    notesHit, notesMissed, overstrums, hoposStrummed, overhits, ghostInputs,
                    ghostsHit, accentsHit, dynamicsBonus, bandBonusScore,
                    vocalsTicksHit, vocalsTicksMissed, vocalsPhraseTicksHit, vocalsPhraseTicksTotal,
                    soloActive, soloSequence, soloNoteCount, soloNotesHit, soloLastBonus, soloTotalBonus,
                    sustainsHeld, whammyValue,
                    songTime);
                
                // If hosting, relay to all other clients
                if (_isHosting)
                {
                    RelayGameplaySnapshotToOthers(connection, payload);
                }
            });
        }
        
        private void ApplySnapshotToPlayerData(NetworkPlayerData player, uint sequence,
            int score, int combo, int streak, bool starPowerActive, float starPowerAmount,
            int starPowerPhrasesHit, int totalStarPowerPhrases, int notesHit, int notesMissed,
            int overstrums, int hoposStrummed, int overhits, int ghostInputs,
            int ghostsHit, int accentsHit, int dynamicsBonus, int bandBonusScore,
            int vocalsTicksHit, int vocalsTicksMissed, float vocalsPhraseTicksHit, int vocalsPhraseTicksTotal,
            bool soloActive, int soloSequence, int soloNoteCount, int soloNotesHit, int soloLastBonus, int soloTotalBonus,
            int sustainsHeld, float whammyValue,
            double songTime)
        {
            if (player == null) return;
            
            var bindingFlags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var type = typeof(NetworkPlayerData);
            
            // Check sequence to avoid applying old snapshots
            var seqField = type.GetField("lastGameplaySnapshotSequence", bindingFlags);
            if (seqField != null)
            {
                uint lastSeq = (uint)seqField.GetValue(player);
                if (sequence <= lastSeq)
                    return; // Old snapshot, ignore
                seqField.SetValue(player, sequence);
            }
            
            // Apply all the fields
            SetFieldValue(type, player, "currentScore", score, bindingFlags);
            SetFieldValue(type, player, "currentCombo", combo, bindingFlags);
            SetFieldValue(type, player, "currentStreak", streak, bindingFlags);
            SetFieldValue(type, player, "isStarPowerActive", starPowerActive, bindingFlags);
            SetFieldValue(type, player, "starPowerAmount", starPowerAmount, bindingFlags);
            SetFieldValue(type, player, "starPowerPhrasesHit", starPowerPhrasesHit, bindingFlags);
            SetFieldValue(type, player, "totalStarPowerPhrases", totalStarPowerPhrases, bindingFlags);
            SetFieldValue(type, player, "notesHit", notesHit, bindingFlags);
            SetFieldValue(type, player, "notesMissed", notesMissed, bindingFlags);
            SetFieldValue(type, player, "overstrums", overstrums, bindingFlags);
            SetFieldValue(type, player, "hoposStrummed", hoposStrummed, bindingFlags);
            SetFieldValue(type, player, "overhits", overhits, bindingFlags);
            SetFieldValue(type, player, "ghostInputs", ghostInputs, bindingFlags);
            SetFieldValue(type, player, "ghostsHit", ghostsHit, bindingFlags);
            SetFieldValue(type, player, "accentsHit", accentsHit, bindingFlags);
            SetFieldValue(type, player, "dynamicsBonus", dynamicsBonus, bindingFlags);
            SetFieldValue(type, player, "bandBonusScore", bandBonusScore, bindingFlags);
            SetFieldValue(type, player, "vocalsTicksHit", vocalsTicksHit, bindingFlags);
            SetFieldValue(type, player, "vocalsTicksMissed", vocalsTicksMissed, bindingFlags);
            SetFieldValue(type, player, "vocalsPhraseTicksHit", vocalsPhraseTicksHit, bindingFlags);
            SetFieldValue(type, player, "vocalsPhraseTicksTotal", vocalsPhraseTicksTotal, bindingFlags);
            SetFieldValue(type, player, "soloActive", soloActive, bindingFlags);
            SetFieldValue(type, player, "soloSequence", soloSequence, bindingFlags);
            SetFieldValue(type, player, "soloNoteCount", soloNoteCount, bindingFlags);
            SetFieldValue(type, player, "soloNotesHit", soloNotesHit, bindingFlags);
            SetFieldValue(type, player, "soloLastBonus", soloLastBonus, bindingFlags);
            SetFieldValue(type, player, "soloTotalBonus", soloTotalBonus, bindingFlags);
            SetFieldValue(type, player, "sustainsHeld", sustainsHeld, bindingFlags);
            SetFieldValue(type, player, "whammyValue", whammyValue, bindingFlags);
            SetFieldValue(type, player, "lastGameplaySongTime", songTime, bindingFlags);
            SetFieldValue(type, player, "lastGameplayNetworkTime", (double)Time.realtimeSinceStartupAsDouble, bindingFlags);
        }
        
        private void SetFieldValue(Type type, object obj, string fieldName, object value, System.Reflection.BindingFlags flags)
        {
            var field = type.GetField(fieldName, flags);
            field?.SetValue(obj, value);
        }
        
        private void RelayGameplaySnapshotToOthers(INetConnection sourceConnection, ReadOnlyMemory<byte> payload)
        {
            if (!_isHosting || _transport == null)
                return;
            
            byte[] message = payload.ToArray();
            BroadcastPacketToClientsExcept(message, sourceConnection.Id, "gameplay snapshot relay", ChannelType.Unreliable);
        }
        
        // Helper methods for binary serialization - delegate to YARG.Net.Utilities.BinaryUtils
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteInt32(byte[] buffer, ref int offset, int value) => BinaryUtils.WriteInt32(buffer, ref offset, value);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteFloat(byte[] buffer, ref int offset, float value) => BinaryUtils.WriteFloat(buffer, ref offset, value);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteDouble(byte[] buffer, ref int offset, double value) => BinaryUtils.WriteDouble(buffer, ref offset, value);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ReadInt32(ReadOnlySpan<byte> span, ref int offset) => BinaryUtils.ReadInt32(span, ref offset);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float ReadFloat(ReadOnlySpan<byte> span, ref int offset) => BinaryUtils.ReadFloat(span, ref offset);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double ReadDouble(ReadOnlySpan<byte> span, ref int offset) => BinaryUtils.ReadDouble(span, ref offset);
        
        #endregion
        
        private void CheckAllPlayersReady()
        {
            if (!_isHosting) return;
            
            bool allReady = true;
            int playerCount = 0;
            
            Debug.Log($"[LiteNetNetworkingAdapter] Host: Checking all players ready state...");
            
            foreach (var kvp in _connectedPlayers)
            {
                foreach (var player in kvp.Value)
                {
                    playerCount++;
                    Debug.Log($"[LiteNetNetworkingAdapter] Host: Player '{player.PlayerName}' (key: {kvp.Key}) ready: {player.IsReady}");
                    if (!player.IsReady)
                    {
                        allReady = false;
                    }
                }
            }
            
            Debug.Log($"[LiteNetNetworkingAdapter] Host: All ready check result: allReady={allReady}, playerCount={playerCount}");
            
            if (allReady && playerCount > 0)
            {
                Debug.Log($"[LiteNetNetworkingAdapter] Host: All {playerCount} players are ready!");
                
                // Broadcast to clients using ReadyStateBinaryPackets
                byte[] message = ReadyStateBinaryPackets.BuildAllPlayersReadyPacket();
                foreach (var conn in _connectionMap.Values)
                {
                    conn.Send(message, ChannelType.ReliableOrdered);
                }
                
                OnAllPlayersReady?.Invoke();
            }
        }
        
        /// <summary>
        /// Start gameplay for all players (host only).
        /// </summary>
        public void StartGameplayForAll()
        {
            if (!_isHosting)
            {
                Debug.LogWarning("[LiteNetNetworkingAdapter] Only host can start gameplay for all");
                return;
            }
            
            Debug.Log("[LiteNetNetworkingAdapter] Host: Starting gameplay for all players!");
            
            // Clear cached score results from previous song
            ClearCachedScoreResults();
            
            // Reset all players' ready states (they'll need to ready up again on score screen)
            ResetAllPlayerReadyStates();
            
            // Reset all players' gameplay state (score, combo, etc.) for new song
            ResetAllPlayersGameState();
            
            // Ensure we're not in practice mode when in multiplayer gameplay
            GlobalVariables.State.IsPractice = false;
            
            // Broadcast to all clients using GameplayBinaryPackets
            byte[] message = GameplayBinaryPackets.BuildStartPacket();
            foreach (var conn in _connectionMap.Values)
            {
                conn.Send(message, ChannelType.ReliableOrdered);
            }
            
            // Start locally
            OnStartGameplay?.Invoke();
            GlobalVariables.Instance.LoadScene(SceneIndex.Gameplay);
        }
        
        /// <summary>
        /// Gets all players with their ready states.
        /// </summary>
        public List<NetworkPlayerData> GetAllPlayers()
        {
            var result = new List<NetworkPlayerData>();
            foreach (var kvp in _connectedPlayers)
            {
                result.AddRange(kvp.Value);
            }
            return result;
        }
        
        /// <summary>
        /// Checks if all players are ready.
        /// </summary>
        public bool AreAllPlayersReady()
        {
            Debug.Log($"[LiteNetNetworkingAdapter] AreAllPlayersReady called - checking {_connectedPlayers.Count} connection keys");
            int playerCount = 0;
            foreach (var kvp in _connectedPlayers)
            {
                foreach (var player in kvp.Value)
                {
                    playerCount++;
                    Debug.Log($"[LiteNetNetworkingAdapter] AreAllPlayersReady - Player '{player.PlayerName}' (key: {kvp.Key.ToString().Substring(0, Math.Min(8, kvp.Key.ToString().Length))}...) IsReady={player.IsReady}");
                    if (!player.IsReady)
                    {
                        Debug.Log($"[LiteNetNetworkingAdapter] AreAllPlayersReady returning false - found not-ready player");
                        return false;
                    }
                }
            }
            bool result = _connectedPlayers.Count > 0;
            Debug.Log($"[LiteNetNetworkingAdapter] AreAllPlayersReady returning {result} (playerCount={playerCount}, connectionKeys={_connectedPlayers.Count})");
            return result;
        }

        /// <summary>
        /// Gets the local player's NetworkPlayerData.
        /// </summary>
        public NetworkPlayerData GetLocalPlayer()
        {
            foreach (var kvp in _connectedPlayers)
            {
                foreach (var player in kvp.Value)
                {
                    if (player.IsLocalUser)
                    {
                        return player;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Advance from the score screen after all players are ready.
        /// For LiteNet, this handles the transition to next song or back to lobby.
        /// </summary>
        public void AdvanceAfterScoreScreen()
        {
            if (!_isHosting)
            {
                Debug.LogWarning("[LiteNetNetworkingAdapter] AdvanceAfterScoreScreen called but not host");
                return;
            }

            Debug.Log("[LiteNetNetworkingAdapter] AdvanceAfterScoreScreen - all players ready, advancing...");

            // Reset all player ready states
            foreach (var kvp in _connectedPlayers)
            {
                foreach (var player in kvp.Value)
                {
                    SetPlayerReadyState(player, false);
                }
            }

            // Determine what action to take
            bool hasMoreSongs = GlobalVariables.State.PlayingAShow &&
                GlobalVariables.State.ShowIndex + 1 < GlobalVariables.State.ShowSongs.Count;

            // Broadcast the advance command to all clients
            // Format: [PacketType] [HAS_MORE_SONGS (1 byte)] [optional song hash data]
            BroadcastScoreScreenAdvance(hasMoreSongs);

            // Check if we're playing a show with more songs
            if (hasMoreSongs)
            {
                // Remove the completed song from the setlist so it doesn't show in the queue
                var completedEntry = _setlistManager.PopFirst();
                if (completedEntry != null)
                {
                    Debug.Log($"[LiteNetNetworkingAdapter] Removed completed song {completedEntry.SongHash} from setlist (remaining: {_setlistManager.Count})");
                    OnSetlistUpdated?.Invoke();
                }
                
                // Remove the completed song from GlobalVariables.State.ShowSongs as well
                // This ensures the song queue UI only shows remaining songs
                if (GlobalVariables.State.ShowSongs != null && GlobalVariables.State.ShowSongs.Count > 0)
                {
                    GlobalVariables.State.ShowSongs.RemoveAt(0);
                    // ShowIndex stays at 0 since we removed the first song
                    GlobalVariables.State.ShowIndex = 0;
                    Debug.Log($"[LiteNetNetworkingAdapter] Removed completed song from ShowSongs (remaining: {GlobalVariables.State.ShowSongs.Count})");
                }
                
                // Set the current song for the host (now the first song in the updated list)
                if (GlobalVariables.State.ShowSongs.Count > 0)
                {
                    var nextSong = GlobalVariables.State.ShowSongs[0];
                    GlobalVariables.State.CurrentSong = nextSong;
                }
                
                // Set the menu navigation target BEFORE loading the scene
                // This prevents the music library from flashing
                Debug.Log("[LiteNetNetworkingAdapter] Advancing to next song in show - going to difficulty select");
                Networking.YargNetworkManager.SetMenuNavigationAfterSceneLoad(MenuManager.Menu.DifficultySelect);
                
                // Go to Menu scene - MenuManager will automatically navigate to DifficultySelect
                GlobalVariables.Instance.LoadScene(SceneIndex.Menu);
            }
            else
            {
                // End the show and return to music library so players can queue more songs
                Debug.Log("[LiteNetNetworkingAdapter] Show complete or not playing show, returning to music library");
                GlobalVariables.State.PlayingAShow = false;
                GlobalVariables.State.ShowSongs?.Clear();
                GlobalVariables.State.ShowIndex = 0;
                
                // Clear the setlist so songs are removed from the queue
                _setlistManager.Clear();
                OnSetlistUpdated?.Invoke();
                Debug.Log("[LiteNetNetworkingAdapter] Host: Cleared setlist after show complete");
                
                // Update browsing state so clients know we're in song selection mode
                SetBrowsingState(true);
                
                // Set the full menu navigation stack: OnlineMultiplayer > LobbyRoom > MusicLibrary
                // This ensures that backing out of MusicLibrary goes to LobbyRoom, not MainMenu
                Debug.Log("[LiteNetNetworkingAdapter] Host: Navigating to music library with proper menu stack");
                Networking.YargNetworkManager.SetMenuNavigationAfterSceneLoad(
                    MenuManager.Menu.OnlineMultiplayer,
                    MenuManager.Menu.LobbyRoom,
                    MenuManager.Menu.MusicLibrary);
                
                // Transition back to menu - MenuManager will automatically navigate through the stack
                GlobalVariables.Instance.LoadScene(SceneIndex.Menu);
            }
        }

        /// <summary>
        /// Broadcasts the score screen advance command to all clients.
        /// </summary>
        private void BroadcastScoreScreenAdvance(bool hasMoreSongs)
        {
            // Build message using ScoreBinaryPackets - passing hasMoreSongs as index (0 or 1)
            byte[] message = ScoreBinaryPackets.BuildAdvancePacket(hasMoreSongs ? 1 : 0);

            foreach (var conn in _connectionMap.Values)
            {
                conn.Send(message, ChannelType.ReliableOrdered);
            }
            
            Debug.Log($"[LiteNetNetworkingAdapter] Broadcast score screen advance to {_connectionMap.Count} clients, hasMoreSongs={hasMoreSongs}");
        }

        /// <summary>
        /// Handles the score screen advance message on clients.
        /// </summary>
        private void HandleScoreScreenAdvanceMessage(ReadOnlyMemory<byte> payload)
        {
            if (!ScoreBinaryPackets.TryParseAdvancePacket(payload.Span, out int advanceIndex))
            {
                Debug.LogWarning("[LiteNetNetworkingAdapter] Invalid score screen advance message");
                return;
            }

            bool hasMoreSongs = advanceIndex != 0;

            Debug.Log($"[LiteNetNetworkingAdapter] Client: Received score screen advance, hasMoreSongs={hasMoreSongs}");

            // We need to dispatch to main thread, but we do it RIGHT NOW before anything else happens
            // This is critical because if we queue it, the score screen might be destroyed before the action runs
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                // Reset local player ready states AFTER switching to main thread
                foreach (var kvp in _connectedPlayers)
                {
                    foreach (var player in kvp.Value)
                    {
                        SetPlayerReadyState(player, false);
                    }
                }

                if (hasMoreSongs)
                {
                    // Remove the completed song from the setlist so it doesn't show in the queue
                    var completedEntry = _setlistManager.PopFirst();
                    if (completedEntry != null)
                    {
                        Debug.Log($"[LiteNetNetworkingAdapter] Client: Removed completed song {completedEntry.SongHash} from setlist (remaining: {_setlistManager.Count})");
                        OnSetlistUpdated?.Invoke();
                    }
                    
                    // Remove the completed song from GlobalVariables.State.ShowSongs as well
                    // This ensures the song queue UI only shows remaining songs
                    if (GlobalVariables.State.ShowSongs != null && GlobalVariables.State.ShowSongs.Count > 0)
                    {
                        GlobalVariables.State.ShowSongs.RemoveAt(0);
                        // ShowIndex stays at 0 since we removed the first song
                        GlobalVariables.State.ShowIndex = 0;
                        Debug.Log($"[LiteNetNetworkingAdapter] Client: Removed completed song from ShowSongs (remaining: {GlobalVariables.State.ShowSongs.Count})");
                    }
                    
                    // Set the current song for the client (now the first song in the updated list)
                    if (GlobalVariables.State.ShowSongs != null && GlobalVariables.State.ShowSongs.Count > 0)
                    {
                        var nextSong = GlobalVariables.State.ShowSongs[0];
                        GlobalVariables.State.CurrentSong = nextSong;
                    }
                    
                    // Set the menu navigation target BEFORE loading the scene
                    // This prevents the music library from flashing
                    Debug.Log("[LiteNetNetworkingAdapter] Client: Advancing to next song in show - going to difficulty select");
                    Networking.YargNetworkManager.SetMenuNavigationAfterSceneLoad(MenuManager.Menu.DifficultySelect);
                    
                    // Go to Menu scene - MenuManager will automatically navigate to DifficultySelect
                    GlobalVariables.Instance.LoadScene(SceneIndex.Menu);
                }
                else
                {
                    // Return to music library so players can queue more songs
                    Debug.Log("[LiteNetNetworkingAdapter] Client: Show complete, returning to music library");
                    GlobalVariables.State.PlayingAShow = false;
                    GlobalVariables.State.ShowSongs?.Clear();
                    GlobalVariables.State.ShowIndex = 0;
                    
                    // Clear the setlist so songs are removed from the queue
                    _setlistManager.Clear();
                    OnSetlistUpdated?.Invoke();
                    Debug.Log("[LiteNetNetworkingAdapter] Client: Cleared setlist after show complete");
                    
                    // Update browsing state to match host
                    _isBrowsingSongs = true;
                    
                    // Set the full menu navigation stack: OnlineMultiplayer > LobbyRoom > MusicLibrary
                    // This ensures that backing out of MusicLibrary goes to LobbyRoom, not MainMenu
                    Debug.Log("[LiteNetNetworkingAdapter] Client: Navigating to music library with proper menu stack");
                    Networking.YargNetworkManager.SetMenuNavigationAfterSceneLoad(
                        MenuManager.Menu.OnlineMultiplayer,
                        MenuManager.Menu.LobbyRoom,
                        MenuManager.Menu.MusicLibrary);
                    
                    // Transition back to menu - MenuManager will automatically navigate through the stack
                    GlobalVariables.Instance.LoadScene(SceneIndex.Menu);
                }
            });
        }

        /// <summary>
        /// Sends the local player's score screen results to other players.
        /// Called when transitioning to the score screen.
        /// </summary>
        public void SendScoreResults(string playerName, bool isHighScore, bool isFullCombo, int score, int maxCombo, int notesHit, int notesMissed)
        {
            if (_transport == null || !IsNetworkActive)
            {
                Debug.LogWarning("[LiteNetNetworkingAdapter] Cannot send score results - not connected");
                return;
            }

            Debug.Log($"[LiteNetNetworkingAdapter] Sending score results: player={playerName}, highScore={isHighScore}, FC={isFullCombo}, score={score}");

            // Build message using ScoreBinaryPackets
            // Note: starCount is encoded as isHighScore (1 or 0), and fullCombo as isFullCombo
            byte[] message = ScoreBinaryPackets.BuildResultsPacket(
                playerName,
                score,
                notesHit,
                notesMissed,
                maxCombo,
                isHighScore ? 1 : 0, // Use starCount field to pass isHighScore
                isFullCombo);

            // Send to all connections (reliable since this is important)
            foreach (var conn in _connectionMap.Values)
            {
                try
                {
                    conn.Send(message, ChannelType.ReliableOrdered);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[LiteNetNetworkingAdapter] Failed to send score results: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Gets all cached score results from remote players.
        /// Use this to retrieve results that may have arrived before subscribing to events.
        /// </summary>
        public Dictionary<string, PlayerScoreResult> GetCachedScoreResults()
        {
            return _scoreResultsManager.GetResultsDictionary();
        }

        /// <summary>
        /// Clears cached score results. Call this when starting new gameplay.
        /// </summary>
        public void ClearCachedScoreResults()
        {
            _scoreResultsManager.Clear();
            Debug.Log("[LiteNetNetworkingAdapter] Cleared cached score results");
        }

        /// <summary>
        /// Handles incoming score results from another player.
        /// </summary>
        private void HandleScoreResultsMessage(INetConnection connection, ReadOnlyMemory<byte> payload)
        {
            if (!ScoreBinaryPackets.TryParseResultsPacket(payload.Span, out var result))
            {
                Debug.LogWarning("[LiteNetNetworkingAdapter] Invalid score results message");
                return;
            }

            string playerName = result.PlayerName;
            bool isHighScore = result.StarCount > 0; // StarCount field used to pass isHighScore
            bool isFullCombo = result.FullCombo;
            int score = result.FinalScore;
            int maxCombo = result.MaxCombo;
            int notesHit = result.NotesHit;
            int notesMissed = result.NotesMissed;

            Debug.Log($"[LiteNetNetworkingAdapter] Received score results: player={playerName}, highScore={isHighScore}, FC={isFullCombo}, score={score}, maxCombo={maxCombo}");

            // Cache the result for late subscribers (score screen may not be loaded yet)
            // Use the manager - it will also fire the ResultReceived event
            _scoreResultsManager.RecordResult(playerName, isHighScore, isFullCombo, score, maxCombo, notesHit, notesMissed);

            // If we're the host, relay to other clients (excluding the sender)
            if (_isHosting)
            {
                RelayScoreResults(connection, playerName, isHighScore, isFullCombo, score, maxCombo, notesHit, notesMissed);
            }

            // Fire event for the score screen to handle (in addition to manager event)
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                OnScoreResultsReceived?.Invoke(playerName, isHighScore, isFullCombo, score, maxCombo, notesHit, notesMissed);
            });
        }

        /// <summary>
        /// Host relays score results to all other clients except the sender.
        /// </summary>
        private void RelayScoreResults(INetConnection senderConnection, string playerName, bool isHighScore, bool isFullCombo, int score, int maxCombo, int notesHit, int notesMissed)
        {
            // Build message using ScoreBinaryPackets
            byte[] message = ScoreBinaryPackets.BuildResultsPacket(
                playerName,
                score,
                notesHit,
                notesMissed,
                maxCombo,
                isHighScore ? 1 : 0, // Use starCount field to pass isHighScore
                isFullCombo);

            BroadcastPacketToClientsExcept(message, senderConnection.Id, "score results relay");
        }
        
        #endregion
        
        #region Unison Phrase Sync
        
        /// <summary>
        /// Sets the expected number of players for unison tracking.
        /// Should be called when gameplay starts.
        /// </summary>
        public void SetUnisonPlayerCount(int count)
        {
            _unisonCoordinator.ExpectedPlayerCount = count;
            _unisonCoordinator.Reset();
            Debug.Log($"[LiteNetNetworkingAdapter] Unison player count set to {count}");
        }
        
        /// <summary>
        /// Resets unison tracking state. Should be called when gameplay ends or restarts.
        /// </summary>
        public void ResetUnisonTracking()
        {
            _unisonCoordinator.Reset();
            _unisonCoordinator.ExpectedPlayerCount = 0;
            Debug.Log("[LiteNetNetworkingAdapter] Unison tracking reset");
        }
        
        /// <summary>
        /// Called when the local player hits a star power phrase that is part of a unison.
        /// Sends the completion to the host (or processes locally if hosting).
        /// </summary>
        public void SendUnisonPhraseHit(double phraseTime, double phraseEndTime)
        {
            if (!IsNetworkActive)
            {
                Debug.LogWarning("[LiteNetNetworkingAdapter] Cannot send unison phrase hit - network not active");
                return;
            }
            
            string playerName = GetPlayerNameFromProfile();
            Debug.Log($"[LiteNetNetworkingAdapter] Sending unison phrase hit: player={playerName}, time={phraseTime:F3}");
            
            if (_isHosting)
            {
                // Host processes locally with "host" as unique player key
                bool awardedBonus = _unisonCoordinator.RecordPhraseHit("host", phraseTime, phraseEndTime);
                if (awardedBonus)
                {
                    Debug.Log($"[LiteNetNetworkingAdapter] Host: All players completed unison phrase at {phraseTime:F3}");
                    // Event fired via OnUnisonCoordinatorBonusAwarded bridge
                }
            }
            else
            {
                // Client sends to host
                SendUnisonPhraseHitToHost(playerName, phraseTime, phraseEndTime);
            }
        }
        
        private void SendUnisonPhraseHitToHost(string playerName, double phraseTime, double phraseEndTime)
        {
            if (_transport == null) return;
            
            byte[] message = UnisonBinaryPackets.BuildPhraseHitPacket(playerName, phraseTime, phraseEndTime);
            
            // Send to host (first connection when we're a client)
            foreach (var conn in _connectionMap.Values)
            {
                try
                {
                    conn.Send(message, ChannelType.ReliableOrdered);
                    Debug.Log($"[LiteNetNetworkingAdapter] Sent unison phrase hit to host");
                    break;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[LiteNetNetworkingAdapter] Failed to send unison phrase hit: {ex.Message}");
                }
            }
        }
        
        private void HandleUnisonPhraseHitMessage(INetConnection connection, ReadOnlyMemory<byte> payload)
        {
            if (!_isHosting) return;
            
            if (!UnisonBinaryPackets.TryParsePhraseHitPacket(payload.Span, out var result))
            {
                Debug.LogWarning("[LiteNetNetworkingAdapter] Invalid unison phrase hit message");
                return;
            }
            
            string playerName = result.PlayerName;
            double phraseTime = result.PhraseStartTime;
            double phraseEndTime = result.PhraseEndTime;
            string playerKey = connection.Id.ToString();
            
            Debug.Log($"[LiteNetNetworkingAdapter] Host received unison phrase hit: player={playerName}, time={phraseTime:F3}");
            
            // Use coordinator - it will fire event if bonus awarded
            _unisonCoordinator.RecordPhraseHit(playerKey, phraseTime, phraseEndTime);
        }
        
        private void BroadcastUnisonBonusAward(double phraseTime)
        {
            if (!_isHosting || _transport == null) return;
            
            byte[] message = UnisonBinaryPackets.BuildBonusAwardPacket(phraseTime);
            Debug.Log($"[LiteNetNetworkingAdapter] Broadcasting unison bonus award for phrase at {phraseTime:F3}");
            BroadcastPacketToClients(message, "unison bonus award");
        }
        
        private void HandleUnisonBonusAwardMessage(ReadOnlyMemory<byte> payload)
        {
            if (_isHosting) return;
            
            if (!UnisonBinaryPackets.TryParseBonusAwardPacket(payload.Span, out double phraseTime))
            {
                Debug.LogWarning("[LiteNetNetworkingAdapter] Invalid unison bonus award message");
                return;
            }
            
            Debug.Log($"[LiteNetNetworkingAdapter] Client received unison bonus award for phrase at {phraseTime:F3}");
            
            UnityMainThreadDispatcher.EnqueueAction(() => OnUnisonBonusAwarded?.Invoke(phraseTime));
        }
        
        #endregion
        
        #endregion
        
        private void OnTransportPeerDisconnected(INetConnection connection)
        {
            // Remove from connection map
            _connectionMap.Remove(connection.Id);
            
            // Remove authentication state via manager
            _lobbyAuthenticator.RemoveConnection(connection.Id);
            
            // Only handle this when we're hosting (server mode)
            if (!_isHosting)
            {
                return;
            }
            
            Debug.Log($"[LiteNetNetworkingAdapter] Server: Client disconnected - {connection.EndPoint}");
            
            // Remove song library for this player and recalculate shared songs
            RemoveSongLibraryForPlayer(connection.Id);
            RecalculateSharedSongs();
            
            // Update player count
            if (_currentLobby != null && _currentLobby.CurrentPlayers > 1)
            {
                _currentLobby.CurrentPlayers--;
                Debug.Log($"[LiteNetNetworkingAdapter] Server: Player count is now {_currentLobby.CurrentPlayers}");
            }
            
            // Remove player data - dispatch to main thread for thread safety
            string clientId = connection.Id.ToString();
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                if (_connectedPlayers.TryGetValue(clientId, out var players))
                {
                    foreach (var player in players)
                    {
                        string playerName = player.PlayerName;
                        
                        // Fire the standard player left event
                        OnPlayerLeft?.Invoke(player);
                        
                        // If we're in gameplay (show is playing), broadcast to other clients
                        // so they can remove the player's track
                        if (GlobalVariables.State.PlayingAShow)
                        {
                            Debug.Log($"[LiteNetNetworkingAdapter] Player '{playerName}' disconnected during gameplay, broadcasting to other clients");
                            BroadcastPlayerLeftGameplay(playerName);
                        }
                    }
                    _connectedPlayers.Remove(clientId);
                    
                    // Update PlayerNames in current lobby for discovery responses
                    UpdateLobbyPlayerNames();
                }
            });
        }
        
        private void OnTransportLatencyUpdate(INetConnection connection, int latencyMs)
        {
            Debug.Log($"[LiteNetNetworkingAdapter] Latency update: connection={connection.Id}, latency={latencyMs}ms, isHosting={_isHosting}");
            
            // Update ping for the player associated with this connection
            string clientId = connection.Id.ToString();
            
            // Dispatch to main thread since we're modifying Unity objects
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                // Check if this is a client connection (we're hosting)
                if (_isHosting && _connectedPlayers.TryGetValue(clientId, out var players))
                {
                    Debug.Log($"[LiteNetNetworkingAdapter] Host: Updating ping for {players.Count} player(s) from connection {clientId}");
                    foreach (var player in players)
                    {
                        UpdatePlayerPing(player, latencyMs);
                    }
                }
                // If we're a client, the latency update is for our connection to the server
                // We need to update the HOST's ping as displayed on our client
                else if (!_isHosting)
                {
                    Debug.Log($"[LiteNetNetworkingAdapter] Client: Updating host ping to {latencyMs}ms");
                    // Find the host player (not local) and update their ping
                    foreach (var kvp in _connectedPlayers)
                    {
                        foreach (var player in kvp.Value)
                        {
                            if (player != null && player.IsHost)
                            {
                                Debug.Log($"[LiteNetNetworkingAdapter] Client: Found host player '{player.PlayerName}', updating ping");
                                UpdatePlayerPing(player, latencyMs);
                            }
                        }
                    }
                }
                else
                {
                    Debug.Log($"[LiteNetNetworkingAdapter] Host: No players found for connection {clientId}. Keys: {string.Join(", ", _connectedPlayers.Keys)}");
                }
            });
        }
        
        private void UpdatePlayerPing(NetworkPlayerData player, int latencyMs)
        {
            if (player == null) return;
            
            try
            {
                var pingField = typeof(NetworkPlayerData).GetField("ping",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (pingField != null)
                {
                    pingField.SetValue(player, (float)latencyMs);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LiteNetNetworkingAdapter] Failed to update ping: {ex.Message}");
            }
        }
        
        private bool HandleUnconnectedMessage(System.Net.IPEndPoint remoteEndPoint, LiteNetLib.NetPacketReader reader, LiteNetLib.UnconnectedMessageType messageType)
        {
            Debug.Log($"[LiteNetNetworkingAdapter] Received unconnected message from {remoteEndPoint}, type: {messageType}, bytes: {reader.AvailableBytes}");
            
            // Only handle when we're hosting a server
            if (!_isHosting || _liteNetTransport?.NetManager == null)
            {
                Debug.Log($"[LiteNetNetworkingAdapter] Not handling: isHosting={_isHosting}, NetManager={(_liteNetTransport?.NetManager != null)}");
                return false;
            }
            
            return _discovery?.HandleUnconnectedMessage(remoteEndPoint, reader, _liteNetTransport.NetManager) ?? false;
        }
        
        private void HandleLobbyDiscovered(LobbyInfo lobby)
        {
            Debug.Log($"[LiteNetNetworkingAdapter] Discovered lobby: {lobby.LobbyName} at {lobby.IpAddress}:{lobby.Port}");
            OnLobbyListUpdated?.Invoke(new List<LobbyInfo>(_discovery.DiscoveredLobbies.Values));
        }
        
        private void HandleLobbyLost(string lobbyId)
        {
            Debug.Log($"[LiteNetNetworkingAdapter] Lost lobby: {lobbyId}");
            OnLobbyListUpdated?.Invoke(new List<LobbyInfo>(_discovery.DiscoveredLobbies.Values));
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
                    MaxPlayers = maxPlayers
                };

                // Create lobby state manager
                _lobbyStateManager = new LobbyStateManager(_sessionManager, config);

                // Start server runtime
                var serverOptions = new ServerRuntimeOptions(_transport)
                {
                    Port = _defaultPort,
                    Address = "0.0.0.0",
                    EnableNatPunchThrough = true
                };

                _serverRuntime.Configure(serverOptions);
                _ = _serverRuntime.StartAsync();

                // Get actual player name from profile BEFORE creating lobby info
                string actualPlayerName = GetPlayerNameFromProfile();
                _playerName = actualPlayerName; // Update the field so it's consistent

                // Get local LAN address for sidebar display
                string lanAddress = NetworkAddressUtility.GetLocalLanAddress() ?? "Unknown";
                Debug.Log($"[LiteNetNetworkingAdapter] Detected LAN address: {lanAddress}");

                // Create lobby info
                _currentLobby = new LobbyInfo
                {
                    LobbyId = _lobbyStateManager.LobbyId.ToString(),
                    LobbyName = lobbyName,
                    HostName = actualPlayerName,
                    CurrentPlayers = 1, // Host counts as 1 player
                    MaxPlayers = maxPlayers,
                    PrivacyMode = privacyMode,
                    HasPassword = !string.IsNullOrEmpty(password),
                    Password = password,
                    IsActive = true,
                    IpAddress = lanAddress,
                    Port = _defaultPort,
                    PublicPort = _defaultPort,
                    PublicAddress = string.Empty, // TODO: Implement STUN/UPnP for public IP discovery
                    TransportId = "LiteNetLib",
                    PlayerNames = new[] { actualPlayerName },
                    PlayerInstruments = new int[0]
                };

                _isHosting = true;
                _maxPlayers = maxPlayers;
                
                // Create a simple NetworkPlayerData wrapper for the host (not a MonoBehaviour)
                // We create a GameObject but use a simpler approach that doesn't trigger Mirror dependencies
                var hostPlayerData = CreateLiteNetPlayerData(actualPlayerName, isHost: true, isLocal: true);

                // Add host to connected players dictionary (using "host" as the key since we don't have actual connection objects yet)
                _connectedPlayers.Clear();
                _connectedPlayers["host"] = new List<NetworkPlayerData> { hostPlayerData };
                Debug.Log($"[LiteNetNetworkingAdapter] Added host player '{actualPlayerName}' to connected players");
                
                // Start advertising for discovery (private lobbies are still discoverable, they just require a password to join)
                _discovery?.StartAdvertising(_currentLobby);
                Debug.Log($"[LiteNetNetworkingAdapter] Started advertising lobby for discovery (privacy: {privacyMode})");

                OnLobbyCreated?.Invoke(_currentLobby);
                
                // Host also joins their own lobby (triggers UI navigation)
                OnLobbyJoined?.Invoke(_currentLobby);
                
                // Set lobby password for authentication
                SetLobbyPassword(password);
                
                // Register host's song library for shared songs
                UploadSongLibraryAsHost();
                
                // Start STUN resolution to get public IP address
                StartPublicEndpointResolution();

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
                // Store password for authentication
                SetJoinPassword(password);
                
                // Check if already connected or connecting
                if (_isConnected)
                {
                    Debug.LogWarning($"[LiteNetNetworkingAdapter] Already connected to a lobby, ignoring join request");
                    return;
                }

                if (_isJoinInProgress)
                {
                    Debug.LogWarning($"[LiteNetNetworkingAdapter] Join already in progress, ignoring duplicate request");
                    return;
                }

                _isJoinInProgress = true;

                // Parse endpoint (format: "IP:Port")
                var parts = endpoint.Split(':');
                if (parts.Length != 2 || !int.TryParse(parts[1], out int port))
                {
                    throw new ArgumentException($"Invalid endpoint format: {endpoint}. Expected IP:Port");
                }

                string address = parts[0];

                // Try to find lobby info from discovered lobbies first
                LobbyInfo discoveredLobby = null;
                if (_discovery != null)
                {
                    var discoveredLobbies = _discovery.DiscoveredLobbies;
                    Debug.Log($"[LiteNetNetworkingAdapter] Looking for {address}:{port} in {discoveredLobbies.Count} discovered lobbies");
                    foreach (var lobby in discoveredLobbies.Values)
                    {
                        Debug.Log($"[LiteNetNetworkingAdapter] Checking lobby: {lobby.LobbyName} at {lobby.IpAddress}:{lobby.Port} (public: {lobby.PublicAddress}:{lobby.PublicPort})");
                        if ((lobby.IpAddress == address || lobby.PublicAddress == address) && lobby.Port == port)
                        {
                            discoveredLobby = lobby;
                            Debug.Log($"[LiteNetNetworkingAdapter] Found discovered lobby: {lobby.LobbyName}");
                            break;
                        }
                    }
                }
                else
                {
                    Debug.Log("[LiteNetNetworkingAdapter] Discovery is null, cannot look up discovered lobbies");
                }

                // Use discovered lobby info or create temporary lobby info for the join attempt
                if (discoveredLobby != null)
                {
                    _currentLobby = new LobbyInfo
                    {
                        LobbyId = discoveredLobby.LobbyId,
                        LobbyName = discoveredLobby.LobbyName,
                        HostName = discoveredLobby.HostName,
                        CurrentPlayers = discoveredLobby.CurrentPlayers,
                        MaxPlayers = discoveredLobby.MaxPlayers,
                        PrivacyMode = discoveredLobby.PrivacyMode,
                        HasPassword = discoveredLobby.HasPassword || !string.IsNullOrEmpty(password),
                        Password = password,
                        IsActive = true,
                        IpAddress = address,
                        Port = port,
                        PublicPort = discoveredLobby.PublicPort,
                        PublicAddress = discoveredLobby.PublicAddress ?? address,
                        TransportId = discoveredLobby.TransportId ?? "LiteNetLib",
                        PlayerNames = discoveredLobby.PlayerNames,
                        PlayerInstruments = discoveredLobby.PlayerInstruments
                    };
                }
                else
                {
                    Debug.Log($"[LiteNetNetworkingAdapter] No discovered lobby found for {endpoint}, using temporary info");
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
                }

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
        
        private void NotifyClientsOfHostDisconnect()
        {
            if (_liteNetTransport?.NetManager == null)
            {
                Debug.Log("[LiteNetNetworkingAdapter] No NetManager available to notify clients");
                return;
            }
            
            var netManager = _liteNetTransport.NetManager;
            var peers = netManager.ConnectedPeerList;
            
            if (peers.Count == 0)
            {
                Debug.Log("[LiteNetNetworkingAdapter] No connected peers to notify");
                return;
            }
            
            Debug.Log($"[LiteNetNetworkingAdapter] Notifying {peers.Count} clients of host disconnect");
            
            // Build host disconnect packet using NavigationBinaryPackets
            byte[] message = NavigationBinaryPackets.BuildHostDisconnectPacket();
            
            foreach (var peer in peers)
            {
                try
                {
                    peer.Send(message, LiteNetLib.DeliveryMethod.ReliableOrdered);
                    Debug.Log($"[LiteNetNetworkingAdapter] Sent host disconnect notification to {peer.EndPoint}");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[LiteNetNetworkingAdapter] Failed to notify peer {peer.EndPoint}: {ex.Message}");
                }
            }
            
            // Give a moment for the messages to be sent
            System.Threading.Thread.Sleep(100);
        }

        public void LeaveLobby()
        {
            Debug.Log("[LiteNetNetworkingAdapter] Leaving lobby");

            try
            {
                // Cancel any pending STUN resolution
                CancelPublicEndpointResolution();
                
                // Stop advertising
                _discovery?.StopAdvertising();
                
                if (_isHosting)
                {
                    // Notify all connected clients that the host is leaving
                    NotifyClientsOfHostDisconnect();
                    
                    // Stop server synchronously to ensure cleanup
                    Debug.Log("[LiteNetNetworkingAdapter] Stopping server runtime...");
                    try
                    {
                        _serverRuntime.StopAsync().Wait(TimeSpan.FromSeconds(2));
                    }
                    catch (Exception stopEx)
                    {
                        Debug.LogWarning($"[LiteNetNetworkingAdapter] Server stop warning: {stopEx.Message}");
                    }
                    _isHosting = false;
                    Debug.Log("[LiteNetNetworkingAdapter] Server stopped");
                }
                
                if (_isConnected)
                {
                    // Disconnect from server
                    Debug.Log("[LiteNetNetworkingAdapter] Disconnecting client...");
                    try
                    {
                        _clientRuntime.DisconnectAsync().Wait(TimeSpan.FromSeconds(2));
                    }
                    catch (Exception discEx)
                    {
                        Debug.LogWarning($"[LiteNetNetworkingAdapter] Disconnect warning: {discEx.Message}");
                    }
                    _isConnected = false;
                    Debug.Log("[LiteNetNetworkingAdapter] Client disconnected");
                }

                // Clean up player GameObjects
                foreach (var kvp in _connectedPlayers)
                {
                    foreach (var player in kvp.Value)
                    {
                        if (player != null && player.gameObject != null)
                        {
                            GameObject.Destroy(player.gameObject);
                        }
                    }
                }

                _currentLobby = null;
                _connectedPlayers.Clear();
                _pendingConnections.Clear();
                _connectionMap.Clear();
                _isJoinInProgress = false;

                OnLobbyLeft?.Invoke();
                Debug.Log("[LiteNetNetworkingAdapter] Lobby left successfully");
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
        
        #region Discovery

        public void StartDiscovery()
        {
            Debug.Log("[LiteNetNetworkingAdapter] Starting discovery");
            _discovery?.StartDiscovery();
        }
        
        public void StopDiscovery()
        {
            Debug.Log("[LiteNetNetworkingAdapter] Stopping discovery");
            _discovery?.StopDiscovery();
        }
        
        public void SendDiscoveryRequest(string address, int port = 0)
        {
            Debug.Log($"[LiteNetNetworkingAdapter] Sending discovery request to {address}:{(port > 0 ? port : _defaultPort)}");
            _discovery?.SendDiscoveryRequest(address, port > 0 ? port : _defaultPort);
        }
        
        public void SetDiscoveryPort(int port)
        {
            Debug.Log($"[LiteNetNetworkingAdapter] Setting discovery port to {port}");
            _discovery?.SetDiscoveryPort(port);
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

        public void KickPlayer(NetworkPlayerData playerData)
        {
            if (!_isHosting)
            {
                Debug.LogWarning("[LiteNetNetworkingAdapter] Only the host can kick players");
                return;
            }
            
            if (playerData == null)
            {
                Debug.LogWarning("[LiteNetNetworkingAdapter] Cannot kick null player");
                return;
            }
            
            Debug.Log($"[LiteNetNetworkingAdapter] Kicking player: {playerData.PlayerName}");
            
            // Find the connection ID for this player
            string connectionIdToKick = null;
            foreach (var kvp in _connectedPlayers)
            {
                if (kvp.Key.ToString() == "host") continue; // Don't kick the host entry
                
                foreach (var player in kvp.Value)
                {
                    if (player == playerData || player.PlayerName == playerData.PlayerName)
                    {
                        connectionIdToKick = kvp.Key.ToString();
                        break;
                    }
                }
                if (connectionIdToKick != null) break;
            }
            
            if (connectionIdToKick == null)
            {
                Debug.LogWarning($"[LiteNetNetworkingAdapter] Could not find connection for player: {playerData.PlayerName}");
                return;
            }
            
            // Find the peer and disconnect them
            if (_liteNetTransport?.NetManager != null)
            {
                var netManager = _liteNetTransport.NetManager;
                foreach (var peer in netManager.ConnectedPeerList)
                {
                    // Match by endpoint or use a stored mapping
                    // For now, we'll try to match by iterating
                    // This is a simplified approach - ideally we'd have a proper connection ID mapping
                    Debug.Log($"[LiteNetNetworkingAdapter] Disconnecting peer {peer.EndPoint}");
                    peer.Disconnect();
                    break; // In a simple 1-to-1 case, disconnect the first non-host peer
                }
            }
            
            // Remove from connected players
            if (_connectedPlayers.ContainsKey(connectionIdToKick))
            {
                var players = _connectedPlayers[connectionIdToKick];
                foreach (var player in players)
                {
                    if (player != null && player.gameObject != null)
                    {
                        GameObject.Destroy(player.gameObject);
                    }
                    OnPlayerLeft?.Invoke(player);
                }
                _connectedPlayers.Remove(connectionIdToKick);
            }
            
            // Update player count
            if (_currentLobby != null && _currentLobby.CurrentPlayers > 1)
            {
                _currentLobby.CurrentPlayers--;
            }
            
            // Update lobby player names
            UpdateLobbyPlayerNames();
            
            Debug.Log($"[LiteNetNetworkingAdapter] Player kicked: {playerData.PlayerName}");
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

        #region Helper Methods

        /// <summary>
        /// Navigates to the difficulty select screen after a short delay.
        /// Used when advancing to the next song in a show.
        /// </summary>
        private async UniTaskVoid DelayedNavigateToDifficultySelect()
        {
            // Wait for the menu scene to fully load
            await UniTask.Delay(500);
            
            // Make sure we're on the main thread
            await UniTask.SwitchToMainThread();
            
            Debug.Log("[LiteNetNetworkingAdapter] Navigating to difficulty select for next song");
            
            if (MenuManager.Instance != null)
            {
                MenuManager.Instance.PushMenu(MenuManager.Menu.DifficultySelect);
            }
            else
            {
                Debug.LogWarning("[LiteNetNetworkingAdapter] MenuManager.Instance is null, cannot navigate to difficulty select");
            }
        }

        /// <summary>
        /// Gets or creates the local player's network identity.
        /// Uses the player's profile ID as the persistent network ID.
        /// </summary>
        private NetworkPlayerIdentity GetLocalPlayerIdentity()
        {
            // Try to get from PlayerContainer.Players (active players with controllers)
            var localPlayers = PlayerContainer.Players;
            if (localPlayers != null && localPlayers.Count > 0)
            {
                var profile = localPlayers[0].Profile;
                if (profile != null)
                {
                    // Profile.Name is the display name, Profile.Id is the persistent GUID
                    string displayName = SanitizeDisplayName(profile.Name);
                    return LocalPlayerIdentity.GetOrCreate(displayName, profile.Id);
                }
            }
            
            // Fallback: Try to get from PlayerContainer.Profiles (all loaded profiles)
            var profiles = PlayerContainer.Profiles;
            if (profiles != null && profiles.Count > 0)
            {
                // Try to find a non-bot profile
                foreach (var profile in profiles)
                {
                    if (profile.IsBot) continue;
                    
                    string displayName = SanitizeDisplayName(profile.Name);
                    return LocalPlayerIdentity.GetOrCreate(displayName, profile.Id);
                }
            }

            // No profile found - create with a random name and new GUID
            string randomName = LocalPlayerIdentity.GenerateRandomDisplayName();
            _playerName = randomName;
            Debug.Log($"[LiteNetNetworkingAdapter] No valid profile found, using generated identity: {randomName}");
            return LocalPlayerIdentity.GetOrCreate(randomName);
        }
        
        /// <summary>
        /// Sanitizes a display name for network use.
        /// </summary>
        private static string SanitizeDisplayName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return LocalPlayerIdentity.GenerateRandomDisplayName();
            }
            
            // Truncate very long names
            if (name.Length > 64)
            {
                return name.Substring(0, 64);
            }
            
            return name;
        }

        /// <summary>
        /// Gets the player name from the local identity.
        /// </summary>
        private string GetPlayerNameFromProfile()
        {
            var identity = GetLocalPlayerIdentity();
            return identity.DisplayName;
        }

        private NetworkPlayerData CreateLiteNetPlayerData(string playerName, bool isHost, bool isLocal)
        {
            // Create a GameObject with NetworkPlayerData
            // We keep it disabled to prevent Mirror's OnEnable from running and causing NullReferenceExceptions
            var playerObject = new GameObject($"NetworkPlayer_{playerName}");
            playerObject.SetActive(false); // Keep disabled to prevent Mirror initialization
            
            var playerData = playerObject.AddComponent<NetworkPlayerData>();
            
            // Debug: List all private fields on NetworkPlayerData to understand Mirror's IL weaving
            var allFields = typeof(NetworkPlayerData).GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Debug.Log($"[LiteNetNetworkingAdapter] NetworkPlayerData has {allFields.Length} private instance fields");
            foreach (var f in allFields)
            {
                if (f.Name.Contains("host") || f.Name.Contains("Host") || f.Name.Contains("name") || f.Name.Contains("Name") || f.Name.Contains("ping") || f.Name.Contains("Ping"))
                {
                    Debug.Log($"[LiteNetNetworkingAdapter]   Field: {f.Name} ({f.FieldType.Name})");
                }
            }
            
            // Set properties using reflection to avoid Mirror server methods
            var playerNameField = typeof(NetworkPlayerData).GetField("playerName", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var isHostField = typeof(NetworkPlayerData).GetField("isHost", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var pingField = typeof(NetworkPlayerData).GetField("ping",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            Debug.Log($"[LiteNetNetworkingAdapter] CreateLiteNetPlayerData reflection - playerNameField: {playerNameField != null}, isHostField: {isHostField != null}, pingField: {pingField != null}");
            
            if (playerNameField != null)
            {
                playerNameField.SetValue(playerData, playerName);
                Debug.Log($"[LiteNetNetworkingAdapter] Set playerName to '{playerName}'");
            }
            else
            {
                Debug.LogError("[LiteNetNetworkingAdapter] playerName field not found!");
            }
            
            if (isHostField != null)
            {
                isHostField.SetValue(playerData, isHost);
                Debug.Log($"[LiteNetNetworkingAdapter] Set isHost to {isHost}");
            }
            else
            {
                Debug.LogError("[LiteNetNetworkingAdapter] isHost field not found!");
            }
            
            // Set initial ping value
            // For local players, ping is 0 (they have no network latency to themselves)
            // For remote players, we use -1 to indicate "no ping data yet"
            // This allows 0ms latency (valid for localhost) to display correctly
            // Note: isLocal determines locality, not isHost - a host is still remote from a client's perspective
            float initialPing = isLocal ? 0f : -1f;
            if (pingField != null)
            {
                pingField.SetValue(playerData, initialPing);
                Debug.Log($"[LiteNetNetworkingAdapter] Set ping to {initialPing}");
            }
            else
            {
                Debug.LogError("[LiteNetNetworkingAdapter] ping field not found!");
            }
            
            // Set IsLocalUser override for LiteNet (since Mirror's isClient/isLocalPlayer don't work)
            var useLocalUserOverrideField = typeof(NetworkPlayerData).GetField("_useLocalUserOverride",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var isLocalUserOverrideField = typeof(NetworkPlayerData).GetField("_isLocalUserOverride",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            if (useLocalUserOverrideField != null && isLocalUserOverrideField != null)
            {
                useLocalUserOverrideField.SetValue(playerData, true);
                isLocalUserOverrideField.SetValue(playerData, isLocal);
                Debug.Log($"[LiteNetNetworkingAdapter] Set IsLocalUser override to {isLocal}");
            }
            
            // Verify the values were set correctly
            Debug.Log($"[LiteNetNetworkingAdapter] Verifying: PlayerName='{playerData.PlayerName}', IsHost={playerData.IsHost}, Ping={playerData.Ping}, IsLocalUser={playerData.IsLocalUser}");
            
            // Don't destroy on scene change
            GameObject.DontDestroyOnLoad(playerObject);
            
            // NOTE: We intentionally do NOT activate the GameObject
            // This prevents NetworkPlayerData.OnEnable() from running and causing Mirror-related NullReferenceExceptions
            // The player data is still usable for reading properties like PlayerName and IsHost
            
            Debug.Log($"[LiteNetNetworkingAdapter] Created player data for '{playerName}' (host={isHost}, local={isLocal}, ping={initialPing})");
            return playerData;
        }

        /// <summary>
        /// Updates the PlayerNames array in the current lobby based on connected players.
        /// This is used for discovery responses to show all players in the lobby.
        /// </summary>
        private void UpdateLobbyPlayerNames()
        {
            if (_currentLobby == null)
            {
                return;
            }
            
            var allNames = new List<string>();
            foreach (var kvp in _connectedPlayers)
            {
                foreach (var player in kvp.Value)
                {
                    if (player != null && !string.IsNullOrEmpty(player.PlayerName))
                    {
                        allNames.Add(player.PlayerName);
                    }
                }
            }
            
            _currentLobby.PlayerNames = allNames.ToArray();
            Debug.Log($"[LiteNetNetworkingAdapter] Updated lobby PlayerNames: [{string.Join(", ", allNames)}]");
        }
        
        /// <summary>
        /// Starts the asynchronous STUN resolution to discover the public IP address.
        /// </summary>
        private void StartPublicEndpointResolution()
        {
            if (_currentLobby == null || !_isHosting)
            {
                return;
            }
            
            // Initialize resolver if needed
            if (_publicEndpointResolver == null)
            {
                _publicEndpointResolver = new PublicEndpointResolver();
                _publicEndpointResolver.EndpointResolved += OnPublicEndpointResolved;
                _publicEndpointResolver.ResolutionFailed += OnPublicEndpointFailed;
            }
            
            Debug.Log("[LiteNetNetworkingAdapter] Starting STUN resolution for public IP...");
            _ = _publicEndpointResolver.ResolveAsync(_currentLobby.Port);
        }
        
        /// <summary>
        /// Cancels any in-progress STUN resolution.
        /// </summary>
        private void CancelPublicEndpointResolution()
        {
            _publicEndpointResolver?.Cancel();
        }
        
        /// <summary>
        /// Handles successful public endpoint resolution from YARG.Net.
        /// </summary>
        private void OnPublicEndpointResolved(object sender, PublicEndpointResolvedEventArgs e)
        {
            // This may be called from a background thread, so dispatch to main thread
            UniTask.Post(() => ApplyPublicEndpoint(e.Address, e.Port));
        }
        
        /// <summary>
        /// Handles failed public endpoint resolution from YARG.Net.
        /// </summary>
        private void OnPublicEndpointFailed(object sender, PublicEndpointFailedEventArgs e)
        {
            Debug.LogWarning($"[LiteNetNetworkingAdapter] STUN resolution failed: {e.Reason}");
        }
        
        /// <summary>
        /// Applies the resolved public endpoint to the current lobby (main thread).
        /// </summary>
        private void ApplyPublicEndpoint(string address, int port)
        {
            if (_currentLobby == null)
            {
                return;
            }
            
            bool changed = false;
            
            if (!string.Equals(_currentLobby.PublicAddress, address, StringComparison.OrdinalIgnoreCase))
            {
                _currentLobby.PublicAddress = address;
                changed = true;
            }
            
            if (_currentLobby.PublicPort != port)
            {
                _currentLobby.PublicPort = port;
                changed = true;
            }
            
            if (changed)
            {
                Debug.Log($"[LiteNetNetworkingAdapter] Resolved public endpoint via STUN: {address}:{port}");
                
                // Update discovery advertising with new public address
                _discovery?.StartAdvertising(_currentLobby);
                
                // Notify listeners that lobby info changed
                OnLobbyJoined?.Invoke(_currentLobby);
            }
        }

        #endregion
        
        #region Manager Event Bridges
        
        /// <summary>
        /// Bridge from SetlistManager.SongAdded to adapter events.
        /// </summary>
        private void OnSetlistManagerSongAdded(object sender, SetlistSongEventArgs e)
        {
            Debug.Log($"[LiteNetNetworkingAdapter] Setlist song added: {e.Entry.SongName} by {e.Entry.AddedByPlayerName}");
            OnSetlistSongAdded?.Invoke(e.Entry.AddedByPlayerName, e.Entry.SongName, e.Entry.SongArtist);
            OnSetlistUpdated?.Invoke();
        }
        
        /// <summary>
        /// Bridge from SetlistManager.SongRemoved to adapter events.
        /// </summary>
        private void OnSetlistManagerSongRemoved(object sender, SetlistSongEventArgs e)
        {
            Debug.Log($"[LiteNetNetworkingAdapter] Setlist song removed: {e.Entry.SongName}");
            OnSetlistSongRemoved?.Invoke(e.Entry.AddedByPlayerName, e.Entry.SongName, e.Entry.SongArtist);
            OnSetlistUpdated?.Invoke();
        }
        
        /// <summary>
        /// Bridge from SetlistManager.SetlistCleared to adapter events.
        /// </summary>
        private void OnSetlistManagerCleared(object sender, EventArgs e)
        {
            Debug.Log("[LiteNetNetworkingAdapter] Setlist cleared");
            OnSetlistUpdated?.Invoke();
        }
        
        /// <summary>
        /// Bridge from SharedSongLibraryManager.SyncStateChanged to adapter events.
        /// </summary>
        private void OnSharedSongSyncStateChanged(object sender, SyncStateChangedEventArgs e)
        {
            Debug.Log($"[LiteNetNetworkingAdapter] Shared song sync state changed: complete={e.IsComplete}");
            OnSharedSongSyncStateChanged?.Invoke(e.IsComplete);
        }
        
        /// <summary>
        /// Bridge from UnisonCoordinator.UnisonBonusAwarded to adapter events.
        /// </summary>
        private void OnUnisonCoordinatorBonusAwarded(object sender, UnisonBonusEventArgs e)
        {
            Debug.Log($"[LiteNetNetworkingAdapter] Unison bonus awarded for phrase at {e.PhraseTime}");
            
            // Broadcast to all clients if hosting
            if (_isHosting)
            {
                BroadcastUnisonBonusAward(e.PhraseTime);
            }
            
            // Fire local event too (for host's own UI)
            OnUnisonBonusAwarded?.Invoke(e.PhraseTime);
        }
        
        /// <summary>
        /// Bridge from ScoreResultsManager.ResultReceived to adapter events.
        /// </summary>
        private void OnScoreResultsManagerResultReceived(object sender, ScoreResultEventArgs e)
        {
            Debug.Log($"[LiteNetNetworkingAdapter] Score result received: {e.Result.PlayerName} - {e.Result.Score}");
            OnScoreResultsReceived?.Invoke(
                e.Result.PlayerName,
                e.Result.IsHighScore,
                e.Result.IsFullCombo,
                e.Result.Score,
                e.Result.MaxCombo,
                e.Result.NotesHit,
                e.Result.NotesMissed);
        }
        
        #endregion
    }
}
