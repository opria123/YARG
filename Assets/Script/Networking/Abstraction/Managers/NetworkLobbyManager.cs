using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using YARG.Multiplayer;
using YARG.Net;
using YARG.Net.Transport;
using YARG.Networking.Abstraction.Handlers;
using YARG.Networking.Settings;

namespace YARG.Networking.Abstraction.Managers
{
    /// <summary>
    /// Manages lobby lifecycle operations: create, join, leave, discovery.
    /// Consolidates lobby-related logic from LiteNetNetworkingAdapter.
    /// </summary>
    public sealed class NetworkLobbyManager : IDisposable
    {
        #region Fields

        private readonly LiteNetDiscovery _discovery;
        private readonly int _defaultPort;
        
        private LobbyInfo _currentLobby;
        private bool _isHosting;
        private bool _isConnected;
        private bool _isJoinInProgress;
        private bool _isDedicatedServer;
        private string _pendingPassword;
        private int _serverPort;
        
        // UPnP port mapping tracking
        private bool _upnpPortMapped;
        private int _upnpMappedPort;

        #endregion

        #region Events

        /// <summary>Fired when a lobby is created.</summary>
        public event Action<LobbyInfo> OnLobbyCreated;
        
        /// <summary>Fired when joined a lobby.</summary>
        public event Action<LobbyInfo> OnLobbyJoined;
        
        /// <summary>Fired when left a lobby.</summary>
        public event Action OnLobbyLeft;
        
        /// <summary>Fired when a lobby is discovered.</summary>
        public event Action<LobbyInfo> OnLobbyDiscovered;
        
        /// <summary>Fired when a lobby is lost/no longer available.</summary>
        public event Action<string> OnLobbyLost;
        
        /// <summary>Fired when a network error occurs.</summary>
        public event Action<string> OnNetworkError;

        #endregion

        #region Properties

        /// <summary>Current lobby information.</summary>
        public LobbyInfo CurrentLobby => _currentLobby;
        
        /// <summary>Whether we are hosting.</summary>
        public bool IsHosting => _isHosting;
        
        /// <summary>Whether we are connected to a remote lobby.</summary>
        public bool IsConnected => _isConnected;
        
        /// <summary>Whether a join operation is in progress.</summary>
        public bool IsJoinInProgress => _isJoinInProgress;
        
        /// <summary>Whether this is a dedicated server.</summary>
        public bool IsDedicatedServer => _isDedicatedServer;
        
        /// <summary>The discovery port.</summary>
        public int DiscoveryPort => _discovery?.DiscoveryPort ?? _defaultPort;
        
        /// <summary>The password for the pending join operation.</summary>
        public string PendingPassword => _pendingPassword;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new NetworkLobbyManager.
        /// </summary>
        public NetworkLobbyManager(LiteNetDiscovery discovery, int defaultPort)
        {
            _discovery = discovery;
            _defaultPort = defaultPort;
            _serverPort = defaultPort;
        }

        #endregion

        #region Configuration

        /// <summary>
        /// Sets dedicated server mode.
        /// </summary>
        public void SetDedicatedServerMode(bool isDedicated)
        {
            _isDedicatedServer = isDedicated;
            NetworkLogger.Info($"Dedicated server mode: {isDedicated}");
        }

        /// <summary>
        /// Sets the server port.
        /// </summary>
        public void SetServerPort(int port)
        {
            _serverPort = port;
            NetworkLogger.Info($"Server port set to: {port}");
        }

        /// <summary>
        /// Sets the discovery port.
        /// </summary>
        public void SetDiscoveryPort(int port)
        {
            _discovery?.SetDiscoveryPort(port);
        }

        /// <summary>
        /// Sets the pending password for joining.
        /// </summary>
        public void SetPendingPassword(string password)
        {
            _pendingPassword = password;
        }

        #endregion

        #region State Management

        /// <summary>
        /// Updates the hosting state.
        /// </summary>
        public void SetHostingState(bool isHosting)
        {
            _isHosting = isHosting;
            NetworkLogger.Info($"Hosting state: {isHosting}");
        }

        /// <summary>
        /// Updates the connected state.
        /// </summary>
        public void SetConnectedState(bool isConnected)
        {
            _isConnected = isConnected;
            NetworkLogger.Info($"Connected state: {isConnected}");
        }

        /// <summary>
        /// Updates the join-in-progress state.
        /// </summary>
        public void SetJoinInProgress(bool inProgress)
        {
            _isJoinInProgress = inProgress;
            NetworkLogger.Verbose($"Join in progress: {inProgress}");
        }

        /// <summary>
        /// Sets the current lobby information.
        /// </summary>
        public void SetCurrentLobby(LobbyInfo lobby)
        {
            _currentLobby = lobby;
        }

        /// <summary>
        /// Updates the current lobby's player count.
        /// </summary>
        public void UpdatePlayerCount(int delta)
        {
            if (_currentLobby != null)
            {
                _currentLobby.CurrentPlayers = Math.Max(1, _currentLobby.CurrentPlayers + delta);
                NetworkLogger.Verbose($"Player count: {_currentLobby.CurrentPlayers}");
            }
        }

        #endregion

        #region Lobby Creation

