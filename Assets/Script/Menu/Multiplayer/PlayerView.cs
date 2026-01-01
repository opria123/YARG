using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using TMPro;
using YARG.Core;
using YARG.Helpers.Extensions;
using YARG.Menu;
using YARG.Menu.Data;
using YARG.Networking.Abstraction;
using YARG.Networking.Bands;
using YARG.Networking.Tracks;

namespace YARG.Menu.Multiplayer
{
    /// <summary>
    /// UI representation of a player in the lobby, or a band header.
    /// Can operate in two modes:
    /// - Player mode: Shows player name, ping, instrument, and kick button
    /// - Band header mode: Shows band name and regenerate button
    /// </summary>
    public class PlayerView : MonoBehaviour
    {
        [Header("Player Mode - UI Elements")]
        [SerializeField] private GameObject playerContainer; // Container for player mode elements
        [SerializeField] private TextMeshProUGUI playerNameText;
        [SerializeField] private TextMeshProUGUI pingText;
        [SerializeField] private ColoredButton kickButton;
        [SerializeField] private GameObject hostBadge; // Optional: visual indicator for host
        [SerializeField] private Image hostBadgeImage;
        [SerializeField] private GameObject playerIcon; // Optional: visual indicator for regular players (inverse of hostBadge)
        [SerializeField] private Image playerIconImage;
        [SerializeField] private Image pingIcon;
        
        [Header("Band Header Mode - UI Elements")]
        [SerializeField] private GameObject bandHeaderContainer; // Container for band header mode elements
        [SerializeField] private TextMeshProUGUI bandNameText;
        [SerializeField] private TextMeshProUGUI bandPlayerCountText;
        [SerializeField] private Button regenerateNameButton;
        
        [Header("Selection Visuals")]
        [SerializeField] private GameObject normalBackground;
        [SerializeField] private GameObject selectedBackground;
        [SerializeField] private GameObject categoryBackground; // Background used for band headers (category style)
        [SerializeField] private Image highlightImage; // Optional: for highlighting on hover/selection
        
        [Header("Track Reorder Controls")]
        [SerializeField] private Button moveUpButton;
        [SerializeField] private Button moveDownButton;
        [SerializeField] private GameObject reorderContainer; // Optional container for up/down buttons
        
        [Header("Move to Band Controls")]
        [SerializeField] private TMP_Dropdown moveToBandDropdown;

        // Mode tracking
        private bool _isBandHeaderMode;
        private BandInfo _bandInfo;
        private int _bandId = -1;
        
        // Player mode state
        private NetworkPlayerData _playerData;
        private bool _isLocalPlayer;
        private bool _isHost;
        private bool _isSelected = false;
        private bool _viewerIsHost = false;
        private int _playerIndex = 0;
        private int _totalPlayers = 0;

        private Color _defaultPingTextColor;
        private bool _hasCachedPingTextColor;
        private Color _hostBadgeDefaultColor;
        private bool _hasCachedHostBadgeColor;
        private string _currentInstrumentIconKey;

        private static readonly Color PING_GOOD_COLOR = new(0.3f, 1f, 0.3f);
        private static readonly Color PING_AVERAGE_COLOR = new(1f, 1f, 0.3f);
        private static readonly Color PING_POOR_COLOR = new(1f, 0.3f, 0.3f);
        private static readonly Color PING_ZERO_COLOR = Color.white;
        private const float PING_GOOD_THRESHOLD = 50f;
        private const float PING_AVERAGE_THRESHOLD = 100f;
        
        /// <summary>
        /// Event fired when move up button is clicked.
        /// </summary>
        public event Action<NetworkPlayerData> OnMoveUpClicked;
        
        /// <summary>
        /// Event fired when move down button is clicked.
        /// </summary>
        public event Action<NetworkPlayerData> OnMoveDownClicked;
        
        /// <summary>
        /// Event fired when regenerate name button is clicked.
        /// Parameter: bandId
        /// </summary>
        public event Action<int> OnRegenerateNameClicked;
        
        /// <summary>
        /// Event fired when a player is moved to a different band via dropdown.
        /// Parameters: (player, targetBandId)
        /// </summary>
        public event Action<NetworkPlayerData, int> OnMoveToBandSelected;
        
