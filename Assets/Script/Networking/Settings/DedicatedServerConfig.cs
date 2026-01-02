using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using Newtonsoft.Json;
using UnityEngine;
using YARG.Playback;

namespace YARG.Networking.Settings
{
    /// <summary>
    /// Configuration for dedicated (headless) servers.
    /// Loaded exclusively from a JSON configuration file.
    /// </summary>
    [Serializable]
    public sealed class DedicatedServerConfig
    {
        public const string DefaultConfigFileName = "dedicated_server.json";
        public const string DefaultBanListFileName = "banned_ips.json";

        #region Config Sections

        /// <summary>
        /// Server connection and identity settings.
        /// </summary>
        [JsonProperty("server")]
        public ServerSection Server { get; set; } = new();

        /// <summary>
        /// Gameplay-related settings.
        /// </summary>
        [JsonProperty("gameplay")]
        public GameplaySection Gameplay { get; set; } = new();

        /// <summary>
        /// Timeout settings for idle and ready-up enforcement.
        /// </summary>
        [JsonProperty("timeouts")]
        public TimeoutsSection Timeouts { get; set; } = new();

        /// <summary>
        /// Player moderation settings.
        /// </summary>
        [JsonProperty("moderation")]
        public ModerationSection Moderation { get; set; } = new();

        /// <summary>
        /// Admin web interface settings.
        /// </summary>
        [JsonProperty("admin")]
        public AdminSection Admin { get; set; } = new();

        /// <summary>
        /// Lobby servers for lobby discovery and registration.
        /// </summary>
        [JsonProperty("lobbyServers")]
        public LobbyServersSection LobbyServers { get; set; } = new();

        #endregion

        #region Section Classes

        [Serializable]
        public sealed class ServerSection
        {
            /// <summary>
            /// Display name shown in lobby browsers.
            /// </summary>
            [JsonProperty("sessionName")]
            public string SessionName { get; set; } = "YARG Dedicated Server";

            /// <summary>
            /// Port to listen on for game connections.
            /// </summary>
            [JsonProperty("port")]
            public int Port { get; set; } = 9050;

            /// <summary>
            /// Maximum number of players allowed.
            /// </summary>
            [JsonProperty("maxPlayers")]
            public int MaxPlayers { get; set; } = 16;

            /// <summary>
            /// Password required to join (empty = no password).
            /// </summary>
            [JsonProperty("password")]
            public string Password { get; set; } = string.Empty;

            /// <summary>
            /// Privacy mode: "public", "private", or "unlisted".
            /// </summary>
            [JsonProperty("privacyMode")]
            public string PrivacyMode { get; set; } = "public";

            /// <summary>
            /// Whether the server should be visible on LAN discovery.
            /// </summary>
            [JsonProperty("visibleOnLan")]
            public bool VisibleOnLan { get; set; } = true;

            /// <summary>
            /// Whether to register with lobby servers for public discovery.
            /// </summary>
            [JsonProperty("registerWithLobbyServers")]
            public bool RegisterWithLobbyServers { get; set; } = true;
        }

        [Serializable]
        public sealed class GameplaySection
        {
            /// <summary>
            /// Band size (0 = bands disabled).
            /// </summary>
            [JsonProperty("bandSize")]
            public int BandSize { get; set; } = 0;

            /// <summary>
            /// Disable failing for all players.
            /// </summary>
            [JsonProperty("noFailMode")]
            public bool NoFailMode { get; set; } = false;

            /// <summary>
            /// Only show songs that all connected players have.
            /// Required for dedicated servers (server has no songs).
            /// </summary>
            [JsonProperty("sharedSongsOnly")]
            public bool SharedSongsOnly { get; set; } = true;

            /// <summary>
            /// Allow players to use modifiers.
            /// </summary>
            [JsonProperty("allowModifiers")]
            public bool AllowModifiers { get; set; } = true;

            /// <summary>
            /// Sync presets between players.
            /// </summary>
            [JsonProperty("enablePresetSync")]
            public bool EnablePresetSync { get; set; } = true;

