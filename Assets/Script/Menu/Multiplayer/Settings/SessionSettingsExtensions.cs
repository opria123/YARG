using YARG.Networking.Bookmarks;

namespace YARG.Menu.Multiplayer.Settings
{
    /// <summary>
    /// Extension methods for converting between settings data types.
    /// </summary>
    public static class SessionSettingsExtensions
    {
        /// <summary>
        /// Creates a SessionSettingsData from this preset.
        /// </summary>
        public static SessionSettingsData ToSettingsData(this HostedLobbyPreset preset)
        {
            return SessionSettingsData.FromPreset(preset);
        }

        /// <summary>
        /// Updates the preset with values from the settings data.
        /// </summary>
        public static void UpdateFromSettingsData(this HostedLobbyPreset preset, SessionSettingsData data)
        {
            data?.ApplyToPreset(preset);
        }

        /// <summary>
        /// Creates a new HostedLobbyPreset from this settings data.
        /// </summary>
        public static HostedLobbyPreset ToNewPreset(this SessionSettingsData data)
        {
            if (data == null)
                return new HostedLobbyPreset();

            var preset = new HostedLobbyPreset();
            data.ApplyToPreset(preset);
            preset.EnsureIdentifiers();
            return preset;
        }
    }
}
