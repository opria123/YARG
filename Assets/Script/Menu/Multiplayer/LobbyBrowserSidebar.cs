using System;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using YARG.Core;
using YARG.Helpers;
using YARG.Helpers.Extensions;
using YARG.Menu;
using YARG.Menu.Data;
using YARG.Menu.DifficultySelect;
using YARG.Menu.Persistent;
using YARG.Networking;
using YARG.Networking.Abstraction;
using YARG.Networking.Bookmarks;
using YARG.Networking.Settings;
using YARG.Menu.Multiplayer.Settings;

namespace YARG.Menu.Multiplayer
{
    /// <summary>
    /// Sidebar controller responsible for rendering the various lobby browser cards:
    /// lobby details, bookmarks, direct connect form, and hosted lobby presets.
    /// </summary>
    public class LobbyBrowserSidebar : MonoBehaviour
    {
        private static int DefaultDirectConnectPort => NetworkTransportDefaults.DefaultUdpPort;
        private static int GetSuggestedPort()
        {
            var networkService = NetworkingServiceFactory.Instance;
            if (networkService != null)
            {
                int port = networkService.DefaultPort;
                if (port > 0)
                    return Mathf.Clamp(port, 1, ushort.MaxValue);
            }
            return DefaultDirectConnectPort;
        }
        
        private static string GetPlayerName()
        {
            var networkService = NetworkingServiceFactory.Instance;
            return networkService?.PlayerName ?? "YARG";
        }


        [Header("Containers")]
        [SerializeField]
        private GameObject _emptyStateContainer;
        [SerializeField]
        private GameObject _contentContainer;
        [SerializeField]
        private GameObject _createLobbyContainer;
        [SerializeField]
        private GameObject _hostedLobbyContainer;
        [SerializeField]
        private GameObject _directConnectContainer;

        [Header("Data Visibility")]
        [SerializeField]
        private Button _hostVisibilityToggle;
        [SerializeField]
        private Sprite _hostVisibleSprite;
        [SerializeField]
        private Sprite _hostHiddenSprite;
        [SerializeField]
        private TextMeshProUGUI _passwordValueText;
        [SerializeField]
        private Button _passwordVisibilityToggle;
        [SerializeField]
        private Sprite _passwordVisibleSprite;
        [SerializeField]
        private Sprite _passwordHiddenSprite;

        [Header("Editable Containers")]
        [SerializeField]
        private GameObject _lobbyNameViewContainer;
        [SerializeField]
        private GameObject _lobbyNameEditContainer;
        [SerializeField]
        private Button _lobbyNameEditButton;
        [SerializeField]
        private GameObject _hostAddressViewContainer;
        [SerializeField]
        private GameObject _hostAddressEditContainer;
        [SerializeField]
        private Button _hostAddressEditButton;
        [SerializeField]
        private GameObject _passwordViewContainer;
        [SerializeField]
        private GameObject _passwordEditContainer;
        [SerializeField]
        private Button _passwordEditButton;

        [Header("Lobby Info")]
        [SerializeField]
        private TextMeshProUGUI _lobbyNameText;
        [SerializeField]
        private TextMeshProUGUI _hostNameText;
        [SerializeField]
        private TextMeshProUGUI _playerCountText;
        [SerializeField]
        private TextMeshProUGUI _pingText;
        [SerializeField]
        private TextMeshProUGUI _privacyText;
        [SerializeField]
        private GameObject _passwordIcon;

        [Header("Lobby Settings Icons")]
        [SerializeField]
        private Image _noFailIconImage;
        
        [Header("Lobby Game Modes")]
        [SerializeField]
        private Image _guitarIconImage;
        [SerializeField]
        private Image _drumsIconImage;
        [SerializeField]
        private Image _vocalsIconImage;
        [SerializeField]
        private Image _keysIconImage;

        [Header("Player List")]
        [SerializeField]
        private Transform _playerListContainer;
        [SerializeField]
        private GameObject _playerEntryPrefab;
        [SerializeField]
        private TextMeshProUGUI _noPlayersText;

        [Header("Create Lobby Form")]
        [SerializeField]
        private SessionSettingsPanelBuilder _createLobbySettingsPanel;
        [SerializeField]
        private ColoredButton _createLobbySubmitButton;
        [SerializeField]
        private Button _createLobbyCancelButton;

        [Header("Hosted Lobby Form")]
        [SerializeField]
        private SessionSettingsPanelBuilder _hostedLobbySettingsPanel;
        [SerializeField]
        private ColoredButton _hostedLobbyHostButton;
        [SerializeField]
        private ColoredButton _hostedLobbyDeleteButton;

        [Header("Direct Connect Form")]
        [SerializeField]
        private TMP_InputField _directConnectAddressInput;
        [SerializeField]
        private TMP_InputField _directConnectPasswordInput;
        [SerializeField]
        private ColoredButton _directConnectSubmitButton;
        [SerializeField]
        private Button _directConnectCancelButton;
        
        [Header("Join by Code")]
        [SerializeField]
        private TMP_InputField _lobbyCodeInput;

        private LobbyBrowserMenu _menu;
        private YARG.Networking.Abstraction.LobbyInfo _currentLobby;
        private HostedLobbyPreset _activePreset;
        private LobbyBookmark _activeBookmark;
        private SidebarMode _currentMode = SidebarMode.Empty;
        private bool _listenersRegistered;
        private string _currentHostAddress = string.Empty;
        private string _currentPassword = string.Empty;
        private bool _isHostAddressVisible;
        private bool _isPasswordVisible;
        private bool _hostToggleAvailable;
        private bool _hasPassword;
        private bool _passwordToggleAvailable;
        private TMP_InputField _passwordEditInputField;
        private EditableField? _activeEditField;
        private LobbyPrivacyMode _currentPrivacyMode = LobbyPrivacyMode.Public;
        private int _defaultMaxPlayersOptionIndex;
        private bool _attemptedHostedContainerResolve;
        private bool IsEditing => _activeEditField.HasValue;

        private enum EditableField
        {
            LobbyName,
            HostAddress,
            Password
        }

        private readonly struct LobbyPlayerEntry
        {
            public LobbyPlayerEntry(string displayName, string instrumentMarkup)
            {
                DisplayName = displayName;
                InstrumentMarkup = instrumentMarkup;
            }

            public string DisplayName { get; }
            public string InstrumentMarkup { get; }
        }

        public event Action<CreateLobbyFormData> CreateLobbySubmitted;
        public event Action<DirectConnectFormData> DirectConnectSubmitted;
        public event Action<string> JoinByCodeSubmitted;

        private enum SidebarMode
        {
            Empty,
            Lobby,
            CreateLobby,
            HostedLobby,
            DirectConnect,
            JoinGame  // New: Combined join mode (code + direct connect)
        }

        #region Initialization

        public void Initialize(LobbyBrowserMenu menu)
        {
            _menu = menu;

            EnsureContainerReferences();
            ApplyButtonColors();

            if (!_listenersRegistered)
            {
                RegisterButtonListeners();
                PopulateDropdowns();
                _listenersRegistered = true;
            }

            ClearLobby();
        }

        private void RegisterButtonListeners()
        {
            if (_createLobbySubmitButton != null)
                _createLobbySubmitButton.OnClick.AddListener(SubmitCreateLobbyForm);

            if (_createLobbyCancelButton != null)
                _createLobbyCancelButton.onClick.AddListener(ClearLobby);

            if (_hostVisibilityToggle != null)
                _hostVisibilityToggle.onClick.AddListener(ToggleHostVisibility);

            if (_passwordVisibilityToggle != null)
                _passwordVisibilityToggle.onClick.AddListener(TogglePasswordVisibility);

            if (_lobbyNameEditButton != null)
                _lobbyNameEditButton.onClick.AddListener(BeginLobbyNameEdit);

            if (_hostAddressEditButton != null)
                _hostAddressEditButton.onClick.AddListener(BeginHostAddressEdit);

            if (_passwordEditButton != null)
                _passwordEditButton.onClick.AddListener(BeginPasswordEdit);

            if (_hostedLobbyHostButton != null)
                _hostedLobbyHostButton.OnClick.AddListener(HandleHostedHost);

            if (_hostedLobbyDeleteButton != null)
                _hostedLobbyDeleteButton.OnClick.AddListener(HandleHostedDelete);

            if (_directConnectSubmitButton != null)
                _directConnectSubmitButton.OnClick.AddListener(SubmitConnectForm);

            if (_directConnectCancelButton != null)
                _directConnectCancelButton.onClick.AddListener(ClearLobby);

            AttachEditableInputHandlers(_lobbyNameEditContainer, ConfirmLobbyNameEdit, EditableField.LobbyName);
            AttachEditableInputHandlers(_hostAddressEditContainer, ConfirmHostAddressEdit, EditableField.HostAddress);
            AttachEditableInputHandlers(_passwordEditContainer, ConfirmPasswordEdit, EditableField.Password);
        }

        private void ApplyButtonColors()
        {
            var colors = MenuData.Colors;
            if (colors == null)
                return;

            if (_createLobbySubmitButton != null)
                _createLobbySubmitButton.SetBackgroundAndTextColor(colors.ConfirmButton);

            if (_hostedLobbyHostButton != null)
                _hostedLobbyHostButton.SetBackgroundAndTextColor(colors.ConfirmButton);

            if (_hostedLobbyDeleteButton != null)
                _hostedLobbyDeleteButton.SetBackgroundAndTextColor(colors.CancelButton);

            if (_directConnectSubmitButton != null)
                _directConnectSubmitButton.SetBackgroundAndTextColor(colors.ConfirmButton);
        }

