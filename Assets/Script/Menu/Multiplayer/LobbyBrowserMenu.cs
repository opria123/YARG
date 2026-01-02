using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;
using TMPro;
using YARG.Core.Input;
using YARG.Core.Logging;
using YARG.Menu.ListMenu;
using YARG.Menu.Navigation;
using YARG.Menu.Data;
using YARG.Menu.Dialogs;
using YARG.Menu.Persistent;
using YARG.Networking;
using YARG.Networking.Abstraction;
using YARG.Networking.Bookmarks;
using YARG.Networking.Gameplay;
using YARG.Networking.Session;
using YARG.Networking.Settings;
using Cysharp.Threading.Tasks;
using YARG.Localization;

namespace YARG.Menu.Multiplayer
{
    /// <summary>
    /// Lobby browser menu using YARG's ListMenu pattern.
    /// Shows discovered lobbies with favorites support and live pinging for saved servers.
    /// Uses only the networking abstraction layer (no Mirror dependencies).
    /// </summary>
    public class LobbyBrowserMenu : ListMenu<LobbyViewType, LobbyView>
    {
        [Header("UI References")]
        [SerializeField]
        private TextMeshProUGUI _statusText;
        [SerializeField]
        private LobbyBrowserSidebar _sidebar;

        [Header("Navigation")]
        [SerializeField]
        private NavigationGroup _navigationGroup;

        private const double STALE_LOBBY_SECONDS = 18.0;
        private const float STALE_SWEEP_INTERVAL = 1.0f;
        private const float PING_STATUS_REFRESH_MIN_INTERVAL = 4.0f;
        private const float DISCOVERY_PING_INTERVAL = 3.0f;
        private const float DISCOVERY_BROADCAST_INTERVAL = 1.5f;
        private const float DISCOVERY_BURST_INTERVAL = 0.3f; // Fast initial bursts
        private const int DISCOVERY_BURST_COUNT = 3; // Number of rapid initial broadcasts
        private const int MAX_CONSECUTIVE_PROBE_FAILURES = 3; // Mark offline after 3 failed pings
        
        private int _discoveryBurstRemaining = 0;

        private LobbyFavorites _favorites;
        private List<LobbyInfo> _currentLobbies = new();
        private readonly List<int> _sectionStartIndices = new();
        private LobbyInfo _selectedLobby;
        private bool _navigationSchemePushed;
        private string _lastNavigationHelpSignature;
        private bool _isCreatingLobby;

        // Cache for ping results: endpointKey -> LobbyInfo (if online)
        private Dictionary<string, LobbyInfo> _pingedLobbies = new();
        private readonly Dictionary<string, int> _consecutiveProbeFailures = new();
        private HashSet<string> _pendingPings = new();
        private bool _isPingingSavedServers = false;
        private LobbyViewType _lastShownSidebarView;
        private float _nextStaleSweepAt;
        private float _nextPingStatusRefreshAt;
        private bool _pendingPingStatusRefresh;
        private CancellationTokenSource _pingCancellation;
        private float _lastPingStartedAt = float.NegativeInfinity;
        private float _nextAutomaticPingAt;
        private float _nextDiscoveryBroadcastAt;

        private bool _pendingPasswordSaveRequested;
        private string _pendingPasswordAddress;
        private int _pendingPasswordPort;
        private string _pendingPasswordDisplayName;
        private string _pendingPasswordValue;
        private bool _pendingIsServerSession; // Only servers should be saved to recents
        private readonly HashSet<string> _passwordFailures = new(StringComparer.OrdinalIgnoreCase);
        private LobbyInfo _lastPasswordAttemptLobby;
        private string _lastPasswordAttemptKey;
        private bool _lastPasswordAttemptWasAuto;

        protected override int ExtraListViewPadding => 15;

        private INetworkingService NetworkService => NetworkingServiceFactory.Instance;
        
        private int SuggestedDirectConnectPort => NetworkService?.DefaultPort ?? NetworkTransportDefaults.DefaultUdpPort;
        
        private string PlayerName => NetworkService?.PlayerName ?? "YARG";

