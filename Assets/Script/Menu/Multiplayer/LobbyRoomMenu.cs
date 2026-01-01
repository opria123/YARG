using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Cysharp.Threading.Tasks;
using YARG.Core;
using YARG.Core.Input;
using YARG.Multiplayer;
using YARG.Networking;
using YARG.Networking.Abstraction;
using YARG.Networking.Bands;
using YARG.Networking.Bookmarks;
using YARG.Networking.Gameplay;
using YARG.Networking.Session;
using YARG.Networking.Settings;
using YARG.Networking.Tracks;
using YARG.Net.Packets;
using YARG.Menu.Data;
using YARG.Menu.Dialogs;
using YARG.Menu.MusicLibrary;
using YARG.Menu.Multiplayer.Settings;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;

namespace YARG.Menu.Multiplayer
{
    /// <summary>
    /// Lobby waiting room where players gather before browsing songs.
    /// Host can start song selection when ready.
    /// </summary>
    public class LobbyRoomMenu : MonoBehaviour
    {
        private static bool _isQuitting = false;
        
        [Header("Lobby Info")]
        [SerializeField] private TextMeshProUGUI lobbyNameText;
        [SerializeField] private TextMeshProUGUI hostNameText;
        [SerializeField] private TextMeshProUGUI playerCountText;
        [SerializeField] private TextMeshProUGUI lobbyCodeText;
        [SerializeField] private TextMeshProUGUI connectionInfoText;
        [SerializeField] private RectTransform hostAddressPanelParent;
        [SerializeField] private GameObject hostAddressPanel;
        [SerializeField] private GameObject lobbyCodeRow;
        [SerializeField] private TextMeshProUGUI lobbyCodeValueText;
        [SerializeField] private Button lobbyCodeVisibilityToggleButton;
        [SerializeField] private TextMeshProUGUI lobbyCodeVisibilityToggleLabel;
        [SerializeField] private Button lobbyCodeCopyButton;
        [SerializeField] private GameObject lanAddressRow;
        [SerializeField] private TextMeshProUGUI lanAddressValueText;
        [SerializeField] private Button lanVisibilityToggleButton;
        [SerializeField] private TextMeshProUGUI lanVisibilityToggleLabel;
        [SerializeField] private Button lanCopyButton;
        [SerializeField] private GameObject wanAddressRow;
        [SerializeField] private TextMeshProUGUI wanAddressValueText;
        [SerializeField] private Button wanVisibilityToggleButton;
        [SerializeField] private TextMeshProUGUI wanVisibilityToggleLabel;
        [SerializeField] private Button wanCopyButton;
        [Header("Visibility Icons")]
        [SerializeField] private Sprite visibilityVisibleSprite;
        [SerializeField] private Sprite visibilityHiddenSprite;
        
        [Header("Session Settings")]
        [SerializeField] private SessionSettingsPanelBuilder sessionSettingsPanel;
        [SerializeField] private GameObject sessionSettingsDivider;
        
        [Header("Player List")]
        [SerializeField] private Transform playerListContainer;
        [SerializeField] private GameObject playerEntryPrefab;
        
        [Header("Controls")]
        [SerializeField] private Button browseSongsButton; // Host only
        [SerializeField] private Button leaveLobbyButton;
        [SerializeField] private TextMeshProUGUI waitingForHostText; // Client only

        private bool isHost = false;
        private NetworkPlayerData selectedPlayer = null;
        private PlayerView selectedPlayerView = null;
        private Dictionary<NetworkPlayerData, PlayerView> playerViews = new Dictionary<NetworkPlayerData, PlayerView>();
        private Dictionary<int, PlayerView> bandHeaderViews = new Dictionary<int, PlayerView>(); // bandId -> header view
        private HashSet<NetworkPlayerData> _instrumentChangeSubscriptions = new HashSet<NetworkPlayerData>();
        private string _defaultWaitingForHostText;
        private bool _hostPanelListenersBound;
        private string _lobbyCode = string.Empty;
        private string _lanAddress = string.Empty;
        private string _wanAddress = string.Empty;
        private bool _lobbyCodeVisible;
        private bool _lanVisible;
        private bool _wanVisible;
        private bool _hostIsBrowsingSongs = false;
        private bool _navigationSchemePushed = false;

        private void Start()
        {
            EnsureHostAddressPanel();
            
            // Wire up button onClick events
            if (browseSongsButton != null)
            {
                browseSongsButton.onClick.AddListener(OnBrowseSongsClicked);
            }
            
            if (leaveLobbyButton != null)
            {
                leaveLobbyButton.onClick.AddListener(OnLeaveLobbyClicked);
            }

            if (waitingForHostText != null)
            {
                _defaultWaitingForHostText = waitingForHostText.text;
            }
            
            // Subscribe to critical player events in Start (not OnEnable) so we still receive them
            // when other menus (like MusicLibrary) are pushed on top and this menu is disabled
            Debug.Log("[LobbyRoomMenu] Start called - subscribing to events");
            var networkingService = NetworkingServiceFactory.Instance;
            if (networkingService != null)
            {
                networkingService.OnPlayerJoined += OnPlayerJoinedLobby;
                networkingService.OnPlayerLeft += OnPlayerLeftLobby;
                
                if (networkingService is LiteNetNetworkingAdapter liteNetAdapter)
                {
                    liteNetAdapter.OnPlayersJoinedFromConnection += OnPlayersJoinedFromConnection;
                    
                    // Late join events (client-side)
                    liteNetAdapter.OnLateJoinWaiting += OnLateJoinWaiting;
                    liteNetAdapter.OnLateJoinSpectating += OnLateJoinSpectating;
                    liteNetAdapter.OnSetlistAborted += OnSetlistAborted;
                    liteNetAdapter.OnLateJoinReady += OnLateJoinReady;
                    
                    // Check for pending late join state that arrived before we subscribed
                    CheckPendingLateJoinState(liteNetAdapter);
                }
            }

            // Check if we have a valid lobby before trying to refresh
            if (networkingService == null || networkingService.CurrentLobby == null)
            {
                Debug.Log("[LobbyRoomMenu] No active lobby, waiting for OnEnable");
                return;
            }

            RefreshLobbyInfo();
        }

        private void OnEnable()
        {
            Debug.Log("[LobbyRoomMenu] OnEnable called");
            
            // Reset cached address values for new session
            // This ensures stale values from previous sessions don't persist
            _lobbyCode = string.Empty;
            _lanAddress = string.Empty;
            _wanAddress = string.Empty;
            _lobbyCodeVisible = false;
            _lanVisible = false;
            _wanVisible = false;
            
            var networkingService = NetworkingServiceFactory.Instance;
            if (networkingService != null)
            {
                // Note: OnPlayerJoined/OnPlayerLeft are now subscribed in Start() to persist through menu changes
                networkingService.OnLobbyLeft += OnLobbyLeft;
                networkingService.OnLobbyJoined += OnLobbyInfoUpdated;
                networkingService.OnSessionSettingsReceived += OnSessionSettingsReceivedFromHost;
                networkingService.OnHostChanged += OnHostChangedHandler;
                
                if (networkingService is LiteNetNetworkingAdapter liteNetAdapter)
                {
                    liteNetAdapter.OnBrowsingStateChanged += OnHostBrowsingStateChanged;
                    liteNetAdapter.OnNetworkError += OnNetworkError;
                    liteNetAdapter.OnTrackOrderReceived += OnTrackOrderReceived;
                    // Note: OnPlayersJoinedFromConnection is now subscribed in Start()
                    _hostIsBrowsingSongs = liteNetAdapter.IsBrowsingSongs;
                    
                    // Band sync events (client-side)
                    liteNetAdapter.OnBandAssignmentReceived += OnBandAssignmentReceivedFromHost;
                    liteNetAdapter.OnBandNameChangeReceived += OnBandNameChangeReceivedFromHost;
                    
                    // Band sync events (host-side)
                    liteNetAdapter.OnBandNameChangeRequested += OnBandNameChangeRequestedFromClient;
                    
                    // Check for pending late join state (in case we were pushed/popped)
                    CheckPendingLateJoinState(liteNetAdapter);
                }
            }
            
            // Subscribe to TrackOrderManager changes
            var trackOrderManager = TrackOrderManager.Instance;
            if (trackOrderManager != null)
            {
                trackOrderManager.OnOrderChanged += OnTrackOrderChanged;
            }
            
            // Subscribe to session settings changes
            if (sessionSettingsPanel != null)
            {
                sessionSettingsPanel.OnSettingsChanged += OnSessionSettingsChanged;
            }
            
            RefreshLobbyInfo();
        }

        private void OnDisable()
        {
            var networkingService = NetworkingServiceFactory.Instance;
            if (networkingService != null)
            {
                // Unsubscribe from OnLobbyLeft FIRST to prevent re-entry during cleanup
                networkingService.OnLobbyLeft -= OnLobbyLeft;
                networkingService.OnLobbyJoined -= OnLobbyInfoUpdated;
                networkingService.OnSessionSettingsReceived -= OnSessionSettingsReceivedFromHost;
                networkingService.OnHostChanged -= OnHostChangedHandler;
                
                // Check if we're in a "browsing" state (host navigating to song selection).
                // In this case, the menu is hidden but the lobby should remain active.
                bool isBrowsing = _hostIsBrowsingSongs;
                if (!isBrowsing && networkingService is LiteNetNetworkingAdapter liteNetAdapter)
                {
                    // Double-check the adapter's state in case our local flag is out of sync
                    isBrowsing = liteNetAdapter.IsBrowsingSongs;
                }
                
                // If we're the actual server process (IsHosting) and the lobby is still active when 
                // this menu is disabled, that means we're leaving without proper cleanup.
                // Clean up the session to prevent "session already active" errors.
                // HOWEVER, if the host is browsing songs (navigating to music library), don't leave!
                // NOTE: Use IsHosting (actual server process), NOT isHost (designated host).
                // A designated host on a dedicated server is still a client and should NOT
                // trigger lobby cleanup when navigating to song selection.
                if (networkingService.IsHosting && networkingService.CurrentLobby != null && !isBrowsing)
                {
                    Debug.Log("[LobbyRoomMenu] OnDisable: Server host is leaving with active lobby - cleaning up session");
                    
                    // Stop the session lifecycle (releases lobby code, closes UPnP port)
                    if (SessionLifecycleManager.Instance != null && SessionLifecycleManager.Instance.IsSessionActive)
                    {
                        SessionLifecycleManager.Instance.StopAsync().Forget();
                    }
                    
                    // Leave the lobby (OnLobbyLeft already unsubscribed above)
                    networkingService.LeaveLobby();
                }
                else if (isBrowsing)
                {
                    Debug.Log("[LobbyRoomMenu] OnDisable: Host is browsing songs, keeping lobby active");
                }
                
                // Unsubscribe from LiteNet-specific events
                var liteNetAdapterForEvents = networkingService as LiteNetNetworkingAdapter;
                if (liteNetAdapterForEvents != null)
                {
                    liteNetAdapterForEvents.OnBrowsingStateChanged -= OnHostBrowsingStateChanged;
                    liteNetAdapterForEvents.OnNetworkError -= OnNetworkError;
                    liteNetAdapterForEvents.OnTrackOrderReceived -= OnTrackOrderReceived;
                    // Note: OnPlayersJoinedFromConnection is now unsubscribed in OnDestroy()
                    
                    // Band sync events (client-side)
                    liteNetAdapterForEvents.OnBandAssignmentReceived -= OnBandAssignmentReceivedFromHost;
                    liteNetAdapterForEvents.OnBandNameChangeReceived -= OnBandNameChangeReceivedFromHost;
                    
                    // Band sync events (host-side)
                    liteNetAdapterForEvents.OnBandNameChangeRequested -= OnBandNameChangeRequestedFromClient;
                }
            }
            
            // Unsubscribe from TrackOrderManager
            var trackOrderManager = TrackOrderManager.Instance;
            if (trackOrderManager != null)
            {
                trackOrderManager.OnOrderChanged -= OnTrackOrderChanged;
            }
            
            // Unsubscribe from session settings changes
            if (sessionSettingsPanel != null)
            {
                sessionSettingsPanel.OnSettingsChanged -= OnSessionSettingsChanged;
            }
            
            // Pop our navigation scheme if we pushed it
            if (_navigationSchemePushed)
            {
                try
                {
                    Navigator.Instance?.PopScheme();
                }
                catch (InvalidOperationException)
                {
                    // Stack was empty
                }
                _navigationSchemePushed = false;
            }
        }

        private void OnDestroy()
        {
            // Unsubscribe from instrument change events
            foreach (var player in _instrumentChangeSubscriptions)
            {
                if (player != null)
                {
                    player.OnInstrumentChangedEvent -= OnPlayerInstrumentChangedForFilter;
                }
            }
            _instrumentChangeSubscriptions.Clear();
            
            // Unsubscribe from critical player events that were subscribed in Start()
            var networkingService = NetworkingServiceFactory.Instance;
            if (networkingService != null)
            {
                networkingService.OnPlayerJoined -= OnPlayerJoinedLobby;
                networkingService.OnPlayerLeft -= OnPlayerLeftLobby;
                networkingService.OnLobbyLeft -= OnLobbyLeft;
                networkingService.OnLobbyJoined -= OnLobbyInfoUpdated;
                
                if (networkingService is LiteNetNetworkingAdapter liteNetAdapter)
                {
                    liteNetAdapter.OnPlayersJoinedFromConnection -= OnPlayersJoinedFromConnection;
                    
                    // Late join events (client-side)
                    liteNetAdapter.OnLateJoinWaiting -= OnLateJoinWaiting;
                    liteNetAdapter.OnLateJoinSpectating -= OnLateJoinSpectating;
                    liteNetAdapter.OnSetlistAborted -= OnSetlistAborted;
                    liteNetAdapter.OnLateJoinReady -= OnLateJoinReady;
                }
            }

            if (lanVisibilityToggleButton != null)
                lanVisibilityToggleButton.onClick.RemoveListener(ToggleLanVisibility);
            if (lanCopyButton != null)
                lanCopyButton.onClick.RemoveListener(CopyLanAddress);
            if (wanVisibilityToggleButton != null)
                wanVisibilityToggleButton.onClick.RemoveListener(ToggleWanVisibility);
            if (wanCopyButton != null)
                wanCopyButton.onClick.RemoveListener(CopyWanAddress);
        }
        
        private void OnApplicationQuit()
        {
            _isQuitting = true;
        }
        
