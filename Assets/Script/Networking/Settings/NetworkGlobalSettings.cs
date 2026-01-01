using System;
using System.Collections.Generic;
using System.Linq;

namespace YARG.Networking.Settings
{
    /// <summary>
    /// Global network settings that apply across all sessions.
    /// Persisted to network_settings.json.
    /// </summary>
    [Serializable]
    public sealed class NetworkGlobalSettings
    {
        /// <summary>
        /// List of introducer endpoints for lobby discovery and registration.
        /// </summary>
        public List<IntroducerEndpoint> introducers = new();

        /// <summary>
        /// Default port for hosting sessions.
        /// </summary>
        public int defaultPort = 9050;

        /// <summary>
        /// When true, local players' tracks are displayed first in the track order.
        /// Can be overridden per-session in SessionPreset.
        /// </summary>
        public bool localPlayersFirst = true;

        /// <summary>
        /// Snapshot of the last used session settings for quick "New Lobby" creation.
        /// Auto-updated when hosting a session.
        /// </summary>
        public LastUsedSessionSettings lastUsedSettings = new();

        /// <summary>
        /// Returns enabled introducers only.
        /// </summary>
        public IReadOnlyList<IntroducerEndpoint> EnabledIntroducers =>
            introducers?.Where(i => i.enabled).ToList() ?? new List<IntroducerEndpoint>();

        /// <summary>
        /// Gets the first enabled introducer, or null if none are enabled.
        /// </summary>
        public IntroducerEndpoint GetFirstEnabledIntroducer() =>
            introducers?.FirstOrDefault(i => i.enabled);

        /// <summary>
        /// Gets the YARG Official introducer, or null if removed.
        /// </summary>
        public IntroducerEndpoint YargOfficialIntroducer =>
            introducers?.FirstOrDefault(i => i.isBuiltIn && i.id == "yarg-official");

        /// <summary>
        /// Ensures the settings have valid default values.
        /// </summary>
        public void EnsureDefaults()
        {
            introducers ??= new List<IntroducerEndpoint>();
            lastUsedSettings ??= new LastUsedSessionSettings();

            // Ensure YARG Official introducer exists
            if (!introducers.Any(i => i.isBuiltIn && i.id == "yarg-official"))
            {
                introducers.Insert(0, IntroducerEndpoint.CreateYargOfficial());
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // In development, add local introducer for testing if not present
            // Insert at position 0 so it's the primary (first enabled) introducer
            if (!introducers.Any(i => i.id == "local-dev"))
            {
                var localDev = IntroducerEndpoint.CreateLocalDev();
                introducers.Insert(0, localDev);
                UnityEngine.Debug.Log($"[NetworkGlobalSettings] Added local-dev introducer at position 0: {localDev.url}");
            }
#endif

            // Validate port
            if (defaultPort < 1 || defaultPort > 65535)
            {
                defaultPort = 9050;
            }
        }

        /// <summary>
        /// Adds a new custom introducer endpoint.
        /// </summary>
        public IntroducerEndpoint AddIntroducer(string displayName, string url)
        {
            var endpoint = new IntroducerEndpoint
            {
                displayName = displayName?.Trim() ?? "Custom Introducer",
                url = url?.Trim() ?? string.Empty,
                enabled = true,
                isBuiltIn = false
            };
            endpoint.EnsureIdentifiers();

            introducers ??= new List<IntroducerEndpoint>();
            introducers.Add(endpoint);

            return endpoint;
        }

        /// <summary>
        /// Removes a custom introducer endpoint by ID.
        /// Built-in introducers cannot be removed, only disabled.
        /// </summary>
        public bool RemoveIntroducer(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return false;

            var endpoint = introducers?.FirstOrDefault(i => i.id == id);
            if (endpoint == null)
                return false;

            if (endpoint.isBuiltIn)
                return false; // Cannot remove built-in

            return introducers.Remove(endpoint);
        }

        /// <summary>
        /// Enables or disables an introducer by ID.
        /// </summary>
        public bool SetIntroducerEnabled(string id, bool enabled)
        {
            var endpoint = introducers?.FirstOrDefault(i => i.id == id);
            if (endpoint == null)
                return false;

            endpoint.enabled = enabled;
            return true;
        }

        /// <summary>
        /// Updates the last used settings from a session preset.
        /// </summary>
        public void UpdateLastUsedFromPreset(SessionPreset preset)
        {
            if (preset == null)
                return;

            lastUsedSettings ??= new LastUsedSessionSettings();
            lastUsedSettings.UpdateFrom(preset);
        }

        /// <summary>
        /// Creates default global settings.
        /// </summary>
        public static NetworkGlobalSettings CreateDefault()
        {
            var settings = new NetworkGlobalSettings();
            settings.EnsureDefaults();
            return settings;
        }
    }

    /// <summary>
    /// Snapshot of session settings for quick restoration.
    /// Auto-updated when a session is hosted.
    /// </summary>
    [Serializable]
    public sealed class LastUsedSessionSettings
    {
        public string sessionName = "YARG Session";
        public int maxPlayers = 4;
        public int bandSize = 0;
        public int privacyMode = (int)SessionPrivacyMode.Public;
        public bool noFailMode;
        public bool sharedSongsOnly = true;
        public bool enablePresetSync = true;
        public bool allowLateJoin = true;
        public bool visibleOnLan = true;
        public bool registerWithIntroducers = true;

        /// <summary>
        /// Updates this snapshot from a session preset.
        /// </summary>
        public void UpdateFrom(SessionPreset preset)
        {
            if (preset == null)
                return;

            sessionName = preset.sessionName;
            maxPlayers = preset.maxPlayers;
            bandSize = preset.bandSize;
            privacyMode = preset.privacyMode;
            noFailMode = preset.noFailMode;
            sharedSongsOnly = preset.sharedSongsOnly;
            enablePresetSync = preset.enablePresetSync;
            allowLateJoin = preset.allowLateJoin;
            visibleOnLan = preset.visibleOnLan;
            registerWithIntroducers = preset.registerWithIntroducers;
        }

        /// <summary>
        /// Applies this snapshot to a session preset.
        /// </summary>
        public void ApplyTo(SessionPreset preset)
        {
            if (preset == null)
                return;

            preset.sessionName = sessionName;
            preset.maxPlayers = maxPlayers;
            preset.bandSize = bandSize;
            preset.privacyMode = privacyMode;
            preset.noFailMode = noFailMode;
            preset.sharedSongsOnly = sharedSongsOnly;
            preset.enablePresetSync = enablePresetSync;
            preset.allowLateJoin = allowLateJoin;
            preset.visibleOnLan = visibleOnLan;
            preset.registerWithIntroducers = registerWithIntroducers;
        }
    }
}