        /// <summary>
        /// Event fired when "Create New Band" is selected from dropdown.
        /// Parameter: player to move to the new band
        /// </summary>
        public event Action<NetworkPlayerData> OnCreateNewBandSelected;
        
        // Band dropdown state
        private List<BandInfo> _availableBands;
        private int _currentBandId = -1;
        private bool _isUpdatingDropdown = false;
        private int _bandSize = 0;
        private const int CREATE_NEW_BAND_INDEX = -999; // Special index for "Create New Band" option
        
        /// <summary>
        /// Gets the player data this view represents (null if in band header mode).
        /// </summary>
        public NetworkPlayerData PlayerData => _playerData;
        
        /// <summary>
        /// Gets whether this view is in band header mode.
        /// </summary>
        public bool IsBandHeaderMode => _isBandHeaderMode;
        
        /// <summary>
        /// Gets the band ID if in band header mode (-1 otherwise).
        /// </summary>
        public int BandId => _bandId;

        #region Band Header Mode
        
        /// <summary>
        /// Initializes this view as a band header.
        /// </summary>
        /// <param name="bandInfo">The band information to display.</param>
        /// <param name="bandSize">Maximum players per band (for count display).</param>
        /// <param name="canRegenerate">Whether the regenerate button should be enabled.</param>
        public void InitializeAsBandHeader(BandInfo bandInfo, int bandSize, bool canRegenerate)
        {
            _isBandHeaderMode = true;
            _bandInfo = bandInfo;
            _bandId = bandInfo.BandId;
            _playerData = null;
            
            // Switch to band header mode visuals
            // Hide player container if assigned
            if (playerContainer != null) playerContainer.SetActive(false);
            
            // Also hide individual player elements in case container isn't assigned
            // This ensures player info doesn't show over band header
            if (playerNameText != null) playerNameText.gameObject.SetActive(false);
            if (pingText != null) pingText.gameObject.SetActive(false);
            if (pingIcon != null) pingIcon.gameObject.SetActive(false);
            if (hostBadge != null) hostBadge.SetActive(false);
            if (playerIcon != null) playerIcon.SetActive(false);
            
            // Show band header container
            if (bandHeaderContainer != null) bandHeaderContainer.SetActive(true);
            
            // Hide player-specific controls
            if (reorderContainer != null) reorderContainer.SetActive(false);
            if (kickButton != null) kickButton.gameObject.SetActive(false);
            
            // Switch to category background style
            // Try to auto-find category background if not assigned
            if (categoryBackground == null)
            {
                // Look for a child named "Category Background" or similar
                var categoryBg = transform.Find("Category Background");
                if (categoryBg == null) categoryBg = transform.Find("CategoryBackground");
                if (categoryBg == null) categoryBg = transform.Find("Band Header Background");
                if (categoryBg == null) categoryBg = transform.Find("BandHeaderBackground");
                if (categoryBg != null)
                {
                    categoryBackground = categoryBg.gameObject;
                    Debug.Log($"[PlayerView] Auto-found category background: {categoryBg.name}");
                }
            }
            
            Debug.Log($"[PlayerView] Band header backgrounds - normal={normalBackground != null}, category={categoryBackground != null}, selected={selectedBackground != null}");
            if (categoryBackground != null)
            {
                if (normalBackground != null) normalBackground.SetActive(false);
                if (selectedBackground != null) selectedBackground.SetActive(false);
                categoryBackground.SetActive(true);
                Debug.Log($"[PlayerView] Using category background for band header");
            }
            else
            {
                // Fallback: keep normal background visible if no category background assigned
                if (normalBackground != null) normalBackground.SetActive(true);
                if (selectedBackground != null) selectedBackground.SetActive(false);
                Debug.Log($"[PlayerView] Fallback: Using normal background for band header (no category background assigned)");
            }
            
            // Update band header display
            UpdateBandHeaderDisplay(bandSize);
            
            // Setup regenerate button
            if (regenerateNameButton != null)
            {
                regenerateNameButton.onClick.RemoveListener(HandleRegenerateName);
                regenerateNameButton.gameObject.SetActive(canRegenerate);
                
                if (canRegenerate)
                {
                    regenerateNameButton.onClick.AddListener(HandleRegenerateName);
                }
            }
            
            Debug.Log($"[PlayerView] Initialized as band header: {bandInfo.DisplayName} ({bandInfo.PlayerCount}/{bandSize})");
        }
        