        private bool EnsureSidebar()
        {
            if (_sidebar != null)
                return true;

            try
            {
                _sidebar = GetComponentInChildren<LobbyBrowserSidebar>(includeInactive: true);
                if (_sidebar != null)
                    return true;

                var sidebars = Resources.FindObjectsOfTypeAll<LobbyBrowserSidebar>();
                foreach (var candidate in sidebars)
                {
                    if (candidate == null)
                        continue;

                    var go = candidate.gameObject;
                    if (go == null || !go.scene.IsValid())
                        continue;

                    _sidebar = candidate;
                    Debug.LogWarning("[LobbyBrowserMenu] Sidebar reference was missing; auto-assigned to scene instance.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LobbyBrowserMenu] EnsureSidebar encountered an exception: {ex}");
            }

            Debug.LogWarning("[LobbyBrowserMenu] Sidebar reference is not assigned.");
            return false;
        }

        protected override void Awake()
        {
            _favorites = new LobbyFavorites();
            _favorites.OnFavoritesChanged += RefreshList;

            if (_navigationGroup == null)
            {
                _navigationGroup = GetComponent<NavigationGroup>() ?? gameObject.AddComponent<NavigationGroup>();
            }

            base.Awake();
        }

        private void OnDestroy()
        {
            if (_favorites != null)
            {
                _favorites.OnFavoritesChanged -= RefreshList;
                _favorites.Dispose();
                _favorites = null;
            }
        }

        private void OnEnable()
        {
            SetNavigationScheme();
            EnsureSidebar();

            // Subscribe to networking events through the abstraction layer
            if (NetworkService != null)
            {
                NetworkService.OnLobbyListUpdated += OnLobbyListUpdated;
                NetworkService.OnLobbyCreated += HandleLobbyCreated;
                NetworkService.OnLobbyJoined += HandleLobbyJoined;
                NetworkService.OnNetworkError += HandleNetworkError;
                
                // Start discovery
                NetworkService.StartDiscovery();
            }

            if (_sidebar != null)
            {
                _sidebar.Initialize(this);
                _sidebar.CreateLobbySubmitted += OnCreateLobbySubmitted;
                _sidebar.DirectConnectSubmitted += OnDirectConnectSubmitted;
                _sidebar.JoinByCodeSubmitted += OnJoinByCodeSubmitted;
                YargLogger.LogInfo("[LobbyBrowserMenu] Sidebar event subscriptions complete");
            }
            else
            {
                YargLogger.LogWarning("[LobbyBrowserMenu] _sidebar is null - cannot subscribe to events");
            }

            RefreshList(false);

            if (_navigationGroup != null)
            {
                _navigationGroup.PushNavGroupToStack();
                if (_navigationGroup.Count > 0 && (_navigationGroup.SelectedIndex == null || _navigationGroup.SelectedIndex < 0))
                {
                    _navigationGroup.SelectFirst();
                }
            }

            RefreshLobbies();

            _nextAutomaticPingAt = Time.unscaledTime + DISCOVERY_PING_INTERVAL;
            
            // Send first broadcast immediately and enable burst mode for rapid initial discovery
            _discoveryBurstRemaining = DISCOVERY_BURST_COUNT;
            _nextDiscoveryBroadcastAt = 0f; // Send immediately
            NetworkService?.SendBroadcastDiscoveryRequest();
            
            UniTask.Void(async () => await PingSavedServersAsync(force: true));

            _nextStaleSweepAt = Time.unscaledTime + STALE_SWEEP_INTERVAL;
        }

        private void OnDisable()
        {
            Debug.Log($"[LobbyBrowserMenu] OnDisable: _navigationSchemePushed={_navigationSchemePushed}");
            if (_navigationSchemePushed && Navigator.Instance != null)
            {
                Debug.Log("[LobbyBrowserMenu] OnDisable: Popping navigation scheme");
                Navigator.Instance.PopScheme();
                _navigationSchemePushed = false;
            }
            _lastNavigationHelpSignature = null;

            // Cancel any pending pings
            if (_pingCancellation != null)
            {
                try
                {
                    _pingCancellation.Cancel();
                }
                catch { }
                _pingCancellation.Dispose();
                _pingCancellation = null;
            }

            // Unsubscribe from networking events
            if (NetworkService != null)
            {
                NetworkService.OnLobbyListUpdated -= OnLobbyListUpdated;
                NetworkService.OnLobbyCreated -= HandleLobbyCreated;
                NetworkService.OnLobbyJoined -= HandleLobbyJoined;
                NetworkService.OnNetworkError -= HandleNetworkError;
                
                NetworkService.StopDiscovery();
            }

            ClearPendingPasswordUpdate();
            _lastPasswordAttemptLobby = null;
            _lastPasswordAttemptKey = null;
            _lastPasswordAttemptWasAuto = false;

            if (_sidebar != null)
            {
                _sidebar.CreateLobbySubmitted -= OnCreateLobbySubmitted;
                _sidebar.DirectConnectSubmitted -= OnDirectConnectSubmitted;
                _sidebar.JoinByCodeSubmitted -= OnJoinByCodeSubmitted;
                _sidebar.ClearLobby();
            }
            _selectedLobby = null;

            _nextStaleSweepAt = 0f;
        }

        protected override void OnSelectedIndexChanged()
        {
            base.OnSelectedIndexChanged();
            UpdateSidebarForSelection();
        }

        protected override List<LobbyViewType> CreateViewList()
        {
            var viewTypes = new List<LobbyViewType>();
            var discoveredLookup = new Dictionary<string, LobbyInfo>(StringComparer.Ordinal);

            foreach (var lobby in _currentLobbies)
            {
                if (!IsLobbyLive(lobby))
                    continue;

                void TryAdd(string address, int port)
                {
                    if (string.IsNullOrWhiteSpace(address) || port <= 0)
                        return;

                    string key = LobbyBookmarkUtility.BuildKey(address, port);
                    if (string.IsNullOrEmpty(key))
                        return;

                    if (!discoveredLookup.ContainsKey(key))
                        discoveredLookup[key] = lobby;
                }

                TryAdd(lobby.IpAddress, lobby.Port);
                TryAdd(lobby.PublicAddress, lobby.PublicPort);
            }

            var usedEndpointKeys = new HashSet<string>();
            foreach (var bookmark in _favorites.GetFavorites())
                usedEndpointKeys.Add(bookmark.EndpointKey);
            foreach (var bookmark in _favorites.GetRecents())
                usedEndpointKeys.Add(bookmark.EndpointKey);

            var favoritesSection = new List<LobbyViewType>();
            var favoriteEndpointKeys = new HashSet<string>();
            var usedAddresses = new HashSet<string>();

            foreach (var bookmark in _favorites.GetFavorites().OrderByDescending(b => discoveredLookup.ContainsKey(b.EndpointKey)))
            {
                string addressKey = NormalizeAddress(bookmark.address);
                if (string.IsNullOrEmpty(addressKey) || !usedAddresses.Add(addressKey))
                    continue;

                favoriteEndpointKeys.Add(bookmark.EndpointKey);
                usedEndpointKeys.Add(bookmark.EndpointKey);

                LobbyInfo liveInfo = null;
                if (discoveredLookup.TryGetValue(bookmark.EndpointKey, out var dl) && IsLobbyLive(dl))
                    liveInfo = dl;

                if ((liveInfo == null || !IsLobbyLive(liveInfo)) && _pingedLobbies.TryGetValue(bookmark.EndpointKey, out var pinged) && IsLobbyLive(pinged))
                    liveInfo = pinged;

                if (liveInfo != null && !IsLobbyLive(liveInfo))
                    liveInfo = null;

                favoritesSection.Add(new SavedLobbyViewType(bookmark, this, _favorites) { LiveInfo = liveInfo });
            }

            var recentsSection = new List<LobbyViewType>();
            // Sort recents by online status first (online before offline), then by lastConnected
            foreach (var bookmark in _favorites.GetRecents()
                .OrderByDescending(b => discoveredLookup.ContainsKey(b.EndpointKey) || (_pingedLobbies.TryGetValue(b.EndpointKey, out var pingedLobby) && IsLobbyLive(pingedLobby)))
                .ThenByDescending(entry => entry.lastConnected))
            {
                if (recentsSection.Count >= 5)
                    break;

                string addressKey = NormalizeAddress(bookmark.address);
                if (string.IsNullOrEmpty(addressKey) || usedAddresses.Contains(addressKey))
                    continue;

                // Skip if this entry is also in favorites (using EndpointKey for exact match)
                if (favoriteEndpointKeys.Contains(bookmark.EndpointKey))
                    continue;
                
                // Also skip if we've already used this address in favorites (handles IP vs hostname differences)
                usedAddresses.Add(addressKey);
                usedEndpointKeys.Add(bookmark.EndpointKey);

                LobbyInfo liveInfo = null;
                if (discoveredLookup.TryGetValue(bookmark.EndpointKey, out var dl2) && IsLobbyLive(dl2))
                    liveInfo = dl2;

                if ((liveInfo == null || !IsLobbyLive(liveInfo)) && _pingedLobbies.TryGetValue(bookmark.EndpointKey, out var pinged2) && IsLobbyLive(pinged2))
                    liveInfo = pinged2;

                if (liveInfo != null && !IsLobbyLive(liveInfo))
                    liveInfo = null;

                recentsSection.Add(new SavedLobbyViewType(bookmark, this, _favorites) { LiveInfo = liveInfo });
            }

            var myLobbiesSection = new List<LobbyViewType>();
            var myLobbies = LobbyBookmarkStore.Instance.MyLobbies;
            if (myLobbies != null && myLobbies.Count > 0)
            {
                foreach (var preset in myLobbies)
                {
                    if (preset == null)
                        continue;
                    myLobbiesSection.Add(new MyLobbyViewType(preset, this));
                }
            }

            var discoveredSection = new List<LobbyViewType>();
            foreach (var lobby in _currentLobbies.OrderByDescending(l => l.CurrentPlayers))
            {
                if (!IsLobbyLive(lobby))
                    continue;

                string endpointKey = LobbyBookmarkUtility.BuildKey(lobby.IpAddress, lobby.Port);
                if (usedEndpointKeys.Contains(endpointKey))
                    continue;
                discoveredSection.Add(new DiscoveredLobbyViewType(lobby, this, _favorites));
            }

            // Add action items at the top (Host/Join)
            viewTypes.Add(new LobbyCategoryViewType(Localize.Key("Menu", "LobbyBrowser", "SectionHostGame"), "SectionHostGame"));
            viewTypes.Add(new LobbyCategoryViewType(Localize.Key("Menu", "LobbyBrowser", "SectionJoinGame"), "SectionJoinGame"));

            viewTypes.Add(new LobbyCategoryViewType(Localize.Key("Menu", "LobbyBrowser", "SectionFavorites"), "SectionFavorites"));
            if (favoritesSection.Count > 0) viewTypes.AddRange(favoritesSection);
            else viewTypes.Add(new LobbyEmptyViewType(Localize.Key("Menu", "LobbyBrowser", "EmptyFavorites")));

            viewTypes.Add(new LobbyCategoryViewType(Localize.Key("Menu", "LobbyBrowser", "SectionMySessions"), "SectionMySessions"));
            if (myLobbiesSection.Count > 0) viewTypes.AddRange(myLobbiesSection);
            else viewTypes.Add(new LobbyEmptyViewType(Localize.Key("Menu", "LobbyBrowser", "EmptyMySessions")));

            viewTypes.Add(new LobbyCategoryViewType(Localize.Key("Menu", "LobbyBrowser", "SectionRecents"), "SectionRecents"));
            if (recentsSection.Count > 0) viewTypes.AddRange(recentsSection);
            else viewTypes.Add(new LobbyEmptyViewType(Localize.Key("Menu", "LobbyBrowser", "EmptyRecents")));

            viewTypes.Add(new LobbyCategoryViewType(Localize.Key("Menu", "LobbyBrowser", "SectionDiscovered"), "SectionDiscovered"));
            if (discoveredSection.Count > 0) viewTypes.AddRange(discoveredSection);
            else viewTypes.Add(new LobbyEmptyViewType(Localize.Key("Menu", "LobbyBrowser", "EmptyDiscovered")));

            RebuildSectionCache(viewTypes);
            UpdateStatusText(favoritesSection.Count, myLobbiesSection.Count, recentsSection.Count, discoveredSection.Count);

            return viewTypes;
        }

        private static string NormalizeAddress(string address) => string.IsNullOrWhiteSpace(address) ? string.Empty : address.Trim().ToLowerInvariant();

        private void RefreshList() => RefreshListInternal(true);
        private void RefreshList(bool keepSection) => RefreshListInternal(keepSection);

        private void RefreshListInternal(bool keepSection)
        {
            // Don't refresh if this menu is disabled (e.g., we're in LobbyRoomMenu)
            if (!gameObject.activeInHierarchy)
                return;
            
            string currentSelectionKey = CurrentSelection?.GetSelectionKey();
            int previousSection = keepSection ? GetSectionIndexFor(SelectedIndex) : 0;
            int priorSelectedIndex = SelectedIndex;
            RequestViewListUpdate();

            var views = ViewList;
            if (views == null || views.Count == 0)
            {
                SelectedIndex = 0;
                UpdateSidebarForSelection();
                return;
            }

            if (SelectedIndex >= views.Count) SelectedIndex = views.Count - 1;

            if (!string.IsNullOrEmpty(currentSelectionKey))
            {
                for (int i = 0; i < views.Count; i++)
                {
                    try
                    {
                        if (string.Equals(views[i].GetSelectionKey(), currentSelectionKey, StringComparison.Ordinal))
                        {
                            SelectedIndex = i;
                            UpdateSidebarForSelection();
                            return;
                        }
                    }
                    catch { }
                }
            }

            if (string.IsNullOrEmpty(currentSelectionKey) && keepSection)
            {
                if (priorSelectedIndex >= 0 && priorSelectedIndex < views.Count)
                {
                    SelectedIndex = priorSelectedIndex;
                    UpdateSidebarForSelection();
                    return;
                }
            }

            if (keepSection && _sectionStartIndices.Count > 0)
            {
                if (!SelectFirstSelectableInSection(previousSection)) SelectFirstSelectableInRange(0, views.Count);
            }
            else if (!IsSelectable(CurrentSelection))
            {
                if (!SelectFirstSelectableInRange(0, views.Count)) SelectedIndex = 0;
            }

            UpdateSidebarForSelection();
        }

        /// <summary>
        /// Returns true if a specific endpoint is currently being scanned/pinged.
        /// Used by SavedLobbyViewType to show "Scanning..." state.
        /// </summary>
        public bool IsEndpointBeingScanned(string endpointKey)
        {
            if (string.IsNullOrEmpty(endpointKey))
                return false;
            return _pendingPings.Contains(endpointKey) || (_discoveryBurstRemaining > 0);
        }

        /// <summary>
        /// Returns the number of consecutive probe failures for an endpoint.
        /// Used to determine if a lobby should be shown as offline.
        /// </summary>
        public int GetProbeFailureCount(string endpointKey)
        {
            if (string.IsNullOrEmpty(endpointKey))
                return 0;
            return _consecutiveProbeFailures.TryGetValue(endpointKey, out var count) ? count : 0;
        }

        public void RefreshLobbies()
        {
            if (_statusText != null) _statusText.text = Localize.Key("Menu", "LobbyBrowser", "SearchingForLobbies");
            
            // Trigger discovery refresh through abstraction layer
            NetworkService?.StartDiscovery();
        }

        private void OnLobbyListUpdated(List<LobbyInfo> lobbies)
        {
            // Store lobbies even if disabled, but don't update UI
            _currentLobbies = lobbies ?? new List<LobbyInfo>();
            
            // Don't update UI if this menu is disabled (e.g., we're in LobbyRoomMenu)
            if (!gameObject.activeInHierarchy)
                return;
            
            if (_statusText != null)
            {
                if (lobbies.Count == 0) _statusText.text = Localize.Key("Menu", "LobbyBrowser", "NoLobbiesFound");
                else
                {
                    int favoriteCount = lobbies.Count(l => _favorites.IsFavorited(l.IpAddress, l.Port));
                    string lobbyWord = Localize.Key("Menu", "LobbyBrowser", lobbies.Count == 1 ? "Lobby" : "Lobbies");
                    string status = Localize.KeyFormat(("Menu", "LobbyBrowser", "LobbiesFound"), lobbies.Count, lobbyWord);
                    if (favoriteCount > 0)
                    {
                        string favoriteWord = Localize.Key("Menu", "LobbyBrowser", favoriteCount == 1 ? "Favorite" : "Favorites");
                        string suffix = Localize.KeyFormat(("Menu", "LobbyBrowser", "FavoritesSuffix"), favoriteCount, favoriteWord);
                        status = string.Concat(status, " (", suffix, ")");
                    }
                    _statusText.text = status;
                }
            }

            RefreshList();
            UniTask.Void(async () => await PingSavedServersAsync());
        }

        private CancellationTokenSource _createLobbyCts;

        private void OnCreateLobbySubmitted(LobbyBrowserSidebar.CreateLobbyFormData data)
        {
            // Prevent double-clicks while creation is in progress
            if (_isCreatingLobby)
            {
                Debug.LogWarning("[LobbyBrowserMenu] Lobby creation already in progress, ignoring duplicate request");
                return;
            }
            
            // Fire and forget async lobby creation
            CreateLobbyAsync(data).Forget();
        }

        private async UniTaskVoid CreateLobbyAsync(LobbyBrowserSidebar.CreateLobbyFormData data)
        {
            _isCreatingLobby = true;
            _createLobbyCts = new CancellationTokenSource();
            
            // Validate that connected profiles aren't blocked by the lobby's game mode restrictions
            // Do this BEFORE showing the dialog or doing any work
            if (data.AllowedInstruments != null && data.AllowedInstruments.Count > 0 && NetworkService != null)
            {
                var gameModeBlacklist = new List<YARG.Core.GameMode>();
                foreach (var mode in data.AllowedInstruments)
                {
                    if (mode >= 0 && mode <= 255 && Enum.IsDefined(typeof(YARG.Core.GameMode), (byte)mode))
                    {
                        gameModeBlacklist.Add((YARG.Core.GameMode)mode);
                    }
                }
                
                if (!NetworkService.ValidateProfilesAgainstGameModes(gameModeBlacklist, out var blockedProfiles))
                {
                    string blockedList = string.Join(", ", blockedProfiles);
                    Debug.LogWarning($"[LobbyBrowserMenu] Cannot create lobby - blocked profiles: {blockedList}");
                    ToastManager.ToastWarning($"Cannot create lobby: {blockedList} uses a disabled game mode.");
                    _isCreatingLobby = false;
                    return;
                }
            }
            
            // Show a modal dialog with cancel option
            MessageDialog dialog = null;
            if (DialogManager.Instance != null)
            {
                dialog = DialogManager.Instance.ShowMessage("Creating Lobby", "Setting up lobby...\nThis may take a moment.");
                dialog.ClearButtons();
                dialog.AddDialogButton("Cancel", MenuData.Colors.CancelButton, () =>
                {
                    Debug.Log("[LobbyBrowserMenu] Lobby creation cancelled by user");
                    _createLobbyCts?.Cancel();
                    DialogManager.Instance?.ClearDialog();
                });
            }
            
            try
            {
                var ct = _createLobbyCts.Token;
                
                Debug.Log($"[LobbyBrowserMenu] CreateLobbyAsync: data.AllowLateJoin={data.AllowLateJoin}");
                
                var store = LobbyBookmarkStore.Instance;
                var preset = store.UpsertMyLobby(
                    data.PresetId, 
                    data.LobbyName, 
                    data.MaxPlayers, 
                    data.PrivacyMode, 
                    data.SessionType,
                    data.Password, 
                    true,
                    data.BandSize,
                    data.NoFailMode,
                    data.SharedSongsOnly,
                    data.AllowModifiers,
                    data.EnablePresetSync,
                    data.AllowLateJoin,
                    data.AllowedInstruments ?? new List<int>(),
                    data.LocalPlayersFirst);
                
                Debug.Log($"[LobbyBrowserMenu] UpsertMyLobby returned preset.allowLateJoin={preset?.allowLateJoin}");
                    
                if (_sidebar != null)
                    _sidebar.ShowHostedLobbyPreset(preset);

                // Apply gameplay settings from preset
                ApplyGameplaySettingsFromPreset(preset);

                // Create a SessionPreset for the SessionLifecycleManager
                var sessionPreset = CreateSessionPresetFromHosted(preset);
                
                // Get the port from the preset (or fallback to default) and create lobby ID
                int port = sessionPreset.port > 0 ? sessionPreset.port : (NetworkService?.DefaultPort ?? 7777);
                Guid lobbyId = Guid.NewGuid();
                
                YargLogger.LogInfo($"[LobbyBrowserMenu] Starting session with port {port} (preset.port={sessionPreset.port})");
                
                // Update dialog status
                if (dialog != null)
                {
                    dialog.Message.text = "Configuring network settings...";
                }
                
                // Start the session lifecycle (UPnP for Lobby mode, nothing for Server mode)
                string lobbyCode = null;
                if (SessionLifecycleManager.Instance == null)
                {
                    Debug.LogError("[LobbyBrowserMenu] CRITICAL: SessionLifecycleManager.Instance is null! " +
                        "This indicates networking was not initialized properly. " +
                        $"NetworkManagersBootstrap.IsInitialized={NetworkManagersBootstrap.IsInitialized}");
                    
                    if (data.SessionType == SessionType.Lobby)
                    {
                        DialogManager.Instance?.ClearDialog();
                        ToastManager.ToastError("Networking not initialized. Please restart the game.");
                        _isCreatingLobby = false;
                        return;
                    }
                }
                
                if (SessionLifecycleManager.Instance != null)
                {
                    ct.ThrowIfCancellationRequested();
                    var result = await SessionLifecycleManager.Instance.StartHostingAsync(sessionPreset, port, lobbyId, PlayerName);
                    
                    ct.ThrowIfCancellationRequested();
                    if (!result.IsSuccess)
                    {
                        Debug.LogWarning($"[LobbyBrowserMenu] Session lifecycle failed: {result.Error}");
                        // For Server mode, this is fine - user handles port forwarding
                        // For Lobby mode, show an error and abort
                        if (data.SessionType == SessionType.Lobby)
                        {
                            Debug.LogError($"[LobbyBrowserMenu] Failed to start lobby session: {result.Error}");
                            DialogManager.Instance?.ClearDialog();
                            ToastManager.ToastError($"Failed to create lobby: {result.Error}");
                            _isCreatingLobby = false;
                            return;
                        }
                    }
                    else
                    {
                        lobbyCode = result.LobbyCode;
                        if (!string.IsNullOrEmpty(lobbyCode))
                        {
                            Debug.Log($"[LobbyBrowserMenu] Lobby code generated: {lobbyCode}");
                        }
                        
                        // Log network setup info (for debugging), but don't show toast for normal UPnP failure
                        // since NAT punch-through should handle most cases
                        if (data.SessionType == SessionType.Lobby && !result.UPnPSuccess)
                        {
                            // Only log - NAT punch-through is the primary connection method now
                            Debug.LogWarning($"[LobbyBrowserMenu] Network setup warning: {result.Message}");
                            // Only show toast for truly problematic cases (no public IP at all)
                            if (result.Message?.Contains("Could not determine public IP") == true)
                            {
                                ToastManager.ToastWarning("Could not determine public IP. Only local network players can connect.");
                            }
                        }
                    }
                }
                
                // Update dialog before creating local lobby
                if (dialog != null)
                {
                    dialog.Message.text = "Starting lobby...";
                }
                ct.ThrowIfCancellationRequested();

                // IMPORTANT: Set the server port BEFORE creating the lobby
                // This ensures the NetworkService uses the correct port from the preset
                NetworkService?.SetServerPort(port);

                var password = preset.PrivacyMode == LobbyPrivacyMode.Private ? preset.password ?? string.Empty : string.Empty;
                var lobby = NetworkService?.CreateLobby(preset.lobbyName, preset.maxPlayers, preset.PrivacyMode, preset.SessionType, password);
                
                // Store the lobby code on the LobbyInfo if we got one
                if (lobby != null && !string.IsNullOrEmpty(lobbyCode))
                {
                    lobby.LobbyCode = lobbyCode;
                }
                
                // IMPORTANT: Start NAT punch keepalive NOW that the transport is running.
                // This sends UDP packets to the punch server to create NAT mapping so clients can connect.
                if (lobby != null)
                {
                    SessionLifecycleManager.Instance?.StartPunchKeepalive();
                }
                
                // CRITICAL: Close the dialog AFTER creating the lobby but BEFORE navigating!
                // Dialog.OnDisable pops from the navigation stack, and we need to ensure
                // the stack is in the correct state before pushing LobbyRoomMenu's scheme.
                if (DialogManager.Instance != null && DialogManager.Instance.IsDialogShowing)
                {
                    DialogManager.Instance.ClearDialog();
                    dialog = null;  // Mark as cleared so finally doesn't try again
                }
                
                // Navigate to lobby room explicitly (don't rely solely on HandleLobbyCreated callback)
                // This ensures navigation happens even if there's a timing issue with the event
                if (lobby != null && MenuManager.Instance != null)
                {
                    Debug.Log($"[LobbyBrowserMenu] Lobby created, navigating to LobbyRoom (explicit)");
                    MenuManager.Instance.PushMenu(MenuManager.Menu.LobbyRoom);
                }
                
                // Lobby code is displayed in the LobbyRoom UI - no need for toast
            }
            catch (OperationCanceledException)
            {
                Debug.Log("[LobbyBrowserMenu] Lobby creation was cancelled");
                ToastManager.ToastInformation("Lobby creation cancelled");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LobbyBrowserMenu] Failed to create lobby: {ex}");
                ToastManager.ToastError($"Failed to create lobby: {ex.Message}");
            }
            finally
            {
                // Clean up dialog if still showing
                if (DialogManager.Instance != null && DialogManager.Instance.IsDialogShowing)
                {
                    DialogManager.Instance.ClearDialog();
                }
                
                _createLobbyCts?.Dispose();
                _createLobbyCts = null;
                _isCreatingLobby = false;
            }
        }
        