        private void PopulateDropdowns()
        {
            // SessionSettingsPanelBuilder handles dropdown population internally
            // Keep default max players index for reference if needed
            _defaultMaxPlayersOptionIndex = 0;
        }

        private void AttachEditableInputHandlers(GameObject container, Action confirmAction, EditableField? field = null)
        {
            if (container == null || confirmAction == null)
                return;

            TMP_InputField input = null;

            if (field == EditableField.Password)
            {
                input = EnsurePasswordEditInputField();
            }
            else
            {
                input = container.GetComponentInChildren<TMP_InputField>(true);
            }

            if (input == null)
                return;

            input.onSubmit.AddListener(_ => confirmAction());
            input.onEndEdit.AddListener(_ => confirmAction());
        }

        #endregion

        #region Public API

        public void SetLobby(YARG.Networking.Abstraction.LobbyInfo lobby, LobbyBookmark bookmarkOverride = null)
        {
            if (lobby == null)
            {
                ClearLobby();
                return;
            }

            var store = LobbyBookmarkStore.Instance;

            ExitAllEditModes();
            _currentLobby = lobby;
            _activePreset = null;
            _activeBookmark = bookmarkOverride
                ?? store.GetFavorite(lobby.IpAddress, lobby.Port)
                ?? store.GetRecent(lobby.IpAddress, lobby.Port);

            ShowMode(SidebarMode.Lobby);
            PopulateLobbyInfo(lobby);
            ApplyBookmarkOverlayData(_activeBookmark);
            UpdatePlayerList(lobby);
            RefreshEditableButtons();
        }

        public void SetBookmark(LobbyBookmark bookmark)
        {
            bool editingCurrentBookmark = IsEditingBookmark(bookmark);

            if (!editingCurrentBookmark)
            {
                ExitAllEditModes();
            }

            if (bookmark == null)
            {
                if (!editingCurrentBookmark)
                {
                    ClearLobby();
                }
                return;
            }

            _activeBookmark = bookmark;
            _currentLobby = null;
            _activePreset = null;

            ShowMode(SidebarMode.Lobby);

            if (editingCurrentBookmark)
            {
                // Keep user input intact while ensuring sidebar remains in bookmark mode.
                _currentLobby = null;
                return;
            }

            PopulateBookmarkInfo(bookmark);
            RefreshEditableButtons();
        }

        public void ShowCreateLobbyForm(HostedLobbyPreset preset, bool focusFirstField = false)
        {
            bool wasCreateMode = _currentMode == SidebarMode.CreateLobby;
            bool presetChanged = !HostedPresetsEquivalent(_activePreset, preset);
            bool shouldReset = !wasCreateMode || focusFirstField || presetChanged;

            if (shouldReset)
            {
                _activePreset = preset?.Clone();
                ExitAllEditModes();
            }

            _currentLobby = null;
            _activeBookmark = null;

            ShowMode(SidebarMode.CreateLobby);

            // Use SessionSettingsPanelBuilder for consistent UX
            ApplyCreateLobbyToSettingsPanel(shouldReset, focusFirstField);
        }

        public void ShowDirectConnectForm(bool focusFirstField = false)
        {
            // If already showing direct connect, don't reset the form - just preserve user input
            if (_currentMode == SidebarMode.DirectConnect && !focusFirstField)
            {
                return;
            }
            
            _activePreset = null;
            _currentLobby = null;
            _activeBookmark = null;

            ExitAllEditModes();
            ShowMode(SidebarMode.DirectConnect);

            if (_directConnectAddressInput != null)
            {
                SetInputFieldText(_directConnectAddressInput, string.Empty);
                if (focusFirstField)
                {
                    FocusInput(_directConnectAddressInput);
                }
            }

            if (_directConnectPasswordInput != null)
            {
                SetInputFieldText(_directConnectPasswordInput, string.Empty);
            }
            
            // Clear lobby code input
            ClearLobbyCodeInput();
            
            RefreshEditableButtons();
        }

        /// <summary>
        /// Shows the "Host a Game" form. This is the new name for ShowCreateLobbyForm.
        /// Includes session type selection (Automatic/Manual) in Advanced Options.
        /// </summary>
        public void ShowHostGameForm(HostedLobbyPreset preset, bool focusFirstField = false)
        {
            // Delegates to CreateLobby form which includes Session Type in settings panel
            ShowCreateLobbyForm(preset, focusFirstField);
        }

        /// <summary>
        /// Shows the "Join a Game" form with both lobby code and direct connect options.
        /// </summary>
        public void ShowJoinGameForm(bool focusFirstField = false)
        {
            // Delegates to DirectConnect form which includes lobby code input
            ShowDirectConnectForm(focusFirstField);
        }

        public void ShowHostedLobbyPreset(HostedLobbyPreset preset)
        {
            if (preset == null)
            {
                ClearLobby();
                return;
            }

            _activePreset = preset.Clone();
            _currentLobby = null;
            _activeBookmark = null;

            ExitAllEditModes();
            ShowMode(SidebarMode.HostedLobby);

            // Use SessionSettingsPanelBuilder for consistent UX
            ApplyHostedLobbyToSettingsPanel();

            RefreshEditableButtons();
        }

        public void ClearLobby()
        {
            ExitAllEditModes();
            _currentLobby = null;
            _activePreset = null;
            _activeBookmark = null;
            _currentPrivacyMode = LobbyPrivacyMode.Public;
            ShowMode(SidebarMode.Empty);
            ClearPlayerList();
            SetHostAddress(string.Empty, false);
            SetPasswordValue(string.Empty, false, false);
            ResetHostedForm();
            RefreshEditableButtons();
        }

        #endregion

        #region Lobby Info Rendering

        private void PopulateLobbyInfo(YARG.Networking.Abstraction.LobbyInfo lobby)
        {
            if (_lobbyNameText != null)
                _lobbyNameText.text = lobby.LobbyName;

            string endpoint = BuildEndpoint(lobby.IpAddress, lobby.Port, lobby.PublicAddress, lobby.PublicPort);
            SetHostAddress(endpoint, !string.IsNullOrEmpty(endpoint));

            if (_playerCountText != null)
            {
                bool lobbyFull = lobby.CurrentPlayers >= lobby.MaxPlayers;
                Color countColor = lobbyFull ? new Color(1f, 0.3f, 0.3f) : MenuData.Colors.PrimaryText;
                string value = ZString.Format("{0}/{1}", lobby.CurrentPlayers, lobby.MaxPlayers);
                _playerCountText.text = TextColorer.StyleString(value, countColor, 600);
            }

            if (_pingText != null)
            {
                // If lobby is not fresh (hasn't been seen recently), show as Offline
                if (!lobby.IsFresh)
                {
                    _pingText.text = TextColorer.StyleString("Offline", MenuData.Colors.PrimaryText.WithAlpha(0.45f), 600);
                }
                else if (lobby.Ping >= 0)
                {
                    // We have an actual ping measurement
                    Color pingColor = lobby.Ping switch
                    {
                        < 50 => new Color(0.3f, 1f, 0.3f),
                        < 100 => new Color(1f, 1f, 0.3f),
                        _ => new Color(1f, 0.3f, 0.3f)
                    };
                    string pingValue = TextColorer.StyleString(ZString.Format("{0}ms", lobby.Ping), pingColor, 600);
                    _pingText.text = pingValue;
                }
                else
                {
                    // Fresh lobby but no ping measurement - show as Online
                    _pingText.text = TextColorer.StyleString("Online", new Color(0.3f, 1f, 0.3f), 600);
                }
            }

            if (_privacyText != null)
            {
                string privacyMode = lobby.PrivacyMode switch
                {
                    YARG.Networking.Abstraction.LobbyPrivacyMode.Private => "Private",
                    YARG.Networking.Abstraction.LobbyPrivacyMode.Unlisted => "Unlisted",
                    _ => "Public"
                };
                _privacyText.text = ZString.Format("Privacy: {0}", privacyMode);
            }

            bool hasPassword = lobby.HasPassword;
            if (_passwordIcon != null)
                _passwordIcon.SetActive(hasPassword);

            _currentPrivacyMode = (LobbyPrivacyMode)lobby.PrivacyMode;
            SetPasswordValue(lobby.Password, hasPassword, hasPassword);
            RefreshEditableButtons();
            
            // Determine if server is offline (not seen recently)
            bool isOffline = !lobby.IsFresh;
            
            // Display gameplay settings tags
            PopulateLobbySettingsTags(lobby, isOffline);
            
            // Display game mode icons
            PopulateGameModeIcons(lobby, isOffline);
        }
        
        /// <summary>
        /// Builds and displays the No Fail icon. Always visible, greyed out when disabled or offline.
        /// </summary>
        private void PopulateLobbySettingsTags(YARG.Networking.Abstraction.LobbyInfo lobby, bool isOffline)
        {
            if (_noFailIconImage == null)
                return;
            
            // No Fail Mode icon - white when on, greyed when off or offline
            bool noFailEnabled = lobby.NoFailMode && !isOffline;
            _noFailIconImage.color = noFailEnabled ? Color.white : new Color(0.4f, 0.4f, 0.4f, 0.5f);
        }
        