        /// <summary>
        /// Updates the band header display with current band info.
        /// </summary>
        public void UpdateBandHeaderDisplay(int bandSize)
        {
            if (!_isBandHeaderMode || _bandInfo == null) return;
            
            if (bandNameText != null)
            {
                bandNameText.text = _bandInfo.DisplayName;
            }
            
            if (bandPlayerCountText != null)
            {
                string countText = bandSize > 0 
                    ? $"({_bandInfo.PlayerCount}/{bandSize})"
                    : $"({_bandInfo.PlayerCount})";
                bandPlayerCountText.text = countText;
            }
        }
        
        /// <summary>
        /// Updates the band info reference and refreshes the display.
        /// </summary>
        public void UpdateBandInfo(BandInfo bandInfo, int bandSize)
        {
            if (!_isBandHeaderMode) return;
            
            _bandInfo = bandInfo;
            UpdateBandHeaderDisplay(bandSize);
        }
        
        private void HandleRegenerateName()
        {
            if (_isBandHeaderMode && _bandId >= 0)
            {
                OnRegenerateNameClicked?.Invoke(_bandId);
            }
        }
        
        #endregion

        #region Player Mode

        public void Initialize(NetworkPlayerData playerData, bool isLocalPlayer, bool viewerIsHost)
        {
            _isBandHeaderMode = false;
            _bandInfo = null;
            _bandId = -1;
            
            // Switch to player mode visuals
            if (playerContainer != null) playerContainer.SetActive(true);
            if (bandHeaderContainer != null) bandHeaderContainer.SetActive(false);
            
            // Ensure player elements are visible (in case this was previously a band header)
            if (playerNameText != null) playerNameText.gameObject.SetActive(true);
            if (pingText != null) pingText.gameObject.SetActive(true);
            if (pingIcon != null) pingIcon.gameObject.SetActive(true);
            // hostBadge and playerIcon visibility is handled by UpdateHostVisuals()
            
            // Switch to normal background style
            if (normalBackground != null) normalBackground.SetActive(true);
            if (categoryBackground != null) categoryBackground.SetActive(false);
            // selectedBackground is handled by SetSelected()
            
            _playerData = playerData;
            _isLocalPlayer = isLocalPlayer;
            _viewerIsHost = viewerIsHost;
            // Use the synced IsHost property from NetworkPlayerData
            _isHost = playerData.IsHost;
            
            Debug.Log($"[PlayerView] Initialize: name='{playerData.PlayerName}', playerIsHost={_isHost}, viewerIsHost={viewerIsHost}, isLocalPlayer={isLocalPlayer}, ping={playerData.Ping}");
            
            if (pingText != null && !_hasCachedPingTextColor)
            {
                _defaultPingTextColor = pingText.color;
                _hasCachedPingTextColor = true;
            }

            if (hostBadgeImage != null && !_hasCachedHostBadgeColor)
            {
                _hostBadgeDefaultColor = hostBadgeImage.color;
                _hasCachedHostBadgeColor = true;
            }

            UpdateDisplay();
            
            // Show kick button only if viewer is host and this is not the local player
            // In dedicated server mode, host cannot kick - only admin via web UI
            if (kickButton != null)
            {
                kickButton.OnClick.RemoveListener(OnKickClicked);
                bool canKick = viewerIsHost && !isLocalPlayer && !_isHost;
                
                // Check dedicated server permissions
                var dedicatedManager = YARG.Networking.DedicatedServer.DedicatedServerManager.Instance;
                if (dedicatedManager != null && !dedicatedManager.CanHostKickPlayers())
                {
                    canKick = false;
                }
                
                kickButton.gameObject.SetActive(canKick);
                
                if (canKick)
                {
                    MenuColors colors = MenuData.Instance != null ? MenuData.Colors : null;
                    if (colors != null)
                    {
                        kickButton.SetBackgroundAndTextColor(colors.CancelButton);
                    }

                    kickButton.OnClick.AddListener(OnKickClicked);
                }
            }
            
            // Set up reorder buttons (host only)
            SetupReorderButtons(viewerIsHost);
            
            // Set up move to band dropdown (host only, hidden until bands are configured)
            SetupMoveToBandDropdown(viewerIsHost);
            
            UpdateHostVisuals();
            
            // Subscribe to player data changes
            if (_playerData != null)
            {
                _playerData.OnPlayerNameChangedEvent += OnPlayerNameChanged;
                _playerData.OnInstrumentChangedEvent += OnInstrumentOrDifficultyChanged;
                _playerData.OnDifficultyChangedEvent += OnInstrumentOrDifficultyChanged;
            }
        }
        
