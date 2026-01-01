using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using YARG.Networking.Abstraction;

namespace YARG.Menu.Multiplayer.Settings
{
    /// <summary>
    /// Reusable UI component for displaying and editing session settings.
    /// Can be placed in sidebars, dialogs, or any container.
    /// </summary>
    public class SessionSettingsPanel : MonoBehaviour
    {
        #region Serialized Fields

        [Header("Lobby Info")]
        [SerializeField] private GameObject _lobbyNameRow;
        [SerializeField] private TMP_InputField _lobbyNameInput;

        [SerializeField] private GameObject _maxPlayersRow;
        [SerializeField] private TMP_Dropdown _maxPlayersDropdown;

        [SerializeField] private GameObject _privacyRow;
        [SerializeField] private TMP_Dropdown _privacyDropdown;

        [SerializeField] private GameObject _passwordRow;
        [SerializeField] private TMP_InputField _passwordInput;

        [Header("Gameplay Settings")]
        [SerializeField] private GameObject _bandSizeRow;
        [SerializeField] private TMP_InputField _bandSizeInput;

        [SerializeField] private GameObject _noFailModeRow;
        [SerializeField] private Toggle _noFailModeToggle;

        [SerializeField] private GameObject _sharedSongsRow;
        [SerializeField] private Toggle _sharedSongsToggle;

        [SerializeField] private GameObject _allowModifiersRow;
        [SerializeField] private Toggle _allowModifiersToggle;

        [Header("Session Settings")]
        [SerializeField] private GameObject _presetSyncRow;
        [SerializeField] private Toggle _presetSyncToggle;

        [SerializeField] private GameObject _allowLateJoinRow;
        [SerializeField] private Toggle _allowLateJoinToggle;

        #endregion

        #region Events

        /// <summary>
        /// Fired when any setting value changes. Provides the current settings data.
        /// </summary>
        public event Action<SessionSettingsData> OnSettingsChanged;

        #endregion

        #region Private State

        private SettingsPanelMode _mode = SettingsPanelMode.Create;
        private SettingsPanelConfig _config = SettingsPanelConfig.Default;
        private SessionSettingsData _currentData = new SessionSettingsData();
        private bool _suppressCallbacks;
        private bool _initialized;

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

        private void OnEnable()
        {
            RegisterListeners();
        }

        private void OnDisable()
        {
            UnregisterListeners();
        }

        #endregion

        #region Initialization

        private void InitializeIfNeeded()
        {
            if (_initialized)
                return;

            EnsureDropdownOptions();
            _initialized = true;
        }

        private void EnsureDropdownOptions()
        {
            // Max players dropdown
            if (_maxPlayersDropdown != null && (_maxPlayersDropdown.options == null || _maxPlayersDropdown.options.Count == 0))
            {
                _maxPlayersDropdown.ClearOptions();
                var options = new List<string>();
                for (int i = 2; i <= 32; i++)
                {
                    options.Add(i.ToString());
                }
                _maxPlayersDropdown.AddOptions(options);
            }

            // Privacy dropdown
            if (_privacyDropdown != null && (_privacyDropdown.options == null || _privacyDropdown.options.Count < 3))
            {
                _privacyDropdown.ClearOptions();
                _privacyDropdown.AddOptions(new List<string>
                {
                    "Public",
                    "Private (Password)",
                    "Unlisted (Direct Connect Only)"
                });
            }
        }