        private void UpdateNavigationScheme()
        {
            // Only update navigation scheme if this menu is actually active
            // This prevents pushing our scheme when MusicLibrary is on top
            if (!gameObject.activeInHierarchy || !enabled)
            {
                Debug.Log("[LobbyRoomMenu] UpdateNavigationScheme: Menu not active, skipping");
                return;
            }
            
            // Only push the navigation scheme once - don't pop/re-push as that corrupts
            // the navigation stack when dialogs or settings controls have pushed their own schemes
            if (_navigationSchemePushed)
            {
                Debug.Log("[LobbyRoomMenu] UpdateNavigationScheme: Already pushed, skipping");
                return;
            }
            
            Debug.Log($"[LobbyRoomMenu] UpdateNavigationScheme: Pushing navigation scheme (isHost={isHost})");
            
            var entries = new List<NavigationScheme.Entry>
            {
                NavigationScheme.Entry.NavigateSelect,
                NavigationScheme.Entry.NavigateUp,
                NavigationScheme.Entry.NavigateDown,
                new NavigationScheme.Entry(MenuAction.Red, "Leave Lobby", OnLeaveLobbyClicked)
            };
            
            if (isHost)
            {
                entries.Add(new NavigationScheme.Entry(MenuAction.Yellow, "Browse Songs", OnBrowseSongsClicked));
            }
            
            // Don't use PopCallback - back navigation should be disabled in lobby
            // User must use the explicit "Leave Lobby" (Red) button to exit
            Navigator.Instance?.PushScheme(new NavigationScheme(entries, true));
            _navigationSchemePushed = true;
            Debug.Log("[LobbyRoomMenu] UpdateNavigationScheme: Pushed successfully");
        }
        
        public void OnPlayerSelected(NetworkPlayerData player)
        {
            if (selectedPlayerView != null)
                selectedPlayerView.SetSelected(false);
            
            selectedPlayer = player;
            
            if (playerViews.TryGetValue(player, out var playerView))
            {
                selectedPlayerView = playerView;
                selectedPlayerView.SetSelected(true);
            }
            
            UpdateNavigationScheme();
        }
        
        public void OnPlayerDeselected()
        {
            if (selectedPlayerView != null)
            {
                selectedPlayerView.SetSelected(false);
                selectedPlayerView = null;
            }
            
            selectedPlayer = null;
            UpdateNavigationScheme();
        }
        
        private void OnLobbyInfoUpdated(LobbyInfo lobby)
        {
            Debug.Log($"[LobbyRoomMenu] Lobby info updated: {lobby?.LobbyName}");
            RefreshLobbyInfo();
        }
        
        private void OnPlayerJoinedLobby(NetworkPlayerData player)
        {
            Debug.Log($"[LobbyRoomMenu] Player joined: {player?.PlayerName}, NetworkPlayerId={player?.NetworkPlayerId}");
            
            // NOTE: Track order is now handled by OnPlayersJoinedFromConnection to cluster
            // players from the same connection together. This handler just refreshes the UI.
            
            // Subscribe to instrument changes for song filter refresh
            if (player != null && !_instrumentChangeSubscriptions.Contains(player))
            {
                player.OnInstrumentChangedEvent += OnPlayerInstrumentChangedForFilter;
                _instrumentChangeSubscriptions.Add(player);
            }
            
            // Refresh the song filter since player instruments affect filtering
            MultiplayerSongFilter.RefreshFilter();
            
            RefreshLobbyInfo();
        }

        /// <summary>
        /// Called when multiple players from the same connection join.
        /// This allows us to add them as a group to maintain clustering.
        /// </summary>
        private void OnPlayersJoinedFromConnection(List<NetworkPlayerData> players)
        {
            if (!isHost || players == null || players.Count == 0)
                return;
            
            Debug.Log($"[LobbyRoomMenu] {players.Count} player(s) joined from same connection: {string.Join(", ", players.Select(p => p.PlayerName))}");
            
            var trackOrderManager = TrackOrderManager.Instance;
            if (trackOrderManager == null)
                return;
            
            // Add all players from this connection as a group (they'll be adjacent)
            var playerIds = players.Select(p => p.NetworkPlayerId).ToList();
            trackOrderManager.AddPlayersAsGroup(playerIds);
            
            Debug.Log($"[LobbyRoomMenu] Track order now has {trackOrderManager.ExportCustomOrder()?.Count ?? 0} players");
            
            // Assign the new players to bands (without reinitializing)
            AssignNewPlayersFromConnectionToBand(players);
            
            // Broadcast the updated order to all clients
            var customOrder = trackOrderManager.ExportCustomOrder();
            if (customOrder != null && customOrder.Count > 0)
            {
                BroadcastTrackOrder();
            }
        }
        
        /// <summary>
        /// Assigns newly joined players from a single connection to a band.
        /// </summary>
        private void AssignNewPlayersFromConnectionToBand(List<NetworkPlayerData> players)
        {
            var bandManager = BandManager.Instance;
            if (bandManager == null || !bandManager.AreBandsEnabled)
                return;
            
            // Check if these players are already assigned
            var playerIds = players.Select(p => p.NetworkPlayerId).ToList();
            bool allAssigned = playerIds.All(pid => bandManager.GetPlayerBandId(pid) >= 0);
            if (allAssigned)
            {
                Debug.Log($"[LobbyRoomMenu] Players from connection already assigned to bands");
                return;
            }
            
            // Get a consistent connection ID
            int effectiveConnectionId = players[0].ConnectionId != Guid.Empty 
                ? players[0].ConnectionId.GetHashCode() 
                : UnityEngine.Random.Range(1000000, int.MaxValue);
            
            bool isLocalConnection = players.Any(p => p.IsLocalUser);
            if (isLocalConnection)
            {
                bandManager.SetLocalConnectionId(effectiveConnectionId);
            }
            
            int assignedBandId = bandManager.AssignClientPlayersToBand(effectiveConnectionId, playerIds, isLocalConnection);
            Debug.Log($"[LobbyRoomMenu] Assigned {playerIds.Count} new players from connection {effectiveConnectionId} to band {assignedBandId}");
            
            // Host broadcasts updated band assignments to all clients
            BroadcastBandAssignments();
            
            // Refresh player list to show band grouping
            RefreshPlayerList();
        }
        
        private void OnPlayerLeftLobby(NetworkPlayerData player)
        {
            Debug.Log($"[LobbyRoomMenu] Player left: {player?.PlayerName}");
            
            // Show toast notification (only for host, since clients get disconnected themselves)
            if (isHost && player != null && !player.IsLocalUser)
            {
                ToastManager.ToastInformation($"{player.PlayerName} left the lobby");
            }
            
            // Unsubscribe from instrument changes
            if (player != null && _instrumentChangeSubscriptions.Contains(player))
            {
                player.OnInstrumentChangedEvent -= OnPlayerInstrumentChangedForFilter;
                _instrumentChangeSubscriptions.Remove(player);
            }
            
            // Remove the player from the track order
            if (player != null)
            {
                var trackOrderManager = TrackOrderManager.Instance;
                trackOrderManager?.RemovePlayerFromOrder(player.NetworkPlayerId);
                
                // Remove the player from their band
                var bandManager = BandManager.Instance;
                bandManager?.RemovePlayer(player.NetworkPlayerId);
            }
            
            // Refresh the song filter since player instruments affect filtering
            MultiplayerSongFilter.RefreshFilter();

            StartCoroutine(RefreshAfterDelay());
        }
        
        /// <summary>
        /// Called when any player changes their instrument. Triggers song filter refresh.
        /// </summary>
        private void OnPlayerInstrumentChangedForFilter(int instrument, int difficulty)
        {
            Debug.Log($"[LobbyRoomMenu] Player instrument changed - refreshing song filter");
            MultiplayerSongFilter.RefreshFilter();
        }
        
        private void OnHostBrowsingStateChanged(bool isBrowsing)
        {
            Debug.Log($"[LobbyRoomMenu] Host browsing state changed: {isBrowsing}");
            _hostIsBrowsingSongs = isBrowsing;
            UpdateWaitingForHostText();
            UpdateNavigationScheme();
        }
        
        /// <summary>
        /// Called when the designated host changes (for dedicated server mode).
        /// Updates the local isHost flag and refreshes the UI accordingly.
        /// </summary>
        private void OnHostChangedHandler(Guid newHostPlayerId)
        {
            var networkingService = NetworkingServiceFactory.Instance;
            bool wasHost = isHost;
            isHost = networkingService?.HasHostAuthority ?? false;
            
            Debug.Log($"[LobbyRoomMenu] Host changed: newHostPlayerId={newHostPlayerId}, wasHost={wasHost}, isHost={isHost}");
            
            if (wasHost != isHost)
            {
                // Host status changed - need to update UI
                Debug.Log($"[LobbyRoomMenu] Local player host status changed from {wasHost} to {isHost} - refreshing UI");
                
                // Pop the old navigation scheme and re-push with new host status
                if (_navigationSchemePushed)
                {
                    try
                    {
                        Navigator.Instance?.PopScheme();
                    }
                    catch (InvalidOperationException)
                    {
                        // Stack was empty, ignore
                    }
                    _navigationSchemePushed = false;
                }
                
                // Re-push navigation scheme with correct host status
                UpdateNavigationScheme();
                
                // Update session settings panel mode (host can edit, clients view only)
                UpdateControlsForRole();
                
                // Refresh player list to update host indicators
                RefreshPlayerList();
            }
        }

        /// <summary>
        /// Called when TrackOrderManager fires OnOrderChanged.
        /// </summary>
        private void OnTrackOrderChanged()
        {
            Debug.Log("[LobbyRoomMenu] Track order changed - reordering player views");
            ReorderPlayerViews();
        }
        
        /// <summary>
        /// Called when track order is received from host (client-side).
        /// </summary>
        private void OnTrackOrderReceived(List<Guid> playerOrder)
        {
            Debug.Log($"[LobbyRoomMenu] Received track order from host: {playerOrder?.Count ?? 0} players");
            ReorderPlayerViews();
        }
        
        private void OnSessionSettingsChanged(SessionSettingsData settings)
        {
            if (!isHost)
            {
                Debug.LogWarning("[LobbyRoomMenu] Non-host received settings change event - ignoring");
                return;
            }
            
            Debug.Log($"[LobbyRoomMenu] Session settings changed: LobbyName={settings.LobbyName}, MaxPlayers={settings.MaxPlayers}, BandSize={settings.BandSize}, LocalPlayersFirst={settings.LocalPlayersFirst}, AllowedGameModes=[{string.Join(", ", settings.AllowedGameModes ?? new System.Collections.Generic.List<YARG.Core.GameMode>())}]");
            
            // First, try to broadcast settings to validate and apply them
            // If validation fails (e.g., trying to block a connected player's game mode),
            // we need to revert the settings panel to the current lobby state
            bool settingsApplied = BroadcastSessionSettingsToClients(settings);
            
            if (!settingsApplied)
            {
                Debug.LogWarning("[LobbyRoomMenu] Settings validation failed - reverting panel to current lobby state");
                // Reload the settings panel with the actual current lobby settings
                LoadCurrentLobbySettings();
                return;
            }
            
            // Settings were applied successfully - update UI and save
            
            // Update lobby info display immediately
            if (lobbyNameText != null)
                lobbyNameText.text = settings.LobbyName;
            if (playerCountText != null)
            {
                var networkingService = NetworkingServiceFactory.Instance;
                var currentPlayers = networkingService?.CurrentLobby?.CurrentPlayers ?? 1;
                playerCountText.text = $"{currentPlayers}/{settings.MaxPlayers} Players";
            }
            
            // Update BandManager if band size changed
            // Note: InitializeBandManager handles the case where band size didn't change (no-op)
            var bandManager = BandManager.Instance;
            int previousBandSize = bandManager?.BandSize ?? 0;
            
            InitializeBandManager(settings.BandSize);
            
            // Check if LocalPlayersFirst setting changed
            var trackOrderManager = TrackOrderManager.Instance;
            bool previousLocalPlayersFirst = trackOrderManager?.LocalPlayersFirst ?? false;
            bool localPlayersFirstChanged = trackOrderManager != null && previousLocalPlayersFirst != settings.LocalPlayersFirst;
            
            // Update TrackOrderManager with the new LocalPlayersFirst setting
            if (trackOrderManager != null && localPlayersFirstChanged)
            {
                trackOrderManager.LocalPlayersFirst = settings.LocalPlayersFirst;
                Debug.Log($"[LobbyRoomMenu] LocalPlayersFirst changed from {previousLocalPlayersFirst} to {settings.LocalPlayersFirst}");
            }
            
            // Update MultiplayerGameplaySettings so gameplay uses the updated settings
            // This keeps TrackOrderManager and MultiplayerGameplaySettings in sync
            if (MultiplayerGameplaySettings.Instance != null)
            {
                // Get current session type from existing preset to preserve it (can't be changed in lobby room)
                var currentSessionType = MultiplayerGameplaySettings.Instance.ActivePreset?.sessionType ?? (int)settings.SessionType;
                
                var sessionPreset = new SessionPreset
                {
                    sessionName = settings.LobbyName,
                    maxPlayers = settings.MaxPlayers,
                    privacyMode = (int)settings.PrivacyMode,
                    sessionType = currentSessionType, // Preserve existing session type
                    bandSize = settings.BandSize,
                    noFailMode = settings.NoFailMode,
                    sharedSongsOnly = settings.SharedSongsOnly,
                    allowModifiers = settings.AllowModifiers,
                    enablePresetSync = settings.EnablePresetSync,
                    allowLateJoin = settings.AllowLateJoin,
                    allowedInstruments = settings.AllowedGameModes?.Select(g => (int)g).ToList() ?? new List<int>(),
                    localPlayersFirstValue = settings.LocalPlayersFirst
                };
                MultiplayerGameplaySettings.Instance.ApplyPreset(sessionPreset);
                Debug.Log($"[LobbyRoomMenu] Host applied gameplay settings: NoFail={settings.NoFailMode}, SharedSongs={settings.SharedSongsOnly}, AllowMods={settings.AllowModifiers}, LocalPlayersFirst={settings.LocalPlayersFirst}, SessionType={currentSessionType}");
            }
            
            // Also update SessionLifecycleManager preset to keep late join setting in sync
            // Note: We DON'T update sessionType here - it should remain unchanged from session start
            var sessionLifecycle = SessionLifecycleManager.Instance;
            if (sessionLifecycle != null)
            {
                sessionLifecycle.UpdatePresetSetting(preset =>
                {
                    // Note: sessionType is intentionally NOT updated - it's set at session start and shouldn't change
                    preset.sessionName = settings.LobbyName;
                    preset.maxPlayers = settings.MaxPlayers;
                    preset.privacyMode = (int)settings.PrivacyMode;
                    preset.bandSize = settings.BandSize;
                    preset.noFailMode = settings.NoFailMode;
                    preset.sharedSongsOnly = settings.SharedSongsOnly;
                    preset.allowModifiers = settings.AllowModifiers;
                    preset.enablePresetSync = settings.EnablePresetSync;
                    preset.allowLateJoin = settings.AllowLateJoin;
                    preset.allowedInstruments = settings.AllowedGameModes?.Select(g => (int)g).ToList() ?? new List<int>();
                    preset.localPlayersFirstValue = settings.LocalPlayersFirst;
                });
                Debug.Log($"[LobbyRoomMenu] Updated SessionLifecycleManager preset: allowLateJoin={settings.AllowLateJoin}");
            }
            
            // Refresh player list if band size changed OR LocalPlayersFirst changed
            // (LocalPlayersFirst affects move button visibility)
            if ((bandManager != null && previousBandSize != settings.BandSize) || localPlayersFirstChanged)
            {
                Debug.Log($"[LobbyRoomMenu] Band size changed from {previousBandSize} to {settings.BandSize} or LocalPlayersFirst changed");
                RefreshPlayerList();
            }
            
            // Save settings to the current preset (persists to disk for Server sessions)
            SaveSettingsToPreset(settings);
        }
        