        /// <summary>
        /// Sets up the reorder (move up/down) buttons.
        /// Buttons must be assigned in the prefab.
        /// Reordering is disabled when LocalPlayersFirst is enabled (tracks are auto-sorted).
        /// </summary>
        private void SetupReorderButtons(bool viewerIsHost)
        {
            // Check if LocalPlayersFirst is enabled - if so, disable reordering
            var trackOrderManager = TrackOrderManager.Instance;
            bool localPlayersFirstEnabled = trackOrderManager != null && trackOrderManager.LocalPlayersFirst;
            bool canReorder = viewerIsHost && !localPlayersFirstEnabled;
            
            // Show/hide container based on host status and LocalPlayersFirst setting
            if (reorderContainer != null)
            {
                reorderContainer.SetActive(canReorder);
            }
            
            // Wire up move up button
            if (moveUpButton != null)
            {
                moveUpButton.onClick.RemoveListener(HandleMoveUp);
                moveUpButton.gameObject.SetActive(canReorder);
                
                if (canReorder)
                {
                    moveUpButton.onClick.AddListener(HandleMoveUp);
                }
            }
            
            // Wire up move down button  
            if (moveDownButton != null)
            {
                moveDownButton.onClick.RemoveListener(HandleMoveDown);
                moveDownButton.gameObject.SetActive(canReorder);
                
                if (canReorder)
                {
                    moveDownButton.onClick.AddListener(HandleMoveDown);
                }
            }
        }
        
        /// <summary>
        /// Updates the interactability of move buttons based on position.
        /// Also updates visual appearance (greyed out when disabled).
        /// </summary>
        public void UpdateReorderButtonState(int playerIndex, int totalPlayers)
        {
            _playerIndex = playerIndex;
            _totalPlayers = totalPlayers;
            
            bool canMoveUp = playerIndex > 0;
            bool canMoveDown = playerIndex < totalPlayers - 1;
            
            if (moveUpButton != null)
            {
                moveUpButton.interactable = canMoveUp;
                UpdateButtonTextAlpha(moveUpButton, canMoveUp);
            }
            
            if (moveDownButton != null)
            {
                moveDownButton.interactable = canMoveDown;
                UpdateButtonTextAlpha(moveDownButton, canMoveDown);
            }
        }
        
        /// <summary>
        /// Updates the alpha of button text to match enabled/disabled state.
        /// </summary>
        private void UpdateButtonTextAlpha(Button button, bool enabled)
        {
            if (button == null) return;
            
            var text = button.GetComponentInChildren<TextMeshProUGUI>();
            if (text != null)
            {
                var color = text.color;
                color.a = enabled ? 1f : 0.3f;
                text.color = color;
            }
        }
        
        private void HandleMoveUp()
        {
            if (_playerData != null)
            {
                OnMoveUpClicked?.Invoke(_playerData);
            }
        }
        
        private void HandleMoveDown()
        {
            if (_playerData != null)
            {
                OnMoveDownClicked?.Invoke(_playerData);
            }
        }
        
        #region Move to Band Dropdown
        