            /// <summary>
            /// Allow players to join while a song is in progress.
            /// </summary>
            [JsonProperty("allowLateJoin")]
            public bool AllowLateJoin { get; set; } = true;

            /// <summary>
            /// Blocked game modes (by name: "FiveFretGuitar", "Drums", etc.).
            /// </summary>
            [JsonProperty("blockedGameModes")]
            public List<string> BlockedGameModes { get; set; } = new();
        }

        [Serializable]
        public sealed class TimeoutsSection
        {
            /// <summary>
            /// Kick the current host after this many minutes of inactivity.
            /// 0 = disabled.
            /// </summary>
            [JsonProperty("idleMinutes")]
            public int IdleMinutes { get; set; } = 30;

            /// <summary>
            /// Kick players who don't ready up within this many minutes.
            /// 0 = disabled.
            /// </summary>
            [JsonProperty("readyUpMinutes")]
            public int ReadyUpMinutes { get; set; } = 5;
        }

        [Serializable]
        public sealed class ModerationSection
        {
            /// <summary>
            /// Allow players to vote kick other players.
            /// </summary>
            [JsonProperty("voteToKickEnabled")]
            public bool VoteToKickEnabled { get; set; } = true;

            /// <summary>
            /// Fraction of players needed to kick (0.5 = majority).
            /// </summary>
            [JsonProperty("voteToKickThreshold")]
            public float VoteToKickThreshold { get; set; } = 0.5f;

            /// <summary>
            /// Path to the IP ban list JSON file (relative to config file location).
            /// </summary>
            [JsonProperty("banListPath")]
            public string BanListPath { get; set; } = DefaultBanListFileName;
        }

        [Serializable]
        public sealed class AdminSection
        {
            /// <summary>
            /// Port for the admin web interface.
            /// </summary>
            [JsonProperty("webPort")]
            public int WebPort { get; set; } = 8080;

            /// <summary>
            /// Username for admin authentication.
            /// </summary>
            [JsonProperty("username")]
            public string Username { get; set; } = "admin";

            /// <summary>
            /// Password for admin authentication.
            /// IMPORTANT: Change this from the default!
            /// </summary>
            [JsonProperty("password")]
            public string Password { get; set; } = "changeme";

            /// <summary>
            /// Allow admin access from non-localhost addresses.
            /// WARNING: Only enable this if you have proper network security!
            /// </summary>
            [JsonProperty("allowRemoteAccess")]
            public bool AllowRemoteAccess { get; set; } = false;
        }

        [Serializable]
        public sealed class LobbyServersSection
        {
            /// <summary>
            /// Whether to use the official YARG lobby server (https://lobby.yarg.in).
            /// </summary>
            [JsonProperty("useOfficial")]
            public bool UseOfficial { get; set; } = true;

            /// <summary>
            /// Additional custom lobby server URLs to register with.
            /// Each entry should be a full URL like "https://my-lobby-server.example.com".
            /// </summary>
            [JsonProperty("customUrls")]
            public List<string> CustomUrls { get; set; } = new();
        }

        #endregion

        #region Computed Properties

        /// <summary>
        /// Returns the privacy mode as a typed enum.
        /// </summary>
        [JsonIgnore]
        public SessionPrivacyMode PrivacyModeEnum
        {
            get
            {
                return Server.PrivacyMode?.ToLowerInvariant() switch
                {
                    "public" => SessionPrivacyMode.Public,
                    "private" => SessionPrivacyMode.Private,
                    "unlisted" => SessionPrivacyMode.Unlisted,
                    _ => SessionPrivacyMode.Public
                };
            }
        }

        /// <summary>
        /// Returns the privacy mode as LobbyPrivacyMode for networking.
        /// </summary>
        [JsonIgnore]
        public Abstraction.LobbyPrivacyMode LobbyPrivacyModeEnum
        {
            get
            {
                return Server.PrivacyMode?.ToLowerInvariant() switch
                {
                    "public" => Abstraction.LobbyPrivacyMode.Public,
                    "private" => Abstraction.LobbyPrivacyMode.Private,
                    "unlisted" => Abstraction.LobbyPrivacyMode.Unlisted,
                    _ => Abstraction.LobbyPrivacyMode.Public
                };
            }
        }