        /// <summary>
        /// Updates game mode icon colors based on lobby settings.
        /// </summary>
        private void PopulateGameModeIcons(YARG.Networking.Abstraction.LobbyInfo lobby, bool isOffline)
        {
            // AllowedGameModes is actually a BLACKLIST - modes IN the list are DISABLED
            var disabledModes = new HashSet<YARG.Core.GameMode>();
            if (lobby.AllowedGameModes != null)
            {
                foreach (var mode in lobby.AllowedGameModes)
                {
                    disabledModes.Add(mode);
                }
            }
            
            // Mode is allowed if it's NOT in the blacklist AND server is online
            // When offline, all icons are greyed out
            UpdateIconColor(_guitarIconImage, !isOffline && !disabledModes.Contains(YARG.Core.GameMode.FiveFretGuitar));
            UpdateIconColor(_drumsIconImage, !isOffline && !disabledModes.Contains(YARG.Core.GameMode.FourLaneDrums));
            UpdateIconColor(_vocalsIconImage, !isOffline && !disabledModes.Contains(YARG.Core.GameMode.Vocals));
            UpdateIconColor(_keysIconImage, !isOffline && !disabledModes.Contains(YARG.Core.GameMode.ProKeys));
        }
        
        private void UpdateIconColor(Image icon, bool isEnabled)
        {
            if (icon != null)
            {
                icon.color = isEnabled ? Color.white : new Color(0.4f, 0.4f, 0.4f, 0.5f);
            }
        }

        private void ApplyBookmarkOverlayData(LobbyBookmark bookmark)
        {
            if (bookmark == null)
                return;

            bool storedHasPassword = !string.IsNullOrEmpty(bookmark.password);
            if (storedHasPassword || !_hasPassword)
            {
                SetPasswordValue(bookmark.password, storedHasPassword || _hasPassword, true);
            }

            if (_passwordIcon != null && storedHasPassword && !_passwordIcon.activeSelf)
            {
                _passwordIcon.SetActive(true);
            }
        }

        private void PopulateBookmarkInfo(LobbyBookmark bookmark)
        {
            if (_lobbyNameText != null)
            {
                _lobbyNameText.text = string.IsNullOrWhiteSpace(bookmark.displayName)
                    ? bookmark.address
                    : bookmark.displayName;
            }

            string endpoint = EndpointUtility.FormatEndpoint(bookmark.address, bookmark.port > 0 ? bookmark.port : GetSuggestedPort());
            SetHostAddress(endpoint, !string.IsNullOrEmpty(endpoint));

            if (_playerCountText != null)
            {
                _playerCountText.text = TextColorer.StyleString("Offline", MenuData.Colors.PrimaryText.WithAlpha(0.45f), 600);
            }

            if (_pingText != null)
            {
                _pingText.text = TextColorer.StyleString("Offline", MenuData.Colors.PrimaryText.WithAlpha(0.45f), 600);
            }

            if (_privacyText != null)
            {
                _privacyText.text = string.IsNullOrEmpty(bookmark.password) ? "No password saved" : "Password saved";
            }

            if (_passwordIcon != null)
            {
                _passwordIcon.SetActive(!string.IsNullOrEmpty(bookmark.password));
            }

            ClearPlayerList();

            if (_noPlayersText != null)
            {
                _noPlayersText.gameObject.SetActive(true);
                _noPlayersText.text = "Live player list unavailable for saved servers.";
            }

            _currentPrivacyMode = string.IsNullOrEmpty(bookmark.password)
                ? LobbyPrivacyMode.Public
                : LobbyPrivacyMode.Private;
            SetPasswordValue(bookmark.password, !string.IsNullOrEmpty(bookmark.password), true);
            RefreshEditableButtons();
        }

        private void UpdatePlayerList(YARG.Networking.Abstraction.LobbyInfo lobby)
        {
            ClearPlayerList();

            var playerEntries = BuildLobbyPlayerEntries(lobby);

            if (playerEntries.Count == 0)
            {
                if (_noPlayersText != null)
                {
                    _noPlayersText.gameObject.SetActive(true);
                    string label = lobby.CurrentPlayers <= 0
                        ? "No players in lobby"
                        : ZString.Format("{0} {1} in lobby",
                            lobby.CurrentPlayers,
                            lobby.CurrentPlayers == 1 ? "player" : "players");
                    _noPlayersText.text = label;
                }
                return;
            }

            if (_noPlayersText != null)
                _noPlayersText.gameObject.SetActive(false);

            foreach (var entry in playerEntries)
            {
                AddPlayerListEntry(entry);
            }
        }

        private List<LobbyPlayerEntry> BuildLobbyPlayerEntries(YARG.Networking.Abstraction.LobbyInfo lobby)
        {
            var entries = new List<LobbyPlayerEntry>();
            if (lobby == null)
                return entries;

            string hostName = string.IsNullOrWhiteSpace(lobby.HostName) ? string.Empty : lobby.HostName.Trim();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string[] playerNames = lobby.PlayerNames;
            int[] instruments = lobby.PlayerInstruments;

            if (playerNames != null && playerNames.Length > 0)
            {
                for (int i = 0; i < playerNames.Length; i++)
                {
                    string normalizedName = SanitizePlayerName(playerNames[i], i);
                    bool isHost = !string.IsNullOrEmpty(hostName) && string.Equals(normalizedName, hostName, StringComparison.OrdinalIgnoreCase);
                    string displayName = isHost ? AppendHostSuffix(normalizedName) : normalizedName;

                    string instrumentMarkup = string.Empty;
                    if (instruments != null && i < instruments.Length)
                    {
                        instrumentMarkup = FormatInstrumentMarkup((Instrument)instruments[i]);
                    }

                    entries.Add(new LobbyPlayerEntry(displayName, instrumentMarkup));
                    seenNames.Add(normalizedName);
                }
            }

            // For dedicated servers, don't add a phantom host entry - there's no actual host player
            // Only add/promote host entry for regular lobbies
            if (!string.IsNullOrEmpty(hostName) && !lobby.IsDedicatedServer)
            {
                int hostIndex = entries.FindIndex(e => string.Equals(RemoveHostSuffix(e.DisplayName), hostName, StringComparison.OrdinalIgnoreCase));
                if (hostIndex >= 0)
                {
                    var hostEntry = entries[hostIndex];
                    var normalized = RemoveHostSuffix(hostEntry.DisplayName);
                    var updatedHost = new LobbyPlayerEntry(AppendHostSuffix(normalized), hostEntry.InstrumentMarkup);
                    entries.RemoveAt(hostIndex);
                    entries.Insert(0, updatedHost);
                }
                else if (!seenNames.Contains(hostName))
                {
                    entries.Insert(0, new LobbyPlayerEntry(AppendHostSuffix(hostName), string.Empty));
                    seenNames.Add(hostName);
                }
            }

            if (entries.Count == 0 && lobby.CurrentPlayers > 0)
            {
                for (int i = 0; i < lobby.CurrentPlayers; i++)
                {
                    string baseName = i == 0 && !string.IsNullOrEmpty(hostName)
                        ? hostName
                        : ZString.Format("Player {0}", i + 1);

                    if (!seenNames.Add(baseName))
                        continue;

                    bool isHost = !string.IsNullOrEmpty(hostName) && string.Equals(baseName, hostName, StringComparison.OrdinalIgnoreCase);
                    string displayName = isHost ? AppendHostSuffix(baseName) : baseName;
                    entries.Add(new LobbyPlayerEntry(displayName, string.Empty));
                }
            }

            return entries;
        }