        /// <summary>
        /// Sets up the move to band dropdown (host only).
        /// Dropdown is hidden until ConfigureMoveToBandDropdown is called with bands.
        /// In dedicated server mode, dropdown is always hidden (admin manages bands via web UI).
        /// </summary>
        private void SetupMoveToBandDropdown(bool viewerIsHost)
        {
            if (moveToBandDropdown == null)
                return;
            
            // In dedicated server mode, host cannot move players - hide dropdown
            var dedicatedManager = YARG.Networking.DedicatedServer.DedicatedServerManager.Instance;
            if (dedicatedManager != null && !dedicatedManager.CanHostMovePlayers())
            {
                if (moveToBandDropdown.transform.parent != null)
                {
                    moveToBandDropdown.transform.parent.gameObject.SetActive(false);
                }
                moveToBandDropdown.gameObject.SetActive(false);
                return;
            }
            
            // Initially hide dropdown and its parent container - will show when bands are configured
            if (moveToBandDropdown.transform.parent != null)
            {
                moveToBandDropdown.transform.parent.gameObject.SetActive(false);
            }
            moveToBandDropdown.gameObject.SetActive(false);
            
            // Wire up change listener
            moveToBandDropdown.onValueChanged.RemoveListener(HandleMoveToBandChanged);
            
            if (viewerIsHost)
            {
                moveToBandDropdown.onValueChanged.AddListener(HandleMoveToBandChanged);
            }
        }
        
        /// <summary>
        /// Configures the move to band dropdown with available bands.
        /// Call this after Initialize when band system is active.
        /// Shows when:
        /// - 2+ bands exist, OR
        /// - 1 band exists but it's over capacity (allows creating a new band)
        /// </summary>
        /// <param name="bands">All available bands in the session.</param>
        /// <param name="currentBandId">The band this player is currently in.</param>
        /// <param name="playerGroupSize">Number of players that will move together (for space checking).</param>
        /// <param name="bandSize">Maximum players allowed per band.</param>
        public void ConfigureMoveToBandDropdown(IReadOnlyList<BandInfo> bands, int currentBandId, int playerGroupSize, int bandSize)
        {
            Debug.Log($"[PlayerView] ConfigureMoveToBandDropdown: player={_playerData?.PlayerName}, dropdown={moveToBandDropdown != null}, viewerIsHost={_viewerIsHost}, isBandHeaderMode={_isBandHeaderMode}, bands={bands?.Count ?? 0}, bandSize={bandSize}");
            
            if (moveToBandDropdown == null || !_viewerIsHost || _isBandHeaderMode)
            {
                Debug.Log($"[PlayerView] ConfigureMoveToBandDropdown: Early return - dropdown={moveToBandDropdown != null}, viewerIsHost={_viewerIsHost}, isBandHeaderMode={_isBandHeaderMode}");
                return;
            }
            
            _currentBandId = currentBandId;
            _bandSize = bandSize;
            _availableBands = bands != null ? new List<BandInfo>(bands) : new List<BandInfo>();
            
            // Check if any band is over capacity
            bool anyBandOverCapacity = bandSize > 0 && _availableBands.Any(b => b.PlayerCount > bandSize);
            
            // Show dropdown if:
            // - 2+ bands exist (can move between them), OR
            // - 1 band exists but it's over capacity (need to create new band to redistribute)
            bool shouldShowDropdown = _availableBands.Count >= 2 || 
                                      (_availableBands.Count == 1 && anyBandOverCapacity);
            
            if (!shouldShowDropdown)
            {
                Debug.Log($"[PlayerView] ConfigureMoveToBandDropdown: Hiding dropdown - bands={_availableBands.Count}, anyOverCapacity={anyBandOverCapacity}");
                HideMoveToBandDropdown();
                return;
            }
            
            _isUpdatingDropdown = true;
            
            moveToBandDropdown.ClearOptions();
            
            var options = new List<TMP_Dropdown.OptionData>();
            int currentIndex = 0;
            int index = 0;
            
            foreach (var band in _availableBands)
            {
                // Show band name with current player count
                // We allow over-capacity moves - validation happens at game start
                string optionText = band.DisplayName;
                if (bandSize > 0)
                {
                    bool isOverCapacity = band.PlayerCount > bandSize;
                    string countDisplay = isOverCapacity 
                        ? $"({band.PlayerCount}/{bandSize} ⚠)" 
                        : $"({band.PlayerCount}/{bandSize})";
                    optionText += $" {countDisplay}";
                }
                
                options.Add(new TMP_Dropdown.OptionData(optionText));
                
                if (band.BandId == currentBandId)
                {
                    currentIndex = index;
                }
                index++;
            }
            
            // Add "Create New Band" option - always available when dropdown is shown
            options.Add(new TMP_Dropdown.OptionData("➕ Create New Band"));
            
            moveToBandDropdown.AddOptions(options);
            moveToBandDropdown.value = currentIndex;
            moveToBandDropdown.RefreshShownValue();
            
            _isUpdatingDropdown = false;
            
            // Enable both the dropdown AND its parent container (which may be disabled by default in prefab)
            if (moveToBandDropdown.transform.parent != null)
            {
                moveToBandDropdown.transform.parent.gameObject.SetActive(true);
            }
            moveToBandDropdown.gameObject.SetActive(true);
            Debug.Log($"[PlayerView] ConfigureMoveToBandDropdown COMPLETE: player={_playerData?.PlayerName}, options={options.Count}, dropdown.activeSelf={moveToBandDropdown.gameObject.activeSelf}, dropdown.activeInHierarchy={moveToBandDropdown.gameObject.activeInHierarchy}, parent={moveToBandDropdown.transform.parent?.name}, parentActive={moveToBandDropdown.transform.parent?.gameObject.activeSelf}");
        }
        
