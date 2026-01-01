using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using YARG.Networking.Settings;
using YARG.Networking.UPnP;
using YARG.Net.Introducer;
using YARG.Net.Utilities;

namespace YARG.Networking.Session
{
    /// <summary>
    /// Manages session lifecycle based on session type (Server vs Lobby).
    /// Handles UPnP, lobby codes, and introducer registration automatically.
    /// </summary>
    public sealed class SessionLifecycleManager : MonoBehaviour
    {
        public static SessionLifecycleManager Instance { get; private set; }

        private UPnPPortForwarder? _upnpForwarder;
        private LobbyCodeClient? _lobbyCodeClient;
        private SessionPreset? _currentPreset;
        private string? _currentLobbyCode;
        private Guid _currentLobbyId;
        private CancellationTokenSource? _sessionCts;
        private CancellationTokenSource? _heartbeatCts;
        private bool _isSessionActive;
        
        // Heartbeat configuration - must be less than introducer TTL (30s)
        private const float HeartbeatIntervalSeconds = 15f;
        
        // Cached lobby info for heartbeats
        private string? _cachedLobbyName;
        private string? _cachedHostName;
        private string? _cachedHostAddress;
        private int _cachedHostPort;
        private int _cachedMaxPlayers;
        private bool _cachedHasPassword;
        
        /// <summary>
        /// List of introducer URLs that have the current lobby code registered.
        /// Used for cleanup to release the code from all introducers.
        /// </summary>
        private readonly List<string> _registeredIntroducerUrls = new();

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
                Debug.Log($"[SessionManager] Updated preset setting. allowLateJoin={_currentPreset.allowLateJoin}");
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
                Debug.Log("[SessionManager] Stopping existing session before starting new one");
                await StopAsync();
            }

            _sessionCts = new CancellationTokenSource();
            _currentPreset = preset;
            
            // Log preset details for debugging late join issues
            Debug.Log($"[SessionManager] StartHostingAsync: preset.allowLateJoin={preset?.allowLateJoin}, " +
                $"preset.sessionName={preset?.sessionName}, preset.id={preset?.id}");
            
            var ct = _sessionCts.Token;