        private SessionPreset CreateSessionPresetFromHosted(HostedLobbyPreset preset)
        {
            Debug.Log($"[LobbyBrowserMenu] CreateSessionPresetFromHosted: preset.allowLateJoin={preset?.allowLateJoin}, " +
                $"preset.lobbyName={preset?.lobbyName}, preset.id={preset?.id}");
            
            return new SessionPreset
            {
                id = preset.id,
                presetName = preset.lobbyName,
                sessionName = preset.lobbyName,
                sessionType = (int)preset.SessionType,
                maxPlayers = preset.maxPlayers,
                privacyMode = preset.privacyMode,
                password = preset.password,
                bandSize = preset.bandSize,
                noFailMode = preset.noFailMode,
                sharedSongsOnly = preset.sharedSongsOnly,
                allowModifiers = preset.allowModifiers,
                visibleOnLan = preset.VisibleOnLan,
                registerWithIntroducers = preset.RegisterWithIntroducers,
                enablePresetSync = preset.enablePresetSync,
                allowLateJoin = preset.allowLateJoin,
                localPlayersFirstValue = preset.localPlayersFirst,
            };
        }

        private void ApplyGameplaySettingsFromPreset(HostedLobbyPreset preset)
        {
            if (preset == null)
                return;

            // Create a SessionPreset from the HostedLobbyPreset to apply gameplay settings
            // Note: VisibleOnLan and RegisterWithIntroducers are derived from PrivacyMode
            // IMPORTANT: Preserve the preset ID so settings can be saved back later
            var sessionPreset = new SessionPreset
            {
                id = preset.id, // Preserve the ID for later saving
                presetName = preset.lobbyName,
                sessionName = preset.lobbyName,
                sessionType = preset.sessionType, // Preserve session type (Server vs Lobby)
                maxPlayers = preset.maxPlayers,
                privacyMode = preset.privacyMode,
                password = preset.password,
                bandSize = preset.bandSize,
                noFailMode = preset.noFailMode,
                sharedSongsOnly = preset.sharedSongsOnly,
                allowModifiers = preset.allowModifiers,
                visibleOnLan = preset.VisibleOnLan,
                registerWithIntroducers = preset.RegisterWithIntroducers,
                enablePresetSync = preset.enablePresetSync,
                allowLateJoin = preset.allowLateJoin,
                localPlayersFirstValue = preset.localPlayersFirst,
            };

            // Apply to MultiplayerGameplaySettings if available
            if (MultiplayerGameplaySettings.Instance != null)
            {
                MultiplayerGameplaySettings.Instance.ApplyPreset(sessionPreset);
                Debug.Log($"[LobbyBrowserMenu] Applied gameplay settings: BandSize={preset.bandSize}, NoFail={preset.noFailMode}, SharedSongs={preset.sharedSongsOnly}, AllowMods={preset.allowModifiers}, VisibleOnLan={preset.VisibleOnLan}, Introducers={preset.RegisterWithIntroducers}, PresetSync={preset.enablePresetSync}, LateJoin={preset.allowLateJoin}, LocalPlayersFirst={preset.localPlayersFirst}");
            }
        }