        /// <summary>
        /// Hides the move to band dropdown (e.g., when bands are disabled).
        /// </summary>
        public void HideMoveToBandDropdown()
        {
            if (moveToBandDropdown != null)
            {
                // Hide the parent container (which contains the dropdown)
                if (moveToBandDropdown.transform.parent != null)
                {
                    moveToBandDropdown.transform.parent.gameObject.SetActive(false);
                }
                moveToBandDropdown.gameObject.SetActive(false);
            }
        }
        
        /// <summary>
        /// Handles dropdown value change - fires event to move player to selected band or create a new band.
        /// </summary>
        private void HandleMoveToBandChanged(int selectedIndex)
        {
            // Ignore changes while programmatically updating
            if (_isUpdatingDropdown)
                return;
            
            if (_playerData == null || _availableBands == null || selectedIndex < 0)
                return;
            
            // Check if "Create New Band" was selected (last option)
            if (selectedIndex >= _availableBands.Count)
            {
                Debug.Log($"[PlayerView] Creating new band for player {_playerData.PlayerName}");
                OnCreateNewBandSelected?.Invoke(_playerData);
                return;
            }
            
            var targetBand = _availableBands[selectedIndex];
            
            // Don't fire event if selecting current band
            if (targetBand.BandId == _currentBandId)
                return;
            
            Debug.Log($"[PlayerView] Moving player {_playerData.PlayerName} to band {targetBand.DisplayName} (ID: {targetBand.BandId})");
            OnMoveToBandSelected?.Invoke(_playerData, targetBand.BandId);
        }
        
        #endregion

        private void OnDestroy()
        {
            // Unsubscribe from events
            if (_playerData != null)
            {
                _playerData.OnPlayerNameChangedEvent -= OnPlayerNameChanged;
                _playerData.OnInstrumentChangedEvent -= OnInstrumentOrDifficultyChanged;
                _playerData.OnDifficultyChangedEvent -= OnInstrumentOrDifficultyChanged;
            }
            
            if (kickButton != null)
            {
                kickButton.OnClick.RemoveListener(OnKickClicked);
            }
            
            // Clean up reorder button listeners
            if (moveUpButton != null)
            {
                moveUpButton.onClick.RemoveListener(HandleMoveUp);
            }
            
            if (moveDownButton != null)
            {
                moveDownButton.onClick.RemoveListener(HandleMoveDown);
            }
            
            // Clean up band header button listener
            if (regenerateNameButton != null)
            {
                regenerateNameButton.onClick.RemoveListener(HandleRegenerateName);
            }
            
            // Clean up move to band dropdown listener
            if (moveToBandDropdown != null)
            {
                moveToBandDropdown.onValueChanged.RemoveListener(HandleMoveToBandChanged);
            }
        }
        
        #endregion

        private void Update()
        {
            // Update ping every frame (or throttle if needed)
            UpdatePing();
        }

        private void UpdateDisplay()
        {
            if (_playerData == null) return;
            
            // Update player name
            if (playerNameText != null)
            {
                string displayName = _playerData.PlayerName;
                if (_isLocalPlayer) displayName += " (You)";
                playerNameText.text = displayName;
            }

            UpdateHostVisuals();
            
            // Update instrument
            UpdateInstrument();
        }