        /// <summary>
        /// Creates lobby information without starting the server.
        /// </summary>
        public LobbyInfo CreateLobbyInfo(string lobbyName, int maxPlayers, LobbyPrivacyMode privacyMode, 
            SessionType sessionType, string password)
        {
            var lobby = new LobbyInfo
            {
                LobbyName = lobbyName,
                MaxPlayers = maxPlayers,
                CurrentPlayers = 1, // Host
                PrivacyMode = privacyMode,
                SessionType = sessionType == SessionType.Lobby ? 1 : 0, // 1 = Lobby, 0 = Server
                HasPassword = !string.IsNullOrEmpty(password),
                Password = password // Store plain password - hash comparison happens elsewhere
            };
            
            // Generate lobby code for Lobby (automatic) mode
            if (sessionType == SessionType.Lobby)
            {
                lobby.LobbyCode = GenerateLobbyCode();
            }
            
            return lobby;
        }

        /// <summary>
        /// Fires the lobby created event.
        /// </summary>
        public void NotifyLobbyCreated(LobbyInfo lobby)
        {
            _currentLobby = lobby;
            _isHosting = true;
            OnLobbyCreated?.Invoke(lobby);
        }

        /// <summary>
        /// Fires the lobby joined event.
        /// </summary>
        public void NotifyLobbyJoined(LobbyInfo lobby)
        {
            _currentLobby = lobby;
            _isConnected = true;
            _isJoinInProgress = false;
            OnLobbyJoined?.Invoke(lobby);
        }

        /// <summary>
        /// Fires the network error event.
        /// </summary>
        public void NotifyNetworkError(string error)
        {
            _isJoinInProgress = false;
            OnNetworkError?.Invoke(error);
        }

        #endregion

        #region Lobby Leave

        /// <summary>
        /// Clears lobby state when leaving.
        /// </summary>
        public void ClearLobbyState()
        {
            _currentLobby = null;
            _isHosting = false;
            _isConnected = false;
            _isJoinInProgress = false;
            _pendingPassword = null;
            
            OnLobbyLeft?.Invoke();
            NetworkLogger.Info("Cleared lobby state");
        }

        /// <summary>
        /// Tracks UPnP port mapping for cleanup.
        /// </summary>
        public void SetUpnpMapping(int port)
        {
            _upnpPortMapped = true;
            _upnpMappedPort = port;
        }

        /// <summary>
        /// Gets whether UPnP mapping was used and the port.
        /// </summary>
        public (bool mapped, int port) GetUpnpMapping()
        {
            return (_upnpPortMapped, _upnpMappedPort);
        }

        /// <summary>
        /// Clears UPnP mapping tracking.
        /// </summary>
        public void ClearUpnpMapping()
        {
            _upnpPortMapped = false;
            _upnpMappedPort = 0;
        }

        #endregion

        #region Discovery

        /// <summary>
        /// Starts LAN discovery.
        /// </summary>
        public void StartDiscovery()
        {
            if (_discovery != null)
            {
                _discovery.OnLobbyDiscovered -= HandleLobbyDiscovered;
                _discovery.OnLobbyDiscovered += HandleLobbyDiscovered;
                _discovery.StartDiscovery();
                NetworkLogger.Info("Started discovery");
            }
        }

        /// <summary>
        /// Stops LAN discovery.
        /// </summary>
        public void StopDiscovery()
        {
            if (_discovery != null)
            {
                _discovery.OnLobbyDiscovered -= HandleLobbyDiscovered;
                _discovery.StopDiscovery();
                NetworkLogger.Info("Stopped discovery");
            }
        }

        /// <summary>
        /// Sends a discovery request to a specific address.
        /// </summary>
        public void SendDiscoveryRequest(string address, int port = 0)
        {
            _discovery?.SendDiscoveryRequest(address, port > 0 ? port : _defaultPort);
        }

        /// <summary>
        /// Sends a broadcast discovery request.
        /// </summary>
        public void SendBroadcastDiscoveryRequest(int port = 0)
        {
            _discovery?.SendBroadcastDiscoveryRequest(port > 0 ? port : _defaultPort);
        }

        private void HandleLobbyDiscovered(LobbyInfo lobby)
        {
            OnLobbyDiscovered?.Invoke(lobby);
        }

        #endregion

        #region Probe

        /// <summary>
        /// Probes a lobby to get information without joining.
        /// </summary>
        public async Task<LobbyInfo> ProbeLobby(string address, int port)
        {
            if (_discovery == null)
            {
                NetworkLogger.Warn("Cannot probe - discovery not initialized");
                return null;
            }
            
            try
            {
                // Send a probe request and wait for response
                var tcs = new TaskCompletionSource<LobbyInfo>();
                
                void OnProbeResponse(LobbyInfo lobby)
                {
                    if (lobby != null)
                    {
                        // Check if this lobby matches our probe address
                        if (lobby.IpAddress?.Contains(address) == true || 
                            lobby.PublicAddress?.Contains(address) == true)
                        {
                            tcs.TrySetResult(lobby);
                        }
                    }
                }
                
                _discovery.OnLobbyDiscovered += OnProbeResponse;
                _discovery.SendDiscoveryRequest(address, port);
                
                // Wait with timeout
                var timeoutTask = Task.Delay(5000);
                var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
                
                _discovery.OnLobbyDiscovered -= OnProbeResponse;
                
                if (completedTask == tcs.Task)
                {
                    return await tcs.Task;
                }
                
                return null; // Timeout
            }
            catch (Exception ex)
            {
                NetworkLogger.Warn($"Probe failed: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Helpers

        private static string GenerateLobbyCode()
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // Excluding confusing characters
            var random = new System.Random();
            var code = new char[6];
            for (int i = 0; i < code.Length; i++)
            {
                code[i] = chars[random.Next(chars.Length)];
            }
            return new string(code);
        }

        #endregion

        #region Cleanup

        public void Dispose()
        {
            StopDiscovery();
            ClearLobbyState();
        }

        #endregion
    }
}