        private void OnDirectConnectSubmitted(LobbyBrowserSidebar.DirectConnectFormData data)
        {
            string address = data.Address?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(address))
            {
                Debug.LogWarning("[LobbyBrowserMenu] Direct connect submission missing address.");
                return;
            }

            string displayName = string.IsNullOrWhiteSpace(data.DisplayName) ? address : data.DisplayName.Trim();
            LobbyBookmarkStore.Instance.RecordConnection(address, data.Port, displayName, data.Password ?? string.Empty);

            string endpoint;
            try
            {
                endpoint = EndpointUtility.FormatEndpoint(address, data.Port);
            }
            catch (Exception)
            {
                endpoint = string.Concat(address, ":", data.Port);
            }

            try
            {
                NetworkService?.JoinLobby(endpoint, data.Password ?? string.Empty);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LobbyBrowserMenu] Direct connect join failed: {ex}");
            }
        }
        
        private void OnJoinByCodeSubmitted(string code)
        {
            YargLogger.LogInfo($"[LobbyBrowserMenu] OnJoinByCodeSubmitted received code: {code}");
            // Fire and forget async lookup
            JoinByCodeAsync(code).Forget();
        }
        
        private async UniTaskVoid JoinByCodeAsync(string code)
        {
            try
            {
                YargLogger.LogInfo($"[LobbyBrowserMenu] Looking up lobby code: {code}");
                
                if (SessionLifecycleManager.Instance == null)
                {
                    YargLogger.LogError("[LobbyBrowserMenu] SessionLifecycleManager.Instance is null!");
                    ToastManager.ToastError("Networking not initialized");
                    return;
                }
                
                YargLogger.LogInfo("[LobbyBrowserMenu] Calling LookupLobbyCodeAsync...");
                var result = await SessionLifecycleManager.Instance.LookupLobbyCodeAsync(code);
                YargLogger.LogInfo($"[LobbyBrowserMenu] LookupLobbyCodeAsync returned: IsSuccess={(result?.IsSuccess ?? false)}, Error={result?.Error ?? "null"}");
                
                if (result == null || !result.IsSuccess || result.Lobby == null)
                {
                    string errorMsg = result?.Error ?? "Invalid or expired code";
                    YargLogger.LogFormatWarning("[LobbyBrowserMenu] Code lookup failed: {0}", errorMsg);
                    ToastManager.ToastError(errorMsg);
                    return;
                }
                
                var lobby = result.Lobby;
                YargLogger.LogInfo($"[LobbyBrowserMenu] Found lobby: {lobby.LobbyName} at {lobby.Address}:{lobby.Port} (hasPassword={lobby.HasPassword})");
                
                string endpoint = $"{lobby.Address}:{lobby.Port}";
                
                // If lobby requires a password, prompt the user
                if (lobby.HasPassword)
                {
                    YargLogger.LogInfo("[LobbyBrowserMenu] Lobby requires password, showing dialog");
                    ShowPasswordDialogForCodeJoin(lobby.LobbyName, endpoint, lobby.LobbyId, result.IntroducerUrl);
                    return;
                }
                
                ToastManager.ToastInformation($"Connecting to {lobby.LobbyName}...");
                
                // Try NAT punch-through first (if we have the introducer URL)
                string connectEndpoint = endpoint;
                if (!string.IsNullOrEmpty(result.IntroducerUrl))
                {
                    YargLogger.LogInfo($"[LobbyBrowserMenu] Attempting NAT punch-through via {result.IntroducerUrl}...");
                    ToastManager.ToastInformation("Establishing connection...");
                    
                    var punchedEndpoint = await SessionLifecycleManager.Instance.InitiateNatPunchAsync(
                        lobby.LobbyId, result.IntroducerUrl, 5000);
                    
                    if (punchedEndpoint != null)
                    {
                        connectEndpoint = $"{punchedEndpoint.Address}:{punchedEndpoint.Port}";
                        YargLogger.LogInfo($"[LobbyBrowserMenu] NAT punch succeeded! Connecting to {connectEndpoint}");
                        ToastManager.ToastInformation("NAT punch succeeded!");
                    }
                    else
                    {
                        YargLogger.LogWarning("[LobbyBrowserMenu] NAT punch failed, trying direct connection...");
                    }
                }
                
                // Join via the networking service
                if (NetworkService != null)
                {
                    YargLogger.LogInfo($"[LobbyBrowserMenu] Joining lobby at {connectEndpoint}");
                    NetworkService.JoinLobby(connectEndpoint, string.Empty);
                    _sidebar?.ClearLobbyCodeInput();
                }
                else
                {
                    YargLogger.LogError("[LobbyBrowserMenu] NetworkService is null!");
                    ToastManager.ToastError("Networking service not available");
                }
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex, "[LobbyBrowserMenu] Error joining by code");
                ToastManager.ToastError($"Error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Shows a password dialog for joining a lobby via code.
        /// </summary>
        private void ShowPasswordDialogForCodeJoin(string lobbyName, string endpoint, Guid lobbyId, string? introducerUrl)
        {
            if (DialogManager.Instance == null)
            {
                Debug.LogWarning("[LobbyBrowserMenu] Password dialog requested but DialogManager is unavailable.");
                return;
            }

            var dialog = DialogManager.Instance.ShowRenameDialog("Password Required", value =>
            {
                var submitted = (value ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(submitted))
                    return;

                Debug.Log($"[LobbyBrowserMenu] Joining {lobbyName} at {endpoint} with password");
                ToastManager.ToastInformation($"Connecting to {lobbyName}...");
                
                // Try NAT punch then connect with password
                JoinWithPasswordAndPunchAsync(lobbyName, endpoint, submitted, lobbyId, introducerUrl).Forget();
            });

            dialog.AllowEmpty = false;
            dialog.SetInitialText(string.Empty, false);

            var inputField = dialog.GetComponentInChildren<TMP_InputField>(true);
            if (inputField != null)
            {
                inputField.contentType = TMP_InputField.ContentType.Password;
                inputField.lineType = TMP_InputField.LineType.SingleLine;
                inputField.text = string.Empty;
                if (inputField.placeholder is TMP_Text placeholderText)
                    placeholderText.text = "Enter lobby password";
                inputField.Select();
                inputField.ActivateInputField();
            }
        }
        
        /// <summary>
        /// Joins a lobby with password, trying NAT punch first.
        /// </summary>
        private async UniTaskVoid JoinWithPasswordAndPunchAsync(string lobbyName, string endpoint, string password, Guid lobbyId, string? introducerUrl)
        {
            try
            {
                string connectEndpoint = endpoint;
                
                // Try NAT punch-through first if we have the introducer URL
                if (!string.IsNullOrEmpty(introducerUrl) && SessionLifecycleManager.Instance != null)
                {
                    YargLogger.LogInfo($"[LobbyBrowserMenu] Attempting NAT punch-through via {introducerUrl}...");
                    ToastManager.ToastInformation("Establishing connection...");
                    
                    var punchedEndpoint = await SessionLifecycleManager.Instance.InitiateNatPunchAsync(
                        lobbyId, introducerUrl, 5000);
                    
                    if (punchedEndpoint != null)
                    {
                        connectEndpoint = $"{punchedEndpoint.Address}:{punchedEndpoint.Port}";
                        YargLogger.LogInfo($"[LobbyBrowserMenu] NAT punch succeeded! Connecting to {connectEndpoint}");
                        ToastManager.ToastInformation("NAT punch succeeded!");
                    }
                    else
                    {
                        YargLogger.LogWarning("[LobbyBrowserMenu] NAT punch failed, trying direct connection...");
                    }
                }
                
                if (NetworkService != null)
                {
                    NetworkService.JoinLobby(connectEndpoint, password);
                    _sidebar?.ClearLobbyCodeInput();
                }
                else
                {
                    ToastManager.ToastError("Networking service not available");
                }
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex, "[LobbyBrowserMenu] Error joining with password");
                ToastManager.ToastError($"Error: {ex.Message}");
            }
        }

        public void JoinLobby(LobbyInfo lobby)
        {
            if (lobby == null)
                return;

            if (lobby.HasPassword)
            {
                if (TryAutoJoinWithStoredPassword(lobby))
                    return;

                ShowPasswordDialog(lobby);
                return;
            }

            _lastPasswordAttemptWasAuto = false;
            JoinLobbyWithPassword(lobby, string.Empty);
        }

        private void ShowPasswordDialog(LobbyInfo lobby)
        {
            if (DialogManager.Instance == null)
            {
                Debug.LogWarning("[LobbyBrowserMenu] Password dialog requested but DialogManager is unavailable.");
                return;
            }

            var dialog = DialogManager.Instance.ShowRenameDialog("Password Required", value =>
            {
                var submitted = (value ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(submitted))
                    return;

                _lastPasswordAttemptWasAuto = false;
                JoinLobbyWithPassword(lobby, submitted);
            });

            dialog.AllowEmpty = false;
            dialog.SetInitialText(string.Empty, false);

            var inputField = dialog.GetComponentInChildren<TMP_InputField>(true);
            if (inputField != null)
            {
                inputField.contentType = TMP_InputField.ContentType.Password;
                inputField.lineType = TMP_InputField.LineType.SingleLine;
                inputField.text = string.Empty;
                if (inputField.placeholder is TMP_Text placeholderText)
                    placeholderText.text = "Enter password";
                inputField.Select();
                inputField.ActivateInputField();
            }
        }

        private void JoinLobbyWithPassword(LobbyInfo lobby, string password)
        {
            TrackPasswordSubmission(lobby, password);
            NetworkService?.JoinDiscoveredLobby(lobby, password);
        }

        private bool TryAutoJoinWithStoredPassword(LobbyInfo lobby)
        {
            if (lobby == null || _favorites == null)
                return false;

            int port = ResolveLobbyPort(lobby);
            LobbyBookmark bookmark = null;

            if (!string.IsNullOrWhiteSpace(lobby.IpAddress))
                bookmark = _favorites.FindBookmark(lobby.IpAddress, port);

            if (bookmark == null && !string.IsNullOrWhiteSpace(lobby.PublicAddress))
                bookmark = _favorites.FindBookmark(lobby.PublicAddress, port);

            if (bookmark == null)
                return false;

            string storedPassword = bookmark.password;
            if (string.IsNullOrWhiteSpace(storedPassword))
                return false;

            string address = !string.IsNullOrWhiteSpace(bookmark.address)
                ? bookmark.address.Trim()
                : (!string.IsNullOrWhiteSpace(lobby.IpAddress) ? lobby.IpAddress.Trim() : lobby.PublicAddress?.Trim());

            int finalPort = bookmark.port > 0 ? bookmark.port : port;

            if (string.IsNullOrWhiteSpace(address))
                return false;

            string endpointKey = LobbyBookmarkUtility.BuildKey(address, finalPort);
            if (_passwordFailures.Contains(endpointKey))
                return false;

            _lastPasswordAttemptWasAuto = true;
            JoinLobbyWithPassword(lobby, storedPassword);
            return true;
        }

        private void TrackPasswordSubmission(LobbyInfo lobby, string password)
        {
            if (lobby == null || string.IsNullOrWhiteSpace(password))
            {
                ClearPendingPasswordUpdate();
                return;
            }

            lobby.HasPassword = true;
            lobby.Password = password;

            int port = ResolveLobbyPort(lobby);

            LobbyBookmark matchingBookmark = null;
            if (_favorites != null)
            {
                if (!string.IsNullOrWhiteSpace(lobby.IpAddress))
                    matchingBookmark = _favorites.FindBookmark(lobby.IpAddress, port);

                if (matchingBookmark == null && !string.IsNullOrWhiteSpace(lobby.PublicAddress))
                    matchingBookmark = _favorites.FindBookmark(lobby.PublicAddress, port);
            }

            if (matchingBookmark != null)
            {
                _pendingPasswordAddress = matchingBookmark.address?.Trim();
                _pendingPasswordPort = matchingBookmark.port > 0 ? matchingBookmark.port : port;
                _pendingPasswordDisplayName = string.IsNullOrWhiteSpace(matchingBookmark.displayName)
                    ? matchingBookmark.address
                    : matchingBookmark.displayName;
            }
            else
            {
                string chosenAddress = !string.IsNullOrWhiteSpace(lobby.IpAddress)
                    ? lobby.IpAddress.Trim()
                    : lobby.PublicAddress?.Trim();

                if (string.IsNullOrWhiteSpace(chosenAddress))
                {
                    ClearPendingPasswordUpdate();
                    return;
                }

                _pendingPasswordAddress = chosenAddress;
                _pendingPasswordPort = port;
                _pendingPasswordDisplayName = !string.IsNullOrWhiteSpace(lobby.LobbyName)
                    ? lobby.LobbyName
                    : _pendingPasswordAddress;
            }

            _pendingPasswordValue = password;
            _pendingPasswordSaveRequested = true;
            _pendingIsServerSession = lobby.IsServer; // Only save servers to recents (lobbies are ephemeral)

            _lastPasswordAttemptLobby = CloneLobbyInfo(lobby);
            if (!string.IsNullOrWhiteSpace(_pendingPasswordAddress) && _pendingPasswordPort > 0)
                _lastPasswordAttemptKey = LobbyBookmarkUtility.BuildKey(_pendingPasswordAddress, _pendingPasswordPort);
            else
                _lastPasswordAttemptKey = null;
        }

        private int ResolveLobbyPort(LobbyInfo lobby)
        {
            if (lobby == null)
                return SuggestedDirectConnectPort;

            if (lobby.Port > 0)
                return lobby.Port;

            if (lobby.PublicPort > 0)
                return lobby.PublicPort;

            return SuggestedDirectConnectPort;
        }

        private void HandleLobbyCreated(LobbyInfo lobby)
        {
            Debug.Log($"[LobbyBrowserMenu] HandleLobbyCreated: {lobby?.LobbyName}");
            
            // NOTE: Navigation for hosts is now handled explicitly in StartHostedLobbyAsync
            // to ensure proper dialog cleanup timing. This callback is kept for logging
            // and potential future use cases (e.g., client creating lobbies).
            // Do NOT navigate here for hosts - it would cause double navigation!
        }

        private void HandleLobbyJoined(LobbyInfo lobby)
        {
            bool isHosting = NetworkService?.IsHosting ?? false;
            YargLogger.LogInfo($"[LobbyBrowserMenu] HandleLobbyJoined: lobby={lobby?.LobbyName}, IsHosting={isHosting}, MenuManager={MenuManager.Instance != null}");
            
            // Navigate to the lobby room when joining
            if (MenuManager.Instance != null && !isHosting)
            {
                // Only navigate for clients - hosts already navigate via CreateLobbyAsync/StartHostedLobbyAsync
                YargLogger.LogInfo("[LobbyBrowserMenu] Navigating to LobbyRoom for client");
                MenuManager.Instance.PushMenu(MenuManager.Menu.LobbyRoom);
            }
            else
            {
                YargLogger.LogInfo($"[LobbyBrowserMenu] NOT navigating: MenuManager={MenuManager.Instance != null}, IsHosting={isHosting}");
            }
            
            // Handle password saving (original logic)
            // Only save to recents if this is a server session (lobbies are ephemeral)
            if (!_pendingPasswordSaveRequested || !_pendingIsServerSession)
            {
                ClearPendingPasswordUpdate();
                return;
            }

            if (string.IsNullOrWhiteSpace(_pendingPasswordAddress))
            {
                ClearPendingPasswordUpdate();
                return;
            }

            string displayName = !string.IsNullOrWhiteSpace(_pendingPasswordDisplayName)
                ? _pendingPasswordDisplayName
                : (!string.IsNullOrWhiteSpace(lobby?.LobbyName) ? lobby.LobbyName : _pendingPasswordAddress);

            var attemptKey = _lastPasswordAttemptKey;
            LobbyBookmarkStore.Instance.RecordConnection(
                _pendingPasswordAddress,
                _pendingPasswordPort,
                displayName,
                _pendingPasswordValue ?? string.Empty);

            if (!string.IsNullOrEmpty(attemptKey))
                _passwordFailures.Remove(attemptKey);

            ClearPendingPasswordUpdate();
        }

        private void HandleNetworkError(string error)
        {
            if (!string.Equals(error, "Incorrect password", StringComparison.OrdinalIgnoreCase))
                return;

            if (!string.IsNullOrEmpty(_lastPasswordAttemptKey))
                _passwordFailures.Add(_lastPasswordAttemptKey);

            var lobbyClone = _lastPasswordAttemptLobby != null ? CloneLobbyInfo(_lastPasswordAttemptLobby) : null;
            bool wasAuto = _lastPasswordAttemptWasAuto;

            ClearPendingPasswordUpdate();

            if (wasAuto && lobbyClone != null)
                ShowPasswordDialog(lobbyClone);
        }

        private void ClearPendingPasswordUpdate()
        {
            _pendingPasswordSaveRequested = false;
            _pendingPasswordAddress = null;
            _pendingPasswordDisplayName = null;
            _pendingPasswordValue = null;
            _pendingPasswordPort = 0;
            _pendingIsServerSession = false;
            _lastPasswordAttemptLobby = null;
            _lastPasswordAttemptKey = null;
            _lastPasswordAttemptWasAuto = false;
        }

        /// <summary>
        /// Called from the UI back button.
        /// </summary>
        public void Back() => MenuManager.Instance.PopMenu();

        private void UpdateStatusText(int favoritesCount, int myLobbiesCount, int recentsCount, int discoveredCount)
        {
            if (_statusText == null) return;
            _statusText.text = $"Favorites: {favoritesCount} · My Lobbies: {myLobbiesCount} · Recents: {recentsCount} · Discovered: {discoveredCount}";
        }

        private void SetNavigationScheme()
        {
            if (Navigator.Instance == null)
            {
                Debug.Log("[LobbyBrowserMenu] Navigator unavailable; navigation scheme not set.");
                return;
            }

            _lastNavigationHelpSignature = null;
            ApplyNavigationSchemeForCurrentView(force: true);
        }

        private void ApplyNavigationSchemeForCurrentView(bool force = false)
        {
            // Don't modify navigation if this menu is disabled (e.g., we're in LobbyRoomMenu)
            if (!gameObject.activeInHierarchy)
                return;
            
            if (Navigator.Instance == null)
                return;

            var target = ResolveActionTargetView();
            string signature = BuildNavigationSignature(target);

            if (!force && _navigationSchemePushed && string.Equals(signature, _lastNavigationHelpSignature, StringComparison.Ordinal))
                return;

            var scheme = BuildNavigationSchemeForCurrentView(target);

            if (_navigationSchemePushed)
            {
                try
                {
                    Navigator.Instance.PopScheme();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[LobbyBrowserMenu] Failed to pop previous navigation scheme: {ex}");
                }
                _navigationSchemePushed = false;
            }

            Navigator.Instance.PushScheme(scheme);
            _navigationSchemePushed = true;
            _lastNavigationHelpSignature = signature;
        }

        private string BuildNavigationSignature(LobbyViewType target)
        {
            bool canGreen = CanPerformGreenAction(target);
            string greenKey = canGreen ? GetGreenActionLocalizationKey(target) : string.Empty;
            bool canYellow = target != null && target.ShowFavoriteButton;
            return string.Concat(canGreen ? "1" : "0", "|", greenKey, "|", canYellow ? "1" : "0");
        }

        private NavigationScheme BuildNavigationSchemeForCurrentView(LobbyViewType target)
        {
            var entries = new List<NavigationScheme.Entry>
            {
                new NavigationScheme.Entry(MenuAction.Up, "Menu.Common.Up", ctx =>
                {
                    SetWrapAroundState(!ctx.IsRepeat);
                    SelectedIndex--;
                }),
                new NavigationScheme.Entry(MenuAction.Down, "Menu.Common.Down", ctx =>
                {
                    SetWrapAroundState(!ctx.IsRepeat);
                    SelectedIndex++;
                }),
                new NavigationScheme.Entry(MenuAction.Left, "Menu.MusicLibrary.SkipSection", GoToPreviousSection),
                new NavigationScheme.Entry(MenuAction.Right, "Menu.MusicLibrary.SkipSection", GoToNextSection),
            };

            if (CanPerformGreenAction(target))
            {
                string greenKey = GetGreenActionLocalizationKey(target);
                entries.Add(new NavigationScheme.Entry(MenuAction.Green, greenKey, TryExecuteJoinAction));
            }

            entries.Add(new NavigationScheme.Entry(MenuAction.Red, "Menu.Common.Back", Back));

            if (target != null && target.ShowFavoriteButton)
                entries.Add(new NavigationScheme.Entry(MenuAction.Yellow, "Menu.MusicLibrary.AddToFavorites", TryToggleFavorite));

            entries.Add(new NavigationScheme.Entry(MenuAction.Blue, "Menu.Common.Refresh", TriggerRefreshAction));

            return new NavigationScheme(entries, true);
        }

        private bool CanPerformGreenAction(LobbyViewType view)
        {
            if (view == null)
                return false;

            if (view is DiscoveredLobbyViewType d)
                return IsLobbyLive(d.LobbyInfo);

            if (view is SavedLobbyViewType saved)
                return saved.LiveInfo != null && IsLobbyLive(saved.LiveInfo);

            if (view is MyLobbyViewType)
                return true;

            return false;
        }

        private string GetGreenActionLocalizationKey(LobbyViewType view) => "Menu.Common.Confirm";

        private void TriggerRefreshAction()
        {
            bool hadLiveInfo = InvalidateSavedLobbyLiveInfo();
            if (hadLiveInfo)
                RequestPingStatusRefresh(forceImmediate: true);
            RefreshLobbies();
            UniTask.Void(async () => await PingSavedServersAsync(force: true));
        }

        private bool InvalidateSavedLobbyLiveInfo()
        {
            if (_pingedLobbies == null || _pingedLobbies.Count == 0)
                return false;

            bool changed = false;
            var keys = _pingedLobbies.Keys.ToList();
            foreach (var key in keys)
            {
                if (string.IsNullOrEmpty(key))
                    continue;

                if (_pingedLobbies[key] != null)
                {
                    _pingedLobbies[key] = null;
                    changed = true;
                }
            }

            return changed;
        }

        private bool IsLobbyLive(LobbyInfo lobby)
        {
            if (lobby == null)
                return false;

            if (!lobby.IsActive)
                return false;

            string endpointKey = LobbyBookmarkUtility.BuildKey(lobby.IpAddress, lobby.Port);
            int failureCount = 0;
            bool hasFailureTracking = false;
            if (!string.IsNullOrEmpty(endpointKey))
            {
                hasFailureTracking = _consecutiveProbeFailures.TryGetValue(endpointKey, out failureCount);
                if (hasFailureTracking && failureCount >= MAX_CONSECUTIVE_PROBE_FAILURES)
                    return false;
            }

            return true;
        }

        private static void MarkLobbyHeartbeat(LobbyInfo lobby)
        {
            if (lobby == null)
                return;

            lobby.IsActive = true;
        }

        private static LobbyInfo CloneLobbyInfo(LobbyInfo source)
        {
            if (source == null)
                return null;

            return new LobbyInfo
            {
                LobbyId = source.LobbyId,
                LobbyName = source.LobbyName,
                HostName = source.HostName,
                IpAddress = source.IpAddress,
                PublicAddress = source.PublicAddress,
                TransportId = source.TransportId,
                CurrentPlayers = source.CurrentPlayers,
                MaxPlayers = source.MaxPlayers,
                PrivacyMode = source.PrivacyMode,
                HasPassword = source.HasPassword,
                Password = source.Password,
                IsActive = source.IsActive,
                Port = source.Port,
                PublicPort = source.PublicPort,
                PlayerNames = source.PlayerNames != null ? (string[])source.PlayerNames.Clone() : null,
                PlayerInstruments = source.PlayerInstruments != null ? (int[])source.PlayerInstruments.Clone() : null,
                // Copy discovery tracking fields
                LastSeen = source.LastSeen,
                Ping = source.Ping,
                // Copy gameplay settings
                NoFailMode = source.NoFailMode,
                SharedSongsOnly = source.SharedSongsOnly,
                BandSize = source.BandSize,
                AllowedGameModes = source.AllowedGameModes != null 
                    ? new System.Collections.Generic.List<YARG.Core.GameMode>(source.AllowedGameModes) 
                    : new System.Collections.Generic.List<YARG.Core.GameMode>(),
                // Copy session type for bookmark eligibility
                SessionType = source.SessionType
            };
        }

        private void RequestPingStatusRefresh(bool forceImmediate = false)
        {
            if (forceImmediate)
                _nextPingStatusRefreshAt = Time.unscaledTime;

            if (Time.unscaledTime >= _nextPingStatusRefreshAt)
                ApplyPendingPingRefresh();
            else
                _pendingPingStatusRefresh = true;
        }

        private void ApplyPendingPingRefresh()
        {
            _pendingPingStatusRefresh = false;
            _nextPingStatusRefreshAt = Time.unscaledTime + PING_STATUS_REFRESH_MIN_INTERVAL;
            RefreshList(true);
            TryRefreshSidebarLiveInfo();
        }

        private void TryRefreshSidebarLiveInfo()
        {
            if (_sidebar == null)
                return;

            try
            {
                ShowSidebarFor(_lastShownSidebarView ?? CurrentSelection);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LobbyBrowserMenu] Exception while updating sidebar after ping refresh: {ex}");
            }
        }

        private LobbyViewType ResolveActionTargetView()
        {
            var target = TryRehydrateView(_lastShownSidebarView);
            if (target != null)
                return target;

            target = TryRehydrateView(CurrentSelection);
            if (target != null)
                return target;

            return null;
        }

        private LobbyViewType TryRehydrateView(LobbyViewType view)
        {
            if (view == null)
                return null;

            var views = ViewList;
            if (views != null && views.Count > 0)
            {
                string key = null;
                try { key = view.GetSelectionKey(); }
                catch { key = null; }

                if (!string.IsNullOrEmpty(key))
                {
                    foreach (var candidate in views)
                    {
                        if (candidate == null)
                            continue;

                        try
                        {
                            if (string.Equals(candidate.GetSelectionKey(), key, StringComparison.Ordinal))
                                return candidate;
                        }
                        catch { }
                    }
                }

                if (views.Contains(view))
                    return view;
            }

            return view;
        }

        private void TryExecuteJoinAction()
        {
            var target = ResolveActionTargetView();
            if (!CanPerformGreenAction(target))
                return;

            try
            {
                target.OnJoinClick();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LobbyBrowserMenu] Failed to execute join action: {ex}");
            }
        }

        private void TryToggleFavorite()
        {
            var target = ResolveActionTargetView();
            if (target == null || !target.ShowFavoriteButton)
                return;

            try
            {
                target.OnFavoriteClick();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LobbyBrowserMenu] Failed to toggle favorite: {ex}");
            }
        }

        private void UpdateSidebarForSelection()
        {
            // Don't update sidebar if this menu is disabled
            if (!gameObject.activeInHierarchy)
                return;
            
            ShowSidebarFor(CurrentSelection);
        }

        public void ShowSidebarFor(LobbyViewType view)
        {
            // Don't show sidebar if this menu is disabled (e.g., we're in LobbyRoomMenu)
            if (!gameObject.activeInHierarchy)
                return;
            
            EnsureSidebar();

            _lastShownSidebarView = view;
            ApplyNavigationSchemeForCurrentView();

            if (_sidebar == null)
                return;

            if (view is LobbyCategoryViewType category)
            {
                _selectedLobby = null;

                string key = category.CategoryKey;
                if (!string.IsNullOrEmpty(key))
                {
                    // New action categories
                    if (string.Equals(key, "SectionHostGame", StringComparison.Ordinal))
                    {
                        _sidebar.ShowHostGameForm(null, false);
                        return;
                    }

                    if (string.Equals(key, "SectionJoinGame", StringComparison.Ordinal))
                    {
                        _sidebar.ShowJoinGameForm();
                        return;
                    }

                    // Legacy categories (kept for compatibility)
                    if (string.Equals(key, "SectionCreateLobby", StringComparison.Ordinal))
                    {
                        _sidebar.ShowHostGameForm(null, false);
                        return;
                    }

                    if (string.Equals(key, "SectionDirectConnect", StringComparison.Ordinal))
                    {
                        _sidebar.ShowJoinGameForm();
                        return;
                    }
                    return;
                }
                return;
            }

            if (view is MyLobbyViewType myLobby)
            {
                _selectedLobby = null;
                _sidebar.ShowHostedLobbyPreset(myLobby.Preset);
                return;
            }

            if (view is DiscoveredLobbyViewType d)
            {
                var lobbyInfo = d.LobbyInfo;
                if (lobbyInfo != null)
                {
                    _selectedLobby = lobbyInfo;
                    _sidebar.SetLobby(_selectedLobby);
                    return;
                }
            }

            if (view is SavedLobbyViewType s)
            {
                var bookmark = s.Bookmark;
                if (_sidebar != null && bookmark != null && _sidebar.IsEditingBookmark(bookmark))
                {
                    _selectedLobby = null;
                    _sidebar.SetBookmark(bookmark);
                    return;
                }

                var live = s.LiveInfo;
                if (live != null)
                {
                    _selectedLobby = live;
                    _sidebar.SetLobby(_selectedLobby, bookmark);
                    return;
                }

                if (bookmark != null)
                {
                    _selectedLobby = null;
                    _sidebar.SetBookmark(bookmark);
                    return;
                }
            }

            if (view == null)
            {
                _selectedLobby = null;
                _sidebar.ClearLobby();
            }
        }
        
        /// <summary>
        /// Handles selection of action items (Host Game, Join Game, etc.).
        /// Called from LobbyActionViewType.OnJoinClick().
        /// </summary>
        internal void HandleActionSelection(LobbyActionViewType actionView)
        {
            if (actionView == null) return;
            
            EnsureSidebar();
            if (_sidebar == null) return;
            
            switch (actionView.Kind)
            {
                case LobbyActionViewType.ActionKind.HostGame:
                    _sidebar.ShowHostGameForm(null, false);
                    break;
                case LobbyActionViewType.ActionKind.JoinGame:
                    _sidebar.ShowJoinGameForm();
                    break;
                // Legacy - map to new methods
                case LobbyActionViewType.ActionKind.CreateLobby:
                    _sidebar.ShowHostGameForm(null, false);
                    break;
                case LobbyActionViewType.ActionKind.DirectConnect:
                    _sidebar.ShowJoinGameForm();
                    break;
            }
        }

        internal void HandleViewPointerClick(LobbyViewType view)
        {
            var target = TryRehydrateView(view);
            if (target == null)
                return;

            SelectViewInternal(target);

            if (!CanPerformGreenAction(target))
                return;

            try
            {
                target.OnJoinClick();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LobbyBrowserMenu] Failed to activate view via pointer click: {ex}");
            }
        }

        private void SelectViewInternal(LobbyViewType view)
        {
            if (view == null)
                return;

            var views = ViewList;
            if (views == null || views.Count == 0)
                return;

            int index = -1;
            for (int i = 0; i < views.Count; i++)
            {
                if (ReferenceEquals(views[i], view))
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                string key = null;
                try { key = view.GetSelectionKey(); }
                catch { key = null; }

                if (!string.IsNullOrEmpty(key))
                {
                    for (int i = 0; i < views.Count; i++)
                    {
                        var candidate = views[i];
                        if (candidate == null)
                            continue;

                        string candidateKey = null;
                        try { candidateKey = candidate.GetSelectionKey(); }
                        catch { candidateKey = null; }

                        if (!string.IsNullOrEmpty(candidateKey) && string.Equals(candidateKey, key, StringComparison.Ordinal))
                        {
                            index = i;
                            break;
                        }
                    }
                }
            }

            if (index >= 0)
                SelectedIndex = index;
        }

        private CancellationTokenSource _startHostedLobbyCts;
        
        internal void StartHostedLobby(HostedLobbyPreset preset)
        {
            if (preset == null)
                return;

            // Fire and forget async lobby creation
            StartHostedLobbyAsync(preset).Forget();
        }
        
        private async UniTaskVoid StartHostedLobbyAsync(HostedLobbyPreset preset)
        {
            Debug.Log($"[LobbyBrowserMenu] StartHostedLobbyAsync: preset.SessionType={preset?.SessionType}, preset.sessionType={preset?.sessionType}");
            
            // Validate that connected profiles aren't blocked by the preset's game mode restrictions
            // Do this BEFORE showing the dialog or doing any work
            if (preset.allowedInstruments != null && preset.allowedInstruments.Count > 0 && NetworkService != null)
            {
                var gameModeBlacklist = new List<YARG.Core.GameMode>();
                foreach (var mode in preset.allowedInstruments)
                {
                    if (mode >= 0 && mode <= 255 && Enum.IsDefined(typeof(YARG.Core.GameMode), (byte)mode))
                    {
                        gameModeBlacklist.Add((YARG.Core.GameMode)mode);
                    }
                }
                
                if (!NetworkService.ValidateProfilesAgainstGameModes(gameModeBlacklist, out var blockedProfiles))
                {
                    string blockedList = string.Join(", ", blockedProfiles);
                    Debug.LogWarning($"[LobbyBrowserMenu] Cannot start lobby - blocked profiles: {blockedList}");
                    ToastManager.ToastWarning($"Cannot start lobby: {blockedList} uses a disabled game mode.");
                    return;
                }
            }
            
            _startHostedLobbyCts?.Cancel();
            _startHostedLobbyCts?.Dispose();
            _startHostedLobbyCts = new CancellationTokenSource();
            var ct = _startHostedLobbyCts.Token;
            
            // Show a loading dialog with cancel option
            MessageDialog dialog = null;
            if (DialogManager.Instance != null)
            {
                dialog = DialogManager.Instance.ShowMessage("Starting Lobby", "Setting up lobby...\nThis may take a moment.");
                dialog.ClearButtons();
                dialog.AddDialogButton("Cancel", MenuData.Colors.CancelButton, () =>
                {
                    Debug.Log("[LobbyBrowserMenu] Hosted lobby start cancelled by user");
                    _startHostedLobbyCts?.Cancel();
                    DialogManager.Instance?.ClearDialog();
                });
            }
            
            try
            {
                var store = LobbyBookmarkStore.Instance;
                var privacy = preset.PrivacyMode;
                string password = privacy == LobbyPrivacyMode.Private ? (preset.password ?? string.Empty) : string.Empty;
                var storedPreset = store.UpsertMyLobby(
                    preset.id, 
                    preset.lobbyName, 
                    preset.maxPlayers, 
                    privacy, 
                    preset.SessionType,
                    password, 
                    true,
                    preset.bandSize,
                    preset.noFailMode,
                    preset.sharedSongsOnly,
                    preset.allowModifiers,
                    preset.enablePresetSync,
                    preset.allowLateJoin,
                    preset.allowedInstruments ?? new List<int>(),
                    preset.localPlayersFirst);

                if (_sidebar != null)
                    _sidebar.ShowHostedLobbyPreset(storedPreset);

                // Apply gameplay settings from preset
                ApplyGameplaySettingsFromPreset(storedPreset);

                // Create a SessionPreset for the SessionLifecycleManager
                var sessionPreset = CreateSessionPresetFromHosted(storedPreset);
                
                // Get the port from the preset (or fallback to default) and create lobby ID
                int port = sessionPreset.port > 0 ? sessionPreset.port : (NetworkService?.DefaultPort ?? 7777);
                Guid lobbyId = Guid.NewGuid();
                
                YargLogger.LogInfo($"[LobbyBrowserMenu] Starting hosted session with port {port} (preset.port={sessionPreset.port})");
                
                // Update dialog status
                if (dialog != null)
                {
                    dialog.Message.text = "Configuring network settings...";
                }
                
                // Start the session lifecycle (UPnP for Lobby mode, nothing for Server mode)
                string lobbyCode = null;
                if (SessionLifecycleManager.Instance == null)
                {
                    Debug.LogError("[LobbyBrowserMenu] CRITICAL: SessionLifecycleManager.Instance is null! " +
                        "This indicates networking was not initialized properly. " +
                        $"NetworkManagersBootstrap.IsInitialized={NetworkManagersBootstrap.IsInitialized}");
                    
                    if (storedPreset.SessionType == SessionType.Lobby)
                    {
                        DialogManager.Instance?.ClearDialog();
                        ToastManager.ToastError("Networking not initialized. Please restart the game.");
                        return;
                    }
                }
                
                if (SessionLifecycleManager.Instance != null)
                {
                    ct.ThrowIfCancellationRequested();
                    var result = await SessionLifecycleManager.Instance.StartHostingAsync(sessionPreset, port, lobbyId);
                    
                    ct.ThrowIfCancellationRequested();
                    if (!result.IsSuccess)
                    {
                        Debug.LogWarning($"[LobbyBrowserMenu] Session lifecycle failed: {result.Error}");
                        if (storedPreset.SessionType == SessionType.Lobby)
                        {
                            Debug.LogError($"[LobbyBrowserMenu] Failed to start lobby session: {result.Error}");
                            DialogManager.Instance?.ClearDialog();
                            ToastManager.ToastError($"Failed to start lobby: {result.Error}");
                            return;
                        }
                    }
                    else
                    {
                        lobbyCode = result.LobbyCode;
                        if (!string.IsNullOrEmpty(lobbyCode))
                        {
                            Debug.Log($"[LobbyBrowserMenu] Lobby code generated: {lobbyCode}");
                        }
                        
                        // Log network setup info (for debugging), but don't show toast for normal UPnP failure
                        // since NAT punch-through should handle most cases
                        if (storedPreset.SessionType == SessionType.Lobby && !result.UPnPSuccess)
                        {
                            // Only log - NAT punch-through is the primary connection method now
                            Debug.LogWarning($"[LobbyBrowserMenu] Network setup warning: {result.Message}");
                            // Only show toast for truly problematic cases (no public IP at all)
                            if (result.Message?.Contains("Could not determine public IP") == true)
                            {
                                ToastManager.ToastWarning("Could not determine public IP. Only local network players can connect.");
                            }
                        }
                    }
                }

                // Update dialog before creating local lobby
                if (dialog != null)
                {
                    dialog.Message.text = "Starting lobby...";
                }
                ct.ThrowIfCancellationRequested();

                // IMPORTANT: Set the server port BEFORE creating the lobby
                // This ensures the NetworkService uses the correct port from the preset
                NetworkService?.SetServerPort(port);

                // Create the lobby first (this triggers OnLobbyCreated event, but we'll navigate manually)
                var lobby = NetworkService?.CreateLobby(storedPreset.lobbyName, storedPreset.maxPlayers, storedPreset.PrivacyMode, storedPreset.SessionType, storedPreset.password ?? string.Empty);
                
                // Set the lobby code on the LobbyInfo - explicitly set to null for Server mode
                // to clear any stale code from a previous Lobby session
                if (lobby != null)
                {
                    lobby.LobbyCode = lobbyCode; // null for Server, valid code for Lobby
                    
                    // IMPORTANT: Start NAT punch keepalive NOW that the transport is running.
                    // This sends UDP packets to the punch server to create NAT mapping so clients can connect.
                    SessionLifecycleManager.Instance?.StartPunchKeepalive();
                }
                
                // CRITICAL: Close the dialog AFTER creating the lobby but BEFORE navigating!
                // Dialog.OnDisable pops from the navigation stack, and we need to ensure
                // the stack is in the correct state before pushing LobbyRoomMenu's scheme.
                if (DialogManager.Instance != null && DialogManager.Instance.IsDialogShowing)
                {
                    DialogManager.Instance.ClearDialog();
                    dialog = null;  // Mark as cleared so finally doesn't try again
                }
                
                // Navigate to lobby room explicitly (don't rely solely on HandleLobbyCreated callback)
                // This ensures navigation happens even if there's a timing issue with the event
                if (lobby != null && MenuManager.Instance != null)
                {
                    Debug.Log($"[LobbyBrowserMenu] Lobby created, navigating to LobbyRoom (explicit)");
                    MenuManager.Instance.PushMenu(MenuManager.Menu.LobbyRoom);
                }
                
                // Lobby code is displayed in the LobbyRoom UI - no need for toast
            }
            catch (OperationCanceledException)
            {
                Debug.Log("[LobbyBrowserMenu] Hosted lobby start was cancelled");
                ToastManager.ToastInformation("Lobby start cancelled");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LobbyBrowserMenu] Failed to create lobby from preset: {ex}");
                ToastManager.ToastError($"Failed to start lobby: {ex.Message}");
            }
            finally
            {
                // Clean up dialog if still showing
                if (DialogManager.Instance != null && DialogManager.Instance.IsDialogShowing)
                {
                    DialogManager.Instance.ClearDialog();
                }
                
                _startHostedLobbyCts?.Dispose();
                _startHostedLobbyCts = null;
            }
        }

        internal void ShowHostedLobbyEditor(HostedLobbyPreset preset)
        {
            // Don't show editor if this menu is disabled (e.g., we're in LobbyRoomMenu)
            if (!gameObject.activeInHierarchy)
                return;
            
            if (_sidebar == null || preset == null)
                return;

            _lastShownSidebarView = null;
            ApplyNavigationSchemeForCurrentView();
            _sidebar.ShowCreateLobbyForm(preset, true);
        }

        protected override void Update()
        {
            base.Update();

            if (Time.unscaledTime >= _nextStaleSweepAt)
            {
                _nextStaleSweepAt = Time.unscaledTime + STALE_SWEEP_INTERVAL;
                if (CullStalePingCache() || PruneStaleDiscoveryEntries())
                    RequestPingStatusRefresh();
            }

            if (_pendingPingStatusRefresh && Time.unscaledTime >= _nextPingStatusRefreshAt)
                ApplyPendingPingRefresh();

            if (!_isPingingSavedServers && Time.unscaledTime >= _nextAutomaticPingAt)
                UniTask.Void(async () => await PingSavedServersAsync());
            
            // Send periodic LAN discovery broadcasts
            // Uses rapid burst mode initially, then slows to normal interval
            if (Time.unscaledTime >= _nextDiscoveryBroadcastAt)
            {
                if (_discoveryBurstRemaining > 0)
                {
                    // Burst mode: send rapid discovery requests to catch servers quickly
                    _discoveryBurstRemaining--;
                    _nextDiscoveryBroadcastAt = Time.unscaledTime + DISCOVERY_BURST_INTERVAL;
                }
                else
                {
                    // Normal mode: slower interval to reduce network traffic
                    _nextDiscoveryBroadcastAt = Time.unscaledTime + DISCOVERY_BROADCAST_INTERVAL;
                }
                NetworkService?.SendBroadcastDiscoveryRequest();
            }
        }

        private bool CullStalePingCache()
        {
            if (_pingedLobbies == null || _pingedLobbies.Count == 0)
                return false;

            bool changed = false;
            var keys = _pingedLobbies.Keys.ToList();
            foreach (var key in keys)
            {
                if (string.IsNullOrEmpty(key))
                    continue;

                var info = _pingedLobbies[key];
                if (info != null && !IsLobbyLive(info))
                {
                    _pingedLobbies[key] = null;
                    changed = true;
                }
            }

            return changed;
        }

        private bool PruneStaleDiscoveryEntries()
        {
            if (_currentLobbies == null || _currentLobbies.Count == 0)
                return false;

            int removed = _currentLobbies.RemoveAll(lobby => !IsLobbyLive(lobby));
            return removed > 0;
        }

        private void GoToPreviousSection()
        {
            if (_sectionStartIndices.Count == 0) return;
            int currentSection = GetSectionIndexFor(SelectedIndex);
            JumpToSection(Mathf.Max(0, currentSection - 1));
        }

        private void GoToNextSection()
        {
            if (_sectionStartIndices.Count == 0) return;
            int currentSection = GetSectionIndexFor(SelectedIndex);
            JumpToSection(Mathf.Min(_sectionStartIndices.Count - 1, currentSection + 1));
        }

        private void JumpToSection(int sectionIndex)
        {
            if (_sectionStartIndices.Count == 0 || ViewList == null || ViewList.Count == 0) return;
            sectionIndex = Mathf.Clamp(sectionIndex, 0, _sectionStartIndices.Count - 1);
            if (!SelectFirstSelectableInSection(sectionIndex)) SelectedIndex = _sectionStartIndices[sectionIndex];
        }

        private int GetSectionIndexFor(int viewIndex)
        {
            if (_sectionStartIndices.Count == 0) return 0;
            if (viewIndex < 0) return 0;
            for (int i = _sectionStartIndices.Count - 1; i >= 0; i--)
                if (viewIndex >= _sectionStartIndices[i]) return i;
            return 0;
        }

        private int GetSectionEndIndex(int sectionIndex)
        {
            var views = ViewList;
            if (views == null || views.Count == 0) return 0;
            if (sectionIndex + 1 < _sectionStartIndices.Count) return _sectionStartIndices[sectionIndex + 1];
            return views.Count;
        }

        private bool SelectFirstSelectableInSection(int sectionIndex)
        {
            if (_sectionStartIndices.Count == 0) return false;
            sectionIndex = Mathf.Clamp(sectionIndex, 0, _sectionStartIndices.Count - 1);
            return SelectFirstSelectableInRange(_sectionStartIndices[sectionIndex], GetSectionEndIndex(sectionIndex));
        }

        private bool SelectFirstSelectableInRange(int startInclusive, int endExclusive)
        {
            var views = ViewList;
            if (views == null || views.Count == 0) return false;
            startInclusive = Mathf.Clamp(startInclusive, 0, views.Count - 1);
            endExclusive = Mathf.Clamp(endExclusive, startInclusive + 1, views.Count);
            for (int i = startInclusive; i < endExclusive; i++)
                if (IsSelectable(views[i])) { SelectedIndex = i; return true; }
            return false;
        }

        private static bool IsSelectable(LobbyViewType view) => view is not LobbyCategoryViewType and not LobbyEmptyViewType;

        private void RebuildSectionCache(List<LobbyViewType> viewTypes)
        {
            _sectionStartIndices.Clear();
            if (viewTypes == null || viewTypes.Count == 0) return;
            for (int i = 0; i < viewTypes.Count; i++)
                if (viewTypes[i] is LobbyCategoryViewType) _sectionStartIndices.Add(i);
        }

        private async UniTask PingSavedServersAsync(bool force = false)
        {
            if (_favorites == null)
                return;

            float now = Time.unscaledTime;
            if (!force && (now - _lastPingStartedAt) < DISCOVERY_PING_INTERVAL)
                return;

            _lastPingStartedAt = now;
            _nextAutomaticPingAt = now + DISCOVERY_PING_INTERVAL;

            _pingedLobbies ??= new Dictionary<string, LobbyInfo>();

            if (_isPingingSavedServers)
            {
                _pingCancellation?.Cancel();
                await UniTask.WaitUntil(() => !_isPingingSavedServers);
            }

            _pingCancellation?.Dispose();
            _pingCancellation = new CancellationTokenSource();

            _isPingingSavedServers = true;

            try
            {
                var processedKeys = new HashSet<string>();
                var token = _pingCancellation.Token;

                await PingBookmarkCollectionAsync(_favorites.GetFavorites(), processedKeys, token);
                await PingBookmarkCollectionAsync(_favorites.GetRecents(), processedKeys, token);

                RequestPingStatusRefresh();
            }
            catch (OperationCanceledException)
            {
                // Cancellation is expected
            }
            finally
            {
                _isPingingSavedServers = false;
                _pingCancellation?.Dispose();
                _pingCancellation = null;
            }
        }

        private async UniTask PingBookmarkCollectionAsync(IReadOnlyList<LobbyBookmark> bookmarks, HashSet<string> processedKeys, CancellationToken token)
        {
            if (bookmarks == null || bookmarks.Count == 0)
                return;

            foreach (var bookmark in bookmarks)
            {
                token.ThrowIfCancellationRequested();

                if (bookmark == null)
                    continue;

                string key = bookmark.EndpointKey;
                if (string.IsNullOrEmpty(key))
                    continue;

                if (processedKeys != null && !processedKeys.Add(key))
                    continue;

                if (_pendingPings.Contains(key))
                    continue;

                _pendingPings.Add(key);
                try
                {
                    SendDiscoveryRequestForBookmark(bookmark);
                    await ProbeBookmarkAsync(bookmark, token);
                    await UniTask.Yield(PlayerLoopTiming.Update, token);
                }
                finally
                {
                    _pendingPings.Remove(key);
                }
            }
        }

        private void SendDiscoveryRequestForBookmark(LobbyBookmark bookmark)
        {
            if (bookmark == null || string.IsNullOrWhiteSpace(bookmark.address))
                return;

            var candidatePorts = new List<int>(4);

            int discoveryPort = NetworkService?.DiscoveryPort ?? 0;
            if (discoveryPort > 0)
                candidatePorts.Add(discoveryPort);

            int bookmarkPort = bookmark.port > 0 ? bookmark.port : SuggestedDirectConnectPort;
            if (bookmarkPort > 0)
                candidatePorts.Add(bookmarkPort);

            if (NetworkTransportDefaults.DefaultUdpPort > 0)
                candidatePorts.Add(NetworkTransportDefaults.DefaultUdpPort);
            if (NetworkTransportDefaults.DefaultTcpPort > 0)
                candidatePorts.Add(NetworkTransportDefaults.DefaultTcpPort);

            foreach (int port in candidatePorts.Distinct())
            {
                if (port <= 0 || port > ushort.MaxValue)
                    continue;

                try
                {
                    NetworkService?.SendDiscoveryRequest(bookmark.address, port);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[LobbyBrowserMenu] Failed to send discovery request to {bookmark.address}:{port}: {ex.Message}");
                }
            }
        }

        private async UniTask<bool> ProbeBookmarkAsync(LobbyBookmark bookmark, CancellationToken token)
        {
            if (bookmark == null || NetworkService == null)
                return false;

            string key = bookmark.EndpointKey;
            if (string.IsNullOrEmpty(key))
                return false;

            if (string.IsNullOrWhiteSpace(bookmark.address))
                return false;

            int port = bookmark.port > 0 ? bookmark.port : SuggestedDirectConnectPort;

            try
            {
                var info = await NetworkService.ProbeLobby(bookmark.address, port);
                if (info != null)
                {
                    info.IpAddress = bookmark.address;
                    info.PublicAddress = string.IsNullOrWhiteSpace(info.PublicAddress) ? bookmark.address : info.PublicAddress;
                    info.Port = port;
                    MarkLobbyHeartbeat(info);
                    var snapshot = CloneLobbyInfo(info);
                    ResetProbeFailureCount(key);
                    _pingedLobbies[key] = snapshot;
                    RequestPingStatusRefresh(forceImmediate: true);
                    return true;
                }

                HandleProbeFailure(key);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LobbyBrowserMenu] Probe failed for {bookmark.address}:{port}: {ex.Message}");
                HandleProbeFailure(key);
            }

            return false;
        }

        private void ResetProbeFailureCount(string endpointKey)
        {
            if (string.IsNullOrEmpty(endpointKey))
                return;

            _consecutiveProbeFailures.Remove(endpointKey);
        }

        private void HandleProbeFailure(string endpointKey)
        {
            if (string.IsNullOrEmpty(endpointKey))
                return;

            int failures = 1;
            if (_consecutiveProbeFailures.TryGetValue(endpointKey, out var existing))
                failures = existing + 1;

            if (failures > MAX_CONSECUTIVE_PROBE_FAILURES)
                failures = MAX_CONSECUTIVE_PROBE_FAILURES;

            _consecutiveProbeFailures[endpointKey] = failures;

            if (failures >= MAX_CONSECUTIVE_PROBE_FAILURES)
            {
                bool hadEntry = _pingedLobbies.TryGetValue(endpointKey, out var previous);
                bool shouldNotify = !hadEntry || previous != null;
                _pingedLobbies[endpointKey] = null;

                if (shouldNotify)
                    RequestPingStatusRefresh();
            }
        }

        public void JoinSavedBookmark(LobbyBookmark bookmark)
        {
            if (bookmark == null) return;
            if (_pingedLobbies.TryGetValue(bookmark.EndpointKey, out var live) && live != null)
            {
                JoinLobby(live);
                return;
            }

            try
            {
                string endpoint = EndpointUtility.FormatEndpoint(bookmark.address, bookmark.port <= 0 ? SuggestedDirectConnectPort : bookmark.port);
                NetworkService?.JoinLobby(endpoint, bookmark.password ?? string.Empty);
            }
            catch (Exception)
            {
                string endpoint = string.Concat(bookmark.address, ":", bookmark.port);
                NetworkService?.JoinLobby(endpoint, bookmark.password ?? string.Empty);
            }
        }

        public void EditBookmark(LobbyBookmark bookmark)
        {
            if (bookmark == null) return;

            if (DialogManager.Instance != null)
            {
                var dialog = DialogManager.Instance.ShowMessage("Edit Bookmark", "Bookmark editing UI is not implemented in this build.");
                dialog.AddDialogButton("OK", MenuData.Colors.BrightButton, () => DialogManager.Instance.ClearDialog());
            }

            _favorites?.UpdateBookmark(bookmark, bookmark.displayName, bookmark.address, bookmark.port, bookmark.password);
        }
    }
}