        private void RegisterListeners()
        {
            if (_lobbyNameInput != null)
                _lobbyNameInput.onEndEdit.AddListener(OnLobbyNameChanged);

            if (_maxPlayersDropdown != null)
                _maxPlayersDropdown.onValueChanged.AddListener(OnMaxPlayersChanged);

            if (_privacyDropdown != null)
                _privacyDropdown.onValueChanged.AddListener(OnPrivacyChanged);

            if (_passwordInput != null)
                _passwordInput.onEndEdit.AddListener(OnPasswordChanged);

            if (_bandSizeInput != null)
                _bandSizeInput.onEndEdit.AddListener(OnBandSizeChanged);

            if (_noFailModeToggle != null)
                _noFailModeToggle.onValueChanged.AddListener(OnNoFailModeChanged);

            if (_sharedSongsToggle != null)
                _sharedSongsToggle.onValueChanged.AddListener(OnSharedSongsChanged);

            if (_allowModifiersToggle != null)
                _allowModifiersToggle.onValueChanged.AddListener(OnAllowModifiersChanged);

            if (_presetSyncToggle != null)
                _presetSyncToggle.onValueChanged.AddListener(OnPresetSyncChanged);

            if (_allowLateJoinToggle != null)
                _allowLateJoinToggle.onValueChanged.AddListener(OnAllowLateJoinChanged);
        }

        private void UnregisterListeners()
        {
            if (_lobbyNameInput != null)
                _lobbyNameInput.onEndEdit.RemoveListener(OnLobbyNameChanged);

            if (_maxPlayersDropdown != null)
                _maxPlayersDropdown.onValueChanged.RemoveListener(OnMaxPlayersChanged);

            if (_privacyDropdown != null)
                _privacyDropdown.onValueChanged.RemoveListener(OnPrivacyChanged);

            if (_passwordInput != null)
                _passwordInput.onEndEdit.RemoveListener(OnPasswordChanged);

            if (_bandSizeInput != null)
                _bandSizeInput.onEndEdit.RemoveListener(OnBandSizeChanged);

            if (_noFailModeToggle != null)
                _noFailModeToggle.onValueChanged.RemoveListener(OnNoFailModeChanged);

            if (_sharedSongsToggle != null)
                _sharedSongsToggle.onValueChanged.RemoveListener(OnSharedSongsChanged);

            if (_allowModifiersToggle != null)
                _allowModifiersToggle.onValueChanged.RemoveListener(OnAllowModifiersChanged);

            if (_presetSyncToggle != null)
                _presetSyncToggle.onValueChanged.RemoveListener(OnPresetSyncChanged);

            if (_allowLateJoinToggle != null)
                _allowLateJoinToggle.onValueChanged.RemoveListener(OnAllowLateJoinChanged);
        }

        #endregion

        #region Public API

        /// <summary>
        /// Configures the panel with a mode and configuration.
        /// </summary>
        /// <param name="mode">The editing mode.</param>
        /// <param name="config">Optional configuration. If null, uses default for the mode.</param>
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
        /// <param name="data">The settings data to display.</param>
        public void SetData(SessionSettingsData data)
        {
            InitializeIfNeeded();

            _currentData = data?.Clone() ?? new SessionSettingsData();
            ApplyDataToUI();
        }

        /// <summary>
        /// Gets the current settings data from the UI.
        /// </summary>
        /// <returns>A copy of the current settings.</returns>
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
            if (_lobbyNameRow != null && _lobbyNameRow.activeSelf && _lobbyNameInput != null && _lobbyNameInput.interactable)
            {
                _lobbyNameInput.Select();
                _lobbyNameInput.ActivateInputField();
            }
        }

        /// <summary>
        /// Validates the current settings and returns any error messages.
        /// </summary>
        /// <returns>Null if valid, otherwise an error message.</returns>
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

        #endregion

        #region UI Application

        private void ApplyVisibility()
        {
            SetRowActive(_lobbyNameRow, _config.ShowLobbyName);
            SetRowActive(_maxPlayersRow, _config.ShowMaxPlayers);
            SetRowActive(_privacyRow, _config.ShowPrivacy);
            SetRowActive(_bandSizeRow, _config.ShowBandSize);
            SetRowActive(_noFailModeRow, _config.ShowNoFailMode);
            SetRowActive(_sharedSongsRow, _config.ShowSharedSongsOnly);
            SetRowActive(_allowModifiersRow, _config.ShowAllowModifiers);
            SetRowActive(_presetSyncRow, _config.ShowEnablePresetSync);
            SetRowActive(_allowLateJoinRow, _config.ShowAllowLateJoin);

            // Password row visibility depends on both config and current privacy mode
            UpdatePasswordRowVisibility();
        }