        /// <summary>
        /// Returns a list of LobbyServerEndpoint objects based on the config.
        /// Used by SessionLifecycleManager for registration.
        /// </summary>
        [JsonIgnore]
        public List<LobbyServerEndpoint> EnabledLobbyServers
        {
            get
            {
                var endpoints = new List<LobbyServerEndpoint>();

                // Add official YARG lobby server if enabled
                if (LobbyServers.UseOfficial)
                {
                    endpoints.Add(LobbyServerEndpoint.CreateYargOfficial());
                }

                // Add custom lobby servers
                if (LobbyServers.CustomUrls != null)
                {
                    int index = 0;
                    foreach (var url in LobbyServers.CustomUrls)
                    {
                        if (string.IsNullOrWhiteSpace(url))
                            continue;

                        endpoints.Add(new LobbyServerEndpoint
                        {
                            id = $"custom-{index++}",
                            displayName = $"Custom ({url})",
                            url = url.Trim(),
                            enabled = true,
                            isBuiltIn = false,
                            createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        });
                    }
                }

                return endpoints;
            }
        }

        #endregion

        #region Validation

        /// <summary>
        /// Validates and normalizes config values, returning a list of warnings.
        /// </summary>
        public List<string> Validate()
        {
            var warnings = new List<string>();

            // Server section
            Server.Port = Mathf.Clamp(Server.Port, 1, 65535);
            Server.MaxPlayers = Mathf.Clamp(Server.MaxPlayers, 2, 64);

            if (string.IsNullOrWhiteSpace(Server.SessionName))
            {
                Server.SessionName = "YARG Dedicated Server";
                warnings.Add("Empty session name, using default.");
            }

            // Validate privacy mode
            var validPrivacy = new[] { "public", "private", "unlisted" };
            if (!Array.Exists(validPrivacy, p => p.Equals(Server.PrivacyMode, StringComparison.OrdinalIgnoreCase)))
            {
                warnings.Add($"Invalid privacy mode '{Server.PrivacyMode}', defaulting to 'public'.");
                Server.PrivacyMode = "public";
            }

            // Private requires password
            if (Server.PrivacyMode.Equals("private", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(Server.Password))
            {
                Server.Password = Guid.NewGuid().ToString("N").Substring(0, 12);
                warnings.Add($"Private mode requires password. Generated: {Server.Password}");
            }

            // Gameplay section
            Gameplay.BandSize = Mathf.Clamp(Gameplay.BandSize, 0, 8);

            // Dedicated servers MUST use shared songs only (they have no local songs)
            if (!Gameplay.SharedSongsOnly)
            {
                Gameplay.SharedSongsOnly = true;
                warnings.Add("Dedicated servers require sharedSongsOnly=true (server has no songs).");
            }

            // Timeouts section
            Timeouts.IdleMinutes = Mathf.Max(0, Timeouts.IdleMinutes);
            Timeouts.ReadyUpMinutes = Mathf.Max(0, Timeouts.ReadyUpMinutes);

            // Moderation section
            Moderation.VoteToKickThreshold = Mathf.Clamp01(Moderation.VoteToKickThreshold);

            if (string.IsNullOrWhiteSpace(Moderation.BanListPath))
            {
                Moderation.BanListPath = DefaultBanListFileName;
            }

            // Admin section
            Admin.WebPort = Mathf.Clamp(Admin.WebPort, 1, 65535);

            if (string.IsNullOrWhiteSpace(Admin.Username))
            {
                Admin.Username = "admin";
                warnings.Add("Empty admin username, using 'admin'.");
            }

            if (Admin.Password == "changeme")
            {
                warnings.Add("WARNING: Using default admin password! Change 'admin.password' in config file.");
            }

            if (string.IsNullOrWhiteSpace(Admin.Password))
            {
                Admin.Password = Guid.NewGuid().ToString("N").Substring(0, 16);
                warnings.Add($"Empty admin password. Generated: {Admin.Password}");
            }

            if (Admin.AllowRemoteAccess)
            {
                warnings.Add("WARNING: Remote admin access is enabled. Ensure proper network security!");
            }

            return warnings;
        }

        #endregion

        #region Loading / Saving

        private static readonly JsonSerializerSettings _jsonSettings = new()
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Include,
            DefaultValueHandling = DefaultValueHandling.Include
        };

