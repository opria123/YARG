using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using YARG.Net.Directory;

namespace YARG.Networking.Abstraction
{
    /// <summary>
    /// Discovery protocol for LiteNet that can run on the same port as the game server.
    /// Uses unconnected UDP messages for discovery, similar to how Mirror's discovery works.
    /// 
    /// This class uses DiscoveryProtocol from YARG.Net for packet building/parsing,
    /// and integrates with LiteNetLib for the transport layer.
    /// </summary>
    public class LiteNetDiscovery : IDisposable, INetEventListener
    {
        // Discovery client for sending requests (uses LiteNetLib's NetManager)
        private NetManager _discoveryNetManager;
        private Thread _pollThread;
        private volatile bool _isRunning;
        
        // Server-side state
        private LobbyInfo _advertisedLobby;
        private bool _isAdvertising;
        
        // Client-side state - uses DiscoveryManager from YARG.Net
        private readonly DiscoveryManager _discoveryManager = new();
        private readonly object _lobbiesLock = new();
        
        // Configuration
        private int _discoveryPort = 7777; // Default to same as game port
        
        /// <summary>
        /// Event fired when a lobby is discovered.
        /// </summary>
        public event Action<LobbyInfo> OnLobbyDiscovered;
        
        /// <summary>
        /// Event fired when a lobby times out and is considered lost.
        /// </summary>
        public event Action<string> OnLobbyLost;
        
        /// <summary>
        /// Get the current discovery port.
        /// </summary>
        public int DiscoveryPort => _discoveryPort;
        
        /// <summary>
        /// Get all currently discovered lobbies.
        /// </summary>
        public IReadOnlyDictionary<string, LobbyInfo> DiscoveredLobbies
        {
            get
            {
                lock (_lobbiesLock)
                {
                    var result = new Dictionary<string, LobbyInfo>();
                    foreach (var kvp in _discoveryManager.Lobbies)
                    {
                        result[kvp.Key] = ConvertToLobbyInfo(kvp.Value);
                    }
                    return result;
                }
            }
        }

        public LiteNetDiscovery()
        {
            // Wire up events from DiscoveryManager
            _discoveryManager.LobbyDiscovered += info =>
            {
                var lobbyInfo = ConvertToLobbyInfo(info);
                UnityMainThreadDispatcher.EnqueueAction(() => OnLobbyDiscovered?.Invoke(lobbyInfo));
            };
            
            _discoveryManager.LobbyLost += lobbyId =>
            {
                UnityMainThreadDispatcher.EnqueueAction(() => OnLobbyLost?.Invoke(lobbyId));
            };
        }
        
        /// <summary>
        /// Configure the discovery port.
        /// </summary>
        public void SetDiscoveryPort(int port)
        {
            if (port > 0 && port <= 65535)
            {
                _discoveryPort = port;
            }
        }
        
        #region Server-Side (Advertising)
        
        /// <summary>
        /// Start advertising a lobby for discovery.
        /// </summary>
        public void StartAdvertising(LobbyInfo lobby)
        {
            _advertisedLobby = lobby;
            _isAdvertising = true;
            Debug.Log($"[LiteNetDiscovery] Started advertising lobby: {lobby.LobbyName} on port {lobby.Port}");
        }
        
        /// <summary>
        /// Stop advertising the lobby.
        /// </summary>
        public void StopAdvertising()
        {
            _advertisedLobby = null;
            _isAdvertising = false;
            Debug.Log("[LiteNetDiscovery] Stopped advertising lobby");
        }
        
