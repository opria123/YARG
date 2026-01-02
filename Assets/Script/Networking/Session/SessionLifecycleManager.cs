using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using Cysharp.Threading.Tasks;
using LiteNetLib;
using UnityEngine;
using YARG.Core.Logging;
using YARG.Networking.Settings;
using YARG.Networking.UPnP;
using YARG.Net.Directory;
using YARG.Net.LobbyServer;
using YARG.Net.Utilities;

namespace YARG.Networking.Session
{
    /// <summary>
    /// Manages session lifecycle based on session type (Server vs Lobby).
    /// Handles UPnP, lobby codes, and Lobby server registration automatically.
    /// </summary>
    public sealed class SessionLifecycleManager : MonoBehaviour
    {
        public static SessionLifecycleManager Instance { get; private set; }

        private UPnPPortForwarder? _upnpForwarder;
        private LobbyCodeClient? _lobbyCodeClient;
        private NatPunchClient? _natPunchClient;
        private SessionPreset? _currentPreset;
        private string? _currentLobbyCode;
        private Guid _currentLobbyId;
        private CancellationTokenSource? _sessionCts;
        private CancellationTokenSource? _heartbeatCts;
        private bool _isSessionActive;
        
        // NAT punch endpoint info (for clients)
        private IPEndPoint? _punchedEndpoint;
        
        // NAT punch server info (for hosts) - needs refreshing in heartbeat
        private string? _natPunchLobbyServerUrl;
        private int _natPunchGamePort;
        
        /// <summary>
        /// Gets the punched endpoint if NAT punch was successful.
        /// Use this address to connect instead of the registered address.
        /// </summary>
        public IPEndPoint? PunchedEndpoint => _punchedEndpoint;
        
        // Heartbeat configuration - must be less than lobby server TTL (30s)
        private const float HeartbeatIntervalSeconds = 15f;
        
        // Cached lobby info for heartbeats
        private string? _cachedLobbyName;
        private string? _cachedHostName;
        private string? _cachedHostAddress;
        private int _cachedHostPort;
        private int _cachedMaxPlayers;
        private bool _cachedHasPassword;
        
        /// <summary>
        /// List of lobby server URLs that have the current lobby code registered.
        /// Used for cleanup to release the code from All lobby servers.
        /// </summary>
        private readonly List<string> _registeredLobbyServerUrls = new();

        /// <summary>
        /// Gets the current session type.
        /// </summary>
        public SessionType CurrentSessionType => _currentPreset?.SessionType ?? SessionType.Server;

        /// <summary>
        /// Gets whether this is a Lobby session (uses UPnP + codes).
        /// </summary>
        public bool IsLobby => CurrentSessionType == SessionType.Lobby;

        /// <summary>
        /// Gets whether this is a Server session (manual port forward).
        /// </summary>
        public bool IsServer => CurrentSessionType == SessionType.Server;

        /// <summary>
        /// Gets the current lobby code (Lobby sessions only).
        /// </summary>
        public string? LobbyCode => _currentLobbyCode;
        
        /// <summary>
        /// Gets whether UPnP port forwarding is active.
        /// </summary>
        public bool IsUPnPActive => _upnpForwarder?.HasMappedPort ?? false;

        /// <summary>
        /// Gets whether a session is currently active.
        /// </summary>
        public bool IsSessionActive => _isSessionActive;

        /// <summary>
        /// Gets the current session preset.
        /// </summary>
        public SessionPreset? CurrentPreset => _currentPreset;

        /// <summary>
        /// Updates a specific setting in the current preset.
        /// Used when host changes settings in the lobby room.
        /// </summary>
        public void UpdatePresetSetting(Action<SessionPreset> updateAction)
        {
            if (_currentPreset != null)
            {
                updateAction(_currentPreset);
                YargLogger.LogInfo($"[SessionManager] Updated preset setting. allowLateJoin={_currentPreset.allowLateJoin}");
            }
        }

        /// <summary>
        /// Fired when session state changes.
        /// </summary>
        public event Action<SessionState>? OnSessionStateChanged;

        /// <summary>
        /// Fired when a lobby code is generated.
        /// </summary>
        public event Action<string>? OnLobbyCodeGenerated;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }

            CleanupAsync().Forget();
        }

        /// <summary>
        /// Starts a hosting session with the given preset.
        /// </summary>
        /// <param name="preset">Session configuration.</param>
        /// <param name="port">Port to host on.</param>
        /// <param name="lobbyId">Unique lobby ID.</param>
        /// <param name="hostName">Display name of the host player.</param>
        /// <returns>Result of the session start.</returns>
        public async UniTask<SessionStartResult> StartHostingAsync(
            SessionPreset preset,
            int port,
            Guid lobbyId,
            string hostName = null)
        {
            // If a session is already active, stop it first
            if (_isSessionActive)
            {
                YargLogger.LogInfo("[SessionManager] Stopping existing session before starting new one");
                await StopAsync();
            }

            _sessionCts = new CancellationTokenSource();
            _currentPreset = preset;
            
            // Log preset details for debugging late join issues
            YargLogger.LogInfo($"[SessionManager] StartHostingAsync: preset.allowLateJoin={preset?.allowLateJoin}, " +
                $"preset.sessionName={preset?.sessionName}, preset.id={preset?.id}");
            
            var ct = _sessionCts.Token;

            try
            {
                OnSessionStateChanged?.Invoke(SessionState.Starting);

                if (preset.SessionType == SessionType.Lobby)
                {
                    // Lobby flow: UPnP → Register with Lobby Server → Get code
                    return await StartLobbySessionAsync(preset, port, lobbyId, hostName, ct);
                }
                else
                {
                    // Server flow: No UPnP, no lobby code, optional Lobby server registration for discovery
                    return await StartServerSessionAsync(preset, port, lobbyId, hostName, ct);
                }
            }
            catch (OperationCanceledException)
            {
                OnSessionStateChanged?.Invoke(SessionState.Stopped);
                return SessionStartResult.Failure("Session start was cancelled");
            }
            catch (Exception ex)
            {
                YargLogger.LogError($"[SessionManager] Failed to start session: {ex}");
                OnSessionStateChanged?.Invoke(SessionState.Failed);
                return SessionStartResult.Failure($"Failed to start session: {ex.Message}");
            }
        }

        /// <summary>
        /// Stops the current session and cleans up resources.
        /// </summary>
        public async UniTask StopAsync()
        {
            if (!_isSessionActive)
                return;

            _sessionCts?.Cancel();
            await CleanupAsync();

            _isSessionActive = false;
            OnSessionStateChanged?.Invoke(SessionState.Stopped);
        }

        /// <summary>
        /// Joins a lobby using a lobby code.
        /// Tries all enabled lobby servers until one finds the code.
        /// </summary>
        /// <param name="code">The 6-character lobby code.</param>
        /// <returns>Connection info if successful.</returns>
        public async UniTask<LobbyLookupResult?> LookupLobbyCodeAsync(string code)
        {
            var settings = NetworkSettingsStore.Instance?.Settings;
            if (settings == null)
            {
                YargLogger.LogError("[SessionManager] NetworkSettingsStore not initialized");
                return null;
            }

            var EnabledLobbyServers = settings.EnabledLobbyServers;
            if (EnabledLobbyServers == null || EnabledLobbyServers.Count == 0)
            {
                YargLogger.LogError("[SessionManager] No enabled lobby servers configured");
                return null;
            }

            _lobbyCodeClient ??= new LobbyCodeClient();

            // Try each lobby server until we find the lobby
            foreach (var lobbyServer in EnabledLobbyServers)
            {
                try
                {
                    YargLogger.LogInfo($"[SessionManager] Looking up code {code} on {lobbyServer.displayName}...");
                    var result = await _lobbyCodeClient.LookupCodeAsync(lobbyServer.url, code).AsUniTask();
                    if (result.IsSuccess)
                    {
                        YargLogger.LogInfo($"[SessionManager] Found lobby code {code} via {lobbyServer.displayName}");
                        return result;
                    }
                    else
                    {
                        YargLogger.LogInfo($"[SessionManager] Code {code} not found on {lobbyServer.displayName}: {result.Error}");
                    }
                }
                catch (Exception ex)
                {
                    YargLogger.LogWarning($"[SessionManager] Error looking up code on {lobbyServer.displayName}: {ex.Message}");
                }
            }
            
            YargLogger.LogWarning($"[SessionManager] Lobby code {code} not found on any lobby server");
            return LobbyLookupResult.Failure("Invalid or expired code");
        }

        private async UniTask<SessionStartResult> StartLobbySessionAsync(
            SessionPreset preset,
            int port,
            Guid lobbyId,
            string hostName,
            CancellationToken ct)
        {
            // Step 1: Attempt UPnP port forwarding
            OnSessionStateChanged?.Invoke(SessionState.ConfiguringUPnP);

            _upnpForwarder = new UPnPPortForwarder();
            bool upnpSuccess = await _upnpForwarder.DiscoverAsync(ct);
            string? externalAddress = null;

            if (upnpSuccess)
            {
                upnpSuccess = await _upnpForwarder.OpenPortAsync(port, ct);
                
                if (upnpSuccess)
                {
                    // Get the external address for code dissemination
                    externalAddress = await _upnpForwarder.GetExternalIPAsync(ct);
                    YargLogger.LogInfo($"[SessionManager] UPnP success! External address: {externalAddress ?? "null"}, port: {port}");
                    
                    // Verify the mapping by listing existing mappings
                    var mappings = await _upnpForwarder.ListMappingsAsync(ct);
                    if (mappings != null && mappings.Count > 0)
                    {
                        bool foundOurMapping = false;
                        foreach (var mapping in mappings)
                        {
                            YargLogger.LogInfo($"[SessionManager] Found UPnP mapping: {mapping.Protocol} {mapping.PublicPort} -> {mapping.PrivateIP}:{mapping.PrivatePort} ({mapping.Description})");
                            if (mapping.PublicPort == port)
                            {
                                foundOurMapping = true;
                            }
                        }
                        
                        if (foundOurMapping)
                        {
                            YargLogger.LogInfo($"[SessionManager] Verified: Port {port} mapping is active");
                        }
                        else
                        {
                            YargLogger.LogWarning($"[SessionManager] WARNING: Port {port} mapping not found in listing despite UPnP success");
                        }
                    }
                    else
                    {
                        YargLogger.LogWarning("[SessionManager] Could not verify UPnP mapping - listing returned empty");
                    }
                }
            }

            if (!upnpSuccess)
            {
                YargLogger.LogWarning("[SessionManager] UPnP port forwarding failed. Trying STUN to resolve public IP...");
                
                // Try STUN as a fallback to at least get the public IP
                // Note: Without UPnP port forwarding, connections from outside the LAN won't work,
                // but this allows the lobby server to have the correct public IP for reference.
                try
                {
                    externalAddress = await StunResolver.ResolvePublicAddressAsync(ct, 3000);
                    if (!string.IsNullOrEmpty(externalAddress))
                    {
                        YargLogger.LogInfo($"[SessionManager] STUN resolved public IP: {externalAddress}");
                    }
                    else
                    {
                        YargLogger.LogWarning("[SessionManager] STUN failed to resolve public IP");
                    }
                }
                catch (Exception ex)
                {
                    YargLogger.LogWarning($"[SessionManager] STUN resolution failed: {ex.Message}");
                }
            }

            // Step 2: Register with Lobby Server(s)
            OnSessionStateChanged?.Invoke(SessionState.RegisteringWithLobbyServer);

            var settings = NetworkSettingsStore.Instance?.Settings;
            var EnabledLobbyServers = settings?.EnabledLobbyServers;

            if (EnabledLobbyServers == null || EnabledLobbyServers.Count == 0)
            {
                return SessionStartResult.Failure("No enabled lobby servers configured. Cannot create lobby code.");
            }

            // Step 3: Try to register lobby and generate code from each lobby server until one succeeds
            _lobbyCodeClient ??= new LobbyCodeClient();
            _registeredLobbyServerUrls.Clear();
            
            string lobbyCode = null;
            LobbyServerEndpoint successfulLobbyServer = null;
            var failedLobbyServers = new List<(LobbyServerEndpoint lobbyServer, string error)>();
            
            // Get address to register - prefer external (UPnP/STUN) address, fall back to LAN address
            // Never use 0.0.0.0 because the lobby server would use its own view of our IP (e.g., Docker gateway)
            string lanAddress = NetworkAddressUtility.GetLocalLanAddress();
            string registerAddress = externalAddress ?? lanAddress ?? "0.0.0.0";
            
            // Detect if we're registering with a LAN address - this will NOT work for external players!
            bool isRegisteringWithLanAddress = string.IsNullOrEmpty(externalAddress) && !string.IsNullOrEmpty(lanAddress);
            if (isRegisteringWithLanAddress)
            {
                YargLogger.LogWarning($"[SessionManager] WARNING: No public IP could be determined! " +
                    $"Registering with LAN address {registerAddress} - external players will NOT be able to connect via lobby code. " +
                    "Only players on the same local network can connect.");
            }
            
            YargLogger.LogInfo($"[SessionManager] Registering with address: {registerAddress} (external: {externalAddress ?? "null"}, lan: {lanAddress ?? "null"}, port: {port})");
            
            foreach (var lobbyServer in EnabledLobbyServers)
            {
                YargLogger.LogInfo($"[SessionManager] Trying lobbyServer: {lobbyServer.displayName} ({lobbyServer.url})");
                
                try
                {
                    // First, register the lobby with this lobby server
                    YargLogger.LogInfo($"[SessionManager] Registering lobby with {lobbyServer.displayName}...");
                    bool registered = await _lobbyCodeClient.RegisterLobbyAsync(
                        lobbyServer.url,
                        lobbyId,
                        preset.sessionName ?? "YARG Lobby",
                        hostName ?? "Host",
                        registerAddress,
                        port,
                        preset.maxPlayers,
                        preset.HasPassword,
                        ct).AsUniTask();
                    
                    if (!registered)
                    {
                        YargLogger.LogWarning($"[SessionManager] Failed to register lobby with {lobbyServer.displayName}");
                        failedLobbyServers.Add((lobbyServer, "Failed to register lobby"));
                        continue;
                    }
                    
                    YargLogger.LogInfo($"[SessionManager] Lobby registered with {lobbyServer.displayName}, generating code...");
                    
                    // Now generate the code
                    var codeResult = await _lobbyCodeClient.GenerateCodeAsync(lobbyServer.url, lobbyId, ct).AsUniTask();
                    
                    if (codeResult.IsSuccess && !string.IsNullOrEmpty(codeResult.Code))
                    {
                        lobbyCode = codeResult.Code;
                        successfulLobbyServer = lobbyServer;
                        _registeredLobbyServerUrls.Add(lobbyServer.url);
                        YargLogger.LogInfo($"[SessionManager] Successfully generated lobby code '{lobbyCode}' from {lobbyServer.displayName}");
                        break;
                    }
                    else
                    {
                        var error = codeResult.Error ?? "Unknown error";
                        YargLogger.LogWarning($"[SessionManager] LobbyServer {lobbyServer.displayName} failed: {error}");
                        failedLobbyServers.Add((lobbyServer, error));
                    }
                }
                catch (Exception ex)
                {
                    YargLogger.LogWarning($"[SessionManager] LobbyServer {lobbyServer.displayName} threw exception: {ex.Message}");
                    failedLobbyServers.Add((lobbyServer, ex.Message));
                }
            }
            
            // If All lobby servers failed, return error
            if (string.IsNullOrEmpty(lobbyCode))
            {
                var errorSummary = string.Join("; ", failedLobbyServers.Select(f => $"{f.lobbyServer.displayName}: {f.error}"));
                YargLogger.LogError($"[SessionManager] All {EnabledLobbyServers.Count} lobby servers failed to generate lobby code: {errorSummary}");
                return SessionStartResult.Failure($"All lobby servers failed. {errorSummary}");
            }

            _currentLobbyCode = lobbyCode;
            _currentLobbyId = lobbyId;
            
            OnLobbyCodeGenerated?.Invoke(_currentLobbyCode);
            YargLogger.LogInfo($"[SessionManager] Lobby code generated: {_currentLobbyCode} (from {successfulLobbyServer.displayName})");

            // Step 4: Disseminate the code to all OTHER enabled lobby servers (ones we haven't registered with yet)
            var otherLobbyServers = EnabledLobbyServers.Where(i => i.url != successfulLobbyServer.url).ToList();
            if (otherLobbyServers.Count > 0 && !string.IsNullOrEmpty(externalAddress))
            {
                await DisseminateCodeToLobbyServersAsync(
                    otherLobbyServers,
                    _currentLobbyCode,
                    lobbyId,
                    externalAddress,
                    port,
                    ct);
            }
            else if (otherLobbyServers.Count > 0)
            {
                YargLogger.LogWarning("[SessionManager] Could not get public address for code dissemination. " +
                    "Code will only be registered with the successful lobby server.");
            }

            // Cache lobby info for heartbeats
            _cachedLobbyName = preset.sessionName ?? "YARG Lobby";
            _cachedHostName = hostName ?? "Host";
            _cachedHostAddress = registerAddress;
            _cachedHostPort = port;
            _cachedMaxPlayers = preset.maxPlayers;
            _cachedHasPassword = preset.HasPassword;
            
            // Step 5: Register with NAT punch server for hole punching coordination
            // This allows clients to connect even without UPnP port forwarding
            await RegisterWithNatPunchServerAsync(successfulLobbyServer.url, lobbyId, port, ct);
            
            // Step 6: Allocate relay session and connect host to relay
            // This enables relay fallback for clients when direct/NAT punch fails
            await AllocateAndConnectHostRelayAsync(successfulLobbyServer.url, lobbyId, ct);
            
            // Start heartbeat loop to keep lobby alive on lobby servers
            StartHeartbeatLoop();

            _isSessionActive = true;
            OnSessionStateChanged?.Invoke(SessionState.Active);

            int registeredCount = _registeredLobbyServerUrls.Count;
            int totalCount = EnabledLobbyServers.Count;
            string registrationMessage = registeredCount == totalCount
                ? $"Lobby code registered with all {totalCount} lobby servers"
                : $"Lobby code registered with {registeredCount} of {totalCount} lobby servers";

            // Build the result message based on the actual state
            string resultMessage;
            if (upnpSuccess)
            {
                resultMessage = $"Lobby created successfully. {registrationMessage}.";
            }
            else if (isRegisteringWithLanAddress)
            {
                // This is the worst case - no public IP at all
                resultMessage = $"WARNING: Could not determine public IP! {registrationMessage}. " +
                    $"External players will NOT be able to connect via lobby code. " +
                    $"Only players on the same local network can join. " +
                    $"Enable UPnP on your router or manually forward port {port}.";
            }
            else
            {
                // UPnP failed but STUN got the public IP - connections may still work with manual port forward
                resultMessage = $"Lobby created, but UPnP port forwarding failed. {registrationMessage}. " +
                    $"External players may not be able to connect unless port {port} is manually forwarded.";
            }

            return SessionStartResult.Success(
                upnpSuccess: upnpSuccess,
                lobbyCode: _currentLobbyCode,
                message: resultMessage);
        }
        
        /// <summary>
        /// Disseminates a lobby code to additional lobby servers so players using
        /// any of the same lobby servers can find each other.
        /// </summary>
        private async UniTask DisseminateCodeToLobbyServersAsync(
            IReadOnlyList<LobbyServerEndpoint> lobbyServers,
            string code,
            Guid lobbyId,
            string hostAddress,
            int hostPort,
            CancellationToken ct)
        {
            var tasks = new List<UniTask<(LobbyServerEndpoint lobbyServer, bool success)>>();
            
            foreach (var lobbyServer in lobbyServers)
            {
                tasks.Add(RegisterCodeWithLobbyServerAsync(lobbyServer, code, lobbyId, hostAddress, hostPort, ct));
            }
            
            var results = await UniTask.WhenAll(tasks);
            
            foreach (var (lobbyServer, success) in results)
            {
                if (success)
                {
                    _registeredLobbyServerUrls.Add(lobbyServer.url);
                    YargLogger.LogInfo($"[SessionManager] Lobby code registered with {lobbyServer.displayName}");
                }
                else
                {
                    YargLogger.LogWarning($"[SessionManager] Failed to register lobby code with {lobbyServer.displayName}");
                }
            }
        }
        
        private async UniTask<(LobbyServerEndpoint lobbyServer, bool success)> RegisterCodeWithLobbyServerAsync(
            LobbyServerEndpoint lobbyServer,
            string code,
            Guid lobbyId,
            string hostAddress,
            int hostPort,
            CancellationToken ct)
        {
            try
            {
                bool success = await _lobbyCodeClient!.RegisterCodeAsync(
                    lobbyServer.url, 
                    code, 
                    lobbyId, 
                    hostAddress, 
                    hostPort, 
                    ct).AsUniTask();
                return (lobbyServer, success);
            }
            catch (Exception ex)
            {
                YargLogger.LogWarning($"[SessionManager] Error registering code with {lobbyServer.displayName}: {ex.Message}");
                return (lobbyServer, false);
            }
        }

        private async UniTask<SessionStartResult> StartServerSessionAsync(
            SessionPreset preset,
            int port,
            Guid lobbyId,
            string hostName,
            CancellationToken ct)
        {
            // Server mode - no UPnP, no lobby code
            // User is expected to handle port forwarding manually
            YargLogger.LogInfo($"[SessionManager] Starting server session (privacy: {preset.PrivacyMode}, registerWithLobbyServers: {preset.registerWithLobbyServers})");

            // For non-Unlisted servers that want to be discoverable, Register with Lobby Servers
            // This allows the server to appear in the server browser without a lobby code
            bool shouldRegisterWithLobbyServers = preset.registerWithLobbyServers && 
                                                  preset.PrivacyMode != SessionPrivacyMode.Unlisted;
            
            if (shouldRegisterWithLobbyServers)
            {
                OnSessionStateChanged?.Invoke(SessionState.RegisteringWithLobbyServer);
                
                var settings = NetworkSettingsStore.Instance?.Settings;
                var EnabledLobbyServers = settings?.EnabledLobbyServers;
                
                if (EnabledLobbyServers != null && EnabledLobbyServers.Count > 0)
                {
                    _lobbyCodeClient ??= new LobbyCodeClient();
                    _registeredLobbyServerUrls.Clear();
                    _currentLobbyId = lobbyId;
                    
                    // Get LAN address for registration (no UPnP external address for servers)
                    string lanAddress = NetworkAddressUtility.GetLocalLanAddress() ?? "0.0.0.0";
                    YargLogger.LogInfo($"[SessionManager] Registering server with lobby servers at {lanAddress}:{port}");
                    
                    // Cache for heartbeats
                    _cachedLobbyName = preset.sessionName ?? "YARG Server";
                    _cachedHostName = hostName ?? "Host";
                    _cachedHostAddress = lanAddress;
                    _cachedHostPort = port;
                    _cachedMaxPlayers = preset.maxPlayers;
                    _cachedHasPassword = preset.HasPassword;
                    
                    // Register with each lobby server for discovery (no code generation)
                    foreach (var lobbyServer in EnabledLobbyServers)
                    {
                        try
                        {
                            bool registered = await _lobbyCodeClient.RegisterLobbyAsync(
                                lobbyServer.url,
                                lobbyId,
                                preset.sessionName ?? "YARG Server",
                                hostName ?? "Host",
                                lanAddress,
                                port,
                                preset.maxPlayers,
                                preset.HasPassword,
                                ct).AsUniTask();
                            
                            if (registered)
                            {
                                _registeredLobbyServerUrls.Add(lobbyServer.url);
                                YargLogger.LogInfo($"[SessionManager] Server registered with {lobbyServer.displayName} for discovery");
                            }
                            else
                            {
                                YargLogger.LogWarning($"[SessionManager] Failed to register server with {lobbyServer.displayName}");
                            }
                        }
                        catch (Exception ex)
                        {
                            YargLogger.LogWarning($"[SessionManager] Error registering server with {lobbyServer.displayName}: {ex.Message}");
                        }
                    }
                    
                    // Start heartbeat if we registered with any lobby servers
                    if (_registeredLobbyServerUrls.Count > 0)
                    {
                        StartHeartbeatLoop();
                    }
                }
                else
                {
                    YargLogger.LogInfo("[SessionManager] No lobby servers configured, server will only be discoverable on LAN");
                }
            }
            else
            {
                YargLogger.LogInfo("[SessionManager] Server is Unlisted or doesn't want Lobby server registration - skipping");
            }

            _isSessionActive = true;
            OnSessionStateChanged?.Invoke(SessionState.Active);

            string message = shouldRegisterWithLobbyServers && _registeredLobbyServerUrls.Count > 0
                ? $"Server started and registered with {_registeredLobbyServerUrls.Count} lobby server(s). Ensure port forwarding is configured."
                : "Server started. Ensure port forwarding is configured for players to connect.";

            return SessionStartResult.Success(
                upnpSuccess: false,
                lobbyCode: null,  // NO lobby code for servers
                message: message);
        }

        private async UniTask CleanupAsync()
        {
            // Stop heartbeat loop first
            StopHeartbeatLoop();
            
            // Cleanup NAT punch resources
            CleanupNatPunch();
            
            // Release lobby code from all registered lobby servers
            if (!string.IsNullOrEmpty(_currentLobbyCode) && _lobbyCodeClient != null)
            {
                var releaseTasks = new List<UniTask>();
                
                foreach (var lobbyServerUrl in _registeredLobbyServerUrls)
                {
                    releaseTasks.Add(ReleaseCodeFromLobbyServerAsync(lobbyServerUrl, _currentLobbyCode));
                }
                
                if (releaseTasks.Count > 0)
                {
                    await UniTask.WhenAll(releaseTasks);
                    YargLogger.LogInfo($"[SessionManager] Released lobby code {_currentLobbyCode} from {releaseTasks.Count} lobby server(s)");
                }
                
                _registeredLobbyServerUrls.Clear();
            }

            // Close UPnP port
            if (_upnpForwarder != null)
            {
                try
                {
                    await _upnpForwarder.ClosePortAsync();
                }
                catch (Exception ex)
                {
                    YargLogger.LogWarning($"[SessionManager] Failed to close UPnP port: {ex.Message}");
                }

                _upnpForwarder.Dispose();
                _upnpForwarder = null;
            }

            _currentLobbyCode = null;
            _currentLobbyId = Guid.Empty;
            _currentPreset = null;
            _sessionCts?.Dispose();
            _sessionCts = null;
            
            // Clear cached heartbeat data
            _cachedLobbyName = null;
            _cachedHostName = null;
            _cachedHostAddress = null;
            _cachedHostPort = 0;
            _cachedMaxPlayers = 0;
            _cachedHasPassword = false;
        }
        
        private async UniTask ReleaseCodeFromLobbyServerAsync(string lobbyServerUrl, string code)
        {
            try
            {
                await _lobbyCodeClient!.ReleaseCodeAsync(lobbyServerUrl, code).AsUniTask();
            }
            catch (Exception ex)
            {
                YargLogger.LogWarning($"[SessionManager] Failed to release lobby code from {lobbyServerUrl}: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Starts the heartbeat loop to keep the lobby alive on all registered lobby servers.
        /// </summary>
        private void StartHeartbeatLoop()
        {
            StopHeartbeatLoop(); // Cancel any existing heartbeat
            
            _heartbeatCts = new CancellationTokenSource();
            HeartbeatLoopAsync(_heartbeatCts.Token).Forget();
            
            YargLogger.LogInfo($"[SessionManager] Started heartbeat loop (interval: {HeartbeatIntervalSeconds}s)");
        }
        
        /// <summary>
        /// Stops the heartbeat loop.
        /// </summary>
        private void StopHeartbeatLoop()
        {
            if (_heartbeatCts != null)
            {
                _heartbeatCts.Cancel();
                _heartbeatCts.Dispose();
                _heartbeatCts = null;
                YargLogger.LogInfo("[SessionManager] Stopped heartbeat loop");
            }
        }
        
        /// <summary>
        /// The heartbeat loop that periodically sends updates to all registered lobby servers.
        /// </summary>
        private async UniTaskVoid HeartbeatLoopAsync(CancellationToken ct)
        {
            // Wait for the first interval before sending the first heartbeat
            // (the initial registration just happened)
            await UniTask.Delay(TimeSpan.FromSeconds(HeartbeatIntervalSeconds), cancellationToken: ct);
            
            while (!ct.IsCancellationRequested && _isSessionActive)
            {
                try
                {
                    await SendHeartbeatToAllLobbyServersAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    YargLogger.LogWarning($"[SessionManager] Heartbeat error: {ex.Message}");
                }
                
                // Wait for next heartbeat interval
                try
                {
                    await UniTask.Delay(TimeSpan.FromSeconds(HeartbeatIntervalSeconds), cancellationToken: ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        
        // ========== NAT PUNCH METHODS ==========
        
        // Cached punch server info for UDP keepalives
        private string? _punchServerHost;
        private int _punchServerPort;
        private CancellationTokenSource? _punchKeepaliveCts;
        
        /// <summary>
        /// Registers this host with the NAT punch server for hole punching coordination.
        /// IMPORTANT: The game server transport must be running before calling this.
        /// NAT punch messages are received by the game server's socket (not a separate client).
        /// </summary>
        private async UniTask RegisterWithNatPunchServerAsync(string lobbyServerUrl, Guid lobbyId, int gamePort, CancellationToken ct)
        {
            try
            {
                // Subscribe to NAT punch events on the networking service's transport
                var networkService = Abstraction.NetworkingServiceFactory.Instance;
                if (networkService != null)
                {
                    networkService.OnNatPunchSuccess += OnNetworkServiceNatPunchSuccess;
                }
                
                var httpClient = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                
                // First, get the punch server's UDP address
                var punchInfoUri = new Uri(new Uri(lobbyServerUrl), "/api/punch/info");
                var punchInfoResponse = await httpClient.GetAsync(punchInfoUri, ct);
                
                if (!punchInfoResponse.IsSuccessStatusCode)
                {
                    YargLogger.LogWarning("[SessionManager] Failed to get NAT punch server info");
                    httpClient.Dispose();
                    return;
                }
                
                var punchInfoJson = await punchInfoResponse.Content.ReadAsStringAsync();
                var punchInfo = System.Text.Json.JsonSerializer.Deserialize<PunchInfoResponse>(punchInfoJson,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                
                if (punchInfo == null || !punchInfo.Available || string.IsNullOrEmpty(punchInfo.Address))
                {
                    YargLogger.LogWarning("[SessionManager] NAT punch server not available");
                    httpClient.Dispose();
                    return;
                }
                
                // Save punch server address for UDP keepalives
                _punchServerHost = punchInfo.Address;
                _punchServerPort = punchInfo.Port;
                YargLogger.LogInfo($"[SessionManager] NAT punch server: {_punchServerHost}:{_punchServerPort}");
                
                // Register with punch server via HTTP - using the game server's port
                // The punch server will send NAT introduce packets to this port
                var localIp = NetworkAddressUtility.GetLocalLanAddress();
                var localEndpoint = $"{localIp}:{gamePort}";
                
                var request = new PunchRegisterRequest(lobbyId, localEndpoint, gamePort);
                var json = System.Text.Json.JsonSerializer.Serialize(request);
                var content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");
                
                var uri = new Uri(new Uri(lobbyServerUrl), "/api/punch/register");
                var response = await httpClient.PostAsync(uri, content, ct);
                
                if (response.IsSuccessStatusCode)
                {
                    // Save for heartbeat refreshes
                    _natPunchLobbyServerUrl = lobbyServerUrl;
                    _natPunchGamePort = gamePort;
                    YargLogger.LogInfo($"[SessionManager] Registered with NAT punch server (game port: {gamePort})");
                    
                    // NOTE: We do NOT start the keepalive loop here!
                    // The transport must be running before we can send UDP packets.
                    // Call StartPunchKeepalive() after NetworkService.CreateLobby() completes.
                }
                else
                {
                    var error = await response.Content.ReadAsStringAsync();
                    YargLogger.LogWarning($"[SessionManager] Failed to register with NAT punch server: {response.StatusCode} - {error}");
                }
                
                httpClient.Dispose();
            }
            catch (Exception ex)
            {
                YargLogger.LogWarning($"[SessionManager] NAT punch registration failed: {ex.Message}");
            }
        }
        
        // Cached relay allocation info
        private Guid _relaySessionId = Guid.Empty;
        private string? _relayAddress;
        private int _relayPort;
        
        /// <summary>
        /// Gets the current relay session ID (if allocated).
        /// </summary>
        public Guid RelaySessionId => _relaySessionId;
        
        /// <summary>
        /// Allocates a relay session and connects the host to the relay server.
        /// This enables relay fallback for clients who can't connect directly.
        /// </summary>
        private async UniTask AllocateAndConnectHostRelayAsync(string lobbyServerUrl, Guid lobbyId, CancellationToken ct)
        {
            try
            {
                // First check if relay is available
                var relayInfo = await CheckRelayAvailableAsync(lobbyServerUrl, ct);
                
                if (relayInfo == null || !relayInfo.Available)
                {
                    YargLogger.LogInfo("[SessionManager] Relay not available - clients will use direct/NAT punch only");
                    return;
                }
                
                YargLogger.LogInfo($"[SessionManager] Relay available at {relayInfo.Address}:{relayInfo.Port}");
                
                // Allocate a relay session for this lobby
                var allocation = await AllocateRelaySessionAsync(lobbyId, lobbyServerUrl, ct);
                
                if (allocation == null || !allocation.Success)
                {
                    YargLogger.LogWarning("[SessionManager] Failed to allocate relay session - relay fallback disabled");
                    return;
                }
                
                _relaySessionId = allocation.SessionId;
                _relayAddress = allocation.RelayAddress;
                _relayPort = allocation.RelayPort;
                
                YargLogger.LogInfo($"[SessionManager] Relay session allocated: {_relaySessionId}");
                
                // Connect the host to the relay
                var networkService = Abstraction.NetworkingServiceFactory.Instance;
                if (networkService == null)
                {
                    YargLogger.LogWarning("[SessionManager] NetworkService not available - cannot connect host to relay");
                    return;
                }
                
                bool connected = await networkService.ConnectHostToRelayAsync(
                    _relayAddress!, 
                    _relayPort, 
                    _relaySessionId);
                
                if (connected)
                {
                    YargLogger.LogInfo($"[SessionManager] Host connected to relay - clients can now use relay fallback");
                }
                else
                {
                    YargLogger.LogWarning("[SessionManager] Host failed to connect to relay - relay fallback disabled");
                    _relaySessionId = Guid.Empty;
                    _relayAddress = null;
                    _relayPort = 0;
                }
            }
            catch (Exception ex)
            {
                YargLogger.LogWarning($"[SessionManager] Relay allocation/connection failed: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Starts a loop that sends periodic UDP packets to the punch server to keep NAT mapping open.
        /// </summary>
        private void StartPunchKeepaliveLoop(Guid lobbyId)
        {
            StopPunchKeepaliveLoop();
            
            if (string.IsNullOrEmpty(_punchServerHost) || _punchServerPort == 0)
            {
                YargLogger.LogWarning("[SessionManager] Cannot start punch keepalive - no punch server configured");
                return;
            }
            
            _punchKeepaliveCts = new CancellationTokenSource();
            PunchKeepaliveLoopAsync(lobbyId, _punchKeepaliveCts.Token).Forget();
            YargLogger.LogInfo("[SessionManager] Started NAT punch keepalive loop");
        }
        
        /// <summary>
        /// Starts sending NAT punch keepalive packets to maintain NAT mapping.
        /// IMPORTANT: Call this AFTER the transport is running (after CreateLobby()).
        /// </summary>
        public void StartPunchKeepalive()
        {
            if (!_isSessionActive)
            {
                YargLogger.LogWarning("[SessionManager] Cannot start punch keepalive - no active session");
                return;
            }
            
            if (_currentLobbyId == Guid.Empty)
            {
                YargLogger.LogWarning("[SessionManager] Cannot start punch keepalive - no lobby ID");
                return;
            }
            
            StartPunchKeepaliveLoop(_currentLobbyId);
        }
        
        /// <summary>
        /// Stops the punch keepalive loop.
        /// </summary>
        private void StopPunchKeepaliveLoop()
        {
            if (_punchKeepaliveCts != null)
            {
                _punchKeepaliveCts.Cancel();
                _punchKeepaliveCts.Dispose();
                _punchKeepaliveCts = null;
                YargLogger.LogInfo("[SessionManager] Stopped NAT punch keepalive loop");
            }
        }
        
        /// <summary>
        /// Periodically sends UDP packets to the punch server to keep NAT mapping open.
        /// </summary>
        private async UniTaskVoid PunchKeepaliveLoopAsync(Guid lobbyId, CancellationToken ct)
        {
            var networkService = Abstraction.NetworkingServiceFactory.Instance;
            if (networkService == null)
            {
                YargLogger.LogWarning("[SessionManager] Cannot send punch keepalives - no networking service");
                return;
            }
            
            // Token format: "host:{lobbyId}" - this tells the server we're a host
            var token = $"host:{lobbyId}";
            
            // Send initial keepalive immediately
            networkService.SendNatIntroduceRequest(_punchServerHost!, _punchServerPort, token);
            YargLogger.LogInfo($"[SessionManager] Sent initial punch keepalive to {_punchServerHost}:{_punchServerPort}");
            
            // Send keepalives every 5 seconds to keep NAT mapping alive
            // Most NATs have timeouts of 30-60 seconds for UDP
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await UniTask.Delay(TimeSpan.FromSeconds(5), cancellationToken: ct);
                    
                    networkService.SendNatIntroduceRequest(_punchServerHost!, _punchServerPort, token);
                    YargLogger.LogDebug($"[SessionManager] Sent punch keepalive to {_punchServerHost}:{_punchServerPort}");
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    YargLogger.LogWarning($"[SessionManager] Punch keepalive error: {ex.Message}");
                }
            }
        }
        
        /// <summary>
        /// Called when a NAT punch succeeds on the networking service's transport.
        /// </summary>
        private void OnNetworkServiceNatPunchSuccess(IPEndPoint targetEndpoint, NatAddressType type, string token)
        {
            YargLogger.LogInfo($"[SessionManager] NAT punch success to {targetEndpoint} (type={type}, token={token})");
            _punchedEndpoint = targetEndpoint;
        }
        
        /// <summary>
        /// Called when a NAT punch succeeds (legacy - keeping for compatibility during transition).
        /// </summary>
        private void OnNatPunchSuccess(IPEndPoint targetEndpoint, NatAddressType type, string token)
        {
            YargLogger.LogInfo($"[SessionManager] NAT punch success (legacy) to {targetEndpoint} (type={type}, token={token})");
            _punchedEndpoint = targetEndpoint;
        }
        
        /// <summary>
        /// Initiates NAT punch-through to connect to a lobby.
        /// Call this before attempting to connect via NetworkService.JoinLobby().
        /// The networking service's transport will receive the punch messages.
        /// </summary>
        /// <param name="lobbyId">The lobby ID to connect to</param>
        /// <param name="lobbyServerUrl">The lobby server URL to use for coordination</param>
        /// <param name="timeoutMs">How long to wait for punch to complete</param>
        /// <returns>The punched endpoint if successful, null if punch failed</returns>
        public async UniTask<IPEndPoint?> InitiateNatPunchAsync(Guid lobbyId, string lobbyServerUrl, int timeoutMs = 5000)
        {
            _punchedEndpoint = null;
            bool startedTransportForPunch = false;
            
            try
            {
                var networkService = Abstraction.NetworkingServiceFactory.Instance;
                if (networkService == null)
                {
                    YargLogger.LogWarning("[SessionManager] NAT punch failed: Networking service not available");
                    return null;
                }
                
                // Subscribe to NAT punch events
                networkService.OnNatPunchSuccess += OnNetworkServiceNatPunchSuccess;
                
                // Start transport if not already running (needed to receive NAT punch messages)
                var localPort = networkService.LocalTransportPort;
                if (localPort == 0)
                {
                    YargLogger.LogInfo("[SessionManager] Starting transport for NAT punch...");
                    if (!networkService.StartTransportForNatPunch())
                    {
                        YargLogger.LogWarning("[SessionManager] NAT punch failed: Could not start transport");
                        networkService.OnNatPunchSuccess -= OnNetworkServiceNatPunchSuccess;
                        return null;
                    }
                    startedTransportForPunch = true;
                    localPort = networkService.LocalTransportPort;
                    YargLogger.LogInfo($"[SessionManager] Transport started for NAT punch on port {localPort}");
                }
                
                var httpClient = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                
                // First, get punch server info to know where to send UDP
                var punchInfoUri = new Uri(new Uri(lobbyServerUrl), "/api/punch/info");
                var punchInfoResponse = await httpClient.GetAsync(punchInfoUri);
                
                if (!punchInfoResponse.IsSuccessStatusCode)
                {
                    YargLogger.LogWarning("[SessionManager] NAT punch failed: Could not get punch server info");
                    httpClient.Dispose();
                    if (startedTransportForPunch)
                    {
                        networkService.StopTransport();
                    }
                    networkService.OnNatPunchSuccess -= OnNetworkServiceNatPunchSuccess;
                    return null;
                }
                
                var punchInfoJson = await punchInfoResponse.Content.ReadAsStringAsync();
                var punchInfo = System.Text.Json.JsonSerializer.Deserialize<PunchInfoResponse>(punchInfoJson,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                
                if (punchInfo == null || !punchInfo.Available || string.IsNullOrEmpty(punchInfo.Address))
                {
                    YargLogger.LogWarning("[SessionManager] NAT punch server not available");
                    httpClient.Dispose();
                    if (startedTransportForPunch)
                    {
                        networkService.StopTransport();
                    }
                    networkService.OnNatPunchSuccess -= OnNetworkServiceNatPunchSuccess;
                    return null;
                }
                
                var punchServerHost = punchInfo.Address;
                var punchServerPort = punchInfo.Port;
                YargLogger.LogInfo($"[SessionManager] NAT punch server: {punchServerHost}:{punchServerPort}");
                
                // Client token for this punch request
                var clientToken = Guid.NewGuid().ToString("N");
                var token = $"client:{lobbyId}:{clientToken}";
                
                // CRITICAL: Send UDP packet to punch server BEFORE HTTP request
                // This opens the NAT mapping so the server can send packets back to us
                YargLogger.LogInfo($"[SessionManager] Sending UDP to punch server to open NAT mapping...");
                networkService.SendNatIntroduceRequest(punchServerHost, punchServerPort, token);
                
                // Send a few packets to ensure the NAT mapping is established
                for (int i = 0; i < 3; i++)
                {
                    await UniTask.Delay(100);
                    networkService.SendNatIntroduceRequest(punchServerHost, punchServerPort, token);
                }
                
                // Now request punch coordination via HTTP
                var localIp = NetworkAddressUtility.GetLocalLanAddress();
                var localEndpoint = $"{localIp}:{localPort}";
                
                var request = new PunchRequest(lobbyId, localEndpoint, localPort, clientToken);
                var json = System.Text.Json.JsonSerializer.Serialize(request);
                var content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");
                
                var uri = new Uri(new Uri(lobbyServerUrl), "/api/punch/request");
                YargLogger.LogInfo($"[SessionManager] Requesting NAT punch via HTTP...");
                var response = await httpClient.PostAsync(uri, content);
                
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    YargLogger.LogWarning($"[SessionManager] NAT punch request failed: {response.StatusCode} - {error}");
                    httpClient.Dispose();
                    
                    // Stop transport if we started it for punch
                    if (startedTransportForPunch)
                    {
                        YargLogger.LogInfo("[SessionManager] Stopping transport started for failed NAT punch");
                        networkService.StopTransport();
                    }
                    networkService.OnNatPunchSuccess -= OnNetworkServiceNatPunchSuccess;
                    return null;
                }
                
                var responseJson = await response.Content.ReadAsStringAsync();
                var result = System.Text.Json.JsonSerializer.Deserialize<PunchResponseDto>(responseJson, 
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                
                httpClient.Dispose();
                
                if (result == null || !result.Success)
                {
                    YargLogger.LogWarning($"[SessionManager] NAT punch initiation failed: {result?.Message ?? "Unknown error"}");
                    
                    // Stop transport if we started it for punch
                    if (startedTransportForPunch)
                    {
                        YargLogger.LogInfo("[SessionManager] Stopping transport started for failed NAT punch");
                        networkService.StopTransport();
                    }
                    networkService.OnNatPunchSuccess -= OnNetworkServiceNatPunchSuccess;
                    return null;
                }
                
                YargLogger.LogInfo($"[SessionManager] NAT punch initiated, waiting for success (timeout: {timeoutMs}ms)...");
                
                // Wait for punch success (with timeout)
                // Keep sending UDP packets to maintain NAT mapping
                var startTime = DateTime.UtcNow;
                var lastSendTime = DateTime.UtcNow;
                while (_punchedEndpoint == null && (DateTime.UtcNow - startTime).TotalMilliseconds < timeoutMs)
                {
                    await UniTask.Delay(50);
                    
                    // Send UDP keepalive every 500ms while waiting
                    if ((DateTime.UtcNow - lastSendTime).TotalMilliseconds > 500)
                    {
                        networkService.SendNatIntroduceRequest(punchServerHost, punchServerPort, token);
                        lastSendTime = DateTime.UtcNow;
                    }
                }
                
                // Unsubscribe from events
                networkService.OnNatPunchSuccess -= OnNetworkServiceNatPunchSuccess;
                
                if (_punchedEndpoint != null)
                {
                    YargLogger.LogInfo($"[SessionManager] NAT punch succeeded! Endpoint: {_punchedEndpoint}");
                    // Keep transport running - it will be used for the connection
                    return _punchedEndpoint;
                }
                else
                {
                    YargLogger.LogWarning("[SessionManager] NAT punch timed out - falling back to direct connection");
                    
                    // Stop transport if we started it for punch
                    if (startedTransportForPunch)
                    {
                        YargLogger.LogInfo("[SessionManager] Stopping transport started for failed NAT punch");
                        networkService.StopTransport();
                    }
                    return null;
                }
            }
            catch (Exception ex)
            {
                YargLogger.LogWarning($"[SessionManager] NAT punch error: {ex.Message}");
                
                // Try to clean up transport if we started it
                if (startedTransportForPunch)
                {
                    try
                    {
                        var networkService = Abstraction.NetworkingServiceFactory.Instance;
                        if (networkService != null)
                        {
                            YargLogger.LogInfo("[SessionManager] Stopping transport after NAT punch error");
                            networkService.StopTransport();
                            networkService.OnNatPunchSuccess -= OnNetworkServiceNatPunchSuccess;
                        }
                    }
                    catch { /* ignore cleanup errors */ }
                }
                return null;
            }
        }
        
        // DTOs for HTTP punch communication (Unity-compatible classes instead of records)
        [Serializable]
        private class PunchRegisterRequest
        {
            public Guid LobbyId { get; set; }
            public string InternalEndpoint { get; set; }
            public int ExternalPort { get; set; }
            
            public PunchRegisterRequest(Guid lobbyId, string internalEndpoint, int externalPort)
            {
                LobbyId = lobbyId;
                InternalEndpoint = internalEndpoint;
                ExternalPort = externalPort;
            }
        }
        
        [Serializable]
        private class PunchRequest
        {
            public Guid LobbyId { get; set; }
            public string ClientInternalEndpoint { get; set; }
            public int ClientPort { get; set; }
            public string ClientToken { get; set; }
            
            public PunchRequest(Guid lobbyId, string clientInternalEndpoint, int clientPort, string clientToken = null)
            {
                LobbyId = lobbyId;
                ClientInternalEndpoint = clientInternalEndpoint;
                ClientPort = clientPort;
                ClientToken = clientToken;
            }
        }
        
        [Serializable]
        private class PunchResponseDto
        {
            public bool Success { get; set; }
            public string PunchToken { get; set; }
            public string Message { get; set; }
        }
        
        [Serializable]
        private class PunchInfoResponse
        {
            public bool Available { get; set; }
            public string Address { get; set; }
            public int Port { get; set; }
            public string Message { get; set; }
        }
        
        /// <summary>
        /// Cleans up NAT punch resources.
        /// </summary>
        private void CleanupNatPunch()
        {
            // Stop punch keepalive loop
            StopPunchKeepaliveLoop();
            
            // Clear punch server info
            _punchServerHost = null;
            _punchServerPort = 0;
            
            // Unsubscribe from networking service's NAT punch events
            // Use InstanceOrNull to avoid re-initialization during shutdown
            var networkService = Abstraction.NetworkingServiceFactory.InstanceOrNull;
            if (networkService != null)
            {
                networkService.OnNatPunchSuccess -= OnNetworkServiceNatPunchSuccess;
            }
            
            // Clean up legacy NatPunchClient if still present
            if (_natPunchClient != null)
            {
                _natPunchClient.OnPunchSuccess -= OnNatPunchSuccess;
                
                if (_currentLobbyId != Guid.Empty)
                {
                    // Fire and forget unregistration
                    _ = _natPunchClient.UnregisterAsHostAsync(_currentLobbyId);
                }
                
                _natPunchClient.Dispose();
                _natPunchClient = null;
            }
            
            _punchedEndpoint = null;
        }

        /// <summary>
        /// Sends heartbeat (re-register) to all registered lobby servers.
        /// </summary>
        private async UniTask SendHeartbeatToAllLobbyServersAsync(CancellationToken ct)
        {
            if (_registeredLobbyServerUrls.Count == 0 || _lobbyCodeClient == null)
                return;
                
            var tasks = new List<UniTask>();
            
            foreach (var lobbyServerUrl in _registeredLobbyServerUrls)
            {
                tasks.Add(SendHeartbeatToLobbyServerAsync(lobbyServerUrl, ct));
            }
            
            // Also refresh NAT punch registration (has 60s TTL on server)
            if (!string.IsNullOrEmpty(_natPunchLobbyServerUrl))
            {
                tasks.Add(RefreshNatPunchRegistrationAsync(ct));
            }
            
            await UniTask.WhenAll(tasks);
        }
        
        /// <summary>
        /// Refreshes the NAT punch registration to keep it alive.
        /// </summary>
        private async UniTask RefreshNatPunchRegistrationAsync(CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_natPunchLobbyServerUrl) || _currentLobbyId == Guid.Empty)
                return;
                
            try
            {
                var httpClient = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var localIp = NetworkAddressUtility.GetLocalLanAddress();
                var localEndpoint = $"{localIp}:{_natPunchGamePort}";
                
                var request = new PunchRegisterRequest(_currentLobbyId, localEndpoint, _natPunchGamePort);
                var json = System.Text.Json.JsonSerializer.Serialize(request);
                var content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");
                
                var uri = new Uri(new Uri(_natPunchLobbyServerUrl), "/api/punch/register");
                var response = await httpClient.PostAsync(uri, content, ct);
                
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    YargLogger.LogWarning($"[SessionManager] NAT punch heartbeat failed: {response.StatusCode} - {error}");
                }
                
                httpClient.Dispose();
            }
            catch (Exception ex)
            {
                YargLogger.LogWarning($"[SessionManager] NAT punch heartbeat error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Sends a heartbeat to a single lobby server by re-registering the lobby.
        /// </summary>
        private async UniTask SendHeartbeatToLobbyServerAsync(string lobbyServerUrl, CancellationToken ct)
        {
            try
            {
                bool success = await _lobbyCodeClient!.RegisterLobbyAsync(
                    lobbyServerUrl,
                    _currentLobbyId,
                    _cachedLobbyName ?? "YARG Lobby",
                    _cachedHostName ?? "Host",
                    _cachedHostAddress ?? "0.0.0.0",
                    _cachedHostPort,
                    _cachedMaxPlayers,
                    _cachedHasPassword,
                    ct).AsUniTask();
                    
                if (!success)
                {
                    YargLogger.LogWarning($"[SessionManager] Heartbeat failed for {lobbyServerUrl}");
                }
            }
            catch (Exception ex)
            {
                YargLogger.LogWarning($"[SessionManager] Heartbeat error for {lobbyServerUrl}: {ex.Message}");
            }
        }
        
        // ========== RELAY CONNECTION SUPPORT ==========
        
        /// <summary>
        /// Checks if relay is available for connecting to a lobby.
        /// </summary>
        /// <param name="lobbyServerUrl">The lobby server URL to check.</param>
        /// <returns>Relay info if available, null otherwise.</returns>
        public async UniTask<RelayInfo?> CheckRelayAvailableAsync(string lobbyServerUrl, CancellationToken ct = default)
        {
            try
            {
                var httpClient = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var uri = new Uri(new Uri(lobbyServerUrl), "/api/relay/info");
                
                var response = await httpClient.GetAsync(uri, ct);
                if (!response.IsSuccessStatusCode)
                {
                    httpClient.Dispose();
                    return null;
                }
                
                var json = await response.Content.ReadAsStringAsync();
                httpClient.Dispose();
                
                var info = System.Text.Json.JsonSerializer.Deserialize<RelayInfo>(json, 
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                
                return info?.Available == true ? info : null;
            }
            catch (Exception ex)
            {
                YargLogger.LogWarning($"[SessionManager] Relay check failed: {ex.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// Allocates a relay session for a lobby (host-side).
        /// </summary>
        public async UniTask<RelayAllocation?> AllocateRelaySessionAsync(Guid lobbyId, string lobbyServerUrl, CancellationToken ct = default)
        {
            try
            {
                var httpClient = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                
                var request = new { lobbyId };
                var content = new System.Net.Http.StringContent(
                    System.Text.Json.JsonSerializer.Serialize(request),
                    System.Text.Encoding.UTF8,
                    "application/json");
                
                var uri = new Uri(new Uri(lobbyServerUrl), "/api/relay/allocate");
                var response = await httpClient.PostAsync(uri, content, ct);
                
                if (!response.IsSuccessStatusCode)
                {
                    httpClient.Dispose();
                    YargLogger.LogWarning($"[SessionManager] Relay allocation failed: {response.StatusCode}");
                    return null;
                }
                
                var json = await response.Content.ReadAsStringAsync();
                httpClient.Dispose();
                
                var allocation = System.Text.Json.JsonSerializer.Deserialize<RelayAllocation>(json,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                
                if (allocation?.Success == true)
                {
                    YargLogger.LogInfo($"[SessionManager] Relay session allocated: {allocation.SessionId}");
                }
                
                return allocation;
            }
            catch (Exception ex)
            {
                YargLogger.LogError($"[SessionManager] Relay allocation error: {ex.Message}");
                return null;
            }
        }
    }
    
    // ========== RELAY DTOs ==========
    
    /// <summary>
    /// Relay server info from /api/relay/info
    /// </summary>
    [Serializable]
    public class RelayInfo
    {
        public bool Available { get; set; }
        public string? Address { get; set; }
        public int Port { get; set; }
        public string? Message { get; set; }
    }
    
    /// <summary>
    /// Relay session allocation result from /api/relay/allocate
    /// </summary>
    [Serializable]
    public class RelayAllocation
    {
        public bool Success { get; set; }
        public Guid SessionId { get; set; }
        public string? RelayAddress { get; set; }
        public int RelayPort { get; set; }
        public string? Message { get; set; }
    }

    /// <summary>
    /// Current state of a session.
    /// </summary>
    public enum SessionState
    {
        /// <summary>
        /// No session active.
        /// </summary>
        Stopped,

        /// <summary>
        /// Session is starting.
        /// </summary>
        Starting,

        /// <summary>
        /// Configuring UPnP port forwarding.
        /// </summary>
        ConfiguringUPnP,

        /// <summary>
        /// Registering with lobby server.
        /// </summary>
        RegisteringWithLobbyServer,

        /// <summary>
        /// Session is active and accepting connections.
        /// </summary>
        Active,

        /// <summary>
        /// Session failed to start.
        /// </summary>
        Failed
    }

    /// <summary>
    /// Result of starting a session.
    /// </summary>
    public sealed class SessionStartResult
    {
        public bool IsSuccess { get; }
        public bool UPnPSuccess { get; }
        public string? LobbyCode { get; }
        public string? Error { get; }
        public string? Message { get; }

        private SessionStartResult(bool isSuccess, bool upnpSuccess, string? lobbyCode, string? error, string? message)
        {
            IsSuccess = isSuccess;
            UPnPSuccess = upnpSuccess;
            LobbyCode = lobbyCode;
            Error = error;
            Message = message;
        }

        public static SessionStartResult Success(bool upnpSuccess, string? lobbyCode, string message) =>
            new(true, upnpSuccess, lobbyCode, null, message);

        public static SessionStartResult Failure(string error) =>
            new(false, false, null, error, null);
    }
}