        /// <summary>
        /// Broadcasts the current session settings to all connected clients.
        /// Returns true if settings were applied, false if blocked by validation.
        /// </summary>
        private bool BroadcastSessionSettingsToClients(SessionSettingsData settings)
        {
            var networkingService = NetworkingServiceFactory.Instance;
            if (networkingService == null || !networkingService.IsHosting)
            {
                return false;
            }
            
            var allowedGameModes = settings.AllowedGameModes?
                .Select(g => (int)g)
                .ToList() ?? new System.Collections.Generic.List<int>();
            
            bool success = networkingService.BroadcastSessionSettings(
                settings.LobbyName,
                settings.MaxPlayers,
                (byte)settings.PrivacyMode,
                settings.BandSize,
                settings.NoFailMode,
                settings.SharedSongsOnly,
                settings.AllowModifiers,
                settings.EnablePresetSync,
                settings.AllowLateJoin,
                allowedGameModes,
                settings.LocalPlayersFirst);
            
            if (success)
            {
                Debug.Log($"[LobbyRoomMenu] Broadcast session settings to clients: GameModes=[{string.Join(", ", settings.AllowedGameModes ?? new System.Collections.Generic.List<YARG.Core.GameMode>())}]");
            }
            else
            {
                Debug.LogWarning($"[LobbyRoomMenu] Failed to broadcast session settings - validation rejected");
            }
            
            return success;
        }
        
        /// <summary>
        /// Called when session settings are received from the host (client-side).
        /// Updates the view-only settings panel to reflect the host's settings.
        /// Also applies the settings to MultiplayerGameplaySettings so gameplay uses the host's settings.
        /// </summary>
        private void OnSessionSettingsReceivedFromHost(
            string lobbyName,
            int maxPlayers,
            byte privacyMode,
            int bandSize,
            bool noFailMode,
            bool sharedSongsOnly,
            bool allowModifiers,
            bool enablePresetSync,
            bool allowLateJoin,
            System.Collections.Generic.List<int> allowedGameModes,
            bool localPlayersFirst)
        {
            Debug.Log($"[LobbyRoomMenu] Received session settings from host: LobbyName={lobbyName}, MaxPlayers={maxPlayers}, NoFail={noFailMode}, LocalPlayersFirst={localPlayersFirst}, GameModes=[{string.Join(", ", allowedGameModes ?? new System.Collections.Generic.List<int>())}]");
            
            // Apply the settings to MultiplayerGameplaySettings so gameplay uses the host's settings
            // This is critical for clients to respect the host's NoFail setting
            if (MultiplayerGameplaySettings.Instance != null)
            {
                var sessionPreset = new SessionPreset
                {
                    sessionName = lobbyName,
                    maxPlayers = maxPlayers,
                    privacyMode = (int)privacyMode,
                    bandSize = bandSize,
                    noFailMode = noFailMode,
                    sharedSongsOnly = sharedSongsOnly,
                    allowModifiers = allowModifiers,
                    enablePresetSync = enablePresetSync,
                    allowLateJoin = allowLateJoin,
                    allowedInstruments = allowedGameModes?.ToList() ?? new List<int>(),
                    localPlayersFirstValue = localPlayersFirst
                };
                
                MultiplayerGameplaySettings.Instance.ApplyPreset(sessionPreset);
                Debug.Log($"[LobbyRoomMenu] Applied host's gameplay settings: NoFail={noFailMode}, SharedSongs={sharedSongsOnly}, AllowMods={allowModifiers}, LocalPlayersFirst={localPlayersFirst}");
            }
            
            // Update lobby info display
            if (lobbyNameText != null)
                lobbyNameText.text = lobbyName;
            if (playerCountText != null)
            {
                var networkingService = NetworkingServiceFactory.Instance;
                var currentPlayers = networkingService?.CurrentLobby?.CurrentPlayers ?? 1;
                playerCountText.text = $"{currentPlayers}/{maxPlayers} Players";
            }
            
            // Update the settings panel with the new data (view-only for clients)
            if (sessionSettingsPanel != null)
            {
                var gameModes = allowedGameModes?
                    .Select(i => (YARG.Core.GameMode)i)
                    .Where(gm => System.Enum.IsDefined(typeof(YARG.Core.GameMode), gm))
                    .ToList();
                
                var settings = new SessionSettingsData
                {
                    LobbyName = lobbyName,
                    MaxPlayers = maxPlayers,
                    PrivacyMode = (LobbyPrivacyMode)privacyMode,
                    BandSize = bandSize,
                    NoFailMode = noFailMode,
                    SharedSongsOnly = sharedSongsOnly,
                    AllowModifiers = allowModifiers,
                    EnablePresetSync = enablePresetSync,
                    AllowLateJoin = allowLateJoin,
                    AllowedGameModes = gameModes,
                    LocalPlayersFirst = localPlayersFirst
                };
                
                sessionSettingsPanel.SetData(settings);
                
                // Initialize the band manager with the received band size (client-side)
                InitializeBandManager(bandSize);
            }
        }
        
        /// <summary>
        /// Saves the current session settings to the active preset.
        /// For Server sessions, this persists changes to disk.
        /// </summary>
        private void SaveSettingsToPreset(SessionSettingsData settings)
        {
            var bookmarkStore = LobbyBookmarkStore.Instance;
            
            // ALWAYS update the HostedLobbyPreset (MyLobbies) since that's what gets loaded when re-hosting
            #pragma warning disable CS0618 // Suppress obsolete warning for MyLobbies
            var myLobbies = bookmarkStore?.MyLobbies;
            #pragma warning restore CS0618
            
            if (myLobbies != null && myLobbies.Count > 0)
            {
                var recentPreset = myLobbies[0];
                
                // IMPORTANT: Preserve the existing session type - it cannot be changed in the lobby room
                // The settings panel may have incorrect SessionType during initialization
                var sessionTypeToSave = recentPreset.SessionType;
                Debug.Log($"[LobbyRoomMenu] SaveSettingsToPreset: preserving SessionType={sessionTypeToSave} (settings had {settings.SessionType})");
                
                bookmarkStore.UpsertMyLobby(
                    recentPreset.id,
                    settings.LobbyName,
                    settings.MaxPlayers,
                    settings.PrivacyMode,
                    sessionTypeToSave, // Use preserved session type, not from settings
                    settings.Password ?? string.Empty,
                    false, // Don't update hosted timestamp for settings changes
                    settings.BandSize,
                    settings.NoFailMode,
                    settings.SharedSongsOnly,
                    settings.AllowModifiers,
                    settings.EnablePresetSync,
                    settings.AllowLateJoin,
                    settings.AllowedGameModes?.Select(g => (int)g).ToList() ?? new System.Collections.Generic.List<int>(),
                    settings.LocalPlayersFirst
                );
                Debug.Log($"[LobbyRoomMenu] Saved settings to HostedLobbyPreset '{settings.LobbyName}' (ID: {recentPreset.id}), AllowedGameModes=[{string.Join(", ", settings.AllowedGameModes ?? new System.Collections.Generic.List<YARG.Core.GameMode>())}]");
            }
            
            // Also update SessionLifecycleManager preset if available
            var currentPreset = SessionLifecycleManager.Instance?.CurrentPreset;
            if (currentPreset != null && !string.IsNullOrEmpty(currentPreset.id))
            {
                currentPreset.sessionName = settings.LobbyName;
                currentPreset.presetName = settings.LobbyName;
                currentPreset.maxPlayers = settings.MaxPlayers;
                currentPreset.privacyMode = (int)settings.PrivacyMode;
                currentPreset.password = settings.Password ?? string.Empty;
                currentPreset.bandSize = settings.BandSize;
                currentPreset.noFailMode = settings.NoFailMode;
                currentPreset.sharedSongsOnly = settings.SharedSongsOnly;
                currentPreset.allowModifiers = settings.AllowModifiers;
                currentPreset.allowedInstruments = settings.AllowedGameModes?.Select(g => (int)g).ToList() ?? new System.Collections.Generic.List<int>();
                currentPreset.localPlayersFirstValue = settings.LocalPlayersFirst;
                currentPreset.Normalize();
                
                bookmarkStore?.UpsertSessionPreset(currentPreset, updateHostedTimestamp: false);
            }
            
            // Also update MultiplayerGameplaySettings.ActivePreset if available (in-memory)
            var gameplayPreset = MultiplayerGameplaySettings.Instance?.ActivePreset;
            if (gameplayPreset != null)
            {
                gameplayPreset.sessionName = settings.LobbyName;
                gameplayPreset.presetName = settings.LobbyName;
                gameplayPreset.maxPlayers = settings.MaxPlayers;
                gameplayPreset.privacyMode = (int)settings.PrivacyMode;
                gameplayPreset.password = settings.Password ?? string.Empty;
                gameplayPreset.bandSize = settings.BandSize;
                gameplayPreset.noFailMode = settings.NoFailMode;
                gameplayPreset.sharedSongsOnly = settings.SharedSongsOnly;
                gameplayPreset.allowModifiers = settings.AllowModifiers;
                gameplayPreset.allowedInstruments = settings.AllowedGameModes?.Select(g => (int)g).ToList() ?? new System.Collections.Generic.List<int>();
                gameplayPreset.localPlayersFirstValue = settings.LocalPlayersFirst;
                gameplayPreset.Normalize();
                
                // Update the song filter to match the new SharedSongsOnly setting
                MultiplayerSongFilter.RequirePartsForAllProfiles = settings.SharedSongsOnly;
                
                // Also save to session presets store if it has an ID
                if (!string.IsNullOrEmpty(gameplayPreset.id))
                {
                    bookmarkStore?.UpsertSessionPreset(gameplayPreset, updateHostedTimestamp: false);
                }
            }
        }
        
        private void UpdateWaitingForHostText()
        {
            if (waitingForHostText == null || isHost)
                return;
            
            if (_hostIsBrowsingSongs)
            {
                waitingForHostText.text = "Host is browsing songs - Press <color=#FFFF00>Y</color> to join";
                waitingForHostText.gameObject.SetActive(true);
            }
            else
            {
                waitingForHostText.text = !string.IsNullOrEmpty(_defaultWaitingForHostText)
                    ? _defaultWaitingForHostText
                    : "Waiting for host...";
                waitingForHostText.gameObject.SetActive(true);
            }
        }
        
        private System.Collections.IEnumerator RefreshAfterDelay()
        {
            yield return null;
            RefreshLobbyInfo();
        }

        private void RefreshLobbyInfo()
        {
            Debug.Log("[LobbyRoomMenu] RefreshLobbyInfo called");
            EnsureHostAddressPanel();
            
            var networkingService = NetworkingServiceFactory.Instance;
            if (networkingService == null || networkingService.CurrentLobby == null)
            {
                Debug.LogWarning("[LobbyRoomMenu] No active networking service or lobby");
                return;
            }
            
            var lobby = networkingService.CurrentLobby;
            Debug.Log($"[LobbyRoomMenu] Using lobby: {lobby.LobbyName}, Players: {lobby.CurrentPlayers}/{lobby.MaxPlayers}");
            
            // Use HasHostAuthority for unified host detection across in-game hosting and dedicated servers
            isHost = networkingService.HasHostAuthority;
            
            // If host and track order is empty, initialize with host first
            if (isHost)
            {
                InitializeHostTrackOrder(networkingService);
            }
            
            // Update lobby info display
            if (lobbyNameText != null)
                lobbyNameText.text = lobby.LobbyName;
            if (hostNameText != null)
                hostNameText.text = $"Host: {lobby.HostName}";
            if (playerCountText != null)
                playerCountText.text = $"{lobby.CurrentPlayers}/{lobby.MaxPlayers} Players";
            
            // Format endpoints for sidebar display
            int port = lobby.Port > 0 ? lobby.Port : NetworkTransportDefaults.DefaultUdpPort;
            string lanEndpoint = FormatEndpoint(lobby.IpAddress, lobby.Port, port);
            string wanEndpoint = FormatEndpoint(lobby.PublicAddress, lobby.PublicPort, port);
            
            // Get the lobby code from the lobby or SessionLifecycleManager
            string lobbyCode = null;
            if (lobby.IsLobby && !string.IsNullOrEmpty(lobby.LobbyCode))
            {
                lobbyCode = lobby.LobbyCode;
            }
            else if (lobby.IsLobby && SessionLifecycleManager.Instance?.LobbyCode != null)
            {
                lobbyCode = SessionLifecycleManager.Instance.LobbyCode;
            }
            
            // Update the host address panel with lobby code (or LAN/WAN for Server mode)
            bool hostPanelVisible = UpdateHostAddressPanel(lobbyCode, lanEndpoint, wanEndpoint);
            
            // Update connection info text (simplified now that sidebar shows details)
            var connectionText = connectionInfoText ?? lobbyCodeText;
            if (connectionText != null)
            {
                string connectLabel = string.Empty;
                
                // For Lobby sessions with code, show simple message
                if (!string.IsNullOrEmpty(lobbyCode))
                {
                    connectLabel = isHost && hostPanelVisible 
                        ? "Share your lobby code with friends!" 
                        : $"Lobby Code: {lobbyCode}";
                }
                else
                {
                    // Server mode or no lobby code - show direct connect info
                    if (!string.IsNullOrEmpty(lanEndpoint))
                        connectLabel = $"Direct Connect (LAN): {lanEndpoint}";
                    
                    if (!string.IsNullOrEmpty(wanEndpoint) && !wanEndpoint.Equals(lanEndpoint))
                    {
                        if (!string.IsNullOrEmpty(connectLabel))
                            connectLabel += "\n";
                        connectLabel += $"Public Address: {wanEndpoint}";
                    }
                    
                    if (string.IsNullOrEmpty(connectLabel))
                        connectLabel = "Direct Connect: Resolving...";
                    
                    if (isHost && hostPanelVisible)
                        connectLabel = "Direct connect details are shown below.";
                }
                
                connectionText.text = connectLabel;
            }
            
            UpdateControlsForRole();
            RefreshPlayerList();
            UpdateNavigationScheme();
            Canvas.ForceUpdateCanvases();
        }

        private static string FormatEndpoint(string address, int port, int fallbackPort)
        {
            if (string.IsNullOrWhiteSpace(address))
                return string.Empty;

            int finalPort = port > 0 ? port : fallbackPort;
            return finalPort > 0 ? $"{address}:{finalPort}" : address;
        }

        private bool EnsureHostAddressPanel()
        {
            if (!HasPrefabHostPanel())
            {
                Debug.LogWarning("[LobbyRoomMenu] Host address UI references are missing.");
                return false;
            }

            BindHostPanelButtons();
            return true;
        }

        private bool HasPrefabHostPanel()
        {
            return hostAddressPanel != null &&
                   lanAddressRow != null &&
                   lanAddressValueText != null &&
                   lanVisibilityToggleButton != null &&
                   lanCopyButton != null &&
                   wanAddressRow != null &&
                   wanAddressValueText != null &&
                   wanVisibilityToggleButton != null &&
                   wanCopyButton != null;
        }

