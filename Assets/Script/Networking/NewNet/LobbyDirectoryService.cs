using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using YARG.Core.Logging;
using YARG.Net.Directory;

namespace YARG.Networking.NewNet
{
    /// <summary>
    /// Unity wrapper around <see cref="LobbyDirectoryClient"/> that manages
    /// polling and exposes lobby listings to UI menus.
    /// </summary>
    public sealed class LobbyDirectoryService : IDisposable
    {
        private static LobbyDirectoryService? _instance;
        private static readonly object InstanceGate = new();

        private readonly object _gate = new();
        private LobbyDirectoryClient? _client;
        private bool _disposed;

        private LobbyDirectoryService()
        {
        }

        /// <summary>
        /// Singleton instance of the lobby directory service.
        /// </summary>
        public static LobbyDirectoryService Instance
        {
            get
            {
                lock (InstanceGate)
                {
                    return _instance ??= new LobbyDirectoryService();
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
        /// Raised when the lobby list changes.
        /// </summary>
        public event EventHandler<LobbyDirectoryChangedEventArgs>? LobbiesChanged;

        /// <summary>
        /// Returns the current list of lobbies.
        /// </summary>
        public IReadOnlyList<LobbyDirectoryEntry> Lobbies => _client?.Lobbies ?? Array.Empty<LobbyDirectoryEntry>();

        /// <summary>
        /// Indicates whether the service is currently polling.
        /// </summary>
        public bool IsPolling { get; private set; }

        /// <summary>
        /// Initializes the client with the given introducer URI.
        /// </summary>
        /// <param name="directoryUri">Base URI for the introducer's lobby list endpoint.</param>
        /// <param name="lobbyTtl">Optional lobby TTL for filtering stale entries.</param>
        public void Initialize(Uri directoryUri, TimeSpan? lobbyTtl = null)
        {
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(LobbyDirectoryService));
                if (_client is not null)
                {
                    YargLogger.LogWarning("[LobbyDirectoryService] Already initialized.");
                    return;
                }

                _client = new LobbyDirectoryClient(directoryUri, lobbyTtl);
                _client.LobbiesChanged += HandleLobbiesChanged;
                YargLogger.LogInfo($"[LobbyDirectoryService] Initialized with introducer URI: {directoryUri}");
            }
        }

        /// <summary>
        /// Starts periodic polling for lobby updates.
        /// </summary>
        /// <param name="interval">Polling interval.</param>
        public void StartPolling(TimeSpan? interval = null)
        {
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(LobbyDirectoryService));
                if (_client is null)
                {
                    YargLogger.LogWarning("[LobbyDirectoryService] Not initialized. Call Initialize() first.");
                    return;
                }

                if (IsPolling) return;

                var pollInterval = interval ?? TimeSpan.FromSeconds(5);
                _client.StartPolling(pollInterval);
                IsPolling = true;
                YargLogger.LogInfo($"[LobbyDirectoryService] Started polling every {pollInterval.TotalSeconds:F1}s.");
            }
        }

        /// <summary>
        /// Stops periodic polling.
        /// </summary>
        public void StopPolling()
        {
            lock (_gate)
            {
                if (_client is null || !IsPolling) return;

                _client.StopPolling();
                IsPolling = false;
                YargLogger.LogInfo("[LobbyDirectoryService] Stopped polling.");
            }
        }

        /// <summary>
        /// Performs an immediate refresh of the lobby list.
        /// </summary>
        public async Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            LobbyDirectoryClient? client;
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(LobbyDirectoryService));
                client = _client;
            }

            if (client is null)
            {
                YargLogger.LogWarning("[LobbyDirectoryService] Not initialized. Call Initialize() first.");
                return;
            }

            await client.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;

                if (_client is not null)
                {
                    _client.LobbiesChanged -= HandleLobbiesChanged;
                    _client.Dispose();
                    _client = null;
                }

                IsPolling = false;
            }

            lock (InstanceGate)
            {
                if (_instance == this)
                {
                    _instance = null;
                }
            }
        }

        private void HandleLobbiesChanged(object? sender, LobbyDirectoryChangedEventArgs e)
        {
            // Forward to subscribers on main thread if needed, or just invoke directly
            LobbiesChanged?.Invoke(this, e);
        }
    }
}