        /// <summary>
        /// The path this config was loaded from (for saving and resolving relative paths).
        /// </summary>
        [JsonIgnore]
        public string LoadedFromPath { get; private set; }

        /// <summary>
        /// Loads config from the default location.
        /// </summary>
        public static DedicatedServerConfig Load()
        {
            // Use command line path if specified, otherwise use default
            var configPath = !string.IsNullOrEmpty(CommandLineArgs.DedicatedServerConfigPath)
                ? CommandLineArgs.DedicatedServerConfigPath
                : GetDefaultConfigPath();
            
            return Load(configPath);
        }

        /// <summary>
        /// Loads config from a specific path.
        /// </summary>
        public static DedicatedServerConfig Load(string path)
        {
            DedicatedServerConfig config = null;

            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    config = JsonConvert.DeserializeObject<DedicatedServerConfig>(json, _jsonSettings);
                    Debug.Log($"[DedicatedServerConfig] Loaded config from: {path}");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[DedicatedServerConfig] Failed to parse config file: {ex.Message}");
                    Debug.LogWarning("[DedicatedServerConfig] Using default configuration.");
                }
            }
            else
            {
                Debug.LogWarning($"[DedicatedServerConfig] Config file not found: {path}");
                Debug.LogWarning("[DedicatedServerConfig] Creating default config file...");
            }

            config ??= new DedicatedServerConfig();
            config.LoadedFromPath = path;

            // Validate and log warnings
            var warnings = config.Validate();
            foreach (var warning in warnings)
            {
                Debug.LogWarning($"[DedicatedServerConfig] {warning}");
            }

            // Save config (creates default if missing, or updates with normalized values)
            config.Save();