        private void BindHostPanelButtons()
        {
            if (_hostPanelListenersBound)
                return;

            if (lobbyCodeVisibilityToggleButton != null)
                lobbyCodeVisibilityToggleButton.onClick.AddListener(ToggleLobbyCodeVisibility);
            if (lobbyCodeCopyButton != null)
                lobbyCodeCopyButton.onClick.AddListener(CopyLobbyCode);
            if (lanVisibilityToggleButton != null)
                lanVisibilityToggleButton.onClick.AddListener(ToggleLanVisibility);
            if (lanCopyButton != null)
                lanCopyButton.onClick.AddListener(CopyLanAddress);
            if (wanVisibilityToggleButton != null)
                wanVisibilityToggleButton.onClick.AddListener(ToggleWanVisibility);
            if (wanCopyButton != null)
                wanCopyButton.onClick.AddListener(CopyWanAddress);

            _hostPanelListenersBound = true;
        }

        private bool UpdateHostAddressPanel(string lobbyCode, string lanEndpoint, string wanEndpoint)
        {
            if (!EnsureHostAddressPanel())
                return false;

            if (!isHost)
            {
                SetHostSidebarActive(false);
                return false;
            }

            // Apply lobby code row first (most prominent for Lobby sessions)
            bool hasLobbyCode = ApplyEndpointRow(ref _lobbyCode, lobbyCode, ref _lobbyCodeVisible, lobbyCodeRow, lobbyCodeValueText, lobbyCodeVisibilityToggleButton, lobbyCodeVisibilityToggleLabel, lobbyCodeCopyButton);
            
            // Hide LAN/WAN rows when we have a lobby code (cleaner UI)
            if (hasLobbyCode)
            {
                if (lanAddressRow != null) lanAddressRow.SetActive(false);
                if (wanAddressRow != null) wanAddressRow.SetActive(false);
            }
            else
            {
                // No lobby code - show LAN/WAN addresses for Server mode
                ApplyEndpointRow(ref _lanAddress, lanEndpoint, ref _lanVisible, lanAddressRow, lanAddressValueText, lanVisibilityToggleButton, lanVisibilityToggleLabel, lanCopyButton);
                ApplyEndpointRow(ref _wanAddress, wanEndpoint, ref _wanVisible, wanAddressRow, wanAddressValueText, wanVisibilityToggleButton, wanVisibilityToggleLabel, wanCopyButton);
            }

            SetHostSidebarActive(true);
            return true;
        }

        private void SetHostSidebarActive(bool active)
        {
            if (hostAddressPanel != null)
                hostAddressPanel.SetActive(active);

            var container = hostAddressPanelParent != null
                ? hostAddressPanelParent.gameObject
                : hostAddressPanel?.transform.parent?.gameObject;

            if (container != null && container != hostAddressPanel && active)
                container.SetActive(true);
        }

        private bool ApplyEndpointRow(ref string cachedValue, string incomingValue, ref bool isVisible, GameObject row, TextMeshProUGUI valueLabel, Button toggleButton, TextMeshProUGUI toggleLabel, Button copyButton)
        {
            string previousValue = cachedValue ?? string.Empty;
            string sanitized = SanitizeEndpoint(incomingValue, previousValue);
            bool valueChanged = !string.Equals(previousValue, sanitized, StringComparison.Ordinal);
            cachedValue = sanitized;
            bool hasValue = !string.IsNullOrEmpty(sanitized);

            // Only show the row if there's actually a value to display
            // (e.g., hide lobby code row for Server mode where there's no code)
            if (row != null)
                row.SetActive(hasValue);
            if (copyButton != null)
                copyButton.interactable = hasValue;
            if (!hasValue || valueChanged)
                isVisible = false;

            RefreshEndpointDisplay(sanitized, isVisible, valueLabel, toggleLabel, toggleButton, hasValue);
            return hasValue;
        }

        private static string SanitizeEndpoint(string incomingValue, string fallback)
        {
            // If incoming value is provided, use it (trimmed)
            if (!string.IsNullOrWhiteSpace(incomingValue))
                return incomingValue.Trim();
            
            // Fall back to previous value if incoming is empty
            // This preserves values during async updates (e.g., STUN resolution)
            // NOTE: Stale values from previous sessions are cleared in OnEnable
            return string.IsNullOrWhiteSpace(fallback) ? string.Empty : fallback.Trim();
        }

        private void RefreshEndpointDisplay(string endpoint, bool isVisible, TextMeshProUGUI valueLabel, TextMeshProUGUI toggleLabel, Button toggleButton, bool toggleAvailable)
        {
            if (valueLabel != null)
                valueLabel.text = !toggleAvailable ? "Resolving..." : (isVisible ? endpoint : "****");
            if (toggleButton != null)
                toggleButton.interactable = toggleAvailable;
            if (toggleLabel != null)
                toggleLabel.text = toggleAvailable ? (isVisible ? "Hide" : "Show") : "Show";

            UpdateVisibilityToggleIcon(toggleButton, toggleAvailable ? (bool?)isVisible : null);
        }

        private void UpdateVisibilityToggleIcon(Button toggleButton, bool? isVisible)
        {
            if (toggleButton == null)
                return;

            var image = toggleButton.image;
            if (image == null || (visibilityVisibleSprite == null && visibilityHiddenSprite == null))
                return;

            if (!isVisible.HasValue)
            {
                image.enabled = visibilityHiddenSprite != null || visibilityVisibleSprite != null;
                image.sprite = visibilityHiddenSprite ?? visibilityVisibleSprite;
                return;
            }

            if (isVisible.Value)
            {
                image.enabled = visibilityVisibleSprite != null;
                if (visibilityVisibleSprite != null)
                    image.sprite = visibilityVisibleSprite;
            }
            else
            {
                image.enabled = visibilityHiddenSprite != null;
                if (visibilityHiddenSprite != null)
                    image.sprite = visibilityHiddenSprite;
            }
        }

        private void ToggleEndpointVisibility(ref bool visibility, string endpoint, TextMeshProUGUI valueLabel, TextMeshProUGUI toggleLabel, Button toggleButton)
        {
            if (string.IsNullOrEmpty(endpoint))
            {
                ToastManager.ToastWarning("No address is available yet to show.");
                return;
            }

            visibility = !visibility;
            RefreshEndpointDisplay(endpoint, visibility, valueLabel, toggleLabel, toggleButton, true);
        }

        private void ToggleLobbyCodeVisibility() => ToggleEndpointVisibility(ref _lobbyCodeVisible, _lobbyCode, lobbyCodeValueText, lobbyCodeVisibilityToggleLabel, lobbyCodeVisibilityToggleButton);
        private void ToggleLanVisibility() => ToggleEndpointVisibility(ref _lanVisible, _lanAddress, lanAddressValueText, lanVisibilityToggleLabel, lanVisibilityToggleButton);
        private void ToggleWanVisibility() => ToggleEndpointVisibility(ref _wanVisible, _wanAddress, wanAddressValueText, wanVisibilityToggleLabel, wanVisibilityToggleButton);
        private void CopyLobbyCode() => CopyEndpointToClipboard(_lobbyCode, "Lobby code");
        private void CopyLanAddress() => CopyEndpointToClipboard(_lanAddress, "LAN");
        private void CopyWanAddress() => CopyEndpointToClipboard(_wanAddress, "WAN");

        private void CopyEndpointToClipboard(string endpoint, string label)
        {
            if (string.IsNullOrEmpty(endpoint))
            {
                ToastManager.ToastWarning($"No {label} address available yet.");
                return;
            }

            GUIUtility.systemCopyBuffer = endpoint;
            ToastManager.ToastInformation($"{label} address copied to clipboard.");
        }

        private void UpdateControlsForRole()
        {
            if (browseSongsButton != null)
                browseSongsButton.gameObject.SetActive(isHost);
            
            if (waitingForHostText != null && !isHost)
            {
                if (string.IsNullOrEmpty(waitingForHostText.text))
                {
                    waitingForHostText.text = !string.IsNullOrEmpty(_defaultWaitingForHostText)
                        ? _defaultWaitingForHostText
                        : "Waiting for host...";
                }
            }
            
            // Configure session settings panel based on role
            ConfigureSessionSettingsPanel();
        }
        
        private void ConfigureSessionSettingsPanel()
        {
            Debug.Log($"[LobbyRoomMenu] ConfigureSessionSettingsPanel: panel={sessionSettingsPanel != null}, " +
                $"panelActive={(sessionSettingsPanel?.gameObject.activeInHierarchy ?? false)}, " +
                $"panelEnabled={(sessionSettingsPanel?.enabled ?? false)}, " +
                $"divider={sessionSettingsDivider != null}");
            
            if (sessionSettingsPanel == null)
            {
                Debug.LogWarning("[LobbyRoomMenu] ConfigureSessionSettingsPanel: sessionSettingsPanel is NULL!");
                return;
            }
            
            // CRITICAL: Ensure the panel's GameObject is active before configuring
            // After scene reload, the panel may be on an inactive child hierarchy
            if (!sessionSettingsPanel.gameObject.activeInHierarchy)
            {
                Debug.Log("[LobbyRoomMenu] ConfigureSessionSettingsPanel: Activating panel GameObject");
                sessionSettingsPanel.gameObject.SetActive(true);
            }
            
            // Show/hide the divider
            if (sessionSettingsDivider != null)
                sessionSettingsDivider.SetActive(true);
            
            // Configure based on host vs player role
            // In dedicated server mode, even the designated host sees settings as view-only
            // because the server controls settings, not the host player
            var mode = SettingsPanelMode.ViewOnly;
            if (isHost)
            {
                var networkingService = NetworkingServiceFactory.Instance;
                bool connectedToDedicatedServer = networkingService?.CurrentLobby?.IsDedicatedServer ?? false;
                
                // If we're hosting ourselves OR connected to a non-dedicated server, allow editing
                // If connected to a dedicated server, settings are read-only
                if (!connectedToDedicatedServer)
                {
                    // Check if the dedicated server manager allows settings changes (only relevant if we ARE the server)
                    var dedicatedManager = YARG.Networking.DedicatedServer.DedicatedServerManager.Instance;
                    if (dedicatedManager == null || dedicatedManager.CanHostChangeSettings())
                    {
                        mode = SettingsPanelMode.HostEdit;
                    }
                }
                // else: connected to dedicated server, stay in ViewOnly mode
            }
            sessionSettingsPanel.Configure(mode);
            
            // Load current lobby settings into the panel
            LoadCurrentLobbySettings();
            
            Debug.Log($"[LobbyRoomMenu] Configured session settings panel for mode: {mode}");
        }
        
        private void LoadCurrentLobbySettings()
        {
            Debug.Log($"[LobbyRoomMenu] LoadCurrentLobbySettings: panel={sessionSettingsPanel != null}, " +
                $"panelActive={(sessionSettingsPanel?.gameObject.activeInHierarchy ?? false)}");
            
            if (sessionSettingsPanel == null)
            {
                Debug.LogWarning("[LobbyRoomMenu] LoadCurrentLobbySettings: sessionSettingsPanel is NULL!");
                return;
            }
            
            var networkingService = NetworkingServiceFactory.Instance;
            if (networkingService?.CurrentLobby == null)
                return;
            
            var lobby = networkingService.CurrentLobby;
            
            // Try to get settings from active preset first
            var activePreset = MultiplayerGameplaySettings.Instance?.ActivePreset;
            var currentPreset = SessionLifecycleManager.Instance?.CurrentPreset;
            
            // Also check HostedLobbyPreset from bookmark store
            HostedLobbyPreset hostedPreset = null;
            var bookmarkStore = LobbyBookmarkStore.Instance;
            if (bookmarkStore?.MyLobbies?.Count > 0)
            {
                hostedPreset = bookmarkStore.MyLobbies[0];
            }
            
            // Create settings data from current lobby, loading from preset if available
            // For allowed game modes, check if list is non-empty (not just non-null)
            var activeAllowedGameModes = activePreset?.allowedInstruments?.Select(i => (YARG.Core.GameMode)i).ToList();
            var hostedAllowedGameModes = hostedPreset?.allowedInstruments?.Select(i => (YARG.Core.GameMode)i).ToList();
            
            var settings = new SessionSettingsData
            {
                LobbyName = lobby.LobbyName ?? "Unnamed Lobby",
                MaxPlayers = lobby.MaxPlayers,
                PrivacyMode = hostedPreset?.PrivacyMode ?? LobbyPrivacyMode.Public,
                Password = hostedPreset?.password ?? string.Empty,
                
                // Load gameplay settings from active preset, fallback to hosted preset, then defaults
                BandSize = activePreset?.bandSize ?? hostedPreset?.bandSize ?? 0,
                NoFailMode = activePreset?.noFailMode ?? hostedPreset?.noFailMode ?? false,
                SharedSongsOnly = activePreset?.sharedSongsOnly ?? hostedPreset?.sharedSongsOnly ?? true,
                AllowModifiers = activePreset?.AllowModifiers ?? hostedPreset?.allowModifiers ?? true,
                
                // Session settings from current preset or hosted preset
                EnablePresetSync = currentPreset?.enablePresetSync ?? hostedPreset?.enablePresetSync ?? true,
                AllowLateJoin = currentPreset?.allowLateJoin ?? hostedPreset?.allowLateJoin ?? true,
                
                // Game mode restrictions - prefer non-empty list from activePreset, else hostedPreset
                AllowedGameModes = (activeAllowedGameModes != null && activeAllowedGameModes.Count > 0) 
                    ? activeAllowedGameModes
                    : (hostedAllowedGameModes != null && hostedAllowedGameModes.Count > 0)
                        ? hostedAllowedGameModes
                        : new System.Collections.Generic.List<YARG.Core.GameMode>(),
                LocalPlayersFirst = activePreset?.LocalPlayersFirst ?? hostedPreset?.localPlayersFirst ?? false
            };
            
            Debug.Log($"[LobbyRoomMenu] LoadCurrentLobbySettings: BandSize={settings.BandSize}, NoFail={settings.NoFailMode}, SharedSongs={settings.SharedSongsOnly}, AllowMods={settings.AllowModifiers}, LocalPlayersFirst={settings.LocalPlayersFirst}, AllowLateJoin={settings.AllowLateJoin}, EnablePresetSync={settings.EnablePresetSync}");
            Debug.Log($"[LobbyRoomMenu] LoadCurrentLobbySettings sources: ActivePreset={activePreset != null}, CurrentPreset={currentPreset != null}, HostedPreset={hostedPreset != null}");
            Debug.Log($"[LobbyRoomMenu] LoadCurrentLobbySettings source values: activePreset.allowLateJoin={activePreset?.allowLateJoin}, currentPreset.allowLateJoin={currentPreset?.allowLateJoin}, hostedPreset.allowLateJoin={hostedPreset?.allowLateJoin}");
            Debug.Log($"[LobbyRoomMenu] LoadCurrentLobbySettings AllowedGameModes: Count={settings.AllowedGameModes?.Count ?? 0}, Items=[{string.Join(", ", settings.AllowedGameModes ?? new System.Collections.Generic.List<YARG.Core.GameMode>())}]");
            Debug.Log($"[LobbyRoomMenu] LoadCurrentLobbySettings HostedPreset.allowedInstruments: Count={hostedPreset?.allowedInstruments?.Count ?? 0}, Items=[{string.Join(", ", hostedPreset?.allowedInstruments ?? new System.Collections.Generic.List<int>())}]");
            
            sessionSettingsPanel.SetData(settings);
            
            // Initialize the band manager with the current band size
            InitializeBandManager(settings.BandSize);
            
            // If we're the host, broadcast the initial settings to update _currentLobby for discovery
            // This ensures discovery responses include the correct gameplay settings from the start
            if (networkingService.IsHosting)
            {
                BroadcastSessionSettingsToClients(settings);
                Debug.Log("[LobbyRoomMenu] Host: Broadcasted initial settings to update discovery info");
            }
        }
        
