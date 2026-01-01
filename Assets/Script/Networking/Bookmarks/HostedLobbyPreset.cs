using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using YARG.Networking.Abstraction;
using YARG.Networking.Settings;

namespace YARG.Networking.Bookmarks
{
    /// <summary>
    /// Stores a reusable lobby configuration created by the local player.
    /// These presets are surfaced in the "My Lobbies" section of the browser.
    /// </summary>
    [Serializable]
    public sealed class HostedLobbyPreset
    {
        public string id = string.Empty;
        public string lobbyName = string.Empty;
        public int maxPlayers = 4;
        public int privacyMode = (int)LobbyPrivacyMode.Public;
        public string password = string.Empty;
        public long createdAt;
        public long lastHostedAt;
        
        // Session type: Lobby (automatic UPnP) or Server (manual port forward)
        // Default to Server for backward compatibility with presets saved before lobby support
        public int sessionType = (int)SessionType.Server;

        // Extended gameplay settings
        public int bandSize = 0;
        public bool noFailMode = false;
        public bool sharedSongsOnly = true;
        public bool allowModifiers = true;
        
        // Additional gameplay settings
        public bool enablePresetSync = true;
        public bool allowLateJoin = true;
        
        // Instrument restrictions (empty = all allowed, stores Instrument enum as int)
        public List<int> allowedInstruments = new List<int>();
        
        // Track ordering
        public bool localPlayersFirst = false;

        /// <summary>
        /// Returns the preset's privacy mode as the strongly-typed enum.
        /// </summary>
        public LobbyPrivacyMode PrivacyMode
        {
            get
            {
                var value = Mathf.Clamp(privacyMode, 0, Enum.GetValues(typeof(LobbyPrivacyMode)).Length - 1);
                return (LobbyPrivacyMode)value;
            }
            set => privacyMode = (int)value;
        }
        
        /// <summary>
        /// Returns the preset's session type as the strongly-typed enum.
        /// </summary>
        public SessionType SessionType
        {
            get
            {
                var value = Mathf.Clamp(sessionType, 0, 1);
                return (SessionType)value;
            }
            set => sessionType = (int)value;
        }

        /// <summary>
        /// Gets whether band mode is enabled.
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
        /// Gets whether this lobby is visible in LAN discovery.
        /// Derived from privacy mode - false for Unlisted, true otherwise.
        /// </summary>
        public bool VisibleOnLan => PrivacyMode != LobbyPrivacyMode.Unlisted;

        /// <summary>
        /// Gets whether this lobby should register with introducers.
        /// Derived from privacy mode - false for Unlisted, true otherwise.
        /// </summary>
        public bool RegisterWithIntroducers => PrivacyMode != LobbyPrivacyMode.Unlisted;

        /// <summary>
        /// Gets whether preset sync (camera/colors) is enabled.
        /// </summary>
        public bool EnablePresetSync => enablePresetSync;

        /// <summary>
        /// Gets whether late joining is allowed.
        /// </summary>
        public bool AllowLateJoin => allowLateJoin;
        
        /// <summary>
        /// Gets the allowed instruments list. Empty = all allowed.
        /// </summary>
        public List<int> AllowedInstruments => allowedInstruments ?? new List<int>();
        
        /// <summary>
        /// Gets whether local players should be displayed first in track ordering.
        /// </summary>
        public bool LocalPlayersFirst => localPlayersFirst;

        public HostedLobbyPreset Clone()
        {
            return new HostedLobbyPreset
            {
                id = id,
                lobbyName = lobbyName,
                maxPlayers = maxPlayers,
                privacyMode = privacyMode,
                password = password,
                createdAt = createdAt,
                lastHostedAt = lastHostedAt,
                sessionType = sessionType,
                bandSize = bandSize,
                noFailMode = noFailMode,
                sharedSongsOnly = sharedSongsOnly,
                allowModifiers = allowModifiers,
                enablePresetSync = enablePresetSync,
                allowLateJoin = allowLateJoin,
                allowedInstruments = allowedInstruments?.ToList() ?? new List<int>(),
                localPlayersFirst = localPlayersFirst
            };
        }

        public void TouchHostedTimestamp()
        {
            lastHostedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
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
    }
}