        private void UpdatePing()
        {
            if (pingText == null || _playerData == null) return;
            
            float? pingValue = null;

            // Local players show 0ms - no ping to yourself
            // This includes both:
            // - Host viewing themselves (they are local)
            // - Client viewing themselves (they are local)
            // Remote players (host viewing client, or client viewing host) show actual ping
            if (_isLocalPlayer)
            {
                pingText.text = "0ms";
                pingValue = 0f;
            }
            else
            {
                // Remote players show their ping/latency
                float ping = _playerData.Ping;
                if (ping >= 0f)
                {
                    // Valid ping data (0ms is valid for localhost connections)
                    pingValue = ping;
                    pingText.text = $"{Mathf.RoundToInt(ping)}ms";
                }
                else
                {
                    // Negative ping (-1) means no ping data yet - show placeholder
                    pingText.text = "--";
                    pingValue = null;
                }
            }

            UpdatePingColor(pingValue);
        }

        private void UpdateInstrument()
        {
            if (_playerData == null)
            {
                Debug.Log($"[PlayerView] UpdateInstrument: playerData is null");
                return;
            }

            int instrumentIndex = _playerData.Instrument;
            Debug.Log($"[PlayerView] UpdateInstrument: player={_playerData.PlayerName}, instrumentIndex={instrumentIndex}");
            
            if (instrumentIndex < 0)
            {
                Debug.Log($"[PlayerView] UpdateInstrument: No instrument selected (index < 0), hiding icon");
                UpdateInstrumentIcon(null);
                return;
            }

            try
            {
                var instrument = (Instrument)instrumentIndex;
                Debug.Log($"[PlayerView] UpdateInstrument: Loading icon for {instrument}");
                UpdateInstrumentIcon(instrument);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PlayerView] Failed to interpret instrument value '{instrumentIndex}': {ex.Message}");
                UpdateInstrumentIcon(null);
            }
        }

        private void OnPlayerNameChanged(string newName)
        {
            UpdateDisplay();
        }

        private void OnInstrumentOrDifficultyChanged(int newInstrument, int newDifficulty)
        {
            UpdateInstrument();
        }

        private void OnKickClicked()
        {
            if (_playerData == null)
            {
                Debug.LogWarning("[PlayerView] Cannot kick player - no player data");
                return;
            }
            
            Debug.Log($"[PlayerView] Kicking player: {_playerData.PlayerName}");
            
            // Use abstraction layer to kick player
            var networkingService = NetworkingServiceFactory.Instance;
            if (networkingService != null && networkingService.IsHosting)
            {
                networkingService.KickPlayer(_playerData);
            }
            else
            {
                Debug.LogWarning("[PlayerView] Cannot kick - not hosting or networking service unavailable");
            }
        }

        /// <summary>
        /// Manually update the view (call if player data changes outside of events)
        /// </summary>
        public void Refresh()
        {
            UpdateDisplay();
            UpdatePing();
        }
        
        /// <summary>
        /// Set the selection state of this player view
        /// </summary>
        public void SetSelected(bool selected)
        {
            _isSelected = selected;
            UpdateSelectionVisual();
        }
        
        private void UpdateSelectionVisual()
        {
            // Toggle background visibility
            if (normalBackground != null)
            {
                normalBackground.SetActive(!_isSelected);
            }
            
            if (selectedBackground != null)
            {
                selectedBackground.SetActive(_isSelected);
            }
            
            // Optional: Update highlight image color/alpha
            if (highlightImage != null)
            {
                var color = highlightImage.color;
                color.a = _isSelected ? 0.3f : 0f;
                highlightImage.color = color;
            }
        }

        private void UpdateHostVisuals()
        {
            if (hostBadge != null)
            {
                if (!hostBadge.activeSelf)
                {
                    hostBadge.SetActive(true);
                }

                var badgeImage = hostBadgeImage != null ? hostBadgeImage : hostBadge.GetComponent<Image>();
                if (badgeImage != null)
                {
                    if (!_hasCachedHostBadgeColor)
                    {
                        _hostBadgeDefaultColor = badgeImage.color;
                        _hasCachedHostBadgeColor = true;
                    }

                    badgeImage.enabled = _isHost;
                    badgeImage.raycastTarget = _isHost;

                    if (_isHost && _hasCachedHostBadgeColor)
                    {
                        badgeImage.color = _hostBadgeDefaultColor;
                    }
                }
            }

            if (playerIcon != null && !playerIcon.activeSelf)
            {
                playerIcon.SetActive(true);
            }
        }