        /// <summary>
        /// Initializes the BandManager with the given band size and assigns current players to bands.
        /// Uses a stable seed based on lobby ID so band names are consistent across all clients.
        /// </summary>
        private void InitializeBandManager(int bandSize, bool forceReinitialize = false)
        {
            var bandManager = BandManager.Instance;
            if (bandManager == null)
            {
                Debug.LogWarning("[LobbyRoomMenu] BandManager.Instance is null, cannot initialize bands");
                return;
            }
            
            var networkingService = NetworkingServiceFactory.Instance;
            if (networkingService == null) return;
            
            var lobby = networkingService.CurrentLobby;
            string lobbyId = lobby?.LobbyId?.ToString() ?? "unknown";
            
            // Generate a stable seed from the lobby ID so all clients get the same band names
            int stableSeed = GetStableLobbyIdSeed(lobbyId);
            
            // Initialize the band manager with stable seed
            // BandManager will skip if already initialized for this lobby with same band size
            bool wasInitialized = bandManager.IsInitialized;
            bandManager.Initialize(bandSize, stableSeed, lobbyId, forceReinitialize);
            bool justInitialized = !wasInitialized && bandManager.IsInitialized;
            
            // Only assign players to bands if we just initialized (not on every RefreshLobbyInfo call)
            // For host: new players should be assigned via OnPlayersJoinedFromConnection, not here
            // For client: bands come from host via network sync
            if (justInitialized || forceReinitialize)
            {
                Debug.Log($"[LobbyRoomMenu] Initialized BandManager with band size: {bandSize}");
                
                // Assign all current players to bands (only on initial setup or reinit)
                AssignCurrentPlayersToBands(bandManager, networkingService);
                
                // Host broadcasts band assignments to all clients
                if (isHost)
                {
                    BroadcastBandAssignments();
                }
            }
            
            // Refresh the player list now that bands are set up
            // This is necessary because RefreshPlayerList may have been called before bands were initialized
            RefreshPlayerList();
        }
        
        /// <summary>
        /// Generates a stable seed from a lobby ID string.
        /// Uses a deterministic hash algorithm since string.GetHashCode() varies between processes.
        /// </summary>
        private static int GetStableLobbyIdSeed(string lobbyId)
        {
            if (string.IsNullOrEmpty(lobbyId) || lobbyId == "unknown")
            {
                // Fallback: use a random seed but log a warning
                Debug.LogWarning("[LobbyRoomMenu] No lobby ID available for stable seed, band names may differ across clients");
                return UnityEngine.Random.Range(int.MinValue, int.MaxValue);
            }
            
            // Use a deterministic hash algorithm instead of string.GetHashCode()
            // string.GetHashCode() is randomized per-process in .NET Core/5+
            return GetDeterministicStringHash(lobbyId);
        }
        
        /// <summary>
        /// Computes a deterministic hash code for a string.
        /// Unlike string.GetHashCode(), this produces the same result across all processes/machines.
        /// Uses a simple djb2-like algorithm.
        /// </summary>
        private static int GetDeterministicStringHash(string str)
        {
            unchecked
            {
                int hash = 5381;
                foreach (char c in str)
                {
                    hash = ((hash << 5) + hash) ^ c; // hash * 33 XOR c
                }
                return hash;
            }
        }
        
        /// <summary>
        /// Assigns all currently connected players to bands.
        /// </summary>
        private void AssignCurrentPlayersToBands(BandManager bandManager, INetworkingService networkingService)
        {
            var connectedPlayers = networkingService.GetConnectedPlayers();
            if (connectedPlayers == null || connectedPlayers.Count == 0)
            {
                Debug.Log("[LobbyRoomMenu] No connected players to assign to bands yet");
                return;
            }
            
            // Group players by connection ID and assign each group to a band
            int connectionIndex = 1; // Start from 1, 0 might be reserved
            foreach (var kvp in connectedPlayers)
            {
                var connectionId = kvp.Key;
                var players = kvp.Value;
                
                if (players == null || players.Count == 0) continue;
                
                var playerIds = players.Select(p => p.NetworkPlayerId).ToList();
                
                // Check if all players in this group are already assigned to a band
                bool allAssigned = playerIds.All(pid => bandManager.GetPlayerBandId(pid) >= 0);
                if (allAssigned)
                {
                    // Already assigned, skip
                    continue;
                }
                
                // Check if this is the local connection
                bool isLocalConnection = players.Any(p => p.IsLocalUser);
                
                // Use a consistent connection ID (use hash of first player's NetworkPlayerId for persistence)
                int effectiveConnectionId = players[0].ConnectionId != Guid.Empty 
                    ? players[0].ConnectionId.GetHashCode() 
                    : connectionIndex++;
                    
                if (isLocalConnection)
                {
                    bandManager.SetLocalConnectionId(effectiveConnectionId);
                }
                
                int assignedBandId = bandManager.AssignClientPlayersToBand(effectiveConnectionId, playerIds, isLocalConnection);
                Debug.Log($"[LobbyRoomMenu] Assigned {playerIds.Count} players from connection {effectiveConnectionId} to band {assignedBandId}");
            }
        }
        
        private void MoveWaitingTextToContainer()
        {
            if (waitingForHostText == null || playerListContainer == null)
                return;
            
            waitingForHostText.transform.SetParent(playerListContainer, false);
            waitingForHostText.transform.SetAsLastSibling();
            
            var rectTransform = waitingForHostText.GetComponent<RectTransform>();
            if (rectTransform != null)
            {
                rectTransform.localScale = Vector3.one;
                rectTransform.anchorMin = new Vector2(0, 0.5f);
                rectTransform.anchorMax = new Vector2(1, 0.5f);
                rectTransform.pivot = new Vector2(0.5f, 0.5f);
                var layoutElement = waitingForHostText.GetComponent<LayoutElement>();
                if (layoutElement == null)
                    layoutElement = waitingForHostText.gameObject.AddComponent<LayoutElement>();
                layoutElement.minHeight = 40;
                layoutElement.preferredHeight = 40;
            }
        }
        
        /// <summary>
        /// Updates the browse songs button state based on band configuration.
        /// Disables the button if any band has more players than the band size allows.
        /// </summary>
        private void UpdateBandValidationState()
        {
            if (browseSongsButton == null || !isHost)
                return;
            
            var bandManager = BandManager.Instance;
            
            // Enable button if bands not enabled
            if (bandManager == null || !bandManager.AreBandsEnabled)
            {
                browseSongsButton.interactable = true;
                return;
            }
            
            var validation = bandManager.ValidateForGameStart();
            browseSongsButton.interactable = validation.IsValid;
        }

        private void RefreshPlayerList()
        {
            if (playerListContainer == null)
            {
                Debug.LogWarning("[LobbyRoomMenu] playerListContainer is null!");
                return;
            }
            
            playerViews.Clear();
            bandHeaderViews.Clear();
            selectedPlayerView = null;
            
            foreach (Transform child in playerListContainer)
            {
                if (waitingForHostText != null && child.gameObject == waitingForHostText.gameObject)
                    continue;
                Destroy(child.gameObject);
            }

            var networkingService = NetworkingServiceFactory.Instance;
            if (networkingService == null || networkingService.CurrentLobby == null)
            {
                Debug.Log("[LobbyRoomMenu] No players to display");
                return;
            }
            
            var connectedPlayers = networkingService.GetConnectedPlayers();
            if (connectedPlayers == null || connectedPlayers.Count == 0)
            {
                Debug.Log("[LobbyRoomMenu] No connected players yet");
                return;
            }
            
            var allPlayers = new List<NetworkPlayerData>();
            foreach (var kvp in connectedPlayers)
            {
                if (kvp.Value != null)
                    allPlayers.AddRange(kvp.Value);
            }
            
            // Subscribe to instrument changes for any players we haven't subscribed to yet
            foreach (var player in allPlayers)
            {
                if (player != null && !_instrumentChangeSubscriptions.Contains(player))
                {
                    player.OnInstrumentChangedEvent += OnPlayerInstrumentChangedForFilter;
                    _instrumentChangeSubscriptions.Add(player);
                }
            }
            
            // Check if bands are enabled
            var bandManager = BandManager.Instance;
            bool bandsEnabled = bandManager != null && bandManager.AreBandsEnabled;
            
            if (bandsEnabled)
            {
                RefreshPlayerListWithBands(allPlayers, bandManager);
            }
            else
            {
                RefreshPlayerListNoBands(allPlayers);
            }
            
            MoveWaitingTextToContainer();
            
            // Update browse songs button state based on band validation (host only)
            if (isHost)
            {
                UpdateBandValidationState();
            }
        }
        
        /// <summary>
        /// Refreshes the player list without band grouping (band size = 0).
        /// </summary>
        private void RefreshPlayerListNoBands(List<NetworkPlayerData> allPlayers)
        {
            var orderedPlayers = GetOrderedPlayers(allPlayers);

            Debug.Log($"[LobbyRoomMenu] Refreshing player list with {orderedPlayers.Count} players (no bands)");
            for (int i = 0; i < orderedPlayers.Count; i++)
            {
                CreatePlayerView(orderedPlayers[i], i, orderedPlayers.Count);
                if (i < orderedPlayers.Count - 1)
                    CreateDivider();
            }
        }
        
        /// <summary>
        /// Refreshes the player list with band grouping.
        /// </summary>
        private void RefreshPlayerListWithBands(List<NetworkPlayerData> allPlayers, BandManager bandManager)
        {
            var orderedPlayers = GetOrderedPlayers(allPlayers);
            
            // Group players by band
            var playersByBand = new Dictionary<int, List<NetworkPlayerData>>();
            var unassignedPlayers = new List<NetworkPlayerData>();
            
            foreach (var player in orderedPlayers)
            {
                int bandId = bandManager.GetPlayerBandId(player.NetworkPlayerId);
                if (bandId >= 0)
                {
                    if (!playersByBand.ContainsKey(bandId))
                    {
                        playersByBand[bandId] = new List<NetworkPlayerData>();
                    }
                    playersByBand[bandId].Add(player);
                }
                else
                {
                    unassignedPlayers.Add(player);
                }
            }
            
            // Sort bands by ID for consistent display
            var sortedBandIds = playersByBand.Keys.OrderBy(id => id).ToList();
            
            Debug.Log($"[LobbyRoomMenu] Refreshing player list with {orderedPlayers.Count} players in {sortedBandIds.Count} bands");
            
            int globalPlayerIndex = 0;
            bool isFirstBand = true;
            
            foreach (var bandId in sortedBandIds)
            {
                var bandInfo = bandManager.Bands.TryGetValue(bandId, out var info) ? info : null;
                if (bandInfo == null) continue;
                
                // Add divider before band (except first)
                if (!isFirstBand)
                {
                    CreateDivider();
                }
                isFirstBand = false;
                
                // Create band header
                CreateBandHeaderView(bandInfo, bandManager.BandSize);
                
                // Add players in this band
                // When bands are enabled, use band-relative index for reorder button state
                var bandPlayers = playersByBand[bandId];
                var allBands = bandManager.Bands.Values.ToList();
                for (int i = 0; i < bandPlayers.Count; i++)
                {
                    CreateDivider();
                    // Pass band-relative index and band count for proper button state
                    // Also pass band info for the move to band dropdown
                    CreatePlayerViewWithBandIndex(bandPlayers[i], i, bandPlayers.Count, bandId, allBands, bandManager.BandSize);
                    globalPlayerIndex++;
                }
            }
            
            // Add unassigned players at the end (shouldn't normally happen)
            if (unassignedPlayers.Count > 0)
            {
                Debug.LogWarning($"[LobbyRoomMenu] {unassignedPlayers.Count} players not assigned to any band");
                foreach (var player in unassignedPlayers)
                {
                    CreateDivider();
                    CreatePlayerView(player, globalPlayerIndex, orderedPlayers.Count);
                    globalPlayerIndex++;
                }
            }
        }
        
        /// <summary>
        /// Creates a band header view.
        /// </summary>
        private void CreateBandHeaderView(BandInfo bandInfo, int bandSize)
        {
            if (playerEntryPrefab == null || playerListContainer == null || bandInfo == null)
                return;
            
            var entry = Instantiate(playerEntryPrefab, playerListContainer);
            entry.SetActive(true);
            entry.name = $"BandHeader_{bandInfo.BandId}_{bandInfo.DisplayName}";
            
            var rectTransform = entry.GetComponent<RectTransform>();
            if (rectTransform != null)
            {
                rectTransform.localScale = Vector3.one;
                rectTransform.anchorMin = new Vector2(0, 0.5f);
                rectTransform.anchorMax = new Vector2(1, 0.5f);
                rectTransform.pivot = new Vector2(0.5f, 0.5f);
            }
            
            var playerView = entry.GetComponent<PlayerView>();
            if (playerView == null)
                playerView = entry.AddComponent<PlayerView>();
            
            // Check if user can regenerate this band's name
            var networkingService = NetworkingServiceFactory.Instance;
            var bandManager = BandManager.Instance;
            bool canRegenerate = false;
            if (networkingService != null && bandManager != null)
            {
                // Host can always regenerate
                if (networkingService.IsHosting)
                {
                    canRegenerate = true;
                }
                else
                {
                    // Check if local player is in this band
                    int localBandId = bandManager.LocalPlayerBandId;
                    canRegenerate = localBandId == bandInfo.BandId;
                    // Note: Clients can see the button but can't regenerate yet (would need network request)
                }
            }
            
            playerView.InitializeAsBandHeader(bandInfo, bandSize, canRegenerate);
            playerView.OnRegenerateNameClicked += OnRegenerateBandName;
            
            bandHeaderViews[bandInfo.BandId] = playerView;
            
            Debug.Log($"[LobbyRoomMenu] Created band header: {bandInfo.DisplayName}");
        }
        
        /// <summary>
        /// Called when user clicks regenerate button on a band header.
        /// </summary>
        private void OnRegenerateBandName(int bandId)
        {
            var bandManager = BandManager.Instance;
            if (bandManager == null) return;
            
            var networkingService = NetworkingServiceFactory.Instance;
            if (networkingService == null) return;
            
            // Host can regenerate any band's name
            // Non-host can only regenerate their own band's name
            if (!networkingService.IsHosting && !IsLocalPlayerInBand(bandId))
            {
                Debug.Log($"[LobbyRoomMenu] Cannot regenerate band {bandId} name - local player is not in this band");
                return;
            }
            
            if (networkingService.IsHosting)
            {
                // Host: regenerate directly
                RegenerateBandNameAsHost(bandId);
            }
            else
            {
                // Client: send request to host
                RequestBandNameChangeFromHost(bandId);
            }
        }
        