        private void AddPlayerListEntry(LobbyPlayerEntry entry)
        {
            if (_playerEntryPrefab == null || _playerListContainer == null)
                return;

            var instance = Instantiate(_playerEntryPrefab, _playerListContainer);

            var entryComponent = instance.GetComponent<MultiplayerPlayerEntry>();
            if (entryComponent != null)
            {
                entryComponent.SetPlayer(entry.DisplayName ?? string.Empty, entry.InstrumentMarkup ?? string.Empty);
                return;
            }

            var texts = instance.GetComponentsInChildren<TextMeshProUGUI>(true);
            foreach (var text in texts)
            {
                if (text == null)
                    continue;

                string componentName = text.gameObject.name;
                if (string.Equals(componentName, "Player Name", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(componentName, "PlayerName", StringComparison.OrdinalIgnoreCase))
                {
                    text.text = entry.DisplayName ?? string.Empty;
                }
                else if (string.Equals(componentName, "Instrument", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(componentName, "Icons", StringComparison.OrdinalIgnoreCase))
                {
                    text.text = entry.InstrumentMarkup ?? string.Empty;
                }
            }
        }

        private static string SanitizePlayerName(string name, int fallbackIndex)
        {
            if (!string.IsNullOrWhiteSpace(name))
                return name.Trim();

            return ZString.Format("Player {0}", fallbackIndex + 1);
        }

        private static string AppendHostSuffix(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "Host";

            const string suffix = " (Host)";
            return name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? name : name + suffix;
        }

        private static string RemoveHostSuffix(string name)
        {
            if (string.IsNullOrEmpty(name))
                return string.Empty;

            const string suffix = " (Host)";
            return name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - suffix.Length).TrimEnd()
                : name;
        }

        private static string FormatInstrumentMarkup(Instrument instrument)
        {
            string resourceName = instrument.ToResourceName();
            return string.IsNullOrEmpty(resourceName)
                ? string.Empty
                : ZString.Format("<sprite name=\"{0}\">", resourceName);
        }

        private void ClearPlayerList()
        {
            if (_playerListContainer != null)
            {
                foreach (Transform child in _playerListContainer)
                {
                    Destroy(child.gameObject);
                }
            }

            if (_noPlayersText != null)
            {
                _noPlayersText.gameObject.SetActive(false);
            }
        }

        private string BuildEndpoint(string address, int port, string fallbackAddress, int fallbackPort)
        {
            string selectedAddress = !string.IsNullOrWhiteSpace(address) ? address : fallbackAddress;
            int selectedPort = port > 0 ? port : (fallbackPort > 0 ? fallbackPort : GetSuggestedPort());
            if (string.IsNullOrWhiteSpace(selectedAddress))
                return string.Empty;
            return EndpointUtility.FormatEndpoint(selectedAddress, selectedPort);
        }

        private void SetHostAddress(string address, bool allowToggle)
        {
            string newAddress = address ?? string.Empty;
            bool hasAddress = !string.IsNullOrEmpty(newAddress);
            bool newToggleAvailable = hasAddress && allowToggle;

            bool addressChanged = !string.Equals(_currentHostAddress, newAddress, StringComparison.Ordinal);
            bool availabilityChanged = _hostToggleAvailable != newToggleAvailable;

            _currentHostAddress = newAddress;
            _hostToggleAvailable = newToggleAvailable;

            if (addressChanged || availabilityChanged)
            {
                _isHostAddressVisible = false;
            }

            if (_hostVisibilityToggle != null)
                _hostVisibilityToggle.gameObject.SetActive(_hostToggleAvailable);

            ApplyHostVisibility();
        }

        private void ApplyHostVisibility()
        {
            if (_hostNameText == null)
                return;

            if (string.IsNullOrEmpty(_currentHostAddress))
            {
                _hostNameText.text = "Unavailable";
            }
            else if (_hostToggleAvailable)
            {
                _hostNameText.text = _isHostAddressVisible ? _currentHostAddress : "****";
            }
            else
            {
                _hostNameText.text = _currentHostAddress;
            }

            UpdateHostVisibilityIcons();
        }

        private void UpdateHostVisibilityIcons()
        {
            var image = _hostVisibilityToggle != null ? _hostVisibilityToggle.image : null;
            if (image == null)
                return;

            if (_hostToggleAvailable)
            {
                image.enabled = true;
                if (_isHostAddressVisible && _hostVisibleSprite != null)
                    image.sprite = _hostVisibleSprite;
                else if (!_isHostAddressVisible && _hostHiddenSprite != null)
                    image.sprite = _hostHiddenSprite;
            }
            else
            {
                if (_hostHiddenSprite != null)
                {
                    image.enabled = true;
                    image.sprite = _hostHiddenSprite;
                }
                else if (_hostVisibleSprite != null)
                {
                    image.enabled = true;
                    image.sprite = _hostVisibleSprite;
                }
                else
                {
                    image.enabled = false;
                }
            }
        }

        private void SetPasswordValue(string password, bool hasPassword, bool allowToggle)
        {
            string previousPassword = _currentPassword;
            bool previousHasPassword = _hasPassword;
            bool previousToggleAvailable = _passwordToggleAvailable;

            string sanitizedPassword;
            if (!hasPassword)
            {
                sanitizedPassword = string.Empty;
            }
            else if (!string.IsNullOrEmpty(password))
            {
                sanitizedPassword = password;
            }
            else if (!string.IsNullOrEmpty(previousPassword))
            {
                sanitizedPassword = previousPassword;
            }
            else if (_activeBookmark != null && !string.IsNullOrEmpty(_activeBookmark.password))
            {
                sanitizedPassword = _activeBookmark.password;
            }
            else
            {
                sanitizedPassword = string.Empty;
            }

            bool passwordChanged = !string.Equals(previousPassword, sanitizedPassword, StringComparison.Ordinal);
            bool hasPasswordChanged = previousHasPassword != hasPassword;

            bool retainFromLocal = hasPassword && string.IsNullOrEmpty(password) && !string.IsNullOrEmpty(previousPassword);
            bool newToggleAvailable = hasPassword && (allowToggle || retainFromLocal || !string.IsNullOrEmpty(sanitizedPassword));
            bool toggleAvailabilityChanged = previousToggleAvailable != newToggleAvailable;

            _hasPassword = hasPassword;
            _currentPassword = sanitizedPassword;
            _passwordToggleAvailable = newToggleAvailable;

            if (!newToggleAvailable || passwordChanged || hasPasswordChanged || (toggleAvailabilityChanged && newToggleAvailable))
            {
                _isPasswordVisible = false;
            }

            ApplyPasswordVisibility();
        }

        private void ApplyPasswordVisibility()
        {
            if (_passwordValueText != null)
            {
                if (!_hasPassword)
                {
                    _passwordValueText.text = "None";
                }
                else if (_passwordToggleAvailable)
                {
                    _passwordValueText.text = _isPasswordVisible ? _currentPassword : "****";
                }
                else
                {
                    _passwordValueText.text = "****";
                }
            }

            UpdatePasswordVisibilityIcons();
            UpdatePasswordContainersVisibility();
        }

        private void UpdatePasswordVisibilityIcons()
        {
            var image = _passwordVisibilityToggle != null ? _passwordVisibilityToggle.image : null;
            if (image == null)
                return;

            if (_passwordToggleAvailable)
            {
                image.enabled = true;
                if (_isPasswordVisible && _passwordVisibleSprite != null)
                    image.sprite = _passwordVisibleSprite;
                else if (!_isPasswordVisible && _passwordHiddenSprite != null)
                    image.sprite = _passwordHiddenSprite;
            }
            else
            {
                if (_passwordHiddenSprite != null)
                {
                    image.enabled = true;
                    image.sprite = _passwordHiddenSprite;
                }
                else if (_passwordVisibleSprite != null)
                {
                    image.enabled = true;
                    image.sprite = _passwordVisibleSprite;
                }
                else
                {
                    image.enabled = false;
                }
            }
        }

        private void ToggleHostVisibility()
        {
            if (!_hostToggleAvailable)
                return;

            _isHostAddressVisible = !_isHostAddressVisible;
            ApplyHostVisibility();
        }

        private void TogglePasswordVisibility()
        {
            if (!_passwordToggleAvailable)
                return;

            _isPasswordVisible = !_isPasswordVisible;
            ApplyPasswordVisibility();
        }

        #endregion

        #region Hosted Preset Editing

        /// <summary>
        /// Password visibility is now handled by SessionSettingsPanelBuilder internally.
        /// </summary>
        private void UpdateHostedLobbyPasswordVisibility()
        {
            // SessionSettingsPanelBuilder handles password visibility based on privacy mode
        }

        /// <summary>
        /// Legacy method - now handled by SessionSettingsPanelBuilder.
        /// Kept as stub for any remaining call sites.
        /// </summary>
        private void ApplyHostedPresetToFields(HostedLobbyPreset preset)
        {
            // Now handled by ApplyHostedLobbyToSettingsPanel via SessionSettingsPanelBuilder
            ApplyHostedLobbyToSettingsPanel();
        }

        /// <summary>
        /// Resets the hosted form to default values via SessionSettingsPanelBuilder.
        /// </summary>
        private void ResetHostedForm()
        {
            if (_hostedLobbySettingsPanel != null)
            {
                _hostedLobbySettingsPanel.SetData(new SessionSettingsData());
            }
        }

        private void CommitHostedPreset(
            string lobbyName = null, 
            int? maxPlayers = null, 
            LobbyPrivacyMode? privacyMode = null, 
            SessionType? sessionType = null,
            string password = null,
            int? bandSize = null,
            bool? noFailMode = null,
            bool? sharedSongsOnly = null,
            bool? allowModifiers = null,
            bool? enablePresetSync = null,
            bool? allowLateJoin = null,
            List<int> allowedInstruments = null,
            bool? localPlayersFirst = null)
        {
            Debug.Log($"[LobbyBrowserSidebar] CommitHostedPreset: sessionType param={sessionType}, _activePreset.SessionType={_activePreset?.SessionType}");
            if (_activePreset == null)
                return;

            string newName = lobbyName ?? (_activePreset.lobbyName ?? string.Empty);
            int newMaxPlayers = Mathf.Clamp(maxPlayers ?? _activePreset.maxPlayers, 2, 32);
            var newPrivacy = privacyMode ?? _activePreset.PrivacyMode;
            var newSessionType = sessionType ?? _activePreset.SessionType;
            Debug.Log($"[LobbyBrowserSidebar] CommitHostedPreset: newSessionType={newSessionType}");
            string newPassword = password ?? (_activePreset.password ?? string.Empty);
            int newBandSize = Mathf.Clamp(bandSize ?? _activePreset.bandSize, 0, 8);
            bool newNoFailMode = noFailMode ?? _activePreset.noFailMode;
            bool newSharedSongsOnly = sharedSongsOnly ?? _activePreset.sharedSongsOnly;
            bool newAllowModifiers = allowModifiers ?? _activePreset.allowModifiers;
            bool newEnablePresetSync = enablePresetSync ?? _activePreset.enablePresetSync;
            bool newAllowLateJoin = allowLateJoin ?? _activePreset.allowLateJoin;
            var newAllowedInstruments = allowedInstruments ?? _activePreset.allowedInstruments ?? new List<int>();
            bool newLocalPlayersFirst = localPlayersFirst ?? _activePreset.localPlayersFirst;

            // Password rules based on privacy and session type
            bool canHavePassword = newPrivacy == LobbyPrivacyMode.Private ||
                                   (newPrivacy == LobbyPrivacyMode.Unlisted && newSessionType == SessionType.Server);
            if (!canHavePassword)
            {
                newPassword = string.Empty;
            }

            bool changed =
                !string.Equals(_activePreset.lobbyName ?? string.Empty, newName, StringComparison.Ordinal) ||
                _activePreset.maxPlayers != newMaxPlayers ||
                _activePreset.PrivacyMode != newPrivacy ||
                _activePreset.SessionType != newSessionType ||
                !string.Equals(_activePreset.password ?? string.Empty, newPassword ?? string.Empty, StringComparison.Ordinal) ||
                _activePreset.bandSize != newBandSize ||
                _activePreset.noFailMode != newNoFailMode ||
                _activePreset.sharedSongsOnly != newSharedSongsOnly ||
                _activePreset.allowModifiers != newAllowModifiers ||
                _activePreset.enablePresetSync != newEnablePresetSync ||
                _activePreset.allowLateJoin != newAllowLateJoin ||
                !InstrumentListsEqual(_activePreset.allowedInstruments, newAllowedInstruments) ||
                _activePreset.localPlayersFirst != newLocalPlayersFirst;

            if (!changed)
                return;

            try
            {
                var updated = LobbyBookmarkStore.Instance.UpsertMyLobby(
                    _activePreset.id,
                    newName,
                    newMaxPlayers,
                    newPrivacy,
                    newSessionType,
                    newPassword ?? string.Empty,
                    updateHostedTimestamp: false,
                    newBandSize,
                    newNoFailMode,
                    newSharedSongsOnly,
                    newAllowModifiers,
                    newEnablePresetSync,
                    newAllowLateJoin,
                    newAllowedInstruments,
                    newLocalPlayersFirst);

                _activePreset = updated?.Clone();
                ApplyHostedPresetToFields(_activePreset);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LobbyBrowserSidebar] Failed to save hosted lobby preset '{_activePreset?.id}': {ex}");
            }
        }
        