        /// <summary>
        /// Handle an unconnected message that might be a discovery request.
        /// Call this from the LiteNetLib transport's OnNetworkReceiveUnconnected.
        /// Returns true if the message was handled as a discovery packet.
        /// </summary>
        public bool HandleUnconnectedMessage(IPEndPoint remoteEndPoint, NetPacketReader reader, NetManager netManager)
        {
            if (!_isAdvertising || _advertisedLobby == null)
            {
                return false;
            }
            
            if (reader.AvailableBytes < DiscoveryProtocol.MIN_PACKET_SIZE)
            {
                return false;
            }
            
            try
            {
                // Peek at the data to check if it's a discovery request
                var data = new byte[reader.AvailableBytes];
                Buffer.BlockCopy(reader.RawData, reader.Position, data, 0, reader.AvailableBytes);
                
                if (!DiscoveryProtocol.IsRequest(data))
                {
                    return false;
                }
                
                // This is a discovery request, send a response
                Debug.Log($"[LiteNetDiscovery] Received discovery request from {remoteEndPoint}");
                SendDiscoveryResponse(netManager, remoteEndPoint);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LiteNetDiscovery] Error handling unconnected message: {ex.Message}");
                return false;
            }
        }
        
        private void SendDiscoveryResponse(NetManager netManager, IPEndPoint remoteEndPoint)
        {
            if (_advertisedLobby == null)
            {
                return;
            }
            
            try
            {
                // Convert LobbyInfo to DiscoveredLobbyInfo for the builder
                var lobbyInfo = new DiscoveredLobbyInfo
                {
                    LobbyId = _advertisedLobby.LobbyId ?? "",
                    LobbyName = _advertisedLobby.LobbyName ?? "",
                    HostName = _advertisedLobby.HostName ?? "",
                    CurrentPlayers = _advertisedLobby.CurrentPlayers,
                    MaxPlayers = _advertisedLobby.MaxPlayers,
                    HasPassword = _advertisedLobby.HasPassword,
                    PrivacyMode = (LobbyPrivacy)(int)_advertisedLobby.PrivacyMode,
                    Port = _advertisedLobby.Port,
                    PublicPort = _advertisedLobby.PublicPort,
                    PublicAddress = _advertisedLobby.PublicAddress ?? "",
                    TransportId = _advertisedLobby.TransportId ?? "LiteNetLib",
                    PlayerNames = _advertisedLobby.PlayerNames ?? Array.Empty<string>(),
                    PlayerInstruments = _advertisedLobby.PlayerInstruments ?? Array.Empty<int>()
                };
                
                // Use DiscoveryResponseBuilder from YARG.Net
                var responseBytes = new DiscoveryResponseBuilder()
                    .WithLobbyInfo(lobbyInfo)
                    .Build();
                
                // Send via LiteNetLib
                var writer = new NetDataWriter();
                writer.Put(responseBytes);
                netManager.SendUnconnectedMessage(writer, remoteEndPoint);
                
                Debug.Log($"[LiteNetDiscovery] Sent discovery response to {remoteEndPoint}: {_advertisedLobby.LobbyName}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiteNetDiscovery] Failed to send discovery response: {ex.Message}");
            }
        }
        
        #endregion
        
        #region Client-Side (Discovery)
        
