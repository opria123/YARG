using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using YARG.Multiplayer;
using YARG.Net;
using YARG.Net.Packets;
using YARG.Net.Runtime;
using YARG.Net.Sessions;
using YARG.Net.Transport;
using YARG.Net.Utilities;

namespace YARG.Networking.Abstraction.Handlers
{
    /// <summary>
    /// Handles lobby lifecycle management (create, join, leave, discovery).
    /// </summary>
    public sealed class LobbyHandler : IDisposable
    {
        private readonly LiteNetDiscovery _discovery;
        private readonly int _defaultPort;
        private readonly Func<string, bool, bool, NetworkPlayerData> _createPlayerData;
        
        private LobbyStateManager _lobbyStateManager;
        private SessionManager _sessionManager;
        private PublicEndpointResolver _publicEndpointResolver;
        
        private LobbyInfo _currentLobby;
        private bool _isHosting;
        private bool _isDedicatedServer;
        private int _maxPlayers;
        private string _lobbyPassword;
        private string _pendingPassword;
        
        /// <summary>
        /// Fired when a lobby is successfully created.
        /// </summary>
        public event Action<LobbyInfo> OnLobbyCreated;
        
        /// <summary>
        /// Fired when successfully joined a lobby.
        /// </summary>
        public event Action<LobbyInfo> OnLobbyJoined;
        
        /// <summary>
        /// Fired when lobby is left.
        /// </summary>
        public event Action OnLobbyLeft;
        
        /// <summary>
        /// Fired when a network error occurs.
        /// </summary>
        public event Action<string> OnNetworkError;
        
        /// <summary>
        /// Fired when lobbies are discovered.
        /// </summary>
        public event Action<List<LobbyInfo>> OnLobbyListUpdated;
        
        /// <summary>
        /// Fired when public endpoint is resolved via STUN.
        /// </summary>
        public event Action<string, int> OnPublicEndpointResolved;

        #region Properties

        public LobbyInfo CurrentLobby => _currentLobby;
        public bool IsHosting => _isHosting;
        public bool IsDedicatedServer => _isDedicatedServer;
        public int MaxPlayers => _maxPlayers;
        public string LobbyPassword => _lobbyPassword;
        public string PendingPassword => _pendingPassword;
        public int DiscoveryPort => _discovery?.DiscoveryPort ?? _defaultPort;

        #endregion

        public LobbyHandler(
            LiteNetDiscovery discovery, 
            int defaultPort,
            Func<string, bool, bool, NetworkPlayerData> createPlayerData)
        {
            _discovery = discovery;
            _defaultPort = defaultPort;
            _createPlayerData = createPlayerData;
            _sessionManager = new SessionManager();
            
            // Subscribe to discovery events
            if (_discovery != null)
            {
                _discovery.OnLobbyDiscovered += HandleLobbyDiscovered;
                _discovery.OnLobbyLost += HandleLobbyLost;
            }
        }

        #region Configuration

        public void SetDedicatedServerMode(bool isDedicated)
        {
            _isDedicatedServer = isDedicated;
            NetworkLogger.Info("Dedicated server mode: {0}", isDedicated);
        }

        public void SetMaxPlayers(int maxPlayers)
        {
            _maxPlayers = maxPlayers;
        }

        public void SetLobbyPassword(string password)
        {
            _lobbyPassword = password;
            if (_currentLobby != null)
            {
                _currentLobby.HasPassword = !string.IsNullOrEmpty(password);
                _currentLobby.Password = password;
            }
            NetworkLogger.Verbose("Lobby password set (hasPassword={0})", !string.IsNullOrEmpty(password));
        }

        public void SetJoinPassword(string password)
        {
            _pendingPassword = password;
        }

        #endregion

        #region Create Lobby

