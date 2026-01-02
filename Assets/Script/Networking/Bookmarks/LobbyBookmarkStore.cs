using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using YARG.Networking.Abstraction;
using YARG.Networking.Settings;

namespace YARG.Networking.Bookmarks
{
    /// <summary>
    /// Persists lobby bookmarks (favorites + recents) to disk.
    /// </summary>
    public sealed class LobbyBookmarkStore
    {
        private const string StorageFileName = "lobby_bookmarks.json";
        private const int MaxRecentEntries = 25;

        private static LobbyBookmarkStore _instance;

        private readonly List<LobbyBookmark> _favorites = new();
        private readonly List<LobbyBookmark> _recents = new();
        private readonly List<HostedLobbyPreset> _myLobbies = new();
        private readonly List<SessionPreset> _sessionPresets = new();

        private readonly string _storagePath;

        public static LobbyBookmarkStore Instance => _instance ??= new LobbyBookmarkStore();

        public IReadOnlyList<LobbyBookmark> Favorites => _favorites;
        public IReadOnlyList<LobbyBookmark> Recents => _recents;
        /// <summary>
        /// Legacy lobby presets. Use SessionPresets for new code.
        /// </summary>
        [Obsolete("Use SessionPresets instead")]
        public IReadOnlyList<HostedLobbyPreset> MyLobbies => _myLobbies;
        /// <summary>
        /// New session presets (Server and Lobby configurations).
        /// </summary>
        public IReadOnlyList<SessionPreset> SessionPresets => _sessionPresets;

        public event Action Changed;

        private LobbyBookmarkStore()
        {
            _storagePath = Path.Combine(Application.persistentDataPath, StorageFileName);
            Load();
        }

        public bool IsFavorite(string address, int port)
        {
            return _favorites.Any(entry => LobbyBookmarkUtility.Matches(address, port, entry));
        }

        public LobbyBookmark GetFavorite(string address, int port)
        {
            return _favorites.FirstOrDefault(entry => LobbyBookmarkUtility.Matches(address, port, entry));
        }

        public LobbyBookmark GetRecent(string address, int port)
        {
            return _recents.FirstOrDefault(entry => LobbyBookmarkUtility.Matches(address, port, entry));
        }

        public void ToggleFavorite(LobbyBookmark bookmark)
        {
            if (bookmark == null)
            {
                return;
            }

            if (IsFavorite(bookmark.address, bookmark.port))
            {
                RemoveFavorite(bookmark.address, bookmark.port);
                return;
            }

            AddFavorite(bookmark.address, bookmark.port, bookmark.displayName, bookmark.password);
        }

        public void AddFavorite(string address, int port, string displayName, string password)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                return;
            }

            var existing = GetFavorite(address, port);
            if (existing != null)
            {
                if (!existing.displayNamePinned && !string.IsNullOrWhiteSpace(displayName))
                {
                    existing.displayName = displayName.Trim();
                }
                existing.password = password ?? string.Empty;
                existing.lastConnected = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }
            else
            {
                _favorites.Add(new LobbyBookmark
                {
                    address = address.Trim(),
                    port = port,
                    displayName = string.IsNullOrWhiteSpace(displayName) ? address : displayName.Trim(),
                    password = password ?? string.Empty,
                    favorite = true,
                    createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    lastConnected = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    displayNamePinned = false
                });
            }