            return config;
        }

        /// <summary>
        /// Saves this config to its original path.
        /// </summary>
        public void Save()
        {
            if (string.IsNullOrEmpty(LoadedFromPath))
            {
                LoadedFromPath = GetDefaultConfigPath();
            }
            Save(LoadedFromPath);
        }

        /// <summary>
        /// Saves this config to a specific path.
        /// </summary>
        public void Save(string path)
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonConvert.SerializeObject(this, _jsonSettings);
                File.WriteAllText(path, json);
                Debug.Log($"[DedicatedServerConfig] Saved config to: {path}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DedicatedServerConfig] Failed to save config: {ex.Message}");
            }
        }

        /// <summary>
        /// Creates a default config without loading from file.
        /// </summary>
        public static DedicatedServerConfig CreateDefault()
        {
            var config = new DedicatedServerConfig();
            config.Validate();
            return config;
        }

        /// <summary>
        /// Gets the default config file path.
        /// </summary>
        public static string GetDefaultConfigPath()
        {
            return Path.Combine(Application.persistentDataPath, DefaultConfigFileName);
        }

        /// <summary>
        /// Resolves a relative path from the config file's directory.
        /// </summary>
        public string ResolvePath(string relativePath)
        {
            if (Path.IsPathRooted(relativePath))
            {
                return relativePath;
            }

            var configDir = Path.GetDirectoryName(LoadedFromPath) ?? Application.persistentDataPath;
            return Path.Combine(configDir, relativePath);
        }

        /// <summary>
        /// Gets the resolved ban list file path.
        /// </summary>
        [JsonIgnore]
        public string ResolvedBanListPath => ResolvePath(Moderation.BanListPath);

        #endregion

        #region Session Preset Conversion

        /// <summary>
        /// Creates a SessionPreset from this config's settings.
        /// </summary>
        public SessionPreset ToSessionPreset()
        {
            return new SessionPreset
            {
                id = Guid.NewGuid().ToString("N"),
                presetName = Server.SessionName,
                sessionType = (int)SessionType.Server,
                createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),

                sessionName = Server.SessionName,
                port = Server.Port,
                password = Server.Password,
                privacyMode = (int)PrivacyModeEnum,

                maxPlayers = Server.MaxPlayers,
                bandSize = Gameplay.BandSize,

                visibleOnLan = Server.VisibleOnLan,
                registerWithLobbyServers = Server.RegisterWithLobbyServers,

                noFailMode = Gameplay.NoFailMode,
                sharedSongsOnly = Gameplay.SharedSongsOnly,
                allowModifiers = Gameplay.AllowModifiers,
                enablePresetSync = Gameplay.EnablePresetSync,
                allowLateJoin = Gameplay.AllowLateJoin
            };
        }

        #endregion
    }

    #region IP Ban List

    /// <summary>
    /// Manages a list of banned IP addresses for the dedicated server.
    /// Bans are IP-based to prevent players from rejoining with different profiles.
    /// </summary>
    [Serializable]
    public sealed class IpBanList
    {
        private static readonly JsonSerializerSettings _jsonSettings = new()
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore
        };

        /// <summary>
        /// List of banned IP entries.
        /// </summary>
        [JsonProperty("bannedIps")]
        public List<BannedIpEntry> BannedIps { get; set; } = new();

        /// <summary>
        /// File path this ban list was loaded from.
        /// </summary>
        [JsonIgnore]
        public string LoadedFromPath { get; private set; }

        /// <summary>
        /// Represents a single banned IP entry.
        /// </summary>
        [Serializable]
        public sealed class BannedIpEntry
        {
            /// <summary>
            /// The banned IP address.
            /// </summary>
            [JsonProperty("ip")]
            public string IpAddress { get; set; }

            /// <summary>
            /// The last known player name(s) from this IP (for reference).
            /// </summary>
            [JsonProperty("lastKnownNames")]
            public List<string> LastKnownNames { get; set; } = new();

            /// <summary>
            /// Reason for the ban (optional).
            /// </summary>
            [JsonProperty("reason")]
            public string Reason { get; set; }

            /// <summary>
            /// When the ban was created (Unix timestamp).
            /// </summary>
            [JsonProperty("bannedAt")]
            public long BannedAt { get; set; }

            /// <summary>
            /// Who banned this IP (admin username or "VoteKick").
            /// </summary>
            [JsonProperty("bannedBy")]
            public string BannedBy { get; set; }

            /// <summary>
            /// When the ban expires (Unix timestamp). 0 = permanent.
            /// </summary>
            [JsonProperty("expiresAt")]
            public long ExpiresAt { get; set; }

            /// <summary>
            /// Returns true if this ban has expired.
            /// </summary>
            [JsonIgnore]
            public bool IsExpired => ExpiresAt > 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() > ExpiresAt;
        }

        /// <summary>
        /// Checks if an IP address is banned.
        /// </summary>
        public bool IsBanned(string ipAddress)
        {
            if (string.IsNullOrWhiteSpace(ipAddress)) return false;

            // Normalize IP for comparison
            var normalized = NormalizeIp(ipAddress);

            foreach (var entry in BannedIps)
            {
                if (NormalizeIp(entry.IpAddress) == normalized && !entry.IsExpired)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Checks if an IP address is banned and returns the ban entry.
        /// </summary>
        public bool TryGetBan(string ipAddress, out BannedIpEntry entry)
        {
            entry = null;
            if (string.IsNullOrWhiteSpace(ipAddress)) return false;

            var normalized = NormalizeIp(ipAddress);

            foreach (var e in BannedIps)
            {
                if (NormalizeIp(e.IpAddress) == normalized && !e.IsExpired)
                {
                    entry = e;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Adds an IP to the ban list.
        /// </summary>
        public BannedIpEntry AddBan(string ipAddress, string playerName, string reason, string bannedBy, int durationMinutes = 0)
        {
            var normalized = NormalizeIp(ipAddress);

            // Remove any existing ban for this IP
            BannedIps.RemoveAll(e => NormalizeIp(e.IpAddress) == normalized);

            var entry = new BannedIpEntry
            {
                IpAddress = normalized,
                LastKnownNames = new List<string> { playerName },
                Reason = reason ?? "No reason provided",
                BannedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                BannedBy = bannedBy ?? "Admin",
                ExpiresAt = durationMinutes > 0
                    ? DateTimeOffset.UtcNow.AddMinutes(durationMinutes).ToUnixTimeSeconds()
                    : 0
            };

            BannedIps.Add(entry);
            Save();

            Debug.Log($"[IpBanList] Banned IP {normalized} (player: {playerName}, reason: {reason}, by: {bannedBy})");
            return entry;
        }

        /// <summary>
        /// Removes an IP from the ban list.
        /// </summary>
        public bool RemoveBan(string ipAddress)
        {
            var normalized = NormalizeIp(ipAddress);
            var removed = BannedIps.RemoveAll(e => NormalizeIp(e.IpAddress) == normalized);

            if (removed > 0)
            {
                Save();
                Debug.Log($"[IpBanList] Unbanned IP {normalized}");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Removes expired bans from the list.
        /// </summary>
        public int PurgeExpiredBans()
        {
            var removed = BannedIps.RemoveAll(e => e.IsExpired);
            if (removed > 0)
            {
                Save();
                Debug.Log($"[IpBanList] Purged {removed} expired ban(s)");
            }
            return removed;
        }

        /// <summary>
        /// Normalizes an IP address for consistent comparison.
        /// </summary>
        private static string NormalizeIp(string ipAddress)
        {
            if (string.IsNullOrWhiteSpace(ipAddress)) return string.Empty;

            // Remove port if present (e.g., "192.168.1.1:7777" -> "192.168.1.1")
            var colonIndex = ipAddress.LastIndexOf(':');
            if (colonIndex > 0)
            {
                // Check if it's IPv6 (multiple colons)
                if (ipAddress.IndexOf(':') != colonIndex)
                {
                    // IPv6 - leave as is for now
                }
                else
                {
                    ipAddress = ipAddress.Substring(0, colonIndex);
                }
            }

            // Try to parse and normalize
            if (IPAddress.TryParse(ipAddress, out var parsed))
            {
                return parsed.ToString();
            }

            return ipAddress.Trim().ToLowerInvariant();
        }

        #region Loading / Saving

        /// <summary>
        /// Loads ban list from a file path.
        /// </summary>
        public static IpBanList Load(string path)
        {
            IpBanList banList = null;

            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    banList = JsonConvert.DeserializeObject<IpBanList>(json, _jsonSettings);
                    Debug.Log($"[IpBanList] Loaded {banList?.BannedIps?.Count ?? 0} banned IPs from: {path}");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[IpBanList] Failed to parse ban list: {ex.Message}");
                }
            }

            banList ??= new IpBanList();
            banList.LoadedFromPath = path;
            banList.BannedIps ??= new List<BannedIpEntry>();

            // Purge expired bans on load
            banList.PurgeExpiredBans();

            return banList;
        }

        /// <summary>
        /// Saves the ban list to its original path.
        /// </summary>
        public void Save()
        {
            if (string.IsNullOrEmpty(LoadedFromPath))
            {
                Debug.LogWarning("[IpBanList] No path set for saving ban list.");
                return;
            }

            Save(LoadedFromPath);
        }

        /// <summary>
        /// Saves the ban list to a specific path.
        /// </summary>
        public void Save(string path)
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonConvert.SerializeObject(this, _jsonSettings);
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[IpBanList] Failed to save ban list: {ex.Message}");
            }
        }

        #endregion
    }

    #endregion
}