        /// <summary>
        /// Start listening for discovery responses using LiteNetLib's NetManager.
        /// This ensures proper packet formatting for unconnected messages.
        /// </summary>
        public void StartDiscovery()
        {
            if (_discoveryNetManager != null)
            {
                Debug.Log("[LiteNetDiscovery] Discovery client already exists, stopping first");
                StopDiscovery();
            }
            
            try
            {
                // Create a NetManager for discovery - this ensures proper LiteNetLib packet headers
                _discoveryNetManager = new NetManager(this)
                {
                    UnconnectedMessagesEnabled = true,
                    AutoRecycle = true
                };
                
                // Start on any available port (0)
                if (!_discoveryNetManager.Start(0))
                {
                    Debug.LogError("[LiteNetDiscovery] Failed to start discovery NetManager");
                    _discoveryNetManager = null;
                    return;
                }
                
                _isRunning = true;
                
                // Start polling thread for receiving responses
                _pollThread = new Thread(PollLoop)
                {
                    Name = "LiteNetDiscovery-Poll",
                    IsBackground = true
                };
                _pollThread.Start();
                
                Debug.Log($"[LiteNetDiscovery] Started discovery client on port {_discoveryNetManager.LocalPort} using NetManager");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiteNetDiscovery] Failed to start discovery: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Polling loop for the discovery NetManager.
        /// </summary>
        private void PollLoop()
        {
            Debug.Log("[LiteNetDiscovery] Poll loop started");
            
            while (_isRunning && _discoveryNetManager != null)
            {
                try
                {
                    _discoveryNetManager.PollEvents();
                    Thread.Sleep(15); // ~60 polls per second
                }
                catch (Exception ex)
                {
                    if (_isRunning)
                    {
                        Debug.LogWarning($"[LiteNetDiscovery] Poll error: {ex.Message}");
                    }
                }
            }
            
            Debug.Log("[LiteNetDiscovery] Poll loop ended");
        }
        
        /// <summary>
        /// Stop listening for discovery responses.
        /// </summary>
        public void StopDiscovery()
        {
            _isRunning = false;
            
            _discoveryNetManager?.Stop();
            _discoveryNetManager = null;
            
            _pollThread?.Join(500); // Wait up to 500ms for thread to exit
            _pollThread = null;
            
            Debug.Log("[LiteNetDiscovery] Stopped discovery client");
        }
        
        /// <summary>
        /// Clear all discovered lobbies.
        /// </summary>
        public void ClearDiscoveredLobbies()
        {
            lock (_lobbiesLock)
            {
                _discoveryManager.Clear();
            }
            Debug.Log("[LiteNetDiscovery] Cleared discovered lobbies");
        }
        
        /// <summary>
        /// Check if the discovery listener is still running and restart if needed.
        /// </summary>
        private void EnsureDiscoveryClientRunning()
        {
            if (_discoveryNetManager == null || !_isRunning)
            {
                Debug.Log("[LiteNetDiscovery] Discovery client not running, restarting...");
                StartDiscovery();
            }
        }
        
        /// <summary>
        /// Send a discovery request to a specific address and port using LiteNetLib's NetManager.
        /// This ensures the packet has proper LiteNetLib headers so the server can receive it.
        /// </summary>
        public void SendDiscoveryRequest(string address, int port = 0)
        {
            // Auto-restart discovery client if it died
            EnsureDiscoveryClientRunning();
            
            if (_discoveryNetManager == null)
            {
                Debug.LogWarning("[LiteNetDiscovery] Discovery client not started");
                return;
            }
            
            int targetPort = port > 0 ? port : _discoveryPort;
            
            try
            {
                IPAddress ipAddress;
                if (!IPAddress.TryParse(address, out ipAddress))
                {
                    var addresses = Dns.GetHostAddresses(address);
                    ipAddress = Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork);
                    if (ipAddress == null && addresses.Length > 0)
                    {
                        ipAddress = addresses[0];
                    }
                    
                    if (ipAddress == null)
                    {
                        Debug.LogWarning($"[LiteNetDiscovery] Could not resolve address: {address}");
                        return;
                    }
                }
                
                // Use DiscoveryProtocol from YARG.Net to build the request
                var requestBytes = DiscoveryProtocol.BuildRequestPacket();
                var writer = new NetDataWriter();
                writer.Put(requestBytes);
                
                var endpoint = new IPEndPoint(ipAddress, targetPort);
                
                // Use SendUnconnectedMessage - this adds the proper LiteNetLib packet header
                bool sent = _discoveryNetManager.SendUnconnectedMessage(writer, endpoint);
                
                if (sent)
                {
                    Debug.Log($"[LiteNetDiscovery] Sent discovery request to {endpoint} via NetManager");
                }
                else
                {
                    Debug.LogWarning($"[LiteNetDiscovery] Failed to send discovery request to {endpoint}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LiteNetDiscovery] Failed to send discovery request to {address}:{targetPort}: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Send a broadcast discovery request on the local network.
        /// </summary>
        public void SendBroadcastDiscoveryRequest(int port = 0)
        {
            EnsureDiscoveryClientRunning();
            
            if (_discoveryNetManager == null)
            {
                Debug.LogWarning("[LiteNetDiscovery] Discovery client not started");
                return;
            }
            
            int targetPort = port > 0 ? port : _discoveryPort;
            
            try
            {
                // Use DiscoveryProtocol from YARG.Net to build the request
                var requestBytes = DiscoveryProtocol.BuildRequestPacket();
                var writer = new NetDataWriter();
                writer.Put(requestBytes);
                
                // Use broadcast with proper LiteNetLib headers
                bool sent = _discoveryNetManager.SendBroadcast(writer, targetPort);
                
                if (sent)
                {
                    Debug.Log($"[LiteNetDiscovery] Sent broadcast discovery request on port {targetPort}");
                }
                else
                {
                    Debug.LogWarning($"[LiteNetDiscovery] Failed to send broadcast discovery request");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LiteNetDiscovery] Failed to send broadcast discovery request: {ex.Message}");
            }
        }
        
        #region INetEventListener Implementation (for discovery client)
        
        public void OnPeerConnected(NetPeer peer)
        {
            // Discovery client doesn't establish connections
        }
        
        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            // Discovery client doesn't establish connections
        }
        
        public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            // Ignore ICMP port unreachable errors - these are expected when host isn't running
            if (socketError == SocketError.ConnectionReset)
            {
                Debug.Log($"[LiteNetDiscovery] Connection reset from {endPoint} (host not running)");
                return;
            }
            
            Debug.LogWarning($"[LiteNetDiscovery] Network error from {endPoint}: {socketError}");
        }
        
        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            // Discovery client doesn't receive connected messages
        }
        
        public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
            // This is called when we receive a discovery response!
            Debug.Log($"[LiteNetDiscovery] OnNetworkReceiveUnconnected from {remoteEndPoint}, type={messageType}, bytes={reader.AvailableBytes}");
            ProcessDiscoveryResponse(reader, remoteEndPoint);
        }
        