        /// <summary>
        /// Creates lobby info and state manager for hosting.
        /// Returns the created LobbyInfo - caller is responsible for server stack creation.
        /// </summary>
        public LobbyInfo CreateLobbyInfo(string lobbyName, int maxPlayers, LobbyPrivacyMode privacyMode, 
            string password, string hostPlayerName)
        {
            NetworkLogger.Info("Creating lobby: {0} (max: {1}, privacy: {2}, dedicated: {3})", 
                lobbyName, maxPlayers, privacyMode, _isDedicatedServer);

            try
            {
                _maxPlayers = maxPlayers;
                
                // Create lobby configuration
                var config = new LobbyConfiguration
                {
                    MaxPlayers = maxPlayers
                };

                // Create lobby state manager
                _lobbyStateManager = new LobbyStateManager(_sessionManager, config);

                // Get local LAN address for sidebar display
                string lanAddress = NetworkAddressUtility.GetLocalLanAddress() ?? "Unknown";
                NetworkLogger.Verbose("Detected LAN address: {0}", lanAddress);

                // Create lobby info
                _currentLobby = new LobbyInfo
                {
                    LobbyId = _lobbyStateManager.LobbyId.ToString(),
                    LobbyName = lobbyName,
                    HostName = hostPlayerName,
                    CurrentPlayers = _isDedicatedServer ? 0 : 1, // Dedicated server doesn't count as a player
                    MaxPlayers = maxPlayers,
                    PrivacyMode = privacyMode,
                    HasPassword = !string.IsNullOrEmpty(password),
                    Password = password,
                    IsActive = true,
                    IpAddress = lanAddress,
                    Port = _defaultPort,
                    PublicPort = _defaultPort,
                    PublicAddress = string.Empty,
                    TransportId = "LiteNetLib",
                    PlayerNames = _isDedicatedServer ? Array.Empty<string>() : new[] { hostPlayerName },
                    PlayerInstruments = new int[0]
                };

                _isHosting = true;
                _lobbyPassword = password;

                // Start advertising for discovery
                _discovery?.StartAdvertising(_currentLobby);
                NetworkLogger.Info("Started advertising lobby for discovery (privacy: {0})", privacyMode);

                // Start STUN resolution
                StartPublicEndpointResolution();

                NetworkLogger.Info("Lobby created successfully: {0} (dedicated: {1})", 
                    _currentLobby.LobbyId, _isDedicatedServer);
                    
                return _currentLobby;
            }
            catch (Exception ex)
            {
                NetworkLogger.Error("Failed to create lobby: {0}", ex.Message);
                OnNetworkError?.Invoke($"Failed to create lobby: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Fires the lobby created events after successful server start.
        /// </summary>
        public void NotifyLobbyCreated()
        {
            OnLobbyCreated?.Invoke(_currentLobby);
            
            // Host also joins their own lobby (triggers UI navigation) - not for dedicated servers
            if (!_isDedicatedServer)
            {
                OnLobbyJoined?.Invoke(_currentLobby);
            }
        }

        #endregion

        #region Join Lobby

        /// <summary>
        /// Parses an endpoint string and creates temporary lobby info for joining.
        /// Returns the lobby info - caller is responsible for client stack creation.
        /// </summary>
        public LobbyInfo PrepareJoinLobby(string endpoint, string password, out string address, out int port)
        {
            NetworkLogger.Info("Joining lobby at: {0}", endpoint);

            // Parse endpoint (format: "IP:Port")
            var parts = endpoint.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[1], out port))
            {
                throw new ArgumentException($"Invalid endpoint format: {endpoint}. Expected IP:Port");
            }

            address = parts[0];
            _pendingPassword = password;

            // Try to find lobby info from discovered lobbies first
            LobbyInfo discoveredLobby = FindDiscoveredLobby(address, port);

            // Use discovered lobby info or create temporary lobby info
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
                NetworkLogger.Verbose("No discovered lobby found for {0}, using temporary info", endpoint);
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

            return _currentLobby;
        }

        private LobbyInfo FindDiscoveredLobby(string address, int port)
        {
            if (_discovery == null)
            {
                NetworkLogger.Verbose("Discovery is null, cannot look up discovered lobbies");
                return null;
            }

            var discoveredLobbies = _discovery.DiscoveredLobbies;
            NetworkLogger.Verbose("Looking for {0}:{1} in {2} discovered lobbies", address, port, discoveredLobbies.Count);
            
            foreach (var lobby in discoveredLobbies.Values)
            {
                NetworkLogger.Verbose("Checking lobby: {0} at {1}:{2} (public: {3}:{4})",
                    lobby.LobbyName, lobby.IpAddress, lobby.Port, lobby.PublicAddress, lobby.PublicPort);
                    
                if ((lobby.IpAddress == address || lobby.PublicAddress == address) && lobby.Port == port)
                {
                    NetworkLogger.Verbose("Found discovered lobby: {0}", lobby.LobbyName);
                    return lobby;
                }
            }
            
            return null;
        }

        /// <summary>
        /// Fires the lobby joined event after successful connection.
        /// </summary>
        public void NotifyLobbyJoined()
        {
            OnLobbyJoined?.Invoke(_currentLobby);
        }

        /// <summary>
        /// Join a discovered lobby directly.
        /// </summary>
        public string GetEndpointForLobby(LobbyInfo lobby)
        {
            NetworkLogger.Info("Joining discovered lobby: {0}", lobby.LobbyName);
            return $"{lobby.IpAddress}:{lobby.Port}";
        }

        #endregion

        #region Leave Lobby

        public void LeaveLobby()
        {
            NetworkLogger.Info("Leaving lobby");

            try
            {
                // Cancel any pending STUN resolution
                CancelPublicEndpointResolution();
                
                // Stop advertising
                _discovery?.StopAdvertising();
                
                _currentLobby = null;
                _isHosting = false;
                _lobbyPassword = null;
                _pendingPassword = null;

                OnLobbyLeft?.Invoke();
                NetworkLogger.Info("Lobby left successfully");
            }
            catch (Exception ex)
            {
                NetworkLogger.Error("Error leaving lobby: {0}", ex.Message);
                OnNetworkError?.Invoke($"Error leaving lobby: {ex.Message}");
            }
        }

        #endregion

        #region Discovery

        public void StartDiscovery()
        {
            NetworkLogger.Info("Starting discovery");
            _discovery?.StartDiscovery();
        }

        public void StopDiscovery()
        {
            NetworkLogger.Info("Stopping discovery");
            _discovery?.StopDiscovery();
        }

        public void SendDiscoveryRequest(string address, int port = 0)
        {
            int targetPort = port > 0 ? port : _defaultPort;
            NetworkLogger.Verbose("Sending discovery request to {0}:{1}", address, targetPort);
            _discovery?.SendDiscoveryRequest(address, targetPort);
        }

        public void SendBroadcastDiscoveryRequest(int port = 0)
        {
            int targetPort = port > 0 ? port : _defaultPort;
            NetworkLogger.Verbose("Sending broadcast discovery request on port {0}", targetPort);
            _discovery?.SendBroadcastDiscoveryRequest(targetPort);
        }

        public void SetDiscoveryPort(int port)
        {
            NetworkLogger.Verbose("Setting discovery port to {0}", port);
            _discovery?.SetDiscoveryPort(port);
        }

        private void HandleLobbyDiscovered(LobbyInfo lobby)
        {
            NetworkLogger.Info("Discovered lobby: {0} at {1}:{2}", lobby.LobbyName, lobby.IpAddress, lobby.Port);
        }

        private void HandleLobbyLost(string lobbyId)
        {
            NetworkLogger.Info("Lost lobby: {0}", lobbyId);
        }

        /// <summary>
        /// Probe a lobby to see if it's reachable.
        /// </summary>
        public async Task<LobbyInfo> ProbeLobby(string address, int port)
        {
            NetworkLogger.Verbose("Probing lobby at {0}:{1}", address, port);

            try
            {
                if (_discovery == null)
                {
                    NetworkLogger.Warn("Discovery not initialized");
                    return null;
                }
                
                var tcs = new TaskCompletionSource<LobbyInfo>();
                LobbyInfo discoveredLobby = null;
                
                void OnDiscovered(LobbyInfo lobby)
                {
                    if (lobby.IpAddress == address && lobby.Port == port)
                    {
                        discoveredLobby = lobby;
                        tcs.TrySetResult(lobby);
                    }
                }
                
                _discovery.OnLobbyDiscovered += OnDiscovered;
                
                try
                {
                    _discovery.StartDiscovery();
                    _discovery.SendDiscoveryRequest(address, port);
                    
                    using var cts = new CancellationTokenSource(3000);
                    var timeoutTask = Task.Delay(-1, cts.Token);
                    
                    var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
                    
                    if (completedTask == tcs.Task)
                    {
                        NetworkLogger.Verbose("Probe succeeded for {0}:{1}", address, port);
                        return discoveredLobby;
                    }
                    else
                    {
                        NetworkLogger.Verbose("Probe timed out for {0}:{1}", address, port);
                        return null;
                    }
                }
                finally
                {
                    _discovery.OnLobbyDiscovered -= OnDiscovered;
                }
            }
            catch (Exception ex)
            {
                NetworkLogger.Error("Failed to probe lobby: {0}", ex.Message);
                return null;
            }
        }

        #endregion

        #region Public Endpoint (STUN)

        private void StartPublicEndpointResolution()
        {
            if (_currentLobby == null || !_isHosting) return;
            
            if (_publicEndpointResolver == null)
            {
                _publicEndpointResolver = new PublicEndpointResolver();
                _publicEndpointResolver.EndpointResolved += HandlePublicEndpointResolved;
                _publicEndpointResolver.ResolutionFailed += HandlePublicEndpointFailed;
            }
            
            NetworkLogger.Info("Starting STUN resolution for public IP...");
            _ = _publicEndpointResolver.ResolveAsync(_currentLobby.Port);
        }

        private void CancelPublicEndpointResolution()
        {
            _publicEndpointResolver?.Cancel();
        }

        private void HandlePublicEndpointResolved(object sender, PublicEndpointResolvedEventArgs e)
        {
            ApplyPublicEndpoint(e.Address, e.Port);
        }

        private void HandlePublicEndpointFailed(object sender, PublicEndpointFailedEventArgs e)
        {
            NetworkLogger.Warn("STUN resolution failed: {0}", e.Reason);
        }

        private void ApplyPublicEndpoint(string address, int port)
        {
            if (_currentLobby == null) return;
            
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
                NetworkLogger.Info("Resolved public endpoint via STUN: {0}:{1}", address, port);
                
                // Update discovery advertising with new public address
                _discovery?.StartAdvertising(_currentLobby);
                
                OnPublicEndpointResolved?.Invoke(address, port);
            }
        }

        #endregion

        #region Player Names Update

        /// <summary>
        /// Updates the PlayerNames array in the current lobby.
        /// </summary>
        public void UpdateLobbyPlayerNames(IEnumerable<string> playerNames)
        {
            if (_currentLobby == null) return;
            
            var names = new List<string>(playerNames);
            _currentLobby.PlayerNames = names.ToArray();
            NetworkLogger.Verbose("Updated lobby PlayerNames: [{0}]", string.Join(", ", names));
        }

        #endregion

        public void Dispose()
        {
            CancelPublicEndpointResolution();
            
            if (_publicEndpointResolver != null)
            {
                _publicEndpointResolver.EndpointResolved -= HandlePublicEndpointResolved;
                _publicEndpointResolver.ResolutionFailed -= HandlePublicEndpointFailed;
                _publicEndpointResolver.Dispose();
                _publicEndpointResolver = null;
            }
            
            if (_discovery != null)
            {
                _discovery.OnLobbyDiscovered -= HandleLobbyDiscovered;
                _discovery.OnLobbyLost -= HandleLobbyLost;
            }
        }
    }
}
