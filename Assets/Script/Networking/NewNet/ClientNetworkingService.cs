using System;
using System.Threading;
using System.Threading.Tasks;
using YARG.Core.Logging;
using YARG.Net.Handlers.Client;
using YARG.Net.Packets;
using YARG.Net.Runtime;
using YARG.Net.Serialization;
using YARG.Net.Sessions;
using YARG.Net.Transport;

namespace YARG.Networking.NewNet
{
    /// <summary>
    /// Thin Unity-facing wrapper around the YARG.Net client bootstrapper.
    /// </summary>
    public sealed class ClientNetworkingService : IDisposable
    {
        private static ClientNetworkingService? _instance;
        private readonly object _stateGate = new();

        private ClientNetworkingClient? _client;
        private LiteNetLibTransport? _transport;
        private INetConnection? _activeConnection;
        private PendingHandshakeInfo? _pendingHandshake;
        private bool _initialized;
        private bool _disposed;

        private ClientNetworkingService()
        {
        }

        public static ClientNetworkingService Instance => _instance ??= new ClientNetworkingService();
        public static bool HasInstance => _instance is not null;

        public event EventHandler<ClientConnectedEventArgs>? Connected;
        public event EventHandler<ClientDisconnectedEventArgs>? Disconnected;
        public event EventHandler<ClientHandshakeCompletedEventArgs>? HandshakeCompleted;
        public event EventHandler<ClientLobbyStateChangedEventArgs>? LobbyStateChanged;
        public event EventHandler<CountdownReceivedEventArgs>? CountdownReceived;
        public event EventHandler<ClientGameplayEndEventArgs>? GameplayEnded;

        public bool IsInitialized => _initialized;
        public bool IsConnected => _activeConnection is not null;
        public ClientSessionContext? SessionContext => _client?.SessionContext;
        public ClientLobbyStateHandler? LobbyHandler => _client?.LobbyStateHandler;
        public ClientCountdownHandler? CountdownHandler => _client?.CountdownHandler;
        public ClientGameplayHandler? GameplayHandler => _client?.GameplayHandler;
        public ClientLobbyCommandSender? CommandSender => _client?.CommandSender;
        public INetConnection? ActiveConnection => _activeConnection;