        /// <summary>
        /// Checks if any local player is in the specified band.
        /// </summary>
        private bool IsLocalPlayerInBand(int bandId)
        {
            var bandManager = BandManager.Instance;
            if (bandManager == null || !bandManager.Bands.TryGetValue(bandId, out var bandInfo))
                return false;
            
            var networkingService = NetworkingServiceFactory.Instance;
            if (networkingService == null)
                return false;
            
            // Get local player IDs
            var connectedPlayers = networkingService.GetConnectedPlayers();
            foreach (var kvp in connectedPlayers)
            {
                foreach (var player in kvp.Value)
                {
                    if (player.IsLocalUser && bandInfo.PlayerIds.Contains(player.NetworkPlayerId))
                    {
                        return true;
                    }
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Regenerates a band name (host-side only).
        /// </summary>
        private void RegenerateBandNameAsHost(int bandId)
        {
            var bandManager = BandManager.Instance;
            if (bandManager == null) return;
            
            string newName = bandManager.RegenerateBandName(bandId);
            if (!string.IsNullOrEmpty(newName))
            {
                Debug.Log($"[LobbyRoomMenu] Regenerated band {bandId} name to: {newName}");
                
                // Update the header view
                if (bandHeaderViews.TryGetValue(bandId, out var headerView))
                {
                    if (bandManager.Bands.TryGetValue(bandId, out var bandInfo))
                    {
                        headerView.UpdateBandInfo(bandInfo, bandManager.BandSize);
                    }
                }
                
                // Broadcast name change to clients
                BroadcastBandNameChange(bandId, newName, bandManager.Bands.TryGetValue(bandId, out var band) ? band.NameRegenerationCount : 0);
            }
        }
        
        /// <summary>
        /// Sends a request to the host to regenerate a band name (client-side).
        /// </summary>
        private void RequestBandNameChangeFromHost(int bandId)
        {
            var networkingService = NetworkingServiceFactory.Instance;
            if (networkingService == null || networkingService.IsHosting) return;
            
            if (networkingService is LiteNetNetworkingAdapter liteNetAdapter)
            {
                // Get a local player ID for the request (for validation on host)
                Guid localPlayerId = Guid.Empty;
                var connectedPlayers = networkingService.GetConnectedPlayers();
                foreach (var kvp in connectedPlayers)
                {
                    foreach (var player in kvp.Value)
                    {
                        if (player.IsLocalUser)
                        {
                            localPlayerId = player.NetworkPlayerId;
                            break;
                        }
                    }
                    if (localPlayerId != Guid.Empty) break;
                }
                
                if (localPlayerId == Guid.Empty)
                {
                    Debug.LogWarning("[LobbyRoomMenu] Cannot request band name change - no local player found");
                    return;
                }
                
                liteNetAdapter.RequestBandNameChange(bandId, localPlayerId);
                Debug.Log($"[LobbyRoomMenu] Client: Sent band name change request for band {bandId}");
            }
        }
        
        /// <summary>
        /// Called when a client requests a band name change (host-side).
        /// </summary>
        private void OnBandNameChangeRequestedFromClient(int bandId, Guid requestingPlayerId)
        {
            var networkingService = NetworkingServiceFactory.Instance;
            if (networkingService == null || !networkingService.IsHosting) return;
            
            var bandManager = BandManager.Instance;
            if (bandManager == null) return;
            
            // Validate that the requesting player is actually in the band
            if (!bandManager.Bands.TryGetValue(bandId, out var bandInfo))
            {
                Debug.LogWarning($"[LobbyRoomMenu] Host: Rejecting band name change request - band {bandId} doesn't exist");
                return;
            }
            
            if (!bandInfo.PlayerIds.Contains(requestingPlayerId))
            {
                Debug.LogWarning($"[LobbyRoomMenu] Host: Rejecting band name change request - player {requestingPlayerId} is not in band {bandId}");
                return;
            }
            
            Debug.Log($"[LobbyRoomMenu] Host: Processing band name change request from {requestingPlayerId} for band {bandId}");
            
            // Regenerate the name and broadcast to all clients
            RegenerateBandNameAsHost(bandId);
        }
        
        /// <summary>
        /// Broadcasts band assignments to all connected clients (host-side).
        /// Should be called after band assignments change (e.g., new players join, band size changes).
        /// </summary>
        private void BroadcastBandAssignments()
        {
            var networkingService = NetworkingServiceFactory.Instance;
            if (networkingService == null || !networkingService.IsHosting) return;
            
            var bandManager = BandManager.Instance;
            if (bandManager == null || !bandManager.IsInitialized) return;
            
            if (networkingService is LiteNetNetworkingAdapter liteNetAdapter)
            {
                var syncData = bandManager.ExportSyncData();
                liteNetAdapter.BroadcastBandAssignments(syncData);
                Debug.Log($"[LobbyRoomMenu] Host: Broadcasted band assignments to clients");
            }
        }
        
        /// <summary>
        /// Broadcasts a band name change to all connected clients (host-side).
        /// </summary>
        private void BroadcastBandNameChange(int bandId, string newName, int regenerationCount)
        {
            var networkingService = NetworkingServiceFactory.Instance;
            if (networkingService == null || !networkingService.IsHosting) return;
            
            if (networkingService is LiteNetNetworkingAdapter liteNetAdapter)
            {
                liteNetAdapter.BroadcastBandNameChange(bandId, newName, regenerationCount);
            }
        }
        
        /// <summary>
        /// Called when band assignment data is received from host (client-side).
        /// </summary>
        private void OnBandAssignmentReceivedFromHost(BandAssignmentMessage message)
        {
            Debug.Log($"[LobbyRoomMenu] Client: Received band assignments from host - {message.Assignments?.Count ?? 0} players, {message.BandNames?.Count ?? 0} bands");
            
            var bandManager = BandManager.Instance;
            if (bandManager == null)
            {
                Debug.LogWarning("[LobbyRoomMenu] Client: BandManager.Instance is null, cannot apply band assignments");
                return;
            }
            
            // Import the sync data from host
            var syncData = new BandSyncData
            {
                BandSize = message.BandSize,
                LobbySeed = message.LobbySeed,
                PlayerAssignments = message.Assignments ?? new Dictionary<Guid, int>(),
                BandNames = message.BandNames ?? new Dictionary<int, string>(),
                BandNameRegenerations = message.BandNameRegenerations ?? new Dictionary<int, int>(),
                ConnectionGroups = message.ConnectionGroups ?? new Dictionary<int, List<Guid>>()
            };
            
            // Find local connection ID by looking for local players in connection groups
            int localConnectionId = FindLocalConnectionId(syncData.ConnectionGroups);
            
            bandManager.ImportSyncData(syncData, localConnectionId);
            
            Debug.Log($"[LobbyRoomMenu] Client: Applied band assignments from host (localConnectionId={localConnectionId})");
            
            // Refresh the player list to display updated band info
            RefreshPlayerList();
        }
        
        /// <summary>
        /// Finds the local connection ID by looking for local players in the connection groups.
        /// </summary>
        private int FindLocalConnectionId(Dictionary<int, List<Guid>> connectionGroups)
        {
            var networkingService = NetworkingServiceFactory.Instance;
            if (networkingService == null) return -1;
            
            var connectedPlayers = networkingService.GetConnectedPlayers();
            if (connectedPlayers == null) return -1;
            
            // Get local player IDs
            var localPlayerIds = new HashSet<Guid>();
            foreach (var kvp in connectedPlayers)
            {
                if (kvp.Value != null)
                {
                    foreach (var player in kvp.Value)
                    {
                        if (player.IsLocalUser)
                        {
                            localPlayerIds.Add(player.NetworkPlayerId);
                        }
                    }
                }
            }
            
            if (localPlayerIds.Count == 0) return -1;
            
            // Find which connection group contains a local player
            foreach (var kvp in connectionGroups)
            {
                if (kvp.Value != null)
                {
                    foreach (var playerId in kvp.Value)
                    {
                        if (localPlayerIds.Contains(playerId))
                        {
                            return kvp.Key;
                        }
                    }
                }
            }
            
            return -1;
        }
        
        /// <summary>
        /// Called when a band name change is received from host (client-side).
        /// </summary>
        private void OnBandNameChangeReceivedFromHost(int bandId, string newName, int regenerationCount)
        {
            Debug.Log($"[LobbyRoomMenu] Client: Received band name change from host - band {bandId} -> '{newName}'");
            
            var bandManager = BandManager.Instance;
            if (bandManager == null) return;
            
            // Update the band name in BandManager
            bandManager.UpdateBandNameFromHost(bandId, newName, regenerationCount);
            
            // Update the header view if it exists
            if (bandHeaderViews.TryGetValue(bandId, out var headerView))
            {
                if (bandManager.Bands.TryGetValue(bandId, out var bandInfo))
                {
                    headerView.UpdateBandInfo(bandInfo, bandManager.BandSize);
                }
            }
        }
        
        /// <summary>
        /// Reorders existing PlayerViews without destroying/recreating them.
        /// This prevents visual flashing when reordering tracks.
        /// </summary>
        private void ReorderPlayerViews()
        {
            if (playerListContainer == null)
                return;
            
            var networkingService = NetworkingServiceFactory.Instance;
            if (networkingService == null || networkingService.CurrentLobby == null)
                return;
            
            var connectedPlayers = networkingService.GetConnectedPlayers();
            if (connectedPlayers == null || connectedPlayers.Count == 0)
                return;
            
            var allPlayers = new List<NetworkPlayerData>();
            foreach (var kvp in connectedPlayers)
            {
                if (kvp.Value != null)
                    allPlayers.AddRange(kvp.Value);
            }
            
            // Get the new order
            var orderedPlayers = GetOrderedPlayers(allPlayers);
            
            // Check if bands are enabled - if so, always do a full refresh
            // because the hierarchy includes band headers which makes partial reordering unreliable
            var bandManager = BandManager.Instance;
            bool bandsEnabled = bandManager != null && bandManager.AreBandsEnabled;
            
            if (bandsEnabled)
            {
                Debug.Log("[LobbyRoomMenu] Bands enabled - doing full refresh for reorder");
                RefreshPlayerList();
                return;
            }
            
            // Check if we have all the views - if not, do a full refresh
            bool needsFullRefresh = orderedPlayers.Count != playerViews.Count;
            if (!needsFullRefresh)
            {
                foreach (var player in orderedPlayers)
                {
                    if (!playerViews.ContainsKey(player))
                    {
                        needsFullRefresh = true;
                        break;
                    }
                }
            }
            
            if (needsFullRefresh)
            {
                Debug.Log("[LobbyRoomMenu] Player count mismatch - doing full refresh");
                RefreshPlayerList();
                return;
            }
            
            // Reorder existing views using SetSiblingIndex
            // Each player has a view + divider (except the last one)
            // So player 0 is at sibling 0, divider at 1, player 1 at 2, divider at 3, etc.
            int siblingIndex = 0;
            for (int i = 0; i < orderedPlayers.Count; i++)
            {
                var player = orderedPlayers[i];
                if (playerViews.TryGetValue(player, out var playerView) && playerView != null)
                {
                    playerView.transform.SetSiblingIndex(siblingIndex);
                    siblingIndex++;
                    
                    // Find the divider after this view (if not last player)
                    if (i < orderedPlayers.Count - 1)
                    {
                        // Dividers are named "Divider" - find the next one and move it
                        for (int j = siblingIndex; j < playerListContainer.childCount; j++)
                        {
                            var child = playerListContainer.GetChild(j);
                            if (child.name == "Divider")
                            {
                                child.SetSiblingIndex(siblingIndex);
                                siblingIndex++;
                                break;
                            }
                        }
                    }
                    
                    // Update the reorder button state for the new position
                    playerView.UpdateReorderButtonState(i, orderedPlayers.Count);
                }
            }
            
            Debug.Log($"[LobbyRoomMenu] Reordered {orderedPlayers.Count} player views");
        }
        
        /// <summary>
        /// Gets players in the correct display order from TrackOrderManager.
        /// </summary>
        private List<NetworkPlayerData> GetOrderedPlayers(List<NetworkPlayerData> allPlayers)
        {
            var trackOrderManager = TrackOrderManager.Instance;
            if (trackOrderManager == null)
            {
                // No track order manager, return in default order
                return allPlayers.OrderBy(p => p.PlayerIndex).ToList();
            }
            
            // Get base ordering from track order manager (custom order)
            var orderedIds = trackOrderManager.ExportCustomOrder();
            List<NetworkPlayerData> result;
            
            if (orderedIds != null && orderedIds.Count > 0)
            {
                // Apply custom order
                result = new List<NetworkPlayerData>(orderedIds.Count);
                foreach (var playerId in orderedIds)
                {
                    var player = allPlayers.FirstOrDefault(p => p.NetworkPlayerId == playerId);
                    if (player != null)
                    {
                        result.Add(player);
                    }
                }
                
                // Add any players not in the order (shouldn't happen, but be safe)
                foreach (var player in allPlayers)
                {
                    if (!result.Contains(player))
                    {
                        result.Add(player);
                    }
                }
            }
            else
            {
                // No custom order, use default order by player index
                result = allPlayers.OrderBy(p => p.PlayerIndex).ToList();
            }
            
            // Apply LocalPlayersFirst sorting if enabled
            // We check IsLocalUser directly to avoid triggering OnOrderChanged events
            if (trackOrderManager.LocalPlayersFirst)
            {
                var localPlayers = result.Where(p => p.IsLocalUser).ToList();
                var remotePlayers = result.Where(p => !p.IsLocalUser).ToList();
                result = localPlayers.Concat(remotePlayers).ToList();
            }
            
            return result;
        }

        private void CreatePlayerView(NetworkPlayerData playerData, int playerIndex, int totalPlayers)
        {
            if (playerEntryPrefab == null || playerListContainer == null || playerData == null)
                return;

            var entry = Instantiate(playerEntryPrefab, playerListContainer);
            entry.SetActive(true);
            
            var rectTransform = entry.GetComponent<RectTransform>();
            if (rectTransform != null)
            {
                rectTransform.localScale = Vector3.one;
                rectTransform.anchorMin = new Vector2(0, 0.5f);
                rectTransform.anchorMax = new Vector2(1, 0.5f);
                rectTransform.pivot = new Vector2(0.5f, 0.5f);
            }
            
            var playerView = entry.GetComponent<PlayerView>();
            if (playerView == null)
                playerView = entry.AddComponent<PlayerView>();

            var networkingService = NetworkingServiceFactory.Instance;
            bool isLocalPlayer = networkingService != null && networkingService.IsHosting == playerData.IsHost;
            
            var button = entry.GetComponent<Button>();
            if (button == null)
                button = entry.AddComponent<Button>();
            
            var navigation = button.navigation;
            navigation.mode = UnityEngine.UI.Navigation.Mode.Automatic;
            button.navigation = navigation;
            
            if (isHost)
            {
                button.onClick.AddListener(() => OnPlayerSelected(playerData));
                var colorBlock = button.colors;
                colorBlock.highlightedColor = new Color(1f, 1f, 1f, 0.3f);
                colorBlock.selectedColor = new Color(0.2f, 0.9f, 0.2f, 0.3f);
                button.colors = colorBlock;
                
                // Wire up move events for track reordering (host only)
                playerView.OnMoveUpClicked += OnPlayerMoveUp;
                playerView.OnMoveDownClicked += OnPlayerMoveDown;
            }
            else
            {
                button.interactable = true;
            }
            
            playerView.Initialize(playerData, isLocalPlayer, isHost);
            playerView.UpdateReorderButtonState(playerIndex, totalPlayers);
            playerViews[playerData] = playerView;
        }
        
        /// <summary>
        /// Creates a player view with band-relative index for reorder button state.
        /// Used when bands are enabled to ensure reorder buttons respect band boundaries.
        /// </summary>
        /// <param name="playerData">The player data to display.</param>
        /// <param name="bandIndex">The player's index within their band (for reorder buttons).</param>
        /// <param name="bandPlayerCount">Number of players in this band (for reorder buttons).</param>
        /// <param name="currentBandId">The band this player is currently in.</param>
        /// <param name="allBands">All available bands (for move to band dropdown).</param>
        /// <param name="maxBandSize">Maximum players per band.</param>
        private void CreatePlayerViewWithBandIndex(NetworkPlayerData playerData, int bandIndex, int bandPlayerCount, 
            int currentBandId, IReadOnlyList<BandInfo> allBands, int maxBandSize)
        {
            if (playerEntryPrefab == null || playerListContainer == null || playerData == null)
                return;

            var entry = Instantiate(playerEntryPrefab, playerListContainer);
            entry.SetActive(true);
            
            var rectTransform = entry.GetComponent<RectTransform>();
            if (rectTransform != null)
            {
                rectTransform.localScale = Vector3.one;
                rectTransform.anchorMin = new Vector2(0, 0.5f);
                rectTransform.anchorMax = new Vector2(1, 0.5f);
                rectTransform.pivot = new Vector2(0.5f, 0.5f);
            }
            
            var playerView = entry.GetComponent<PlayerView>();
            if (playerView == null)
                playerView = entry.AddComponent<PlayerView>();

            var networkingService = NetworkingServiceFactory.Instance;
            bool isLocalPlayer = networkingService != null && networkingService.IsHosting == playerData.IsHost;
            
            var button = entry.GetComponent<Button>();
            if (button == null)
                button = entry.AddComponent<Button>();
            
            var navigation = button.navigation;
            navigation.mode = UnityEngine.UI.Navigation.Mode.Automatic;
            button.navigation = navigation;
            
            if (isHost)
            {
                button.onClick.AddListener(() => OnPlayerSelected(playerData));
                var colorBlock = button.colors;
                colorBlock.highlightedColor = new Color(1f, 1f, 1f, 0.3f);
                colorBlock.selectedColor = new Color(0.2f, 0.9f, 0.2f, 0.3f);
                button.colors = colorBlock;
                
                // Wire up move events for track reordering (host only)
                playerView.OnMoveUpClicked += OnPlayerMoveUp;
                playerView.OnMoveDownClicked += OnPlayerMoveDown;
                
                // Wire up move to band events (host only)
                playerView.OnMoveToBandSelected += OnPlayerMoveToBand;
                playerView.OnCreateNewBandSelected += OnPlayerCreateNewBand;
            }
            else
            {
                button.interactable = true;
            }
            
            playerView.Initialize(playerData, isLocalPlayer, isHost);
            // Use band-relative index and band size so buttons respect band boundaries
            playerView.UpdateReorderButtonState(bandIndex, bandPlayerCount);
            playerViews[playerData] = playerView;
            
            // Configure move to band dropdown (host only)
            // Show when: 2+ bands exist, OR 1 band exists but is over capacity
            if (isHost && allBands != null)
            {
                bool anyBandOverCapacity = maxBandSize > 0 && allBands.Any(b => b.PlayerCount > maxBandSize);
                bool shouldShowDropdown = allBands.Count >= 2 || (allBands.Count == 1 && anyBandOverCapacity);
                
                if (shouldShowDropdown)
                {
                    // Get the group size for this player (how many players will move together)
                    var bandManager = BandManager.Instance;
                    int playerGroupSize = 1;
                    if (bandManager != null)
                    {
                        var group = bandManager.GetMovablePlayerGroup(playerData.NetworkPlayerId);
                        playerGroupSize = group?.Count ?? 1;
                    }
                    
                    playerView.ConfigureMoveToBandDropdown(allBands, currentBandId, playerGroupSize, maxBandSize);
                }
            }
        }
        
        /// <summary>
        /// Handler for when host clicks move up on a player.
        /// When bands are enabled, only allows moving within the same band.
        /// </summary>
        private void OnPlayerMoveUp(NetworkPlayerData player)
        {
            if (!isHost || player == null)
                return;
            
            var trackOrderManager = TrackOrderManager.Instance;
            if (trackOrderManager == null)
            {
                Debug.LogWarning("[LobbyRoomMenu] TrackOrderManager not available for reordering");
                return;
            }
            
            // Ensure the custom order is initialized with all current players
            EnsureTrackOrderInitialized(trackOrderManager);
            
            // Check if bands are enabled - if so, only allow moving within band
            var bandManager = BandManager.Instance;
            if (bandManager != null && bandManager.AreBandsEnabled)
            {
                if (!CanMovePlayerUp(player, trackOrderManager, bandManager))
                {
                    Debug.Log($"[LobbyRoomMenu] Cannot move player {player.PlayerName} up - at top of band");
                    return;
                }
            }
            
            Debug.Log($"[LobbyRoomMenu] Moving player {player.PlayerName} up");
            trackOrderManager.MovePlayerUp(player.NetworkPlayerId);
            
            // Broadcast the new order to clients
            BroadcastTrackOrder();
        }
        
        /// <summary>
        /// Handler for when host selects a different band for a player via dropdown.
        /// Moves the player (and their connection group) to the selected band.
        /// </summary>
        private void OnPlayerMoveToBand(NetworkPlayerData player, int targetBandId)
        {
            if (!isHost || player == null)
                return;
            
            // In dedicated server mode, host cannot move players - only admin via web UI
            var dedicatedManager = YARG.Networking.DedicatedServer.DedicatedServerManager.Instance;
            if (dedicatedManager != null && !dedicatedManager.CanHostMovePlayers())
            {
                Debug.LogWarning("[LobbyRoomMenu] In dedicated server mode, only admin can move players via web UI!");
                YARG.Menu.Persistent.ToastManager.ToastWarning("Only the server admin can move players between bands.");
                return;
            }
            
            var bandManager = BandManager.Instance;
            if (bandManager == null)
            {
                Debug.LogWarning("[LobbyRoomMenu] BandManager not available for band move");
                return;
            }
            
            int currentBandId = bandManager.GetPlayerBandId(player.NetworkPlayerId);
            if (currentBandId == targetBandId)
            {
                Debug.Log($"[LobbyRoomMenu] Player {player.PlayerName} already in band {targetBandId}");
                return;
            }
            
            Debug.Log($"[LobbyRoomMenu] Moving player {player.PlayerName} from band {currentBandId} to band {targetBandId}");
            
            bool success = bandManager.MovePlayerToBand(player.NetworkPlayerId, targetBandId);
            if (success)
            {
                Debug.Log($"[LobbyRoomMenu] Successfully moved player {player.PlayerName} to band {targetBandId}");
                
                // Broadcast updated band assignments to all clients
                BroadcastBandAssignments();
                
                // Refresh UI to show new arrangement
                RefreshPlayerList();
            }
            else
            {
                Debug.LogWarning($"[LobbyRoomMenu] Failed to move player {player.PlayerName} to band {targetBandId}");
                
                // Refresh UI to reset dropdown to current band
                RefreshPlayerList();
            }
        }
        
        /// <summary>
        /// Handler for when host selects "Create New Band" from the dropdown.
        /// Creates a new band and moves the player (and their connection group) to it.
        /// </summary>
        private void OnPlayerCreateNewBand(NetworkPlayerData player)
        {
            if (!isHost || player == null)
                return;
            
            // In dedicated server mode, host cannot move players - only admin via web UI
            var dedicatedManager = YARG.Networking.DedicatedServer.DedicatedServerManager.Instance;
            if (dedicatedManager != null && !dedicatedManager.CanHostMovePlayers())
            {
                Debug.LogWarning("[LobbyRoomMenu] In dedicated server mode, only admin can move players via web UI!");
                YARG.Menu.Persistent.ToastManager.ToastWarning("Only the server admin can move players between bands.");
                return;
            }
            
            var bandManager = BandManager.Instance;
            if (bandManager == null)
            {
                Debug.LogWarning("[LobbyRoomMenu] BandManager not available for creating new band");
                return;
            }
            
            Debug.Log($"[LobbyRoomMenu] Creating new band for player {player.PlayerName}");
            
            int newBandId = bandManager.CreateNewBandAndMovePlayer(player.NetworkPlayerId);
            if (newBandId >= 0)
            {
                var bandInfo = bandManager.Bands.TryGetValue(newBandId, out var band) ? band : null;
                string bandName = bandInfo?.DisplayName ?? $"Band {newBandId}";
                Debug.Log($"[LobbyRoomMenu] Successfully created band '{bandName}' (ID: {newBandId}) for player {player.PlayerName}");
                
                // Broadcast updated band assignments to all clients
                BroadcastBandAssignments();
                
                // Refresh UI to show new arrangement
                RefreshPlayerList();
            }
            else
            {
                Debug.LogWarning($"[LobbyRoomMenu] Failed to create new band for player {player.PlayerName}");
                
                // Refresh UI to reset dropdown
                RefreshPlayerList();
            }
        }
        
        /// <summary>
        /// Handler for when host clicks move down on a player.
        /// When bands are enabled, only allows moving within the same band.
        /// </summary>
        private void OnPlayerMoveDown(NetworkPlayerData player)
        {
            if (!isHost || player == null)
                return;
            
            var trackOrderManager = TrackOrderManager.Instance;
            if (trackOrderManager == null)
            {
                Debug.LogWarning("[LobbyRoomMenu] TrackOrderManager not available for reordering");
                return;
            }
            
            // Ensure the custom order is initialized with all current players
            EnsureTrackOrderInitialized(trackOrderManager);
            
            // Check if bands are enabled - if so, only allow moving within band
            var bandManager = BandManager.Instance;
            if (bandManager != null && bandManager.AreBandsEnabled)
            {
                if (!CanMovePlayerDown(player, trackOrderManager, bandManager))
                {
                    Debug.Log($"[LobbyRoomMenu] Cannot move player {player.PlayerName} down - at bottom of band");
                    return;
                }
            }
            
            Debug.Log($"[LobbyRoomMenu] Moving player {player.PlayerName} down");
            trackOrderManager.MovePlayerDown(player.NetworkPlayerId);
            
            // Broadcast the new order to clients
            BroadcastTrackOrder();
        }
        
        /// <summary>
        /// Checks if a player can move up within their band.
        /// Returns true if the player above them is in the same band.
        /// </summary>
        private bool CanMovePlayerUp(NetworkPlayerData player, TrackOrderManager trackOrderManager, BandManager bandManager)
        {
            var customOrder = trackOrderManager.ExportCustomOrder();
            int currentIndex = customOrder.IndexOf(player.NetworkPlayerId);
            
            // Can't move up if at top
            if (currentIndex <= 0)
                return false;
            
            // Check if the player above is in the same band
            Guid playerAboveId = customOrder[currentIndex - 1];
            int playerBandId = bandManager.GetPlayerBandId(player.NetworkPlayerId);
            int aboveBandId = bandManager.GetPlayerBandId(playerAboveId);
            
            return playerBandId == aboveBandId;
        }
        
        /// <summary>
        /// Checks if a player can move down within their band.
        /// Returns true if the player below them is in the same band.
        /// </summary>
        private bool CanMovePlayerDown(NetworkPlayerData player, TrackOrderManager trackOrderManager, BandManager bandManager)
        {
            var customOrder = trackOrderManager.ExportCustomOrder();
            int currentIndex = customOrder.IndexOf(player.NetworkPlayerId);
            
            // Can't move down if at bottom
            if (currentIndex < 0 || currentIndex >= customOrder.Count - 1)
                return false;
            
            // Check if the player below is in the same band
            Guid playerBelowId = customOrder[currentIndex + 1];
            int playerBandId = bandManager.GetPlayerBandId(player.NetworkPlayerId);
            int belowBandId = bandManager.GetPlayerBandId(playerBelowId);
            
            return playerBandId == belowBandId;
        }
        
        /// <summary>
        /// Initializes the track order for the host when the lobby is first created.
        /// Ensures the host is first in the order, followed by any other players.
        /// </summary>
        private void InitializeHostTrackOrder(INetworkingService networkingService)
        {
            var trackOrderManager = TrackOrderManager.Instance;
            if (trackOrderManager == null)
                return;
            
            // Check if we already have a track order initialized
            var existingOrder = trackOrderManager.ExportCustomOrder();
            if (existingOrder != null && existingOrder.Count > 0)
                return; // Already initialized
            
            var connectedPlayers = networkingService.GetConnectedPlayers();
            if (connectedPlayers == null || connectedPlayers.Count == 0)
                return;
            
            var allPlayers = new List<NetworkPlayerData>();
            foreach (var kvp in connectedPlayers)
            {
                if (kvp.Value != null)
                    allPlayers.AddRange(kvp.Value);
            }
            
            if (allPlayers.Count == 0)
                return;
            
            // Add all host players first (host may have multiple profiles)
            var hostPlayers = allPlayers.Where(p => p.IsHost).OrderBy(p => p.PlayerIndex).ToList();
            foreach (var hostPlayer in hostPlayers)
            {
                trackOrderManager.AddPlayerToOrder(hostPlayer.NetworkPlayerId);
            }
            
            // Add remaining players (non-host) in join order (by player index as a proxy)
            foreach (var player in allPlayers.OrderBy(p => p.PlayerIndex))
            {
                if (!player.IsHost)
                {
                    trackOrderManager.AddPlayerToOrder(player.NetworkPlayerId);
                }
            }
            
            Debug.Log($"[LobbyRoomMenu] Initialized track order for host with {allPlayers.Count} players ({hostPlayers.Count} host profiles)");
        }
        
        /// <summary>
        /// Ensures the TrackOrderManager has been initialized with all current players.
        /// </summary>
        private void EnsureTrackOrderInitialized(TrackOrderManager trackOrderManager)
        {
            var networkingService = NetworkingServiceFactory.Instance;
            if (networkingService == null)
                return;
            
            var connectedPlayers = networkingService.GetConnectedPlayers();
            if (connectedPlayers == null)
                return;
            
            var allPlayers = new List<NetworkPlayerData>();
            foreach (var kvp in connectedPlayers)
            {
                if (kvp.Value != null)
                    allPlayers.AddRange(kvp.Value);
            }
            
            trackOrderManager.InitializeOrderFromPlayers(allPlayers);
        }
        
        /// <summary>
        /// Broadcasts the current track order to all clients.
        /// </summary>
        private void BroadcastTrackOrder()
        {
            var networkingService = NetworkingServiceFactory.Instance;
            if (networkingService == null || !networkingService.IsHosting)
            {
                Debug.LogWarning($"[LobbyRoomMenu] BroadcastTrackOrder: early return (service null={networkingService == null}, IsHosting={networkingService?.IsHosting})");
                return;
            }
            
            var trackOrderManager = TrackOrderManager.Instance;
            if (trackOrderManager == null)
            {
                Debug.LogWarning("[LobbyRoomMenu] BroadcastTrackOrder: TrackOrderManager is null");
                return;
            }
            
            var customOrder = trackOrderManager.ExportCustomOrder();
            Debug.Log($"[LobbyRoomMenu] BroadcastTrackOrder: Sending {customOrder?.Count ?? 0} players");
            
            // Send via networking abstraction
            if (networkingService is LiteNetNetworkingAdapter liteNetAdapter)
            {
                liteNetAdapter.BroadcastTrackOrder(customOrder);
            }
            
            // NOTE: Don't call RefreshPlayerList here - the OnOrderChanged event already triggers
            // ReorderPlayerViews() which handles reordering without destroying/recreating views
        }

        private void CreateDivider()
        {
            GameObject divider = new GameObject("Divider");
            divider.transform.SetParent(playerListContainer, false);
            
            var image = divider.AddComponent<Image>();
            image.color = new Color(1, 1, 1, 0.2f);
            
            var rectTransform = divider.GetComponent<RectTransform>();
            rectTransform.anchorMin = new Vector2(0, 0.5f);
            rectTransform.anchorMax = new Vector2(1, 0.5f);
            rectTransform.pivot = new Vector2(0.5f, 0.5f);
            rectTransform.sizeDelta = new Vector2(0, 1);
        }

        public void OnBrowseSongsClicked()
        {
            if (!isHost)
            {
                Debug.LogWarning("Only host can start song selection!");
                return;
            }
            
            // Check band validation before allowing browse
            var bandManager = BandManager.Instance;
            if (bandManager != null && bandManager.AreBandsEnabled)
            {
                var validation = bandManager.ValidateForGameStart();
                if (!validation.IsValid)
                {
                    Debug.LogWarning($"[LobbyRoomMenu] Cannot start song selection - {validation.ErrorMessage}");
                    // Show toast to remind user of the issue
                    ToastManager.ToastWarning(validation.ErrorMessage);
                    return;
                }
            }
            
            // Check if any connected players have game modes that are blocked by current settings
            var networkingService = NetworkingServiceFactory.Instance;
            if (networkingService != null)
            {
                var currentLobby = networkingService.CurrentLobby;
                if (currentLobby?.AllowedGameModes != null && currentLobby.AllowedGameModes.Count > 0)
                {
                    // AllowedGameModes is a blacklist - check if any player uses a blocked game mode
                    var blockedPlayers = new List<string>();
                    var allPlayers = networkingService.GetAllPlayers();
                    
                    foreach (var player in allPlayers)
                    {
                        if (player == null) continue;
                        
                        // Get game mode from the player's instrument
                        var gameMode = ((YARG.Core.Instrument)player.Instrument).ToNativeGameMode();
                        if (currentLobby.AllowedGameModes.Contains(gameMode))
                        {
                            blockedPlayers.Add($"{player.PlayerName} ({gameMode})");
                        }
                    }
                    
                    if (blockedPlayers.Count > 0)
                    {
                        string blockedList = string.Join(", ", blockedPlayers);
                        Debug.LogWarning($"[LobbyRoomMenu] Cannot start song selection - players with blocked game modes: {blockedList}");
                        ToastManager.ToastWarning($"Cannot browse: {blockedList} uses a disabled game mode. Remove restriction or kick player.");
                        return;
                    }
                }
            }

            Debug.Log("Host starting song selection...");

            if (networkingService == null || networkingService.CurrentLobby == null)
            {
                Debug.LogWarning("[LobbyRoomMenu] Cannot start song selection - no active lobby");
                return;
            }
            
            if (networkingService is LiteNetNetworkingAdapter liteNetAdapter)
            {
                // Use the unified NavigateToMusicLibrary method - works the same for
                // in-game hosting and dedicated server designated hosts
                liteNetAdapter.NavigateToMusicLibrary();
            }
            
            MenuManager.Instance.PushMenu(MenuManager.Menu.MusicLibrary);
        }
        
        public void OnJoinHostClicked()
        {
            if (isHost)
            {
                Debug.LogWarning("[LobbyRoomMenu] OnJoinHostClicked called but we are the host!");
                return;
            }
            
            if (!_hostIsBrowsingSongs)
            {
                Debug.LogWarning("[LobbyRoomMenu] OnJoinHostClicked called but host is not browsing songs!");
                return;
            }
            
            Debug.Log("[LobbyRoomMenu] Client: Joining host in music library");
            MenuManager.Instance.PushMenu(MenuManager.Menu.MusicLibrary);
        }

        public void OnLeaveLobbyClicked()
        {
            var networkService = NetworkingServiceFactory.Instance;
            bool isInLobby = networkService != null && networkService.CurrentLobby != null;
            
            if (isInLobby)
                ShowLeaveLobbyDialog();
            else
                LeaveLobby();
        }
        
        public void OnKickPlayerClicked()
        {
            if (!isHost)
            {
                Debug.LogWarning("[LobbyRoomMenu] Only host can kick players!");
                return;
            }
            
            // In dedicated server mode, host cannot kick players - only admin via web UI
            var dedicatedManager = YARG.Networking.DedicatedServer.DedicatedServerManager.Instance;
            if (dedicatedManager != null && !dedicatedManager.CanHostKickPlayers())
            {
                Debug.LogWarning("[LobbyRoomMenu] In dedicated server mode, only admin can kick players via web UI!");
                YARG.Menu.Persistent.ToastManager.ToastWarning("Only the server admin can kick players.");
                return;
            }
            
            if (selectedPlayer == null)
            {
                Debug.LogWarning("[LobbyRoomMenu] No player selected to kick!");
                return;
            }
            
            if (selectedPlayer.IsLocalUser)
            {
                Debug.LogWarning("[LobbyRoomMenu] Cannot kick yourself!");
                return;
            }
            
            ShowKickPlayerDialog();
        }
        
        private void ShowKickPlayerDialog()
        {
            if (DialogManager.Instance == null || selectedPlayer == null) return;
            
            string playerName = selectedPlayer.PlayerName;
            
            var dialog = DialogManager.Instance.ShowMessage(
                "Kick Player?",
                $"Are you sure you want to kick {playerName} from the lobby?");
            
            dialog.ClearButtons();
            dialog.AddDialogButton("Cancel", MenuData.Colors.BrightButton, () => DialogManager.Instance.ClearDialog());
            dialog.AddDialogButton("Kick Player", MenuData.Colors.CancelButton, () =>
            {
                DialogManager.Instance.ClearDialog();
                KickPlayer(selectedPlayer);
            });
        }
        
        private void KickPlayer(NetworkPlayerData player)
        {
            if (!isHost || player == null)
            {
                Debug.LogWarning("[LobbyRoomMenu] Cannot kick player - not host or player is null");
                return;
            }
            
            Debug.Log($"[LobbyRoomMenu] Kicking player: {player.PlayerName}");
            
            var networkService = NetworkingServiceFactory.Instance;
            if (networkService is LiteNetNetworkingAdapter liteNetAdapter)
            {
                liteNetAdapter.KickPlayer(player);
            }
            
            selectedPlayer = null;
            UpdateNavigationScheme();
        }
        
        private void ShowLeaveLobbyDialog()
        {
            if (DialogManager.Instance == null) return;
            
            // Don't show another dialog if one is already showing
            if (DialogManager.Instance.IsDialogShowing)
            {
                Debug.Log("[LobbyRoomMenu] Dialog already showing, skipping leave lobby dialog");
                return;
            }
            
            var networkService = NetworkingServiceFactory.Instance;
            bool isHostUser = networkService != null && networkService.IsHosting;
            
            string title = isHostUser ? "Close Lobby?" : "Leave Lobby?";
            string message = isHostUser
                ? "Are you sure you want to close the lobby? All connected players will be disconnected."
                : "Are you sure you want to leave the lobby?";
            
            var dialog = DialogManager.Instance.ShowMessage(title, message);
            
            dialog.ClearButtons();
            dialog.AddDialogButton("Cancel", MenuData.Colors.BrightButton, () => DialogManager.Instance.ClearDialog());
            dialog.AddDialogButton(isHostUser ? "Close Lobby" : "Leave Lobby", MenuData.Colors.CancelButton, () =>
            {
                DialogManager.Instance.ClearDialog();
                LeaveLobby();
            });
        }

        private void LeaveLobby()
        {
            Debug.Log("[LobbyRoomMenu] LeaveLobby called");
            CleanupMultiplayerState();
            
            var factory = NetworkingServiceFactory.Instance;
            if (factory != null && factory.CurrentLobby != null)
            {
                // If we're the host, stop the session lifecycle (cleans up UPnP, releases lobby code)
                if (isHost && SessionLifecycleManager.Instance != null)
                {
                    SessionLifecycleManager.Instance.StopAsync().Forget();
                }
                
                factory.LeaveLobby();
            }
            
            MenuManager.Instance?.PopMenu();
        }
        
        private void CleanupMultiplayerState()
        {
            Debug.Log("[LobbyRoomMenu] Cleaning up multiplayer state");
            GlobalVariables.State.IsPractice = false;
            GlobalVariables.State.PlayingAShow = false;
            GlobalVariables.State.ShowSongs?.Clear();
            GlobalVariables.State.ShowIndex = 0;
            MusicLibraryMenu.LibraryMode = MusicLibraryMode.QuickPlay;
        }

        private void OnLobbyLeft()
        {
            Debug.Log("Lobby left, returning to lobby browser");
            
            // Clear band manager data when leaving lobby
            var bandManager = BandManager.Instance;
            bandManager?.Clear();
            
            // Clear multiplayer player manager mappings
            MultiplayerPlayerManager.Clear();
            
            // Clear track order manager
            var trackOrderManager = TrackOrderManager.Instance;
            trackOrderManager?.Clear();
            
            if (_isQuitting)
                return;
            
            if (MenuManager.Instance == null)
                return;
            
            if (MenuManager.Instance.CurrentMenu == MenuManager.Menu.OnlineMultiplayer ||
                MenuManager.Instance.CurrentMenu == MenuManager.Menu.MainMenu)
                return;
            
            while (MenuManager.Instance != null &&
                   MenuManager.Instance.CurrentMenu != MenuManager.Menu.OnlineMultiplayer && 
                   MenuManager.Instance.MenuStackCount > 1)
            {
                MenuManager.Instance.PopMenu();
            }
        }

        private void OnNetworkError(string error)
        {
            Debug.LogError($"[LobbyRoomMenu] Network error in lobby: {error}");
            
            if (DialogManager.Instance != null && !DialogManager.Instance.IsDialogShowing)
            {
                DialogManager.Instance.ShowMessage("Connection Error", error);
            }
            
            LeaveLobby();
        }
        
        #region Late Join Handling
        
        private MessageDialog _lateJoinDialog;
        
        /// <summary>
        /// Checks if there's a pending late join state from before this menu was active.
        /// This handles the race condition where the late join action arrives before we subscribe.
        /// </summary>
        private void CheckPendingLateJoinState(LiteNetNetworkingAdapter adapter)
        {
            if (!adapter.IsLateJoiner)
                return;
            
            var action = adapter.MyLateJoinAction;
            var pendingMessage = adapter.PendingLateJoinMessage;
            
            Debug.Log($"[LobbyRoomMenu] Checking pending late join state: IsLateJoiner={adapter.IsLateJoiner}, Action={action}, PendingMessage={pendingMessage}");
            
            // If the action is SpectateCurrentSong, the adapter has already navigated to gameplay
            // We only need to handle WaitForSongEnd here (showing dialog)
            if (action == LateJoinAction.WaitForSongEnd && !string.IsNullOrEmpty(pendingMessage))
            {
                OnLateJoinWaiting(pendingMessage);
                adapter.ClearPendingLateJoinMessage();
            }
        }
        
        /// <summary>
        /// Called when this client joined during an active song but DOESN'T have the song.
        /// Shows a dialog explaining they need to wait.
        /// </summary>
        private void OnLateJoinWaiting(string message)
        {
            Debug.Log($"[LobbyRoomMenu] Late join waiting (missing song): {message}");
            
            if (DialogManager.Instance == null)
            {
                Debug.LogWarning("[LobbyRoomMenu] DialogManager.Instance is null in OnLateJoinWaiting");
                ToastManager.ToastWarning("Song is in progress. You'll join after it ends.");
                return;
            }
            
            // Clear any existing dialog first
            if (_lateJoinDialog != null)
            {
                DialogManager.Instance.ClearDialog();
            }
            
            _lateJoinDialog = DialogManager.Instance.ShowMessage(
                "Song In Progress",
                "The lobby is currently playing a song you don't have.\n\n" +
                "You can wait here until the song ends, or leave the lobby.");
            
            _lateJoinDialog.AddDialogButton("Wait", MenuData.Colors.BrightButton, () =>
            {
                DialogManager.Instance.ClearDialog();
                _lateJoinDialog = null;
                ToastManager.ToastInformation("Waiting for current song to finish...");
            });
            
            _lateJoinDialog.AddDialogButton("Leave Lobby", MenuData.Colors.CancelButton, () =>
            {
                DialogManager.Instance.ClearDialog();
                _lateJoinDialog = null;
                LeaveLobby();
            });
        }
        
        /// <summary>
        /// Called when this client joined during an active song and CAN spectate.
        /// Note: The adapter now auto-navigates to gameplay, so this is just for notifications.
        /// </summary>
        private void OnLateJoinSpectating(string message)
        {
            // This is now handled by the adapter's NavigateToSpectateMode()
            // Keep for backward compatibility / edge cases
            Debug.Log($"[LobbyRoomMenu] Late join spectating: {message}");
            ToastManager.ToastInformation($"Joining song as spectator...");
        }
        
        /// <summary>
        /// Called when the setlist was aborted (e.g., due to a late joiner missing songs).
        /// </summary>
        private void OnSetlistAborted(string reason)
        {
            Debug.Log($"[LobbyRoomMenu] Setlist aborted: {reason}");
            
            // Clear any late join dialog
            if (_lateJoinDialog != null && DialogManager.Instance != null)
            {
                DialogManager.Instance.ClearDialog();
                _lateJoinDialog = null;
            }
            
            // Toast is already shown by the adapter, but we can update UI here if needed
        }
        
        /// <summary>
        /// Called when the late joiner is ready to join (song ended, or can join immediately).
        /// </summary>
        private void OnLateJoinReady(LateJoinAction action, string message)
        {
            Debug.Log($"[LobbyRoomMenu] Late join ready: action={action}, message={message}");
            ToastManager.ToastInformation($"You can now join the game!");
            
            // Clear any late join dialog
            if (_lateJoinDialog != null && DialogManager.Instance != null)
            {
                DialogManager.Instance.ClearDialog();
                _lateJoinDialog = null;
            }
            
            // Clear the pending late join state
            var networkingService = NetworkingServiceFactory.Instance;
            if (networkingService is LiteNetNetworkingAdapter liteNetAdapter)
            {
                liteNetAdapter.ClearPendingLateJoinMessage();
            }
        }
        
        #endregion
    }
}
