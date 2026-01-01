using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using YARG.Core;

namespace YARG.Networking.Settings
{
    /// <summary>
    /// Stores a reusable session configuration (Server or Lobby).
    /// These presets are surfaced in the "My Lobbies" section of the multiplayer menu.
    /// </summary>
    [Serializable]
    public sealed class SessionPreset
    {
        #region Identity

        /// <summary>
        /// Unique identifier for this preset.
        /// </summary>
        public string id = string.Empty;

        /// <summary>
        /// Display name for this preset in the "My Lobbies" list.
        /// </summary>
        public string presetName = string.Empty;

        /// <summary>
        /// The type of session this preset creates.
        /// </summary>
        public int sessionType = (int)SessionType.Lobby;

        /// <summary>
        /// When this preset was created (Unix timestamp).
        /// </summary>
        public long createdAt;

        /// <summary>
        /// When this preset was last used to host (Unix timestamp).
        /// </summary>
        public long lastHostedAt;

        #endregion

        #region Core Settings

        /// <summary>
        /// Display name shown in browsers (not the IP address).
        /// </summary>
        public string sessionName = "YARG Session";

        /// <summary>
        /// Port to listen on for incoming connections.
        /// </summary>
        public int port = 9050;

        /// <summary>
        /// Password required to join (empty = no password).
        /// </summary>
        public string password = string.Empty;

        /// <summary>
        /// Privacy/visibility mode for this session.
        /// </summary>
        public int privacyMode = (int)SessionPrivacyMode.Public;

        #endregion

        #region Player Limits

        /// <summary>
        /// Maximum number of players allowed (2-64).
        /// </summary>
        public int maxPlayers = 4;

        /// <summary>
        /// Number of players per band (2-8). Set to 0 to disable bands.
        /// When player count exceeds this, players are split into competing bands.
        /// </summary>
        public int bandSize = 0;

        #endregion

        #region Discovery

        /// <summary>
        /// Whether this session is visible in LAN discovery.
        /// </summary>
        public bool visibleOnLan = true;

        /// <summary>
        /// Whether this session should be registered with enabled introducers.
        /// For Lobbies: Required for lobby code generation.
        /// For Servers: Optional, allows appearing in introducer browser.
        /// </summary>
        public bool registerWithIntroducers = true;

        #endregion

        #region Gameplay Rules

        /// <summary>
        /// When enabled, players cannot fail out. Applies to all players in the session.
        /// </summary>
        public bool noFailMode;

        /// <summary>
        /// Only show songs that all players have and can play (have parts for their instruments).
        /// </summary>
        public bool sharedSongsOnly = true;
        
        /// <summary>
        /// When true, players can use modifiers. When false, all modifiers are disabled.
        /// </summary>
        public bool allowModifiers = true;

        /// <summary>
        /// Sync camera and color presets so players can see each other's track backgrounds and settings.
        /// </summary>
        public bool enablePresetSync = true;

        /// <summary>
        /// Allow players to join while a song is in progress.
        /// </summary>
        public bool allowLateJoin = true;

        /// <summary>
        /// List of allowed instruments. Empty list = all instruments allowed.
        /// Players with instruments not in this list cannot join.
        /// </summary>
        public List<int> allowedInstruments = new();

        #endregion

        #region Track Display

        /// <summary>
        /// Override global "local players first" track ordering setting.
        /// null = use global setting, true = local first, false = use session order.
        /// </summary>
        public bool? localPlayersFirst;

        // Note: For serialization, we need to handle nullable bool specially
        public bool hasLocalPlayersFirstOverride;
        public bool localPlayersFirstValue;

        #endregion

        #region Computed Properties

        /// <summary>
        /// Returns the session type as the strongly-typed enum.
        /// </summary>
        public SessionType SessionType
        {
            get => (SessionType)Mathf.Clamp(sessionType, 0, 1);
            set => sessionType = (int)value;
        }

        /// <summary>
        /// Returns the privacy mode as the strongly-typed enum.
        /// </summary>
        public SessionPrivacyMode PrivacyMode
        {
            get => (SessionPrivacyMode)Mathf.Clamp(privacyMode, 0, 2);
            set => privacyMode = (int)value;
        }

        /// <summary>
        /// Returns true if a password is required to join.
        /// </summary>
        public bool HasPassword => !string.IsNullOrEmpty(password);

        /// <summary>
        /// Returns true if band mode is enabled.
        /// </summary>
        public bool BandsEnabled => bandSize >= 2;

        /// <summary>
        /// Gets the band size (players per band). 0 = disabled.
        /// </summary>
        public int BandSize => bandSize;

        /// <summary>
        /// Gets whether no-fail mode is enabled.
        /// </summary>
        public bool NoFailMode => noFailMode;

        /// <summary>
        /// Gets whether only shared songs should be shown.
        /// </summary>
        public bool SharedSongsOnly => sharedSongsOnly;

        /// <summary>
        /// Gets whether modifiers are allowed.
        /// </summary>
        public bool AllowModifiers => allowModifiers;

        /// <summary>
        /// Gets whether local players should be displayed first in track order.
        /// </summary>
        public bool LocalPlayersFirst => localPlayersFirstValue;

        /// <summary>
        /// Gets the allowed instruments array. Null or empty = all allowed.
        /// </summary>
        public int[] AllowedInstruments => allowedInstruments?.ToArray() ?? Array.Empty<int>();

