using System;
using System.Threading;
using System.Threading.Tasks;
using YARG.Core.Logging;
using YARG.Net.Handlers;
using YARG.Net.Handlers.Server;
using YARG.Net.Packets;
using YARG.Net.Packets.Dispatch;
using YARG.Net.Runtime;
using YARG.Net.Serialization;
using YARG.Net.Sessions;
using YARG.Net.Transport;

namespace YARG.Networking.NewNet
{
    /// <summary>
    /// Unity wrapper for hosting a LiteNetLib server.
    /// </summary>
    public sealed class ServerNetworkingService : IDisposable
    {
        private static ServerNetworkingService? _instance;
        private static readonly object InstanceGate = new();

        private readonly object _gate = new();

        private LiteNetLibTransport? _transport;
        private IServerRuntime? _runtime;
        private SessionManager? _sessionManager;
        private LobbyStateManager? _lobbyManager;
        private ServerLobbyCoordinator? _lobbyCoordinator;
        private PacketDispatcher? _dispatcher;
        private ServerHandshakeHandler? _handshakeHandler;
        private ServerLobbyCommandHandler? _lobbyCommandHandler;
        private ServerGameplayHandler? _gameplayHandler;
        private INetSerializer? _serializer;

        private CancellationTokenSource? _shutdownCts;
        private bool _isRunning;
        private bool _disposed;

        private string _lobbyName = string.Empty;
        private string _hostName = string.Empty;
        private int _maxPlayers = 8;
        private string? _password;
        private int _port = 7777;
        private Uri? _introducerUri;
        private string? _publicAddress;

        private ServerNetworkingService()
        {
        }

        /// <summary>
        /// Singleton instance.
        /// </summary>
        public static ServerNetworkingService Instance
        {
            get
            {
                lock (InstanceGate)
                {
                    return _instance ??= new ServerNetworkingService();
                }
            }
        }

        /// <summary>
        /// True if the singleton has been created.
        /// </summary>
        public static bool HasInstance
        {
            get
            {
                lock (InstanceGate)
                {
                    return _instance is not null;
                }
            }
        }

        /// <summary>
        /// Raised when a player joins the lobby.
        /// </summary>
        public event EventHandler<LobbyPlayerChangedEventArgs>? PlayerJoined;

        /// <summary>
        /// Raised when a player leaves the lobby.
        /// </summary>
        public event EventHandler<LobbyPlayerChangedEventArgs>? PlayerLeft;

        /// <summary>
        /// Raised when the lobby status changes.
        /// </summary>
        public event EventHandler<LobbyStatusChangedEventArgs>? StatusChanged;

        /// <summary>
        /// Raised when the server is stopped.
        /// </summary>
        public event EventHandler? ServerStopped;

        /// <summary>
        /// Whether the server is currently running.
        /// </summary>
        public bool IsRunning
        {
            get
            {
                lock (_gate)
                {
                    return _isRunning;
                }
            }
        }

        /// <summary>
        /// The lobby state manager for the hosted lobby.
        /// </summary>
        public LobbyStateManager? LobbyManager => _lobbyManager;

        /// <summary>
        /// The gameplay handler for broadcasting gameplay-related packets.
        /// </summary>
        public ServerGameplayHandler? GameplayHandler => _gameplayHandler;

        /// <summary>
        /// The current lobby name.
        /// </summary>
        public string LobbyName => _lobbyName;

        /// <summary>
        /// The host's display name.
        /// </summary>
        public string HostName => _hostName;

        /// <summary>
        /// Maximum players allowed.
        /// </summary>
        public int MaxPlayers => _maxPlayers;

        /// <summary>
        /// The port the server is listening on.
        /// </summary>
        public int Port => _port;

        /// <summary>
        /// Whether the lobby has a password set.
        /// </summary>
        public bool HasPassword => !string.IsNullOrEmpty(_password);

        /// <summary>
        /// Starts the server with the given lobby configuration.
        /// </summary>
        public async Task StartAsync(ServerHostOptions options)
        {
            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(ServerNetworkingService));
                }