        private static bool InstrumentListsEqual(List<int> a, List<int> b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        private void CommitHostedPresetFromFields()
        {
            if (_activePreset == null)
                return;

            // Use SessionSettingsPanelBuilder for consistent UX
            var data = GetHostedLobbyDataFromPanel();
            if (data != null)
            {
                CommitHostedPreset(
                    data.LobbyName, 
                    data.MaxPlayers, 
                    data.PrivacyMode,
                    data.SessionType, 
                    data.Password, 
                    data.BandSize, 
                    data.NoFailMode, 
                    data.SharedSongsOnly, 
                    data.AllowModifiers, 
                    data.EnablePresetSync, 
                    data.AllowLateJoin,
                    data.AllowedGameModes?.Select(g => (int)g).ToList(),
                    data.LocalPlayersFirst);
            }
        }

        private void HandleHostedHost()
        {
            Debug.Log($"[LobbyBrowserSidebar] HandleHostedHost: BEFORE commit - _activePreset.SessionType={_activePreset?.SessionType}");
            CommitHostedPresetFromFields();
            Debug.Log($"[LobbyBrowserSidebar] HandleHostedHost: AFTER commit - _activePreset.SessionType={_activePreset?.SessionType}");

            if (_activePreset != null)
            {
                _menu?.StartHostedLobby(_activePreset);
            }
        }

        private void HandleHostedDelete()
        {
            if (_activePreset == null)
                return;

            var preset = _activePreset;
            string presetName = string.IsNullOrWhiteSpace(preset.lobbyName) ? "My Lobby" : preset.lobbyName;
            const string confirmText = "DELETE";

            void PerformDelete()
            {
                bool removed = LobbyBookmarkStore.Instance.RemoveMyLobby(preset.id);
                if (!removed)
                    return;

                ToastManager.ToastInformation(ZString.Format("Deleted \"{0}\".", presetName));
                ClearLobby();
            }

            if (DialogManager.Instance != null)
            {
                DialogManager.Instance.ShowConfirmDeleteDialog(presetName, PerformDelete, confirmText);
            }
            else
            {
                PerformDelete();
            }
        }

        #endregion

        #region Editable Fields

        // Compatibility wrappers for existing prefab bindings
        public void BeginLobbyNameEdit() => SetLobbyNameEditMode(true);
        public void ConfirmLobbyNameEdit() => SetLobbyNameEditMode(false);
        public void CancelLobbyNameEdit() => CancelEditableFieldInternal(EditableField.LobbyName);

        public void BeginHostAddressEdit() => SetHostAddressEditMode(true);
        public void ConfirmHostAddressEdit() => SetHostAddressEditMode(false);
        public void CancelHostAddressEdit() => CancelEditableFieldInternal(EditableField.HostAddress);

        public void BeginPasswordEdit() => SetPasswordEditMode(true);
        public void ConfirmPasswordEdit() => SetPasswordEditMode(false);
        public void CancelPasswordEdit() => CancelEditableFieldInternal(EditableField.Password);

        public void SetLobbyNameEditMode(bool editing) => SetEditableMode(EditableField.LobbyName, editing);
        public void SetHostAddressEditMode(bool editing) => SetEditableMode(EditableField.HostAddress, editing);
        public void SetPasswordEditMode(bool editing) => SetEditableMode(EditableField.Password, editing);

        private void SetEditableMode(EditableField field, bool editing)
        {
            if (!CanEditSelectedBookmark())
            {
                ExitAllEditModes();
                return;
            }

            if (field == EditableField.Password)
            {
                EnsurePasswordEditInputField();
            }

            var state = GetEditableFieldState(field);
            if (state.ViewContainer == null || state.EditContainer == null)
            {
                ExitAllEditModes();
                return;
            }

            if (editing)
            {
                if (_activeEditField == field)
                    return;

                ExitAllEditModes();
                _activeEditField = field;

                if (state.EditContainer != null)
                    EnsureContainerHierarchyActive(state.EditContainer);

                if (state.ViewContainer != null)
                    EnsureContainerHierarchyActive(state.ViewContainer);

                SetEditContainers(state.ViewContainer, state.EditContainer, true);

                var input = state.GetInputField();
                if (input != null)
                {
                    input.interactable = true;
                    input.readOnly = false;

                    string initialValue = GetCurrentEditableValue(field);
                    SetInputFieldText(input, initialValue);
                }

                UpdatePasswordContainersVisibility();

                if (input != null)
                {
                    FocusInput(input);
                    MoveCaretToEnd(input);
                }
                return;
            }

            if (_activeEditField != field)
            {
                ExitAllEditModes();
                return;
            }

            if (!TryCommitEditableField(field, state.GetInputField()))
            {
                var input = state.GetInputField();
                if (input != null)
                {
                    input.caretPosition = input.text.Length;
                    FocusInput(input);
                }
                return;
            }

            ExitAllEditModes();

            if (_activeBookmark != null)
            {
                PopulateBookmarkInfo(_activeBookmark);
            }

            UpdatePasswordContainersVisibility();
        }

        public bool IsEditingBookmark(LobbyBookmark bookmark)
        {
            if (!IsEditing || bookmark == null || _activeBookmark == null)
                return false;

            return string.Equals(_activeBookmark.EndpointKey, bookmark.EndpointKey, StringComparison.OrdinalIgnoreCase);
        }

        private void CancelEditableFieldInternal(EditableField field)
        {
            if (_activeEditField == field)
            {
                ExitAllEditModes();
            }
        }

        private bool TryCommitEditableField(EditableField field, TMP_InputField input)
        {
            var bookmark = _activeBookmark;
            if (bookmark == null)
                return false;

            switch (field)
            {
                case EditableField.LobbyName:
                    if (input == null)
                        return false;

                    string displayName = input.text?.Trim() ?? string.Empty;
                    LobbyBookmarkStore.Instance.UpdateBookmark(bookmark, displayName, bookmark.address, bookmark.port, bookmark.password);
                    ToastManager.ToastInformation("Bookmark name saved.");
                    return true;

                case EditableField.HostAddress:
                    if (input == null)
                        return false;

                    string submitted = input.text?.Trim() ?? string.Empty;
                    int fallbackPort = bookmark.port > 0 ? bookmark.port : GetSuggestedPort();

                    if (!EndpointUtility.TryParseEndpoint(submitted, fallbackPort, out string address, out int port, out string error))
                    {
                        ToastManager.ToastError(string.IsNullOrEmpty(error) ? "Enter a valid address." : error);
                        return false;
                    }

                    LobbyBookmarkStore.Instance.UpdateBookmark(bookmark, bookmark.displayName, address, port, bookmark.password);
                    ToastManager.ToastInformation("Address saved.");
                    return true;

                case EditableField.Password:
                    if (input == null)
                        return false;

                    string password = input.text ?? string.Empty;
                    LobbyBookmarkStore.Instance.UpdateBookmark(bookmark, bookmark.displayName, bookmark.address, bookmark.port, password);
                    ToastManager.ToastInformation("Password saved.");
                    return true;

                default:
                    return false;
            }
        }

        private void ExitAllEditModes()
        {
            _activeEditField = null;
            SetEditContainers(_lobbyNameViewContainer, _lobbyNameEditContainer, false);
            SetEditContainers(_hostAddressViewContainer, _hostAddressEditContainer, false);
            SetEditContainers(_passwordViewContainer, _passwordEditContainer, false);
            ResetEditableInputs();
            UpdatePasswordContainersVisibility();
        }

        private void ResetEditableInputs()
        {
            SetInputFieldValue(_lobbyNameEditContainer, EditableField.LobbyName);
            SetInputFieldValue(_hostAddressEditContainer, EditableField.HostAddress);
            SetInputFieldValue(_passwordEditContainer, EditableField.Password);
        }

        private void SetInputFieldValue(GameObject editContainer, EditableField field)
        {
            if (editContainer == null)
                return;

            TMP_InputField input = editContainer == _passwordEditContainer
                ? EnsurePasswordEditInputField()
                : editContainer.GetComponentInChildren<TMP_InputField>(true);
            if (input == null)
                return;

            SetInputFieldText(input, GetCurrentEditableValue(field));
        }

        private void RefreshEditableButtons()
        {
            bool canEdit = CanEditSelectedBookmark();

            SetButtonState(_lobbyNameEditButton, canEdit);
            SetButtonState(_hostAddressEditButton, canEdit);
            SetButtonState(_passwordEditButton, canEdit);
            UpdatePasswordContainersVisibility();
        }

        private bool CanEditSelectedBookmark()
        {
            return _currentMode == SidebarMode.Lobby && _activeBookmark != null;
        }

        private static void SetButtonState(Button button, bool enabled)
        {
            if (button == null)
                return;

            button.interactable = enabled;
            button.gameObject.SetActive(enabled);
        }

        private static void SetEditContainers(GameObject viewContainer, GameObject editContainer, bool editing)
        {
            if (viewContainer != null)
                viewContainer.SetActive(!editing);

            if (editContainer != null)
                editContainer.SetActive(editing);
        }

        private string GetCurrentEditableValue(EditableField field)
        {
            var bookmark = _activeBookmark;
            if (bookmark == null)
                return string.Empty;

            return field switch
            {
                EditableField.LobbyName => bookmark.displayName ?? string.Empty,
                EditableField.HostAddress => FormatBookmarkEndpoint(bookmark),
                EditableField.Password => GetPasswordEditableValue(bookmark),
                _ => string.Empty
            };
        }

        private string GetPasswordEditableValue(LobbyBookmark bookmark)
        {
            if (!string.IsNullOrEmpty(_currentPassword))
                return _currentPassword;

            if (bookmark != null && !string.IsNullOrEmpty(bookmark.password))
                return bookmark.password;

            return string.Empty;
        }

        private string FormatBookmarkEndpoint(LobbyBookmark bookmark)
        {
            if (bookmark == null)
                return string.Empty;

            int port = bookmark.port > 0 ? bookmark.port : GetSuggestedPort();
            return EndpointUtility.FormatEndpoint(bookmark.address ?? string.Empty, port);
        }

        private EditableFieldState GetEditableFieldState(EditableField field)
        {
            return field switch
            {
                EditableField.LobbyName => new EditableFieldState(_lobbyNameViewContainer, _lobbyNameEditContainer),
                EditableField.HostAddress => new EditableFieldState(_hostAddressViewContainer, _hostAddressEditContainer),
                EditableField.Password => new EditableFieldState(_passwordViewContainer, _passwordEditContainer, EnsurePasswordEditInputField()),
                _ => default
            };
        }

        private readonly struct EditableFieldState
        {
            public readonly GameObject ViewContainer;
            public readonly GameObject EditContainer;
            private readonly TMP_InputField _explicitInput;

            public EditableFieldState(GameObject viewContainer, GameObject editContainer, TMP_InputField explicitInput = null)
            {
                ViewContainer = viewContainer;
                EditContainer = editContainer;
                _explicitInput = explicitInput;
            }

            public TMP_InputField GetInputField()
            {
                if (_explicitInput != null)
                    return _explicitInput;

                if (EditContainer == null)
                    return null;

                return EditContainer.GetComponentInChildren<TMP_InputField>(true);
            }
        }

        private TMP_InputField EnsurePasswordEditInputField()
        {
            if (_passwordEditInputField != null)
                return _passwordEditInputField;

            if (_passwordEditContainer == null)
                return null;

            _passwordEditInputField = _passwordEditContainer.GetComponentInChildren<TMP_InputField>(true);
            if (_passwordEditInputField == null)
            {
                Debug.LogWarning("[LobbyBrowserSidebar] Password edit container is missing a TMP_InputField. Please assign it in the prefab.");
            }

            return _passwordEditInputField;
        }

        private void UpdatePasswordContainersVisibility()
        {
            bool editing = _activeEditField == EditableField.Password;
            if (editing)
            {
                if (_passwordViewContainer != null)
                    _passwordViewContainer.SetActive(false);

                if (_passwordEditContainer != null)
                    _passwordEditContainer.SetActive(true);

                if (_passwordEditButton != null)
                    _passwordEditButton.gameObject.SetActive(false);

                if (_passwordVisibilityToggle != null)
                {
                    _passwordVisibilityToggle.gameObject.SetActive(false);
                    _passwordVisibilityToggle.interactable = false;
                }

                return;
            }

            bool showRow = false;

            if (_currentMode == SidebarMode.Lobby)
            {
                if (_currentPrivacyMode == LobbyPrivacyMode.Private)
                {
                    showRow = true;
                }
                else if (_hasPassword || !string.IsNullOrEmpty(_currentPassword))
                {
                    showRow = true;
                }
                else if (_activeBookmark != null && !string.IsNullOrEmpty(_activeBookmark.password))
                {
                    showRow = true;
                }
            }

            if (_passwordViewContainer != null)
                _passwordViewContainer.SetActive(showRow);

            if (_passwordEditContainer != null)
                _passwordEditContainer.SetActive(false);

            if (_passwordEditButton != null)
            {
                bool canEditBookmark = CanEditSelectedBookmark();
                _passwordEditButton.gameObject.SetActive(showRow && canEditBookmark);
                _passwordEditButton.interactable = canEditBookmark;
            }

            if (_passwordVisibilityToggle != null)
            {
                bool toggleVisible = showRow && _hasPassword;
                _passwordVisibilityToggle.gameObject.SetActive(toggleVisible);
                _passwordVisibilityToggle.interactable = toggleVisible && _passwordToggleAvailable;
            }
        }

        #endregion

        #region View Helpers

        private void ShowMode(SidebarMode mode)
        {
            EnsureContainerReferences();

            _currentMode = mode;

            if (_emptyStateContainer != null)
                _emptyStateContainer.SetActive(mode == SidebarMode.Empty);

            bool hostedMode = mode == SidebarMode.HostedLobby;
            bool hostedInsideContent = hostedMode &&
                                        _hostedLobbyContainer != null &&
                                        _contentContainer != null &&
                                        _hostedLobbyContainer.transform.IsChildOf(_contentContainer.transform);

            if (_contentContainer != null)
                _contentContainer.SetActive(mode == SidebarMode.Lobby || hostedInsideContent);

            if (_createLobbyContainer != null)
                _createLobbyContainer.SetActive(mode == SidebarMode.CreateLobby);

            if (_hostedLobbyContainer != null)
            {
                _hostedLobbyContainer.SetActive(hostedMode);
                if (hostedMode)
                    EnsureContainerHierarchyActive(_hostedLobbyContainer);
            }

            if (_directConnectContainer != null)
                _directConnectContainer.SetActive(mode == SidebarMode.DirectConnect);

            if (mode == SidebarMode.CreateLobby && _createLobbyContainer != null)
                EnsureContainerHierarchyActive(_createLobbyContainer);

            if (mode == SidebarMode.DirectConnect && _directConnectContainer != null)
                EnsureContainerHierarchyActive(_directConnectContainer);

            UpdatePasswordContainersVisibility();
            UpdateHostedLobbyPasswordVisibility();
        }

        private void EnsureContainerHierarchyActive(GameObject container)
        {
            if (container == null)
                return;

            var current = container.transform;
            while (current != null && current != transform)
            {
                var go = current.gameObject;
                if (!go.activeSelf)
                {
                    go.SetActive(true);
                }

                current = current.parent;
            }
        }

        private void EnsureContainerReferences()
        {
            if (_hostedLobbyContainer == null && !_attemptedHostedContainerResolve)
            {
                _attemptedHostedContainerResolve = true;
                var resolved = TryResolveHostedContainer();
                if (resolved != null)
                {
                    _hostedLobbyContainer = resolved;
                    Debug.LogWarning("[LobbyBrowserSidebar] Hosted lobby container reference was missing; auto-assigned at runtime. Please assign it in the prefab to avoid this lookup.");
                }
                else
                {
                    Debug.LogWarning("[LobbyBrowserSidebar] Hosted lobby container reference is missing and could not be auto-resolved. Hosted presets may remain hidden.");
                }
            }
        }

        private GameObject TryResolveHostedContainer()
        {
            var markers = new List<Transform>(4);

            // Use the SessionSettingsPanelBuilder and buttons as markers
            if (_hostedLobbySettingsPanel != null)
                markers.Add(_hostedLobbySettingsPanel.transform);
            if (_hostedLobbyHostButton != null)
                markers.Add(_hostedLobbyHostButton.transform);
            if (_hostedLobbyDeleteButton != null)
                markers.Add(_hostedLobbyDeleteButton.transform);

            markers.RemoveAll(t => t == null);
            if (markers.Count < 2)
                return null;

            var candidate = FindCommonAncestorWithinSidebar(markers);
            if (candidate != null && candidate != transform)
                return candidate.gameObject;

            return null;
        }

        private Transform FindCommonAncestorWithinSidebar(IReadOnlyList<Transform> nodes)
        {
            if (nodes == null || nodes.Count == 0)
                return null;

            var baseChain = BuildAncestorChain(nodes[0]);
            foreach (var candidate in baseChain)
            {
                if (candidate == null || candidate == transform)
                    continue;

                bool containsAll = true;
                for (int i = 1; i < nodes.Count; i++)
                {
                    var other = nodes[i];
                    if (other == null || !IsDescendantOf(other, candidate))
                    {
                        containsAll = false;
                        break;
                    }
                }

                if (containsAll)
                    return candidate;
            }

            return null;
        }

        private List<Transform> BuildAncestorChain(Transform start)
        {
            var chain = new List<Transform>();
            var current = start;
            while (current != null)
            {
                chain.Add(current);
                if (current == transform)
                    break;
                current = current.parent;
            }

            return chain;
        }

        private static bool IsDescendantOf(Transform node, Transform potentialAncestor)
        {
            var current = node;
            while (current != null)
            {
                if (current == potentialAncestor)
                    return true;

                current = current.parent;
            }

            return false;
        }

        #endregion

        #region Form Submission

        private void SubmitCreateLobbyForm()
        {
            // Use SessionSettingsPanelBuilder for consistent UX
            var formData = GetCreateLobbyFormDataFromPanel();
            if (formData.HasValue)
            {
                var data = formData.Value;
                ToastManager.ToastInformation(ZString.Format("Hosting {0}...", data.LobbyName));
                CreateLobbySubmitted?.Invoke(data);
            }
        }

        private static int GetBandSizeFromInput(TMP_InputField input)
        {
            if (input == null)
                return 0;
            
            if (int.TryParse(input.text, out int bandSize))
                return Mathf.Clamp(bandSize, 0, 8);
            
            return 0;
        }

        /// <summary>
        /// Smart connect handler - chooses between lobby code join and direct IP connect
        /// based on which input field has content.
        /// </summary>
        private void SubmitConnectForm()
        {
            string lobbyCode = _lobbyCodeInput != null ? _lobbyCodeInput.text?.Trim().ToUpperInvariant() : string.Empty;
            string endpointInput = _directConnectAddressInput != null ? _directConnectAddressInput.text?.Trim() : string.Empty;
            
            // Priority: Lobby code first (if it has content), then direct IP
            if (!string.IsNullOrEmpty(lobbyCode))
            {
                // User entered a lobby code - use that
                SubmitLobbyCode();
            }
            else if (!string.IsNullOrEmpty(endpointInput))
            {
                // User entered an IP/address - use direct connect
                SubmitDirectConnectForm();
            }
            else
            {
                // Neither field has content - show error
                ToastManager.ToastError("Enter a lobby code or IP address to connect.");
                FocusInput(_lobbyCodeInput ?? _directConnectAddressInput);
            }
        }
        
        private void SubmitDirectConnectForm()
        {
            string endpointInput = _directConnectAddressInput != null ? _directConnectAddressInput.text : string.Empty;
            if (!EndpointUtility.TryParseEndpoint(endpointInput, GetSuggestedPort(), out string address, out int port, out string error))
            {
                ToastManager.ToastError(string.IsNullOrEmpty(error) ? "Enter a valid address." : error);
                FocusInput(_directConnectAddressInput);
                return;
            }

            string displayName = string.Empty;
            string password = _directConnectPasswordInput != null ? _directConnectPasswordInput.text : string.Empty;

            var form = new DirectConnectFormData(address, port, displayName, password);
            ToastManager.ToastInformation(ZString.Format("Connecting to {0}...", EndpointUtility.FormatEndpoint(address, port)));
            DirectConnectSubmitted?.Invoke(form);
        }
        
        private void SubmitLobbyCode()
        {
            string code = _lobbyCodeInput != null ? _lobbyCodeInput.text?.Trim().ToUpperInvariant() : string.Empty;
            
            if (string.IsNullOrEmpty(code))
            {
                ToastManager.ToastError("Enter a lobby code");
                FocusInput(_lobbyCodeInput);
                return;
            }
            
            if (code.Length != 6)
            {
                ToastManager.ToastError("Code must be 6 characters");
                FocusInput(_lobbyCodeInput);
                return;
            }
            
            ToastManager.ToastInformation(ZString.Format("Looking up lobby code {0}...", code));
            JoinByCodeSubmitted?.Invoke(code);
        }
        
        /// <summary>
        /// Clears the lobby code input field.
        /// </summary>
        public void ClearLobbyCodeInput()
        {
            if (_lobbyCodeInput != null)
            {
                SetInputFieldText(_lobbyCodeInput, string.Empty);
            }
        }

        private static void SetInputFieldText(TMP_InputField field, string value)
        {
            if (field == null)
                return;

            string sanitized = value ?? string.Empty;
            string current = field.text ?? string.Empty;
            bool changed = !string.Equals(current, sanitized, StringComparison.Ordinal);

            int caret = -1;
            int anchor = -1;
            int focus = -1;

            if (field.isFocused)
            {
                caret = field.caretPosition;
                anchor = field.selectionAnchorPosition;
                focus = field.selectionFocusPosition;
            }

            if (changed)
            {
                field.SetTextWithoutNotify(sanitized);
            }

            field.ForceLabelUpdate();

            if (caret < 0)
                return;

            int length = field.text?.Length ?? sanitized.Length;
            caret = Mathf.Clamp(caret, 0, length);
            anchor = Mathf.Clamp(anchor, 0, length);
            focus = Mathf.Clamp(focus, 0, length);

            field.caretPosition = caret;
            field.selectionAnchorPosition = anchor;
            field.selectionFocusPosition = focus;
        }

        private static void FocusInput(TMP_InputField field)
        {
            if (field == null)
                return;

            var eventSystem = EventSystem.current;
            if (eventSystem != null)
            {
                if (eventSystem.currentSelectedGameObject != field.gameObject)
                {
                    eventSystem.SetSelectedGameObject(null);
                    eventSystem.SetSelectedGameObject(field.gameObject);
                }
                else
                {
                    field.OnSelect(new BaseEventData(eventSystem));
                }
            }

            field.Select();
            field.ActivateInputField();
        }

        private static void MoveCaretToEnd(TMP_InputField field)
        {
            if (field == null)
                return;

            field.MoveTextEnd(false);

            int length = field.text?.Length ?? 0;
            field.caretPosition = length;
            field.selectionAnchorPosition = length;
            field.selectionFocusPosition = length;
        }

        private static void EnsureMaxPlayersDropdownOptions(TMP_Dropdown dropdown)
        {
            if (dropdown == null)
                return;

            if (dropdown.options == null || dropdown.options.Count == 0)
            {
                var fallbackOptions = new List<string>();
                for (int i = 2; i <= 32; i++)
                {
                    fallbackOptions.Add(i.ToString());
                }

                dropdown.ClearOptions();
                dropdown.AddOptions(fallbackOptions);
            }
        }

        private static void EnsurePrivacyDropdownOptions(TMP_Dropdown dropdown)
        {
            if (dropdown == null)
                return;

            if (dropdown.options == null || dropdown.options.Count < 3)
            {
                dropdown.ClearOptions();
                dropdown.AddOptions(new List<string>
                {
                    "Public",
                    "Private (Password)",
                    "Unlisted (Direct Connect Only)"
                });
            }
        }

        private static bool HostedPresetsEquivalent(HostedLobbyPreset currentPreset, HostedLobbyPreset targetPreset)
        {
            if (ReferenceEquals(currentPreset, targetPreset))
                return true;

            if (currentPreset == null || targetPreset == null)
                return currentPreset == null && targetPreset == null;

            return string.Equals(currentPreset.id ?? string.Empty, targetPreset.id ?? string.Empty, StringComparison.Ordinal)
                && string.Equals(currentPreset.lobbyName ?? string.Empty, targetPreset.lobbyName ?? string.Empty, StringComparison.Ordinal)
                && currentPreset.maxPlayers == targetPreset.maxPlayers
                && currentPreset.PrivacyMode == targetPreset.PrivacyMode
                && string.Equals(currentPreset.password ?? string.Empty, targetPreset.password ?? string.Empty, StringComparison.Ordinal);
        }

        private static int FindMaxPlayersOptionIndex(TMP_Dropdown dropdown, int desiredPlayers)
        {
            if (dropdown == null || dropdown.options == null)
                return -1;

            for (int i = 0; i < dropdown.options.Count; i++)
            {
                var option = dropdown.options[i];
                if (option != null && int.TryParse(option.text, out int value) && value == desiredPlayers)
                {
                    return i;
                }
            }

            return -1;
        }

        private static int ParseMaxPlayersOption(TMP_Dropdown dropdown, int optionIndex)
        {
            if (dropdown == null || dropdown.options == null)
                return 8;

            if (optionIndex < 0 || optionIndex >= dropdown.options.Count)
                return 8;

            var option = dropdown.options[optionIndex];
            if (option != null && int.TryParse(option.text, out int value))
                return value;

            return 8;
        }

        #endregion

        #region SessionSettingsPanelBuilder Integration

        /// <summary>
        /// Applies the current preset data to the create lobby settings panel.
        /// </summary>
        private void ApplyCreateLobbyToSettingsPanel(bool shouldReset, bool focusFirstField)
        {
            if (_createLobbySettingsPanel == null)
                return;

            // Configure for lobby creation mode
            _createLobbySettingsPanel.Configure(SettingsPanelMode.Create, SettingsPanelConfig.ForLobbyBrowser);

            if (shouldReset)
            {
                var sourcePreset = _activePreset;

                // Build suggested lobby name if empty
                string lobbyName = sourcePreset?.lobbyName;
                if (string.IsNullOrWhiteSpace(lobbyName))
                {
                    string player = GetPlayerName();
                    lobbyName = ZString.Format("{0}'s Lobby", player);
                }

                // Convert stored instrument integers to GameMode list
                List<GameMode> allowedGameModes = null;
                if (sourcePreset?.allowedInstruments != null && sourcePreset.allowedInstruments.Count > 0)
                {
                    allowedGameModes = sourcePreset.allowedInstruments
                        .Select(i => (GameMode)i)
                        .Where(gm => Enum.IsDefined(typeof(GameMode), gm))
                        .ToList();
                }

                var data = new SessionSettingsData
                {
                    LobbyName = lobbyName ?? string.Empty,
                    MaxPlayers = sourcePreset?.maxPlayers ?? 4,
                    PrivacyMode = sourcePreset?.PrivacyMode ?? LobbyPrivacyMode.Public,
                    Password = sourcePreset?.password ?? string.Empty,
                    BandSize = sourcePreset?.bandSize ?? 0,
                    NoFailMode = sourcePreset?.noFailMode ?? false,
                    SharedSongsOnly = sourcePreset?.sharedSongsOnly ?? true,
                    AllowModifiers = sourcePreset?.allowModifiers ?? true,
                    EnablePresetSync = sourcePreset?.enablePresetSync ?? true,
                    AllowLateJoin = sourcePreset?.allowLateJoin ?? true,
                    AllowedGameModes = allowedGameModes,
                    LocalPlayersFirst = sourcePreset?.localPlayersFirst ?? false
                };

                _createLobbySettingsPanel.SetData(data);
            }

            if (focusFirstField)
            {
                _createLobbySettingsPanel.FocusFirstField();
            }
        }

        /// <summary>
        /// Applies the current preset data to the hosted lobby settings panel.
        /// </summary>
        private void ApplyHostedLobbyToSettingsPanel()
        {
            if (_hostedLobbySettingsPanel == null)
                return;

            // Configure for hosted lobby editing mode
            _hostedLobbySettingsPanel.Configure(SettingsPanelMode.Create, SettingsPanelConfig.ForLobbyBrowser);

            var sourcePreset = _activePreset;
            
            // Convert stored instrument integers to GameMode list
            List<GameMode> allowedGameModes = null;
            if (sourcePreset?.allowedInstruments != null && sourcePreset.allowedInstruments.Count > 0)
            {
                allowedGameModes = sourcePreset.allowedInstruments
                    .Select(i => (GameMode)i)
                    .Where(gm => Enum.IsDefined(typeof(GameMode), gm))
                    .ToList();
            }
            
            var data = new SessionSettingsData
            {
                LobbyName = sourcePreset?.lobbyName ?? string.Empty,
                MaxPlayers = sourcePreset?.maxPlayers ?? 4,
                PrivacyMode = sourcePreset?.PrivacyMode ?? LobbyPrivacyMode.Public,
                SessionType = sourcePreset?.SessionType ?? SessionType.Server,
                Password = sourcePreset?.password ?? string.Empty,
                BandSize = sourcePreset?.bandSize ?? 0,
                NoFailMode = sourcePreset?.noFailMode ?? false,
                SharedSongsOnly = sourcePreset?.sharedSongsOnly ?? true,
                AllowModifiers = sourcePreset?.allowModifiers ?? true,
                EnablePresetSync = sourcePreset?.enablePresetSync ?? true,
                AllowLateJoin = sourcePreset?.allowLateJoin ?? true,
                AllowedGameModes = allowedGameModes,
                LocalPlayersFirst = sourcePreset?.localPlayersFirst ?? false
            };

            _hostedLobbySettingsPanel.SetData(data);
        }

        /// <summary>
        /// Gets form data from the create lobby settings panel for submission.
        /// </summary>
        private CreateLobbyFormData? GetCreateLobbyFormDataFromPanel()
        {
            if (_createLobbySettingsPanel == null)
                return null;

            var error = _createLobbySettingsPanel.Validate();
            if (!string.IsNullOrEmpty(error))
            {
                ToastManager.ToastError(error);
                return null;
            }

            var data = _createLobbySettingsPanel.GetData();
            Debug.Log($"[LobbyBrowserSidebar] GetCreateLobbyFormDataFromPanel: data.AllowLateJoin={data.AllowLateJoin}");
            
            return new CreateLobbyFormData(
                _activePreset?.id ?? string.Empty,
                data.LobbyName,
                data.MaxPlayers,
                data.PrivacyMode,
                data.SessionType,
                data.Password,
                data.BandSize,
                data.NoFailMode,
                data.SharedSongsOnly,
                data.AllowModifiers,
                data.EnablePresetSync,
                data.AllowLateJoin,
                data.AllowedGameModes?.Select(g => (int)g).ToList() ?? new List<int>(),
                data.LocalPlayersFirst
            );
        }

        /// <summary>
        /// Gets settings data from the hosted lobby settings panel for saving.
        /// </summary>
        private SessionSettingsData GetHostedLobbyDataFromPanel()
        {
            if (_hostedLobbySettingsPanel == null)
                return null;

            var data = _hostedLobbySettingsPanel.GetData();
            Debug.Log($"[LobbyBrowserSidebar] GetHostedLobbyDataFromPanel: SessionType={data.SessionType}");
            return data;
        }

        #endregion

        #region Data Contracts

        public readonly struct CreateLobbyFormData
        {
            public string PresetId { get; }
            public string LobbyName { get; }
            public int MaxPlayers { get; }
            public LobbyPrivacyMode PrivacyMode { get; }
            public SessionType SessionType { get; }
            public string Password { get; }
            
            // Extended gameplay settings
            public int BandSize { get; }
            public bool NoFailMode { get; }
            public bool SharedSongsOnly { get; }
            public bool AllowModifiers { get; }
            
            // Session settings
            public bool EnablePresetSync { get; }
            public bool AllowLateJoin { get; }
            
            // Instrument restrictions
            public List<int> AllowedInstruments { get; }
            public bool LocalPlayersFirst { get; }

            public CreateLobbyFormData(string presetId, string lobbyName, int maxPlayers, LobbyPrivacyMode privacyMode, string password)
                : this(presetId, lobbyName, maxPlayers, privacyMode, SessionType.Lobby, password, 0, false, true, true, true, true, new List<int>(), false)
            {
            }

            public CreateLobbyFormData(
                string presetId, 
                string lobbyName, 
                int maxPlayers, 
                LobbyPrivacyMode privacyMode, 
                string password,
                int bandSize,
                bool noFailMode,
                bool sharedSongsOnly,
                bool allowModifiers)
                : this(presetId, lobbyName, maxPlayers, privacyMode, SessionType.Lobby, password, bandSize, noFailMode, sharedSongsOnly, allowModifiers, true, true, new List<int>(), false)
            {
            }

            public CreateLobbyFormData(
                string presetId, 
                string lobbyName, 
                int maxPlayers, 
                LobbyPrivacyMode privacyMode, 
                string password,
                int bandSize,
                bool noFailMode,
                bool sharedSongsOnly,
                bool allowModifiers,
                bool enablePresetSync,
                bool allowLateJoin)
                : this(presetId, lobbyName, maxPlayers, privacyMode, SessionType.Lobby, password, bandSize, noFailMode, sharedSongsOnly, allowModifiers, enablePresetSync, allowLateJoin, new List<int>(), false)
            {
            }

            public CreateLobbyFormData(
                string presetId, 
                string lobbyName, 
                int maxPlayers, 
                LobbyPrivacyMode privacyMode, 
                SessionType sessionType,
                string password,
                int bandSize,
                bool noFailMode,
                bool sharedSongsOnly,
                bool allowModifiers,
                bool enablePresetSync,
                bool allowLateJoin,
                List<int> allowedInstruments,
                bool localPlayersFirst)
            {
                PresetId = presetId ?? string.Empty;
                LobbyName = lobbyName ?? string.Empty;
                MaxPlayers = maxPlayers;
                PrivacyMode = privacyMode;
                SessionType = sessionType;
                Password = password ?? string.Empty;
                BandSize = bandSize;
                NoFailMode = noFailMode;
                SharedSongsOnly = sharedSongsOnly;
                AllowModifiers = allowModifiers;
                EnablePresetSync = enablePresetSync;
                AllowLateJoin = allowLateJoin;
                AllowedInstruments = allowedInstruments ?? new List<int>();
                LocalPlayersFirst = localPlayersFirst;
            }
        }

        public readonly struct DirectConnectFormData
        {
            public string Address { get; }
            public int Port { get; }
            public string DisplayName { get; }
            public string Password { get; }

            public DirectConnectFormData(string address, int port, string displayName, string password)
            {
                Address = address ?? string.Empty;
                Port = port;
                DisplayName = displayName ?? string.Empty;
                Password = password ?? string.Empty;
            }
        }

        #endregion
    }
}
