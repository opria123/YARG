using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using YARG.Helpers.Extensions;
using YARG.Menu.Navigation;
using YARG.Networking.Abstraction;

namespace YARG.Menu.Multiplayer.Settings
{
    /// <summary>
    /// Dynamic version of the session settings panel that spawns setting items using prefabs.
    /// Organizes settings into collapsible categories for better UX.
    /// </summary>
    public class SessionSettingsPanelDynamic : MonoBehaviour
    {
        #region Serialized Fields

        [Header("Prefabs")]
        [SerializeField] private SessionSettingsCategoryDrawer _categoryPrefab;
        [SerializeField] private SessionSettingItem _textSettingPrefab;
        [SerializeField] private SessionSettingItem _integerSettingPrefab;
        [SerializeField] private SessionSettingItem _toggleSettingPrefab;
        [SerializeField] private SessionSettingItem _dropdownSettingPrefab;

        [Header("Layout")]
        [SerializeField] private RectTransform _contentContainer;
        [SerializeField] private ScrollRect _scrollRect;
        [SerializeField] private NavigationGroup _navigationGroup;

        [Header("Category Expansion")]
        [SerializeField] private bool _lobbyInfoExpanded = true;
        [SerializeField] private bool _gameplaySettingsExpanded = true;
        [SerializeField] private bool _sessionSettingsExpanded = true;

        #endregion

        #region Events

        /// <summary>
        /// Fired when any setting value changes.
        /// </summary>
        public event Action<SessionSettingsData> OnSettingsChanged;

        #endregion

        #region Private State

        private SettingsPanelMode _mode = SettingsPanelMode.Create;
        private SettingsPanelConfig _config = SettingsPanelConfig.Default;
        private SessionSettingsData _currentData = new();
        private bool _suppressCallbacks;
        private bool _initialized;

        // Category drawers
        private SessionSettingsCategoryDrawer _lobbyInfoCategory;
        private SessionSettingsCategoryDrawer _gameplayCategory;
        private SessionSettingsCategoryDrawer _sessionCategory;

        // Setting items by key
        private readonly Dictionary<string, SessionSettingItem> _settingItems = new();

        // Setting keys
        private const string KEY_LOBBY_NAME = "LobbyName";
        private const string KEY_MAX_PLAYERS = "MaxPlayers";
        private const string KEY_PRIVACY = "Privacy";
        private const string KEY_PASSWORD = "Password";
        private const string KEY_BAND_SIZE = "BandSize";
        private const string KEY_NO_FAIL_MODE = "NoFailMode";
        private const string KEY_SHARED_SONGS = "SharedSongs";
        private const string KEY_ALLOW_MODIFIERS = "AllowModifiers";
        private const string KEY_PRESET_SYNC = "PresetSync";
        private const string KEY_ALLOW_LATE_JOIN = "AllowLateJoin";

        #endregion

        #region Public Properties

        /// <summary>
        /// Gets the current panel mode.
        /// </summary>
        public SettingsPanelMode Mode => _mode;

        /// <summary>
        /// Gets the current configuration.
        /// </summary>
        public SettingsPanelConfig Config => _config;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            InitializeIfNeeded();
        }

        #endregion

        #region Initialization

        private void InitializeIfNeeded()
        {
            if (_initialized)
                return;

            BuildSettingsUI();
            _initialized = true;
        }