        private void ApplyInteractability()
        {
            bool isViewOnly = _mode == SettingsPanelMode.ViewOnly;
            bool isHostEdit = _mode == SettingsPanelMode.HostEdit;

            // In ViewOnly mode, everything is disabled
            // In HostEdit mode, locked settings are disabled
            // In Create mode, everything is enabled

            SetInteractable(_lobbyNameInput, !isViewOnly);
            SetInteractable(_maxPlayersDropdown, !isViewOnly && !(isHostEdit && _config.LockMaxPlayersInSession));
            SetInteractable(_privacyDropdown, !isViewOnly && !(isHostEdit && _config.LockPrivacyInSession));
            SetInteractable(_passwordInput, !isViewOnly);
            SetInteractable(_bandSizeInput, !isViewOnly && !(isHostEdit && _config.LockBandSizeInSession));
            SetInteractable(_noFailModeToggle, !isViewOnly);
            SetInteractable(_sharedSongsToggle, !isViewOnly);
            SetInteractable(_allowModifiersToggle, !isViewOnly);
            SetInteractable(_presetSyncToggle, !isViewOnly);
            SetInteractable(_allowLateJoinToggle, !isViewOnly);
        }

        private void ApplyDataToUI()
        {
            _suppressCallbacks = true;

            try
            {
                // Lobby name
                if (_lobbyNameInput != null)
                {
                    _lobbyNameInput.SetTextWithoutNotify(_currentData.LobbyName ?? string.Empty);
                }

                // Max players
                if (_maxPlayersDropdown != null)
                {
                    int index = Mathf.Clamp(_currentData.MaxPlayers - 2, 0, _maxPlayersDropdown.options.Count - 1);
                    _maxPlayersDropdown.SetValueWithoutNotify(index);
                    _maxPlayersDropdown.RefreshShownValue();
                }

                // Privacy
                if (_privacyDropdown != null)
                {
                    int index = Mathf.Clamp((int)_currentData.PrivacyMode, 0, 2);
                    _privacyDropdown.SetValueWithoutNotify(index);
                    _privacyDropdown.RefreshShownValue();
                }

                // Password
                if (_passwordInput != null)
                {
                    _passwordInput.SetTextWithoutNotify(_currentData.Password ?? string.Empty);
                }

                // Band size
                if (_bandSizeInput != null)
                {
                    _bandSizeInput.SetTextWithoutNotify(_currentData.BandSize.ToString());
                }

                // Toggles
                if (_noFailModeToggle != null)
                    _noFailModeToggle.SetIsOnWithoutNotify(_currentData.NoFailMode);

                if (_sharedSongsToggle != null)
                    _sharedSongsToggle.SetIsOnWithoutNotify(_currentData.SharedSongsOnly);

                if (_allowModifiersToggle != null)
                    _allowModifiersToggle.SetIsOnWithoutNotify(_currentData.AllowModifiers);

                if (_presetSyncToggle != null)
                    _presetSyncToggle.SetIsOnWithoutNotify(_currentData.EnablePresetSync);

                if (_allowLateJoinToggle != null)
                    _allowLateJoinToggle.SetIsOnWithoutNotify(_currentData.AllowLateJoin);

                UpdatePasswordRowVisibility();
            }
            finally
            {
                _suppressCallbacks = false;
            }
        }