            Save();
            Changed?.Invoke();
            // Keep favorites sorted by when they were added (most recent first)
            SortFavorites();
        }

        public void RemoveFavorite(string address, int port)
        {
            // Demote favorite to recents instead of deleting so user still has the entry available
            DemoteFavoriteToRecent(address, port);
        }

        private void DemoteFavoriteToRecent(string address, int port)
        {
            var fav = GetFavorite(address, port);
            if (fav == null)
                return;

            // Create or update recent entry from favorite
            var existingRecent = GetRecent(address, port);
            if (existingRecent != null)
            {
                if (!existingRecent.displayNamePinned && !string.IsNullOrWhiteSpace(fav.displayName))
                {
                    existingRecent.displayName = fav.displayName;
                }
                existingRecent.displayNamePinned = existingRecent.displayNamePinned || fav.displayNamePinned;
                existingRecent.password = fav.password ?? existingRecent.password;
                existingRecent.lastConnected = fav.lastConnected;
            }
            else
            {
                var recent = new LobbyBookmark
                {
                    address = fav.address,
                    port = fav.port,
                    displayName = string.IsNullOrWhiteSpace(fav.displayName) ? fav.address : fav.displayName,
                    password = fav.password ?? string.Empty,
                    favorite = false,
                    createdAt = fav.createdAt,
                    lastConnected = fav.lastConnected,
                    displayNamePinned = fav.displayNamePinned
                };

                _recents.Insert(0, recent);
            }

            // Remove favorite entry
            bool removed = _favorites.RemoveAll(entry => LobbyBookmarkUtility.Matches(address, port, entry)) > 0;

            // Ensure recents list size cap and sorting
            if (_recents.Count > MaxRecentEntries)
            {
                _recents.RemoveRange(MaxRecentEntries, _recents.Count - MaxRecentEntries);
            }

            SortRecents();
            SortFavorites();

            if (removed)
            {
                Save();
                Changed?.Invoke();
            }
        }

        public void UpdateBookmark(LobbyBookmark bookmark, string displayName, string address, int port, string password)
        {
            if (bookmark == null)
            {
                return;
            }

            string trimmedAddress = string.IsNullOrWhiteSpace(address) ? bookmark.address : address.Trim();
            if (string.IsNullOrWhiteSpace(trimmedAddress))
            {
                return;
            }

            int fallbackPort = bookmark.port > 0 ? bookmark.port : NetworkTransportDefaults.DefaultUdpPort;
            int normalizedPort = Math.Clamp(port > 0 ? port : fallbackPort, 1, ushort.MaxValue);
            string normalizedName = string.IsNullOrWhiteSpace(displayName) ? trimmedAddress : displayName.Trim();
            string normalizedPassword = password ?? string.Empty;

            string originalKey = bookmark.EndpointKey;

            bookmark.displayNamePinned = true;

            bool changed = false;

            changed |= ApplyBookmarkUpdate(_favorites, originalKey, normalizedName, trimmedAddress, normalizedPort, normalizedPassword, true, true);
            changed |= ApplyBookmarkUpdate(_recents, originalKey, normalizedName, trimmedAddress, normalizedPort, normalizedPassword, false, true);

            if (changed)
            {
                Save();
                Changed?.Invoke();
                // Keep lists sorted after an update
                SortRecents();
                SortFavorites();
            }
        }

        public void RecordConnection(string address, int port, string displayName, string password)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                return;
            }

            if (port <= 0)
            {
                port = NetworkTransportDefaults.DefaultTcpPort;
            }

            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var key = LobbyBookmarkUtility.BuildKey(address, port);

            // Update recents
            var existingRecent = _recents.FirstOrDefault(entry => entry.EndpointKey == key);
            if (existingRecent != null)
            {
                if (!existingRecent.displayNamePinned && !string.IsNullOrWhiteSpace(displayName))
                {
                    existingRecent.displayName = displayName.Trim();
                }
                existingRecent.password = password ?? existingRecent.password;
                existingRecent.lastConnected = timestamp;
            }
            else
            {
                var recent = new LobbyBookmark
                {
                    address = address.Trim(),
                    port = port,
                    displayName = string.IsNullOrWhiteSpace(displayName) ? address : displayName.Trim(),
                    password = password ?? string.Empty,
                    favorite = false,
                    createdAt = timestamp,
                    lastConnected = timestamp,
                    displayNamePinned = false
                };

                _recents.Insert(0, recent);
            }

            // Cap recents list size
            if (_recents.Count > MaxRecentEntries)
            {
                _recents.RemoveRange(MaxRecentEntries, _recents.Count - MaxRecentEntries);
            }

            // Update corresponding favorite timestamp
            var favorite = GetFavorite(address, port);
            if (favorite != null)
            {
                favorite.lastConnected = timestamp;
                if (!favorite.displayNamePinned && !string.IsNullOrWhiteSpace(displayName))
                {
                    favorite.displayName = displayName.Trim();
                }
                favorite.password = password ?? favorite.password;
            }

            Save();
            Debug.Log($"[LobbyBookmarkStore] Recorded connection to {address}:{port} (favorite: {favorite != null})");
            Changed?.Invoke();
        }

        /// <summary>
        /// Promote a recent entry to a favorite. If a favorite for the same address/port
        /// already exists it will be updated. Removes matching recents after promotion.
        /// </summary>
        public void PromoteRecentToFavorite(string address, int port, string displayName, string password)
        {
            if (string.IsNullOrWhiteSpace(address))
                return;

            string normalizedName = string.IsNullOrWhiteSpace(displayName) ? string.Empty : displayName.Trim();
            var recent = GetRecent(address, port);
            bool shouldPinDisplayName = recent != null && (recent.displayNamePinned || (!string.IsNullOrWhiteSpace(normalizedName) && !string.Equals(recent.displayName, normalizedName, StringComparison.Ordinal)));

            if (recent != null && shouldPinDisplayName)
            {
                recent.displayNamePinned = true;
                if (!string.IsNullOrWhiteSpace(normalizedName))
                {
                    recent.displayName = normalizedName;
                }
            }

            // Create or update favorite
            AddFavorite(address, port, normalizedName, password);

            var favorite = GetFavorite(address, port);
            if (favorite != null && shouldPinDisplayName)
            {
                favorite.displayNamePinned = true;
                if (!string.IsNullOrWhiteSpace(normalizedName))
                {
                    favorite.displayName = normalizedName;
                }
            }

            // Remove any recents that match this endpoint
            int removed = _recents.RemoveAll(entry => LobbyBookmarkUtility.Matches(address, port, entry));
            if (removed > 0)
            {
                Save();
                Changed?.Invoke();
            }
        }

        public HostedLobbyPreset UpsertMyLobby(string id, string lobbyName, int maxPlayers, LobbyPrivacyMode privacyMode, string password, bool updateHostedTimestamp)
        {
            return UpsertMyLobby(id, lobbyName, maxPlayers, privacyMode, SessionType.Lobby, password, updateHostedTimestamp, 0, false, true, true, true, true, new List<int>(), false);
        }

        public HostedLobbyPreset UpsertMyLobby(
            string id, 
            string lobbyName, 
            int maxPlayers, 
            LobbyPrivacyMode privacyMode, 
            string password, 
            bool updateHostedTimestamp,
            int bandSize,
            bool noFailMode,
            bool sharedSongsOnly,
            bool allowModifiers)
        {
            return UpsertMyLobby(id, lobbyName, maxPlayers, privacyMode, SessionType.Lobby, password, updateHostedTimestamp, 
                bandSize, noFailMode, sharedSongsOnly, allowModifiers, true, true, new List<int>(), false);
        }

        public HostedLobbyPreset UpsertMyLobby(
            string id, 
            string lobbyName, 
            int maxPlayers, 
            LobbyPrivacyMode privacyMode, 
            string password, 
            bool updateHostedTimestamp,
            int bandSize,
            bool noFailMode,
            bool sharedSongsOnly,
            bool allowModifiers,
            bool enablePresetSync,
            bool allowLateJoin)
        {
            return UpsertMyLobby(id, lobbyName, maxPlayers, privacyMode, SessionType.Lobby, password, updateHostedTimestamp,
                bandSize, noFailMode, sharedSongsOnly, allowModifiers, enablePresetSync, allowLateJoin,
                new List<int>(), false);
        }

        public HostedLobbyPreset UpsertMyLobby(
            string id, 
            string lobbyName, 
            int maxPlayers, 
            LobbyPrivacyMode privacyMode, 
            SessionType sessionType,
            string password, 
            bool updateHostedTimestamp,
            int bandSize,
            bool noFailMode,
            bool sharedSongsOnly,
            bool allowModifiers,
            bool enablePresetSync,
            bool allowLateJoin,
            List<int> allowedInstruments,
            bool localPlayersFirst)
        {
            string normalizedName = string.IsNullOrWhiteSpace(lobbyName) ? "My Lobby" : lobbyName.Trim();
            int clampedPlayers = Mathf.Clamp(maxPlayers, 2, 32);
            int clampedBandSize = Mathf.Clamp(bandSize, 0, 8);
            // Password rules:
            // - Private: always allow password (required)
            // - Unlisted + Server: allow password (optional, for direct connect)
            // - Unlisted + Lobby: no password (code is the secret)
            // - Public: no password
            bool canHavePassword = privacyMode == LobbyPrivacyMode.Private ||
                                   (privacyMode == LobbyPrivacyMode.Unlisted && sessionType == SessionType.Server);
            string normalizedPassword = canHavePassword ? (password ?? string.Empty) : string.Empty;

            HostedLobbyPreset preset = null;
            if (!string.IsNullOrWhiteSpace(id))
            {
                preset = _myLobbies.FirstOrDefault(p => string.Equals(p.id, id, StringComparison.Ordinal));
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            if (preset == null)
            {
                preset = new HostedLobbyPreset
                {
                    id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id.Trim(),
                    lobbyName = normalizedName,
                    maxPlayers = clampedPlayers,
                    privacyMode = (int)privacyMode,
                    sessionType = (int)sessionType,
                    password = normalizedPassword,
                    createdAt = now,
                    lastHostedAt = updateHostedTimestamp ? now : 0,
                    bandSize = clampedBandSize,
                    noFailMode = noFailMode,
                    sharedSongsOnly = sharedSongsOnly,
                    allowModifiers = allowModifiers,
                    enablePresetSync = enablePresetSync,
                    allowLateJoin = allowLateJoin,
                    allowedInstruments = allowedInstruments ?? new List<int>(),
                    localPlayersFirst = localPlayersFirst
                };

                _myLobbies.Insert(0, preset);
            }
            else
            {
                preset.lobbyName = normalizedName;
                preset.maxPlayers = clampedPlayers;
                preset.PrivacyMode = privacyMode;
                preset.SessionType = sessionType;
                preset.password = normalizedPassword;
                preset.bandSize = clampedBandSize;
                preset.noFailMode = noFailMode;
                preset.sharedSongsOnly = sharedSongsOnly;
                preset.allowModifiers = allowModifiers;
                preset.enablePresetSync = enablePresetSync;
                preset.allowLateJoin = allowLateJoin;
                preset.allowedInstruments = allowedInstruments ?? new List<int>();
                preset.localPlayersFirst = localPlayersFirst;

                if (updateHostedTimestamp)
                {
                    preset.TouchHostedTimestamp();
                }
            }

            SortMyLobbies();
            Save();
            Changed?.Invoke();

            return preset;
        }

        public HostedLobbyPreset GetMyLobby(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return null;

            return _myLobbies.FirstOrDefault(p => string.Equals(p.id, id, StringComparison.Ordinal));
        }

        public void TouchMyLobbyHosted(string id)
        {
            var preset = GetMyLobby(id);
            if (preset == null)
                return;

            preset.TouchHostedTimestamp();
            SortMyLobbies();
            Save();
            Changed?.Invoke();
        }

        public bool RemoveMyLobby(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return false;

            int removed = _myLobbies.RemoveAll(p => string.Equals(p.id, id, StringComparison.Ordinal));
            if (removed > 0)
            {
                Save();
                Changed?.Invoke();
                return true;
            }

            return false;
        }

        #region SessionPreset Methods

        /// <summary>
        /// Creates or updates a session preset.
        /// </summary>
        public SessionPreset UpsertSessionPreset(SessionPreset preset, bool updateHostedTimestamp = false)
        {
            if (preset == null)
                return null;

            preset.EnsureIdentifiers();
            preset.Normalize();

            var existing = _sessionPresets.FirstOrDefault(p => string.Equals(p.id, preset.id, StringComparison.Ordinal));
            if (existing != null)
            {
                // Update existing
                int index = _sessionPresets.IndexOf(existing);
                _sessionPresets[index] = preset;
            }
            else
            {
                _sessionPresets.Insert(0, preset);
            }

            if (updateHostedTimestamp)
            {
                preset.TouchHostedTimestamp();
            }

            SortSessionPresets();
            Save();
            Changed?.Invoke();

            return preset;
        }

        /// <summary>
        /// Gets a session preset by ID.
        /// </summary>
        public SessionPreset GetSessionPreset(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return null;

            return _sessionPresets.FirstOrDefault(p => string.Equals(p.id, id, StringComparison.Ordinal));
        }

        /// <summary>
        /// Updates the hosted timestamp for a session preset.
        /// </summary>
        public void TouchSessionPresetHosted(string id)
        {
            var preset = GetSessionPreset(id);
            if (preset == null)
                return;

            preset.TouchHostedTimestamp();
            SortSessionPresets();
            Save();
            Changed?.Invoke();
        }

        /// <summary>
        /// Removes a session preset by ID.
        /// </summary>
        public bool RemoveSessionPreset(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return false;

            int removed = _sessionPresets.RemoveAll(p => string.Equals(p.id, id, StringComparison.Ordinal));
            if (removed > 0)
            {
                Save();
                Changed?.Invoke();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Gets all session presets of a specific type.
        /// </summary>
        public IReadOnlyList<SessionPreset> GetSessionPresetsByType(SessionType type)
        {
            return _sessionPresets.Where(p => p.SessionType == type).ToList();
        }

        /// <summary>
        /// Gets lobby-type session presets.
        /// </summary>
        public IReadOnlyList<SessionPreset> GetLobbyPresets()
        {
            return GetSessionPresetsByType(SessionType.Lobby);
        }

        /// <summary>
        /// Gets server-type session presets.
        /// </summary>
        public IReadOnlyList<SessionPreset> GetServerPresets()
        {
            return GetSessionPresetsByType(SessionType.Server);
        }

        #endregion

        #region Migration

        /// <summary>
        /// Migrates legacy HostedLobbyPreset entries to SessionPreset format.
        /// Only migrates if the preset hasn't already been migrated.
        /// </summary>
        private void MigrateLegacyPresets()
        {
            if (_myLobbies == null || _myLobbies.Count == 0)
                return;

            bool migrated = false;
            var existingIds = new HashSet<string>(_sessionPresets.Select(p => p.id));

            foreach (var legacy in _myLobbies)
            {
                if (legacy == null || existingIds.Contains(legacy.id))
                    continue;

                var newPreset = ConvertLegacyPreset(legacy);
                _sessionPresets.Add(newPreset);
                migrated = true;

                Debug.Log($"[LobbyBookmarkStore] Migrated legacy preset '{legacy.lobbyName}' to SessionPreset");
            }

            if (migrated)
            {
                SortSessionPresets();
                // Note: We don't clear _myLobbies to maintain backwards compatibility
                // The legacy list will be removed in a future version
            }
        }

        /// <summary>
        /// Converts a legacy HostedLobbyPreset to the new SessionPreset format.
        /// </summary>
        private static SessionPreset ConvertLegacyPreset(HostedLobbyPreset legacy)
        {
            // Map old LobbyPrivacyMode to new SessionPrivacyMode
            // Old: Public=0, Private=1
            // New: Public=0, Private=1, Unlisted=2
            var privacyMode = legacy.privacyMode switch
            {
                0 => SessionPrivacyMode.Public,
                1 => SessionPrivacyMode.Private,
                _ => SessionPrivacyMode.Public
            };

            return new SessionPreset
            {
                id = legacy.id,
                presetName = legacy.lobbyName,
                sessionType = (int)SessionType.Lobby, // Legacy presets were all lobbies
                createdAt = legacy.createdAt,
                lastHostedAt = legacy.lastHostedAt,

                sessionName = legacy.lobbyName,
                port = 9050, // Default port (wasn't stored in legacy)
                password = legacy.password,
                privacyMode = (int)privacyMode,

                maxPlayers = legacy.maxPlayers,
                bandSize = legacy.bandSize,

                visibleOnLan = true,
                registerWithLobbyServers = true,

                noFailMode = legacy.noFailMode,
                sharedSongsOnly = legacy.sharedSongsOnly,
                allowModifiers = legacy.allowModifiers,
                enablePresetSync = legacy.enablePresetSync,
                allowLateJoin = legacy.allowLateJoin,
                allowedInstruments = legacy.allowedInstruments?.ToList() ?? new List<int>(),
                localPlayersFirstValue = legacy.localPlayersFirst
            };
        }

        #endregion

        private void Load()
        {
            _favorites.Clear();
            _recents.Clear();
            _myLobbies.Clear();
            _sessionPresets.Clear();

            if (!File.Exists(_storagePath))
            {
                return;
            }

            try
            {
                var json = File.ReadAllText(_storagePath);
                var payload = JsonUtility.FromJson<BookmarkData>(json) ?? new BookmarkData();

                if (payload.favorites != null)
                {
                    _favorites.AddRange(payload.favorites.Where(entry => !string.IsNullOrWhiteSpace(entry.address)));
                }

                if (payload.recents != null)
                {
                    _recents.AddRange(payload.recents.Where(entry => !string.IsNullOrWhiteSpace(entry.address)));
                }

                // Load legacy myLobbies
                if (payload.myLobbies != null)
                {
                    foreach (var preset in payload.myLobbies)
                    {
                        if (preset == null)
                            continue;

                        preset.EnsureIdentifiers();
                        preset.maxPlayers = Mathf.Clamp(preset.maxPlayers, 2, 32);

                        if (string.IsNullOrWhiteSpace(preset.lobbyName))
                        {
                            preset.lobbyName = "My Lobby";
                        }

                        // Debug: Log what we loaded for allowedInstruments
                        Debug.Log($"[LobbyBookmarkStore] Load: myLobby '{preset.lobbyName}' allowedInstruments = [{string.Join(", ", preset.allowedInstruments ?? new List<int>())}]");

                        _myLobbies.Add(preset);
                    }
                }

                // Load new sessionPresets
                if (payload.sessionPresets != null)
                {
                    foreach (var preset in payload.sessionPresets)
                    {
                        if (preset == null)
                            continue;

                        preset.EnsureIdentifiers();
                        preset.Normalize();
                        _sessionPresets.Add(preset);
                    }
                }

                // Migrate legacy myLobbies to sessionPresets if not already migrated
                MigrateLegacyPresets();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LobbyBookmarkStore] Failed to load bookmarks: {ex.Message}");
            }

            SortMyLobbies();
            SortSessionPresets();
        }

        private void Save()
        {
            try
            {
                var payload = new BookmarkData
                {
                    favorites = _favorites.Select(entry => entry.Clone()).ToList(),
                    recents = _recents.Select(entry => entry.Clone()).ToList(),
                    myLobbies = _myLobbies.Select(entry => entry.Clone()).ToList(),
                    sessionPresets = _sessionPresets.Select(entry => entry.Clone()).ToList()
                };

                // Debug: Log what we're saving for myLobbies
                if (payload.myLobbies != null && payload.myLobbies.Count > 0)
                {
                    var first = payload.myLobbies[0];
                    Debug.Log($"[LobbyBookmarkStore] Save: myLobbies[0].allowedInstruments = [{string.Join(", ", first.allowedInstruments ?? new List<int>())}]");
                }

                var json = JsonUtility.ToJson(payload, true);
                
                // Debug: Log a snippet of the JSON to verify allowedInstruments
                if (json.Contains("allowedInstruments"))
                {
                    int idx = json.IndexOf("allowedInstruments");
                    int endIdx = Math.Min(idx + 100, json.Length);
                    Debug.Log($"[LobbyBookmarkStore] Save JSON snippet: ...{json.Substring(idx, endIdx - idx)}...");
                }
                else
                {
                    Debug.LogWarning("[LobbyBookmarkStore] Save: JSON does NOT contain 'allowedInstruments' field!");
                }
                
                File.WriteAllText(_storagePath, json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LobbyBookmarkStore] Failed to save bookmarks: {ex.Message}");
            }
        }

        private void SortRecents()
        {
            if (_recents == null) return;
            _recents.Sort((a, b) => b.lastConnected.CompareTo(a.lastConnected));
        }

        private void SortFavorites()
        {
            if (_favorites == null) return;
            // Sort favorites by createdAt ascending (oldest first) so UI can show oldest at top
            _favorites.Sort((a, b) => a.createdAt.CompareTo(b.createdAt));
        }

        private void SortMyLobbies()
        {
            if (_myLobbies == null) return;
            _myLobbies.Sort((a, b) =>
            {
                int hostedCompare = b.lastHostedAt.CompareTo(a.lastHostedAt);
                if (hostedCompare != 0)
                    return hostedCompare;

                return b.createdAt.CompareTo(a.createdAt);
            });
        }

        private void SortSessionPresets()
        {
            if (_sessionPresets == null) return;
            _sessionPresets.Sort((a, b) =>
            {
                // Sort by last hosted (most recent first), then by created (most recent first)
                int hostedCompare = b.lastHostedAt.CompareTo(a.lastHostedAt);
                if (hostedCompare != 0)
                    return hostedCompare;

                return b.createdAt.CompareTo(a.createdAt);
            });
        }

        /// <summary>
        /// Return favorites ordered with online entries first (based on provided endpoint keys), then by createdAt desc.
        /// </summary>
        public IReadOnlyList<LobbyBookmark> GetFavoritesOrderedByOnline(IEnumerable<string> onlineEndpointKeys)
        {
            var onlineSet = new HashSet<string>(onlineEndpointKeys ?? System.Array.Empty<string>());
            // Online entries first, then sort within each group by createdAt ascending (oldest -> newest)
            var ordered = _favorites.OrderByDescending(b => onlineSet.Contains(b.EndpointKey))
                                    .ThenBy(b => b.createdAt)
                                    .ToList();
            return ordered;
        }

        [Serializable]
        private sealed class BookmarkData
        {
            public List<LobbyBookmark> favorites;
            public List<LobbyBookmark> recents;
            public List<HostedLobbyPreset> myLobbies;
            public List<SessionPreset> sessionPresets;
        }

        private static bool ApplyBookmarkUpdate(List<LobbyBookmark> list, string originalKey,
            string displayName, string address, int port, string password, bool favoriteFlag, bool? pinDisplayName)
        {
            if (list == null || list.Count == 0)
            {
                return false;
            }

            int index = list.FindIndex(entry => entry.EndpointKey == originalKey);
            if (index < 0)
            {
                return false;
            }

            var entry = list[index];
            entry.displayName = displayName;
            entry.address = address;
            entry.port = port;
            entry.password = password;
            entry.favorite = favoriteFlag;
            if (pinDisplayName.HasValue)
            {
                entry.displayNamePinned = pinDisplayName.Value;
            }
            return true;
        }
    }
}