        private void BuildSettingsUI()
        {
            // Clear existing content
            if (_contentContainer != null)
            {
                _contentContainer.DestroyChildren();
            }
            _settingItems.Clear();

            // Create categories
            _lobbyInfoCategory = CreateCategory("Lobby Info");
            _gameplayCategory = CreateCategory("Gameplay Settings");
            _sessionCategory = CreateCategory("Session Settings");

            // Create settings in each category
            BuildLobbyInfoSettings();
            BuildGameplaySettings();
            BuildSessionSettings();

            // Set initial expansion states
            _lobbyInfoCategory?.SetExpanded(_lobbyInfoExpanded, rebuild: false);
            _gameplayCategory?.SetExpanded(_gameplaySettingsExpanded, rebuild: false);
            _sessionCategory?.SetExpanded(_sessionSettingsExpanded, rebuild: false);

            // Force layout rebuild
            if (_contentContainer != null)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(_contentContainer);
            }
        }

        private SessionSettingsCategoryDrawer CreateCategory(string categoryName)
        {
            if (_categoryPrefab == null || _contentContainer == null)
                return null;

            var category = Instantiate(_categoryPrefab, _contentContainer);
            category.Initialize(categoryName, _navigationGroup);
            return category;
        }

        private void BuildLobbyInfoSettings()
        {
            if (_lobbyInfoCategory == null) return;

            // Lobby Name
            var lobbyNameItem = CreateSettingItem(KEY_LOBBY_NAME, "Lobby Name", SessionSettingType.Text, _textSettingPrefab);
            if (lobbyNameItem != null)
            {
                lobbyNameItem.ConfigureTextInput("Enter lobby name...", 64);
                _lobbyInfoCategory.AddSettingItem(lobbyNameItem);
            }

            // Max Players
            var maxPlayersItem = CreateSettingItem(KEY_MAX_PLAYERS, "Max Players", SessionSettingType.Dropdown, _dropdownSettingPrefab);
            if (maxPlayersItem != null)
            {
                var options = new List<string>();
                for (int i = 2; i <= 32; i++)
                {
                    options.Add(i.ToString());
                }
                maxPlayersItem.SetDropdownOptions(options);
                _lobbyInfoCategory.AddSettingItem(maxPlayersItem);
            }

            // Privacy
            var privacyItem = CreateSettingItem(KEY_PRIVACY, "Privacy", SessionSettingType.Dropdown, _dropdownSettingPrefab);
            if (privacyItem != null)
            {
                privacyItem.SetDropdownOptions(
                    "Public",
                    "Private (Password)",
                    "Unlisted (Direct Connect Only)"
                );
                _lobbyInfoCategory.AddSettingItem(privacyItem);
            }

            // Password
            var passwordItem = CreateSettingItem(KEY_PASSWORD, "Password", SessionSettingType.Text, _textSettingPrefab);
            if (passwordItem != null)
            {
                passwordItem.ConfigurePasswordInput("Enter password...");
                _lobbyInfoCategory.AddSettingItem(passwordItem);
            }
        }

        private void BuildGameplaySettings()
        {
            if (_gameplayCategory == null) return;

            // Band Size
            var bandSizeItem = CreateSettingItem(KEY_BAND_SIZE, "Band Size (0 = unlimited)", SessionSettingType.Integer, _integerSettingPrefab);
            if (bandSizeItem != null)
            {
                bandSizeItem.ConfigureIntegerInput(0, 8, "0-8");
                _gameplayCategory.AddSettingItem(bandSizeItem);
            }

            // No Fail Mode
            var noFailItem = CreateSettingItem(KEY_NO_FAIL_MODE, "No Fail Mode", SessionSettingType.Toggle, _toggleSettingPrefab);
            if (noFailItem != null)
            {
                _gameplayCategory.AddSettingItem(noFailItem);
            }

            // Shared Songs Only
            var sharedSongsItem = CreateSettingItem(KEY_SHARED_SONGS, "Shared Songs Only", SessionSettingType.Toggle, _toggleSettingPrefab);
            if (sharedSongsItem != null)
            {
                _gameplayCategory.AddSettingItem(sharedSongsItem);
            }

            // Allow Modifiers
            var modifiersItem = CreateSettingItem(KEY_ALLOW_MODIFIERS, "Allow Modifiers", SessionSettingType.Toggle, _toggleSettingPrefab);
            if (modifiersItem != null)
            {
                _gameplayCategory.AddSettingItem(modifiersItem);
            }
        }

        private void BuildSessionSettings()
        {
            if (_sessionCategory == null) return;

            // Preset Sync
            var presetSyncItem = CreateSettingItem(KEY_PRESET_SYNC, "Enable Preset Sync", SessionSettingType.Toggle, _toggleSettingPrefab);
            if (presetSyncItem != null)
            {
                _sessionCategory.AddSettingItem(presetSyncItem);
            }

            // Allow Late Join
            var lateJoinItem = CreateSettingItem(KEY_ALLOW_LATE_JOIN, "Allow Late Join", SessionSettingType.Toggle, _toggleSettingPrefab);
            if (lateJoinItem != null)
            {
                _sessionCategory.AddSettingItem(lateJoinItem);
            }
        }

        private SessionSettingItem CreateSettingItem(string key, string label, SessionSettingType type, SessionSettingItem prefab)
        {
            if (prefab == null)
            {
                Debug.LogWarning($"[SessionSettingsPanel] No prefab assigned for setting type {type}");
                return null;
            }

            var item = Instantiate(prefab);
            item.Initialize(key, label, type);
            item.OnValueChanged += OnSettingItemValueChanged;
            _settingItems[key] = item;
            return item;
        }

        #endregion

        #region Public API

        /// <summary>
        /// Configures the panel with a mode and configuration.
        /// </summary>
        public void Configure(SettingsPanelMode mode, SettingsPanelConfig config = null)
        {
            InitializeIfNeeded();

            _mode = mode;
            _config = config ?? (mode == SettingsPanelMode.Create
                ? SettingsPanelConfig.ForLobbyBrowser
                : SettingsPanelConfig.ForLobbyRoom);

            ApplyVisibility();
            ApplyInteractability();
        }

        /// <summary>
        /// Sets the settings data to display.
        /// </summary>
        public void SetData(SessionSettingsData data)
        {
            InitializeIfNeeded();

            _currentData = data?.Clone() ?? new SessionSettingsData();
            ApplyDataToUI();
        }

        /// <summary>
        /// Gets the current settings data from the UI.
        /// </summary>
        public SessionSettingsData GetData()
        {
            ReadDataFromUI();
            return _currentData.Clone();
        }

        /// <summary>
        /// Focuses the first editable input field.
        /// </summary>
        public void FocusFirstField()
        {
            if (_settingItems.TryGetValue(KEY_LOBBY_NAME, out var item) && item.gameObject.activeSelf)
            {
                item.Focus();
            }
        }

        /// <summary>
        /// Validates the current settings.
        /// </summary>
        public string Validate()
        {
            ReadDataFromUI();

            if (_config.ShowLobbyName && string.IsNullOrWhiteSpace(_currentData.LobbyName))
            {
                return "Lobby name cannot be empty.";
            }

            if (_currentData.MaxPlayers < 2 || _currentData.MaxPlayers > 32)
            {
                return "Max players must be between 2 and 32.";
            }

            if (_currentData.PrivacyMode == LobbyPrivacyMode.Private && string.IsNullOrWhiteSpace(_currentData.Password))
            {
                return "Password is required for private lobbies.";
            }

            if (_currentData.BandSize < 0 || _currentData.BandSize > 8)
            {
                return "Band size must be between 0 and 8.";
            }

            return null;
        }

        /// <summary>
        /// Scrolls to the top of the settings list.
        /// </summary>
        public void ScrollToTop()
        {
            if (_scrollRect != null)
            {
                _scrollRect.verticalNormalizedPosition = 1f;
            }
        }

        /// <summary>
        /// Expands or collapses all categories.
        /// </summary>
        public void SetAllCategoriesExpanded(bool expanded)
        {
            _lobbyInfoCategory?.SetExpanded(expanded);
            _gameplayCategory?.SetExpanded(expanded);
            _sessionCategory?.SetExpanded(expanded);
        }

        #endregion

        #region UI Application

        private void ApplyVisibility()
        {
            // Category visibility
            SetCategoryVisible(_lobbyInfoCategory,
                _config.ShowLobbyName || _config.ShowMaxPlayers || _config.ShowPrivacy);

            SetCategoryVisible(_gameplayCategory,
                _config.ShowBandSize || _config.ShowNoFailMode ||
                _config.ShowSharedSongsOnly || _config.ShowAllowModifiers);

            SetCategoryVisible(_sessionCategory,
                _config.ShowEnablePresetSync || _config.ShowAllowLateJoin);

            // Individual setting visibility
            SetSettingVisible(KEY_LOBBY_NAME, _config.ShowLobbyName);
            SetSettingVisible(KEY_MAX_PLAYERS, _config.ShowMaxPlayers);
            SetSettingVisible(KEY_PRIVACY, _config.ShowPrivacy);
            SetSettingVisible(KEY_BAND_SIZE, _config.ShowBandSize);
            SetSettingVisible(KEY_NO_FAIL_MODE, _config.ShowNoFailMode);
            SetSettingVisible(KEY_SHARED_SONGS, _config.ShowSharedSongsOnly);
            SetSettingVisible(KEY_ALLOW_MODIFIERS, _config.ShowAllowModifiers);
            SetSettingVisible(KEY_PRESET_SYNC, _config.ShowEnablePresetSync);
            SetSettingVisible(KEY_ALLOW_LATE_JOIN, _config.ShowAllowLateJoin);

            // Password visibility depends on privacy mode
            UpdatePasswordVisibility();
        }

        private void ApplyInteractability()
        {
            bool isViewOnly = _mode == SettingsPanelMode.ViewOnly;
            bool isHostEdit = _mode == SettingsPanelMode.HostEdit;

            // In ViewOnly mode, everything is disabled
            // In HostEdit mode, locked settings are disabled
            // In Create mode, everything is enabled

            SetSettingInteractable(KEY_LOBBY_NAME, !isViewOnly);
            SetSettingInteractable(KEY_MAX_PLAYERS, !isViewOnly && !(isHostEdit && _config.LockMaxPlayersInSession));
            SetSettingInteractable(KEY_PRIVACY, !isViewOnly && !(isHostEdit && _config.LockPrivacyInSession));
            SetSettingInteractable(KEY_PASSWORD, !isViewOnly);
            SetSettingInteractable(KEY_BAND_SIZE, !isViewOnly && !(isHostEdit && _config.LockBandSizeInSession));
            SetSettingInteractable(KEY_NO_FAIL_MODE, !isViewOnly);
            SetSettingInteractable(KEY_SHARED_SONGS, !isViewOnly);
            SetSettingInteractable(KEY_ALLOW_MODIFIERS, !isViewOnly);
            SetSettingInteractable(KEY_PRESET_SYNC, !isViewOnly);
            SetSettingInteractable(KEY_ALLOW_LATE_JOIN, !isViewOnly);
        }

        private void ApplyDataToUI()
        {
            _suppressCallbacks = true;

            try
            {
                SetSettingValue(KEY_LOBBY_NAME, _currentData.LobbyName ?? string.Empty);
                SetSettingValue(KEY_MAX_PLAYERS, _currentData.MaxPlayers - 2); // Dropdown index
                SetSettingValue(KEY_PRIVACY, (int)_currentData.PrivacyMode);
                SetSettingValue(KEY_PASSWORD, _currentData.Password ?? string.Empty);
                SetSettingValue(KEY_BAND_SIZE, _currentData.BandSize);
                SetSettingValue(KEY_NO_FAIL_MODE, _currentData.NoFailMode);
                SetSettingValue(KEY_SHARED_SONGS, _currentData.SharedSongsOnly);
                SetSettingValue(KEY_ALLOW_MODIFIERS, _currentData.AllowModifiers);
                SetSettingValue(KEY_PRESET_SYNC, _currentData.EnablePresetSync);
                SetSettingValue(KEY_ALLOW_LATE_JOIN, _currentData.AllowLateJoin);

                UpdatePasswordVisibility();
            }
            finally
            {
                _suppressCallbacks = false;
            }
        }

        private void ReadDataFromUI()
        {
            if (_settingItems.TryGetValue(KEY_LOBBY_NAME, out var lobbyName))
                _currentData.LobbyName = lobbyName.GetStringValue()?.Trim() ?? string.Empty;

            if (_settingItems.TryGetValue(KEY_MAX_PLAYERS, out var maxPlayers))
                _currentData.MaxPlayers = maxPlayers.GetIntValue() + 2; // Dropdown index to value

            if (_settingItems.TryGetValue(KEY_PRIVACY, out var privacy))
                _currentData.PrivacyMode = (LobbyPrivacyMode)Mathf.Clamp(privacy.GetIntValue(), 0, 2);

            if (_settingItems.TryGetValue(KEY_PASSWORD, out var password))
                _currentData.Password = password.GetStringValue() ?? string.Empty;

            if (_settingItems.TryGetValue(KEY_BAND_SIZE, out var bandSize))
                _currentData.BandSize = Mathf.Clamp(bandSize.GetIntValue(), 0, 8);

            if (_settingItems.TryGetValue(KEY_NO_FAIL_MODE, out var noFail))
                _currentData.NoFailMode = noFail.GetBoolValue();

            if (_settingItems.TryGetValue(KEY_SHARED_SONGS, out var sharedSongs))
                _currentData.SharedSongsOnly = sharedSongs.GetBoolValue();

            if (_settingItems.TryGetValue(KEY_ALLOW_MODIFIERS, out var modifiers))
                _currentData.AllowModifiers = modifiers.GetBoolValue();

            if (_settingItems.TryGetValue(KEY_PRESET_SYNC, out var presetSync))
                _currentData.EnablePresetSync = presetSync.GetBoolValue();

            if (_settingItems.TryGetValue(KEY_ALLOW_LATE_JOIN, out var lateJoin))
                _currentData.AllowLateJoin = lateJoin.GetBoolValue();
        }

        private void UpdatePasswordVisibility()
        {
            bool showPassword = _config.ShowPassword && _currentData.PrivacyMode == LobbyPrivacyMode.Private;
            SetSettingVisible(KEY_PASSWORD, showPassword);
        }

        #endregion

        #region Event Handlers

        private void OnSettingItemValueChanged(SessionSettingItem item, object value)
        {
            if (_suppressCallbacks) return;

            // Update data based on which setting changed
            switch (item.SettingKey)
            {
                case KEY_LOBBY_NAME:
                    _currentData.LobbyName = value?.ToString() ?? string.Empty;
                    break;

                case KEY_MAX_PLAYERS:
                    _currentData.MaxPlayers = Convert.ToInt32(value) + 2;
                    break;

                case KEY_PRIVACY:
                    _currentData.PrivacyMode = (LobbyPrivacyMode)Mathf.Clamp(Convert.ToInt32(value), 0, 2);
                    UpdatePasswordVisibility();
                    break;

                case KEY_PASSWORD:
                    _currentData.Password = value?.ToString() ?? string.Empty;
                    break;

                case KEY_BAND_SIZE:
                    _currentData.BandSize = Mathf.Clamp(Convert.ToInt32(value), 0, 8);
                    break;

                case KEY_NO_FAIL_MODE:
                    _currentData.NoFailMode = Convert.ToBoolean(value);
                    break;

                case KEY_SHARED_SONGS:
                    _currentData.SharedSongsOnly = Convert.ToBoolean(value);
                    break;

                case KEY_ALLOW_MODIFIERS:
                    _currentData.AllowModifiers = Convert.ToBoolean(value);
                    break;

                case KEY_PRESET_SYNC:
                    _currentData.EnablePresetSync = Convert.ToBoolean(value);
                    break;

                case KEY_ALLOW_LATE_JOIN:
                    _currentData.AllowLateJoin = Convert.ToBoolean(value);
                    break;
            }

            OnSettingsChanged?.Invoke(_currentData.Clone());
        }

        #endregion

        #region Helpers

        private void SetCategoryVisible(SessionSettingsCategoryDrawer category, bool visible)
        {
            if (category != null)
            {
                category.gameObject.SetActive(visible);
            }
        }

        private void SetSettingVisible(string key, bool visible)
        {
            if (_settingItems.TryGetValue(key, out var item))
            {
                item.gameObject.SetActive(visible);
            }
        }

        private void SetSettingInteractable(string key, bool interactable)
        {
            if (_settingItems.TryGetValue(key, out var item))
            {
                item.SetInteractable(interactable);
            }
        }

        private void SetSettingValue(string key, object value)
        {
            if (_settingItems.TryGetValue(key, out var item))
            {
                item.SetValueWithoutNotify(value);
            }
        }

        #endregion

        #region Cleanup

        private void OnDestroy()
        {
            // Unsubscribe from all setting item events
            foreach (var item in _settingItems.Values)
            {
                if (item != null)
                {
                    item.OnValueChanged -= OnSettingItemValueChanged;
                }
            }
        }

        #endregion
    }
}