        private void ReadDataFromUI()
        {
            if (_lobbyNameInput != null)
                _currentData.LobbyName = _lobbyNameInput.text?.Trim() ?? string.Empty;

            if (_maxPlayersDropdown != null)
                _currentData.MaxPlayers = _maxPlayersDropdown.value + 2;

            if (_privacyDropdown != null)
                _currentData.PrivacyMode = (LobbyPrivacyMode)Mathf.Clamp(_privacyDropdown.value, 0, 2);

            if (_passwordInput != null)
                _currentData.Password = _passwordInput.text ?? string.Empty;

            if (_bandSizeInput != null)
            {
                if (int.TryParse(_bandSizeInput.text, out int bandSize))
                    _currentData.BandSize = Mathf.Clamp(bandSize, 0, 8);
            }

            if (_noFailModeToggle != null)
                _currentData.NoFailMode = _noFailModeToggle.isOn;

            if (_sharedSongsToggle != null)
                _currentData.SharedSongsOnly = _sharedSongsToggle.isOn;

            if (_allowModifiersToggle != null)
                _currentData.AllowModifiers = _allowModifiersToggle.isOn;

            if (_presetSyncToggle != null)
                _currentData.EnablePresetSync = _presetSyncToggle.isOn;

            if (_allowLateJoinToggle != null)
                _currentData.AllowLateJoin = _allowLateJoinToggle.isOn;
        }

        private void UpdatePasswordRowVisibility()
        {
            if (_passwordRow == null)
                return;

            bool showPassword = _config.ShowPassword && _currentData.PrivacyMode == LobbyPrivacyMode.Private;
            _passwordRow.SetActive(showPassword);
        }

        #endregion

        #region Change Handlers

        private void OnLobbyNameChanged(string value)
        {
            if (_suppressCallbacks)
                return;

            _currentData.LobbyName = value?.Trim() ?? string.Empty;
            NotifySettingsChanged();
        }

        private void OnMaxPlayersChanged(int index)
        {
            if (_suppressCallbacks)
                return;

            _currentData.MaxPlayers = index + 2;
            NotifySettingsChanged();
        }

        private void OnPrivacyChanged(int index)
        {
            if (_suppressCallbacks)
                return;

            _currentData.PrivacyMode = (LobbyPrivacyMode)Mathf.Clamp(index, 0, 2);
            UpdatePasswordRowVisibility();
            NotifySettingsChanged();
        }

        private void OnPasswordChanged(string value)
        {
            if (_suppressCallbacks)
                return;

            _currentData.Password = value ?? string.Empty;
            NotifySettingsChanged();
        }

        private void OnBandSizeChanged(string value)
        {
            if (_suppressCallbacks)
                return;

            if (int.TryParse(value, out int bandSize))
            {
                _currentData.BandSize = Mathf.Clamp(bandSize, 0, 8);
            }
            NotifySettingsChanged();
        }

        private void OnNoFailModeChanged(bool value)
        {
            if (_suppressCallbacks)
                return;

            _currentData.NoFailMode = value;
            NotifySettingsChanged();
        }

        private void OnSharedSongsChanged(bool value)
        {
            if (_suppressCallbacks)
                return;

            _currentData.SharedSongsOnly = value;
            NotifySettingsChanged();
        }

        private void OnAllowModifiersChanged(bool value)
        {
            if (_suppressCallbacks)
                return;

            _currentData.AllowModifiers = value;
            NotifySettingsChanged();
        }

        private void OnPresetSyncChanged(bool value)
        {
            if (_suppressCallbacks)
                return;

            _currentData.EnablePresetSync = value;
            NotifySettingsChanged();
        }

        private void OnAllowLateJoinChanged(bool value)
        {
            if (_suppressCallbacks)
                return;

            _currentData.AllowLateJoin = value;
            NotifySettingsChanged();
        }

        private void NotifySettingsChanged()
        {
            OnSettingsChanged?.Invoke(_currentData.Clone());
        }

        #endregion

        #region Helpers

        private static void SetRowActive(GameObject row, bool active)
        {
            if (row != null)
                row.SetActive(active);
        }

        private static void SetInteractable(Selectable selectable, bool interactable)
        {
            if (selectable != null)
                selectable.interactable = interactable;
        }

        private static void SetInteractable(TMP_InputField input, bool interactable)
        {
            if (input != null)
                input.interactable = interactable;
        }

        private static void SetInteractable(TMP_Dropdown dropdown, bool interactable)
        {
            if (dropdown != null)
                dropdown.interactable = interactable;
        }

        #endregion
    }
}
