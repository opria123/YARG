using System;
using System.Collections.Generic;
using System.Linq;
using YARG.Core;
using YARG.Networking.Abstraction;
using YARG.Networking.Bookmarks;
using YARG.Networking.Settings;

namespace YARG.Menu.Multiplayer.Settings
{
    /// <summary>
    /// UI data model for session settings. This is the common data structure
    /// that flows between UI components and can be converted to/from persistence models.
    /// </summary>
    [Serializable]
    public class SessionSettingsData
    {
        // Basic lobby info
        public string LobbyName;
        public int MaxPlayers;
        public LobbyPrivacyMode PrivacyMode;
        public string Password;
        
        // Session type (Automatic = Lobby with UPnP, Manual = Server with port forwarding)
        public SessionType SessionType;

        // Gameplay settings
        public int BandSize;
        public bool NoFailMode;
        public bool SharedSongsOnly;
        public bool AllowModifiers;

        // Session settings
        public bool EnablePresetSync;
        public bool AllowLateJoin;
        
        // Game mode restrictions - BLACKLIST (empty = all allowed, items in list are DISABLED)
        // Uses GameMode instead of Instrument to match profile settings
        public List<GameMode> AllowedGameModes;
        
        // Track ordering
        public bool LocalPlayersFirst;

        /// <summary>
        /// Whether the lobby is visible on LAN (derived from PrivacyMode).
        /// </summary>
        public bool VisibleOnLan => PrivacyMode != LobbyPrivacyMode.Unlisted;

        /// <summary>
        /// Whether the lobby registers with lobby servers (derived from PrivacyMode).
        /// </summary>
        public bool RegisterWithLobbyServers => PrivacyMode != LobbyPrivacyMode.Unlisted;
        
        /// <summary>
        /// Legacy property for compatibility - converts GameModes to Instruments.
        /// </summary>
        public List<Instrument> AllowedInstruments
        {
            get => AllowedGameModes?.SelectMany(GameModeToInstruments).Distinct().ToList() ?? new List<Instrument>();
            set => AllowedGameModes = value?.Select(InstrumentToGameMode).Distinct().ToList() ?? new List<GameMode>();
        }

        /// <summary>
        /// Creates a new SessionSettingsData with default values.
        /// </summary>
        public SessionSettingsData()
        {
            LobbyName = string.Empty;
            MaxPlayers = 4;
            PrivacyMode = LobbyPrivacyMode.Public;
            Password = string.Empty;
            SessionType = SessionType.Lobby; // Default to automatic (UPnP)
            BandSize = 0;
            NoFailMode = false;
            SharedSongsOnly = true;
            AllowModifiers = true;
            EnablePresetSync = true;
            AllowLateJoin = true;
            AllowedGameModes = new List<GameMode>(); // Empty = all allowed
            LocalPlayersFirst = false;
        }

        /// <summary>
        /// Creates a copy of this settings data.
        /// </summary>
        public SessionSettingsData Clone()
        {
            return new SessionSettingsData
            {
                LobbyName = LobbyName,
                MaxPlayers = MaxPlayers,
                PrivacyMode = PrivacyMode,
                Password = Password,
                SessionType = SessionType,
                BandSize = BandSize,
                NoFailMode = NoFailMode,
                SharedSongsOnly = SharedSongsOnly,
                AllowModifiers = AllowModifiers,
                EnablePresetSync = EnablePresetSync,
                AllowLateJoin = AllowLateJoin,
                AllowedGameModes = AllowedGameModes?.ToList() ?? new List<GameMode>(),
                LocalPlayersFirst = LocalPlayersFirst
            };
        }