                if (_isRunning)
                {
                    throw new InvalidOperationException("Server is already running.");
                }
            }

            _lobbyName = options.LobbyName ?? "Unnamed Lobby";
            _hostName = options.HostName ?? "Host";
            _maxPlayers = Math.Clamp(options.MaxPlayers, 1, 64);
            _password = options.Password;
            _port = options.Port > 0 ? options.Port : 7777;
            _introducerUri = options.IntroducerUri;
            _publicAddress = options.PublicAddress;

            try
            {
                _shutdownCts = new CancellationTokenSource();
                _transport = new LiteNetLibTransport();
                _serializer = new NewtonsoftNetSerializer();
                _sessionManager = new SessionManager(_maxPlayers);
                _lobbyManager = new LobbyStateManager(_sessionManager, new LobbyConfiguration { MaxPlayers = _maxPlayers });
                _lobbyCoordinator = new ServerLobbyCoordinator(_sessionManager, _lobbyManager, _serializer);
                _dispatcher = new PacketDispatcher(_serializer);

                _lobbyCommandHandler = new ServerLobbyCommandHandler(_sessionManager, _lobbyManager);
                _lobbyCommandHandler.Register(_dispatcher);

                _gameplayHandler = new ServerGameplayHandler(_sessionManager, _lobbyManager, _serializer);
                _gameplayHandler.Register(_dispatcher);

                _handshakeHandler = new ServerHandshakeHandler(
                    _sessionManager,
                    _serializer,
                    new HandshakeServerOptions
                    {
                        Password = _password,
                    });

                _handshakeHandler.Register(_dispatcher);
                _handshakeHandler.HandshakeAccepted += HandleHandshakeAccepted;
                _handshakeHandler.HandshakeRejected += HandleHandshakeRejected;

                _lobbyManager.PlayerJoined += HandlePlayerJoined;
                _lobbyManager.PlayerLeft += HandlePlayerLeft;
                _lobbyManager.StatusChanged += HandleStatusChanged;

                _transport.OnPeerDisconnected += HandlePeerDisconnected;

                _runtime = new DefaultServerRuntime();
                _runtime.Configure(new ServerRuntimeOptions
                {
                    Transport = _transport,
                    Port = _port,
                    EnableNatPunchThrough = options.EnableNatPunchThrough,
                    PacketDispatcher = _dispatcher,
                });

                await _runtime.StartAsync(_shutdownCts.Token);

                lock (_gate)
                {
                    _isRunning = true;
                }

                // Start advertising to introducer if configured
                if (_introducerUri is not null)
                {
                    StartAdvertising();
                }

                YargLogger.LogInfo($"[ServerNetworkingService] Started server '{_lobbyName}' on port {_port}.");
            }
            catch (Exception ex)
            {
                YargLogger.LogError($"[ServerNetworkingService] Failed to start server: {ex.Message}");
                Cleanup();
                throw;
            }
        }

        /// <summary>
        /// Stops the server.
        /// </summary>
        public async Task StopAsync()
        {
            IServerRuntime? runtime;
            CancellationTokenSource? shutdownCts;

            lock (_gate)
            {
                if (!_isRunning)
                {
                    return;
                }

                runtime = _runtime;
                shutdownCts = _shutdownCts;
                _isRunning = false;
            }

            shutdownCts?.Cancel();

            // Stop advertising first
            if (LobbyAdvertiserService.HasInstance)
            {
                try
                {
                    await LobbyAdvertiserService.Instance.StopAdvertisingAsync();
                }
                catch (Exception ex)
                {
                    YargLogger.LogWarning($"[ServerNetworkingService] Error stopping advertiser: {ex.Message}");
                }
            }

            if (runtime is not null)
            {
                try
                {
                    await runtime.StopAsync();
                }
                catch (Exception ex)
                {
                    YargLogger.LogWarning($"[ServerNetworkingService] Error during shutdown: {ex.Message}");
                }
            }

            Cleanup();
            ServerStopped?.Invoke(this, EventArgs.Empty);
            YargLogger.LogInfo("[ServerNetworkingService] Server stopped.");
        }

        /// <summary>
        /// Gets a snapshot of the current lobby state.
        /// </summary>
        public LobbyStateSnapshot? GetLobbySnapshot()
        {
            return _lobbyManager?.BuildSnapshot();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _ = StopAsync();

            lock (InstanceGate)
            {
                if (_instance == this)
                {
                    _instance = null;
                }
            }
        }

        private void Cleanup()
        {
            if (_handshakeHandler is not null)
            {
                _handshakeHandler.HandshakeAccepted -= HandleHandshakeAccepted;
                _handshakeHandler.HandshakeRejected -= HandleHandshakeRejected;
            }

            if (_lobbyManager is not null)
            {
                _lobbyManager.PlayerJoined -= HandlePlayerJoined;
                _lobbyManager.PlayerLeft -= HandlePlayerLeft;
                _lobbyManager.StatusChanged -= HandleStatusChanged;
            }

            if (_transport is not null)
            {
                _transport.OnPeerDisconnected -= HandlePeerDisconnected;
            }

            _lobbyCoordinator?.Dispose();
            _transport?.Dispose();
            _shutdownCts?.Dispose();

            _runtime = null;
            _transport = null;
            _sessionManager = null;
            _lobbyManager = null;
            _lobbyCoordinator = null;
            _dispatcher = null;
            _handshakeHandler = null;
            _lobbyCommandHandler = null;
            _serializer = null;
            _shutdownCts = null;
        }

        private void HandleHandshakeAccepted(object? sender, SessionRecord session)
        {
            YargLogger.LogInfo($"[ServerNetworkingService] Player '{session.PlayerName}' joined ({session.Connection.EndPoint}).");
            _lobbyCoordinator?.HandleHandshakeAccepted(session);
        }

        private void HandleHandshakeRejected(object? sender, HandshakeRejectedEventArgs e)
        {
            YargLogger.LogWarning($"[ServerNetworkingService] Handshake rejected from {e.Context.Connection.EndPoint}: {e.Reason}");
        }

        private void HandlePeerDisconnected(INetConnection connection)
        {
            YargLogger.LogInfo($"[ServerNetworkingService] Peer disconnected: {connection.EndPoint}.");
            _lobbyCoordinator?.HandlePeerDisconnected(connection.Id);
        }

        private void HandlePlayerJoined(object? sender, LobbyPlayerChangedEventArgs e)
        {
            PlayerJoined?.Invoke(this, e);
            UpdateAdvertisement();
        }

        private void HandlePlayerLeft(object? sender, LobbyPlayerChangedEventArgs e)
        {
            PlayerLeft?.Invoke(this, e);
            UpdateAdvertisement();
        }

        private void HandleStatusChanged(object? sender, LobbyStatusChangedEventArgs e)
        {
            StatusChanged?.Invoke(this, e);
        }

        private void StartAdvertising()
        {
            if (_introducerUri is null)
            {
                return;
            }

            try
            {
                var options = new AdvertisementOptions
                {
                    LobbyName = _lobbyName,
                    HostName = _hostName,
                    Address = _publicAddress,
                    Port = _port,
                    CurrentPlayers = _lobbyManager?.PlayerCount ?? 1,
                    MaxPlayers = _maxPlayers,
                    HasPassword = HasPassword,
                    Version = ProtocolVersion.Current,
                };

                LobbyAdvertiserService.Instance.StartAdvertising(_introducerUri, options);
            }
            catch (Exception ex)
            {
                YargLogger.LogWarning($"[ServerNetworkingService] Failed to start advertising: {ex.Message}");
            }
        }

        private void UpdateAdvertisement()
        {
            if (!LobbyAdvertiserService.HasInstance || !LobbyAdvertiserService.Instance.IsAdvertising)
            {
                return;
            }

            try
            {
                var options = new AdvertisementOptions
                {
                    LobbyId = LobbyAdvertiserService.Instance.LobbyId,
                    LobbyName = _lobbyName,
                    HostName = _hostName,
                    Address = _publicAddress,
                    Port = _port,
                    CurrentPlayers = _lobbyManager?.PlayerCount ?? 1,
                    MaxPlayers = _maxPlayers,
                    HasPassword = HasPassword,
                    Version = ProtocolVersion.Current,
                };

                LobbyAdvertiserService.Instance.UpdateAdvertisement(options);
            }
            catch (Exception ex)
            {
                YargLogger.LogWarning($"[ServerNetworkingService] Failed to update advertisement: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Options for starting a hosted server.
    /// </summary>
    public sealed class ServerHostOptions
    {
        /// <summary>
        /// The display name for the lobby.
        /// </summary>
        public string? LobbyName { get; set; }

        /// <summary>
        /// The host's display name.
        /// </summary>
        public string? HostName { get; set; }

        /// <summary>
        /// Maximum number of players (1-64).
        /// </summary>
        public int MaxPlayers { get; set; } = 8;

        /// <summary>
        /// Optional password for the lobby.
        /// </summary>
        public string? Password { get; set; }

        /// <summary>
        /// Port to listen on (default 7777).
        /// </summary>
        public int Port { get; set; } = 7777;

        /// <summary>
        /// Whether to enable NAT punch-through.
        /// </summary>
        public bool EnableNatPunchThrough { get; set; }

        /// <summary>
        /// If set, advertises the lobby to the introducer at this URI.
        /// </summary>
        public Uri? IntroducerUri { get; set; }

        /// <summary>
        /// The public address to advertise to other players.
        /// If null, the introducer will use the source IP.
        /// </summary>
        public string? PublicAddress { get; set; }
    }
}
