using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using YARG.Core.Logging;
using YARG.Net.Directory;
using YARG.Net.Packets;

namespace YARG.Networking.NewNet
{
    /// <summary>
    /// Unity service that advertises the local server to the introducer.
    /// </summary>
    public sealed class LobbyAdvertiserService : IDisposable
    {
        private static LobbyAdvertiserService? _instance;
        private static readonly object InstanceGate = new();

        private readonly object _gate = new();
        private LobbyAdvertiser? _advertiser;
        private Guid _lobbyId;
        private bool _disposed;

        private LobbyAdvertiserService()
        {
        }

        /// <summary>
        /// Singleton instance.
        /// </summary>
        public static LobbyAdvertiserService Instance
        {
            get
            {
                lock (InstanceGate)
                {
                    return _instance ??= new LobbyAdvertiserService();
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
        /// Whether the service is currently advertising.
        /// </summary>
        public bool IsAdvertising => _advertiser?.IsAdvertising ?? false;

        /// <summary>
        /// The lobby ID being advertised.
        /// </summary>
        public Guid LobbyId => _lobbyId;

        /// <summary>
        /// Starts advertising the lobby to the introducer.
        /// </summary>
        /// <param name="introducerUri">Base URI of the introducer service.</param>
        /// <param name="options">Advertisement options.</param>
        /// <param name="heartbeatInterval">Interval between heartbeats.</param>
        public void StartAdvertising(Uri introducerUri, AdvertisementOptions options, TimeSpan? heartbeatInterval = null)
        {
            if (introducerUri is null)
            {
                throw new ArgumentNullException(nameof(introducerUri));
            }

            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(LobbyAdvertiserService));
                }

                if (_advertiser is not null)
                {
                    YargLogger.LogWarning("[LobbyAdvertiserService] Already advertising. Stop first.");
                    return;
                }

                _lobbyId = options.LobbyId != Guid.Empty ? options.LobbyId : Guid.NewGuid();
                _advertiser = new LobbyAdvertiser(introducerUri);

                var advertisement = BuildAdvertisement(options, _lobbyId);
                var interval = heartbeatInterval ?? TimeSpan.FromSeconds(10);

                _advertiser.StartAdvertising(advertisement, interval);
                YargLogger.LogInfo($"[LobbyAdvertiserService] Started advertising lobby '{options.LobbyName}' (ID: {_lobbyId})");
            }
        }

        /// <summary>
        /// Updates the advertisement with new player count or other info.
        /// </summary>
        public void UpdateAdvertisement(AdvertisementOptions options)
        {
            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(LobbyAdvertiserService));
                }

                if (_advertiser is null)
                {
                    YargLogger.LogWarning("[LobbyAdvertiserService] Not advertising. Nothing to update.");
                    return;
                }

                var advertisement = BuildAdvertisement(options, _lobbyId);
                _advertiser.UpdateAdvertisement(advertisement);
            }
        }

        /// <summary>
        /// Stops advertising and removes the lobby from the directory.
        /// </summary>
        public async Task StopAdvertisingAsync(CancellationToken cancellationToken = default)
        {
            LobbyAdvertiser? advertiser;

            lock (_gate)
            {
                advertiser = _advertiser;
                _advertiser = null;
                _lobbyId = Guid.Empty;
            }

            if (advertiser is not null)
            {
                try
                {
                    await advertiser.StopAdvertisingAsync(cancellationToken);
                    YargLogger.LogInfo("[LobbyAdvertiserService] Stopped advertising.");
                }
                finally
                {
                    advertiser.Dispose();
                }
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            LobbyAdvertiser? advertiser;
            lock (_gate)
            {
                advertiser = _advertiser;
                _advertiser = null;
                _lobbyId = Guid.Empty;
            }

            advertiser?.Dispose();

            lock (InstanceGate)
            {
                if (_instance == this)
                {
                    _instance = null;
                }
            }
        }

        private static LobbyAdvertisementRequest BuildAdvertisement(AdvertisementOptions options, Guid lobbyId)
        {
            return new LobbyAdvertisementRequest(
                LobbyId: lobbyId,
                LobbyName: options.LobbyName ?? "Unnamed Lobby",
                HostName: options.HostName ?? "Host",
                Address: options.Address ?? "0.0.0.0",
                Port: options.Port > 0 ? options.Port : 7777,
                CurrentPlayers: options.CurrentPlayers,
                MaxPlayers: options.MaxPlayers > 0 ? options.MaxPlayers : 8,
                HasPassword: options.HasPassword,
                Version: options.Version ?? ProtocolVersion.Current);
        }
    }

    /// <summary>
    /// Options for advertising a lobby.
    /// </summary>
    public sealed class AdvertisementOptions
    {
        /// <summary>
        /// The lobby ID. If empty, a new one will be generated.
        /// </summary>
        public Guid LobbyId { get; set; }

        /// <summary>
        /// Display name for the lobby.
        /// </summary>
        public string? LobbyName { get; set; }

        /// <summary>
        /// The host's display name.
        /// </summary>
        public string? HostName { get; set; }

        /// <summary>
        /// The public address for clients to connect to.
        /// </summary>
        public string? Address { get; set; }

        /// <summary>
        /// The port the server is listening on.
        /// </summary>
        public int Port { get; set; }

        /// <summary>
        /// Current number of players in the lobby.
        /// </summary>
        public int CurrentPlayers { get; set; }

        /// <summary>
        /// Maximum players allowed.
        /// </summary>
        public int MaxPlayers { get; set; }

        /// <summary>
        /// Whether the lobby requires a password.
        /// </summary>
        public bool HasPassword { get; set; }

        /// <summary>
        /// The game/protocol version.
        /// </summary>
        public string? Version { get; set; }
    }
}