        /// <summary>
        /// Creates SessionSettingsData from a HostedLobbyPreset.
        /// </summary>
        public static SessionSettingsData FromPreset(HostedLobbyPreset preset)
        {
            if (preset == null)
                return new SessionSettingsData();

            return new SessionSettingsData
            {
                LobbyName = preset.lobbyName ?? string.Empty,
                MaxPlayers = preset.maxPlayers,
                PrivacyMode = preset.PrivacyMode,
                Password = preset.password ?? string.Empty,
                SessionType = preset.SessionType,
                BandSize = preset.bandSize,
                NoFailMode = preset.noFailMode,
                SharedSongsOnly = preset.sharedSongsOnly,
                AllowModifiers = preset.allowModifiers,
                EnablePresetSync = preset.enablePresetSync,
                AllowLateJoin = preset.allowLateJoin,
                AllowedGameModes = preset.allowedInstruments?.Select(i => (GameMode)i).ToList() ?? new List<GameMode>(),
                LocalPlayersFirst = preset.localPlayersFirst
            };
        }

        /// <summary>
        /// Applies this settings data to a HostedLobbyPreset.
        /// </summary>
        public void ApplyToPreset(HostedLobbyPreset preset)
        {
            if (preset == null)
                return;

            preset.lobbyName = LobbyName ?? string.Empty;
            preset.maxPlayers = MaxPlayers;
            preset.PrivacyMode = PrivacyMode;
            preset.SessionType = SessionType;
            // Password rules:
            // - Private: always require password
            // - Unlisted + Server: password optional
            // - Unlisted + Lobby: no password (code is the secret)
            // - Public: no password
            bool canHavePassword = PrivacyMode == LobbyPrivacyMode.Private ||
                                   (PrivacyMode == LobbyPrivacyMode.Unlisted && SessionType == SessionType.Server);
            preset.password = canHavePassword ? (Password ?? string.Empty) : string.Empty;
            preset.bandSize = BandSize;
            preset.noFailMode = NoFailMode;
            preset.sharedSongsOnly = SharedSongsOnly;
            preset.allowModifiers = AllowModifiers;
            preset.enablePresetSync = EnablePresetSync;
            preset.allowLateJoin = AllowLateJoin;
            preset.allowedInstruments = AllowedGameModes?.Select(g => (int)g).ToList() ?? new List<int>();
            preset.localPlayersFirst = LocalPlayersFirst;
        }

        /// <summary>
        /// Checks if this settings data differs from another.
        /// </summary>
        public bool DiffersFrom(SessionSettingsData other)
        {
            if (other == null)
                return true;

            return !string.Equals(LobbyName, other.LobbyName, StringComparison.Ordinal)
                || MaxPlayers != other.MaxPlayers
                || PrivacyMode != other.PrivacyMode
                || !string.Equals(Password, other.Password, StringComparison.Ordinal)
                || SessionType != other.SessionType
                || BandSize != other.BandSize
                || NoFailMode != other.NoFailMode
                || SharedSongsOnly != other.SharedSongsOnly
                || AllowModifiers != other.AllowModifiers
                || EnablePresetSync != other.EnablePresetSync
                || AllowLateJoin != other.AllowLateJoin
                || LocalPlayersFirst != other.LocalPlayersFirst
                || !AllowedGameModesEqual(other.AllowedGameModes);
        }
        
        private bool AllowedGameModesEqual(List<GameMode> other)
        {
            if (AllowedGameModes == null && other == null) return true;
            if (AllowedGameModes == null || other == null) return false;
            if (AllowedGameModes.Count != other.Count) return false;
            return AllowedGameModes.OrderBy(i => i).SequenceEqual(other.OrderBy(i => i));
        }
        
        /// <summary>
        /// Checks if a specific game mode is allowed.
        /// Empty list means all game modes are allowed.
        /// The list is a BLACKLIST - game modes IN the list are DISABLED.
        /// </summary>
        public bool IsGameModeAllowed(GameMode gameMode)
        {
            if (AllowedGameModes == null || AllowedGameModes.Count == 0)
                return true;
            // Blacklist: game mode is allowed if it's NOT in the list
            return !AllowedGameModes.Contains(gameMode);
        }
        
