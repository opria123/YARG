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
using YARG.Core;
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
using YARG.Networking.Abstraction.Handlers;
using YARG.Networking.DedicatedServer;
using YARG.Networking.Gameplay;
using YARG.Networking.Session;
using YARG.Networking.Settings;
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
        // Bootstrapped networking stacks (mutually exclusive - either hosting OR connected)
        private ServerNetworkingServer _serverStack;
        private ClientNetworkingClient _clientStack;
        
        // Legacy references for gradual migration (will be removed as we migrate)
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
        
        // Handlers (encapsulate related logic into focused classes)
        private SongLibrarySyncHandler _songLibrarySyncHandler;
        private SetlistHandler _setlistHandler;
        private ReadyStateHandler _readyStateHandler;
        private UnisonSyncHandler _unisonSyncHandler;
        private ScoreResultsHandler _scoreResultsHandler;
        private GameplayStateHandler _gameplayStateHandler;
        private ConnectionHandler _connectionHandler;
        private NavigationHandler _navigationHandler;
        private AuthenticationHandler _authenticationHandler;
        private LobbyHandler _lobbyHandler;
        private PacketRouter _packetRouter;
        
        // New decomposed managers (gradual migration from monolithic adapter)
        // These provide cleaner interfaces for player, lobby, and gameplay management
        private Managers.INetworkManagers _managers;

        private LobbyInfo _currentLobby;
        private string _playerName = "Player";
        private bool _isHosting;
        private bool _isDedicatedServer;
        private bool _isConnected;
        private bool _isJoinInProgress;
        private int _maxPlayers = 32;
        private int _maxLocalPlayersPerClient = 4;
        private int _defaultPort = 7777;

        // Well-known ConnectionId for the host's players (all zeros is reserved, use a specific value)
        private static readonly Guid HostConnectionId = new Guid("00000000-0000-0000-0000-000000000001");
        
        // Player tracking - keyed by ConnectionId (Guid), not string
        private Dictionary<Guid, List<NetworkPlayerData>> _connectedPlayersByConnection = new();
        
        // Legacy player tracking (will be removed after migration)
        private Dictionary<object, List<NetworkPlayerData>> _connectedPlayers = new();
        
        // Pending connections waiting for name handshake
        private Dictionary<Guid, INetConnection> _pendingConnections = new();
        
        // Pending player identities that have been received on network thread but not yet processed on main thread
        // This prevents race conditions where disconnect happens before main thread processes the join
        private Dictionary<Guid, List<NetworkPlayerIdentity>> _pendingPlayerIdentities = new();
        private readonly object _pendingPlayerIdentitiesLock = new object();
        
        // Connection ID to connection mapping for latency updates
        private Dictionary<Guid, INetConnection> _connectionMap = new();
        
        // Lobby state tracking (host only)
        private bool _isBrowsingSongs = false;
        
        // Public endpoint resolution (STUN)
        private PublicEndpointResolver _publicEndpointResolver;
        
        // Song Library Sync (Client-side only)
        private bool _songLibraryUploaded = false;
        private int _lastUploadedSongVersion = -1;
        
        // Song Library Sync state is now in SongLibrarySyncHandler
        // (removed duplicate _playerSongLibraries, _playersPendingSongSync, _sharedSongHashes)
        private bool _sharedSongSyncComplete = false;
        private string _lobbyPassword = null;
        
        // Constants for shared song sync
        private const int MAX_SHARED_SONG_CHUNK_BYTES = 8192;
        private const int SONG_HASHES_PER_CHUNK = MAX_SHARED_SONG_CHUNK_BYTES / HashWrapper.HASH_SIZE_IN_BYTES;
        
        // Password for joining (client-side)
        private string _pendingPassword = null;
        
        // Handshake retry mechanism (client-side)
        private bool _handshakeSent = false;
        private bool _handshakeAcknowledged = false;
        private int _handshakeRetryCount = 0;
        private const int MAX_HANDSHAKE_RETRIES = 5;
        private const float HANDSHAKE_RETRY_INTERVAL = 0.5f; // seconds
        private Coroutine _handshakeRetryCoroutine = null;
        private INetConnection _serverConnection = null;
        
        // Gameplay start barrier (for waiting until all players are loaded)
        private TaskCompletionSource<bool> _gameplayStartTcs;
        private readonly object _gameplayStartLock = new object();
        private bool _allPlayersLoadedSignaled = false; // Prevents race condition with TCS recreation
        
        // Expected host player IDs from track order (client-side, used to assign IDs to dynamically created host entries)
        private List<Guid> _expectedHostPlayerIds = new();
        private int _hostPlayerIdAssignmentIndex = 0;
        
        // Pending host player ID for dedicated server clients - applied after local players are created
        // This handles the race condition where HostChanged packet arrives before local player setup completes
        private Guid _pendingHostPlayerId = Guid.Empty;
        
        // Map of player ID to player name from band assignment (client-side, used to create NetworkPlayerData for remote players)
        private Dictionary<Guid, string> _remotePlayerIdToName = new();
        
        // Session phase tracking for late join handling
        private SessionPhase _currentSessionPhase = SessionPhase.Lobby;
        private string _currentSongHash = string.Empty;
        
        // Late join tracking (host only)
        private Dictionary<Guid, LateJoinPlayerState> _pendingLateJoiners = new();
        
        // Late join state (client only)
        private LateJoinAction _myLateJoinAction = LateJoinAction.NormalJoin;
        private bool _isLateJoiner = false;
        private bool _waitingForLateJoinDecision = false;
        private string _pendingLateJoinMessage = null;
        
        // Auto-start when all players are ready (host only)
        // This provides a unified auto-start capability for both dedicated servers and in-game hosts
        private bool _autoStartOnAllReady = false;
        private bool _autoStartPending = false;
        private Coroutine _autoStartCoroutine = null;
        
        // Pending preset sync cache (for presets received before player data is established)
        private Dictionary<Guid, (Guid CameraPresetId, string CameraPresetJson, 
                                   Guid HighwayPresetId, string HighwayPresetJson,
                                   Guid ColorProfileId, string ColorProfileJson,
                                   Guid ThemePresetId, string ThemePresetJson)> _pendingPresets = new();

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
        /// Gets the local port the transport is bound to (for NAT punch registration).
        /// </summary>
        public int LocalTransportPort => _liteNetTransport?.LocalPort ?? 0;
        
        /// <summary>
        /// Whether any local player is the designated host.
        /// For dedicated servers, this is the player who can select songs.
        /// For regular lobbies, this is the same as IsHosting.
        /// </summary>
        public bool IsLocalPlayerHost
        {
            get
            {
                // If we're the server/host process, we're definitely the host
                if (_isHosting) return true;
                
                // Check if any local player has the IsHost flag set
                // This handles the dedicated server case where a client is designated as host
                if (_connectedPlayers.TryGetValue("local", out var localPlayers))
                {
                    foreach (var player in localPlayers)
                    {
                        if (player?.IsHost == true)
                            return true;
                    }
                }
                
                return false;
            }
        }
        
        /// <summary>
        /// Whether this client has authority to perform host actions.
        /// This is the unified property that should be used by menu code instead of
        /// checking IsHosting or IsLocalPlayerHost directly.
        /// 
        /// Returns true if:
        /// - In-game hosting: We are the server (IsHosting)
        /// - Dedicated server client: We are the designated host player (IsLocalPlayerHost)
        /// - Regular client: Always false
        /// 
        /// Use this for permission checks in UI code.
        /// </summary>
        public bool HasHostAuthority => IsLocalPlayerHost;
        
        /// <summary>
        /// Whether the host is currently in the song browsing state.
        /// Clients can use this to know if they should navigate to the music library.
        /// </summary>
        public bool IsBrowsingSongs => _isBrowsingSongs;
        
        /// <summary>
        /// Gets the current setlist song hashes.
        /// </summary>
        public IReadOnlyList<string> SetlistSongHashes => _setlistManager?.SongHashes ?? Array.Empty<string>();
        
        /// <summary>
        /// Gets the current session phase for late join handling.
        /// </summary>
        public SessionPhase CurrentSessionPhase => _currentSessionPhase;
        
        /// <summary>
        /// Gets whether this client is a late joiner.
        /// </summary>
        public bool IsLateJoiner => _isLateJoiner;
        
        /// <summary>
        /// Gets the late join action assigned to this client (if late joining).
        /// </summary>
        public LateJoinAction MyLateJoinAction => _myLateJoinAction;
        
        /// <summary>
        /// Gets whether we're waiting for the host to make a late join decision.
        /// </summary>
        public bool IsWaitingForLateJoinDecision => _waitingForLateJoinDecision;
        
        /// <summary>
        /// Gets the pending late join message (if any). Used for UI to check on startup.
        /// </summary>
        public string PendingLateJoinMessage => _pendingLateJoinMessage;
        
        /// <summary>
        /// Gets the connection to the server (client-side only).
        /// Returns null if not connected or if hosting.
        /// </summary>
        private INetConnection GetServerConnection()
        {
            if (_isHosting) return null;
            return _serverConnection;
        }
        
        /// <summary>
        /// Clears the pending late join message after it has been handled by the UI.
        /// </summary>
        public void ClearPendingLateJoinMessage()
        {
            _pendingLateJoinMessage = null;
        }

        #endregion

        #region Events

        public event Action<LobbyInfo> OnLobbyCreated;
        public event Action<LobbyInfo> OnLobbyJoined;
        public event Action OnLobbyLeft;
        public event Action<List<LobbyInfo>> OnLobbyListUpdated;
        public event Action<NetworkPlayerData> OnPlayerJoined;
        public event Action<NetworkPlayerData> OnPlayerLeft;
        
        /// <summary>
        /// Fired when multiple players from the same connection join.
        /// The list contains all players from that connection (e.g., multiple local profiles).
        /// Use this for clustering players by connection in the track order.
        /// </summary>
        public event Action<List<NetworkPlayerData>> OnPlayersJoinedFromConnection;
        
        public event Action<string> OnNetworkError;
        public event Action<string, int, byte, int, bool, bool, bool, bool, bool, List<int>, bool> OnSessionSettingsReceived;
        
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
        /// Fired when the host player changes (dedicated server mode).
        /// Parameter: newHostPlayerId - the NetworkPlayerId of the new host.
        /// </summary>
        public event Action<Guid> OnHostChanged;
        
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
        /// Parameters: bandId - the band that completed the unison, phraseTime - the time of the unison phrase
        /// </summary>
        public event Action<int, double> OnUnisonBonusAwarded;
        
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
        
        /// <summary>
        /// Fired when track order is received from host.
        /// Parameter: ordered list of player IDs.
        /// </summary>
        public event Action<List<Guid>> OnTrackOrderReceived;
        
        /// <summary>
        /// Fired when NAT punch succeeds on the transport.
        /// Parameters: targetEndPoint, addressType, token
        /// </summary>
        public event Action<System.Net.IPEndPoint, LiteNetLib.NatAddressType, string> OnNatPunchSuccess;

        #endregion

        #region Configuration

        /// <summary>
        /// Set whether this instance will operate as a dedicated server.
        /// Must be called before CreateLobby() to take effect.
        /// </summary>
        /// <param name="isDedicated">True for dedicated server mode</param>
        public void SetDedicatedServerMode(bool isDedicated)
        {
            NetworkLogger.Info($"Setting dedicated server mode: {isDedicated}");
            _isDedicatedServer = isDedicated;
        }

        /// <summary>
        /// Set the port to use for hosting.
        /// Must be called before CreateLobby() to take effect.
        /// </summary>
        /// <param name="port">The port number to listen on</param>
        public void SetServerPort(int port)
        {
            NetworkLogger.Info($"Setting server port: {port}");
            _defaultPort = port;
            _discovery?.SetDiscoveryPort(port);
        }

        #endregion

        #region Unified Host Actions (Message-Based Pattern)
        
        // These methods implement the unified message-based pattern where ALL host actions
        // go through the same code path regardless of hosting mode:
        // - In-game hosting: Request is processed locally by server logic
        // - Dedicated server: Request is sent to server, validated, and executed
        //
        // The pattern is: Client → Request → Server (validates authority) → Execute → Broadcast
        // When hosting, the "request" is processed locally as if it came from self.
        
        /// <inheritdoc/>
        public void NavigateToMusicLibrary()
        {
            if (!HasHostAuthority)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] NavigateToMusicLibrary called without host authority");
                return;
            }
            
            // Prepare common state changes (done on the requester side)
            PrepareNavigateToMusicLibrary();
            
            // Route the request through the unified pattern
            RouteHostAction(HostActionType.NavigateToMusicLibrary, null);
        }
        
        /// <inheritdoc/>
        public void NavigateToLobbyRoom()
        {
            if (!HasHostAuthority)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] NavigateToLobbyRoom called without host authority");
                return;
            }
            
            SetBrowsingState(false);
            RouteHostAction(HostActionType.NavigateToLobbyRoom, null);
        }
        
        /// <inheritdoc/>
        public void PopAllPlayersMenu()
        {
            if (!HasHostAuthority)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] PopAllPlayersMenu called without host authority");
                return;
            }
            
            RouteHostAction(HostActionType.PopMenu, null);
        }
        
        /// <inheritdoc/>
        public void StartShow()
        {
            if (!HasHostAuthority)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] StartShow called without host authority");
                return;
            }
            
            RouteHostAction(HostActionType.StartShow, null);
        }
        
        /// <inheritdoc/>
        public void RestartGameplay()
        {
            if (!HasHostAuthority)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] RestartGameplay called without host authority");
                return;
            }
            
            RouteHostAction(HostActionType.RestartGameplay, null);
        }
        
        /// <inheritdoc/>
        public void QuitToLibrary()
        {
            if (!HasHostAuthority)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] QuitToLibrary called without host authority");
                return;
            }
            
            RouteHostAction(HostActionType.QuitToLibrary, null);
        }
        
        /// <summary>
        /// Prepares state for navigating to music library (clears setlist, resets show state).
        /// Called before routing the navigation request.
        /// </summary>
        private void PrepareNavigateToMusicLibrary()
        {
            // Clear the setlist when navigating to music library
            if (_setlistManager.Count > 0)
            {
                _setlistManager.Clear();
                OnSetlistUpdated?.Invoke();
                NetworkLogger.Info("Cleared setlist when navigating to music library");
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
        }
        
        /// <summary>
        /// Host action types for the unified routing system.
        /// </summary>
        private enum HostActionType : byte
        {
            NavigateToMusicLibrary = 1,
            NavigateToLobbyRoom = 2,
            PopMenu = 3,
            StartShow = 4,
            RestartGameplay = 5,
            QuitToLibrary = 6,
            AdvanceScoreScreen = 7,
        }
        
        /// <summary>
        /// Routes a host action through the unified message-based pattern.
        /// - If hosting: Executes locally via ExecuteHostAction
        /// - If client with authority: Sends request packet to server
        /// </summary>
        private void RouteHostAction(HostActionType action, byte[] payload)
        {
            if (_isHosting)
            {
                // We ARE the server - execute directly
                NetworkLogger.Info($"[RouteHostAction] Executing {action} locally (we are the server)");
                ExecuteHostAction(action, payload);
            }
            else
            {
                // We're a client - send request to server
                SendHostActionRequest(action, payload);
            }
        }
        
        /// <summary>
        /// Sends a host action request packet to the server.
        /// </summary>
        private void SendHostActionRequest(HostActionType action, byte[] payload)
        {
            if (!_isConnected || _serverConnection == null)
            {
                NetworkLogger.Warn($"[SendHostActionRequest] Cannot send {action} - not connected to server");
                return;
            }
            
            // Map action type to existing packet types for backwards compatibility
            // This allows the existing packet handlers to work
            switch (action)
            {
                case HostActionType.NavigateToMusicLibrary:
                    var musicLibPacket = NavigationBinaryPackets.BuildNavigationRequestPacket(MenuTarget.MusicLibrary);
                    _serverConnection.Send(musicLibPacket, ChannelType.ReliableOrdered);
                    break;
                    
                case HostActionType.NavigateToLobbyRoom:
                    var lobbyPacket = NavigationBinaryPackets.BuildNavigationRequestPacket(MenuTarget.LobbyRoom);
                    _serverConnection.Send(lobbyPacket, ChannelType.ReliableOrdered);
                    break;
                    
                case HostActionType.PopMenu:
                    var popPacket = NavigationBinaryPackets.BuildNavigationRequestPacket(MenuTarget.PopMenu);
                    _serverConnection.Send(popPacket, ChannelType.ReliableOrdered);
                    break;
                    
                case HostActionType.StartShow:
                    var startPacket = SetlistBinaryPackets.BuildStartRequestPacket();
                    _serverConnection.Send(startPacket, ChannelType.ReliableOrdered);
                    break;
                    
                case HostActionType.RestartGameplay:
                    // Use GameplayRestart packet type for this (reusing existing packet type)
                    var restartPacket = new byte[] { (byte)PacketType.GameplayRestart };
                    _serverConnection.Send(restartPacket, ChannelType.ReliableOrdered);
                    break;
                    
                case HostActionType.QuitToLibrary:
                    // Use QuitToLibrary packet type for this (reusing existing packet type)
                    var quitPacket = new byte[] { (byte)PacketType.QuitToLibrary };
                    _serverConnection.Send(quitPacket, ChannelType.ReliableOrdered);
                    break;
                    
                case HostActionType.AdvanceScoreScreen:
                    var advancePacket = ScoreBinaryPackets.BuildAdvanceRequestPacket();
                    _serverConnection.Send(advancePacket, ChannelType.ReliableOrdered);
                    break;
            }
            
            NetworkLogger.Info($"[SendHostActionRequest] Sent {action} request to server");
        }
        
        /// <summary>
        /// Executes a host action on the server side.
        /// This is called either directly (when hosting) or from a request handler (when processing client requests).
        /// </summary>
        private void ExecuteHostAction(HostActionType action, byte[] payload)
        {
            NetworkLogger.Info($"[ExecuteHostAction] Executing {action}");
            
            switch (action)
            {
                case HostActionType.NavigateToMusicLibrary:
                    SetBrowsingState(true);
                    BroadcastNavigateToMenu(MenuTarget.MusicLibrary);
                    break;
                    
                case HostActionType.NavigateToLobbyRoom:
                    SetBrowsingState(false);
                    BroadcastNavigateToMenu(MenuTarget.LobbyRoom);
                    break;
                    
                case HostActionType.PopMenu:
                    BroadcastNavigateToMenu(MenuTarget.PopMenu);
                    break;
                    
                case HostActionType.StartShow:
                    StartShowInternal();
                    break;
                    
                case HostActionType.RestartGameplay:
                    BroadcastRestartGameplay();
                    break;
                    
                case HostActionType.QuitToLibrary:
                    BroadcastQuitToLibrary();
                    break;
                    
                case HostActionType.AdvanceScoreScreen:
                    AdvanceAfterScoreScreenInternal();
                    break;
            }
        }
        
        /// <summary>
        /// Validates that the given connection has host authority on the server.
        /// </summary>
        /// <param name="connection">The connection to validate. Null means local (server itself).</param>
        /// <returns>True if the connection has host authority.</returns>
        private bool ValidateHostAuthority(INetConnection connection)
        {
            // If connection is null, it's a local call (server calling itself) - always allowed
            if (connection == null)
            {
                return true;
            }
            
            // For dedicated servers, check if the sender is the designated host
            if (_isDedicatedServer)
            {
                var dedicatedManager = DedicatedServerManager.Instance;
                if (dedicatedManager == null)
                {
                    NetworkLogger.Warn("[ValidateHostAuthority] Dedicated server manager not available");
                    return false;
                }
                
                // Find the player associated with this connection
                var allPlayers = GetAllPlayers();
                foreach (var player in allPlayers)
                {
                    if (player?.ConnectionId == connection.Id && player.IsHost)
                    {
                        return true;
                    }
                }
                
                NetworkLogger.Warn($"[ValidateHostAuthority] Connection {connection.EndPoint} is not the designated host");
                return false;
            }
            
            // For regular hosting, only the server itself can execute host actions
            // (client requests should not reach here in regular hosting mode)
            return false;
        }
        
        #endregion

        #region Initialization

        public void Initialize()
        {
            NetworkLogger.Info(" Initializing YARG.Networking (LiteNetLib implementation)");
            
            // Set up transport layer logging to Unity
            YARG.Net.Transport.TransportLogger.LogAction = (msg) => NetworkLogger.Info(msg);

            try
            {
                // Create the transport layer (LiteNetLib)
                _liteNetTransport = new LiteNetLibTransport();
                _transport = _liteNetTransport;
                
                // Subscribe to NAT punch events
                SubscribeToNatPunchEvents();
                
                // Create discovery system
                _discovery = new LiteNetDiscovery();
                _discovery.SetDiscoveryPort(_defaultPort);
                _discovery.OnLobbyDiscovered += HandleLobbyDiscovered;
                _discovery.OnLobbyLost += HandleLobbyLost;
                
                // Hook transport's unconnected messages for discovery
                _liteNetTransport.OnUnconnectedMessage += HandleUnconnectedMessage;

                // Create serializer
                _serializer = new NewtonsoftNetSerializer();

                // Create session manager (used by lobby state manager)
                _sessionManager = new SessionManager();
                
                // Create YARG.Net managers (shared logic for client/server/dedicated server)
                _setlistManager = new SetlistManager();
                _sharedSongLibraryManager = new SharedSongLibraryManager();
                _lobbyAuthenticator = new LobbyAuthenticator();
                _unisonCoordinator = new UnisonCoordinator();
                _scoreResultsManager = new ScoreResultsManager();
                
                // Create handlers (encapsulate related logic into focused classes)
                _songLibrarySyncHandler = new SongLibrarySyncHandler();
                _setlistHandler = new SetlistHandler(_setlistManager);
                _readyStateHandler = new ReadyStateHandler();
                _unisonSyncHandler = new UnisonSyncHandler();
                _scoreResultsHandler = new ScoreResultsHandler();
                _gameplayStateHandler = new GameplayStateHandler();
                _connectionHandler = new ConnectionHandler();
                _navigationHandler = new NavigationHandler();
                _authenticationHandler = new AuthenticationHandler(_lobbyAuthenticator);
                _lobbyHandler = new LobbyHandler(_discovery, _defaultPort, (name, isHost, isLocal) => CreateLiteNetPlayerData(name, isHost, isLocal));
                _packetRouter = new PacketRouter(() => _isHosting);
                
                // Initialize new decomposed managers for gradual migration
                _managers = new Managers.NetworkManagers(
                    _discovery,
                    _defaultPort,
                    (name, isHost, isLocal, instrument, difficulty, connectionId) => 
                        CreateLiteNetPlayerData(name, isHost, isLocal, instrument, difficulty, connectionId));
                
                // Wire up new handler events
                _connectionHandler.OnPlayerJoined += player => OnPlayerJoined?.Invoke(player);
                _connectionHandler.OnPlayerLeft += player => OnPlayerLeft?.Invoke(player);
                _navigationHandler.OnBrowsingStateChanged += browsing => 
                {
                    _isBrowsingSongs = browsing;
                    OnBrowsingStateChanged?.Invoke(browsing);
                };
                _lobbyHandler.OnLobbyCreated += lobby => OnLobbyCreated?.Invoke(lobby);
                _lobbyHandler.OnLobbyJoined += lobby => OnLobbyJoined?.Invoke(lobby);
                _lobbyHandler.OnLobbyLeft += () => OnLobbyLeft?.Invoke();
                _lobbyHandler.OnNetworkError += error => OnNetworkError?.Invoke(error);
                _authenticationHandler.OnAuthenticationFailed += error => OnAuthenticationFailed?.Invoke(error);
                
                // Wire up handler events to adapter events
                _songLibrarySyncHandler.OnSyncStateChanged += HandleSongLibrarySyncStateChanged;
                _setlistHandler.OnSetlistUpdated += () => OnSetlistUpdated?.Invoke();
                _readyStateHandler.OnPlayerReadyStateChanged += (name, ready) => OnPlayerReadyStateChanged?.Invoke(name, ready);
                _readyStateHandler.OnAllPlayersReady += () =>
                {
                    OnAllPlayersReady?.Invoke();
                    HandleAutoStartOnAllReady(); // Unified auto-start handling
                };
                _unisonSyncHandler.OnBonusAwarded += (phraseTime, bandId) => OnUnisonBonusAwarded?.Invoke(bandId, phraseTime);
                _scoreResultsHandler.OnResultReceived += (name, isHighScore, isFC, score, combo, hit, missed) => 
                    OnScoreResultsReceived?.Invoke(name, isHighScore, isFC, score, combo, hit, missed);
                
                // Wire up manager events to adapter events
                _setlistManager.SongAdded += OnSetlistManagerSongAdded;
                _setlistManager.SongRemoved += OnSetlistManagerSongRemoved;
                _setlistManager.SetlistCleared += OnSetlistManagerCleared;
                _sharedSongLibraryManager.SyncStateChanged += HandleSharedSongSyncStateChanged;
                _unisonCoordinator.UnisonBonusAwarded += OnUnisonCoordinatorBonusAwarded;
                _scoreResultsManager.ResultReceived += OnScoreResultsManagerResultReceived;
                
                // Subscribe to transport events for server-side peer tracking
                _transport.OnPeerConnected += OnTransportPeerConnected;
                _transport.OnPeerDisconnected += OnTransportPeerDisconnected;
                _transport.OnPayloadReceived += OnTransportPayloadReceived;
                
                // Subscribe to latency updates for ping tracking
                _liteNetTransport.OnLatencyUpdate += OnTransportLatencyUpdate;
                
                // Note: Client and server runtimes are created lazily when CreateLobby() or JoinLobby() is called
                // via ServerNetworkingBootstrapper or ClientNetworkingBootstrapper respectively

                NetworkLogger.Info(" Successfully initialized YARG.Networking");
            }
            catch (Exception ex)
            {
                NetworkLogger.Error($"[LiteNetNetworkingAdapter] Failed to initialize: {ex.Message}");
                OnNetworkError?.Invoke($"Initialization failed: {ex.Message}");
            }
        }

        public void Shutdown()
        {
            NetworkLogger.Info(" Shutting down");

            try
            {
                // Cancel any pending STUN resolution
                CancelPublicEndpointResolution();
                
                // Unsubscribe from client events
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
                
                // Unsubscribe from NAT punch events
                UnsubscribeFromNatPunchEvents();
                
                // Unsubscribe from manager events
                if (_setlistManager != null)
                {
                    _setlistManager.SongAdded -= OnSetlistManagerSongAdded;
                    _setlistManager.SongRemoved -= OnSetlistManagerSongRemoved;
                    _setlistManager.SetlistCleared -= OnSetlistManagerCleared;
                }
                if (_sharedSongLibraryManager != null)
                {
                    _sharedSongLibraryManager.SyncStateChanged -= HandleSharedSongSyncStateChanged;
                }
                if (_unisonCoordinator != null)
                {
                    _unisonCoordinator.UnisonBonusAwarded -= OnUnisonCoordinatorBonusAwarded;
                }
                if (_scoreResultsManager != null)
                {
                    _scoreResultsManager.ResultReceived -= OnScoreResultsManagerResultReceived;
                }
                
                // Clean up handlers
                _songLibrarySyncHandler?.Dispose();
                _songLibrarySyncHandler = null;
                _setlistHandler = null;
                _readyStateHandler = null;
                _unisonSyncHandler = null;
                _scoreResultsHandler = null;
                _connectionHandler?.Dispose();
                _connectionHandler = null;
                _navigationHandler?.Dispose();
                _navigationHandler = null;
                _authenticationHandler?.Dispose();
                _authenticationHandler = null;
                _lobbyHandler?.Dispose();
                _lobbyHandler = null;
                _packetRouter?.Dispose();
                _packetRouter = null;
                
                // Dispose new decomposed managers
                _managers?.Dispose();
                _managers = null;
                
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

                // Stop server stack if hosting
                if (_serverStack != null)
                {
                    _ = _serverStack.Runtime.StopAsync("Adapter shutdown");
                    _serverStack = null;
                }
                
                // Stop client stack if connected
                if (_clientStack != null)
                {
                    _ = _clientStack.Runtime.DisconnectAsync("Adapter shutdown");
                    _clientStack = null;
                }

                // Stop transport
                _transport?.Shutdown();
                
                // Clear legacy references
                _serverRuntime = null;
                _clientRuntime = null;
                _packetDispatcher = null;

                // Clear state
                _isHosting = false;
                _isConnected = false;
                _isJoinInProgress = false;
                _currentLobby = null;
                _connectedPlayers.Clear();
                _connectedPlayersByConnection.Clear();
                
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
                NetworkLogger.Error($"[LiteNetNetworkingAdapter] Error during shutdown: {ex.Message}");
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
                NetworkLogger.Info($"Ignoring client Connected event while hosting (peer: {e.Connection?.EndPoint})");
                return;
            }
            
            NetworkLogger.Info($"Connected to server: {e.Connection?.EndPoint}");
            _isConnected = true;
            _isJoinInProgress = false;
            
            // Treat connection as "joined" - lobby info is set before connecting via JoinLobby
            // The server will send updated player/lobby state via the packet handlers
            if (_currentLobby != null)
            {
                // Dispatch to main thread for GameObject creation
                UnityMainThreadDispatcher.EnqueueAction(() =>
                {
                    _connectedPlayers.Clear();
                    _connectedPlayersByConnection.Clear();
                    
                    // Clear any stale track order from previous sessions
                    var trackOrderManager = Tracks.TrackOrderManager.Instance;
                    if (trackOrderManager != null)
                    {
                        trackOrderManager.Clear();
                        NetworkLogger.Info("Cleared TrackOrderManager for new lobby connection");
                    }
                    
                    // For dedicated servers, don't add a phantom host player - the server has no host player.
                    // The actual host role will be assigned to the first connecting player by the server.
                    if (!_currentLobby.IsDedicatedServer)
                    {
                        // Add the host player first (we know there's a host from the lobby info)
                        // Use the lobby's HostName if available, otherwise use a placeholder "Unknown"
                        // NOTE: Host's NetworkPlayerId will be set when we receive the track order
                        string hostName = !string.IsNullOrEmpty(_currentLobby.HostName) ? _currentLobby.HostName : "Unknown";
                        var hostPlayerData = CreateLiteNetPlayerData(hostName, isHost: true, isLocal: false, connectionId: HostConnectionId);
                        _connectedPlayers["host"] = new List<NetworkPlayerData> { hostPlayerData };
                        _connectedPlayersByConnection[HostConnectionId] = new List<NetworkPlayerData> { hostPlayerData };
                        NetworkLogger.Info($"Added host player '{hostName}' to connected players (ConnectionId={HostConnectionId})");
                    }
                    else
                    {
                        NetworkLogger.Info("Connecting to dedicated server - no phantom host player added");
                    }
                    
                    // Generate a unique ConnectionId for this client's local players
                    // On the client side, we use the SessionId from LocalPlayerIdentity as our ConnectionId
                    // This will be different from the host's ConnectionId and from other clients
                    var localConnectionId = Net.LocalPlayerIdentity.SessionId;
                    
                    // Add ALL local players (the clients connecting)
                    var localIdentities = GetAllLocalPlayerIdentities();
                    var localPlayerList = new List<NetworkPlayerData>();
                    
                    foreach (var identity in localIdentities)
                    {
                        // Find the matching local profile for this identity to get instrument/difficulty
                        // Note: identity.DisplayName is the sanitized profile name, so we compare by name
                        // (identity.PlayerId is a combined GUID, not the raw profile.Id)
                        var localPlayers = PlayerContainer.Players;
                        int instrument = 0;
                        int difficulty = 0;
                        bool foundProfile = false;
                        
                        if (localPlayers != null)
                        {
                            foreach (var player in localPlayers)
                            {
                                string sanitizedProfileName = SanitizeDisplayName(player.Profile?.Name ?? "");
                                if (sanitizedProfileName == identity.DisplayName)
                                {
                                    instrument = (int)player.Profile.CurrentInstrument;
                                    difficulty = (int)player.Profile.CurrentDifficulty;
                                    foundProfile = true;
                                    NetworkLogger.Info($"Matched profile '{sanitizedProfileName}' -> instrument={(Instrument)instrument}, difficulty={(Difficulty)difficulty}");
                                    break;
                                }
                            }
                        }
                        
                        if (!foundProfile)
                        {
                            NetworkLogger.Warn($"Could not find matching profile for identity '{identity.DisplayName}' - using default instrument");
                        }
                        
                        var localPlayerData = CreateLiteNetPlayerData(identity.DisplayName, isHost: false, isLocal: true, instrument, difficulty, connectionId: localConnectionId);
                        localPlayerData.NetworkPlayerId = identity.PlayerId;
                        localPlayerList.Add(localPlayerData);
                        
                        NetworkLogger.Info($"Added local client player '{identity.DisplayName}' with NetworkPlayerId={identity.PlayerId}, ConnectionId={localConnectionId}");
                    }
                    
                    _connectedPlayers["local"] = localPlayerList;
                    _connectedPlayersByConnection[localConnectionId] = localPlayerList;
                    NetworkLogger.Info($"Added {localPlayerList.Count} local client player(s) to connected players");
                    
                    // Apply pending host player ID if we received HostChanged before local players were set up
                    // This handles the dedicated server race condition
                    ApplyPendingHostPlayerId();
                    
                    NetworkLogger.Info($"Client connected, firing OnLobbyJoined for lobby: {_currentLobby.LobbyName}");
                    OnLobbyJoined?.Invoke(_currentLobby);
                    
                    // NOTE: Initial player state (instrument/difficulty) is sent in SendPlayerIdentityToServer
                    // after the handshake completes, so the host has created our player data first.
                    
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
                NetworkLogger.Info($"Ignoring client Disconnected event while hosting (connection: {e.Connection?.Id})");
                return;
            }
            
            NetworkLogger.Info($"Disconnected from server. Connection: {e.Connection.Id}");
            _isConnected = false;
            _isJoinInProgress = false;
            
            // Stop the client runtime to ensure transport is properly shutdown
            // This allows reconnection attempts to work
            try
            {
                _clientRuntime?.DisconnectAsync().Wait(TimeSpan.FromMilliseconds(500));
            }
            catch (Exception ex)
            {
                NetworkLogger.Warn($"[LiteNetNetworkingAdapter] Error stopping client runtime: {ex.Message}");
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
                _connectedPlayersByConnection.Clear();
                _connectionMap.Clear();
                _currentLobby = null;
                _pendingHostPlayerId = Guid.Empty; // Clear pending host ID on disconnect
                
                OnLobbyLeft?.Invoke();
            });
        }

        private void OnClientHandshakeCompleted(object sender, ClientHandshakeCompletedEventArgs e)
        {
            NetworkLogger.Info($"Handshake completed with session ID: {e.SessionId}");

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
            NetworkLogger.Info($"[OnTransportPeerConnected] Added connection {connection.Id} to _connectionMap (now has {_connectionMap.Count} entries). IsHosting={_isHosting}");
            
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
            
            NetworkLogger.Server($"Client connected - {connection.EndPoint}");
            
            // Store connection as pending - we'll create player data when we receive their name
            _pendingConnections[connection.Id] = connection;
            NetworkLogger.Server($"Stored pending connection {connection.Id}, waiting for player name");
            
            // NOTE: Don't update CurrentPlayers here - we don't know how many profiles yet
            // CurrentPlayers is updated in HandlePlayerIdentityMessage when we receive the actual player identities
            
            // Show toast notification that someone is connecting (even before we know their name)
            if (_currentLobby != null)
            {
                string endpoint = connection.EndPoint ?? "Unknown";
                UnityMainThreadDispatcher.EnqueueAction(() =>
                {
                    YARG.Menu.Persistent.ToastManager.ToastInformation($"Player connecting from {endpoint}...");
                });
            }
        }
        
        private void SendPlayerIdentityToServer(INetConnection connection)
        {
            // NOTE: This method may be called from main thread (via HandleAuthResponseMessage's EnqueueAction)
            // or from network thread (via OnClientConnected for no-password lobbies).
            // We use EnsureMainThread to handle both cases without double-queuing.
            
            NetworkLogger.Info($"[SendPlayerIdentityToServer] Entry - connection={connection?.Id}, endpoint={connection?.EndPoint}, IsMainThread={UnityMainThreadDispatcher.IsMainThread}");
            
            // Store connection for retries
            _serverConnection = connection;
            _handshakeSent = false;
            _handshakeAcknowledged = false;
            _handshakeRetryCount = 0;
            
            void DoSendIdentity()
            {
                NetworkLogger.Info($"[SendPlayerIdentityToServer.DoSendIdentity] Starting - connection={connection?.Id}, retryCount={_handshakeRetryCount}");
                
                // Get identities for ALL local players
                var identities = GetAllLocalPlayerIdentities();
                NetworkLogger.Info($"Client: Sending {identities.Count} player identities to server");
                
                foreach (var identity in identities)
                {
                    NetworkLogger.Info($"  - {identity.DisplayName} (PlayerId: {identity.PlayerId})");
                }
                
                try
                {
                    byte[] message = HandshakeBinaryPackets.BuildMultiPlayerRequestPacket(identities);
                    
                    // Debug: Verify packet type byte is correct (should be 1 for HandshakeRequest)
                    NetworkLogger.Info($"Client: Built HandshakeRequest packet - length={message.Length}, firstByte={message[0]} (expected=1)");
                    NetworkLogger.Info($"Client: Connection endpoint={connection.EndPoint}, connectionId={connection.Id}");
                    
                    // Also verify this matches the connection in _connectionMap
                    connection.Send(message, ChannelType.ReliableOrdered);
                    NetworkLogger.Info($"Client: Sent handshake ({message.Length} bytes, {identities.Count} players)");
                    
                    _handshakeSent = true;
                    
                    // Send initial player state (instrument/difficulty) for ALL local players
                    var localPlayers = PlayerContainer.Players;
                    if (localPlayers != null)
                    {
                        foreach (var localPlayer in localPlayers)
                        {
                            if (localPlayer?.Profile == null) continue;
                            
                            string playerName = localPlayer.Profile.Name;
                            int instrumentValue = (int)localPlayer.Profile.CurrentInstrument;
                            int difficultyValue = (int)localPlayer.Profile.CurrentDifficulty;
                            
                            var stateMessage = _readyStateHandler.BuildClientReadyMessage(playerName, false, instrumentValue, difficultyValue);
                            connection.Send(stateMessage, ChannelType.ReliableOrdered);
                            NetworkLogger.Client($"Sent initial state for '{playerName}': instrument={instrumentValue}, difficulty={difficultyValue}");
                        }
                    }
                    
                    // Start retry coroutine (only on first send)
                    if (_handshakeRetryCount == 0)
                    {
                        StartHandshakeRetryCoroutine();
                    }
                }
                catch (Exception ex)
                {
                    NetworkLogger.Error($"[LiteNetNetworkingAdapter] Client: Failed to send player identities: {ex.Message}\n{ex.StackTrace}");
                }
            }
            
            // Check if we're already on the main thread
            if (UnityMainThreadDispatcher.IsMainThread)
            {
                NetworkLogger.Info($"[SendPlayerIdentityToServer] Already on main thread, calling DoSendIdentity directly");
                DoSendIdentity();
            }
            else
            {
                NetworkLogger.Info($"[SendPlayerIdentityToServer] Not on main thread, enqueueing DoSendIdentity");
                UnityMainThreadDispatcher.EnqueueAction(DoSendIdentity);
            }
        }
        
        private void StartHandshakeRetryCoroutine()
        {
            // Need to run on a MonoBehaviour - use GlobalVariables instance
            if (_handshakeRetryCoroutine != null)
            {
                return; // Already running
            }
            
            NetworkLogger.Info("[Client] Starting handshake retry coroutine");
            _handshakeRetryCoroutine = GlobalVariables.Instance?.StartCoroutine(HandshakeRetryRoutine());
        }
        
        private System.Collections.IEnumerator HandshakeRetryRoutine()
        {
            while (!_handshakeAcknowledged && _handshakeRetryCount < MAX_HANDSHAKE_RETRIES)
            {
                yield return new UnityEngine.WaitForSeconds(HANDSHAKE_RETRY_INTERVAL);
                
                // Check if we've been acknowledged or disconnected
                if (_handshakeAcknowledged || !IsConnected || _serverConnection == null)
                {
                    NetworkLogger.Info($"[Client] Handshake retry stopping - acknowledged={_handshakeAcknowledged}, connected={IsConnected}");
                    break;
                }
                
                _handshakeRetryCount++;
                NetworkLogger.Warn($"[Client] Handshake not acknowledged, retrying ({_handshakeRetryCount}/{MAX_HANDSHAKE_RETRIES})");
                
                // Resend the handshake
                try
                {
                    var identities = GetAllLocalPlayerIdentities();
                    byte[] message = HandshakeBinaryPackets.BuildMultiPlayerRequestPacket(identities);
                    _serverConnection.Send(message, ChannelType.ReliableOrdered);
                    NetworkLogger.Info($"[Client] Resent HandshakeRequest - length={message.Length}, retryCount={_handshakeRetryCount}");
                }
                catch (Exception ex)
                {
                    NetworkLogger.Error($"[Client] Failed to resend handshake: {ex.Message}");
                }
            }
            
            if (!_handshakeAcknowledged && _handshakeRetryCount >= MAX_HANDSHAKE_RETRIES)
            {
                NetworkLogger.Error($"[Client] Handshake failed after {MAX_HANDSHAKE_RETRIES} retries");
            }
            
            _handshakeRetryCoroutine = null;
        }
        
        private void OnTransportPayloadReceived(INetConnection connection, ReadOnlyMemory<byte> payload, ChannelType channel)
        {
            if (payload.Length < 1) return;
            
            var packetType = (PacketType)payload.Span[0];
            
            // Per-packet logging - verbose only (stripped in release builds)
            NetworkLogger.Verbose($"[Packet] Received {packetType} ({payload.Length} bytes) from {connection?.EndPoint}");
            
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
                case PacketType.HostChanged when !_isHosting:
                    HandleHostChangedMessage(payload);
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
                case PacketType.SessionPresetSync when !_isHosting:
                    HandleSessionSettingsSyncMessage(payload);
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
                case PacketType.TrackOrder when !_isHosting:
                    HandleTrackOrderMessage(payload);
                    break;
                case PacketType.GameplayLoadReady when _isHosting:
                    HandleGameplayLoadReadyMessage(connection, payload);
                    break;
                case PacketType.GameplayAllLoadReady when !_isHosting:
                    HandleGameplayAllLoadReadyMessage();
                    break;
                case PacketType.IdentityRequest when !_isHosting:
                    HandleIdentityRequestMessage(connection);
                    break;
                case PacketType.BandAssignment when !_isHosting:
                    HandleBandAssignmentMessage(payload);
                    break;
                case PacketType.BandNameChange when !_isHosting:
                    HandleBandNameChangeMessage(payload);
                    break;
                case PacketType.BandNameChangeRequest when _isHosting:
                    HandleBandNameChangeRequestMessage(connection, payload);
                    break;
                case PacketType.BandScoreUpdate when _isHosting:
                    HandleBandScoreUpdateMessage(connection, payload);
                    break;
                case PacketType.BandScoreUpdate when !_isHosting:
                    HandleBandScoreUpdateBroadcast(payload);
                    break;
                case PacketType.BandFailed when _isHosting:
                    HandleBandFailedMessage(connection, payload);
                    break;
                case PacketType.BandFailed when !_isHosting:
                    HandleBandFailedBroadcast(payload);
                    break;
                case PacketType.PlayerPresetSync when _isHosting:
                    HandlePlayerPresetSyncFromClient(connection, payload);
                    break;
                case PacketType.PlayerPresetSync when !_isHosting:
                    HandlePlayerPresetSyncBroadcast(payload);
                    break;
                    
                // Late Join packets
                case PacketType.LateJoinState when !_isHosting:
                    HandleLateJoinStateMessage(payload);
                    break;
                case PacketType.LateJoinSongCheckResponse when _isHosting:
                    HandleLateJoinSongCheckResponseMessage(connection, payload);
                    break;
                case PacketType.LateJoinAction when !_isHosting:
                    HandleLateJoinActionMessage(payload);
                    break;
                case PacketType.SetlistAbort when !_isHosting:
                    HandleSetlistAbortMessage(payload);
                    break;
                    
                // Navigation request from designated host (dedicated server mode)
                case PacketType.NavigationRequest when _isHosting:
                    HandleNavigationRequestMessage(connection, payload);
                    break;
                    
                // Setlist start request from designated host (dedicated server mode)
                case PacketType.SetlistStartRequest when _isHosting:
                    HandleSetlistStartRequestMessage(connection);
                    break;
                    
                // Score screen advance request from designated host (dedicated server mode)
                case PacketType.ScoreScreenAdvanceRequest when _isHosting:
                    HandleScoreScreenAdvanceRequestMessage(connection);
                    break;
            }
        }
        
        private void HandlePlayerIdentityMessage(INetConnection connection, ReadOnlyMemory<byte> payload)
        {
            NetworkLogger.Info($"HandlePlayerIdentityMessage called, payload length={payload.Length}");
            
            // Try to parse as multi-player packet first
            if (!HandshakeBinaryPackets.TryParseMultiPlayerRequestPacket(payload.Span, out List<NetworkPlayerIdentity>? identities) || identities == null || identities.Count == 0)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Server: Invalid player identity message");
                return;
            }
            
            try
            {
                NetworkLogger.Info($"Server: Received {identities.Count} player(s) from {connection.EndPoint}");
                foreach (var id in identities)
                {
                    NetworkLogger.Info($"  - {id.DisplayName} (ID: {id.PlayerId})");
                }
                
                // Remove from pending connections
                _pendingConnections.Remove(connection.Id);
                
                // Track pending identities SYNCHRONOUSLY on network thread
                // This prevents race conditions where disconnect happens before main thread processes the join
                lock (_pendingPlayerIdentitiesLock)
                {
                    _pendingPlayerIdentities[connection.Id] = new List<NetworkPlayerIdentity>(identities);
                    NetworkLogger.Info($"Server: Tracking {identities.Count} pending player identities for connection {connection.Id}");
                }
                
                // Create player data on main thread
                // Use the connection's Guid as the ConnectionId for all players from this connection
                Guid clientConnectionId = connection.Id;
                string clientId = clientConnectionId.ToString();
                var adapter = this;
                var clientConnection = connection; // Capture for closure
                var playerIdentities = identities; // Capture for closure
                
                UnityMainThreadDispatcher.EnqueueAction(() =>
                {
                    // Check if this connection was already disconnected before we could process
                    bool wasDisconnected;
                    lock (adapter._pendingPlayerIdentitiesLock)
                    {
                        wasDisconnected = !adapter._pendingPlayerIdentities.ContainsKey(clientConnectionId);
                    }
                    
                    if (wasDisconnected)
                    {
                        NetworkLogger.Warn($"Server: Connection {clientConnectionId} already disconnected, skipping player creation");
                        return;
                    }
                    
                    // Remove from pending (we're about to process them)
                    lock (adapter._pendingPlayerIdentitiesLock)
                    {
                        adapter._pendingPlayerIdentities.Remove(clientConnectionId);
                    }
                    
                    // Check if this connection already has players (duplicate handshake prevention)
                    if (adapter._connectedPlayersByConnection.ContainsKey(clientConnectionId) && 
                        adapter._connectedPlayersByConnection[clientConnectionId].Count > 0)
                    {
                        NetworkLogger.Info($"Server: Connection {clientConnectionId} already has players, ignoring duplicate HandshakeRequest");
                        // Still send lobby state so client knows we got it
                        adapter.SendLobbyStateToClient(clientConnection);
                        return;
                    }
                    
                    if (!adapter._connectedPlayers.ContainsKey(clientId))
                    {
                        adapter._connectedPlayers[clientId] = new List<NetworkPlayerData>();
                    }
                    if (!adapter._connectedPlayersByConnection.ContainsKey(clientConnectionId))
                    {
                        adapter._connectedPlayersByConnection[clientConnectionId] = new List<NetworkPlayerData>();
                    }
                    
                    // Collect all players from this connection for the batched event
                    var playersFromConnection = new List<NetworkPlayerData>();
                    
                    // Create player data for EACH identity
                    foreach (var playerIdentity in playerIdentities)
                    {
                        NetworkLogger.Info($"Server: Creating player data for '{playerIdentity.DisplayName}' with NetworkPlayerId={playerIdentity.PlayerId}, ConnectionId={clientConnectionId}");
                        
                        // Pass the client's connection ID to CreateLiteNetPlayerData
                        var playerData = adapter.CreateLiteNetPlayerData(playerIdentity.DisplayName, isHost: false, isLocal: false, connectionId: clientConnectionId);
                        
                        // Store the player ID in the player data for later lookup
                        playerData.NetworkPlayerId = playerIdentity.PlayerId;
                        NetworkLogger.Info($"Server: Set client player NetworkPlayerId to {playerIdentity.PlayerId}");
                        
                        adapter._connectedPlayers[clientId].Add(playerData);
                        adapter._connectedPlayersByConnection[clientConnectionId].Add(playerData);
                        playersFromConnection.Add(playerData);
                        
                        // Show toast notification with player name
                        string displayName = playerIdentity.DisplayName ?? "Unknown Player";
                        YARG.Menu.Persistent.ToastManager.ToastInformation($"{displayName} joined the lobby");
                        
                        // Fire player joined event for each player (for backward compatibility)
                        adapter.OnPlayerJoined?.Invoke(playerData);
                    }
                    
                    // Fire batched event with all players from this connection (for clustering)
                    if (playersFromConnection.Count > 0)
                    {
                        adapter.OnPlayersJoinedFromConnection?.Invoke(playersFromConnection);
                    }
                    
                    // Update CurrentPlayers based on actual number of profiles (not connections)
                    if (adapter._currentLobby != null)
                    {
                        adapter._currentLobby.CurrentPlayers += playersFromConnection.Count;
                        NetworkLogger.Info($"Server: Player count is now {adapter._currentLobby.CurrentPlayers} (added {playersFromConnection.Count} from this connection)");
                    }
                    
                    // Update PlayerNames in current lobby for discovery responses
                    adapter.UpdateLobbyPlayerNames();
                    
                    NetworkLogger.Info($"Server: Added {playerIdentities.Count} player(s) to connected players (total groups: {adapter._connectedPlayers.Count}, total players: {adapter.GetAllPlayers().Count})");
                    
                    // Send current lobby state to the new client
                    adapter.SendLobbyStateToClient(clientConnection);
                    
                    // Send current setlist to the new client
                    adapter.SendSetlistSyncToClient(clientConnection);
                    
                    // Debug: Log current session phase when player joins
                    NetworkLogger.Info($"Server: Player joined, current session phase: {adapter._currentSessionPhase}, IsLateJoinScenario: {adapter.IsLateJoinScenario()}");
                    
                    // Check if this is a late join scenario (joining during active setlist)
                    if (adapter.IsLateJoinScenario())
                    {
                        NetworkLogger.Info($"Server: Late join scenario detected, initiating late join check for connection {clientConnectionId}");
                        adapter.InitiateLateJoinCheck(clientConnection, playersFromConnection);
                    }
                    else
                    {
                        NetworkLogger.Info($"Server: Not a late join scenario - normal join flow (phase={adapter._currentSessionPhase})");
                    }
                });
            }
            catch (Exception ex)
            {
                NetworkLogger.Error($"[LiteNetNetworkingAdapter] Server: Failed to parse player identity: {ex.Message}");
            }
        }
        
        private void HandleHostDisconnectMessage()
        {
            NetworkLogger.Client(" Received host disconnect notification");
            
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                _isConnected = false;
                _currentLobby = null;
                _connectedPlayers.Clear();
                _connectedPlayersByConnection.Clear();
                _isBrowsingSongs = false;
                
                OnLobbyLeft?.Invoke();
            });
        }
        
        private void HandleLobbyStateMessage(ReadOnlyMemory<byte> payload)
        {
            if (!NavigationBinaryPackets.TryParseLobbyStatePacket(payload.Span, out bool isBrowsing))
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Client: Invalid lobby state message");
                return;
            }
            
            NetworkLogger.Client($"Received lobby state, browsing={isBrowsing}");
            
            // Receiving lobby state means our handshake was acknowledged
            if (!_handshakeAcknowledged)
            {
                _handshakeAcknowledged = true;
                NetworkLogger.Info("[Client] Handshake acknowledged by server (received LobbyState)");
            }
            
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                if (_isBrowsingSongs != isBrowsing)
                {
                    _isBrowsingSongs = isBrowsing;
                    OnBrowsingStateChanged?.Invoke(isBrowsing);
                }
            });
        }
        
        /// <summary>
        /// Handles IdentityRequest from host (client-side).
        /// This is a fallback mechanism - if our HandshakeRequest was lost, the host will request our identity.
        /// </summary>
        private void HandleIdentityRequestMessage(INetConnection connection)
        {
            NetworkLogger.Info("[Client] Received IdentityRequest from host - resending player identity");
            
            // The host is asking for our identity because it never received our HandshakeRequest
            // Resend it now using the stored server connection (more reliable than the parameter)
            try
            {
                var identities = GetAllLocalPlayerIdentities();
                if (identities.Count == 0)
                {
                    NetworkLogger.Warn("[Client] No local player identities to send in response to IdentityRequest");
                    return;
                }
                
                byte[] message = HandshakeBinaryPackets.BuildMultiPlayerRequestPacket(identities);
                
                // Use _serverConnection if available, otherwise use the provided connection
                var sendConnection = _serverConnection ?? connection;
                NetworkLogger.Info($"[Client] IdentityRequest response: Using connection {sendConnection?.Id}, endpoint={sendConnection?.EndPoint}");
                
                if (sendConnection == null)
                {
                    NetworkLogger.Error("[Client] No valid connection to send IdentityRequest response!");
                    return;
                }
                
                sendConnection.Send(message, ChannelType.ReliableOrdered);
                NetworkLogger.Info($"[Client] Sent HandshakeRequest in response to IdentityRequest - length={message.Length}, players={identities.Count}");
                
                // Mark as sent (retry mechanism will stop if acknowledged)
                _handshakeSent = true;
            }
            catch (Exception ex)
            {
                NetworkLogger.Error($"[Client] Failed to respond to IdentityRequest: {ex.Message}\n{ex.StackTrace}");
            }
        }
        
        private void HandleNavigateToMenuMessage(ReadOnlyMemory<byte> payload)
        {
            if (!NavigationBinaryPackets.TryParseNavigatePacket(payload.Span, out var menuTarget))
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Client: Invalid navigate message");
                return;
            }
            
            NetworkLogger.Client($"Received navigate to menu command, target={menuTarget}");
            
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                switch (menuTarget)
                {
                    case MenuTarget.MusicLibrary:
                        NetworkLogger.Info(" Client: Navigating to Music Library");
                        // Clear the setlist when navigating back to music library
                        if (_setlistManager.Count > 0)
                        {
                            _setlistManager.Clear();
                            OnSetlistUpdated?.Invoke();
                            NetworkLogger.Info(" Client: Cleared setlist when navigating to music library");
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
                        NetworkLogger.Info(" Client: Navigating to Lobby Room");
                        MenuManager.Instance.PushMenu(MenuManager.Menu.LobbyRoom);
                        break;
                    case MenuTarget.PopMenu:
                        // Allow popping from MusicLibrary or DifficultySelect
                        // This handles:
                        // - Host backing out of MusicLibrary -> clients pop to LobbyRoom
                        // - Host backing out of DifficultySelect -> clients pop to MusicLibrary
                        var currentMenu = MenuManager.Instance.CurrentMenu;
                        if (currentMenu == MenuManager.Menu.MusicLibrary || 
                            currentMenu == MenuManager.Menu.DifficultySelect)
                        {
                            NetworkLogger.Info($" Client: Popping {currentMenu} (host requested navigation back)");
                            MenuManager.Instance.PopMenu();
                        }
                        else
                        {
                            NetworkLogger.Info($" Client: Ignoring PopMenu - current menu is {currentMenu}, not MusicLibrary or DifficultySelect");
                        }
                        break;
                    default:
                        NetworkLogger.Warn($"[LiteNetNetworkingAdapter] Client: Unknown menu target {menuTarget}");
                        break;
                }
            });
        }
        
        /// <summary>
        /// Handles a navigation request from a client (designated host on dedicated server).
        /// Uses the unified ValidateHostAuthority check before executing.
        /// </summary>
        private void HandleNavigationRequestMessage(INetConnection connection, ReadOnlyMemory<byte> payload)
        {
            if (!NavigationBinaryPackets.TryParseNavigationRequestPacket(payload.Span, out var menuTarget))
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Server: Invalid navigation request packet");
                return;
            }
            
            NetworkLogger.Info($"Server: Received navigation request for {menuTarget} from {connection.EndPoint}");
            
            // Validate host authority using the unified method
            if (!ValidateHostAuthority(connection))
            {
                NetworkLogger.Warn($"[LiteNetNetworkingAdapter] Server: Navigation request rejected - sender lacks host authority");
                return;
            }
            
            NetworkLogger.Info($"Server: Navigation request approved, executing {menuTarget}");
            
            // Map menu target to host action and execute
            switch (menuTarget)
            {
                case MenuTarget.MusicLibrary:
                    ExecuteHostAction(HostActionType.NavigateToMusicLibrary, null);
                    break;
                case MenuTarget.LobbyRoom:
                    ExecuteHostAction(HostActionType.NavigateToLobbyRoom, null);
                    break;
                case MenuTarget.PopMenu:
                    ExecuteHostAction(HostActionType.PopMenu, null);
                    break;
                default:
                    // For any other menu target, just broadcast directly
                    BroadcastNavigateToMenu(menuTarget);
                    break;
            }
        }
        
        /// <summary>
        /// Handles a setlist start request from a client (designated host on dedicated server).
        /// Uses the unified ValidateHostAuthority check before executing.
        /// </summary>
        private void HandleSetlistStartRequestMessage(INetConnection connection)
        {
            NetworkLogger.Info($"Server: Received setlist start request from {connection.EndPoint}");
            
            // Validate host authority using the unified method
            if (!ValidateHostAuthority(connection))
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Server: Setlist start request rejected - sender lacks host authority");
                return;
            }
            
            NetworkLogger.Info("Server: Setlist start request approved, executing");
            
            // Execute using unified action (runs on main thread)
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                ExecuteHostAction(HostActionType.StartShow, null);
            });
        }
        
        /// <summary>
        /// Handles a score screen advance request from a client (designated host on dedicated server).
        /// Uses the unified ValidateHostAuthority check before executing.
        /// </summary>
        private void HandleScoreScreenAdvanceRequestMessage(INetConnection connection)
        {
            NetworkLogger.Info($"Server: Received score screen advance request from {connection.EndPoint}");
            
            // Validate host authority using the unified method
            if (!ValidateHostAuthority(connection))
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Server: Score screen advance request rejected - sender lacks host authority");
                return;
            }
            
            NetworkLogger.Info("Server: Score screen advance request approved, executing");
            
            // Execute on main thread using unified action
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                ExecuteHostAction(HostActionType.AdvanceScoreScreen, null);
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
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Cannot broadcast navigation - not hosting");
                return;
            }
            
            if (_transport == null)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Cannot broadcast navigation - transport is null");
                return;
            }
            
            byte[] message = NavigationBinaryPackets.BuildNavigatePacket(menuTarget);
            NetworkLogger.Info($"Host: Broadcasting navigate to menu {menuTarget} to all clients");
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
                NetworkLogger.Info(" Cleared setlist when navigating to music library");
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
        /// Broadcasts a command to pop the current menu for all clients (go back one level).
        /// Only works when hosting.
        /// </summary>
        public void BroadcastPopMenu()
        {
            BroadcastNavigateToMenu(MenuTarget.PopMenu);
        }
        
        /// <summary>
        /// Sends a navigation request to the server.
        /// Used by the designated host on dedicated servers to request navigation.
        /// </summary>
        /// <param name="target">The menu target to navigate to.</param>
        public void RequestNavigateToMenu(MenuTarget target)
        {
            if (_isHosting)
            {
                // If we're the actual host, just broadcast directly
                BroadcastNavigateToMenu(target);
                return;
            }
            
            if (!_isConnected || _serverConnection == null)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Cannot request navigation - not connected to server");
                return;
            }
            
            byte[] packet = NavigationBinaryPackets.BuildNavigationRequestPacket(target);
            NetworkLogger.Info($"Client: Requesting navigation to {target}");
            _serverConnection.Send(packet, ChannelType.ReliableOrdered);
        }
        
        /// <summary>
        /// Requests the server to navigate all players to the Music Library.
        /// Used by the designated host on dedicated servers.
        /// Also sets the local browsing state and clears the setlist.
        /// </summary>
        public void RequestNavigateToMusicLibrary()
        {
            // Clear the setlist when navigating to music library
            if (_setlistManager.Count > 0)
            {
                _setlistManager.Clear();
                OnSetlistUpdated?.Invoke();
                NetworkLogger.Info(" Cleared setlist when requesting navigation to music library");
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
            RequestNavigateToMenu(MenuTarget.MusicLibrary);
        }
        
        /// <summary>
        /// Requests the server to navigate all players to the Lobby Room.
        /// Used by the designated host on dedicated servers.
        /// </summary>
        public void RequestNavigateToLobbyRoom()
        {
            SetBrowsingState(false);
            RequestNavigateToMenu(MenuTarget.LobbyRoom);
        }

        /// <summary>
        /// Broadcasts a host change notification to all clients.
        /// Used in dedicated server mode when the host player changes.
        /// </summary>
        /// <param name="newHostPlayerId">The NetworkPlayerId of the new host.</param>
        public void BroadcastHostChange(Guid newHostPlayerId)
        {
            if (!_isHosting)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Only server can broadcast host change");
                return;
            }
            
            // Build packet: [PacketType.HostChanged (1 byte)][NewHostPlayerId (16 bytes)]
            var packet = new byte[17];
            packet[0] = (byte)YARG.Net.Packets.PacketType.HostChanged;
            newHostPlayerId.ToByteArray().CopyTo(packet, 1);
            
            NetworkLogger.Info($"Host: Broadcasting host change to {newHostPlayerId} to all clients");
            BroadcastPacketToClients(packet, "host change");
            
            // Update local state for all players
            var allPlayers = GetAllPlayers();
            foreach (var player in allPlayers)
            {
                if (player != null)
                {
                    player.IsHost = player.NetworkPlayerId == newHostPlayerId;
                }
            }
        }
        
        /// <summary>
        /// Sends the current host player ID to a specific client.
        /// Used when a new client joins a dedicated server that already has a host.
        /// </summary>
        /// <param name="connection">The client connection to send to.</param>
        /// <param name="hostPlayerId">The NetworkPlayerId of the current host.</param>
        public void SendHostChangeToClient(INetConnection connection, Guid hostPlayerId)
        {
            if (!_isHosting)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Only server can send host change");
                return;
            }
            
            if (hostPlayerId == Guid.Empty)
            {
                NetworkLogger.Info($"Host: No host assigned yet, skipping host change to client {connection.EndPoint}");
                return;
            }
            
            // Build packet: [PacketType.HostChanged (1 byte)][NewHostPlayerId (16 bytes)]
            var packet = new byte[17];
            packet[0] = (byte)YARG.Net.Packets.PacketType.HostChanged;
            hostPlayerId.ToByteArray().CopyTo(packet, 1);
            
            NetworkLogger.Info($"Host: Sending current host {hostPlayerId} to new client {connection.EndPoint}");
            
            try
            {
                connection.Send(packet, ChannelType.ReliableOrdered);
            }
            catch (Exception ex)
            {
                NetworkLogger.Error($"[LiteNetNetworkingAdapter] Host: Failed to send host change to client: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Broadcasts a restart gameplay command to all clients.
        /// The host should call this when they want all players to restart the current song.
        /// </summary>
        public void BroadcastRestartGameplay()
        {
            if (!_isHosting)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Only host can broadcast restart gameplay");
                return;
            }
            
            byte[] message = GameplayBinaryPackets.BuildRestartPacket();
            NetworkLogger.Info(" Host: Broadcasting restart gameplay to all clients");
            BroadcastPacketToClients(message, "restart gameplay");
        }
        
        /// <summary>
        /// Broadcasts to all clients that a player has left during gameplay.
        /// </summary>
        public void BroadcastPlayerLeftGameplay(string playerName)
        {
            if (!_isHosting)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Only host can broadcast player left");
                return;
            }
            
            byte[] message = GameplayBinaryPackets.BuildPlayerLeftPacket(playerName);
            NetworkLogger.Info($"Host: Broadcasting player '{playerName}' left gameplay to all clients");
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
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Only host can broadcast quit to library");
                return;
            }
            
            byte[] message = GameplayBinaryPackets.BuildQuitToLibraryPacket();
            NetworkLogger.Info(" Host: Broadcasting quit to library to all clients");
            BroadcastPacketToClients(message, "quit to library");
        }
        
        /// <summary>
        /// Broadcasts session settings to all connected clients.
        /// </summary>
        public bool BroadcastSessionSettings(
            string lobbyName,
            int maxPlayers,
            byte privacyMode,
            int bandSize,
            bool noFailMode,
            bool sharedSongsOnly,
            bool allowModifiers,
            bool enablePresetSync,
            bool allowLateJoin,
            List<int> allowedGameModes,
            bool localPlayersFirst)
        {
            if (!_isHosting)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Only host can broadcast session settings");
                return false;
            }
            
            // Convert int list to GameMode list for validation
            var gameModeBlacklist = new List<YARG.Core.GameMode>();
            if (allowedGameModes != null)
            {
                foreach (var mode in allowedGameModes)
                {
                    if (mode >= 0 && mode <= 255 && Enum.IsDefined(typeof(YARG.Core.GameMode), (byte)mode))
                    {
                        gameModeBlacklist.Add((YARG.Core.GameMode)mode);
                    }
                }
            }
            
            // Validate that host's LOCAL profiles aren't blocked by the new settings
            if (!ValidateProfilesAgainstGameModes(gameModeBlacklist, out var blockedProfiles))
            {
                // Host is trying to block their own profiles - show toast and don't apply settings
                string blockedList = string.Join(", ", blockedProfiles);
                NetworkLogger.Warn($"[LiteNetNetworkingAdapter] Host: Cannot apply settings - would block own profiles: {blockedList}");
                
                UnityMainThreadDispatcher.EnqueueAction(() =>
                {
                    YARG.Menu.Persistent.ToastManager.ToastWarning($"Cannot disable this game mode: {blockedList} is currently connected.");
                });
                return false;
            }
            
            // Also validate ALL connected players (including remote clients) against the new settings
            if (gameModeBlacklist.Count > 0)
            {
                var allPlayers = GetAllPlayers();
                var blockedRemotePlayers = new List<string>();
                
                foreach (var player in allPlayers)
                {
                    if (player == null) continue;
                    
                    // Get game mode from the player's instrument
                    var gameMode = ((YARG.Core.Instrument)player.Instrument).ToNativeGameMode();
                    if (gameModeBlacklist.Contains(gameMode))
                    {
                        blockedRemotePlayers.Add($"{player.PlayerName} ({gameMode})");
                    }
                }
                
                if (blockedRemotePlayers.Count > 0)
                {
                    string blockedList = string.Join(", ", blockedRemotePlayers);
                    NetworkLogger.Warn($"[LiteNetNetworkingAdapter] Host: Cannot apply settings - would block connected players: {blockedList}");
                    
                    UnityMainThreadDispatcher.EnqueueAction(() =>
                    {
                        YARG.Menu.Persistent.ToastManager.ToastWarning($"Cannot disable this game mode: {blockedList} is currently connected.");
                    });
                    return false;
                }
            }
            
            // Update current lobby info for discovery responses
            if (_currentLobby != null)
            {
                _currentLobby.LobbyName = lobbyName;
                _currentLobby.MaxPlayers = maxPlayers;
                _currentLobby.PrivacyMode = (LobbyPrivacyMode)privacyMode;
                _currentLobby.NoFailMode = noFailMode;
                _currentLobby.SharedSongsOnly = sharedSongsOnly;
                _currentLobby.BandSize = bandSize;
                
                // Convert int list to GameMode list (already validated above)
                _currentLobby.AllowedGameModes.Clear();
                _currentLobby.AllowedGameModes.AddRange(gameModeBlacklist);
                
                NetworkLogger.Verbose($"Updated lobby info for discovery: NoFail={noFailMode}, SharedSongs={sharedSongsOnly}, BandSize={bandSize}");
            }
            
            byte[] message = YARG.Net.Packets.SessionSettingsBinaryPackets.BuildSessionSettingsSyncPacket(
                lobbyName,
                maxPlayers,
                privacyMode,
                bandSize,
                noFailMode,
                sharedSongsOnly,
                allowModifiers,
                enablePresetSync,
                allowLateJoin,
                allowedGameModes,
                localPlayersFirst);
            
            NetworkLogger.Info($"Host: Broadcasting session settings to {_connectionMap.Count} clients");
            BroadcastPacketToClients(message, "session settings");
            return true;
        }
        
        #region Band Management (Unified for Dedicated Server and In-Game Host)
        
        /// <summary>
        /// Initializes the band system for the current session (host only).
        /// This uses a deterministic seed based on the lobby ID so all clients get consistent band names.
        /// After initialization, any connected players are assigned to bands.
        /// </summary>
        /// <param name="bandSize">Max players per band. 0 disables the band system.</param>
        /// <param name="forceReinitialize">If true, forces full reinitialization even if already initialized.</param>
        /// <returns>True if initialization succeeded.</returns>
        public bool InitializeBandsForSession(int bandSize, bool forceReinitialize = false)
        {
            if (!_isHosting)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Only host can initialize bands");
                return false;
            }
            
            var bandManager = Bands.BandManager.Instance;
            if (bandManager == null)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] BandManager.Instance is null, cannot initialize bands");
                return false;
            }
            
            string lobbyId = _currentLobby?.LobbyId?.ToString() ?? "unknown";
            
            // Generate a stable seed from the lobby ID so all clients get the same band names
            int stableSeed = GetDeterministicStringHash(lobbyId);
            
            // Initialize the band manager with stable seed
            bool wasInitialized = bandManager.IsInitialized;
            bandManager.Initialize(bandSize, stableSeed, lobbyId, forceReinitialize);
            bool justInitialized = !wasInitialized && bandManager.IsInitialized;
            
            if (justInitialized || forceReinitialize)
            {
                NetworkLogger.Verbose($"Initialized BandManager with band size: {bandSize}, seed: {stableSeed}");
                
                // Assign all current players to bands
                AssignCurrentPlayersToBands(bandManager);
                
                // Broadcast band assignments to all clients
                BroadcastBandAssignments();
            }
            
            return true;
        }
        
        /// <summary>
        /// Assigns a group of players (from a single connection) to a band.
        /// All players from the same connection must stay in the same band.
        /// </summary>
        /// <param name="connectionId">The network connection ID (use 0 for host connection).</param>
        /// <param name="playerIds">All player IDs from this connection.</param>
        /// <param name="isLocalConnection">Whether this is the local client's connection.</param>
        /// <returns>The assigned band ID, or -1 if assignment failed.</returns>
        public int AssignPlayersToBand(int connectionId, List<Guid> playerIds, bool isLocalConnection = false)
        {
            var bandManager = Bands.BandManager.Instance;
            if (bandManager == null || !bandManager.IsInitialized)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] BandManager not initialized, cannot assign players");
                return -1;
            }
            
            return bandManager.AssignClientPlayersToBand(connectionId, playerIds, isLocalConnection);
        }
        
        /// <summary>
        /// Broadcasts the current band assignments to all connected clients.
        /// </summary>
        public void BroadcastBandAssignments()
        {
            if (!_isHosting)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Only host can broadcast band assignments");
                return;
            }
            
            var bandManager = Bands.BandManager.Instance;
            if (bandManager == null || !bandManager.IsInitialized)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] BandManager not initialized, cannot broadcast assignments");
                return;
            }
            
            // Export sync data and use the existing broadcast method
            var syncData = bandManager.ExportSyncData();
            BroadcastBandAssignments(syncData);
        }
        
        /// <summary>
        /// Assigns all currently connected players to bands.
        /// Called during band initialization or when band size changes.
        /// </summary>
        private void AssignCurrentPlayersToBands(Bands.BandManager bandManager)
        {
            if (_connectedPlayers == null || _connectedPlayers.Count == 0)
            {
                NetworkLogger.Info("[LiteNetNetworkingAdapter] No connected players to assign to bands yet");
                return;
            }
            
            // First pass: Determine effective connection ID for each bucket
            // For host's own players, use a well-known ID (0)
            // For other clients, use the hash of the connection key
            foreach (var kvp in _connectedPlayers)
            {
                string connectionKey = kvp.Key.ToString();
                var players = kvp.Value;
                
                if (players == null || players.Count == 0)
                {
                    continue;
                }
                
                // Determine the effective connection ID
                int effectiveConnectionId;
                bool isLocalConnection;
                
                if (connectionKey == "host" || connectionKey == "local")
                {
                    // Host's own players always use connection ID 0
                    effectiveConnectionId = 0;
                    isLocalConnection = true;
                    bandManager.SetLocalConnectionId(effectiveConnectionId);
                }
                else
                {
                    // Remote clients use hash of their connection key
                    effectiveConnectionId = GetDeterministicStringHash(connectionKey);
                    isLocalConnection = false;
                }
                
                // Collect all player IDs from this connection
                var playerIds = players
                    .Where(p => p != null && p.NetworkPlayerId != Guid.Empty)
                    .Select(p => p.NetworkPlayerId)
                    .ToList();
                
                if (playerIds.Count == 0)
                {
                    continue;
                }
                
                // Check if all players are already assigned to the same band
                bool allAssigned = playerIds.All(pid => bandManager.GetPlayerBandId(pid) >= 0);
                if (allAssigned)
                {
                    NetworkLogger.Verbose($"Players from connection '{connectionKey}' already assigned to bands");
                    continue;
                }
                
                // Assign players to a band
                int assignedBandId = bandManager.AssignClientPlayersToBand(effectiveConnectionId, playerIds, isLocalConnection);
                NetworkLogger.Verbose($"Assigned {playerIds.Count} players from '{connectionKey}' to band {assignedBandId}");
            }
        }
        
        /// <summary>
        /// Computes a deterministic hash code for a string.
        /// Unlike string.GetHashCode(), this produces the same result across all processes/machines.
        /// </summary>
        private static int GetDeterministicStringHash(string str)
        {
            if (string.IsNullOrEmpty(str))
            {
                return 0;
            }
            
            unchecked
            {
                int hash = 5381;
                foreach (char c in str)
                {
                    hash = ((hash << 5) + hash) ^ c; // hash * 33 XOR c
                }
                return hash;
            }
        }
        
        #endregion
        
        /// <summary>
        /// Broadcasts the local player's current state to the network.
        /// Used to sync instrument/difficulty changes when joining.
        /// </summary>
        public void BroadcastPlayerStateUpdate()
        {
            var localPlayer = GetLocalPlayer();
            if (localPlayer == null)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] No local player to broadcast state for");
                return;
            }
            
            // Get current profile info for instrument/difficulty
            var (playerName, instrumentValue, difficultyValue) = _readyStateHandler.GetLocalPlayerInfo();
            
            // Update local player data
            if (_isHosting)
            {
                // Host: update local player and broadcast to clients
                _readyStateHandler.UpdateLocalPlayerReadyState(_isHosting, localPlayer.IsReady, instrumentValue, difficultyValue, _connectedPlayers);
                
                // Get the NetworkPlayerId for this player
                Guid networkPlayerId = localPlayer.NetworkPlayerId;
                
                BroadcastPlayerReadyStateTargeted("host", playerName, localPlayer.IsReady, instrumentValue, difficultyValue, sittingOut: false, networkPlayerId: networkPlayerId);
                NetworkLogger.Info($"[BroadcastPlayerStateUpdate] Host: Broadcast state for '{playerName}' instrument={instrumentValue}, difficulty={difficultyValue}, networkPlayerId={networkPlayerId}");
            }
            else if (_transport != null && _connectionMap.Count > 0)
            {
                // Client: send state to host
                _readyStateHandler.UpdateLocalPlayerReadyState(_isHosting, localPlayer.IsReady, instrumentValue, difficultyValue, _connectedPlayers);
                var message = _readyStateHandler.BuildClientReadyMessage(playerName, localPlayer.IsReady, instrumentValue, difficultyValue);
                
                foreach (var conn in _connectionMap.Values)
                {
                    conn.Send(message, ChannelType.ReliableOrdered);
                    NetworkLogger.Info($"[BroadcastPlayerStateUpdate] Client: Sent state to host - instrument={instrumentValue}, difficulty={difficultyValue}");
                    break;
                }
            }
            else
            {
                NetworkLogger.Warn("[BroadcastPlayerStateUpdate] Cannot broadcast - no transport or connections");
            }
        }
        
        /// <summary>
        /// Broadcasts the custom track order to all connected clients.
        /// </summary>
        /// <param name="playerOrder">The ordered list of player IDs.</param>
        public void BroadcastTrackOrder(List<Guid> playerOrder)
        {
            if (!_isHosting)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] BroadcastTrackOrder called but not hosting");
                return;
            }
            
            var message = new Tracks.TrackOrderMessage
            {
                PlayerOrder = playerOrder ?? new List<Guid>(),
                IsCustomOrder = playerOrder != null && playerOrder.Count > 0
            };
            
            var writer = new LiteNetLib.Utils.NetDataWriter();
            writer.Put((byte)Net.Packets.PacketType.TrackOrder);
            message.Serialize(writer);
            var packet = writer.CopyData();
            
            NetworkLogger.Verbose($"Host: Broadcasting track order with {playerOrder?.Count ?? 0} players");
            BroadcastPacketToClients(packet, "track order");
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
                    NetworkLogger.Error($"[LiteNetNetworkingAdapter] Host: Failed to send {description} to {kvp.Key}: {ex.Message}");
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
                    NetworkLogger.Error($"[LiteNetNetworkingAdapter] Host: Failed to send {description} to {kvp.Key}: {ex.Message}");
                }
            }
        }
        
        /// <summary>
        /// Handles the restart gameplay message from host.
        /// </summary>
        private void HandleRestartGameplayMessage()
        {
            NetworkLogger.Info(" Client: Received restart gameplay command from host");
            
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
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Client: Invalid player left message");
                return;
            }
            
            NetworkLogger.Client($"Received player '{playerName}' left gameplay");
            
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
            NetworkLogger.Info(" Client: Received quit to library command from host");
            
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                OnQuitToLibraryRequested?.Invoke();
            });
        }
        
        /// <summary>
        /// Handles the gameplay load ready message from a client.
        /// Called when a client finishes loading and is ready to start gameplay.
        /// When all players are ready, broadcasts GameplayAllLoadReady to start everyone.
        /// </summary>
        private void HandleGameplayLoadReadyMessage(INetConnection connection, ReadOnlyMemory<byte> payload)
        {
            if (!GameplayBinaryPackets.TryParseGameplayLoadReadyPacket(payload.Span, out string playerName))
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Host: Invalid gameplay load ready message");
                return;
            }
            
            NetworkLogger.Info($"Host: Player '{playerName}' finished loading");
            
            // Find connection key from connection ID - use connection ID to identify the specific player
            string connKey = connection.Id.ToString();
            
            // First try to find the player by connection ID (more accurate when names duplicate)
            bool found = false;
            if (_connectedPlayers.TryGetValue(connKey, out var playersFromConn))
            {
                foreach (var player in playersFromConn)
                {
                    if (player != null && string.Equals(player.PlayerName, playerName, StringComparison.Ordinal))
                    {
                        player.SetGameplayReadyServer(true);
                        NetworkLogger.Verbose($"Player '{playerName}' (conn={connKey}) marked gameplay ready");
                        found = true;
                    }
                }
            }
            
            // Fallback: search all players by name (handles edge cases)
            if (!found)
            {
                foreach (var kvp in _connectedPlayers)
                {
                    foreach (var player in kvp.Value)
                    {
                        // Match by name, but only for non-local users (remote clients)
                        if (player != null && !player.IsLocalUser && 
                            string.Equals(player.PlayerName, playerName, StringComparison.Ordinal))
                        {
                            player.SetGameplayReadyServer(true);
                            NetworkLogger.Verbose($"Player '{playerName}' (fallback) marked gameplay ready");
                            found = true;
                            break;
                        }
                    }
                    if (found) break;
                }
            }
            
            if (!found)
            {
                NetworkLogger.Warn($"Host: Could not find player '{playerName}' to mark gameplay ready");
            }
            
            // Check if all players are now ready
            CheckAndBroadcastAllLoadReady();
        }
        
        /// <summary>
        /// Checks if all players finished loading and broadcasts start signal if so.
        /// </summary>
        private void CheckAndBroadcastAllLoadReady()
        {
            NetworkLogger.Verbose($"[CheckAndBroadcastAllLoadReady] Checking {_connectedPlayers.Count} connection groups");
            int totalPlayers = 0;
            int readyPlayers = 0;
            foreach (var kvp in _connectedPlayers)
            {
                foreach (var player in kvp.Value)
                {
                    if (player != null)
                    {
                        totalPlayers++;
                        if (player.GameplayReady)
                        {
                            readyPlayers++;
                        }
                        NetworkLogger.Verbose($"  Player '{player.PlayerName}' (GameplayReady={player.GameplayReady})");
                    }
                }
            }
            NetworkLogger.Verbose($"[CheckAndBroadcastAllLoadReady] {readyPlayers}/{totalPlayers} players ready");
            
            if (!AreAllPlayersGameplayReady())
            {
                NetworkLogger.Verbose("[CheckAndBroadcastAllLoadReady] Not all players ready - waiting");
                return;
            }
            
            NetworkLogger.Info("Host: All players loaded - broadcasting start signal");
            
            // Broadcast to all clients
            byte[] message = GameplayBinaryPackets.BuildGameplayAllLoadReadyPacket();
            foreach (var conn in _connectionMap.Values)
            {
                conn.Send(message, ChannelType.ReliableOrdered);
            }
            
            // Release local barrier - set flag first to prevent race condition with WaitForMultiplayerGameplayStartAsync
            lock (_gameplayStartLock)
            {
                _allPlayersLoadedSignaled = true;
                _gameplayStartTcs?.TrySetResult(true);
            }
        }
        
        /// <summary>
        /// Handles the gameplay all load ready message from host.
        /// Called when the host signals that all players have finished loading.
        /// </summary>
        private void HandleGameplayAllLoadReadyMessage()
        {
            NetworkLogger.Info("[LiteNetNetworkingAdapter] Client: Received all players loaded signal from host");
            
            // Release the barrier so gameplay can start - set flag first to prevent race condition
            lock (_gameplayStartLock)
            {
                _allPlayersLoadedSignaled = true;
                _gameplayStartTcs?.TrySetResult(true);
            }
        }
        
        /// <summary>
        /// Handles session settings sync message from host.
        /// </summary>
        private void HandleSessionSettingsSyncMessage(ReadOnlyMemory<byte> payload)
        {
            if (!YARG.Net.Packets.SessionSettingsBinaryPackets.TryParseSessionSettingsSyncPacket(
                payload.Span,
                out string lobbyName,
                out int maxPlayers,
                out byte privacyMode,
                out int bandSize,
                out bool noFailMode,
                out bool sharedSongsOnly,
                out bool allowModifiers,
                out bool enablePresetSync,
                out bool allowLateJoin,
                out List<int> allowedGameModes,
                out bool localPlayersFirst))
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Client: Failed to parse session settings sync packet");
                return;
            }
            
            NetworkLogger.Info($"Client: Received session settings from host");
            
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                // Convert int list to GameMode list for validation
                var gameModeBlacklist = new List<YARG.Core.GameMode>();
                if (allowedGameModes != null)
                {
                    foreach (var mode in allowedGameModes)
                    {
                        if (System.Enum.IsDefined(typeof(YARG.Core.GameMode), mode))
                        {
                            gameModeBlacklist.Add((YARG.Core.GameMode)mode);
                        }
                    }
                }
                
                // Validate that our local profiles are allowed by the lobby's game mode restrictions
                if (!ValidateProfilesAgainstGameModes(gameModeBlacklist, out var blockedProfiles))
                {
                    // Some profiles are blocked - disconnect and show toast
                    string blockedList = string.Join(", ", blockedProfiles);
                    NetworkLogger.Warn($"[LiteNetNetworkingAdapter] Client: Disconnecting - blocked profiles: {blockedList}");
                    
                    // Show toast notification (will persist after menu transition)
                    YARG.Menu.Persistent.ToastManager.ToastWarning($"Disconnected: {blockedList} uses a disabled game mode.");
                    
                    // Disconnect from the lobby
                    LeaveLobby();
                    return;
                }
                
                // Update current lobby info if we have one
                if (_currentLobby != null)
                {
                    _currentLobby.LobbyName = lobbyName;
                    _currentLobby.MaxPlayers = maxPlayers;
                    _currentLobby.PrivacyMode = (LobbyPrivacyMode)privacyMode;
                }
                
                // Fire event to update UI
                OnSessionSettingsReceived?.Invoke(
                    lobbyName,
                    maxPlayers,
                    privacyMode,
                    bandSize,
                    noFailMode,
                    sharedSongsOnly,
                    allowModifiers,
                    enablePresetSync,
                    allowLateJoin,
                    allowedGameModes,
                    localPlayersFirst);
            });
        }
        
        #region Song Library Sync
        
        /// <summary>
        /// Handles song library chunk from a client (server-side).
        /// Delegates to SongLibrarySyncHandler.
        /// </summary>
        private void HandleSongLibraryChunkMessage(INetConnection connection, ReadOnlyMemory<byte> payload)
        {
            if (!_isHosting || payload.Length < 4)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Server: Invalid song library chunk (too short)");
                return;
            }
            
            // Parse the chunk header to detect if this is the final chunk for this connection
            var parsed = SongLibraryBinaryPackets.ParseChunkHeader(payload.Span);
            bool isFinalChunkForConnection = parsed.IsValid && parsed.IsFinalChunk;
            
            // Delegate to handler
            bool allPlayersComplete = _songLibrarySyncHandler.HandleSongLibraryChunk(connection.Id, payload.Span);
            
            // If this was the final chunk for this connection, and this connection is still pending,
            // proactively request their identity since HandshakeRequest packets seem to get lost
            if (isFinalChunkForConnection)
            {
                if (_pendingConnections.ContainsKey(connection.Id) && 
                    !_connectedPlayersByConnection.ContainsKey(connection.Id))
                {
                    NetworkLogger.Info($"[Server] Song library sync complete for pending connection {connection.Id}, sending IdentityRequest");
                    SendIdentityRequest(connection);
                }
            }
            
            if (allPlayersComplete)
            {
                // Recalculate and broadcast shared songs when all players are synced
                var sharedHashes = _songLibrarySyncHandler.SharedSongHashes;
                if (sharedHashes != null && sharedHashes.Count > 0)
                {
                    // Update host's filter
                    MultiplayerSongFilter.SetSharedSongs(sharedHashes);
                    
                    // Broadcast to clients
                    BroadcastSharedSongsFromHandler();
                }
                else
                {
                    BroadcastClearSharedSongs();
                }
            }
        }
        
        /// <summary>
        /// Sends an IdentityRequest packet to a client, asking them to send their player identity.
        /// This is a fallback mechanism in case the client's HandshakeRequest was lost.
        /// </summary>
        private void SendIdentityRequest(INetConnection connection)
        {
            try
            {
                // Simple packet: just the packet type byte
                byte[] message = new byte[] { (byte)PacketType.IdentityRequest };
                connection.Send(message, ChannelType.ReliableOrdered);
                NetworkLogger.Info($"[Server] Sent IdentityRequest to {connection.EndPoint}");
            }
            catch (Exception ex)
            {
                NetworkLogger.Error($"[Server] Failed to send IdentityRequest: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Broadcasts shared songs from the handler to all clients.
        /// </summary>
        private void BroadcastSharedSongsFromHandler()
        {
            if (!_isHosting || _transport == null)
                return;
            
            var sharedHashes = _songLibrarySyncHandler.SharedSongHashes;
            if (sharedHashes == null || sharedHashes.Count == 0)
            {
                BroadcastClearSharedSongs();
                return;
            }
            
            // Build chunks using the handler
            var chunks = _songLibrarySyncHandler.BuildSharedSongChunks();
            
            NetworkLogger.Server($"Broadcasting {sharedHashes.Count} shared songs in {chunks.Count} chunks (via handler)");
            
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
                    NetworkLogger.Warn($"[LiteNetNetworkingAdapter] Failed to send shared songs to client: {ex.Message}");
                }
            }
        }
        
        /// <summary>
        /// Handles shared songs chunk from server (client-side).
        /// Delegates to SongLibrarySyncHandler.
        /// </summary>
        private void HandleSharedSongsChunkMessage(ReadOnlyMemory<byte> payload)
        {
            if (payload.Length < 4)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Client: Invalid shared songs chunk (too short)");
                return;
            }
            
            // Copy the payload since we're dispatching to main thread and the memory may be recycled
            byte[] payloadCopy = payload.ToArray();
            
            // Dispatch to main thread since MultiplayerSongFilter accesses Unity components
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                _songLibrarySyncHandler.HandleSharedSongsChunk(payloadCopy.AsSpan());
            });
        }
        
        /// <summary>
        /// Handles clear shared songs message from server (client-side).
        /// </summary>
        private void HandleClearSharedSongsMessage()
        {
            NetworkLogger.Info(" Client: Received clear shared songs command");
            
            // Dispatch to main thread since MultiplayerSongFilter accesses Unity components
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                MultiplayerSongFilter.ClearSharedSongs();
            });
        }
        
        /// <summary>
        /// Recalculates the intersection of all player song libraries and broadcasts to clients.
        /// Delegates to SongLibrarySyncHandler.
        /// </summary>
        private void RecalculateSharedSongs()
        {
            if (!_isHosting)
                return;
            
            // Delegate to handler
            _songLibrarySyncHandler.RecalculateSharedSongs();
            
            var sharedHashes = _songLibrarySyncHandler.SharedSongHashes;
            if (sharedHashes == null || sharedHashes.Count == 0)
            {
                BroadcastClearSharedSongs();
            }
            else
            {
                BroadcastSharedSongs();
            }
            
            UpdateSharedSongSyncState();
        }
        
        /// <summary>
        /// Broadcasts the shared songs to all clients.
        /// </summary>
        private void BroadcastSharedSongs()
        {
            if (!_isHosting || _transport == null)
                return;
            
            var sharedHashes = _songLibrarySyncHandler.SharedSongHashes;
            if (sharedHashes == null || sharedHashes.Count == 0)
            {
                BroadcastClearSharedSongs();
                return;
            }
            
            // Build chunks using handler
            var chunks = _songLibrarySyncHandler.BuildSharedSongChunks();
            
            NetworkLogger.Server($"Broadcasting {sharedHashes.Count} shared songs in {chunks.Count} chunks");
            
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
                    NetworkLogger.Error($"[LiteNetNetworkingAdapter] Server: Failed to send shared songs to {kvp.Key}: {ex.Message}");
                }
            }
            
            // Also update local filter for host
            _songLibrarySyncHandler.UpdateHostFilter();
        }
        
        private void SendSharedSongsChunk(INetConnection connection, byte[] hashData, bool isFirstChunk, bool isFinalChunk)
        {
            // Use YARG.Net packet builder
            byte[] message = SongLibraryBinaryPackets.BuildSharedSongsChunkPacket(hashData, isFirstChunk, isFinalChunk);
            connection.Send(message, ChannelType.ReliableOrdered);
        }
        
        private void BroadcastClearSharedSongs()
        {
            if (!_isHosting || _transport == null)
                return;
            
            byte[] message = SongLibraryBinaryPackets.BuildClearSharedSongsPacket();
            BroadcastPacketToClients(message, "clear shared songs");
            
            // Also clear local filter for host
            MultiplayerSongFilter.ClearSharedSongs();
        }
        
        private void UpdateSharedSongSyncState()
        {
            bool isComplete = _songLibrarySyncHandler.IsSyncComplete;
            
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
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Cannot upload song library - not connected");
                return;
            }
            
            int currentVersion = SongContainer.RefreshVersion;
            if (_lastUploadedSongVersion == currentVersion && _songLibraryUploaded)
            {
                NetworkLogger.Info(" Song library already uploaded for this version");
                return;
            }
            
            var hashList = SongContainer.SongHashes;
            int totalSongs = hashList.Count;
            
            NetworkLogger.Client($"Uploading song library ({totalSongs} songs)");
            
            // Find server connection
            INetConnection serverConnection = null;
            foreach (var kvp in _connectionMap)
            {
                serverConnection = kvp.Value;
                break;
            }
            
            if (serverConnection == null)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] No server connection found for song library upload");
                return;
            }
            
            SendSongLibraryChunks(serverConnection, hashList);
            
            _lastUploadedSongVersion = currentVersion;
            _songLibraryUploaded = true;
        }
        
        private void UploadSongLibraryAsHost()
        {
            // Delegate to handler
            _songLibrarySyncHandler.RegisterHostLibrary();
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
                // Copy to local variable to avoid ref return issues
                var hash = hashList[i];
                var hashSpan = System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                    System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref hash, 1));
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
            
            NetworkLogger.Client($"Sent {chunkIndex} song library chunks ({totalSongs} songs)");
        }
        
        private void SendSongLibraryChunk(INetConnection connection, byte[] hashData, bool isFirstChunk, bool isFinalChunk)
        {
            // Use YARG.Net packet builder
            byte[] message = SongLibraryBinaryPackets.BuildSongLibraryChunkPacket(hashData, isFirstChunk, isFinalChunk);
            connection.Send(message, ChannelType.ReliableOrdered);
        }
        
        private void RemoveSongLibraryForPlayer(Guid connectionId)
        {
            _songLibrarySyncHandler.RemovePlayerLibrary(connectionId);
            UpdateSharedSongSyncState();
        }
        
        private void ResetSharedSongState()
        {
            _songLibrarySyncHandler.Reset();
            _songLibraryUploaded = false;
            _lastUploadedSongVersion = -1;
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
        /// Now also processes embedded player identities for combined auth+handshake.
        /// </summary>
        private void HandleAuthRequestMessage(INetConnection connection, ReadOnlyMemory<byte> payload)
        {
            if (!_isHosting)
                return;
            
            try
            {
                string providedPassword = "";
                int profileCount = 1;
                List<NetworkPlayerIdentity>? playerIdentities = null;
                
                if (!AuthBinaryPackets.TryParseRequestPacket(payload.Span, out providedPassword, out profileCount, out playerIdentities))
                {
                    providedPassword = "";
                    profileCount = 1;
                    playerIdentities = null;
                }
                
                bool hasEmbeddedIdentities = playerIdentities != null && playerIdentities.Count > 0;
                NetworkLogger.Server($"Auth request from {connection.EndPoint}: {profileCount} profile(s), embedded identities: {hasEmbeddedIdentities} ({playerIdentities?.Count ?? 0})");
                
                // Check if there's room for ALL the client's profiles
                int currentPlayerCount = GetAllPlayers().Count;
                int availableSlots = _maxPlayers - currentPlayerCount;
                
                if (profileCount > availableSlots)
                {
                    NetworkLogger.Server($"Auth failed: Not enough room for {profileCount} profiles (available: {availableSlots})");
                    byte[] fullResponse = AuthBinaryPackets.BuildResponsePacket(false, "Lobby is full - not enough room for all profiles");
                    connection.Send(fullResponse, ChannelType.ReliableOrdered);
                    
                    // Disconnect after sending response
                    Task.Delay(100).ContinueWith(_ =>
                    {
                        try { connection.Disconnect(); } catch { }
                    });
                    return;
                }
                
                // Update authenticator with current lobby state
                if (_currentLobby != null)
                {
                    _lobbyAuthenticator.SetCapacity(_currentLobby.MaxPlayers, currentPlayerCount);
                }
                
                // Process via manager (will also check capacity for 1 player, but we've already checked for multiple)
                var result = _lobbyAuthenticator.ProcessAuthRequest(connection.Id, providedPassword);
                bool success = result == AuthResult.Success;
                
                NetworkLogger.Server($"Auth {(success ? "success" : "failed")} for {connection.EndPoint} - {result}");
                
                // Send response using AuthBinaryPackets
                byte[] response = AuthBinaryPackets.BuildResponsePacket(success);
                connection.Send(response, ChannelType.ReliableOrdered);
                
                // If auth succeeded AND we have embedded identities, process them immediately
                // This bypasses the need for a separate HandshakeRequest packet
                if (success && hasEmbeddedIdentities)
                {
                    NetworkLogger.Server($"Processing {playerIdentities!.Count} embedded player identities from auth request");
                    ProcessPlayerIdentitiesFromAuth(connection, playerIdentities);
                }
                
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
                NetworkLogger.Error($"[LiteNetNetworkingAdapter] Server: Error handling auth request: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Processes player identities received from an auth request (combined auth+handshake).
        /// This is called when identities are embedded in the AuthRequest packet.
        /// </summary>
        private void ProcessPlayerIdentitiesFromAuth(INetConnection connection, List<NetworkPlayerIdentity> identities)
        {
            if (identities == null || identities.Count == 0)
                return;
                
            NetworkLogger.Info($"[Server] Processing {identities.Count} player identities from auth packet");
            foreach (var id in identities)
            {
                NetworkLogger.Info($"  - {id.DisplayName} (ID: {id.PlayerId})");
            }
            
            // Remove from pending connections since we now have their identity
            _pendingConnections.Remove(connection.Id);
            
            // Track pending identities SYNCHRONOUSLY on network thread
            lock (_pendingPlayerIdentitiesLock)
            {
                _pendingPlayerIdentities[connection.Id] = new List<NetworkPlayerIdentity>(identities);
                NetworkLogger.Info($"Server: Tracking {identities.Count} pending player identities for connection {connection.Id} (from auth)");
            }
            
            // Create player data on main thread
            Guid clientConnectionId = connection.Id;
            string clientId = clientConnectionId.ToString();
            var adapter = this;
            var clientConnection = connection;
            var playerIdentities = identities;
            
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                // Check if this connection was already disconnected before we could process
                bool wasDisconnected;
                lock (adapter._pendingPlayerIdentitiesLock)
                {
                    wasDisconnected = !adapter._pendingPlayerIdentities.ContainsKey(clientConnectionId);
                }
                
                if (wasDisconnected)
                {
                    NetworkLogger.Warn($"Server: Connection {clientConnectionId} already disconnected, skipping player creation (from auth)");
                    return;
                }
                
                // Remove from pending (we're about to process them)
                lock (adapter._pendingPlayerIdentitiesLock)
                {
                    adapter._pendingPlayerIdentities.Remove(clientConnectionId);
                }
                
                // EARLY CHECK: If this is a late join scenario and late join is disabled,
                // reject the player BEFORE adding them or sending lobby state
                if (adapter.IsLateJoinScenario() && !adapter.IsLateJoinAllowed())
                {
                    NetworkLogger.Info("[LateJoin] Late join not allowed - rejecting player before adding to lobby");
                    
                    // Send rejection packet for each player identity
                    foreach (var playerIdentity in playerIdentities)
                    {
                        byte[] rejectPacket = LateJoinBinaryPackets.BuildLateJoinActionPacket(
                            playerIdentity.PlayerId,
                            playerIdentity.DisplayName ?? "Unknown",
                            LateJoinAction.Rejected,
                            "A setlist is currently in progress. Late joining is disabled for this session.",
                            songTime: 0);
                        clientConnection.Send(rejectPacket, ChannelType.ReliableOrdered);
                    }
                    
                    // Disconnect after a short delay to ensure packet is sent
                    System.Threading.Tasks.Task.Run(async () =>
                    {
                        await System.Threading.Tasks.Task.Delay(100);
                        UnityMainThreadDispatcher.EnqueueAction(() =>
                        {
                            clientConnection.Disconnect();
                            NetworkLogger.Info($"[LateJoin] Disconnected late joiner {clientConnection.EndPoint} - rejected before joining lobby");
                        });
                    });
                    return;
                }
                
                // Check if this connection already has players (duplicate prevention)
                if (adapter._connectedPlayersByConnection.ContainsKey(clientConnectionId) && 
                    adapter._connectedPlayersByConnection[clientConnectionId].Count > 0)
                {
                    NetworkLogger.Info($"Server: Connection {clientConnectionId} already has players, ignoring duplicate identities from auth");
                    adapter.SendLobbyStateToClient(clientConnection);
                    return;
                }
                
                if (!adapter._connectedPlayers.ContainsKey(clientId))
                {
                    adapter._connectedPlayers[clientId] = new List<NetworkPlayerData>();
                }
                if (!adapter._connectedPlayersByConnection.ContainsKey(clientConnectionId))
                {
                    adapter._connectedPlayersByConnection[clientConnectionId] = new List<NetworkPlayerData>();
                }
                
                var playersFromConnection = new List<NetworkPlayerData>();
                
                foreach (var playerIdentity in playerIdentities)
                {
                    NetworkLogger.Info($"Server: Creating player data for '{playerIdentity.DisplayName}' with NetworkPlayerId={playerIdentity.PlayerId}, ConnectionId={clientConnectionId} (from auth)");
                    
                    var playerData = adapter.CreateLiteNetPlayerData(playerIdentity.DisplayName, isHost: false, isLocal: false, connectionId: clientConnectionId);
                    playerData.NetworkPlayerId = playerIdentity.PlayerId;
                    
                    adapter._connectedPlayers[clientId].Add(playerData);
                    adapter._connectedPlayersByConnection[clientConnectionId].Add(playerData);
                    playersFromConnection.Add(playerData);
                    
                    // Show toast notification
                    string displayName = playerIdentity.DisplayName ?? "Unknown Player";
                    YARG.Menu.Persistent.ToastManager.ToastInformation($"{displayName} joined the lobby");
                    
                    adapter.OnPlayerJoined?.Invoke(playerData);
                }
                
                if (playersFromConnection.Count > 0)
                {
                    adapter.OnPlayersJoinedFromConnection?.Invoke(playersFromConnection);
                }
                
                adapter.UpdateLobbyPlayerNames();
                
                NetworkLogger.Info($"Server: Added {playerIdentities.Count} player(s) from auth (total: {adapter.GetAllPlayers().Count})");
                
                adapter.SendLobbyStateToClient(clientConnection);
                adapter.SendSetlistSyncToClient(clientConnection);
                
                // Debug: Log current session phase when player joins via auth
                NetworkLogger.Info($"Server: Player joined via auth, current session phase: {adapter._currentSessionPhase}, IsLateJoinScenario: {adapter.IsLateJoinScenario()}");
                
                // Check if this is a late join scenario (joining during active setlist)
                if (adapter.IsLateJoinScenario())
                {
                    NetworkLogger.Info($"Server: Late join scenario detected (via auth), initiating late join check for connection {clientConnectionId}");
                    adapter.InitiateLateJoinCheck(clientConnection, playersFromConnection);
                }
                else
                {
                    NetworkLogger.Info($"Server: Not a late join scenario (via auth) - normal join flow (phase={adapter._currentSessionPhase})");
                }
            });
        }
        
        /// <summary>
        /// Handles authentication response from server (client-side).
        /// </summary>
        private void HandleAuthResponseMessage(INetConnection connection, ReadOnlyMemory<byte> payload)
        {
            if (_isHosting)
                return;
            
            if (!AuthBinaryPackets.TryParseResponsePacket(payload.Span, out bool success, out string authMessage))
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Client: Invalid auth response message");
                return;
            }
            
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                if (success)
                {
                    NetworkLogger.Info(" Client: Authentication successful");
                    // Continue with normal connection flow - send player identity
                    SendPlayerIdentityToServer(connection);
                }
                else
                {
                    // Use the message from the server if available, otherwise use generic message
                    string errorMessage = !string.IsNullOrEmpty(authMessage) ? authMessage : "Authentication failed";
                    NetworkLogger.Info($" Client: Authentication failed - {errorMessage}");
                    
                    // Show toast notification with the actual error reason
                    YARG.Menu.Persistent.ToastManager.ToastWarning(errorMessage);
                    
                    OnAuthenticationFailed?.Invoke(errorMessage);
                    OnNetworkError?.Invoke(errorMessage);
                }
            });
        }
        
        /// <summary>
        /// Handles track order message from host (client-side).
        /// </summary>
        private void HandleTrackOrderMessage(ReadOnlyMemory<byte> payload)
        {
            if (_isHosting)
                return;
            
            try
            {
                // Skip the packet type byte (first byte)
                var payloadData = payload.Slice(1).ToArray();
                var reader = new LiteNetLib.Utils.NetDataReader(payloadData);
                var message = new Tracks.TrackOrderMessage();
                message.Deserialize(reader);
                
                NetworkLogger.Client($"Received track order from host: {message.PlayerOrder?.Count ?? 0} players, custom={message.IsCustomOrder}");
                
                UnityMainThreadDispatcher.EnqueueAction(() =>
                {
                    // Extract host player IDs from the track order
                    // Host players are those whose IDs are NOT in our local players list
                    if (message.PlayerOrder != null && message.PlayerOrder.Count > 0)
                    {
                        // Get our local player IDs
                        var localPlayerIds = new HashSet<Guid>();
                        if (_connectedPlayers.TryGetValue("local", out var localPlayers))
                        {
                            foreach (var player in localPlayers)
                            {
                                localPlayerIds.Add(player.NetworkPlayerId);
                            }
                        }
                        
                        // Host player IDs are those not in our local list
                        _expectedHostPlayerIds.Clear();
                        _hostPlayerIdAssignmentIndex = 0;
                        foreach (var playerId in message.PlayerOrder)
                        {
                            if (!localPlayerIds.Contains(playerId))
                            {
                                _expectedHostPlayerIds.Add(playerId);
                            }
                        }
                        
                        NetworkLogger.Client($"Extracted {_expectedHostPlayerIds.Count} host player IDs from track order");
                        
                        // Update the first host player's NetworkPlayerId
                        UpdateHostPlayerNetworkId(_expectedHostPlayerIds.Count > 0 ? _expectedHostPlayerIds[0] : Guid.Empty);
                    }
                    
                    var trackOrderManager = Tracks.TrackOrderManager.Instance;
                    if (trackOrderManager != null)
                    {
                        if (message.IsCustomOrder && message.PlayerOrder != null)
                        {
                            trackOrderManager.ImportCustomOrder(message.PlayerOrder);
                        }
                        else
                        {
                            trackOrderManager.ClearCustomOrder();
                        }
                    }
                    
                    // Fire event so UI can refresh
                    OnTrackOrderReceived?.Invoke(message.PlayerOrder);
                });
            }
            catch (Exception ex)
            {
                NetworkLogger.Error($"[LiteNetNetworkingAdapter] Client: Failed to handle track order message: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Updates the host placeholder player's NetworkPlayerId on the client.
        /// This is needed because the client creates a placeholder host before knowing the real ID.
        /// </summary>
        private void UpdateHostPlayerNetworkId(Guid hostPlayerId)
        {
            if (_connectedPlayers.TryGetValue("host", out var hostPlayers) && hostPlayers.Count > 0)
            {
                var hostPlayer = hostPlayers[0];
                if (hostPlayer.NetworkPlayerId == Guid.Empty && hostPlayerId != Guid.Empty)
                {
                    hostPlayer.NetworkPlayerId = hostPlayerId;
                    NetworkLogger.Info($"Client: Updated host player NetworkPlayerId to {hostPlayerId}");
                    
                    // Now that we have the host's NetworkPlayerId, apply any pending presets
                    ApplyPendingPresets(new[] { hostPlayer });
                }
            }
        }
        
        #region Band Assignment Handling
        
        /// <summary>
        /// Event fired when band assignments are received from host (client-side).
        /// </summary>
        public event Action<Bands.BandAssignmentMessage>? OnBandAssignmentReceived;
        
        /// <summary>
        /// Event fired when a band name change is received from host (client-side).
        /// </summary>
        public event Action<int, string, int>? OnBandNameChangeReceived;
        
        /// <summary>
        /// Handles band assignment message from host (client-side).
        /// </summary>
        private void HandleBandAssignmentMessage(ReadOnlyMemory<byte> payload)
        {
            if (_isHosting)
                return;
            
            try
            {
                // Skip the packet type byte (first byte)
                var payloadData = payload.Slice(1).ToArray();
                var reader = new LiteNetLib.Utils.NetDataReader(payloadData);
                var message = new Bands.BandAssignmentMessage();
                message.Deserialize(reader);
                
                NetworkLogger.Client($"Received band assignment from host: {message.Assignments?.Count ?? 0} players, {message.BandNames?.Count ?? 0} bands, {message.PlayerNames?.Count ?? 0} player names, seed={message.LobbySeed}");
                
                UnityMainThreadDispatcher.EnqueueAction(() =>
                {
                    // Store player ID to name mapping for later use in FindTargetPlayerForSnapshot
                    if (message.PlayerNames != null)
                    {
                        foreach (var kvp in message.PlayerNames)
                        {
                            _remotePlayerIdToName[kvp.Key] = kvp.Value;
                            NetworkLogger.Verbose($"Stored player mapping: {kvp.Key} -> '{kvp.Value}'");
                        }
                        
                        // Create NetworkPlayerData entries for remote players we don't already know about
                        CreateNetworkPlayerDataForRemotePlayers(message.PlayerNames);
                    }
                    
                    OnBandAssignmentReceived?.Invoke(message);
                });
            }
            catch (Exception ex)
            {
                NetworkLogger.Error($"[LiteNetNetworkingAdapter] Client: Failed to handle band assignment message: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Creates NetworkPlayerData entries for remote players from band assignment.
        /// This ensures we have data objects to store snapshot data for players from other clients.
        /// </summary>
        private void CreateNetworkPlayerDataForRemotePlayers(Dictionary<Guid, string> playerNames)
        {
            if (playerNames == null)
                return;
            
            // Get set of all player IDs we already have NetworkPlayerData for
            var existingPlayerIds = new HashSet<Guid>();
            foreach (var kvp in _connectedPlayers)
            {
                foreach (var player in kvp.Value)
                {
                    if (player.NetworkPlayerId != Guid.Empty)
                    {
                        existingPlayerIds.Add(player.NetworkPlayerId);
                    }
                }
            }
            
            // Create entries for players we don't have yet
            List<NetworkPlayerData> newRemotePlayers = null;
            foreach (var kvp in playerNames)
            {
                Guid playerId = kvp.Key;
                string playerName = kvp.Value;
                
                if (existingPlayerIds.Contains(playerId))
                    continue;
                
                // Check if this player matches an existing player with no NetworkPlayerId set yet
                // This handles the case where our local player or the host player was created 
                // before we received their NetworkPlayerId from the server.
                // NOTE: We ONLY update players that have no ID set yet, because two
                // different players CAN have the same name!
                bool updatedExisting = false;
                
                // First check if this is the host player by matching against _pendingHostPlayerId
                // (which is set when we receive the HostChanged message from the server).
                // This is more reliable than matching by name, since the discovery HostName
                // may differ from the actual player display name.
                if (_pendingHostPlayerId != Guid.Empty && playerId == _pendingHostPlayerId)
                {
                    if (_connectedPlayers.TryGetValue("host", out var hostPlayers))
                    {
                        foreach (var player in hostPlayers)
                        {
                            if (player.IsHost && player.NetworkPlayerId == Guid.Empty)
                            {
                                // Found a host player with no NetworkPlayerId set
                                // Update both its ID and name (in case discovery name differs from actual name)
                                player.NetworkPlayerId = playerId;
                                if (!string.IsNullOrEmpty(playerName) && player.PlayerName != playerName)
                                {
                                    NetworkLogger.Verbose($"Updating host player name from '{player.PlayerName}' to '{playerName}'");
                                    player.PlayerName = playerName;
                                }
                                NetworkLogger.Verbose($"Updated NetworkPlayerId for host player '{playerName}': {playerId}");
                                existingPlayerIds.Add(playerId);
                                updatedExisting = true;
                                break;
                            }
                        }
                    }
                }
                
                // Then check local players
                if (!updatedExisting && _connectedPlayers.TryGetValue("local", out var localPlayers))
                {
                    foreach (var player in localPlayers)
                    {
                        if (player.PlayerName == playerName && player.NetworkPlayerId == Guid.Empty)
                        {
                            // Found a local player with matching name and no NetworkPlayerId
                            // This is likely our own local player - update its ID
                            player.NetworkPlayerId = playerId;
                            NetworkLogger.Verbose($"Updated NetworkPlayerId for local player '{playerName}': {playerId}");
                            existingPlayerIds.Add(playerId);
                            updatedExisting = true;
                            break;
                        }
                    }
                }
                
                if (!updatedExisting)
                {
                    // This is a new remote player - create NetworkPlayerData for them
                    // Note: They might have the same NAME as one of our local players, but
                    // they have a different NetworkPlayerId, so they're a different person!
                    // Default to Expert (4) since Beginner (0) often doesn't exist in songs
                    var remotePlayer = CreateLiteNetPlayerData(playerName, isHost: false, isLocal: false, initialInstrument: -1, initialDifficulty: 4, connectionId: Guid.Empty);
                    remotePlayer.NetworkPlayerId = playerId;
                    
                    if (newRemotePlayers == null)
                    {
                        newRemotePlayers = new List<NetworkPlayerData>();
                    }
                    newRemotePlayers.Add(remotePlayer);
                    existingPlayerIds.Add(playerId);
                    
                    NetworkLogger.Verbose($"Created NetworkPlayerData for remote player '{playerName}' ({playerId})");
                }
            }
            
            // Add all new remote players to _connectedPlayers["remote"]
            if (newRemotePlayers != null && newRemotePlayers.Count > 0)
            {
                if (!_connectedPlayers.TryGetValue("remote", out var remotePlayers))
                {
                    remotePlayers = new List<NetworkPlayerData>();
                    _connectedPlayers["remote"] = remotePlayers;
                }
                remotePlayers.AddRange(newRemotePlayers);
                NetworkLogger.Verbose($"Added {newRemotePlayers.Count} remote players (total: {remotePlayers.Count})");
                
                // Apply any pending preset sync data to the newly added players
                ApplyPendingPresets(newRemotePlayers);
            }
        }
        
        /// <summary>
        /// Handles band name change message from host (client-side).
        /// </summary>
        private void HandleBandNameChangeMessage(ReadOnlyMemory<byte> payload)
        {
            if (_isHosting)
                return;
            
            try
            {
                // Skip the packet type byte (first byte)
                var payloadData = payload.Slice(1).ToArray();
                var reader = new LiteNetLib.Utils.NetDataReader(payloadData);
                var message = new Bands.BandNameChangeMessage();
                message.Deserialize(reader);
                
                NetworkLogger.Client($"Received band name change from host: band {message.BandId} -> '{message.NewName}' (regen={message.RegenerationCount})");
                
                UnityMainThreadDispatcher.EnqueueAction(() =>
                {
                    OnBandNameChangeReceived?.Invoke(message.BandId, message.NewName, message.RegenerationCount);
                });
            }
            catch (Exception ex)
            {
                NetworkLogger.Error($"[LiteNetNetworkingAdapter] Client: Failed to handle band name change message: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Broadcasts band assignments to all connected clients (host-side).
        /// </summary>
        public void BroadcastBandAssignments(Bands.BandSyncData syncData)
        {
            if (!_isHosting)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] BroadcastBandAssignments called but not hosting");
                return;
            }
            
            var message = new Bands.BandAssignmentMessage
            {
                BandSize = syncData.BandSize,
                LobbySeed = syncData.LobbySeed,
                Assignments = syncData.PlayerAssignments,
                BandNames = syncData.BandNames,
                BandNameRegenerations = syncData.BandNameRegenerations,
                ConnectionGroups = syncData.ConnectionGroups,
                PlayerNames = syncData.PlayerNames
            };
            
            // Serialize the message
            var writer = new LiteNetLib.Utils.NetDataWriter();
            writer.Put((byte)PacketType.BandAssignment);
            message.Serialize(writer);
            byte[] data = writer.CopyData();
            
            // Broadcast to all clients
            foreach (var connection in _connectionMap.Values)
            {
                connection.Send(data, ChannelType.ReliableOrdered);
            }
            
            NetworkLogger.Verbose($"Host: Broadcasted band assignments to {_connectionMap.Count} clients ({message.Assignments?.Count ?? 0} players)");
        }
        
        /// <summary>
        /// Sends band assignments to a specific client (host-side).
        /// </summary>
        public void SendBandAssignmentsToClient(INetConnection connection, Bands.BandSyncData syncData)
        {
            if (!_isHosting)
                return;
            
            var message = new Bands.BandAssignmentMessage
            {
                BandSize = syncData.BandSize,
                LobbySeed = syncData.LobbySeed,
                Assignments = syncData.PlayerAssignments,
                BandNames = syncData.BandNames,
                BandNameRegenerations = syncData.BandNameRegenerations,
                ConnectionGroups = syncData.ConnectionGroups,
                PlayerNames = syncData.PlayerNames
            };
            
            // Serialize the message
            var writer = new LiteNetLib.Utils.NetDataWriter();
            writer.Put((byte)PacketType.BandAssignment);
            message.Serialize(writer);
            byte[] data = writer.CopyData();
            
            connection.Send(data, ChannelType.ReliableOrdered);
            NetworkLogger.Verbose($"Host: Sent band assignments to {connection.EndPoint}");
        }
        
        /// <summary>
        /// Broadcasts a band name change to all connected clients (host-side).
        /// </summary>
        public void BroadcastBandNameChange(int bandId, string newName, int regenerationCount)
        {
            if (!_isHosting)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] BroadcastBandNameChange called but not hosting");
                return;
            }
            
            var message = new Bands.BandNameChangeMessage
            {
                BandId = bandId,
                NewName = newName,
                RegenerationCount = regenerationCount
            };
            
            // Serialize the message
            var writer = new LiteNetLib.Utils.NetDataWriter();
            writer.Put((byte)PacketType.BandNameChange);
            message.Serialize(writer);
            byte[] data = writer.CopyData();
            
            // Broadcast to all clients
            foreach (var connection in _connectionMap.Values)
            {
                connection.Send(data, ChannelType.ReliableOrdered);
            }
            
            NetworkLogger.Verbose($"Host: Broadcasted band name change to {_connectionMap.Count} clients (band {bandId} -> '{newName}')");
        }
        
        /// <summary>
        /// Event fired when a client requests a band name change (host-side).
        /// Parameters: bandId, requestingPlayerId
        /// </summary>
        public event Action<int, Guid>? OnBandNameChangeRequested;
        
        /// <summary>
        /// Handles band name change request from client (host-side).
        /// </summary>
        private void HandleBandNameChangeRequestMessage(INetConnection connection, ReadOnlyMemory<byte> payload)
        {
            if (!_isHosting)
                return;
            
            try
            {
                // Skip the packet type byte (first byte)
                var payloadData = payload.Slice(1).ToArray();
                var reader = new LiteNetLib.Utils.NetDataReader(payloadData);
                var message = new Bands.BandNameChangeRequestMessage();
                message.Deserialize(reader);
                
                NetworkLogger.Verbose($"Host: Received band name change request - band {message.BandId}, player {message.RequestingPlayerId}");
                
                UnityMainThreadDispatcher.EnqueueAction(() =>
                {
                    OnBandNameChangeRequested?.Invoke(message.BandId, message.RequestingPlayerId);
                });
            }
            catch (Exception ex)
            {
                NetworkLogger.Error($"[LiteNetNetworkingAdapter] Host: Failed to handle band name change request: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Sends a request to the host to regenerate a band name (client-side).
        /// </summary>
        public void RequestBandNameChange(int bandId, Guid requestingPlayerId)
        {
            if (_isHosting)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] RequestBandNameChange called but hosting - host should regenerate directly");
                return;
            }
            
            if (_serverConnection == null)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] RequestBandNameChange called but no server connection");
                return;
            }
            
            var message = new Bands.BandNameChangeRequestMessage
            {
                BandId = bandId,
                RequestingPlayerId = requestingPlayerId
            };
            
            // Serialize the message
            var writer = new LiteNetLib.Utils.NetDataWriter();
            writer.Put((byte)PacketType.BandNameChangeRequest);
            message.Serialize(writer);
            byte[] data = writer.CopyData();
            
            _serverConnection.Send(data, ChannelType.ReliableOrdered);
            NetworkLogger.Verbose($"Client: Sent band name change request (band {bandId})");
        }
        
        #region NAT Punch
        
        /// <summary>
        /// Sends a NAT introduction request to a punch server.
        /// Use this to initiate NAT punch-through to a host.
        /// </summary>
        /// <param name="punchServerHost">The hostname or IP of the punch server</param>
        /// <param name="punchServerPort">The UDP port of the punch server</param>
        /// <param name="token">The punch token (identifying the session)</param>
        public void SendNatIntroduceRequest(string punchServerHost, int punchServerPort, string token)
        {
            if (_liteNetTransport == null || !_liteNetTransport.IsRunning)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Cannot send NAT introduce request: transport not running");
                return;
            }
            
            NetworkLogger.Info($"[LiteNetNetworkingAdapter] Sending NAT introduce request to {punchServerHost}:{punchServerPort}");
            _liteNetTransport.SendNatIntroduceRequest(punchServerHost, punchServerPort, token);
        }
        
        /// <summary>
        /// Subscribes to NAT punch success events on the transport.
        /// Called when transport is initialized.
        /// </summary>
        private void SubscribeToNatPunchEvents()
        {
            if (_liteNetTransport != null)
            {
                _liteNetTransport.OnNatPunchSuccess += HandleNatPunchSuccess;
            }
        }
        
        /// <summary>
        /// Unsubscribes from NAT punch success events on the transport.
        /// </summary>
        private void UnsubscribeFromNatPunchEvents()
        {
            if (_liteNetTransport != null)
            {
                _liteNetTransport.OnNatPunchSuccess -= HandleNatPunchSuccess;
            }
        }
        
        /// <summary>
        /// Handles NAT punch success from transport.
        /// </summary>
        private void HandleNatPunchSuccess(System.Net.IPEndPoint targetEndPoint, LiteNetLib.NatAddressType type, string token)
        {
            NetworkLogger.Info($"[LiteNetNetworkingAdapter] NAT punch success: target={targetEndPoint}, type={type}, token={token}");
            
            // Forward to main thread and fire event
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                OnNatPunchSuccess?.Invoke(targetEndPoint, type, token);
            });
        }
        
        /// <summary>
        /// Starts the transport layer without connecting to a server.
        /// Used for NAT punch-through - the client needs an active socket to receive punch messages.
        /// </summary>
        /// <returns>True if transport started successfully, false otherwise.</returns>
        public bool StartTransportForNatPunch()
        {
            if (_liteNetTransport != null && _liteNetTransport.IsRunning)
            {
                NetworkLogger.Info("[LiteNetNetworkingAdapter] Transport already running for NAT punch");
                return true;
            }
            
            try
            {
                // Create transport if not exists
                if (_liteNetTransport == null)
                {
                    _liteNetTransport = new LiteNetLibTransport();
                    _transport = _liteNetTransport;
                    SubscribeToNatPunchEvents();
                }
                
                // Start transport in "server" mode on port 0 (ephemeral port)
                // This creates an active UDP socket that can receive NAT punch messages
                // We're not actually hosting - just need the socket for NAT punch
                var startOptions = new YARG.Net.Transport.TransportStartOptions
                {
                    IsServer = true, // Server mode to just start listening, not connect
                    EnableNatPunchThrough = true,
                    Port = 0, // Let OS assign an ephemeral port
                    Address = "0.0.0.0"
                };
                
                _liteNetTransport.Start(startOptions);
                NetworkLogger.Info($"[LiteNetNetworkingAdapter] Transport started for NAT punch on port {_liteNetTransport.LocalPort}");
                return true;
            }
            catch (Exception ex)
            {
                NetworkLogger.Error($"[LiteNetNetworkingAdapter] Failed to start transport for NAT punch: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Stops the transport layer if it was started for NAT punch but punch failed.
        /// This allows JoinLobby to start fresh.
        /// </summary>
        public void StopTransport()
        {
            try
            {
                if (_liteNetTransport != null && _liteNetTransport.IsRunning)
                {
                    NetworkLogger.Info("[LiteNetNetworkingAdapter] Stopping transport (was started for NAT punch)");
                    UnsubscribeFromNatPunchEvents();
                    _liteNetTransport.Shutdown("NAT punch cleanup");
                }
            }
            catch (Exception ex)
            {
                NetworkLogger.Error($"[LiteNetNetworkingAdapter] Error stopping transport: {ex.Message}");
            }
        }
        
        #endregion
        
        /// <summary>
        /// Event fired when band score update is received from network.
        /// Parameters: bandId, totalScore, starPowerActive (any player in band has SP active)
        /// </summary>
        public event Action<int, long, bool>? OnBandScoreUpdateReceived;
        
        /// <summary>
        /// Handles band score update from a client (host-side).
        /// Aggregates and relays to all clients.
        /// </summary>
        private void HandleBandScoreUpdateMessage(INetConnection connection, ReadOnlyMemory<byte> payload)
        {
            if (!_isHosting)
                return;
            
            try
            {
                // Skip the packet type byte (first byte)
                var payloadData = payload.Slice(1).ToArray();
                var reader = new LiteNetLib.Utils.NetDataReader(payloadData);
                var message = new Bands.BandScoreUpdateMessage();
                message.Deserialize(reader);
                
                NetworkLogger.Verbose($"Host: Received band score update - band {message.BandId}, score {message.TotalScore}");
                
                // Update the band score in BandManager
                var bandManager = Bands.BandManager.Instance;
                if (bandManager != null)
                {
                    bandManager.UpdateBandScore(message.BandId, message.TotalScore);
                }
                
                // Relay to all other clients
                BroadcastBandScoreUpdate(message.BandId, message.TotalScore, false, connection);
            }
            catch (Exception ex)
            {
                NetworkLogger.Error($"[LiteNetNetworkingAdapter] Host: Failed to handle band score update: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Handles band score update broadcast from host (client-side).
        /// </summary>
        private void HandleBandScoreUpdateBroadcast(ReadOnlyMemory<byte> payload)
        {
            if (_isHosting)
                return;
            
            try
            {
                // Skip the packet type byte (first byte)
                var payloadData = payload.Slice(1).ToArray();
                var reader = new LiteNetLib.Utils.NetDataReader(payloadData);
                var message = new Bands.BandScoreUpdateMessage();
                message.Deserialize(reader);
                
                // Update the band score in BandManager
                var bandManager = Bands.BandManager.Instance;
                if (bandManager != null)
                {
                    bandManager.UpdateBandScore(message.BandId, message.TotalScore);
                }
                
                // Fire event for HUD update
                UnityMainThreadDispatcher.EnqueueAction(() =>
                {
                    // Determine if any player in this band has star power active
                    bool starPowerActive = false;
                    if (message.PlayerScores != null)
                    {
                        // If we had per-player SP state, we'd check here
                        // For now, we don't track this in the message
                    }
                    
                    OnBandScoreUpdateReceived?.Invoke(message.BandId, message.TotalScore, starPowerActive);
                });
            }
            catch (Exception ex)
            {
                NetworkLogger.Error($"[LiteNetNetworkingAdapter] Client: Failed to handle band score broadcast: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Sends the local band's score to the host (client-side during gameplay).
        /// </summary>
        public void SendBandScoreUpdate(int bandId, long totalScore)
        {
            if (_isHosting)
            {
                // Host broadcasts directly
                BroadcastBandScoreUpdate(bandId, totalScore, false, null);
                return;
            }
            
            if (_serverConnection == null)
                return;
            
            var message = new Bands.BandScoreUpdateMessage
            {
                BandId = bandId,
                TotalScore = totalScore,
                IsFinal = false
            };
            
            var writer = new LiteNetLib.Utils.NetDataWriter();
            writer.Put((byte)PacketType.BandScoreUpdate);
            message.Serialize(writer);
            byte[] data = writer.CopyData();
            
            _serverConnection.Send(data, ChannelType.Unreliable); // Use unreliable for frequent updates
        }
        
        /// <summary>
        /// Broadcasts band score to all clients (host-side).
        /// </summary>
        public void BroadcastBandScoreUpdate(int bandId, long totalScore, bool isFinal, INetConnection? excludeConnection = null)
        {
            if (!_isHosting)
                return;
            
            var message = new Bands.BandScoreUpdateMessage
            {
                BandId = bandId,
                TotalScore = totalScore,
                IsFinal = isFinal
            };
            
            var writer = new LiteNetLib.Utils.NetDataWriter();
            writer.Put((byte)PacketType.BandScoreUpdate);
            message.Serialize(writer);
            byte[] data = writer.CopyData();
            
            // Use reliable delivery for final score to ensure it arrives
            var channelType = isFinal ? ChannelType.ReliableOrdered : ChannelType.Unreliable;
            
            foreach (var connection in _connectionMap.Values)
            {
                if (excludeConnection != null && connection.Id == excludeConnection.Id)
                    continue;
                    
                connection.Send(data, channelType);
            }
        }
        
        /// <summary>
        /// Event fired when a band failure notification is received.
        /// Parameters: bandId, finalScore
        /// </summary>
        public event Action<int, long>? OnBandFailedReceived;
        
        /// <summary>
        /// Handles band failed message from client (host-side).
        /// </summary>
        private void HandleBandFailedMessage(INetConnection connection, ReadOnlyMemory<byte> payload)
        {
            if (!_isHosting)
                return;
            
            try
            {
                // Skip the packet type byte (first byte)
                var payloadData = payload.Slice(1).ToArray();
                var reader = new LiteNetLib.Utils.NetDataReader(payloadData);
                var message = new Bands.BandFailedMessage();
                message.Deserialize(reader);
                
                NetworkLogger.Info($"Host: Band {message.BandId} failed with score {message.FinalScore}");
                
                // Mark band as failed in BandManager
                var bandManager = Bands.BandManager.Instance;
                if (bandManager != null)
                {
                    bandManager.MarkBandAsFailed(message.BandId);
                }
                
                // Broadcast to all clients
                BroadcastBandFailed(message.BandId, message.FinalScore, connection);
                
                // Fire local event
                UnityMainThreadDispatcher.EnqueueAction(() =>
                {
                    OnBandFailedReceived?.Invoke(message.BandId, message.FinalScore);
                });
            }
            catch (Exception ex)
            {
                NetworkLogger.Error($"[LiteNetNetworkingAdapter] Host: Failed to handle band failed message: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Handles band failed broadcast from host (client-side).
        /// </summary>
        private void HandleBandFailedBroadcast(ReadOnlyMemory<byte> payload)
        {
            if (_isHosting)
                return;
            
            try
            {
                // Skip the packet type byte (first byte)
                var payloadData = payload.Slice(1).ToArray();
                var reader = new LiteNetLib.Utils.NetDataReader(payloadData);
                var message = new Bands.BandFailedMessage();
                message.Deserialize(reader);
                
                NetworkLogger.Info($"Client: Band {message.BandId} failed with score {message.FinalScore}");
                
                // Mark band as failed in BandManager
                var bandManager = Bands.BandManager.Instance;
                if (bandManager != null)
                {
                    bandManager.MarkBandAsFailed(message.BandId);
                }
                
                // Fire event for GameManager to handle spectate mode
                UnityMainThreadDispatcher.EnqueueAction(() =>
                {
                    OnBandFailedReceived?.Invoke(message.BandId, message.FinalScore);
                });
            }
            catch (Exception ex)
            {
                NetworkLogger.Error($"[LiteNetNetworkingAdapter] Client: Failed to handle band failed broadcast: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Sends a band failure notification to the host (client-side during gameplay).
        /// </summary>
        public void SendBandFailed(int bandId, long finalScore)
        {
            if (_isHosting)
            {
                // Host broadcasts directly
                BroadcastBandFailed(bandId, finalScore, null);
                
                // Mark locally
                var bandManager = Bands.BandManager.Instance;
                if (bandManager != null)
                {
                    bandManager.MarkBandAsFailed(bandId);
                }
                
                // Fire local event
                UnityMainThreadDispatcher.EnqueueAction(() =>
                {
                    OnBandFailedReceived?.Invoke(bandId, finalScore);
                });
                return;
            }
            
            if (_serverConnection == null)
                return;
            
            var message = new Bands.BandFailedMessage
            {
                BandId = bandId,
                FinalScore = finalScore
            };
            
            var writer = new LiteNetLib.Utils.NetDataWriter();
            writer.Put((byte)PacketType.BandFailed);
            message.Serialize(writer);
            byte[] data = writer.CopyData();
            
            _serverConnection.Send(data, ChannelType.ReliableOrdered); // Use reliable for important state changes
        }
        
        /// <summary>
        /// Broadcasts band failure to all clients (host-side).
        /// </summary>
        public void BroadcastBandFailed(int bandId, long finalScore, INetConnection? excludeConnection = null)
        {
            if (!_isHosting)
                return;
            
            var message = new Bands.BandFailedMessage
            {
                BandId = bandId,
                FinalScore = finalScore
            };
            
            var writer = new LiteNetLib.Utils.NetDataWriter();
            writer.Put((byte)PacketType.BandFailed);
            message.Serialize(writer);
            byte[] data = writer.CopyData();
            
            foreach (var connection in _connectionMap.Values)
            {
                if (excludeConnection != null && connection.Id == excludeConnection.Id)
                    continue;
                    
                connection.Send(data, ChannelType.ReliableOrdered); // Use reliable for important state changes
            }
        }
        
        #endregion
        
        #region Player Preset Sync
        
        /// <summary>
        /// Sends the local player's visual presets to the host for syncing.
        /// Called when EnablePresetSync is true and the player joins or changes their presets.
        /// </summary>
        /// <param name="playerName">Optional: specific player to send presets for. If null, sends for all local players.</param>
        public void SendLocalPlayerPresets(string playerName = null)
        {
            NetworkLogger.Verbose($"SendLocalPlayerPresets: playerName='{playerName ?? "ALL"}', isHosting={_isHosting}");
            
            if (_isHosting)
            {
                // Host updates their own presets locally and broadcasts
                UpdateHostPresets(playerName);
                return;
            }
            
            if (_serverConnection == null)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Cannot send presets - not connected to server");
                return;
            }
            
            // Get local players to send presets for
            var localPlayers = PlayerContainer.Players;
            if (localPlayers == null || localPlayers.Count == 0)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Cannot send presets - no local players");
                return;
            }
            
            NetworkLogger.Verbose($"Local player count: {localPlayers.Count}");
            
            // If specific player name provided, filter to just that player
            var playersToSync = string.IsNullOrEmpty(playerName)
                ? localPlayers
                : localPlayers.Where(p => p?.Profile?.Name == playerName);
            
            int syncCount = 0;
            foreach (var localPlayer in playersToSync)
            {
                if (localPlayer == null) continue;
                
                NetworkLogger.Verbose($"Sending preset sync for player '{localPlayer.Profile.Name}'");
                SendPresetSyncForPlayer(localPlayer);
                syncCount++;
            }
            
            NetworkLogger.Verbose($"Sent preset sync for {syncCount} player(s)");
        }
        
        /// <summary>
        /// Sends preset sync packet for a specific local player.
        /// </summary>
        private void SendPresetSyncForPlayer(YARG.Player.YargPlayer localPlayer)
        {
            if (_serverConnection == null || localPlayer == null) return;
            
            // Refresh presets to ensure we have the latest
            localPlayer.RefreshPresets();
            
            // Get preset IDs and JSON
            var cameraPresetId = localPlayer.CameraPreset?.Id ?? Guid.Empty;
            var highwayPresetId = localPlayer.HighwayPreset?.Id ?? Guid.Empty;
            var colorProfileId = localPlayer.ColorProfile?.Id ?? Guid.Empty;
            var themePresetId = localPlayer.ThemePreset?.Id ?? Guid.Empty;
            
            // Serialize presets to JSON for remote clients who might not have them locally
            string cameraJson = SerializePresetToJson(localPlayer.CameraPreset);
            string highwayJson = SerializePresetToJson(localPlayer.HighwayPreset);
            string colorJson = SerializePresetToJson(localPlayer.ColorProfile);
            string themeJson = SerializePresetToJson(localPlayer.ThemePreset);
            
            // Use the local player's network ID (must match by name)
            var networkId = GetLocalPlayerNetworkId(localPlayer.Profile.Name);
            var playerId = networkId ?? localPlayer.Profile.Id;
            
            NetworkLogger.Verbose($"SendPresetSyncForPlayer: name='{localPlayer.Profile.Name}', networkId={networkId?.ToString() ?? "NULL"}, playerId={playerId}");
            
            // Build and send the packet
            byte[] packet = PlayerPresetBinaryPackets.BuildPresetSyncPacket(
                playerId,
                cameraPresetId, cameraJson,
                highwayPresetId, highwayJson,
                colorProfileId, colorJson,
                themePresetId, themeJson);
            
            _serverConnection.Send(packet, ChannelType.ReliableOrdered);
            NetworkLogger.Verbose($"Client: Sent preset sync for player '{localPlayer.Profile.Name}' playerId={playerId}");
        }
        
        /// <summary>
        /// Updates host's own presets in NetworkPlayerData and broadcasts to clients.
        /// </summary>
        /// <param name="playerName">Optional: specific player to update. If null, updates all host players.</param>
        private void UpdateHostPresets(string playerName = null)
        {
            var localPlayers = PlayerContainer.Players;
            if (localPlayers == null || localPlayers.Count == 0) return;
            
            // If specific player name provided, filter to just that player
            var playersToSync = string.IsNullOrEmpty(playerName)
                ? localPlayers
                : localPlayers.Where(p => p?.Profile?.Name == playerName);
            
            foreach (var localPlayer in playersToSync)
            {
                if (localPlayer == null) continue;
                
                localPlayer.RefreshPresets();
                
                // Find this player's NetworkPlayerData
                if (_connectedPlayers.TryGetValue("host", out var hostPlayers))
                {
                    foreach (var hostData in hostPlayers)
                    {
                        if (hostData.PlayerName == localPlayer.Profile.Name)
                        {
                            UpdateNetworkPlayerPresets(hostData, localPlayer);
                            
                            // Broadcast to all clients
                            BroadcastPlayerPresets(hostData);
                            break;
                        }
                    }
                }
            }
        }
        
        /// <summary>
        /// Updates a NetworkPlayerData with preset info from a YargPlayer.
        /// </summary>
        private void UpdateNetworkPlayerPresets(NetworkPlayerData networkData, YARG.Player.YargPlayer player)
        {
            networkData.SetSyncedPresets(
                player.CameraPreset?.Id ?? Guid.Empty,
                SerializePresetToJson(player.CameraPreset),
                player.HighwayPreset?.Id ?? Guid.Empty,
                SerializePresetToJson(player.HighwayPreset),
                player.ColorProfile?.Id ?? Guid.Empty,
                SerializePresetToJson(player.ColorProfile),
                player.ThemePreset?.Id ?? Guid.Empty,
                SerializePresetToJson(player.ThemePreset));
        }
        
        /// <summary>
        /// Handles preset sync message from a client (host-side).
        /// </summary>
        private void HandlePlayerPresetSyncFromClient(INetConnection connection, ReadOnlyMemory<byte> payload)
        {
            if (!_isHosting) return;
            
            var parsed = PlayerPresetBinaryPackets.ParsePresetSyncPacket(payload.Span);
            if (!parsed.IsValid)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Host: Invalid preset sync packet from client");
                return;
            }
            
            string connectionKey = connection.Id.ToString();
            NetworkLogger.Verbose($"Host: Received preset sync for playerId={parsed.PlayerId} from connection {connectionKey}");
            
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                // Find the player from this connection by matching PlayerId
                NetworkPlayerData? targetPlayer = null;
                if (_connectedPlayers.TryGetValue(connectionKey, out var players))
                {
                    NetworkLogger.Verbose($"Host: Connection {connectionKey} has {players.Count} player(s)");
                    foreach (var p in players)
                    {
                        NetworkLogger.Verbose($"Host: Checking player '{p.PlayerName}' NetworkPlayerId={p.NetworkPlayerId}");
                    }
                    
                    // Find by PlayerId - must match exactly
                    targetPlayer = players.FirstOrDefault(p => p.NetworkPlayerId == parsed.PlayerId);
                }
                else
                {
                    NetworkLogger.Warn($"[LiteNetNetworkingAdapter] Host: No players found for connection {connectionKey}");
                }
                
                if (targetPlayer == null)
                {
                    NetworkLogger.Warn($"[LiteNetNetworkingAdapter] Host: Could not find player with NetworkPlayerId={parsed.PlayerId} for preset sync from connection {connectionKey}");
                    return;
                }
                
                // Store the preset data
                targetPlayer.SetSyncedPresets(
                    parsed.CameraPresetId, parsed.CameraPresetJson,
                    parsed.HighwayPresetId, parsed.HighwayPresetJson,
                    parsed.ColorProfileId, parsed.ColorProfileJson,
                    parsed.ThemePresetId, parsed.ThemePresetJson);
                
                NetworkLogger.Verbose($"Host: Updated presets for player '{targetPlayer.PlayerName}' (NetworkPlayerId={targetPlayer.NetworkPlayerId})");
                
                // Broadcast to all clients
                BroadcastPlayerPresets(targetPlayer);
            });
        }
        
        /// <summary>
        /// Handles preset sync broadcast from host (client-side).
        /// </summary>
        private void HandlePlayerPresetSyncBroadcast(ReadOnlyMemory<byte> payload)
        {
            if (_isHosting) return;
            
            var parsed = PlayerPresetBinaryPackets.ParsePresetSyncPacket(payload.Span);
            if (!parsed.IsValid)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Client: Invalid preset sync broadcast");
                return;
            }
            
            NetworkLogger.Verbose($"Client: Received preset sync for player {parsed.PlayerId}");
            
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                // Find the player by PlayerId in all connected players
                NetworkPlayerData? targetPlayer = null;
                foreach (var kvp in _connectedPlayers)
                {
                    targetPlayer = kvp.Value.FirstOrDefault(p => p.NetworkPlayerId == parsed.PlayerId);
                    if (targetPlayer != null) break;
                }
                
                // Also check connection-based dictionary
                if (targetPlayer == null)
                {
                    foreach (var kvp in _connectedPlayersByConnection)
                    {
                        targetPlayer = kvp.Value.FirstOrDefault(p => p.NetworkPlayerId == parsed.PlayerId);
                        if (targetPlayer != null) break;
                    }
                }
                
                if (targetPlayer == null)
                {
                    // Player not found yet - store in pending cache for later
                    NetworkLogger.Verbose($"Client: Player {parsed.PlayerId} not found, caching preset for later");
                    _pendingPresets[parsed.PlayerId] = (
                        parsed.CameraPresetId, parsed.CameraPresetJson,
                        parsed.HighwayPresetId, parsed.HighwayPresetJson,
                        parsed.ColorProfileId, parsed.ColorProfileJson,
                        parsed.ThemePresetId, parsed.ThemePresetJson);
                    return;
                }
                
                // Store the preset data
                targetPlayer.SetSyncedPresets(
                    parsed.CameraPresetId, parsed.CameraPresetJson,
                    parsed.HighwayPresetId, parsed.HighwayPresetJson,
                    parsed.ColorProfileId, parsed.ColorProfileJson,
                    parsed.ThemePresetId, parsed.ThemePresetJson);
                
                NetworkLogger.Verbose($"Client: Updated presets for player '{targetPlayer.PlayerName}'");
            });
        }
        
        /// <summary>
        /// Applies any pending preset sync data to players that have been added.
        /// Called after new players are created to apply presets that were received before the player existed.
        /// </summary>
        private void ApplyPendingPresets(IEnumerable<NetworkPlayerData> players)
        {
            if (_pendingPresets.Count == 0) return;
            
            foreach (var player in players)
            {
                if (_pendingPresets.TryGetValue(player.NetworkPlayerId, out var presetData))
                {
                    player.SetSyncedPresets(
                        presetData.CameraPresetId, presetData.CameraPresetJson,
                        presetData.HighwayPresetId, presetData.HighwayPresetJson,
                        presetData.ColorProfileId, presetData.ColorProfileJson,
                        presetData.ThemePresetId, presetData.ThemePresetJson);
                    
                    _pendingPresets.Remove(player.NetworkPlayerId);
                    NetworkLogger.Verbose($"Applied pending preset data to player '{player.PlayerName}'");
                }
            }
        }
        
        /// <summary>
        /// Broadcasts a player's preset data to all clients (host-side).
        /// </summary>
        private void BroadcastPlayerPresets(NetworkPlayerData player)
        {
            if (!_isHosting) return;
            
            byte[] packet = PlayerPresetBinaryPackets.BuildPresetSyncPacket(
                player.NetworkPlayerId,
                player.CameraPresetId, player.CameraPresetJson,
                player.HighwayPresetId, player.HighwayPresetJson,
                player.ColorProfileId, player.ColorProfileJson,
                player.ThemePresetId, player.ThemePresetJson);
            
            foreach (var conn in _connectionMap.Values)
            {
                conn.Send(packet, ChannelType.ReliableOrdered);
            }
            
            NetworkLogger.Verbose($"Host: Broadcast presets for player '{player.PlayerName}' to {_connectionMap.Count} clients");
        }
        
        /// <summary>
        /// Gets the local player's network ID if available.
        /// </summary>
        /// <param name="playerName">The name of the player to get the network ID for.</param>
        private Guid? GetLocalPlayerNetworkId(string playerName)
        {
            string localKey = _isHosting ? "host" : "local";
            
            if (_connectedPlayers.TryGetValue(localKey, out var players))
            {
                foreach (var player in players)
                {
                    if (player.PlayerName == playerName)
                    {
                        return player.NetworkPlayerId;
                    }
                }
                NetworkLogger.Warn($"GetLocalPlayerNetworkId: No matching player found for '{playerName}'");
            }
            else
            {
                NetworkLogger.Warn($"GetLocalPlayerNetworkId: No '{localKey}' bucket found in _connectedPlayers");
            }
            return null;
        }
        
        // JSON settings for preset serialization - must match CustomContent and YargPlayer settings
        private static readonly Newtonsoft.Json.JsonSerializerSettings PresetJsonSettings = new()
        {
            Formatting = Newtonsoft.Json.Formatting.Indented,
            Converters = new System.Collections.Generic.List<Newtonsoft.Json.JsonConverter>
            {
                new YARG.Core.Utility.JsonColorConverter(),
                new YARG.Helpers.JsonVector2Converter()
            }
        };
        
        /// <summary>
        /// Serializes a preset to JSON for network transmission.
        /// Uses the same JSON settings as CustomContent for proper Color and Vector2 handling.
        /// </summary>
        private string SerializePresetToJson<T>(T preset) where T : class
        {
            if (preset == null) return string.Empty;
            try
            {
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(preset, PresetJsonSettings);
                NetworkLogger.Verbose($"Serialized preset to JSON, length={json?.Length ?? 0}");
                return json;
            }
            catch (Exception ex)
            {
                NetworkLogger.Warn($"[LiteNetNetworkingAdapter] Failed to serialize preset: {ex.Message}");
                return string.Empty;
            }
        }
        
        #endregion
        
        /// <summary>
        /// Sends authentication request to server when connecting to a password-protected lobby.
        /// Now includes player identities to avoid the unreliable HandshakeRequest packet.
        /// </summary>
        private void SendAuthRequest(INetConnection connection)
        {
            string password = _pendingPassword ?? "";
            
            // Get the number of local profiles to send to server for capacity checking
            int profileCount = GetLocalProfileCount();
            
            // Include player identities in the auth request (combined auth+handshake)
            var identities = GetAllLocalPlayerIdentities();
            NetworkLogger.Info($"[Client] Sending auth request with {identities.Count} player identities embedded");
            
            byte[] message = AuthBinaryPackets.BuildRequestPacket(password, profileCount, identities);
            
            connection.Send(message, ChannelType.ReliableOrdered);
            NetworkLogger.Info($" Client: Sent authentication request with {profileCount} profile(s) and {identities.Count} identities, length={message.Length}");
        }
        
        /// <summary>
        /// Gets the count of local profiles with active controller bindings.
        /// Only counts profiles that are actually connected.
        /// Returns minimum of 1 for safety (prevents 0-profile edge cases in networking).
        /// </summary>
        private int GetLocalProfileCount()
        {
            var localPlayers = PlayerContainer.Players;
            if (localPlayers != null && localPlayers.Count > 0)
            {
                return localPlayers.Count;
            }
            
            // Return 1 as minimum - if no players are connected, treat as 1 for slot reservation
            // This prevents edge cases where 0 profiles could bypass slot validation
            return 1;
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
                    NetworkLogger.Client($"Browsing state updated to {isBrowsing}");
                    OnBrowsingStateChanged?.Invoke(isBrowsing);
                }
                return;
            }
            
            _isBrowsingSongs = isBrowsing;
            NetworkLogger.Info($"Host: Setting browsing state to {isBrowsing}");
            
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
            NetworkLogger.Info($"Host: Broadcasting lobby state (browsing={_isBrowsingSongs}) to all clients");
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
            
            NetworkLogger.Info($"Host: Sending lobby state (browsing={_isBrowsingSongs}) to new client {connection.EndPoint}");
            
            try
            {
                connection.Send(message, ChannelType.ReliableOrdered);
                
                // Also send all existing players' states (including host) so client can display icons
                SendAllPlayerStatesToClient(connection);
                
                // Send current session settings to the new client
                SendSessionSettingsToClient(connection);
                
                // Send the current host player ID to the new client
                // This ensures clients know who the host is when processing band assignments
                if (_currentLobby?.IsDedicatedServer == true)
                {
                    // For dedicated servers, use DedicatedServerManager's host
                    var dedicatedManager = DedicatedServerManager.Instance;
                    if (dedicatedManager != null && dedicatedManager.CurrentHostPlayerId != Guid.Empty)
                    {
                        SendHostChangeToClient(connection, dedicatedManager.CurrentHostPlayerId);
                    }
                }
                else
                {
                    // For regular hosted lobbies, find the host player from our connected players
                    Guid hostPlayerId = Guid.Empty;
                    foreach (var kvp in _connectedPlayers)
                    {
                        foreach (var player in kvp.Value)
                        {
                            if (player.IsHost && player.NetworkPlayerId != Guid.Empty)
                            {
                                hostPlayerId = player.NetworkPlayerId;
                                break;
                            }
                        }
                        if (hostPlayerId != Guid.Empty) break;
                    }
                    
                    if (hostPlayerId != Guid.Empty)
                    {
                        SendHostChangeToClient(connection, hostPlayerId);
                        NetworkLogger.Info($"Host: Sent host player ID {hostPlayerId} to new client");
                    }
                    else
                    {
                        NetworkLogger.Warn("Host: Could not find host player ID to send to new client");
                    }
                }
            }
            catch (Exception ex)
            {
                NetworkLogger.Error($"[LiteNetNetworkingAdapter] Host: Failed to send lobby state to new client: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Sends the current session settings to a specific client connection.
        /// </summary>
        private void SendSessionSettingsToClient(INetConnection connection)
        {
            if (!_isHosting || _currentLobby == null)
                return;
            
            // Get settings from MultiplayerGameplaySettings or SessionLifecycleManager
            var gameplaySettings = MultiplayerGameplaySettings.Instance;
            var sessionLifecycle = SessionLifecycleManager.Instance;
            var preset = sessionLifecycle?.CurrentPreset ?? gameplaySettings?.ActivePreset;
            
            if (preset == null)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Host: No preset available to send session settings");
                return;
            }
            
            var allowedModes = preset.allowedInstruments ?? new List<int>();
            
            byte[] settingsMessage = YARG.Net.Packets.SessionSettingsBinaryPackets.BuildSessionSettingsSyncPacket(
                _currentLobby.LobbyName ?? string.Empty,
                _currentLobby.MaxPlayers,
                (byte)_currentLobby.PrivacyMode,
                preset.bandSize,
                preset.noFailMode,
                preset.sharedSongsOnly,
                preset.AllowModifiers,
                preset.enablePresetSync,
                preset.allowLateJoin,
                allowedModes,
                preset.LocalPlayersFirst);
            
            NetworkLogger.Verbose($"Host: Sending session settings to client {connection.EndPoint} (gameModes={allowedModes.Count}, LocalPlayersFirst={preset.LocalPlayersFirst})");
            
            try
            {
                connection.Send(settingsMessage, ChannelType.ReliableOrdered);
                
                // If preset sync is enabled, also send existing players' preset data to the new client
                if (preset.enablePresetSync)
                {
                    SendAllPlayerPresetsToClient(connection);
                }
            }
            catch (Exception ex)
            {
                NetworkLogger.Error($"[LiteNetNetworkingAdapter] Host: Failed to send session settings to new client: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Sends all existing players' preset data to a new client (host-side).
        /// Called when EnablePresetSync is true and a new client joins.
        /// </summary>
        private void SendAllPlayerPresetsToClient(INetConnection connection)
        {
            NetworkLogger.Verbose($"Host: Sending all player presets to new client {connection.EndPoint}");
            
            foreach (var kvp in _connectedPlayers)
            {
                foreach (var player in kvp.Value)
                {
                    if (player == null || !player.HasSyncedPresets) continue;
                    
                    byte[] packet = PlayerPresetBinaryPackets.BuildPresetSyncPacket(
                        player.NetworkPlayerId,
                        player.CameraPresetId, player.CameraPresetJson,
                        player.HighwayPresetId, player.HighwayPresetJson,
                        player.ColorProfileId, player.ColorProfileJson,
                        player.ThemePresetId, player.ThemePresetJson);
                    
                    connection.Send(packet, ChannelType.ReliableOrdered);
                    NetworkLogger.Verbose($"Host: Sent preset sync for '{player.PlayerName}' to new client");
                }
            }
        }
        
        /// <summary>
        /// Sends all players' ready/instrument states to a new client.
        /// Only sends states for players OTHER than the joining client (since their state is still defaults).
        /// </summary>
        private void SendAllPlayerStatesToClient(INetConnection connection)
        {
            string newClientConnKey = connection.Id.ToString();
            
            foreach (var kvp in _connectedPlayers)
            {
                // Skip the newly connected client's own player data - they just joined and have defaults
                // Their actual state will be sent after they send their state to us
                if (kvp.Key.ToString() == newClientConnKey)
                {
                    NetworkLogger.Info($"Host: Skipping state for new client's own player (will update after receiving their state)");
                    continue;
                }
                
                foreach (var player in kvp.Value)
                {
                    if (player == null) continue;
                    
                    // This is NOT about the new client (it's about other players like the host)
                    bool isAboutThisClient = false;
                    
                    // CRITICAL: Include NetworkPlayerId so clients can match players correctly (especially for host detection in dedicated server mode)
                    byte[] stateMessage = _readyStateHandler.BuildHostBroadcastMessage(
                        player.PlayerName, 
                        player.IsReady, 
                        isAboutThisClient, 
                        player.Instrument, 
                        player.Difficulty,
                        player.SittingOut,
                        player.NetworkPlayerId);
                    
                    connection.Send(stateMessage, ChannelType.ReliableOrdered);
                    NetworkLogger.Info($"Host: Sent player state for '{player.PlayerName}' to new client: instrument={player.Instrument}, difficulty={player.Difficulty}, networkPlayerId={player.NetworkPlayerId}");
                }
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
            NetworkLogger.Info($"RequestAddToSetlist: {songHash} from {playerName}");
            
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
            NetworkLogger.Info($"RequestRemoveFromSetlist: {songHash} from {playerName}");
            
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
        /// Internal implementation that starts the show. Called by the unified StartShow() method.
        /// </summary>
        private void StartShowInternal()
        {
            if (_setlistManager == null || _setlistManager.IsEmpty)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Cannot start show with empty setlist");
                return;
            }
            
            NetworkLogger.Info($"Host: Starting show with {_setlistManager.Count} songs");;
            
            // Get the first song hash for session phase tracking
            var firstSongHash = _setlistManager.SongHashes.FirstOrDefault() ?? string.Empty;
            SetSessionPhase(SessionPhase.DifficultySelect, firstSongHash);
            
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
                NetworkLogger.Info($"Song {songHash} already in setlist or setlist full");
                return;
            }
            
            NetworkLogger.Info($"Added {songHash} to setlist (now {_setlistManager.Count} songs)");
            
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
                NetworkLogger.Info($"Song {songHash} not in setlist");
                return;
            }
            
            NetworkLogger.Info($"Removed {songHash} from setlist (now {_setlistManager.Count} songs)");
            
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
                    NetworkLogger.Client($"Sent setlist add request for {songHash}");
                    break;
                }
                catch (Exception ex)
                {
                    NetworkLogger.Error($"[LiteNetNetworkingAdapter] Client: Failed to send setlist add: {ex.Message}");
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
                    NetworkLogger.Client($"Sent setlist remove request for {songHash}");
                    break;
                }
                catch (Exception ex)
                {
                    NetworkLogger.Error($"[LiteNetNetworkingAdapter] Client: Failed to send setlist remove: {ex.Message}");
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
            
            NetworkLogger.Info($"Host: Broadcasting start show with {songHashes.Count} songs");
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
            var entries = _setlistManager.GetAllEntries()
                .Select(e => new SetlistEntry(e.SongHash, e.SongName, e.SongArtist, e.AddedByPlayerName))
                .ToList();
            var message = SetlistBinaryPackets.BuildSyncPacket(entries);
            
            NetworkLogger.Info($"Host: Sending setlist sync ({_setlistManager.Count} songs) to {connection.EndPoint}");
            
            try
            {
                connection.Send(message, ChannelType.ReliableOrdered);
            }
            catch (Exception ex)
            {
                NetworkLogger.Error($"[LiteNetNetworkingAdapter] Host: Failed to send setlist sync: {ex.Message}");
            }
        }
        
        private void HandleSetlistAddMessage(INetConnection connection, ReadOnlyMemory<byte> payload)
        {
            if (!SetlistBinaryPackets.TryParseAddOrRemove(payload.Span, out var entry))
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Invalid setlist add message");
                return;
            }
            
            string songHash = entry.SongHash;
            string playerName = entry.PlayerName;
            string songName = entry.SongName;
            string songArtist = entry.ArtistName;
            
            NetworkLogger.Info($"Received setlist add: {songHash} from {playerName}");
            
            // Dispatch to main thread for Unity API safety
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                if (_isHosting)
                {
                    // Host: add and broadcast to all
                    AddToSetlistLocal(songHash, playerName, songName, songArtist);
                }
                else
                {
                    // Client: just update local
                    // Note: OnSetlistSongAdded and OnSetlistUpdated events are fired by the manager bridge
                    _setlistManager.TryAdd(songHash, songName, songArtist, playerName, out _);
                }
            });
        }
        
        private void HandleSetlistRemoveMessage(INetConnection connection, ReadOnlyMemory<byte> payload)
        {
            if (!SetlistBinaryPackets.TryParseAddOrRemove(payload.Span, out var entry))
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Invalid setlist remove message");
                return;
            }
            
            string songHash = entry.SongHash;
            string playerName = entry.PlayerName;
            string songName = entry.SongName;
            string songArtist = entry.ArtistName;
            
            NetworkLogger.Info($"Received setlist remove: {songHash} from {playerName}");
            
            // Dispatch to main thread for Unity API safety
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                if (_isHosting)
                {
                    // Host: remove and broadcast to all
                    RemoveFromSetlistLocal(songHash, playerName, songName, songArtist);
                }
                else
                {
                    // Client: just update local
                    // Note: OnSetlistSongRemoved and OnSetlistUpdated events are fired by the manager bridge
                    _setlistManager.TryRemove(songHash, out _);
                }
            });
        }
        
        private void HandleSetlistSyncMessage(ReadOnlyMemory<byte> payload)
        {
            if (!SetlistBinaryPackets.TryParseSyncPacket(payload.Span, out var entries))
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Invalid setlist sync message");
                return;
            }
            
            NetworkLogger.Client($"Received setlist sync: {entries.Count} entries");
            
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                _setlistManager.Clear();
                foreach (var entry in entries)
                {
                    _setlistManager.TryAdd(entry.SongHash, entry.SongName, entry.ArtistName, entry.PlayerName, out _);
                }
                NetworkLogger.Client($"Setlist synced with {_setlistManager.Count} songs");
                OnSetlistUpdated?.Invoke();
            });
        }
        
        private void HandleSetlistStartMessage(ReadOnlyMemory<byte> payload)
        {
            if (!SetlistBinaryPackets.TryParseStartPacket(payload.Span, out var songHashes))
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Invalid setlist start message");
                return;
            }
            
            NetworkLogger.Client($"Received start show with {songHashes.Count} songs");
            
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                // Sync the local setlist manager with the authoritative list from the host
                // This ensures we have the correct songs even if broadcasts arrived out of order
                SyncSetlistFromHashes(songHashes);
                
                ApplySetlistToGlobalState();
                OnShowStarted?.Invoke();
                
                // Navigate to difficulty select
                MenuManager.Instance.PushMenu(MenuManager.Menu.DifficultySelect);
            });
        }
        
        /// <summary>
        /// Syncs the local setlist manager with an authoritative list of song hashes from the host.
        /// This is used when receiving a start show command to ensure the client has the correct setlist.
        /// </summary>
        private void SyncSetlistFromHashes(List<string> authorativeHashes)
        {
            if (_setlistManager == null)
                return;
            
            // Get current local hashes
            var localHashes = _setlistManager.SongHashes.ToList();
            
            // Add any missing songs
            foreach (var hash in authorativeHashes)
            {
                if (!localHashes.Contains(hash))
                {
                    // Look up song info
                    var hashWrapper = YARG.Core.Song.HashWrapper.FromString(hash);
                    string songName = "Unknown";
                    string songArtist = "Unknown";
                    
                    if (SongContainer.SongsByHash.TryGetValue(hashWrapper, out var songList) && songList.Count > 0)
                    {
                        songName = songList[0].Name;
                        songArtist = songList[0].Artist;
                    }
                    
                    _setlistManager.TryAdd(hash, songName, songArtist, "Host", out _);
                    NetworkLogger.Info($"Synced missing song to setlist: {hash}");
                }
            }
            
            // Remove any songs not in the authoritative list
            foreach (var hash in localHashes)
            {
                if (!authorativeHashes.Contains(hash))
                {
                    _setlistManager.TryRemove(hash, out _);
                    NetworkLogger.Info($"Removed extra song from setlist: {hash}");
                }
            }
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
            
            NetworkLogger.Info($"Applied setlist to GlobalState: {showSongs.Count} songs");
        }
        
        #region Ready State Messages
        
        /// <summary>
        /// Sets the local player's ready state and broadcasts it.
        /// </summary>
        public void SetPlayerReady(bool isReady, bool sittingOut = false)
        {
            // Default to first player
            var (playerName, instrumentValue, difficultyValue) = _readyStateHandler.GetLocalPlayerInfo();
            SetPlayerReadyInternal(isReady, playerName, instrumentValue, difficultyValue, sittingOut);
        }

        public void SetPlayerReady(bool isReady, string playerName, bool sittingOut = false)
        {
            // Find the local player with this name to get their instrument/difficulty
            int instrumentValue = 0;
            int difficultyValue = 0;
            
            var localPlayers = PlayerContainer.Players;
            if (localPlayers != null)
            {
                foreach (var player in localPlayers)
                {
                    if (player?.Profile?.Name == playerName)
                    {
                        instrumentValue = (int)player.Profile.CurrentInstrument;
                        difficultyValue = (int)player.Profile.CurrentDifficulty;
                        break;
                    }
                }
            }
            
            SetPlayerReadyInternal(isReady, playerName, instrumentValue, difficultyValue, sittingOut);
        }

        private void SetPlayerReadyInternal(bool isReady, string playerName, int instrumentValue, int difficultyValue, bool sittingOut = false)
        {
            NetworkLogger.Info($"[SetPlayerReady] Player '{playerName}' ready={isReady} sittingOut={sittingOut} | IsHosting={_isHosting} | _transport={(_transport != null ? "exists" : "NULL")} | _connectionMap.Count={_connectionMap.Count}");
            
            // Update local player data by name
            UpdateLocalPlayerReadyStateByName(_isHosting, playerName, isReady, instrumentValue, difficultyValue, sittingOut);
            
            if (_isHosting)
            {
                // Host: update directly and broadcast to all clients
                // Use "host" as the source connection key - this tells clients it's NOT their local player
                
                // Find the host player to get their NetworkPlayerId
                Guid hostNetworkPlayerId = Guid.Empty;
                if (_connectedPlayers.TryGetValue("host", out var hostPlayers))
                {
                    foreach (var p in hostPlayers)
                    {
                        if (p.PlayerName == playerName)
                        {
                            hostNetworkPlayerId = p.NetworkPlayerId;
                            break;
                        }
                    }
                }
                
                BroadcastPlayerReadyStateTargeted("host", playerName, isReady, instrumentValue, difficultyValue, sittingOut, hostNetworkPlayerId);
                
                // Fire local event on host
                OnPlayerReadyStateChanged?.Invoke(playerName, isReady);
                
                // Check if all players are ready
                CheckAllPlayersReady();
                
                // If preset sync is enabled, update and broadcast host's presets
                var gameplaySettings = MultiplayerGameplaySettings.Instance;
                if (gameplaySettings?.EnablePresetSync == true)
                {
                    UpdateHostPresets();
                }
            }
            else if (_transport != null)
            {
                // Client: send to host using handler-built message
                NetworkLogger.Info($"[SetPlayerReady] Client: Building ready message...");
                
                try
                {
                    var message = _readyStateHandler.BuildClientReadyMessage(playerName, isReady, instrumentValue, difficultyValue, sittingOut);
                    NetworkLogger.Info($"[SetPlayerReady] Client: Built message with {message?.Length ?? 0} bytes");
                    
                    // Log first bytes for debugging
                    if (message != null && message.Length >= 4)
                    {
                        NetworkLogger.Info($"[SetPlayerReady] Client: Message bytes [0]={message[0]} (PacketType), [1]={message[1]} (isReady), [2]={message[2]}, [3]={message[3]} (nameLenHigh/Low)");
                    }
                    
                    if (_connectionMap.Count == 0)
                    {
                        NetworkLogger.Warn("[SetPlayerReady] Client has no connections in _connectionMap - cannot send ready state!");
                        return;
                    }
                    
                    NetworkLogger.Info($"[SetPlayerReady] Client: About to iterate _connectionMap with {_connectionMap.Count} entries");
                    int sentCount = 0;
                    foreach (var conn in _connectionMap.Values)
                    {
                        NetworkLogger.Info($"[SetPlayerReady] Client: Sending to connection {conn?.GetType().Name ?? "NULL"} endpoint={conn?.EndPoint}");
                        conn.Send(message, ChannelType.ReliableOrdered);
                        NetworkLogger.Client($"Sent ready state ({isReady}, instrument: {instrumentValue}, difficulty: {difficultyValue}) to host");
                        sentCount++;
                        break;
                    }
                    NetworkLogger.Info($"[SetPlayerReady] Client: Sent to {sentCount} connection(s)");
                    
                    // If preset sync is enabled, also send the client's preset data
                    var gameplaySettings = MultiplayerGameplaySettings.Instance;
                    if (gameplaySettings?.EnablePresetSync == true)
                    {
                        SendLocalPlayerPresets(playerName);
                    }
                }
                catch (Exception ex)
                {
                    NetworkLogger.Error($"[SetPlayerReady] Client: Exception sending ready state: {ex.Message}\n{ex.StackTrace}");
                }
            }
            else
            {
                NetworkLogger.Warn("[SetPlayerReady] Client: _transport is null - cannot send ready state!");
            }
        }

        /// <summary>
        /// Updates a local player's ready state by their name.
        /// </summary>
        private void UpdateLocalPlayerReadyStateByName(bool isHosting, string playerName, bool isReady, int instrument, int difficulty, bool sittingOut = false)
        {
            // Find the local player with this name and update their state
            string targetKey = isHosting ? "host" : "local";
            
            if (_connectedPlayers.TryGetValue(targetKey, out var players))
            {
                foreach (var player in players)
                {
                    if (player.PlayerName == playerName)
                    {
                        _readyStateHandler.SetPlayerReadyState(player, isReady);
                        if (instrument >= 0 && difficulty >= 0)
                        {
                            _readyStateHandler.SetPlayerInstrumentAndDifficulty(player, instrument, difficulty);
                        }
                        player.SittingOut = sittingOut;
                        NetworkLogger.Info($"[UpdateLocalPlayerReadyStateByName] Updated player '{playerName}' to ready={isReady}, sittingOut={sittingOut}");
                        return;
                    }
                }
            }
            
            NetworkLogger.Warn($"[UpdateLocalPlayerReadyStateByName] Could not find local player '{playerName}' in '{targetKey}' bucket");
        }
        
        private void UpdateLocalPlayerReadyState(bool isReady, int instrument = -1, int difficulty = -1)
        {
            // Delegate to handler
            _readyStateHandler.UpdateLocalPlayerReadyState(_isHosting, isReady, instrument, difficulty, _connectedPlayers);
        }
        
        private void SetPlayerReadyState(NetworkPlayerData player, bool isReady)
        {
            // Delegate to handler
            _readyStateHandler.SetPlayerReadyState(player, isReady);
        }
        
        private void SetPlayerInstrumentAndDifficulty(NetworkPlayerData player, int instrument, int difficulty)
        {
            // Delegate to handler
            _readyStateHandler.SetPlayerInstrumentAndDifficulty(player, instrument, difficulty);
        }
        
        /// <summary>
        /// Resets all players' ready states to false without firing events.
        /// Used when transitioning to gameplay so ready states don't persist to score screen.
        /// </summary>
        private void ResetAllPlayerReadyStates()
        {
            // Delegate to handler
            _readyStateHandler.ResetAllPlayerReadyStates(_connectedPlayers);
        }
        
        /// <summary>
        /// Public interface method to reset all players' ready states without firing events.
        /// Used when entering score screen to ensure clean state before subscribing to events.
        /// </summary>
        public void ResetAllPlayersReadyState()
        {
            NetworkLogger.Info(" ResetAllPlayersReadyState called (public interface method)");
            ResetAllPlayerReadyStates();
        }
        
        /// <summary>
        /// Resets all players' gameplay state (score, combo, etc.) for starting a new song.
        /// </summary>
        private void ResetAllPlayersGameState()
        {
            // Delegate to handler
            _readyStateHandler.ResetAllPlayersGameState(_connectedPlayers);
        }
        
        private void SendPlayerReadyToHost(string playerName, bool isReady, int instrument, int difficulty)
        {
            if (_transport == null) return;
            
            // Build message using handler
            byte[] message = _readyStateHandler.BuildClientReadyMessage(playerName, isReady, instrument, difficulty);
            
            // Send to server (first connection in the map when not hosting)
            foreach (var conn in _connectionMap.Values)
            {
                conn.Send(message, ChannelType.ReliableOrdered);
                NetworkLogger.Client($"Sent ready state ({isReady}, instrument: {instrument}, difficulty: {difficulty}) to host");
                break;
            }
        }
        
        private void HandlePlayerReadyMessage(INetConnection connection, ReadOnlyMemory<byte> payload)
        {
            NetworkLogger.Info($"[HandlePlayerReadyMessage] Called! IsHosting={_isHosting}, payload.Length={payload.Length}, connection={connection.EndPoint}");
            
            if (_isHosting)
            {
                // Parse the client message using handler
                var data = _readyStateHandler.ParseClientReadyMessage(payload);
                if (!data.IsValid)
                {
                    NetworkLogger.Warn("[LiteNetNetworkingAdapter] Invalid ready state message from client");
                    return;
                }
                
                string connectionKey = connection.Id.ToString();
                NetworkLogger.Info($"Host: Received ready state for '{data.PlayerName}' from connection {connectionKey}: {data.IsReady}, sittingOut={data.SittingOut}");
                
                UnityMainThreadDispatcher.EnqueueAction(() =>
                {
                    // Use handler to process the ready state update
                    var targetPlayer = _readyStateHandler.HandleReadyMessageOnHost(connectionKey, data, _connectedPlayers);
                    
                    if (targetPlayer != null)
                    {
                        // Also set the sitting out state on the player
                        targetPlayer.SittingOut = data.SittingOut;
                        
                        // Broadcast to all clients - include the player's NetworkPlayerId
                        BroadcastPlayerReadyStateTargeted(connectionKey, targetPlayer.PlayerName, data.IsReady, data.Instrument, data.Difficulty, data.SittingOut, targetPlayer.NetworkPlayerId);
                    }
                    
                    // Check if all players are ready
                    CheckAllPlayersReady();
                });
            }
            else
            {
                // Parse the host broadcast message using handler
                var data = _readyStateHandler.ParseHostBroadcastMessage(payload);
                if (!data.IsValid)
                {
                    NetworkLogger.Warn("[LiteNetNetworkingAdapter] Invalid ready state broadcast from host");
                    return;
                }
                
                NetworkLogger.Client($"Received ready state for '{data.PlayerName}': ready={data.IsReady}, isLocalPlayer={data.IsLocalPlayer}, instrument={data.Instrument}, difficulty={data.Difficulty}, sittingOut={data.SittingOut}");
                
                UnityMainThreadDispatcher.EnqueueAction(() =>
                {
                    // Use handler to process the broadcast - returns the player found/updated
                    var foundPlayer = _readyStateHandler.HandleBroadcastOnClient(data, _connectedPlayers);
                    
                    // Set the sitting out state on the found player
                    if (foundPlayer != null)
                    {
                        foundPlayer.SittingOut = data.SittingOut;
                    }
                    
                    // If this is a host player (not local) and we didn't find them, create a new entry
                    // This handles multiple host profiles - the client only creates 1 placeholder initially
                    // NOTE: For dedicated servers, create remote players instead of host players.
                    // IMPORTANT: In dedicated server mode, the server broadcasts our own ready state back to us
                    // with IsLocalPlayer=false (from server's perspective). We must NOT create a duplicate!
                    bool isDedicatedServerClient = _currentLobby?.IsDedicatedServer == true;
                    
                    // NOTE: We do NOT check for duplicates by name here. Two different players CAN have
                    // the same name. The IsLocalPlayer flag tells us whether this update is about our
                    // local player (true) or a remote player (false). If foundPlayer is null and
                    // IsLocalPlayer is false, it means this is a new remote player we haven't seen yet.
                    
                    if (foundPlayer == null && !data.IsLocalPlayer)
                    {
                        if (!isDedicatedServerClient)
                        {
                            // Regular hosted lobby: create in "host" bucket with isHost: true
                            NetworkLogger.Client($"Creating new host player entry for '{data.PlayerName}' (host has multiple profiles), NetworkPlayerId={data.NetworkPlayerId}");
                            
                            // Create a new host player with isHost: true, using HostConnectionId
                            var newHostPlayer = CreateLiteNetPlayerData(data.PlayerName, isHost: true, isLocal: false, data.Instrument, data.Difficulty, connectionId: HostConnectionId);
                            
                            // Set sitting out state on the new player
                            newHostPlayer.SittingOut = data.SittingOut;
                            
                            // Use NetworkPlayerId from packet if available, otherwise fall back to expected host player IDs list
                            if (data.NetworkPlayerId != Guid.Empty)
                            {
                                newHostPlayer.NetworkPlayerId = data.NetworkPlayerId;
                                NetworkLogger.Client($"Assigned NetworkPlayerId {data.NetworkPlayerId} to new host player '{data.PlayerName}' (from packet)");
                            }
                            else if (_connectedPlayers.TryGetValue("host", out var existingHostPlayers))
                            {
                                // Legacy fallback: use expected host player IDs list
                                int hostCount = existingHostPlayers.Count;
                                if (hostCount < _expectedHostPlayerIds.Count)
                                {
                                    newHostPlayer.NetworkPlayerId = _expectedHostPlayerIds[hostCount];
                                    NetworkLogger.Client($"Assigned NetworkPlayerId {_expectedHostPlayerIds[hostCount]} to new host player '{data.PlayerName}' (from expected list)");
                                }
                            }
                            
                            // Set ready state
                            _readyStateHandler.SetPlayerReadyState(newHostPlayer, data.IsReady);
                            
                            // Add to the host bucket (both legacy and new)
                            if (!_connectedPlayers.TryGetValue("host", out var hostPlayers))
                            {
                                hostPlayers = new List<NetworkPlayerData>();
                                _connectedPlayers["host"] = hostPlayers;
                            }
                            hostPlayers.Add(newHostPlayer);
                            
                            // Apply any pending preset sync data to this newly added player
                            ApplyPendingPresets(new[] { newHostPlayer });
                            
                            if (!_connectedPlayersByConnection.TryGetValue(HostConnectionId, out var hostPlayersByConn))
                            {
                                hostPlayersByConn = new List<NetworkPlayerData>();
                                _connectedPlayersByConnection[HostConnectionId] = hostPlayersByConn;
                            }
                            hostPlayersByConn.Add(newHostPlayer);
                            
                            // Fire player joined event so UI can update
                            OnPlayerJoined?.Invoke(newHostPlayer);
                            
                            NetworkLogger.Client($"Added new host player '{data.PlayerName}' to connected players (total host profiles: {hostPlayers.Count}, ConnectionId={HostConnectionId})");
                        }
                        else
                        {
                            // Dedicated server: create in "remote" bucket with isHost based on pending host ID
                            NetworkLogger.Client($"Creating remote player entry for '{data.PlayerName}' (dedicated server mode), NetworkPlayerId={data.NetworkPlayerId}");
                            
                            // Create a remote player - host status will be set by ApplyPendingHostPlayerId
                            var remotePlayer = CreateLiteNetPlayerData(data.PlayerName, isHost: false, isLocal: false, data.Instrument, data.Difficulty, connectionId: Guid.Empty);
                            
                            // CRITICAL: Set the NetworkPlayerId from the packet so host matching works
                            remotePlayer.NetworkPlayerId = data.NetworkPlayerId;
                            
                            // Set sitting out state
                            remotePlayer.SittingOut = data.SittingOut;
                            
                            // Set ready state
                            _readyStateHandler.SetPlayerReadyState(remotePlayer, data.IsReady);
                            
                            // Add to the remote bucket
                            if (!_connectedPlayers.TryGetValue("remote", out var remotePlayers))
                            {
                                remotePlayers = new List<NetworkPlayerData>();
                                _connectedPlayers["remote"] = remotePlayers;
                            }
                            remotePlayers.Add(remotePlayer);
                            
                            // Apply any pending preset sync data
                            ApplyPendingPresets(new[] { remotePlayer });
                            
                            // Apply pending host status in case this player is the designated host
                            ApplyPendingHostPlayerId();
                            
                            // Fire player joined event so UI can update
                            OnPlayerJoined?.Invoke(remotePlayer);
                            
                            NetworkLogger.Client($"Added remote player '{data.PlayerName}' to connected players (dedicated server), NetworkPlayerId={remotePlayer.NetworkPlayerId}");
                        }
                    }
                });
            }
        }
        
        /// <summary>
        /// Broadcasts ready state to all clients, telling each client whether the update is about their local player.
        /// Includes NetworkPlayerId for proper player identification in dedicated server mode.
        /// </summary>
        private void BroadcastPlayerReadyStateTargeted(string sourceConnectionKey, string playerName, bool isReady, int instrument = 0, int difficulty = 0, bool sittingOut = false, Guid networkPlayerId = default)
        {
            if (_transport == null || !_isHosting) return;
            
            // Broadcast to all clients, but customize the isLocalPlayer flag for each
            foreach (var kvp in _connectionMap)
            {
                string clientConnKey = kvp.Key.ToString();
                var conn = kvp.Value;
                
                // Is this broadcast about this client's local player?
                bool isAboutThisClient = (clientConnKey == sourceConnectionKey);
                
                // Build message using handler - now includes NetworkPlayerId
                byte[] message = _readyStateHandler.BuildHostBroadcastMessage(playerName, isReady, isAboutThisClient, instrument, difficulty, sittingOut, networkPlayerId);
                
                conn.Send(message, ChannelType.ReliableOrdered);
                NetworkLogger.Info($"Host: Sent ready state to client {clientConnKey}: player='{playerName}', ready={isReady}, isLocal={isAboutThisClient}, instrument={instrument}, difficulty={difficulty}, sittingOut={sittingOut}, networkPlayerId={networkPlayerId}");
            }
        }
        
        private void HandleAllPlayersReadyMessage()
        {
            NetworkLogger.Info(" Client: All players are ready!");
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                OnAllPlayersReady?.Invoke();
            });
        }
        
        /// <summary>
        /// Handles the host changed message from the server (dedicated server mode).
        /// Updates all player IsHost flags accordingly.
        /// </summary>
        private void HandleHostChangedMessage(ReadOnlyMemory<byte> payload)
        {
            // Payload format: [PacketType (1 byte)][NewHostPlayerId (16 bytes)]
            if (payload.Length < 17)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Client: Invalid HostChanged packet length");
                return;
            }
            
            // Skip the packet type byte (offset 1) to read the GUID
            var newHostPlayerId = new Guid(payload.Span.Slice(1, 16));
            NetworkLogger.Info($"Client: Received host change notification - new host is {newHostPlayerId}");
            
            // Store pending host ID immediately (before enqueue) to handle race condition
            // where HostChanged arrives before local players are created
            _pendingHostPlayerId = newHostPlayerId;
            
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                ApplyPendingHostPlayerId();
                
                // Fire event for UI updates
                OnHostChanged?.Invoke(newHostPlayerId);
            });
        }
        
        /// <summary>
        /// Applies the pending host player ID to all players.
        /// Called after local players are created and when HostChanged is received.
        /// </summary>
        private void ApplyPendingHostPlayerId()
        {
            if (_pendingHostPlayerId == Guid.Empty) return;
            
            var allPlayers = GetAllPlayers();
            bool foundMatch = false;
            
            NetworkLogger.Info($"[ApplyPendingHostPlayerId] Searching for host with NetworkPlayerId={_pendingHostPlayerId}, total players={allPlayers.Count}");
            
            foreach (var player in allPlayers)
            {
                if (player != null)
                {
                    NetworkLogger.Info($"[ApplyPendingHostPlayerId] Checking player '{player.PlayerName}' with NetworkPlayerId={player.NetworkPlayerId}");
                    
                    bool wasHost = player.IsHost;
                    player.IsHost = player.NetworkPlayerId == _pendingHostPlayerId;
                    
                    if (player.IsHost)
                    {
                        foundMatch = true;
                        NetworkLogger.Info($"[ApplyPendingHostPlayerId] MATCH FOUND: Player '{player.PlayerName}' is now the host");
                    }
                    
                    if (wasHost != player.IsHost)
                    {
                        NetworkLogger.Info($"Client: Player {player.PlayerName} IsHost changed from {wasHost} to {player.IsHost}");
                    }
                }
            }
            
            if (!foundMatch && allPlayers.Count > 0)
            {
                NetworkLogger.Warn($"[ApplyPendingHostPlayerId] No player found with NetworkPlayerId={_pendingHostPlayerId} to set as host. Available players:");
                foreach (var player in allPlayers)
                {
                    if (player != null)
                    {
                        NetworkLogger.Warn($"  - '{player.PlayerName}' (NetworkPlayerId={player.NetworkPlayerId}, bucket={GetPlayerBucket(player)})");
                    }
                }
            }
        }
        
        /// <summary>
        /// Helper to find which bucket a player is in (for debugging).
        /// </summary>
        private string GetPlayerBucket(NetworkPlayerData targetPlayer)
        {
            foreach (var kvp in _connectedPlayers)
            {
                foreach (var player in kvp.Value)
                {
                    if (player == targetPlayer)
                    {
                        return kvp.Key.ToString();
                    }
                }
            }
            return "unknown";
        }
        
        private void HandleStartGameplayMessage()
        {
            NetworkLogger.Info(" Client: Starting gameplay!");
            
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
        
        /// <summary>
        /// Sends a gameplay snapshot to all other players (host broadcasts to clients, clients send to host who relays).
        /// </summary>
        /// <param name="playerName">The name of the player this snapshot belongs to.</param>
        public void SendGameplaySnapshot(
            string playerName,
            int score, int combo, int streak, bool starPowerActive, float starPowerAmount,
            int starPowerPhrasesHit, int totalStarPowerPhrases,
            int notesHit, int notesMissed,
            int overstrums, int hoposStrummed, int overhits, int ghostInputs,
            int ghostsHit, int accentsHit, int dynamicsBonus, int bandBonusScore,
            int vocalsTicksHit, int vocalsTicksMissed, float vocalsPhraseTicksHit, int vocalsPhraseTicksTotal,
            bool soloActive, int soloSequence, int soloNoteCount, int soloNotesHit, int soloLastBonus, int soloTotalBonus,
            int sustainsHeld, float whammyValue,
            float stars,
            double songTime,
            float happiness = 1.0f, bool hasFailed = false)
        {
            if (_transport == null || !IsNetworkActive)
            {
                NetworkLogger.Warn($"[LiteNetNetworkingAdapter] SendGameplaySnapshot skipped - transport={_transport != null}, IsNetworkActive={IsNetworkActive}");
                return;
            }
            
            if (_connectionMap.Count == 0)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] SendGameplaySnapshot - no connections in _connectionMap");
                return;
            }
            
            // Fallback to profile name if not provided
            if (string.IsNullOrEmpty(playerName))
            {
                playerName = GetPlayerNameFromProfile();
            }
            
            // Increment sequence via handler
            uint sequence = _gameplayStateHandler.NextSequence();
            
            // Log first snapshot and then periodically
            if (_gameplayStateHandler.ShouldLogSequence(sequence))
            {
                NetworkLogger.Info($"SendGameplaySnapshot #{sequence} for player '{playerName}' - connections={_connectionMap.Count}, score={score}, combo={combo}, happiness={happiness:F2}, failed={hasFailed}");
            }
            
            // Build the message using the handler
            byte[] message = _gameplayStateHandler.BuildSnapshotPacket(
                playerName,
                score, combo, streak,
                starPowerActive, starPowerAmount, starPowerPhrasesHit, totalStarPowerPhrases,
                notesHit, notesMissed,
                overstrums, hoposStrummed, overhits, ghostInputs,
                ghostsHit, accentsHit, dynamicsBonus,
                bandBonusScore,
                vocalsTicksHit, vocalsTicksMissed, vocalsPhraseTicksHit, vocalsPhraseTicksTotal,
                soloActive, soloSequence, soloNoteCount, soloNotesHit, soloLastBonus, soloTotalBonus,
                sustainsHeld, whammyValue,
                stars,
                songTime,
                happiness, hasFailed);
            
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
                    NetworkLogger.Warn($"[LiteNetNetworkingAdapter] Failed to send gameplay snapshot: {ex.Message}");
                }
            }
            
            // Log periodically
            if (_gameplayStateHandler.ShouldLogSequence(sequence))
            {
                NetworkLogger.Info($"Sent gameplay snapshot #{sequence} to {sentCount} connections (player={playerName}, score={score}, combo={combo})");
            }
        }
        
        private void HandleGameplaySnapshotMessage(INetConnection connection, ReadOnlyMemory<byte> payload)
        {
            if (payload.Length < GameplayStateBinaryPackets.MinPacketSize)
            {
                NetworkLogger.Warn($"[LiteNetNetworkingAdapter] Gameplay snapshot too short: {payload.Length} bytes");
                return;
            }
            
            // Parse using handler
            var snapshot = _gameplayStateHandler.ParseSnapshotPacket(payload.Span);
            if (!snapshot.HasValue)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Failed to parse gameplay snapshot");
                return;
            }
            
            var snapshotValue = snapshot.Value;
            string connectionKey = connection.Id.ToString();
            
            // Log first and then periodically to avoid spam
            bool shouldLog = _gameplayStateHandler.ShouldLogSequence(snapshotValue.Sequence);
            if (shouldLog || snapshotValue.HasFailed)
            {
                NetworkLogger.Info($"Received gameplay snapshot from '{snapshotValue.PlayerName}' (seq={snapshotValue.Sequence}, score={snapshotValue.Score}, combo={snapshotValue.Combo}, happiness={snapshotValue.Happiness:F2}, hasFailed={snapshotValue.HasFailed}, isHosting={_isHosting})");
            }
            
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                NetworkPlayerData targetPlayer = FindTargetPlayerForSnapshot(connectionKey, snapshotValue.PlayerName, shouldLog);
                
                if (targetPlayer == null)
                    return;
                
                if (shouldLog)
                {
                    NetworkLogger.Info($"Applying snapshot to player '{targetPlayer.PlayerName}' (IsLocalUser={targetPlayer.IsLocalUser})");
                }
                
                // Apply using handler
                _gameplayStateHandler.ApplySnapshotToPlayer(targetPlayer, in snapshotValue, Time.realtimeSinceStartupAsDouble);
                
                // If hosting, relay to all other clients
                if (_isHosting)
                {
                    RelayGameplaySnapshotToOthers(connection, payload);
                }
            });
        }
        
        private NetworkPlayerData FindTargetPlayerForSnapshot(string connectionKey, string playerName, bool shouldLog)
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
            // If we're a client, the snapshot could be from the host OR relayed from another client
            else if (!_isHosting)
            {
                // First, search all known players by name (host + local + any previously created remote players)
                foreach (var kvp in _connectedPlayers)
                {
                    foreach (var player in kvp.Value)
                    {
                        if (player != null && player.PlayerName == playerName)
                        {
                            targetPlayer = player;
                            break;
                        }
                    }
                    if (targetPlayer != null) break;
                }
                
                // If not found, this snapshot is from a player on another client that we haven't seen yet.
                // Create a NetworkPlayerData entry for them so we can track their stats.
                // This handles band mode where players from other bands (other clients) send snapshots.
                // NOTE: We only create remote entries for players that don't exist locally - but we
                // match by name+source connection context, not just name (two players can have same name).
                if (targetPlayer == null && !string.IsNullOrEmpty(playerName))
                {
                    if (shouldLog)
                    {
                        NetworkLogger.Verbose($"Creating remote player entry for '{playerName}' (from another client)");
                    }
                    
                    // Create a new NetworkPlayerData for this remote player
                    // Use "remote" key to track players from other clients
                    // Default to Expert (4) since Beginner (0) often doesn't exist in songs
                    targetPlayer = CreateLiteNetPlayerData(playerName, isHost: false, isLocal: false, initialInstrument: -1, initialDifficulty: 4, connectionId: Guid.Empty);
                    
                    // Try to find the NetworkPlayerId from our stored mapping (populated from band assignment)
                    foreach (var kvp in _remotePlayerIdToName)
                    {
                        if (kvp.Value == playerName)
                        {
                            targetPlayer.NetworkPlayerId = kvp.Key;
                            if (shouldLog)
                            {
                                NetworkLogger.Verbose($"Assigned NetworkPlayerId {kvp.Key} to remote player '{playerName}'");
                            }
                            break;
                        }
                    }
                    
                    // Add to _connectedPlayers under "remote" key
                    if (!_connectedPlayers.TryGetValue("remote", out var remotePlayers))
                    {
                        remotePlayers = new List<NetworkPlayerData>();
                        _connectedPlayers["remote"] = remotePlayers;
                    }
                    remotePlayers.Add(targetPlayer);
                    
                    // Apply any pending preset sync data to this newly added player
                    ApplyPendingPresets(new[] { targetPlayer });
                    
                    if (shouldLog)
                    {
                        NetworkLogger.Verbose($"Added remote player '{playerName}' to connected players (total remote: {remotePlayers.Count})");
                    }
                }
            }
            
            if (targetPlayer == null && shouldLog)
            {
                NetworkLogger.Warn($"[LiteNetNetworkingAdapter] Target player not found for snapshot from '{playerName}'. IsHosting={_isHosting}, connKey={connectionKey}");
            }
            
            return targetPlayer;
        }
        
        private void RelayGameplaySnapshotToOthers(INetConnection sourceConnection, ReadOnlyMemory<byte> payload)
        {
            if (!_isHosting || _transport == null)
                return;
            
            byte[] message = payload.ToArray();
            BroadcastPacketToClientsExcept(message, sourceConnection.Id, "gameplay snapshot relay", ChannelType.Unreliable);
        }
        
        #endregion
        
        private void CheckAllPlayersReady()
        {
            if (!_isHosting) return;
            
            NetworkLogger.Info("Host: Checking all players ready state...");
            
            // Use handler to check if all players are ready
            bool allReady = _readyStateHandler.CheckAllPlayersReady(_connectedPlayers);
            
            if (allReady)
            {
                // Broadcast to clients using handler-built message
                byte[] message = _readyStateHandler.BuildAllPlayersReadyMessage();
                foreach (var conn in _connectionMap.Values)
                {
                    conn.Send(message, ChannelType.ReliableOrdered);
                }
                
                // Note: The handler's CheckAllPlayersReady already fires OnAllPlayersReady event
            }
        }
        
        /// <summary>
        /// Start gameplay for all players (host only).
        /// </summary>
        public void StartGameplayForAll()
        {
            if (!_isHosting)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Only host can start gameplay for all");
                return;
            }
            
            NetworkLogger.Info(" Host: Starting gameplay for all players!");
            
            // Update session phase to playing song
            var currentSongHash = GlobalVariables.State.CurrentSong?.Hash.ToString() ?? string.Empty;
            SetSessionPhase(SessionPhase.PlayingSong, currentSongHash);
            
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
        /// Start gameplay for all players from a dedicated server (no local scene load).
        /// This is called by DedicatedServerManager when all players are ready.
        /// </summary>
        public void StartGameplayForAllDedicated()
        {
            if (!_isHosting)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Only host can start gameplay for all");
                return;
            }
            
            if (!_isDedicatedServer)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] StartGameplayForAllDedicated should only be called on dedicated servers. Use StartGameplayForAll() instead.");
                StartGameplayForAll();
                return;
            }
            
            NetworkLogger.Info(" Dedicated Server: Starting gameplay for all players!");
            
            // Update session phase to playing song
            var currentSongHash = GlobalVariables.State.CurrentSong?.Hash.ToString() ?? string.Empty;
            SetSessionPhase(SessionPhase.PlayingSong, currentSongHash);
            
            // Clear cached score results from previous song
            ClearCachedScoreResults();
            
            // Reset all players' ready states (they'll need to ready up again on score screen)
            ResetAllPlayerReadyStates();
            
            // Reset all players' gameplay state (score, combo, etc.) for new song
            ResetAllPlayersGameState();
            
            // Broadcast to all clients using GameplayBinaryPackets
            byte[] message = GameplayBinaryPackets.BuildStartPacket();
            foreach (var conn in _connectionMap.Values)
            {
                conn.Send(message, ChannelType.ReliableOrdered);
            }
            
            NetworkLogger.Info($" Dedicated Server: Sent StartGameplay to {_connectionMap.Count} client(s)");
            
            // Note: Dedicated server does NOT load the gameplay scene locally - it has no UI
            // The server continues running in headless mode while clients play
        }
        
        #region Auto-Start on All Ready
        
        /// <summary>
        /// Enable or disable automatic gameplay start when all players are ready.
        /// This provides a unified auto-start capability for both dedicated servers and in-game hosts,
        /// eliminating the need to duplicate this logic in UI components or server managers.
        /// </summary>
        /// <param name="enabled">True to enable auto-start, false to disable.</param>
        /// <param name="autoEnableOnDedicatedServer">If true, auto-enables when running as a dedicated server.</param>
        public void SetAutoStartOnAllReady(bool enabled, bool autoEnableOnDedicatedServer = true)
        {
            // Auto-enable for dedicated servers if not explicitly set
            if (autoEnableOnDedicatedServer && _isDedicatedServer)
            {
                enabled = true;
            }
            
            _autoStartOnAllReady = enabled;
            NetworkLogger.Info($"Auto-start on all ready: {enabled}");
        }
        
        /// <summary>
        /// Gets whether auto-start on all ready is enabled.
        /// </summary>
        public bool IsAutoStartOnAllReadyEnabled => _autoStartOnAllReady;
        
        /// <summary>
        /// Internal handler for auto-starting gameplay when all players are ready.
        /// Called from the OnAllPlayersReady event handler if auto-start is enabled.
        /// </summary>
        private void HandleAutoStartOnAllReady()
        {
            if (!_autoStartOnAllReady || !_isHosting)
            {
                return;
            }
            
            if (_autoStartPending)
            {
                NetworkLogger.Info("[LiteNetNetworkingAdapter] Auto-start already pending, ignoring duplicate call");
                return;
            }
            
            NetworkLogger.Info("[LiteNetNetworkingAdapter] All players ready - auto-starting gameplay in 1 second...");
            _autoStartPending = true;
            
            // Start the auto-start coroutine
            // We need a MonoBehaviour to run the coroutine - use GlobalVariables as it's always present
            if (GlobalVariables.Instance != null)
            {
                _autoStartCoroutine = GlobalVariables.Instance.StartCoroutine(AutoStartGameplayCoroutine());
            }
            else
            {
                // Fallback: start immediately without delay
                NetworkLogger.Info("[LiteNetNetworkingAdapter] No MonoBehaviour found, starting gameplay immediately");
                ExecuteAutoStart();
            }
        }
        
        private System.Collections.IEnumerator AutoStartGameplayCoroutine()
        {
            yield return new WaitForSeconds(1.0f);
            
            _autoStartCoroutine = null;
            
            // Double-check all players are still ready
            if (AreAllPlayersReady())
            {
                ExecuteAutoStart();
            }
            else
            {
                NetworkLogger.Info("[LiteNetNetworkingAdapter] Players no longer all ready, cancelling auto-start");
                _autoStartPending = false;
            }
        }
        
        private void ExecuteAutoStart()
        {
            _autoStartPending = false;
            
            if (_isDedicatedServer)
            {
                NetworkLogger.Info("[LiteNetNetworkingAdapter] Auto-starting gameplay for dedicated server");
                StartGameplayForAllDedicated();
            }
            else
            {
                NetworkLogger.Info("[LiteNetNetworkingAdapter] Auto-starting gameplay for in-game host");
                StartGameplayForAll();
            }
        }
        
        /// <summary>
        /// Cancels a pending auto-start if one is in progress.
        /// </summary>
        public void CancelAutoStart()
        {
            if (_autoStartCoroutine != null && GlobalVariables.Instance != null)
            {
                GlobalVariables.Instance.StopCoroutine(_autoStartCoroutine);
                _autoStartCoroutine = null;
            }
            _autoStartPending = false;
        }
        
        #endregion
        
        /// <summary>
        /// Signals that the local player has finished loading and is ready for gameplay.
        /// Called by GameManager after loading the song and creating players.
        /// </summary>
        public void SendGameplayLoadReady()
        {
            if (!IsNetworkActive)
            {
                return;
            }
            
            string playerName = GetPlayerNameFromProfile();
            NetworkLogger.Verbose($"Sending gameplay load ready for '{playerName}'");
            
            if (_isHosting)
            {
                // Host: Mark our own player(s) as ready directly
                foreach (var kvp in _connectedPlayers)
                {
                    foreach (var player in kvp.Value)
                    {
                        if (player != null && player.IsLocalUser)
                        {
                            player.SetGameplayReadyServer(true);
                            NetworkLogger.Verbose($"Host: Local player '{player.PlayerName}' marked as gameplay ready");
                        }
                    }
                }
                
                // Check if all players are ready (might just be host solo)
                CheckAndBroadcastAllLoadReady();
            }
            else
            {
                // Client: Send to host via first connection
                byte[] message = GameplayBinaryPackets.BuildGameplayLoadReadyPacket(playerName);
                foreach (var conn in _connectionMap.Values)
                {
                    conn.Send(message, ChannelType.ReliableOrdered);
                    NetworkLogger.Verbose($"Client: Sent gameplay load ready to host");
                    break; // Only need to send to host once
                }
            }
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
            NetworkLogger.Info($"AreAllPlayersReady called - checking {_connectedPlayers.Count} connection keys");
            int playerCount = 0;
            foreach (var kvp in _connectedPlayers)
            {
                foreach (var player in kvp.Value)
                {
                    playerCount++;
                    NetworkLogger.Info($"AreAllPlayersReady - Player '{player.PlayerName}' (key: {kvp.Key.ToString().Substring(0, Math.Min(8, kvp.Key.ToString().Length))}...) IsReady={player.IsReady}");
                    if (!player.IsReady)
                    {
                        NetworkLogger.Info(" AreAllPlayersReady returning false - found not-ready player");
                        return false;
                    }
                }
            }
            bool result = _connectedPlayers.Count > 0;
            NetworkLogger.Info($"AreAllPlayersReady returning {result} (playerCount={playerCount}, connectionKeys={_connectedPlayers.Count})");
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
        /// Uses the unified message-based pattern - works identically for in-game hosting and dedicated servers.
        /// </summary>
        public void AdvanceAfterScoreScreen()
        {
            if (!HasHostAuthority)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] AdvanceAfterScoreScreen called without host authority");
                return;
            }
            
            // Use the unified routing pattern
            RouteHostAction(HostActionType.AdvanceScoreScreen, null);
        }
        
        /// <summary>
        /// Internal implementation of score screen advance. Called by server process.
        /// </summary>
        private void AdvanceAfterScoreScreenInternal()
        {
            NetworkLogger.Info(" AdvanceAfterScoreScreenInternal - all players ready, advancing...");

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
                    NetworkLogger.Info($"Removed completed song {completedEntry.SongHash} from setlist (remaining: {_setlistManager.Count})");
                    OnSetlistUpdated?.Invoke();
                }
                
                // Remove the completed song from GlobalVariables.State.ShowSongs as well
                // This ensures the song queue UI only shows remaining songs
                if (GlobalVariables.State.ShowSongs != null && GlobalVariables.State.ShowSongs.Count > 0)
                {
                    GlobalVariables.State.ShowSongs.RemoveAt(0);
                    // ShowIndex stays at 0 since we removed the first song
                    GlobalVariables.State.ShowIndex = 0;
                    NetworkLogger.Info($"Removed completed song from ShowSongs (remaining: {GlobalVariables.State.ShowSongs.Count})");
                }
                
                // Set the current song for the host (now the first song in the updated list)
                string nextSongHash = string.Empty;
                if (GlobalVariables.State.ShowSongs.Count > 0)
                {
                    var nextSong = GlobalVariables.State.ShowSongs[0];
                    GlobalVariables.State.CurrentSong = nextSong;
                    nextSongHash = nextSong.Hash.ToString();
                }
                
                // Update session phase to difficulty select for next song
                SetSessionPhase(SessionPhase.DifficultySelect, nextSongHash);
                
                // Set the menu navigation target BEFORE loading the scene
                // This prevents the music library from flashing
                NetworkLogger.Info(" Advancing to next song in show - going to difficulty select");
                MenuNavigationHelper.SetMenuNavigationAfterSceneLoad(MenuManager.Menu.DifficultySelect);
                
                // Go to Menu scene - MenuManager will automatically navigate to DifficultySelect
                GlobalVariables.Instance.LoadScene(SceneIndex.Menu);
            }
            else
            {
                // End the show and return to music library so players can queue more songs
                NetworkLogger.Info(" Show complete or not playing show, returning to music library");
                GlobalVariables.State.PlayingAShow = false;
                GlobalVariables.State.ShowSongs?.Clear();
                GlobalVariables.State.ShowIndex = 0;
                
                // Update session phase to music library (not a late join scenario anymore)
                SetSessionPhase(SessionPhase.MusicLibrary);
                
                // Clear the setlist so songs are removed from the queue
                _setlistManager.Clear();
                OnSetlistUpdated?.Invoke();
                NetworkLogger.Info(" Host: Cleared setlist after show complete");
                
                // Update browsing state so clients know we're in song selection mode
                SetBrowsingState(true);
                
                // Set the full menu navigation stack: OnlineMultiplayer > LobbyRoom > MusicLibrary
                // This ensures that backing out of MusicLibrary goes to LobbyRoom, not MainMenu
                NetworkLogger.Info(" Host: Navigating to music library with proper menu stack");
                MenuNavigationHelper.SetMenuNavigationAfterSceneLoad(
                    MenuManager.Menu.OnlineMultiplayer,
                    MenuManager.Menu.LobbyRoom,
                    MenuManager.Menu.MusicLibrary);
                
                // Transition back to menu - MenuManager will automatically navigate through the stack
                GlobalVariables.Instance.LoadScene(SceneIndex.Menu);
            }
        }
        
        /// <inheritdoc/>
        public void RequestSyncMenuNavigation(bool popMenu = false)
        {
            if (!_isHosting)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] RequestSyncMenuNavigation called but not hosting");
                return;
            }
            
            if (popMenu)
            {
                BroadcastPopMenu();
            }
            else
            {
                // Could sync to the current menu state if needed
                NetworkLogger.Info("RequestSyncMenuNavigation called without popMenu - no action needed");
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
            
            NetworkLogger.Info($"Broadcast score screen advance to {_connectionMap.Count} clients, hasMoreSongs={hasMoreSongs}");
        }

        /// <summary>
        /// Handles the score screen advance message on clients.
        /// </summary>
        private void HandleScoreScreenAdvanceMessage(ReadOnlyMemory<byte> payload)
        {
            if (!ScoreBinaryPackets.TryParseAdvancePacket(payload.Span, out int advanceIndex))
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Invalid score screen advance message");
                return;
            }

            bool hasMoreSongs = advanceIndex != 0;

            NetworkLogger.Client($"Received score screen advance, hasMoreSongs={hasMoreSongs}");

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
                        NetworkLogger.Client($"Removed completed song {completedEntry.SongHash} from setlist (remaining: {_setlistManager.Count})");
                        OnSetlistUpdated?.Invoke();
                    }
                    
                    // Remove the completed song from GlobalVariables.State.ShowSongs as well
                    // This ensures the song queue UI only shows remaining songs
                    if (GlobalVariables.State.ShowSongs != null && GlobalVariables.State.ShowSongs.Count > 0)
                    {
                        GlobalVariables.State.ShowSongs.RemoveAt(0);
                        // ShowIndex stays at 0 since we removed the first song
                        GlobalVariables.State.ShowIndex = 0;
                        NetworkLogger.Client($"Removed completed song from ShowSongs (remaining: {GlobalVariables.State.ShowSongs.Count})");
                    }
                    
                    // Set the current song for the client (now the first song in the updated list)
                    if (GlobalVariables.State.ShowSongs != null && GlobalVariables.State.ShowSongs.Count > 0)
                    {
                        var nextSong = GlobalVariables.State.ShowSongs[0];
                        GlobalVariables.State.CurrentSong = nextSong;
                    }
                    
                    // Set the menu navigation target BEFORE loading the scene
                    // This prevents the music library from flashing
                    NetworkLogger.Info(" Client: Advancing to next song in show - going to difficulty select");
                    MenuNavigationHelper.SetMenuNavigationAfterSceneLoad(MenuManager.Menu.DifficultySelect);
                    
                    // Go to Menu scene - MenuManager will automatically navigate to DifficultySelect
                    GlobalVariables.Instance.LoadScene(SceneIndex.Menu);
                }
                else
                {
                    // Return to music library so players can queue more songs
                    NetworkLogger.Info(" Client: Show complete, returning to music library");
                    GlobalVariables.State.PlayingAShow = false;
                    GlobalVariables.State.ShowSongs?.Clear();
                    GlobalVariables.State.ShowIndex = 0;
                    
                    // Clear the setlist so songs are removed from the queue
                    _setlistManager.Clear();
                    OnSetlistUpdated?.Invoke();
                    NetworkLogger.Info(" Client: Cleared setlist after show complete");
                    
                    // Update browsing state to match host
                    _isBrowsingSongs = true;
                    
                    // Set the full menu navigation stack: OnlineMultiplayer > LobbyRoom > MusicLibrary
                    // This ensures that backing out of MusicLibrary goes to LobbyRoom, not MainMenu
                    NetworkLogger.Info(" Client: Navigating to music library with proper menu stack");
                    MenuNavigationHelper.SetMenuNavigationAfterSceneLoad(
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
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Cannot send score results - not connected");
                return;
            }
            
            // Update session phase to score screen when we enter it
            if (_isHosting)
            {
                SetSessionPhase(SessionPhase.ScoreScreen);
            }

            NetworkLogger.Info($"Sending score results: player={playerName}, highScore={isHighScore}, FC={isFullCombo}, score={score}");

            // Build message using handler
            byte[] message = _scoreResultsHandler.BuildResultsMessage(playerName, isHighScore, isFullCombo, score, maxCombo, notesHit, notesMissed);

            // Send to all connections (reliable since this is important)
            foreach (var conn in _connectionMap.Values)
            {
                try
                {
                    conn.Send(message, ChannelType.ReliableOrdered);
                }
                catch (Exception ex)
                {
                    NetworkLogger.Warn($"[LiteNetNetworkingAdapter] Failed to send score results: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Gets all cached score results from remote players.
        /// Use this to retrieve results that may have arrived before subscribing to events.
        /// </summary>
        public Dictionary<string, PlayerScoreResult> GetCachedScoreResults()
        {
            return _scoreResultsHandler.GetCachedResults();
        }

        /// <summary>
        /// Clears cached score results. Call this when starting new gameplay.
        /// </summary>
        public void ClearCachedScoreResults()
        {
            _scoreResultsHandler.Clear();
        }

        /// <summary>
        /// Handles incoming score results from another player.
        /// </summary>
        private void HandleScoreResultsMessage(INetConnection connection, ReadOnlyMemory<byte> payload)
        {
            // Parse using handler
            var data = _scoreResultsHandler.ParseResultsMessage(payload.Span);
            if (!data.IsValid)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Invalid score results message");
                return;
            }

            NetworkLogger.Info($"Received score results: player={data.PlayerName}, highScore={data.IsHighScore}, FC={data.IsFullCombo}, score={data.Score}, maxCombo={data.MaxCombo}");

            // Cache the result for late subscribers using handler
            _scoreResultsHandler.RecordResult(data.PlayerName, data.IsHighScore, data.IsFullCombo, data.Score, data.MaxCombo, data.NotesHit, data.NotesMissed);

            // If we're the host, relay to other clients (excluding the sender)
            if (_isHosting)
            {
                RelayScoreResults(connection, data.PlayerName, data.IsHighScore, data.IsFullCombo, data.Score, data.MaxCombo, data.NotesHit, data.NotesMissed);
            }

            // Fire event for the score screen to handle (in addition to manager event)
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                OnScoreResultsReceived?.Invoke(data.PlayerName, data.IsHighScore, data.IsFullCombo, data.Score, data.MaxCombo, data.NotesHit, data.NotesMissed);
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
        /// Sets the expected number of players for unison tracking (legacy global).
        /// Should be called when gameplay starts.
        /// </summary>
        public void SetUnisonPlayerCount(int count)
        {
            _unisonSyncHandler.SetPlayerCount(count);
        }
        
        /// <summary>
        /// Sets the expected player count for a specific band.
        /// Should be called when gameplay starts.
        /// </summary>
        /// <param name="bandId">The band ID.</param>
        /// <param name="playerCount">Number of players in this band.</param>
        public void SetBandUnisonPlayerCount(int bandId, int playerCount)
        {
            _unisonSyncHandler.SetBandPlayerCount(bandId, playerCount);
        }
        
        /// <summary>
        /// Clears all per-band player counts.
        /// </summary>
        public void ClearBandUnisonPlayerCounts()
        {
            _unisonSyncHandler.ClearBandPlayerCounts();
        }
        
        /// <summary>
        /// Resets unison tracking state. Should be called when gameplay ends or restarts.
        /// </summary>
        public void ResetUnisonTracking()
        {
            _unisonSyncHandler.Reset();
        }
        
        /// <summary>
        /// Fully resets unison tracking including band counts. Should be called when leaving lobby.
        /// </summary>
        public void FullResetUnisonTracking()
        {
            _unisonSyncHandler.FullReset();
        }
        
        /// <summary>
        /// Called when the local player hits a star power phrase that is part of a unison.
        /// Sends the completion to the host (or processes locally if hosting).
        /// </summary>
        /// <param name="bandId">The band the player belongs to.</param>
        /// <param name="phraseTime">The start time of the unison phrase.</param>
        /// <param name="phraseEndTime">The end time of the unison phrase.</param>
        public void SendUnisonPhraseHit(int bandId, double phraseTime, double phraseEndTime)
        {
            if (!IsNetworkActive)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Cannot send unison phrase hit - network not active");
                return;
            }
            
            string playerName = GetPlayerNameFromProfile();
            NetworkLogger.Info($"Sending unison phrase hit: player={playerName}, band={bandId}, time={phraseTime:F3}");
            
            if (_isHosting)
            {
                // Host processes locally with "host" as unique player key using handler
                bool awardedBonus = _unisonSyncHandler.RecordPhraseHit("host", bandId, phraseTime, phraseEndTime);
                if (awardedBonus)
                {
                    NetworkLogger.Info($"Host: All players in band {bandId} completed unison phrase at {phraseTime:F3}");
                    // Event fired via handler's OnBonusAwarded
                }
            }
            else
            {
                // Client sends to host
                SendUnisonPhraseHitToHost(playerName, bandId, phraseTime, phraseEndTime);
            }
        }
        
        /// <summary>
        /// Called when the local player hits a star power phrase (legacy, uses bandId=0).
        /// </summary>
        public void SendUnisonPhraseHit(double phraseTime, double phraseEndTime)
        {
            SendUnisonPhraseHit(0, phraseTime, phraseEndTime);
        }
        
        private void SendUnisonPhraseHitToHost(string playerName, int bandId, double phraseTime, double phraseEndTime)
        {
            if (_transport == null) return;
            
            // Build message using handler
            byte[] message = _unisonSyncHandler.BuildPhraseHitMessage(playerName, bandId, phraseTime, phraseEndTime);
            
            // Send to host (first connection when we're a client)
            foreach (var conn in _connectionMap.Values)
            {
                try
                {
                    conn.Send(message, ChannelType.ReliableOrdered);
                    NetworkLogger.Info($" Sent unison phrase hit to host (band {bandId})");
                    break;
                }
                catch (Exception ex)
                {
                    NetworkLogger.Error($"[LiteNetNetworkingAdapter] Failed to send unison phrase hit: {ex.Message}");
                }
            }
        }
        
        private void HandleUnisonPhraseHitMessage(INetConnection connection, ReadOnlyMemory<byte> payload)
        {
            if (!_isHosting) return;
            
            // Parse using handler
            var data = _unisonSyncHandler.ParsePhraseHitMessage(payload.Span);
            if (!data.IsValid)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Invalid unison phrase hit message");
                return;
            }
            
            string playerKey = connection.Id.ToString();
            NetworkLogger.Info($"Host received unison phrase hit: player={data.PlayerName}, time={data.PhraseStartTime:F3}");
            
            // Use handler's coordinator - it will fire event if bonus awarded
            _unisonSyncHandler.RecordPhraseHit(playerKey, data.BandId, data.PhraseStartTime, data.PhraseEndTime);
        }
        
        private void BroadcastUnisonBonusAward(int bandId, double phraseTime)
        {
            if (!_isHosting || _transport == null) return;
            
            // Build message using handler
            byte[] message = _unisonSyncHandler.BuildBonusAwardMessage(bandId, phraseTime);
            NetworkLogger.Info($"Broadcasting unison bonus award for band {bandId}, phrase at {phraseTime:F3}");
            BroadcastPacketToClients(message, "unison bonus award");
        }
        
        private void HandleUnisonBonusAwardMessage(ReadOnlyMemory<byte> payload)
        {
            if (_isHosting) return;
            
            // Parse using handler
            var (isValid, bandId, phraseTime) = _unisonSyncHandler.ParseBonusAwardMessage(payload.Span);
            if (!isValid)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Invalid unison bonus award message");
                return;
            }
            
            NetworkLogger.Info($"Client received unison bonus award for band {bandId}, phrase at {phraseTime:F3}");
            
            UnityMainThreadDispatcher.EnqueueAction(() => OnUnisonBonusAwarded?.Invoke(bandId, phraseTime));
        }
        
        #endregion
        
        #endregion
        
        private void OnTransportPeerDisconnected(INetConnection connection)
        {
            // Remove from connection map
            _connectionMap.Remove(connection.Id);
            NetworkLogger.Info($"[OnTransportPeerDisconnected] Removed connection {connection.Id} from _connectionMap (now has {_connectionMap.Count} entries). IsHosting={_isHosting}");
            
            // Remove authentication state via manager
            try
            {
                _lobbyAuthenticator.RemoveConnection(connection.Id);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[OnTransportPeerDisconnected] Exception in RemoveConnection: {ex}");
            }
            
            // Only handle this when we're hosting (server mode)
            if (!_isHosting)
            {
                return;
            }
            
            // Use Debug.Log directly as backup to ensure we see this
            Debug.Log($"[LiteNet] [OnTransportPeerDisconnected] HOST MODE: Processing disconnect for {connection.Id}");
            
            try
            {
                NetworkLogger.Server($"Client disconnected - {connection.EndPoint}");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[OnTransportPeerDisconnected] Exception logging endpoint: {ex.Message}");
            }
            
            // Remove song library for this player and recalculate shared songs
            try
            {
                RemoveSongLibraryForPlayer(connection.Id);
                RecalculateSharedSongs();
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[OnTransportPeerDisconnected] Exception in song library cleanup: {ex.Message}");
            }
            
            string clientId = connection.Id.ToString();
            Guid connectionGuid = connection.Id;
            
            Debug.Log($"[LiteNet] [OnTransportPeerDisconnected] Looking up clientId={clientId}, _connectedPlayers.Count={_connectedPlayers.Count}");
            
            // Get the players for this connection BEFORE removal (for event firing later)
            List<NetworkPlayerData> disconnectedPlayers = null;
            List<NetworkPlayerIdentity> pendingIdentities = null;
            int playerCount = 0;
            
            NetworkLogger.Info($"[OnTransportPeerDisconnected] Looking up clientId: {clientId}");
            NetworkLogger.Info($"[OnTransportPeerDisconnected] _connectedPlayers has {_connectedPlayers.Count} entries: [{string.Join(", ", _connectedPlayers.Keys)}]");
            
            // First check if there are pending player identities that haven't been processed yet
            // This handles the race condition where disconnect happens before the main thread processes the join
            lock (_pendingPlayerIdentitiesLock)
            {
                if (_pendingPlayerIdentities.TryGetValue(connectionGuid, out var identities))
                {
                    pendingIdentities = new List<NetworkPlayerIdentity>(identities);
                    _pendingPlayerIdentities.Remove(connectionGuid);
                    var pendingNames = string.Join(", ", identities.Select(i => i.DisplayName));
                    NetworkLogger.Info($"[OnTransportPeerDisconnected] Found {identities.Count} PENDING player identities [{pendingNames}] - removing");
                }
            }
            
            // Remove from connected players immediately (thread-safe via lock-free check+remove pattern)
            // Note: This is safe because the network thread is the only one that removes,
            // and the main thread only adds (via dispatcher) and reads
            if (_connectedPlayers.TryGetValue(clientId, out var players))
            {
                disconnectedPlayers = new List<NetworkPlayerData>(players); // Copy for event firing
                playerCount = players.Count;
                var playerNames = string.Join(", ", players.Select(p => p.PlayerName));
                Debug.Log($"[LiteNet] [OnTransportPeerDisconnected] Found {players.Count} player(s) [{playerNames}] - removing");
                
                _connectedPlayers.Remove(clientId);
                _connectedPlayersByConnection.Remove(connectionGuid);
                
                Debug.Log($"[LiteNet] [OnTransportPeerDisconnected] Removal complete. _connectedPlayers now has {_connectedPlayers.Count} entries");
            }
            else if (pendingIdentities != null && pendingIdentities.Count > 0)
            {
                // Players were pending - never made it to _connectedPlayers
                // We still need to count them for lobby player count
                playerCount = pendingIdentities.Count;
                Debug.Log($"[LiteNet] [OnTransportPeerDisconnected] No connected players found, but had {playerCount} pending identities");
            }
            else
            {
                Debug.LogWarning($"[LiteNet] [OnTransportPeerDisconnected] No players found for clientId: {clientId}");
            }
            
            // Update player count
            if (_currentLobby != null && playerCount > 0)
            {
                _currentLobby.CurrentPlayers = System.Math.Max(1, _currentLobby.CurrentPlayers - playerCount);
                NetworkLogger.Server($"Player count is now {_currentLobby.CurrentPlayers}");
            }
            
            // Dispatch event firing to main thread (events need to fire on main thread for UI safety)
            // Handle both: players that were fully added, and players that were still pending
            if ((disconnectedPlayers != null && disconnectedPlayers.Count > 0) || (pendingIdentities != null && pendingIdentities.Count > 0))
            {
                Debug.Log($"[LiteNet] [OnTransportPeerDisconnected] Enqueueing main thread callback for {disconnectedPlayers?.Count ?? 0} connected + {pendingIdentities?.Count ?? 0} pending players");
                
                var playersToNotify = disconnectedPlayers; // Capture for closure
                var pendingToNotify = pendingIdentities; // Capture for closure
                UnityMainThreadDispatcher.EnqueueAction(() =>
                {
                    Debug.Log($"[LiteNet] [OnTransportPeerDisconnected] Main thread callback executing");
                    
                    // Update PlayerNames in current lobby for discovery responses
                    UpdateLobbyPlayerNames();
                    
                    // Notify for players that were fully connected
                    if (playersToNotify != null && playersToNotify.Count > 0)
                    {
                        Debug.Log($"[LiteNet] [OnTransportPeerDisconnected] Firing OnPlayerLeft for {playersToNotify.Count} player(s)");
                        
                        foreach (var player in playersToNotify)
                        {
                            string playerName = player.PlayerName;
                            Debug.Log($"[LiteNet] Firing OnPlayerLeft for '{playerName}'");
                            
                            // Fire the standard player left event
                            OnPlayerLeft?.Invoke(player);
                            
                            // If we're in gameplay (show is playing), broadcast to other clients
                            // so they can remove the player's track
                            if (GlobalVariables.State.PlayingAShow)
                            {
                                Debug.Log($"[LiteNet] Player '{playerName}' disconnected during gameplay, broadcasting to other clients");
                                BroadcastPlayerLeftGameplay(playerName);
                            }
                        }
                    }
                    
                    // Notify for players that were still pending (never fully joined)
                    // Create minimal player data just for the notification
                    if (pendingToNotify != null && pendingToNotify.Count > 0)
                    {
                        Debug.Log($"[LiteNet] [OnTransportPeerDisconnected] Firing OnPlayerLeft for {pendingToNotify.Count} PENDING player(s)");
                        
                        foreach (var identity in pendingToNotify)
                        {
                            string playerName = identity.DisplayName;
                            Debug.Log($"[LiteNet] Firing OnPlayerLeft for pending player '{playerName}'");
                            
                            // Create minimal player data for the event
                            var playerData = CreateLiteNetPlayerData(playerName, isHost: false, isLocal: false, connectionId: connectionGuid);
                            playerData.NetworkPlayerId = identity.PlayerId;
                            
                            // Fire the standard player left event
                            OnPlayerLeft?.Invoke(playerData);
                        }
                    }
                });
            }
        }
        
        private void OnTransportLatencyUpdate(INetConnection connection, int latencyMs)
        {
            NetworkLogger.Info($"Latency update: connection={connection.Id}, latency={latencyMs}ms, isHosting={_isHosting}");
            
            // Update ping for the player associated with this connection
            string clientId = connection.Id.ToString();
            
            // Dispatch to main thread since we're modifying Unity objects
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                // Check if this is a client connection (we're hosting)
                if (_isHosting && _connectedPlayers.TryGetValue(clientId, out var players))
                {
                    NetworkLogger.Info($"Host: Updating ping for {players.Count} player(s) from connection {clientId}");
                    foreach (var player in players)
                    {
                        UpdatePlayerPing(player, latencyMs);
                    }
                }
                // If we're a client, the latency update is for our connection to the server
                // We need to update the HOST's ping as displayed on our client
                else if (!_isHosting)
                {
                    NetworkLogger.Client($"Updating host ping to {latencyMs}ms");
                    // Find the host player (not local) and update their ping
                    foreach (var kvp in _connectedPlayers)
                    {
                        foreach (var player in kvp.Value)
                        {
                            if (player != null && player.IsHost)
                            {
                                NetworkLogger.Client($"Found host player '{player.PlayerName}', updating ping");
                                UpdatePlayerPing(player, latencyMs);
                            }
                        }
                    }
                }
                else
                {
                    NetworkLogger.Info($"Host: No players found for connection {clientId}. Keys: {string.Join(", ", _connectedPlayers.Keys)}");
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
                NetworkLogger.Warn($"[LiteNetNetworkingAdapter] Failed to update ping: {ex.Message}");
            }
        }
        
        private void HandleUnconnectedMessage(System.Net.IPEndPoint remoteEndPoint, byte[] data)
        {
            NetworkLogger.Info($"Received unconnected message from {remoteEndPoint}, bytes: {data.Length}, isHosting: {_isHosting}, hasNetManager: {_liteNetTransport?.NetManager != null}, isAdvertising: {_discovery?.IsAdvertising ?? false}, advertisedLobby: {_discovery?.AdvertisedLobbyName ?? "null"}");
            
            // Only handle when we're hosting a server
            if (!_isHosting || _liteNetTransport?.NetManager == null)
            {
                NetworkLogger.Info($"Not handling: isHosting={_isHosting}, NetManager={(_liteNetTransport?.NetManager != null)}");
                return;
            }
            
            // Pass raw bytes to discovery handler
            _discovery?.HandleUnconnectedMessage(remoteEndPoint, data, _liteNetTransport.NetManager);
        }
        
        private void HandleLobbyDiscovered(LobbyInfo lobby)
        {
            NetworkLogger.Info($"Discovered lobby: {lobby.LobbyName} at {lobby.IpAddress}:{lobby.Port}");
            OnLobbyListUpdated?.Invoke(new List<LobbyInfo>(_discovery.DiscoveredLobbies.Values));
        }
        
        private void HandleLobbyLost(string lobbyId)
        {
            NetworkLogger.Info($"Lost lobby: {lobbyId}");
            OnLobbyListUpdated?.Invoke(new List<LobbyInfo>(_discovery.DiscoveredLobbies.Values));
        }

        #endregion

        #region Lobby Management

        public LobbyInfo CreateLobby(string lobbyName, int maxPlayers, LobbyPrivacyMode privacyMode, SessionType sessionType = SessionType.Lobby, string password = "")
        {
            NetworkLogger.Info($"Creating lobby: {lobbyName} (max: {maxPlayers}, privacy: {privacyMode}, sessionType: {sessionType}, dedicated: {_isDedicatedServer})");

            // For non-dedicated servers, require at least one connected profile
            if (!_isDedicatedServer && !HasConnectedProfiles())
            {
                string errorMessage = "Cannot create lobby: No profiles are connected. Please connect a controller and select a profile first.";
                NetworkLogger.Error($"[LiteNetNetworkingAdapter] {errorMessage}");
                YARG.Menu.Persistent.ToastManager.ToastWarning(errorMessage);
                OnNetworkError?.Invoke(errorMessage);
                return null;
            }

            try
            {
                // Use ServerNetworkingBootstrapper to create and configure the server stack
                // Choose dedicated or hosted server based on mode
                if (_isDedicatedServer)
                {
                    _serverStack = ServerNetworkingBootstrapper.InitializeDedicatedServer(
                        _transport,
                        _serializer,
                        _defaultPort,
                        maxPlayers);
                }
                else
                {
                    _serverStack = ServerNetworkingBootstrapper.InitializeHostedServer(
                        _transport,
                        _serializer,
                        _defaultPort,
                        maxPlayers);
                }
                
                // Update legacy references for gradual migration
                _serverRuntime = _serverStack.Runtime;
                _packetDispatcher = _serverStack.PacketDispatcher;
                
                // Create lobby configuration
                var config = new LobbyConfiguration
                {
                    MaxPlayers = maxPlayers
                };

                // Create lobby state manager
                _lobbyStateManager = new LobbyStateManager(_sessionManager, config);

                // Start the server
                _ = _serverStack.Runtime.StartAsync();

                // Get actual player name from profile BEFORE creating lobby info
                // For dedicated servers, use a generic name
                string actualPlayerName = _isDedicatedServer 
                    ? (_playerName ?? "Dedicated Server") 
                    : GetPlayerNameFromProfile();
                _playerName = actualPlayerName; // Update the field so it's consistent

                // Get local LAN address for sidebar display
                string lanAddress = NetworkAddressUtility.GetLocalLanAddress() ?? "Unknown";
                NetworkLogger.Info($"Detected LAN address: {lanAddress}");

                // Create lobby info
                _currentLobby = new LobbyInfo
                {
                    LobbyId = _lobbyStateManager.LobbyId.ToString(),
                    LobbyName = lobbyName,
                    HostName = actualPlayerName,
                    CurrentPlayers = _isDedicatedServer ? 0 : 1, // Dedicated server doesn't count as a player
                    MaxPlayers = maxPlayers,
                    PrivacyMode = privacyMode,
                    SessionType = (int)sessionType,
                    HasPassword = !string.IsNullOrEmpty(password),
                    Password = password,
                    IsActive = true,
                    IpAddress = lanAddress,
                    Port = _defaultPort,
                    PublicPort = _defaultPort,
                    PublicAddress = string.Empty, // Will be populated async via STUN resolution
                    TransportId = "LiteNetLib",
                    PlayerNames = _isDedicatedServer ? Array.Empty<string>() : new[] { actualPlayerName },
                    PlayerInstruments = new int[0],
                    IsDedicatedServer = _isDedicatedServer
                };

                _isHosting = true;
                _maxPlayers = maxPlayers;
                
                // Clear connected players and track order from any previous session
                _connectedPlayers.Clear();
                _connectedPlayersByConnection.Clear();
                var trackOrderManager = Tracks.TrackOrderManager.Instance;
                if (trackOrderManager != null)
                {
                    trackOrderManager.Clear();
                    NetworkLogger.Info("Cleared TrackOrderManager for new hosted lobby");
                }
                
                // For hosted servers (not dedicated), add the host players
                if (!_isDedicatedServer)
                {
                    // Create NetworkPlayerData for ALL local host players
                    var hostIdentities = GetAllLocalPlayerIdentities();
                    var hostPlayerList = new List<NetworkPlayerData>();
                    var allHostNames = new List<string>();
                    
                    foreach (var identity in hostIdentities)
                    {
                        // Find the matching local profile for this identity to get instrument/difficulty
                        // Note: identity.DisplayName is the sanitized profile name, so we compare by name
                        // (identity.PlayerId is a combined GUID, not the raw profile.Id)
                        var localPlayers = PlayerContainer.Players;
                        int instrument = 0;
                        int difficulty = 0;
                        bool foundProfile = false;
                        
                        if (localPlayers != null)
                        {
                            foreach (var player in localPlayers)
                            {
                                string sanitizedProfileName = SanitizeDisplayName(player.Profile?.Name ?? "");
                                if (sanitizedProfileName == identity.DisplayName)
                                {
                                    instrument = (int)player.Profile.CurrentInstrument;
                                    difficulty = (int)player.Profile.CurrentDifficulty;
                                    foundProfile = true;
                                    NetworkLogger.Info($"Matched profile '{sanitizedProfileName}' -> instrument={(Instrument)instrument}, difficulty={(Difficulty)difficulty}");
                                    break;
                                }
                            }
                        }
                        
                        if (!foundProfile)
                        {
                            NetworkLogger.Warn($"Could not find matching profile for identity '{identity.DisplayName}' - using default instrument");
                        }
                        
                        // Host players use the well-known HostConnectionId
                        var hostPlayerData = CreateLiteNetPlayerData(identity.DisplayName, isHost: true, isLocal: true, instrument, difficulty, connectionId: HostConnectionId);
                        hostPlayerData.NetworkPlayerId = identity.PlayerId;
                        hostPlayerList.Add(hostPlayerData);
                        allHostNames.Add(identity.DisplayName);
                        
                        NetworkLogger.Info($"Added host player '{identity.DisplayName}' with NetworkPlayerId={identity.PlayerId}, ConnectionId={HostConnectionId}");
                    }
                    
                    // Update lobby to include all host player names and correct player count
                    _currentLobby.PlayerNames = allHostNames.ToArray();
                    _currentLobby.CurrentPlayers = hostPlayerList.Count;
                    NetworkLogger.Info($"Host has {hostPlayerList.Count} local profile(s), CurrentPlayers set to {_currentLobby.CurrentPlayers}");

                    // Add host players to connected players dictionary (both legacy and new)
                    _connectedPlayers["host"] = hostPlayerList;
                    _connectedPlayersByConnection[HostConnectionId] = hostPlayerList;
                    NetworkLogger.Info($"Added {hostPlayerList.Count} host player(s) to connected players");
                }
                else
                {
                    NetworkLogger.Info(" Dedicated server mode - no host player added");
                }
                
                // Start advertising for discovery (private lobbies are still discoverable, they just require a password to join)
                _discovery?.StartAdvertising(_currentLobby);
                NetworkLogger.Info($"Started advertising lobby for discovery (privacy: {privacyMode})");

                OnLobbyCreated?.Invoke(_currentLobby);
                
                // Host also joins their own lobby (triggers UI navigation) - not for dedicated servers
                if (!_isDedicatedServer)
                {
                    OnLobbyJoined?.Invoke(_currentLobby);
                }
                
                // Set lobby password for authentication
                SetLobbyPassword(password);
                
                // Register host's song library for shared songs (only for hosted servers)
                if (!_isDedicatedServer)
                {
                    UploadSongLibraryAsHost();
                }
                
                // Start STUN resolution to get public IP address
                StartPublicEndpointResolution();

                NetworkLogger.Info($"Lobby created successfully: {_currentLobby.LobbyId} (dedicated: {_isDedicatedServer})");
                return _currentLobby;
            }
            catch (Exception ex)
            {
                NetworkLogger.Error($"[LiteNetNetworkingAdapter] Failed to create lobby: {ex.Message}");
                OnNetworkError?.Invoke($"Failed to create lobby: {ex.Message}");
                return null;
            }
        }

        public void JoinLobby(string endpoint, string password = "")
        {
            NetworkLogger.Info($"Joining lobby at: {endpoint}");

            // Require at least one connected profile to join
            if (!HasConnectedProfiles())
            {
                string errorMessage = "Cannot join lobby: No profiles are connected. Please connect a controller and select a profile first.";
                NetworkLogger.Error($"[LiteNetNetworkingAdapter] {errorMessage}");
                YARG.Menu.Persistent.ToastManager.ToastWarning(errorMessage);
                OnNetworkError?.Invoke(errorMessage);
                return;
            }

            try
            {
                // Store password for authentication
                SetJoinPassword(password);
                
                // Check if already connected or connecting
                if (_isConnected)
                {
                    NetworkLogger.Warn("[LiteNetNetworkingAdapter] Already connected to a lobby, ignoring join request");
                    return;
                }

                if (_isJoinInProgress)
                {
                    NetworkLogger.Warn("[LiteNetNetworkingAdapter] Join already in progress, ignoring duplicate request");
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
                
                // Check if address is localhost (127.0.0.1 or localhost) - these should also match against local LAN IP
                bool isLocalhost = string.Equals(address, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(address, "localhost", StringComparison.OrdinalIgnoreCase);
                string localLanAddress = NetworkAddressUtility.GetLocalLanAddress();
                
                // Also check if we're connecting to our own LAN IP (should match localhost lobbies)
                bool isLocalLanAddress = !string.IsNullOrEmpty(localLanAddress) && 
                                         string.Equals(address, localLanAddress, StringComparison.OrdinalIgnoreCase);

                // Try to find lobby info from discovered lobbies first
                LobbyInfo discoveredLobby = null;
                if (_discovery != null)
                {
                    var discoveredLobbies = _discovery.DiscoveredLobbies;
                    NetworkLogger.Info($"Looking for {address}:{port} in {discoveredLobbies.Count} discovered lobbies (isLocalhost={isLocalhost}, isLocalLan={isLocalLanAddress}, localLan={localLanAddress})");
                    foreach (var lobby in discoveredLobbies.Values)
                    {
                        NetworkLogger.Info($"Checking lobby: {lobby.LobbyName} at {lobby.IpAddress}:{lobby.Port} (public: {lobby.PublicAddress}:{lobby.PublicPort})");
                        
                        // Check if this lobby matches the requested address
                        bool addressMatches = lobby.IpAddress == address || lobby.PublicAddress == address;
                        
                        // Also check if we're connecting to localhost and the lobby is on our local LAN IP
                        if (!addressMatches && isLocalhost && !string.IsNullOrEmpty(localLanAddress))
                        {
                            addressMatches = lobby.IpAddress == localLanAddress || lobby.PublicAddress == localLanAddress;
                        }
                        
                        // Also check if we're connecting to our LAN IP and the lobby is on localhost
                        // This handles the case where discovery reports 127.0.0.1 but we're connecting via LAN IP
                        if (!addressMatches && isLocalLanAddress)
                        {
                            addressMatches = lobby.IpAddress == "127.0.0.1" || lobby.IpAddress == "localhost";
                        }
                        
                        if (addressMatches && lobby.Port == port)
                        {
                            discoveredLobby = lobby;
                            NetworkLogger.Info($"Found discovered lobby: {lobby.LobbyName}");
                            break;
                        }
                    }
                }
                else
                {
                    NetworkLogger.Info(" Discovery is null, cannot look up discovered lobbies");
                }

                // Client-side check: If we have lobby info, verify there's enough room for our profiles
                // This provides early feedback without waiting for server rejection
                if (discoveredLobby != null)
                {
                    int localProfileCount = GetLocalProfileCount();
                    int availableSlots = discoveredLobby.MaxPlayers - discoveredLobby.CurrentPlayers;
                    
                    if (localProfileCount > availableSlots)
                    {
                        _isJoinInProgress = false;
                        string errorMessage = $"Cannot join: Lobby only has {availableSlots} slot{(availableSlots == 1 ? "" : "s")} available, but you have {localProfileCount} profile{(localProfileCount == 1 ? "" : "s")} connected.";
                        NetworkLogger.Warn($"[LiteNetNetworkingAdapter] {errorMessage}");
                        YARG.Menu.Persistent.ToastManager.ToastWarning(errorMessage);
                        OnNetworkError?.Invoke(errorMessage);
                        return;
                    }
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
                        PlayerInstruments = discoveredLobby.PlayerInstruments,
                        IsDedicatedServer = discoveredLobby.IsDedicatedServer
                    };
                    NetworkLogger.Info($"Joining lobby with IsDedicatedServer={discoveredLobby.IsDedicatedServer}");
                }
                else
                {
                    NetworkLogger.Info($"No discovered lobby found for {endpoint}, using temporary info");
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

                // Use ClientNetworkingBootstrapper to create the client stack
                _clientStack = ClientNetworkingBootstrapper.Initialize(_transport, _serializer);
                
                // Update legacy references for gradual migration
                _clientRuntime = _clientStack.Runtime;
                _packetDispatcher = _clientStack.PacketDispatcher;
                
                // Subscribe to client events
                _clientRuntime.Connected += OnClientConnected;
                _clientRuntime.Disconnected += OnClientDisconnected;
                _clientRuntime.HandshakeCompleted += OnClientHandshakeCompleted;

                // Connect to server asynchronously
                _ = ConnectToServerAsync(address, port);
            }
            catch (Exception ex)
            {
                NetworkLogger.Error($"[LiteNetNetworkingAdapter] Failed to join lobby: {ex.Message}");
                _isJoinInProgress = false;
                OnNetworkError?.Invoke($"Failed to join lobby: {ex.Message}");
            }
        }

        /// <summary>
        /// Connection timeout in seconds for joining a lobby.
        /// </summary>
        private const int CONNECTION_TIMEOUT_SECONDS = 15;
        
        private async Task ConnectToServerAsync(string address, int port)
        {
            try
            {
                NetworkLogger.Info($"Attempting connection to {address}:{port} (timeout: {CONNECTION_TIMEOUT_SECONDS}s)");
                
                // Create a cancellation token with timeout
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(CONNECTION_TIMEOUT_SECONDS));
                
                await _clientRuntime.ConnectAsync(address, port, cts.Token);
                NetworkLogger.Info($"Successfully connected to {address}:{port}");
            }
            catch (OperationCanceledException)
            {
                // Connection timed out
                NetworkLogger.Error($"[LiteNetNetworkingAdapter] Connection to {address}:{port} timed out after {CONNECTION_TIMEOUT_SECONDS} seconds");
                _isJoinInProgress = false;
                OnNetworkError?.Invoke($"Connection timed out. The host may be behind a firewall or the port may not be forwarded.");
            }
            catch (Exception ex)
            {
                NetworkLogger.Error($"[LiteNetNetworkingAdapter] Connection failed: {ex.Message}");
                _isJoinInProgress = false;
                OnNetworkError?.Invoke($"Connection failed: {ex.Message}");
            }
        }

        public void JoinDiscoveredLobby(LobbyInfo lobby, string password = "")
        {
            NetworkLogger.Info($"Joining discovered lobby: {lobby.LobbyName}");
            
            // Pre-validate that our profiles are allowed by this lobby's game mode restrictions
            // The AllowedGameModes list is a BLACKLIST - game modes in the list are DISABLED
            if (lobby.AllowedGameModes != null && lobby.AllowedGameModes.Count > 0)
            {
                if (!ValidateProfilesAgainstGameModes(lobby.AllowedGameModes, out var blockedProfiles))
                {
                    // Show toast instead of OnNetworkError to avoid navigating away
                    string blockedList = string.Join(", ", blockedProfiles);
                    NetworkLogger.Warn($"[LiteNetNetworkingAdapter] Cannot join - blocked profiles: {blockedList}");
                    YARG.Menu.Persistent.ToastManager.ToastWarning($"Cannot join: {blockedList} uses a disabled game mode.");
                    return;
                }
            }
            
            string endpoint = $"{lobby.IpAddress}:{lobby.Port}";
            JoinLobby(endpoint, password);
        }
        
        private void NotifyClientsOfHostDisconnect()
        {
            if (_liteNetTransport?.NetManager == null)
            {
                NetworkLogger.Info(" No NetManager available to notify clients");
                return;
            }
            
            var netManager = _liteNetTransport.NetManager;
            var peers = netManager.ConnectedPeerList;
            
            if (peers.Count == 0)
            {
                NetworkLogger.Info(" No connected peers to notify");
                return;
            }
            
            NetworkLogger.Info($"Notifying {peers.Count} clients of host disconnect");
            
            // Build host disconnect packet using NavigationBinaryPackets
            byte[] message = NavigationBinaryPackets.BuildHostDisconnectPacket();
            
            foreach (var peer in peers)
            {
                try
                {
                    peer.Send(message, LiteNetLib.DeliveryMethod.ReliableOrdered);
                    NetworkLogger.Info($"Sent host disconnect notification to {peer}");
                }
                catch (Exception ex)
                {
                    NetworkLogger.Warn($"[LiteNetNetworkingAdapter] Failed to notify peer {peer}: {ex.Message}");
                }
            }
            
            // Give a moment for the messages to be sent
            System.Threading.Thread.Sleep(100);
        }

        public void LeaveLobby()
        {
            NetworkLogger.Info("Leaving lobby");

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
                    NetworkLogger.Info(" Stopping server runtime...");
                    try
                    {
                        _serverRuntime.StopAsync().Wait(TimeSpan.FromSeconds(2));
                    }
                    catch (Exception stopEx)
                    {
                        NetworkLogger.Warn($"[LiteNetNetworkingAdapter] Server stop warning: {stopEx.Message}");
                    }
                    _isHosting = false;
                    NetworkLogger.Info(" Server stopped");
                }
                
                if (_isConnected)
                {
                    // Disconnect from server
                    NetworkLogger.Info(" Disconnecting client...");
                    try
                    {
                        _clientRuntime.DisconnectAsync().Wait(TimeSpan.FromSeconds(2));
                    }
                    catch (Exception discEx)
                    {
                        NetworkLogger.Warn($"[LiteNetNetworkingAdapter] Disconnect warning: {discEx.Message}");
                    }
                    _isConnected = false;
                    NetworkLogger.Info(" Client disconnected");
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
                _connectedPlayersByConnection.Clear();
                _pendingConnections.Clear();
                lock (_pendingPlayerIdentitiesLock)
                {
                    _pendingPlayerIdentities.Clear();
                }
                _connectionMap.Clear();
                _isJoinInProgress = false;
                
                // Reset handshake retry state
                _handshakeSent = false;
                _handshakeAcknowledged = false;
                _handshakeRetryCount = 0;
                _serverConnection = null;
                if (_handshakeRetryCoroutine != null)
                {
                    GlobalVariables.Instance?.StopCoroutine(_handshakeRetryCoroutine);
                    _handshakeRetryCoroutine = null;
                }

                OnLobbyLeft?.Invoke();
                NetworkLogger.Info(" Lobby left successfully");
            }
            catch (Exception ex)
            {
                NetworkLogger.Error($"[LiteNetNetworkingAdapter] Error leaving lobby: {ex.Message}");
                OnNetworkError?.Invoke($"Error leaving lobby: {ex.Message}");
            }
        }

        public async Task<LobbyInfo?> ProbeLobby(string address, int port)
        {
            NetworkLogger.Info($"Probing lobby at {address}:{port}");

            try
            {
                if (_discovery == null)
                {
                    NetworkLogger.Warn("[LiteNetNetworkingAdapter] Discovery not initialized");
                    return null;
                }
                
                // Use TaskCompletionSource to wait for the discovery response
                var tcs = new TaskCompletionSource<LobbyInfo>();
                LobbyInfo discoveredLobby = null;
                
                // Set up a temporary handler for discovery responses
                void OnDiscovered(LobbyInfo lobby)
                {
                    // Check if this response is from the address we're probing
                    if (lobby.IpAddress == address && lobby.Port == port)
                    {
                        discoveredLobby = lobby;
                        tcs.TrySetResult(lobby);
                    }
                }
                
                // Subscribe to discovery events
                _discovery.OnLobbyDiscovered += OnDiscovered;
                
                try
                {
                    // Ensure discovery is running
                    _discovery.StartDiscovery();
                    
                    // Send a discovery request to the specific address
                    _discovery.SendDiscoveryRequest(address, port);
                    
                    // Wait for response with timeout (3 seconds)
                    using var cts = new CancellationTokenSource(3000);
                    var timeoutTask = Task.Delay(-1, cts.Token);
                    
                    var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
                    
                    if (completedTask == tcs.Task)
                    {
                        NetworkLogger.Info($"Probe succeeded for {address}:{port}");
                        return discoveredLobby;
                    }
                    else
                    {
                        NetworkLogger.Info($"Probe timed out for {address}:{port}");
                        return null;
                    }
                }
                finally
                {
                    // Unsubscribe from discovery events
                    _discovery.OnLobbyDiscovered -= OnDiscovered;
                }
            }
            catch (Exception ex)
            {
                NetworkLogger.Error($"[LiteNetNetworkingAdapter] Failed to probe lobby: {ex.Message}");
                return null;
            }
        }

        #endregion
        
        #region Discovery

        public void StartDiscovery()
        {
            NetworkLogger.Info(" Starting discovery");
            _discovery?.StartDiscovery();
        }
        
        public void StopDiscovery()
        {
            NetworkLogger.Info(" Stopping discovery");
            _discovery?.StopDiscovery();
        }
        
        public void SendDiscoveryRequest(string address, int port = 0)
        {
            NetworkLogger.Info($"Sending discovery request to {address}:{(port > 0 ? port : _defaultPort)}");
            _discovery?.SendDiscoveryRequest(address, port > 0 ? port : _defaultPort);
        }
        
        public void SendBroadcastDiscoveryRequest(int port = 0)
        {
            NetworkLogger.Info($"Sending broadcast discovery request on port {(port > 0 ? port : _defaultPort)}");
            _discovery?.SendBroadcastDiscoveryRequest(port > 0 ? port : _defaultPort);
        }
        
        public void SetDiscoveryPort(int port)
        {
            NetworkLogger.Info($"Setting discovery port to {port}");
            _discovery?.SetDiscoveryPort(port);
        }
        
        #endregion

        #region Player Management

        public void SetPlayerName(string name)
        {
            NetworkLogger.Info($"Setting player name to: {name}");
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
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Only the host can kick players");
                return;
            }
            
            if (playerData == null)
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] Cannot kick null player");
                return;
            }
            
            NetworkLogger.Info($"Kicking player: {playerData.PlayerName}");
            
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
                NetworkLogger.Warn($"[LiteNetNetworkingAdapter] Could not find connection for player: {playerData.PlayerName}");
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
                    NetworkLogger.Info($"Disconnecting peer {peer}");
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
            
            NetworkLogger.Info($"Player kicked: {playerData.PlayerName}");
        }

        #endregion

        #region Song Selection and Gameplay

        public void StartSongSelection()
        {
            NetworkLogger.Info(" Starting song selection");

            try
            {
                if (!_isHosting)
                {
                    NetworkLogger.Warn("[LiteNetNetworkingAdapter] Only host can start song selection");
                    return;
                }
                
                // Navigate all clients to the music library for song selection
                BroadcastNavigateToMusicLibrary();
            }
            catch (Exception ex)
            {
                NetworkLogger.Error($"[LiteNetNetworkingAdapter] Failed to start song selection: {ex.Message}");
                OnNetworkError?.Invoke($"Failed to start song selection: {ex.Message}");
            }
        }

        public void StartMultiplayerSong(SongEntry song)
        {
            NetworkLogger.Info($"Starting multiplayer song: {song.Name}");

            try
            {
                if (!_isHosting)
                {
                    NetworkLogger.Warn("[LiteNetNetworkingAdapter] Only host can start multiplayer song");
                    return;
                }
                
                // Add the song to the setlist if not already there
                string songHash = song.Hash.ToString();
                if (!_setlistManager.Contains(songHash))
                {
                    string playerName = GetPlayerNameFromProfile();
                    _setlistManager.TryAdd(songHash, song.Name, song.Artist, playerName, out _);
                    BroadcastSetlistAdd(songHash, playerName, song.Name, song.Artist);
                }
            }
            catch (Exception ex)
            {
                NetworkLogger.Error($"[LiteNetNetworkingAdapter] Failed to start multiplayer song: {ex.Message}");
                OnNetworkError?.Invoke($"Failed to start multiplayer song: {ex.Message}");
            }
        }

        public void StartMultiplayerGameplay()
        {
            NetworkLogger.Info(" Starting multiplayer gameplay");

            try
            {
                if (!_isHosting)
                {
                    NetworkLogger.Warn("[LiteNetNetworkingAdapter] Only host can start multiplayer gameplay");
                    return;
                }
                
                // Reset all players' gameplay state and the "all loaded" signal
                lock (_gameplayStartLock)
                {
                    _allPlayersLoadedSignaled = false;
                }
                
                foreach (var kvp in _connectedPlayers)
                {
                    foreach (var player in kvp.Value)
                    {
                        if (player != null)
                        {
                            player.ResetGameState();
                            player.SetGameplayReadyServer(false);
                        }
                    }
                }
                
                // Broadcast the start show command (which initiates the show/setlist)
                BroadcastStartShow();
                
                // Note: Actual gameplay start happens when all players are ready
                // via HandleGameplayReady -> CheckAllPlayersGameplayReady -> BroadcastStartGameplay
            }
            catch (Exception ex)
            {
                NetworkLogger.Error($"[LiteNetNetworkingAdapter] Failed to start multiplayer gameplay: {ex.Message}");
                OnNetworkError?.Invoke($"Failed to start multiplayer gameplay: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Waits asynchronously for all players to finish loading and be ready for gameplay.
        /// Called by GameManager after loading the song to synchronize the start.
        /// </summary>
        /// <param name="cancellationToken">Token to cancel the wait.</param>
        public async System.Threading.Tasks.Task WaitForMultiplayerGameplayStartAsync(System.Threading.CancellationToken cancellationToken = default)
        {
            TaskCompletionSource<bool> tcs;
            
            lock (_gameplayStartLock)
            {
                // If all players already signaled ready before we started waiting, return immediately
                if (_allPlayersLoadedSignaled)
                {
                    NetworkLogger.Info("All players already loaded - skipping wait");
                    return;
                }
                
                // Create a new TCS if one doesn't exist or is already completed
                if (_gameplayStartTcs == null || _gameplayStartTcs.Task.IsCompleted)
                {
                    _gameplayStartTcs = new TaskCompletionSource<bool>();
                }
                tcs = _gameplayStartTcs;
            }
            
            NetworkLogger.Info("Waiting for multiplayer gameplay start...");
            
            // Register cancellation
            using (cancellationToken.Register(() => tcs.TrySetCanceled()))
            {
                await tcs.Task;
            }
            
            NetworkLogger.Info("Multiplayer gameplay start complete");
        }
        
        /// <summary>
        /// Forces the gameplay start barrier to complete, even if not all players are ready.
        /// Called when timing out or when the host decides to proceed anyway.
        /// </summary>
        public void ForceCompleteGameplayStartBarrier()
        {
            lock (_gameplayStartLock)
            {
                if (_gameplayStartTcs != null)
                {
                    _gameplayStartTcs.TrySetResult(true);
                    NetworkLogger.Info("Forced gameplay start barrier completion");
                }
            }
        }
        
        /// <summary>
        /// Signals that a player has finished loading and is ready for gameplay.
        /// When all players are ready, the barrier is released.
        /// </summary>
        internal void SignalPlayerGameplayReady(NetworkPlayerData player)
        {
            if (player == null) return;
            
            player.SetGameplayReadyServer(true);
            NetworkLogger.Info($"Player {player.PlayerName} signaled gameplay ready");
            
            // Check if all players are ready
            if (AreAllPlayersGameplayReady())
            {
                lock (_gameplayStartLock)
                {
                    _gameplayStartTcs?.TrySetResult(true);
                }
                NetworkLogger.Info("All players gameplay ready - releasing barrier");
            }
        }
        
        /// <summary>
        /// Checks if all connected players are ready for gameplay.
        /// </summary>
        private bool AreAllPlayersGameplayReady()
        {
            if (_connectedPlayers.Count == 0)
            {
                NetworkLogger.Info("[AreAllPlayersGameplayReady] No connected players, returning false");
                return false;
            }
            
            foreach (var kvp in _connectedPlayers)
            {
                foreach (var player in kvp.Value)
                {
                    if (player != null && !player.GameplayReady)
                    {
                        NetworkLogger.Info($"[AreAllPlayersGameplayReady] Player '{player.PlayerName}' not ready (GameplayReady={player.GameplayReady})");
                        return false;
                    }
                }
            }
            return true;
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
            
            NetworkLogger.Info(" Navigating to difficulty select for next song");
            
            if (MenuManager.Instance != null)
            {
                MenuManager.Instance.PushMenu(MenuManager.Menu.DifficultySelect);
            }
            else
            {
                NetworkLogger.Warn("[LiteNetNetworkingAdapter] MenuManager.Instance is null, cannot navigate to difficulty select");
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
            NetworkLogger.Info($"No valid profile found, using generated identity: {randomName}");
            return LocalPlayerIdentity.GetOrCreate(randomName);
        }

        /// <summary>
        /// Gets network identities for ALL local players with active bindings (connected controllers).
        /// Used to register multiple local profiles with an online lobby.
        /// Does NOT fall back to disconnected profiles - only returns players that are actively connected.
        /// </summary>
        private List<NetworkPlayerIdentity> GetAllLocalPlayerIdentities()
        {
            var identities = new List<NetworkPlayerIdentity>();
            
            // ONLY get from PlayerContainer.Players (active players with controllers)
            // Do NOT fall back to disconnected profiles
            var localPlayers = PlayerContainer.Players;
            if (localPlayers != null && localPlayers.Count > 0)
            {
                foreach (var player in localPlayers)
                {
                    var profile = player.Profile;
                    if (profile != null)
                    {
                        string displayName = SanitizeDisplayName(profile.Name);
                        var identity = LocalPlayerIdentity.GetOrCreate(displayName, profile.Id);
                        identities.Add(identity);
                        NetworkLogger.Info($"Added local player identity: {displayName} (ProfileId: {profile.Id}, NetworkPlayerId: {identity.PlayerId})");
                    }
                }
            }
            
            // No fallback - if no players are connected, return empty list
            // The caller should check HasConnectedProfiles() before calling this
            if (identities.Count == 0)
            {
                NetworkLogger.Warn($"GetAllLocalPlayerIdentities: No connected profiles found!");
            }
            
            return identities;
        }
        
        /// <summary>
        /// Checks if there are any profiles with active controller bindings.
        /// Must have at least one connected profile to host or join a lobby.
        /// </summary>
        public bool HasConnectedProfiles()
        {
            var localPlayers = PlayerContainer.Players;
            return localPlayers != null && localPlayers.Count > 0;
        }
        
        /// <summary>
        /// Validates that all connected profiles have game modes that are allowed by the given blacklist.
        /// The blacklist contains game modes that are DISABLED - any profile with a game mode in the blacklist is invalid.
        /// </summary>
        /// <param name="gameModeBlacklist">List of disabled game modes (empty means all allowed)</param>
        /// <param name="blockedProfiles">Out parameter containing names of profiles that are blocked</param>
        /// <returns>True if all profiles are valid, false if any profile has a blocked game mode</returns>
        public bool ValidateProfilesAgainstGameModes(List<YARG.Core.GameMode> gameModeBlacklist, out List<string> blockedProfiles)
        {
            blockedProfiles = new List<string>();
            
            // Empty or null blacklist means all game modes are allowed
            if (gameModeBlacklist == null || gameModeBlacklist.Count == 0)
            {
                return true;
            }
            
            var localPlayers = PlayerContainer.Players;
            if (localPlayers == null || localPlayers.Count == 0)
            {
                return true; // No profiles to validate
            }
            
            foreach (var player in localPlayers)
            {
                if (player?.Profile == null) continue;
                
                var gameMode = player.Profile.GameMode;
                if (gameModeBlacklist.Contains(gameMode))
                {
                    string profileName = player.Profile.Name ?? "Unknown";
                    blockedProfiles.Add($"{profileName} ({gameMode})");
                    NetworkLogger.Warn($"[ValidateProfilesAgainstGameModes] Profile '{profileName}' has blocked game mode: {gameMode}");
                }
            }
            
            return blockedProfiles.Count == 0;
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

        /// <summary>
        /// Gets the local player's profile instrument and difficulty.
        /// Returns default values if no profile is available.
        /// </summary>
        private (int instrument, int difficulty) GetLocalProfileInstrumentAndDifficulty()
        {
            var localPlayers = PlayerContainer.Players;
            if (localPlayers != null && localPlayers.Count > 0)
            {
                var profile = localPlayers[0].Profile;
                if (profile != null)
                {
                    int instrument = (int)profile.CurrentInstrument;
                    int difficulty = (int)profile.CurrentDifficulty;
                    NetworkLogger.Info($"Got instrument/difficulty from profile: {profile.CurrentInstrument} ({instrument}), {profile.CurrentDifficulty} ({difficulty})");
                    return (instrument, difficulty);
                }
            }
            
            // Fallback: Try to get from PlayerContainer.Profiles
            var profiles = PlayerContainer.Profiles;
            if (profiles != null && profiles.Count > 0)
            {
                foreach (var profile in profiles)
                {
                    if (profile.IsBot) continue;
                    
                    int instrument = (int)profile.CurrentInstrument;
                    int difficulty = (int)profile.CurrentDifficulty;
                    NetworkLogger.Info($"Got instrument/difficulty from Profiles: {profile.CurrentInstrument} ({instrument}), {profile.CurrentDifficulty} ({difficulty})");
                    return (instrument, difficulty);
                }
            }
            
            NetworkLogger.Info("No profile found for instrument/difficulty, using defaults (-1, 0)");
            return (-1, 0);
        }

        private NetworkPlayerData CreateLiteNetPlayerData(string playerName, bool isHost, bool isLocal, int initialInstrument = -1, int initialDifficulty = 0, Guid connectionId = default)
        {
            // Create a GameObject with NetworkPlayerData
            // We keep it disabled to prevent Mirror's OnEnable from running and causing NullReferenceExceptions
            var playerObject = new GameObject($"NetworkPlayer_{playerName}");
            playerObject.SetActive(false); // Keep disabled to prevent Mirror initialization
            
            var playerData = playerObject.AddComponent<NetworkPlayerData>();
            
            // Debug: List all private fields on NetworkPlayerData to understand Mirror's IL weaving
            var allFields = typeof(NetworkPlayerData).GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            NetworkLogger.Info($"NetworkPlayerData has {allFields.Length} private instance fields");
            foreach (var f in allFields)
            {
                if (f.Name.Contains("host") || f.Name.Contains("Host") || f.Name.Contains("name") || f.Name.Contains("Name") || f.Name.Contains("ping") || f.Name.Contains("Ping"))
                {
                    NetworkLogger.Info($"  Field: {f.Name} ({f.FieldType.Name})");
                }
            }
            
            // Set properties using reflection to avoid Mirror server methods
            var playerNameField = typeof(NetworkPlayerData).GetField("playerName", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var isHostField = typeof(NetworkPlayerData).GetField("isHost", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var pingField = typeof(NetworkPlayerData).GetField("ping",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var instrumentField = typeof(NetworkPlayerData).GetField("instrument",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var difficultyField = typeof(NetworkPlayerData).GetField("difficulty",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            NetworkLogger.Info($"CreateLiteNetPlayerData reflection - playerNameField: {playerNameField != null}, isHostField: {isHostField != null}, pingField: {pingField != null}");
            
            if (playerNameField != null)
            {
                playerNameField.SetValue(playerData, playerName);
                NetworkLogger.Info($"Set playerName to '{playerName}'");
            }
            else
            {
                NetworkLogger.Error("[LiteNetNetworkingAdapter] playerName field not found!");
            }
            
            if (isHostField != null)
            {
                isHostField.SetValue(playerData, isHost);
                NetworkLogger.Info($"Set isHost to {isHost}");
            }
            else
            {
                NetworkLogger.Error("[LiteNetNetworkingAdapter] isHost field not found!");
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
                NetworkLogger.Info($"Set ping to {initialPing}");
            }
            else
            {
                NetworkLogger.Error("[LiteNetNetworkingAdapter] ping field not found!");
            }
            
            // Set initial instrument and difficulty from profile (for local players)
            if (instrumentField != null)
            {
                instrumentField.SetValue(playerData, initialInstrument);
                NetworkLogger.Info($"Set instrument to {initialInstrument}");
            }
            if (difficultyField != null)
            {
                difficultyField.SetValue(playerData, initialDifficulty);
                NetworkLogger.Info($"Set difficulty to {initialDifficulty}");
            }
            
            // Set ConnectionId - this is the key identifier for player grouping
            // Use HostConnectionId for host players, or the provided connectionId for clients
            if (connectionId != default)
            {
                playerData.ConnectionId = connectionId;
            }
            else if (isHost && isLocal)
            {
                // Host's local players use the well-known HostConnectionId
                playerData.ConnectionId = HostConnectionId;
            }
            // Note: For remote players, connectionId should always be provided by the caller
            
            NetworkLogger.Info($"Set ConnectionId to {playerData.ConnectionId}");
            
            // Set IsLocalUser override for LiteNet (since Mirror's isClient/isLocalPlayer don't work)
            var useLocalUserOverrideField = typeof(NetworkPlayerData).GetField("_useLocalUserOverride",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var isLocalUserOverrideField = typeof(NetworkPlayerData).GetField("_isLocalUserOverride",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            if (useLocalUserOverrideField != null && isLocalUserOverrideField != null)
            {
                useLocalUserOverrideField.SetValue(playerData, true);
                isLocalUserOverrideField.SetValue(playerData, isLocal);
                NetworkLogger.Info($"Set IsLocalUser override to {isLocal}");
            }
            
            // Verify the values were set correctly
            NetworkLogger.Info($"Verifying: PlayerName='{playerData.PlayerName}', IsHost={playerData.IsHost}, Ping={playerData.Ping}, IsLocalUser={playerData.IsLocalUser}, Instrument={playerData.Instrument}, Difficulty={playerData.Difficulty}, ConnectionId={playerData.ConnectionId}");
            
            // Don't destroy on scene change
            GameObject.DontDestroyOnLoad(playerObject);
            
            // NOTE: We intentionally do NOT activate the GameObject
            // This prevents NetworkPlayerData.OnEnable() from running and causing Mirror-related NullReferenceExceptions
            // The player data is still usable for reading properties like PlayerName and IsHost
            
            NetworkLogger.Info($"Created player data for '{playerName}' (host={isHost}, local={isLocal}, ping={initialPing}, instrument={initialInstrument}, difficulty={initialDifficulty})");
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
            NetworkLogger.Info($"Updated lobby PlayerNames: [{string.Join(", ", allNames)}]");
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
            
            NetworkLogger.Info(" Starting STUN resolution for public IP...");
            _ = _publicEndpointResolver.ResolveAsync(_currentLobby.Port);
        }
        
        /// <summary>
        /// Cancels any in-progress STUN resolution and clears the cached result.
        /// This ensures the next session will properly fire the EndpointResolved event
        /// even if the public IP is the same as before.
        /// </summary>
        private void CancelPublicEndpointResolution()
        {
            _publicEndpointResolver?.Clear();
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
            NetworkLogger.Warn($"[LiteNetNetworkingAdapter] STUN resolution failed: {e.Reason}");
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
                NetworkLogger.Info($"Resolved public endpoint via STUN: {address}:{port}");
                
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
            NetworkLogger.Info($"Setlist song added: {e.Entry.SongName} by {e.Entry.AddedByPlayerName}");
            OnSetlistSongAdded?.Invoke(e.Entry.AddedByPlayerName, e.Entry.SongName, e.Entry.SongArtist);
            OnSetlistUpdated?.Invoke();
        }
        
        /// <summary>
        /// Bridge from SetlistManager.SongRemoved to adapter events.
        /// </summary>
        private void OnSetlistManagerSongRemoved(object sender, SetlistSongEventArgs e)
        {
            NetworkLogger.Info($"Setlist song removed: {e.Entry.SongName}");
            OnSetlistSongRemoved?.Invoke(e.Entry.AddedByPlayerName, e.Entry.SongName, e.Entry.SongArtist);
            OnSetlistUpdated?.Invoke();
        }
        
        /// <summary>
        /// Bridge from SetlistManager.SetlistCleared to adapter events.
        /// </summary>
        private void OnSetlistManagerCleared(object sender, EventArgs e)
        {
            NetworkLogger.Info(" Setlist cleared");
            OnSetlistUpdated?.Invoke();
        }
        
        /// <summary>
        /// Bridge from SongLibrarySyncHandler.OnSyncStateChanged to adapter events.
        /// </summary>
        private void HandleSongLibrarySyncStateChanged(bool isComplete)
        {
            NetworkLogger.Info($"Song library sync state changed (handler): complete={isComplete}");
            OnSharedSongSyncStateChanged?.Invoke(isComplete);
        }
        
        /// <summary>
        /// Bridge from SharedSongLibraryManager.SyncStateChanged to adapter events.
        /// </summary>
        private void HandleSharedSongSyncStateChanged(object sender, SyncStateChangedEventArgs e)
        {
            NetworkLogger.Info($"Shared song sync state changed: complete={e.IsComplete}");
            OnSharedSongSyncStateChanged?.Invoke(e.IsComplete);
        }
        
        /// <summary>
        /// Bridge from UnisonCoordinator.UnisonBonusAwarded to adapter events.
        /// </summary>
        private void OnUnisonCoordinatorBonusAwarded(object sender, UnisonBonusEventArgs e)
        {
            NetworkLogger.Info($"Unison bonus awarded for band {e.BandId}, phrase at {e.PhraseTime}");
            
            // Broadcast to all clients if hosting
            if (_isHosting)
            {
                BroadcastUnisonBonusAward(e.BandId, e.PhraseTime);
            }
            
            // Fire local event too (for host's own UI)
            OnUnisonBonusAwarded?.Invoke(e.BandId, e.PhraseTime);
        }
        
        /// <summary>
        /// Bridge from ScoreResultsManager.ResultReceived to adapter events.
        /// </summary>
        private void OnScoreResultsManagerResultReceived(object sender, ScoreResultEventArgs e)
        {
            NetworkLogger.Info($"Score result received: {e.Result.PlayerName} - {e.Result.Score}");
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
        
        #region Late Join Handling
        
        /// <summary>
        /// Updates the current session phase. Called by host when transitioning between states.
        /// </summary>
        public void SetSessionPhase(SessionPhase phase, string currentSongHash = null)
        {
            _currentSessionPhase = phase;
            _currentSongHash = currentSongHash ?? string.Empty;
            
            NetworkLogger.Info($"[LateJoin] Session phase changed to: {phase}, CurrentSong: {_currentSongHash}");
        }
        
        /// <summary>
        /// Checks if late join is allowed based on current session settings.
        /// </summary>
        public bool IsLateJoinAllowed()
        {
            // Check the session setting
            if (_currentLobby == null)
                return false;
            
            // Get from session settings
            var gameplaySettings = MultiplayerGameplaySettings.Instance;
            var sessionLifecycle = SessionLifecycleManager.Instance;
            
            // Log detailed preset info for debugging
            var lifecyclePreset = sessionLifecycle?.CurrentPreset;
            var gameplayPreset = gameplaySettings?.ActivePreset;
            
            NetworkLogger.Info($"[LateJoin] IsLateJoinAllowed debug: lifecyclePreset={lifecyclePreset != null}, " +
                $"lifecyclePreset.allowLateJoin={lifecyclePreset?.allowLateJoin}, " +
                $"gameplayPreset={gameplayPreset != null}, " +
                $"gameplayPreset.allowLateJoin={gameplayPreset?.allowLateJoin}");
            
            var preset = lifecyclePreset ?? gameplayPreset;
            
            bool allowed = preset?.allowLateJoin ?? true;
            NetworkLogger.Info($"[LateJoin] IsLateJoinAllowed: preset={preset != null}, allowLateJoin={allowed}");
            return allowed;
        }
        
        /// <summary>
        /// Determines if a joining player is a late joiner based on current session phase.
        /// </summary>
        public bool IsLateJoinScenario()
        {
            return _currentSessionPhase == SessionPhase.PlayingSong ||
                   _currentSessionPhase == SessionPhase.DifficultySelect ||
                   _currentSessionPhase == SessionPhase.Countdown ||
                   _currentSessionPhase == SessionPhase.ScoreScreen;
        }
        
        /// <summary>
        /// Host: Initiates the late join check for a newly connected player.
        /// Sends session state to the client so they can check song ownership.
        /// </summary>
        private void InitiateLateJoinCheck(INetConnection connection, List<NetworkPlayerData> players)
        {
            if (!_isHosting || !IsLateJoinScenario())
            {
                NetworkLogger.Info($"[LateJoin] Not a late join scenario (phase={_currentSessionPhase})");
                return;
            }
            
            if (!IsLateJoinAllowed())
            {
                NetworkLogger.Info("[LateJoin] Late join not allowed by session settings - sending rejection to player");
                
                // Send a rejection message to the client before disconnecting
                foreach (var player in players)
                {
                    byte[] rejectPacket = LateJoinBinaryPackets.BuildLateJoinActionPacket(
                        player.NetworkPlayerId,
                        player.PlayerName,
                        LateJoinAction.Rejected,
                        "A setlist is currently in progress. Late joining is disabled for this session.",
                        songTime: 0);
                    connection.Send(rejectPacket, ChannelType.ReliableOrdered);
                }
                
                // Disconnect the player after a short delay to ensure message is sent
                UnityMainThreadDispatcher.EnqueueAction(() =>
                {
                    // The OnTransportPeerDisconnected handler will clean up _connectedPlayers
                    // Just disconnect - the normal disconnect flow will handle cleanup
                    connection.Disconnect();
                    NetworkLogger.Info($"[LateJoin] Disconnected late joiner {connection.EndPoint} - late join disabled");
                });
                return;
            }
            
            // Create late joiner state
            var lateJoinerState = new LateJoinPlayerState
            {
                ConnectionId = connection.Id,
                ConnectedTime = DateTime.UtcNow
            };
            
            foreach (var player in players)
            {
                lateJoinerState.PlayerIds.Add(player.NetworkPlayerId);
                lateJoinerState.PlayerNames.Add(player.PlayerName);
            }
            
            _pendingLateJoiners[connection.Id] = lateJoinerState;
            
            // Get remaining setlist
            var remainingHashes = _setlistManager?.SongHashes.ToList() ?? new List<string>();
            
            // Send late join state to client
            byte[] packet = LateJoinBinaryPackets.BuildLateJoinStatePacket(
                _currentSessionPhase,
                _currentSongHash,
                remainingHashes);
            
            connection.Send(packet, ChannelType.ReliableOrdered);
            NetworkLogger.Info($"[LateJoin] Host: Sent late join state to {connection.EndPoint} (phase={_currentSessionPhase}, currentSong={_currentSongHash}, setlist={remainingHashes.Count} songs)");
        }
        
        /// <summary>
        /// Client: Handles the late join state message from host.
        /// </summary>
        private void HandleLateJoinStateMessage(ReadOnlyMemory<byte> payload)
        {
            if (!LateJoinBinaryPackets.TryParseLateJoinStatePacket(payload.Span, out var phase, out var currentSongHash, out var remainingSetlistHashes))
            {
                NetworkLogger.Warn("[LateJoin] Client: Invalid late join state packet");
                return;
            }
            
            NetworkLogger.Info($"[LateJoin] Client: Received late join state - phase={phase}, currentSong={currentSongHash}, setlist={remainingSetlistHashes.Count} songs");
            
            _isLateJoiner = true;
            _waitingForLateJoinDecision = true;
            _currentSessionPhase = phase;
            _currentSongHash = currentSongHash ?? string.Empty;
            
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                // Check which songs we have
                bool hasCurrentSong = false;
                var ownedHashes = new List<string>();
                
                if (!string.IsNullOrEmpty(currentSongHash))
                {
                    var currentHashWrapper = YARG.Core.Song.HashWrapper.FromString(currentSongHash);
                    hasCurrentSong = SongContainer.SongsByHash.ContainsKey(currentHashWrapper);
                }
                
                foreach (var hash in remainingSetlistHashes)
                {
                    var hashWrapper = YARG.Core.Song.HashWrapper.FromString(hash);
                    if (SongContainer.SongsByHash.ContainsKey(hashWrapper))
                    {
                        ownedHashes.Add(hash);
                    }
                }
                
                NetworkLogger.Info($"[LateJoin] Client: Has current song: {hasCurrentSong}, Owns {ownedHashes.Count}/{remainingSetlistHashes.Count} setlist songs");
                
                // Use the session ID as our player identifier for the response
                // The host tracks us by connection ID anyway, this is just for logging
                var localPlayerId = Net.LocalPlayerIdentity.SessionId;
                
                // Send response to host
                byte[] responsePacket = LateJoinBinaryPackets.BuildSongCheckResponsePacket(
                    localPlayerId,
                    hasCurrentSong,
                    ownedHashes);
                
                _serverConnection?.Send(responsePacket, ChannelType.ReliableOrdered);
                NetworkLogger.Info("[LateJoin] Client: Sent song check response to host");
            });
        }
        
        /// <summary>
        /// Host: Handles the song check response from a late joiner.
        /// </summary>
        private void HandleLateJoinSongCheckResponseMessage(INetConnection connection, ReadOnlyMemory<byte> payload)
        {
            if (!_isHosting)
                return;
            
            if (!LateJoinBinaryPackets.TryParseSongCheckResponsePacket(payload.Span, out var playerId, out var hasCurrentSong, out var ownedSetlistHashes))
            {
                NetworkLogger.Warn("[LateJoin] Host: Invalid song check response packet");
                return;
            }
            
            NetworkLogger.Info($"[LateJoin] Host: Received song check from player {playerId} - hasCurrentSong={hasCurrentSong}, owns {ownedSetlistHashes.Count} setlist songs");
            
            if (!_pendingLateJoiners.TryGetValue(connection.Id, out var lateJoinerState))
            {
                NetworkLogger.Warn($"[LateJoin] Host: No pending late joiner state for connection {connection.Id}");
                return;
            }
            
            lateJoinerState.HasSongCheckResponse = true;
            lateJoinerState.HasCurrentSong = hasCurrentSong;
            lateJoinerState.OwnedSetlistHashes = new HashSet<string>(ownedSetlistHashes, StringComparer.OrdinalIgnoreCase);
            
            // Check if they have all remaining setlist songs
            var remainingHashes = _setlistManager?.SongHashes.ToList() ?? new List<string>();
            bool hasAllSetlistSongs = remainingHashes.All(h => lateJoinerState.OwnedSetlistHashes.Contains(h));
            lateJoinerState.IsMissingSetlistSongs = !hasAllSetlistSongs;
            
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                ProcessLateJoinDecision(connection, lateJoinerState);
            });
        }
        
        /// <summary>
        /// Host: Makes and broadcasts the late join decision for a player.
        /// </summary>
        private void ProcessLateJoinDecision(INetConnection connection, LateJoinPlayerState lateJoinerState)
        {
            LateJoinAction action;
            string message;
            double songTime = 0;
            
            // Get the current song time directly from GameManager (host's player data doesn't have updated time)
            var gameManager = UnityEngine.Object.FindAnyObjectByType<YARG.Gameplay.GameManager>();
            if (gameManager != null)
            {
                songTime = gameManager.SongTime;
                NetworkLogger.Info($"[LateJoin] Host: Current song time from GameManager is {songTime:F2}s");
            }
            else
            {
                NetworkLogger.Warn("[LateJoin] Host: GameManager not found, song time will be 0");
            }
            
            // Decision logic:
            // 1. If during song playback and they don't have current song -> Wait
            // 2. If during song playback and they have current song -> Spectate
            // 3. If missing setlist songs -> Abort setlist for everyone
            // 4. Otherwise -> Join difficulty select
            
            if (lateJoinerState.IsMissingSetlistSongs)
            {
                // Late joiner missing songs - abort setlist for EVERYONE
                action = LateJoinAction.AbortSetlist;
                message = $"Player {lateJoinerState.PlayerNames.FirstOrDefault()} doesn't have all setlist songs. Returning to music library.";
                NetworkLogger.Info($"[LateJoin] Host: Late joiner missing setlist songs - aborting setlist");
            }
            else if (_currentSessionPhase == SessionPhase.PlayingSong || _currentSessionPhase == SessionPhase.Countdown)
            {
                if (lateJoinerState.HasCurrentSong)
                {
                    action = LateJoinAction.SpectateCurrentSong;
                    message = "You'll spectate this song and join the next one.";
                }
                else
                {
                    action = LateJoinAction.WaitForSongEnd;
                    message = "Waiting for current song to finish...";
                }
            }
            else
            {
                // Between songs (diff select or score screen) - immediate join
                action = LateJoinAction.JoinDifficultySelect;
                message = "Joining game...";
            }
            
            lateJoinerState.DecidedAction = action;
            
            // Send presets to the late joiner if preset sync is enabled
            var gameplaySettings = MultiplayerGameplaySettings.Instance;
            var sessionLifecycle = SessionLifecycleManager.Instance;
            var preset = sessionLifecycle?.CurrentPreset ?? gameplaySettings?.ActivePreset;
            if (preset?.enablePresetSync == true)
            {
                NetworkLogger.Info($"[LateJoin] Host: Sending player presets to late joiner");
                SendAllPlayerPresetsToClient(connection);
            }
            
            // Send decision to the late joiner
            foreach (var playerId in lateJoinerState.PlayerIds)
            {
                byte[] actionPacket = LateJoinBinaryPackets.BuildLateJoinActionPacket(
                    playerId,
                    lateJoinerState.PlayerNames.FirstOrDefault() ?? "Player",
                    action,
                    message,
                    songTime);
                
                connection.Send(actionPacket, ChannelType.ReliableOrdered);
            }
            
            NetworkLogger.Info($"[LateJoin] Host: Sent decision {action} to late joiner: {message} (songTime={songTime:F2}s)");
            
            // If aborting setlist, broadcast to all clients
            if (action == LateJoinAction.AbortSetlist)
            {
                BroadcastSetlistAbort(message);
            }
        }
        
        /// <summary>
        /// Client: Handles the late join action decision from host.
        /// </summary>
        private void HandleLateJoinActionMessage(ReadOnlyMemory<byte> payload)
        {
            if (!LateJoinBinaryPackets.TryParseLateJoinActionPacket(payload.Span, out var playerId, out var playerName, out var action, out var message, out var songTime))
            {
                NetworkLogger.Warn("[LateJoin] Client: Invalid late join action packet");
                return;
            }
            
            NetworkLogger.Info($"[LateJoin] Client: Received late join decision - action={action}, message={message}, songTime={songTime:F2}s");
            
            _myLateJoinAction = action;
            _waitingForLateJoinDecision = false;
            
            // Store the message so it can be retrieved by UI even if events fire before subscription
            if (action == LateJoinAction.WaitForSongEnd)
            {
                _pendingLateJoinMessage = message;
            }
            
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                switch (action)
                {
                    case LateJoinAction.NormalJoin:
                    case LateJoinAction.JoinDifficultySelect:
                        // Continue with normal join flow
                        OnLateJoinReady?.Invoke(action, message);
                        break;
                        
                    case LateJoinAction.SpectateCurrentSong:
                        // Late joiner HAS the song - automatically navigate to spectate mode
                        NetworkLogger.Info("[LateJoin] Client: Has song, navigating to spectate mode");
                        YARG.Menu.Persistent.ToastManager.ToastInformation("Joining song as spectator...");
                        NavigateToSpectateMode(songTime);
                        break;
                        
                    case LateJoinAction.WaitForSongEnd:
                        // Late joiner DOESN'T have the song - show dialog with leave option
                        NetworkLogger.Info("[LateJoin] Client: Missing song, showing wait dialog");
                        OnLateJoinWaiting?.Invoke(message);
                        break;
                        
                    case LateJoinAction.AbortSetlist:
                        // Will be handled by SetlistAbort packet
                        break;
                        
                    case LateJoinAction.Rejected:
                        // Late join was rejected by the host (late join disabled)
                        NetworkLogger.Info($"[LateJoin] Client: Late join rejected - {message}");
                        YARG.Menu.Persistent.ToastManager.ToastWarning(message);
                        // Disconnect and return to menu - the host will disconnect us shortly anyway
                        // but we do it ourselves for a cleaner experience
                        LeaveLobby();
                        break;
                }
            });
        }
        
        /// <summary>
        /// Navigates to gameplay scene in spectate mode (late join with song).
        /// </summary>
        /// <param name="songTime">The song time to seek to for sync with the host.</param>
        private void NavigateToSpectateMode(double songTime)
        {
            // Set up for spectate mode - the player will join as a spectator
            GlobalVariables.State.IsSpectating = true;
            GlobalVariables.State.SpectateStartTime = songTime;
            GlobalVariables.State.IsPractice = false;
            
            NetworkLogger.Info($"[LateJoin] Client: Navigating to spectate mode, will sync to songTime={songTime:F2}s");
            
            // Load the current song if we have its hash
            if (!string.IsNullOrEmpty(_currentSongHash))
            {
                var songHash = YARG.Core.Song.HashWrapper.FromString(_currentSongHash);
                if (SongContainer.SongsByHash.TryGetValue(songHash, out var songList) && songList.Count > 0)
                {
                    GlobalVariables.State.CurrentSong = songList[0];
                    NetworkLogger.Info($"[LateJoin] Client: Set current song to '{songList[0].Name}'");
                }
                else
                {
                    NetworkLogger.Warn($"[LateJoin] Client: Could not find song with hash {_currentSongHash} in local library");
                }
            }
            
            // Navigate to gameplay scene
            OnStartGameplay?.Invoke();
            GlobalVariables.Instance.LoadScene(SceneIndex.Gameplay);
        }
        
        /// <summary>
        /// Client: Handles the setlist abort message.
        /// </summary>
        private void HandleSetlistAbortMessage(ReadOnlyMemory<byte> payload)
        {
            if (!LateJoinBinaryPackets.TryParseSetlistAbortPacket(payload.Span, out var reason))
            {
                NetworkLogger.Warn("[LateJoin] Client: Invalid setlist abort packet");
                return;
            }
            
            NetworkLogger.Info($"[LateJoin] Client: Setlist aborted - {reason}");
            
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                // Clear setlist and return to music library
                _setlistManager?.Clear();
                GlobalVariables.State.PlayingAShow = false;
                GlobalVariables.State.ShowSongs?.Clear();
                GlobalVariables.State.ShowIndex = 0;
                
                // Update session phase
                SetSessionPhase(SessionPhase.MusicLibrary);
                
                // Show toast notification
                YARG.Menu.Persistent.ToastManager.ToastWarning(reason);
                
                // Fire event for UI to handle
                OnSetlistAborted?.Invoke(reason);
            });
        }
        
        /// <summary>
        /// Host: Broadcasts setlist abort to all clients.
        /// </summary>
        private void BroadcastSetlistAbort(string reason)
        {
            if (!_isHosting)
                return;
            
            byte[] packet = LateJoinBinaryPackets.BuildSetlistAbortPacket(reason);
            
            foreach (var conn in _connectionMap.Values)
            {
                conn.Send(packet, ChannelType.ReliableOrdered);
            }
            
            // Also handle locally on host
            UnityMainThreadDispatcher.EnqueueAction(() =>
            {
                _setlistManager?.Clear();
                GlobalVariables.State.PlayingAShow = false;
                GlobalVariables.State.ShowSongs?.Clear();
                GlobalVariables.State.ShowIndex = 0;
                
                // Update session phase
                SetSessionPhase(SessionPhase.MusicLibrary);
                
                YARG.Menu.Persistent.ToastManager.ToastWarning(reason);
                OnSetlistAborted?.Invoke(reason);
            });
            
            NetworkLogger.Info($"[LateJoin] Host: Broadcast setlist abort to all clients");
        }
        
        // Late join events for UI
        public event Action<LateJoinAction, string> OnLateJoinReady;
        public event Action<string> OnLateJoinSpectating;
        public event Action<string> OnLateJoinWaiting;
        public event Action<string> OnSetlistAborted;
        
        #endregion
        
        #region Dedicated Server Helpers
        
        /// <summary>
        /// Gets the remote endpoint string (IP:Port) for a player connection.
        /// Used by dedicated server admin to track player IPs for banning.
        /// </summary>
        /// <param name="connectionId">The connection ID of the player.</param>
        /// <returns>The endpoint string, or null if not found.</returns>
        public string GetPlayerEndpoint(Guid connectionId)
        {
            if (_connectionMap.TryGetValue(connectionId, out var connection))
            {
                return connection.EndPoint;
            }
            return null;
        }
        
        /// <summary>
        /// Gets all player endpoints mapped by connection ID.
        /// Used by dedicated server admin for IP-based operations.
        /// </summary>
        /// <returns>Dictionary of connection ID to endpoint strings.</returns>
        public IReadOnlyDictionary<Guid, string> GetAllPlayerEndpoints()
        {
            var endpoints = new Dictionary<Guid, string>();
            foreach (var kvp in _connectionMap)
            {
                endpoints[kvp.Key] = kvp.Value.EndPoint;
            }
            return endpoints;
        }
        
        #endregion
    }
}