        private void UpdatePingColor(float? ping)
        {
            if (pingText == null) return;

            if (!_hasCachedPingTextColor)
            {
                _defaultPingTextColor = pingText.color;
                _hasCachedPingTextColor = true;
            }

            Color fallback = _hasCachedPingTextColor ? _defaultPingTextColor : pingText.color;
            Color targetColor = fallback;

            if (ping.HasValue)
            {
                float value = Mathf.Max(0f, ping.Value);

                if (value <= Mathf.Epsilon)
                {
                    targetColor = PING_ZERO_COLOR;
                }
                else if (value < PING_GOOD_THRESHOLD)
                {
                    targetColor = PING_GOOD_COLOR;
                }
                else if (value < PING_AVERAGE_THRESHOLD)
                {
                    targetColor = PING_AVERAGE_COLOR;
                }
                else
                {
                    targetColor = PING_POOR_COLOR;
                }
            }

            pingText.color = targetColor;

            if (pingIcon != null)
            {
                pingIcon.color = targetColor;
                if (!pingIcon.gameObject.activeSelf)
                {
                    pingIcon.gameObject.SetActive(true);
                }
            }
        }

        private void UpdateInstrumentIcon(Instrument? instrument)
        {
            if (playerIcon != null && !playerIcon.activeSelf)
            {
                playerIcon.SetActive(true);
            }

            if (playerIconImage == null)
            {
                Debug.LogWarning("[PlayerView] UpdateInstrumentIcon: playerIconImage is null");
                return;
            }

            if (!instrument.HasValue)
            {
                Debug.Log("[PlayerView] UpdateInstrumentIcon: No instrument, disabling icon");
                playerIconImage.enabled = false;
                playerIconImage.sprite = null;
                _currentInstrumentIconKey = null;
                return;
            }

            string resourceName;
            try
            {
                resourceName = instrument.Value.ToResourceName();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PlayerView] Failed to resolve resource name for instrument '{instrument}': {ex.Message}");
                resourceName = null;
            }

            if (string.IsNullOrEmpty(resourceName))
            {
                var instrumentValue = instrument.Value;
                resourceName = instrumentValue switch
                {
                    Instrument.SixFretBass => "bass",
                    Instrument.SixFretGuitar or Instrument.SixFretRhythm or Instrument.SixFretCoopGuitar => "guitar",
                    Instrument.FourLaneDrums or Instrument.ProDrums or Instrument.FiveLaneDrums or Instrument.EliteDrums => "drums",
                    Instrument.ProGuitar_17Fret or Instrument.ProGuitar_22Fret or Instrument.ProBass_17Fret or Instrument.ProBass_22Fret => "realGuitar",
                    Instrument.ProKeys => "realKeys",
                    Instrument.Vocals or Instrument.Harmony => "vocals",
                    Instrument.Band => "guitar",
                    _ => "guitar"
                };
                Debug.Log($"[PlayerView] UpdateInstrumentIcon: ToResourceName returned null, using fallback: {resourceName}");
            }

            string address = $"InstrumentIcons[{resourceName}]";
            Debug.Log($"[PlayerView] UpdateInstrumentIcon: Loading addressable '{address}'");
            
            if (_currentInstrumentIconKey == address && playerIconImage.sprite != null)
            {
                Debug.Log($"[PlayerView] UpdateInstrumentIcon: Icon already loaded, enabling");
                playerIconImage.enabled = true;
                return;
            }

            try
            {
                var sprite = Addressables.LoadAssetAsync<Sprite>(address).WaitForCompletion();
                playerIconImage.sprite = sprite;
                playerIconImage.enabled = sprite != null;
                _currentInstrumentIconKey = sprite != null ? address : null;
                Debug.Log($"[PlayerView] UpdateInstrumentIcon: Loaded sprite={sprite != null}, enabled={playerIconImage.enabled}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PlayerView] Failed to load instrument icon '{address}': {ex.Message}");
                playerIconImage.enabled = false;
                playerIconImage.sprite = null;
                _currentInstrumentIconKey = null;
            }
        }
    }
}