        public void Initialize(TimeSpan? pollInterval = null)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ClientNetworkingService));
            }

            if (_initialized)
            {
                return;
            }

            _transport = new LiteNetLibTransport();
            INetSerializer serializer = new NewtonsoftNetSerializer();
            _client = ClientNetworkingBootstrapper.Initialize(_transport, serializer, pollInterval);
            _client.Runtime.Connected += HandleRuntimeConnected;
            _client.Runtime.Disconnected += HandleRuntimeDisconnected;
            _client.Runtime.HandshakeCompleted += HandleRuntimeHandshakeCompleted;
            _client.LobbyStateHandler.LobbyStateChanged += HandleLobbyStateChanged;
            _client.CountdownHandler.CountdownReceived += HandleCountdownReceived;
            _client.GameplayHandler.GameplayEnded += HandleGameplayEnded;

            _initialized = true;
            YargLogger.LogInfo("[ClientNetworkingService] Initialized LiteNetLib client runtime.");
        }

        public async Task ConnectAsync(ClientConnectionParameters parameters, CancellationToken cancellationToken = default)
        {
            EnsureInitialized();

            if (string.IsNullOrWhiteSpace(parameters.Address))
            {
                throw new ArgumentException("Address must be provided.", nameof(parameters));
            }

            if (parameters.Port <= 0 || parameters.Port > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(parameters), "Port must be between 1 and 65535.");
            }

            if (string.IsNullOrWhiteSpace(parameters.PlayerName))
            {
                throw new ArgumentException("Player name must be provided.", nameof(parameters));
            }

            var handshake = new PendingHandshakeInfo(
                ResolveClientVersion(parameters.ClientVersion),
                parameters.PlayerName.Trim(),
                string.IsNullOrWhiteSpace(parameters.Password) ? null : parameters.Password);

            lock (_stateGate)
            {
                _pendingHandshake = handshake;
            }

            await _client!.Runtime.ConnectAsync(parameters.Address.Trim(), parameters.Port, cancellationToken).ConfigureAwait(false);
        }

        public Task DisconnectAsync(string? reason = null, CancellationToken cancellationToken = default)
        {
            if (!_initialized || _client is null)
            {
                return Task.CompletedTask;
            }

            return _client.Runtime.DisconnectAsync(reason, cancellationToken);
        }

        public void SendReadyState(bool isReady)
        {
            EnsureInitialized();

            var connection = RequireConnection();
            var sessionContext = _client!.SessionContext ?? throw new InvalidOperationException("Session context has not been registered.");
            _client.CommandSender.SendReadyState(connection, sessionContext, isReady);
        }

        public void SendSongSelection(SongSelectionState selection)
        {
            if (selection is null)
            {
                throw new ArgumentNullException(nameof(selection));
            }

            EnsureInitialized();

            var connection = RequireConnection();
            var sessionContext = _client!.SessionContext ?? throw new InvalidOperationException("Session context has not been registered.");
            _client.CommandSender.SendSongSelection(connection, sessionContext, selection);
        }

        public bool TryGetLobbySnapshot(out LobbyStateSnapshot? snapshot)
        {
            snapshot = null;
            if (_client is null)
            {
                return false;
            }

            return _client.LobbyStateHandler.TryGetSnapshot(out snapshot);
        }

        /// <summary>
        /// Gets the latest lobby snapshot, or null if none is available.
        /// </summary>
        public LobbyStateSnapshot? LatestSnapshot
        {
            get
            {
                TryGetLobbySnapshot(out var snapshot);
                return snapshot;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_client is not null)
            {
                _client.Runtime.Connected -= HandleRuntimeConnected;
                _client.Runtime.Disconnected -= HandleRuntimeDisconnected;
                _client.Runtime.HandshakeCompleted -= HandleRuntimeHandshakeCompleted;
                _client.LobbyStateHandler.LobbyStateChanged -= HandleLobbyStateChanged;
                _client.CountdownHandler.CountdownReceived -= HandleCountdownReceived;
                _client.GameplayHandler.GameplayEnded -= HandleGameplayEnded;
            }

            _transport?.Dispose();
            _transport = null;
            _client = null;
            _activeConnection = null;
            _pendingHandshake = null;
            _initialized = false;
        }

        private void HandleRuntimeConnected(object? sender, ClientConnectedEventArgs e)
        {
            lock (_stateGate)
            {
                _activeConnection = e.Connection;
            }

            SendPendingHandshake();
            Connected?.Invoke(this, e);
        }

        private void HandleRuntimeDisconnected(object? sender, ClientDisconnectedEventArgs e)
        {
            lock (_stateGate)
            {
                _activeConnection = null;
                _pendingHandshake = null;
            }

            Disconnected?.Invoke(this, e);
        }

        private void HandleRuntimeHandshakeCompleted(object? sender, ClientHandshakeCompletedEventArgs e)
        {
            if (!e.Accepted && !string.IsNullOrEmpty(e.Reason))
            {
                YargLogger.LogWarning($"[ClientNetworkingService] Handshake rejected: {e.Reason}");
            }

            HandshakeCompleted?.Invoke(this, e);
        }

        private void HandleLobbyStateChanged(object? sender, ClientLobbyStateChangedEventArgs e)
        {
            LobbyStateChanged?.Invoke(this, e);
        }

        private void HandleCountdownReceived(object? sender, CountdownReceivedEventArgs e)
        {
            CountdownReceived?.Invoke(this, e);
        }

        private void HandleGameplayEnded(object? sender, ClientGameplayEndEventArgs e)
        {
            GameplayEnded?.Invoke(this, e);
        }

        private void SendPendingHandshake()
        {
            if (_client is null)
            {
                return;
            }

            PendingHandshakeInfo? pending;
            INetConnection? connection;

            lock (_stateGate)
            {
                pending = _pendingHandshake;
                connection = _activeConnection;
                _pendingHandshake = null;
            }

            if (!pending.HasValue || connection is null)
            {
                return;
            }

            try
            {
                _client.HandshakeSender.SendHandshake(connection, pending.Value.Version, pending.Value.PlayerName, pending.Value.Password);
            }
            catch (Exception ex)
            {
                YargLogger.LogError($"[ClientNetworkingService] Failed to send handshake: {ex.Message}");
                throw;
            }
        }

        private void EnsureInitialized()
        {
            if (!_initialized)
            {
                Initialize();
            }
        }

        private INetConnection RequireConnection()
        {
            var connection = _activeConnection;
            if (connection is null)
            {
                throw new InvalidOperationException("Client is not currently connected.");
            }

            return connection;
        }

        private static string ResolveClientVersion(string? requestedVersion)
        {
            if (!string.IsNullOrWhiteSpace(requestedVersion))
            {
                return requestedVersion.Trim();
            }

            return ProtocolVersion.Current;
        }

        private readonly struct PendingHandshakeInfo
        {
            public string Version { get; }
            public string PlayerName { get; }
            public string? Password { get; }

            public PendingHandshakeInfo(string version, string playerName, string? password)
            {
                Version = version;
                PlayerName = playerName;
                Password = password;
            }
        }
    }

    /// <summary>
    /// Parameters required to initiate a LiteNetLib client connection.
    /// </summary>
    public readonly struct ClientConnectionParameters
    {
        public string Address { get; }
        public int Port { get; }
        public string PlayerName { get; }
        public string? Password { get; }
        public string? ClientVersion { get; }

        public ClientConnectionParameters(
            string address,
            int port,
            string playerName,
            string? password = null,
            string? clientVersion = null)
        {
            Address = address;
            Port = port;
            PlayerName = playerName;
            Password = password;
            ClientVersion = clientVersion;
        }
    }
}
