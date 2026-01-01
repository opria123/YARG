using System;

namespace YARG.Menu.Multiplayer.Settings
{
    /// <summary>
    /// Defines the editing mode for a SessionSettingsPanel.
    /// </summary>
    public enum SettingsPanelMode
    {
        /// <summary>
        /// Creating a new lobby - all settings editable.
        /// </summary>
        Create,

        /// <summary>
        /// Host editing an active session - some settings may be locked.
        /// </summary>
        HostEdit,

        /// <summary>
        /// Player viewing settings - all settings read-only.
        /// </summary>
        ViewOnly
    }

    /// <summary>
    /// Configuration for which settings are visible and editable in a SessionSettingsPanel.
    /// </summary>
    [Serializable]
    public class SettingsPanelConfig
    {
        // Visibility flags - whether to show these sections/fields
        public bool ShowLobbyName = true;
        public bool ShowMaxPlayers = true;
        public bool ShowPrivacy = true;
        public bool ShowSessionType = true;
        public bool ShowPassword = true;
        public bool ShowBandSize = true;
        public bool ShowNoFailMode = true;
        public bool ShowSharedSongsOnly = true;
        public bool ShowAllowModifiers = true;
        public bool ShowAllowedInstruments = true;
        public bool ShowLocalPlayersFirst = true;
        public bool ShowEnablePresetSync = true;
        public bool ShowAllowLateJoin = true;

        // Lockable flags - settings that can't be changed once a session starts
        // These will be disabled in HostEdit mode even for the host
        public bool LockLobbyNameInSession = true;
        public bool LockMaxPlayersInSession = true;
        public bool LockPrivacyInSession = true;
        public bool LockPasswordInSession = true;
        public bool LockBandSizeInSession = false;

        /// <summary>
        /// Default configuration showing all settings.
        /// </summary>
        public static SettingsPanelConfig Default => new SettingsPanelConfig();

        /// <summary>
        /// Configuration for the lobby browser create/edit forms.
        /// </summary>
        public static SettingsPanelConfig ForLobbyBrowser => new SettingsPanelConfig
        {
            ShowLobbyName = true,
            ShowMaxPlayers = true,
            ShowPrivacy = true,
            ShowSessionType = true,
            ShowPassword = true,
            ShowBandSize = true,
            ShowNoFailMode = true,
            ShowSharedSongsOnly = true,
            ShowAllowModifiers = true,
            ShowAllowedInstruments = true,
            ShowLocalPlayersFirst = true,
            ShowEnablePresetSync = true,
            ShowAllowLateJoin = true
        };

        /// <summary>
        /// Configuration for viewing/editing settings in an active lobby room.
        /// Shows lobby info as read-only, gameplay settings as editable (for host).
        /// </summary>
        public static SettingsPanelConfig ForLobbyRoom => new SettingsPanelConfig
        {
            // Lobby Info - visible but read-only (can't change once created)
            ShowLobbyName = true,
            ShowMaxPlayers = true,
            ShowPrivacy = true,
            ShowSessionType = false, // Don't show session type in active lobby (already determined)
            ShowPassword = true, // Show password field (read-only) when privacy is Private
            
            // Gameplay settings - editable by host
            ShowBandSize = true,
            ShowNoFailMode = true,
            ShowSharedSongsOnly = true,
            ShowAllowModifiers = true,
            ShowAllowedInstruments = true,
            ShowLocalPlayersFirst = true,
            
            // Session settings
            ShowEnablePresetSync = true,
            ShowAllowLateJoin = true,
            
            // Lock lobby info settings (they're read-only in an active session)
            LockLobbyNameInSession = true,
            LockMaxPlayersInSession = true,
            LockPrivacyInSession = true,
            LockPasswordInSession = true,
            
            // Gameplay settings are editable by host
            LockBandSizeInSession = false
        };

        /// <summary>
        /// Configuration for a compact view showing only key settings.
        /// </summary>
        public static SettingsPanelConfig Compact => new SettingsPanelConfig
        {
            ShowLobbyName = false,
            ShowMaxPlayers = true,
            ShowPrivacy = true,
            ShowSessionType = false,
            ShowPassword = false,
            ShowBandSize = true,
            ShowNoFailMode = true,
            ShowSharedSongsOnly = true,
            ShowAllowModifiers = true,
            ShowAllowedInstruments = false,
            ShowLocalPlayersFirst = false,
            ShowEnablePresetSync = false,
            ShowAllowLateJoin = false
        };
    }
}