        /// <summary>
        /// Toggles a game mode in the blacklist.
        /// Clicking a game mode adds it to the blacklist (disabled/greyed out).
        /// Clicking again removes it from the blacklist (enabled/allowed).
        /// </summary>
        public void ToggleGameMode(GameMode gameMode)
        {
            AllowedGameModes ??= new List<GameMode>();
            
            if (AllowedGameModes.Contains(gameMode))
            {
                // Currently in blacklist (disabled) -> remove to allow
                AllowedGameModes.Remove(gameMode);
            }
            else
            {
                // Currently not in blacklist (allowed) -> add to disable
                AllowedGameModes.Add(gameMode);
            }
        }
        
        /// <summary>
        /// Gets all game modes for the instrument picker.
        /// These match the input type categories used in player profiles.
        /// </summary>
        public static List<GameMode> GetAllGameModes()
        {
            return new List<GameMode>
            {
                GameMode.FiveFretGuitar, // Guitar controllers
                GameMode.FourLaneDrums,  // Drum kits
                GameMode.Vocals,         // Microphone
                GameMode.ProKeys         // Keyboard
            };
        }
        
        /// <summary>
        /// Maps a GameMode to its corresponding Instruments for filtering.
        /// </summary>
        private static IEnumerable<Instrument> GameModeToInstruments(GameMode gameMode)
        {
            switch (gameMode)
            {
                case GameMode.FiveFretGuitar:
                    return new[] { Instrument.FiveFretGuitar, Instrument.FiveFretBass, Instrument.FiveFretRhythm, Instrument.FiveFretCoopGuitar };
                case GameMode.SixFretGuitar:
                    return new[] { Instrument.SixFretGuitar, Instrument.SixFretBass, Instrument.SixFretRhythm, Instrument.SixFretCoopGuitar };
                case GameMode.FourLaneDrums:
                    return new[] { Instrument.FourLaneDrums, Instrument.ProDrums };
                case GameMode.FiveLaneDrums:
                    return new[] { Instrument.FiveLaneDrums };
                case GameMode.EliteDrums:
                    return new[] { Instrument.EliteDrums };
                case GameMode.ProGuitar:
                    return new[] { Instrument.ProGuitar_17Fret, Instrument.ProGuitar_22Fret, Instrument.ProBass_17Fret, Instrument.ProBass_22Fret };
                case GameMode.ProKeys:
                    return new[] { Instrument.Keys, Instrument.ProKeys };
                case GameMode.Vocals:
                    return new[] { Instrument.Vocals, Instrument.Harmony };
                default:
                    return Array.Empty<Instrument>();
            }
        }
        
        /// <summary>
        /// Maps an Instrument to its corresponding GameMode.
        /// </summary>
        private static GameMode InstrumentToGameMode(Instrument instrument)
        {
            switch (instrument)
            {
                case Instrument.FiveFretGuitar:
                case Instrument.FiveFretBass:
                case Instrument.FiveFretRhythm:
                case Instrument.FiveFretCoopGuitar:
                    return GameMode.FiveFretGuitar;
                case Instrument.SixFretGuitar:
                case Instrument.SixFretBass:
                case Instrument.SixFretRhythm:
                case Instrument.SixFretCoopGuitar:
                    return GameMode.SixFretGuitar;
                case Instrument.FourLaneDrums:
                case Instrument.ProDrums:
                    return GameMode.FourLaneDrums;
                case Instrument.FiveLaneDrums:
                    return GameMode.FiveLaneDrums;
                case Instrument.EliteDrums:
                    return GameMode.EliteDrums;
                case Instrument.ProGuitar_17Fret:
                case Instrument.ProGuitar_22Fret:
                case Instrument.ProBass_17Fret:
                case Instrument.ProBass_22Fret:
                    return GameMode.ProGuitar;
                case Instrument.Keys:
                case Instrument.ProKeys:
                    return GameMode.ProKeys;
                case Instrument.Vocals:
                case Instrument.Harmony:
                    return GameMode.Vocals;
                default:
                    return GameMode.FiveFretGuitar;
            }
        }
    }
}