        public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
        {
            // Discovery client doesn't track latency
        }
        
        public void OnConnectionRequest(ConnectionRequest request)
        {
            // Discovery client doesn't accept connections
            request.Reject();
        }
        
        #endregion
        
        private void ProcessDiscoveryResponse(NetPacketReader reader, IPEndPoint remoteEndPoint)
        {
            Debug.Log($"[LiteNetDiscovery] Processing response from {remoteEndPoint}, bytes available: {reader.AvailableBytes}");
            
            if (reader.AvailableBytes < DiscoveryProtocol.MIN_PACKET_SIZE)
            {
                Debug.Log($"[LiteNetDiscovery] Data too short ({reader.AvailableBytes} bytes), ignoring");
                return;
            }
            
            try
            {
                // Copy data for parsing
                var data = new byte[reader.AvailableBytes];
                Buffer.BlockCopy(reader.RawData, reader.Position, data, 0, reader.AvailableBytes);
                
                // Validate it's a response using DiscoveryProtocol
                if (!DiscoveryProtocol.IsResponse(data))
                {
                    Debug.Log($"[LiteNetDiscovery] Not a valid discovery response");
                    return;
                }
                
                Debug.Log("[LiteNetDiscovery] Valid discovery response, parsing lobby info...");
                
                // Use DiscoveryResponseParser from YARG.Net
                var parser = new DiscoveryResponseParser(data);
                if (!parser.TryParseLobbyInfo(out var lobbyInfo))
                {
                    Debug.LogWarning("[LiteNetDiscovery] Failed to parse lobby info from response");
                    return;
                }
                
                // Set the IP address from the endpoint
                lobbyInfo.IpAddress = remoteEndPoint.Address.ToString();
                
                Debug.Log($"[LiteNetDiscovery] Parsed lobby: {lobbyInfo.LobbyName} ({lobbyInfo.CurrentPlayers}/{lobbyInfo.MaxPlayers} players)");
                
                // Store/update using DiscoveryManager
                lock (_lobbiesLock)
                {
                    bool isNew = _discoveryManager.AddOrUpdate(lobbyInfo);
                    
                    if (isNew)
                    {
                        Debug.Log($"[LiteNetDiscovery] Discovered new lobby: {lobbyInfo.LobbyName} at {remoteEndPoint}");
                    }
                    else
                    {
                        Debug.Log($"[LiteNetDiscovery] Updated existing lobby: {lobbyInfo.LobbyName}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LiteNetDiscovery] Error processing discovery response: {ex.Message}\n{ex.StackTrace}");
            }
        }
        
        /// <summary>
        /// Clean up lobbies that haven't responded recently.
        /// Call this periodically (e.g., every 5 seconds).
        /// </summary>
        public void CleanupOldLobbies(TimeSpan timeout)
        {
            lock (_lobbiesLock)
            {
                _discoveryManager.CleanupOldLobbies(timeout);
            }
        }
        
        #endregion

        #region Helpers

        /// <summary>
        /// Convert DiscoveredLobbyInfo (from YARG.Net) to LobbyInfo (Unity-side).
        /// </summary>
        private static LobbyInfo ConvertToLobbyInfo(DiscoveredLobbyInfo info)
        {
            return new LobbyInfo
            {
                LobbyId = info.LobbyId,
                LobbyName = info.LobbyName,
                HostName = info.HostName,
                CurrentPlayers = info.CurrentPlayers,
                MaxPlayers = info.MaxPlayers,
                HasPassword = info.HasPassword,
                PrivacyMode = (LobbyPrivacyMode)(int)info.PrivacyMode,
                Port = info.Port,
                PublicPort = info.PublicPort,
                PublicAddress = info.PublicAddress,
                TransportId = info.TransportId,
                IpAddress = info.IpAddress,
                IsActive = info.IsActive,
                PlayerNames = info.PlayerNames,
                PlayerInstruments = info.PlayerInstruments
            };
        }

        #endregion
        
        public void Dispose()
        {
            StopAdvertising();
            StopDiscovery();
        }
    }
    
    /// <summary>
    /// Simple dispatcher to run actions on the Unity main thread.
    /// Uses a static queue that can be accessed from any thread, and processes on Update.
    /// </summary>
    public class UnityMainThreadDispatcher : MonoBehaviour
    {
        private static UnityMainThreadDispatcher _instance;
        private static readonly object _initLock = new();
        private static readonly Queue<Action> _pendingActions = new();
        private static bool _isQuitting;
        
        /// <summary>
        /// Initialize the dispatcher on the main thread. Call this from a MonoBehaviour's Awake or Start.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize()
        {
            if (_instance != null) return;
            
            lock (_initLock)
            {
                if (_instance == null && !_isQuitting)
                {
                    var go = new GameObject("UnityMainThreadDispatcher");
                    _instance = go.AddComponent<UnityMainThreadDispatcher>();
                    DontDestroyOnLoad(go);
                }
            }
        }
        
        public static UnityMainThreadDispatcher Instance
        {
            get
            {
                // Don't create on demand - rely on RuntimeInitializeOnLoadMethod
                return _instance;
            }
        }
        
        /// <summary>
        /// Enqueue an action to run on the main thread. Safe to call from any thread.
        /// </summary>
        public static void EnqueueAction(Action action)
        {
            if (action == null) return;
            
            lock (_pendingActions)
            {
                _pendingActions.Enqueue(action);
            }
        }
        
        /// <summary>
        /// Legacy method for compatibility.
        /// </summary>
        public void Enqueue(Action action)
        {
            EnqueueAction(action);
        }
        
        private void Update()
        {
            // Process all pending actions
            lock (_pendingActions)
            {
                while (_pendingActions.Count > 0)
                {
                    try
                    {
                        _pendingActions.Dequeue()?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[UnityMainThreadDispatcher] Error executing action: {ex.Message}");
                    }
                }
            }
        }
        
        private void OnApplicationQuit()
        {
            _isQuitting = true;
        }
        
        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
        }
    }
}