            try
            {
                OnSessionStateChanged?.Invoke(SessionState.Starting);

                if (preset.SessionType == SessionType.Lobby)
                {
                    // Lobby flow: UPnP → Register with Introducer → Get code
                    return await StartLobbySessionAsync(preset, port, lobbyId, hostName, ct);
                }
                else
                {
                    // Server flow: No UPnP, no lobby code, optional introducer registration for discovery
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
                Debug.LogError($"[SessionManager] Failed to start session: {ex}");
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
        /// Tries all enabled introducers until one finds the code.
        /// </summary>
        /// <param name="code">The 6-character lobby code.</param>
        /// <returns>Connection info if successful.</returns>
        public async UniTask<LobbyLookupResult?> LookupLobbyCodeAsync(string code)
        {
            var settings = NetworkSettingsStore.Instance?.Settings;
            if (settings == null)
            {
                Debug.LogError("[SessionManager] NetworkSettingsStore not initialized");
                return null;
            }

            var enabledIntroducers = settings.EnabledIntroducers;
            if (enabledIntroducers == null || enabledIntroducers.Count == 0)
            {
                Debug.LogError("[SessionManager] No enabled introducers configured");
                return null;
            }

            _lobbyCodeClient ??= new LobbyCodeClient();

            // Try each introducer until we find the lobby
            foreach (var introducer in enabledIntroducers)
            {
                try
                {
                    var result = await _lobbyCodeClient.LookupCodeAsync(introducer.url, code).AsUniTask();
                    if (result.IsSuccess)
                    {
                        Debug.Log($"[SessionManager] Found lobby code {code} via {introducer.displayName}");
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[SessionManager] Error looking up code on {introducer.displayName}: {ex.Message}");
                }
            }
            
            Debug.LogWarning($"[SessionManager] Lobby code {code} not found on any introducer");
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
                }
            }

            if (!upnpSuccess)
            {
                Debug.LogWarning("[SessionManager] UPnP port forwarding failed. " +
                    "Players may not be able to connect unless you manually forward the port.");
            }

            // Step 2: Register with introducer(s)
            OnSessionStateChanged?.Invoke(SessionState.RegisteringWithIntroducer);

            var settings = NetworkSettingsStore.Instance?.Settings;
            var enabledIntroducers = settings?.EnabledIntroducers;

            if (enabledIntroducers == null || enabledIntroducers.Count == 0)
            {
                return SessionStartResult.Failure("No enabled introducers configured. Cannot create lobby code.");
            }

            // Step 3: Try to register lobby and generate code from each introducer until one succeeds
            _lobbyCodeClient ??= new LobbyCodeClient();
            _registeredIntroducerUrls.Clear();
            
            string lobbyCode = null;
            IntroducerEndpoint successfulIntroducer = null;
            var failedIntroducers = new List<(IntroducerEndpoint introducer, string error)>();
            
            // Get address to register - prefer external (UPnP) address, fall back to LAN address
            // Never use 0.0.0.0 because the introducer would use its own view of our IP (e.g., Docker gateway)
            string lanAddress = NetworkAddressUtility.GetLocalLanAddress();
            string registerAddress = externalAddress ?? lanAddress ?? "0.0.0.0";
            Debug.Log($"[SessionManager] Registering with address: {registerAddress} (external: {externalAddress ?? "null"}, lan: {lanAddress ?? "null"})");
            
            foreach (var introducer in enabledIntroducers)
            {
                Debug.Log($"[SessionManager] Trying introducer: {introducer.displayName} ({introducer.url})");
                
                try
                {
                    // First, register the lobby with this introducer
                    Debug.Log($"[SessionManager] Registering lobby with {introducer.displayName}...");
                    bool registered = await _lobbyCodeClient.RegisterLobbyAsync(
                        introducer.url,
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
                        Debug.LogWarning($"[SessionManager] Failed to register lobby with {introducer.displayName}");
                        failedIntroducers.Add((introducer, "Failed to register lobby"));
                        continue;
                    }
                    
                    Debug.Log($"[SessionManager] Lobby registered with {introducer.displayName}, generating code...");
                    
                    // Now generate the code
                    var codeResult = await _lobbyCodeClient.GenerateCodeAsync(introducer.url, lobbyId, ct).AsUniTask();
                    
                    if (codeResult.IsSuccess && !string.IsNullOrEmpty(codeResult.Code))
                    {
                        lobbyCode = codeResult.Code;
                        successfulIntroducer = introducer;
                        _registeredIntroducerUrls.Add(introducer.url);
                        Debug.Log($"[SessionManager] Successfully generated lobby code '{lobbyCode}' from {introducer.displayName}");
                        break;
                    }
                    else
                    {
                        var error = codeResult.Error ?? "Unknown error";
                        Debug.LogWarning($"[SessionManager] Introducer {introducer.displayName} failed: {error}");
                        failedIntroducers.Add((introducer, error));
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[SessionManager] Introducer {introducer.displayName} threw exception: {ex.Message}");
                    failedIntroducers.Add((introducer, ex.Message));
                }
            }
            
            // If all introducers failed, return error
            if (string.IsNullOrEmpty(lobbyCode))
            {
                var errorSummary = string.Join("; ", failedIntroducers.Select(f => $"{f.introducer.displayName}: {f.error}"));
                Debug.LogError($"[SessionManager] All {enabledIntroducers.Count} introducers failed to generate lobby code: {errorSummary}");
                return SessionStartResult.Failure($"All introducers failed. {errorSummary}");
            }

            _currentLobbyCode = lobbyCode;
            _currentLobbyId = lobbyId;
            
            OnLobbyCodeGenerated?.Invoke(_currentLobbyCode);
            Debug.Log($"[SessionManager] Lobby code generated: {_currentLobbyCode} (from {successfulIntroducer.displayName})");

            // Step 4: Disseminate the code to all OTHER enabled introducers (ones we haven't registered with yet)
            var otherIntroducers = enabledIntroducers.Where(i => i.url != successfulIntroducer.url).ToList();
            if (otherIntroducers.Count > 0 && !string.IsNullOrEmpty(externalAddress))
            {
                await DisseminateCodeToIntroducersAsync(
                    otherIntroducers,
                    _currentLobbyCode,
                    lobbyId,
                    externalAddress,
                    port,
                    ct);
            }
            else if (otherIntroducers.Count > 0)
            {
                Debug.LogWarning("[SessionManager] Could not get public address for code dissemination. " +
                    "Code will only be registered with the successful introducer.");
            }

            // Cache lobby info for heartbeats
            _cachedLobbyName = preset.sessionName ?? "YARG Lobby";
            _cachedHostName = hostName ?? "Host";
            _cachedHostAddress = registerAddress;
            _cachedHostPort = port;
            _cachedMaxPlayers = preset.maxPlayers;
            _cachedHasPassword = preset.HasPassword;
            
            // Start heartbeat loop to keep lobby alive on introducers
            StartHeartbeatLoop();

            _isSessionActive = true;
            OnSessionStateChanged?.Invoke(SessionState.Active);

            int registeredCount = _registeredIntroducerUrls.Count;
            int totalCount = enabledIntroducers.Count;
            string registrationMessage = registeredCount == totalCount
                ? $"Lobby code registered with all {totalCount} introducers"
                : $"Lobby code registered with {registeredCount} of {totalCount} introducers";

            return SessionStartResult.Success(
                upnpSuccess: upnpSuccess,
                lobbyCode: _currentLobbyCode,
                message: upnpSuccess
                    ? $"Lobby created successfully. {registrationMessage}."
                    : $"Lobby created, but UPnP failed. {registrationMessage}. Manual port forwarding may be required.");
        }
        
        /// <summary>
        /// Disseminates a lobby code to additional introducers so players using
        /// any of the same introducers can find each other.
        /// </summary>
        private async UniTask DisseminateCodeToIntroducersAsync(
            IReadOnlyList<IntroducerEndpoint> introducers,
            string code,
            Guid lobbyId,
            string hostAddress,
            int hostPort,
            CancellationToken ct)
        {
            var tasks = new List<UniTask<(IntroducerEndpoint introducer, bool success)>>();
            
            foreach (var introducer in introducers)
            {
                tasks.Add(RegisterCodeWithIntroducerAsync(introducer, code, lobbyId, hostAddress, hostPort, ct));
            }
            
            var results = await UniTask.WhenAll(tasks);
            
            foreach (var (introducer, success) in results)
            {
                if (success)
                {
                    _registeredIntroducerUrls.Add(introducer.url);
                    Debug.Log($"[SessionManager] Lobby code registered with {introducer.displayName}");
                }
                else
                {
                    Debug.LogWarning($"[SessionManager] Failed to register lobby code with {introducer.displayName}");
                }
            }
        }
        
        private async UniTask<(IntroducerEndpoint introducer, bool success)> RegisterCodeWithIntroducerAsync(
            IntroducerEndpoint introducer,
            string code,
            Guid lobbyId,
            string hostAddress,
            int hostPort,
            CancellationToken ct)
        {
            try
            {
                bool success = await _lobbyCodeClient!.RegisterCodeAsync(
                    introducer.url, 
                    code, 
                    lobbyId, 
                    hostAddress, 
                    hostPort, 
                    ct).AsUniTask();
                return (introducer, success);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SessionManager] Error registering code with {introducer.displayName}: {ex.Message}");
                return (introducer, false);
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
            Debug.Log($"[SessionManager] Starting server session (privacy: {preset.PrivacyMode}, registerWithIntroducers: {preset.registerWithIntroducers})");

            // For non-Unlisted servers that want to be discoverable, register with introducers
            // This allows the server to appear in the server browser without a lobby code
            bool shouldRegisterWithIntroducers = preset.registerWithIntroducers && 
                                                  preset.PrivacyMode != SessionPrivacyMode.Unlisted;
            
            if (shouldRegisterWithIntroducers)
            {
                OnSessionStateChanged?.Invoke(SessionState.RegisteringWithIntroducer);
                
                var settings = NetworkSettingsStore.Instance?.Settings;
                var enabledIntroducers = settings?.EnabledIntroducers;
                
                if (enabledIntroducers != null && enabledIntroducers.Count > 0)
                {
                    _lobbyCodeClient ??= new LobbyCodeClient();
                    _registeredIntroducerUrls.Clear();
                    _currentLobbyId = lobbyId;
                    
                    // Get LAN address for registration (no UPnP external address for servers)
                    string lanAddress = NetworkAddressUtility.GetLocalLanAddress() ?? "0.0.0.0";
                    Debug.Log($"[SessionManager] Registering server with introducers at {lanAddress}:{port}");
                    
                    // Cache for heartbeats
                    _cachedLobbyName = preset.sessionName ?? "YARG Server";
                    _cachedHostName = hostName ?? "Host";
                    _cachedHostAddress = lanAddress;
                    _cachedHostPort = port;
                    _cachedMaxPlayers = preset.maxPlayers;
                    _cachedHasPassword = preset.HasPassword;
                    
                    // Register with each introducer for discovery (no code generation)
                    foreach (var introducer in enabledIntroducers)
                    {
                        try
                        {
                            bool registered = await _lobbyCodeClient.RegisterLobbyAsync(
                                introducer.url,
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
                                _registeredIntroducerUrls.Add(introducer.url);
                                Debug.Log($"[SessionManager] Server registered with {introducer.displayName} for discovery");
                            }
                            else
                            {
                                Debug.LogWarning($"[SessionManager] Failed to register server with {introducer.displayName}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"[SessionManager] Error registering server with {introducer.displayName}: {ex.Message}");
                        }
                    }
                    
                    // Start heartbeat if we registered with any introducers
                    if (_registeredIntroducerUrls.Count > 0)
                    {
                        StartHeartbeatLoop();
                    }
                }
                else
                {
                    Debug.Log("[SessionManager] No introducers configured, server will only be discoverable on LAN");
                }
            }
            else
            {
                Debug.Log("[SessionManager] Server is Unlisted or doesn't want introducer registration - skipping");
            }

            _isSessionActive = true;
            OnSessionStateChanged?.Invoke(SessionState.Active);

            string message = shouldRegisterWithIntroducers && _registeredIntroducerUrls.Count > 0
                ? $"Server started and registered with {_registeredIntroducerUrls.Count} introducer(s). Ensure port forwarding is configured."
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
            
            // Release lobby code from all registered introducers
            if (!string.IsNullOrEmpty(_currentLobbyCode) && _lobbyCodeClient != null)
            {
                var releaseTasks = new List<UniTask>();
                
                foreach (var introducerUrl in _registeredIntroducerUrls)
                {
                    releaseTasks.Add(ReleaseCodeFromIntroducerAsync(introducerUrl, _currentLobbyCode));
                }
                
                if (releaseTasks.Count > 0)
                {
                    await UniTask.WhenAll(releaseTasks);
                    Debug.Log($"[SessionManager] Released lobby code {_currentLobbyCode} from {releaseTasks.Count} introducer(s)");
                }
                
                _registeredIntroducerUrls.Clear();
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
                    Debug.LogWarning($"[SessionManager] Failed to close UPnP port: {ex.Message}");
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
        
        private async UniTask ReleaseCodeFromIntroducerAsync(string introducerUrl, string code)
        {
            try
            {
                await _lobbyCodeClient!.ReleaseCodeAsync(introducerUrl, code).AsUniTask();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SessionManager] Failed to release lobby code from {introducerUrl}: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Starts the heartbeat loop to keep the lobby alive on all registered introducers.
        /// </summary>
        private void StartHeartbeatLoop()
        {
            StopHeartbeatLoop(); // Cancel any existing heartbeat
            
            _heartbeatCts = new CancellationTokenSource();
            HeartbeatLoopAsync(_heartbeatCts.Token).Forget();
            
            Debug.Log($"[SessionManager] Started heartbeat loop (interval: {HeartbeatIntervalSeconds}s)");
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
                Debug.Log("[SessionManager] Stopped heartbeat loop");
            }
        }
        
        /// <summary>
        /// The heartbeat loop that periodically sends updates to all registered introducers.
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
                    await SendHeartbeatToAllIntroducersAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[SessionManager] Heartbeat error: {ex.Message}");
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
        
        /// <summary>
        /// Sends heartbeat (re-register) to all registered introducers.
        /// </summary>
        private async UniTask SendHeartbeatToAllIntroducersAsync(CancellationToken ct)
        {
            if (_registeredIntroducerUrls.Count == 0 || _lobbyCodeClient == null)
                return;
                
            var tasks = new List<UniTask>();
            
            foreach (var introducerUrl in _registeredIntroducerUrls)
            {
                tasks.Add(SendHeartbeatToIntroducerAsync(introducerUrl, ct));
            }
            
            await UniTask.WhenAll(tasks);
        }
        
        /// <summary>
        /// Sends a heartbeat to a single introducer by re-registering the lobby.
        /// </summary>
        private async UniTask SendHeartbeatToIntroducerAsync(string introducerUrl, CancellationToken ct)
        {
            try
            {
                bool success = await _lobbyCodeClient!.RegisterLobbyAsync(
                    introducerUrl,
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
                    Debug.LogWarning($"[SessionManager] Heartbeat failed for {introducerUrl}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SessionManager] Heartbeat error for {introducerUrl}: {ex.Message}");
            }
        }
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
        /// Registering with introducer service.
        /// </summary>
        RegisteringWithIntroducer,

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