        /// <summary>
        /// Gets the allowed instruments as typed enums.
        /// Returns empty if all instruments are allowed.
        /// </summary>
        public IReadOnlyList<Instrument> AllowedInstrumentsTyped
        {
            get
            {
                if (allowedInstruments == null || allowedInstruments.Count == 0)
                    return Array.Empty<Instrument>();

                return allowedInstruments
                    .Where(i => Enum.IsDefined(typeof(Instrument), (byte)i))
                    .Select(i => (Instrument)(byte)i)
                    .ToList();
            }
        }

        /// <summary>
        /// Gets or sets the nullable localPlayersFirst override.
        /// </summary>
        public bool? LocalPlayersFirstOverride
        {
            get => hasLocalPlayersFirstOverride ? localPlayersFirstValue : null;
            set
            {
                hasLocalPlayersFirstOverride = value.HasValue;
                localPlayersFirstValue = value ?? false;
            }
        }

        #endregion

        #region Methods

        public SessionPreset Clone()
        {
            return new SessionPreset
            {
                id = id,
                presetName = presetName,
                sessionType = sessionType,
                createdAt = createdAt,
                lastHostedAt = lastHostedAt,

                sessionName = sessionName,
                port = port,
                password = password,
                privacyMode = privacyMode,

                maxPlayers = maxPlayers,
                bandSize = bandSize,

                visibleOnLan = visibleOnLan,
                registerWithIntroducers = registerWithIntroducers,

                noFailMode = noFailMode,
                sharedSongsOnly = sharedSongsOnly,
                allowModifiers = allowModifiers,
                enablePresetSync = enablePresetSync,
                allowLateJoin = allowLateJoin,
                allowedInstruments = allowedInstruments?.ToList() ?? new List<int>(),

                hasLocalPlayersFirstOverride = hasLocalPlayersFirstOverride,
                localPlayersFirstValue = localPlayersFirstValue
            };
        }

        public void EnsureIdentifiers()
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                id = Guid.NewGuid().ToString("N");
            }

            if (createdAt <= 0)
            {
                createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }
        }

        public void TouchHostedTimestamp()
        {
            lastHostedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        /// <summary>
        /// Validates and normalizes the preset values.
        /// </summary>
        public void Normalize()
        {
            maxPlayers = Mathf.Clamp(maxPlayers, 2, 64);
            bandSize = Mathf.Clamp(bandSize, 0, 8);
            port = Mathf.Clamp(port, 1, 65535);
            privacyMode = Mathf.Clamp(privacyMode, 0, 2);
            sessionType = Mathf.Clamp(sessionType, 0, 1);

            if (string.IsNullOrWhiteSpace(sessionName))
            {
                sessionName = "YARG Session";
            }

            if (string.IsNullOrWhiteSpace(presetName))
            {
                presetName = sessionName;
            }

            allowedInstruments ??= new List<int>();
        }

        /// <summary>
        /// Checks if a given instrument is allowed in this session.
        /// </summary>
        public bool IsInstrumentAllowed(Instrument instrument)
        {
            if (allowedInstruments == null || allowedInstruments.Count == 0)
                return true; // All allowed

            return allowedInstruments.Contains((int)(byte)instrument);
        }

        /// <summary>
        /// Sets the allowed instruments from typed enum values.
        /// Pass empty list or null to allow all instruments.
        /// </summary>
        public void SetAllowedInstruments(IEnumerable<Instrument> instruments)
        {
            allowedInstruments = instruments?.Select(i => (int)(byte)i).ToList() ?? new List<int>();
        }

        /// <summary>
        /// Creates a default preset for a new lobby.
        /// </summary>
        public static SessionPreset CreateDefaultLobby(string playerName = null)
        {
            string name = string.IsNullOrWhiteSpace(playerName)
                ? "YARG Session"
                : $"{playerName}'s Lobby";

            return new SessionPreset
            {
                id = Guid.NewGuid().ToString("N"),
                presetName = "New Lobby",
                sessionType = (int)SessionType.Lobby,
                createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),

                sessionName = name,
                port = 9050,
                password = string.Empty,
                privacyMode = (int)SessionPrivacyMode.Public,

                maxPlayers = 4,
                bandSize = 0,

                visibleOnLan = true,
                registerWithIntroducers = true,

                noFailMode = false,
                sharedSongsOnly = true,
                enablePresetSync = true,
                allowLateJoin = true,
                allowedInstruments = new List<int>()
            };
        }

        /// <summary>
        /// Creates a default preset for a new server.
        /// </summary>
        public static SessionPreset CreateDefaultServer(string playerName = null)
        {
            string name = string.IsNullOrWhiteSpace(playerName)
                ? "YARG Server"
                : $"{playerName}'s Server";

            return new SessionPreset
            {
                id = Guid.NewGuid().ToString("N"),
                presetName = "New Server",
                sessionType = (int)SessionType.Server,
                createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),

                sessionName = name,
                port = 9050,
                password = string.Empty,
                privacyMode = (int)SessionPrivacyMode.Public,

                maxPlayers = 4,
                bandSize = 0,

                visibleOnLan = true,
                registerWithIntroducers = false, // Off by default for servers

                noFailMode = false,
                sharedSongsOnly = true,
                enablePresetSync = true,
                allowLateJoin = true,
                allowedInstruments = new List<int>()
            };
        }

        #endregion
    }
}
