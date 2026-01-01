using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using YARG.Core;
using YARG.Core.Engine;
using YARG.Core.Engine.Drums;
using YARG.Core.Engine.Guitar;
using YARG.Core.Engine.Vocals;
using YARG.Core.Game;
using YARG.Net.Sessions;
using YARG.Networking;
using YARG.Networking.Abstraction;
using YARG.Networking.Bands;
using YARG.Networking.Gameplay;
using YARG.Networking.Tracks;
using YARG.Player;

namespace YARG.Menu.Multiplayer
{
    /// <summary>
    /// Manages the mapping between network players and local YargPlayer instances.
    /// Responsible for creating YargPlayers from network data for multiplayer gameplay.
    /// </summary>
    public static class MultiplayerPlayerManager
    {
        private static readonly Dictionary<Guid, NetworkPlayerData> _playerIdToNetworkData = new();
        private static readonly Dictionary<string, NetworkPlayerData> _playerNameToNetworkData = new();
        
        // Direct mapping from YargPlayer instance to its NetworkPlayerData
        // This handles the case where multiple players have the same name
        private static readonly Dictionary<YargPlayer, NetworkPlayerData> _yargPlayerToNetworkData = new();

        /// <summary>
        /// Creates YargPlayer instances from network player data for multiplayer gameplay.
        /// Players are ordered according to the TrackOrderManager's custom order.
        /// In band mode, only players from the local band are created for gameplay simulation.
        /// </summary>
        /// <returns>List of YargPlayers created from network data, in track order.</returns>
        public static List<YargPlayer> CreateMultiplayerPlayers()
        {
            var result = new List<YargPlayer>();
            _playerIdToNetworkData.Clear();
            _playerNameToNetworkData.Clear();
            _yargPlayerToNetworkData.Clear();

            var networkService = NetworkingServiceFactory.Instance;
            if (networkService == null || !networkService.IsNetworkActive)
            {
                Debug.LogWarning("[MultiplayerPlayerManager] Network not active, returning empty player list");
                return result;
            }

            var networkPlayers = networkService.GetAllPlayers();
            Debug.Log($"[MultiplayerPlayerManager] Creating players from {networkPlayers.Count} network players");
            Debug.Log($"[MultiplayerPlayerManager] IsHosting={networkService.IsHosting}");
            
            // Check if band mode is active - only create players from the local band
            var bandManager = BandManager.Instance;
            bool isBandModeActive = bandManager != null && bandManager.IsBandSystemActive;
            int localBandId = bandManager?.LocalPlayerBandId ?? -1;
            
            Debug.Log($"[MultiplayerPlayerManager] Band mode: active={isBandModeActive}, localBandId={localBandId}");
            
            // Log all network players before ordering
            for (int i = 0; i < networkPlayers.Count; i++)
            {
                var np = networkPlayers[i];
                Debug.Log($"[MultiplayerPlayerManager] RAW networkPlayer[{i}]: Name={np.PlayerName}, NetworkPlayerId={np.NetworkPlayerId}, IsLocalUser={np.IsLocalUser}, IsHost={np.IsHost}");
            }

            // Get the track order from TrackOrderManager (applies custom order + LocalPlayersFirst)
            var trackOrderManager = TrackOrderManager.Instance;
            Debug.Log($"[MultiplayerPlayerManager] TrackOrderManager exists={trackOrderManager != null}, LocalPlayersFirst={trackOrderManager?.LocalPlayersFirst ?? false}");
            
            // Register local players with TrackOrderManager so LocalPlayersFirst logic works
            // This ensures each client knows which players are "local" to them
            if (trackOrderManager != null)
            {
                trackOrderManager.ClearLocalPlayers();
                foreach (var np in networkPlayers)
                {
                    if (np.IsLocalUser)
                    {
                        trackOrderManager.RegisterLocalPlayer(np.NetworkPlayerId);
                        Debug.Log($"[MultiplayerPlayerManager] Registered local player: {np.PlayerName} ({np.NetworkPlayerId})");
                    }
                }
            }
            
            // Use GetOrderedPlayerIds to apply both custom order AND LocalPlayersFirst logic
            List<NetworkPlayerData> orderedPlayers;
            if (trackOrderManager != null)
            {
                var orderedIds = trackOrderManager.GetOrderedPlayerIds(networkPlayers);
                Debug.Log($"[MultiplayerPlayerManager] GetOrderedPlayerIds returned {orderedIds.Count} entries, LocalPlayersFirst={trackOrderManager.LocalPlayersFirst}");
                
                // Log the ordered IDs
                for (int i = 0; i < orderedIds.Count; i++)
                {
                    Debug.Log($"[MultiplayerPlayerManager] orderedIds[{i}] = {orderedIds[i]}");
                }
                
                // Create a dictionary for quick lookup of network players by ID
                var playerById = networkPlayers.ToDictionary(p => p.NetworkPlayerId);
                
                // Build ordered list based on GetOrderedPlayerIds result
                orderedPlayers = new List<NetworkPlayerData>(orderedIds.Count);
                foreach (var id in orderedIds)
                {
                    if (playerById.TryGetValue(id, out var player))
                    {
                        orderedPlayers.Add(player);
                    }
                }
                
                // Add any players not in the order at the end (shouldn't happen but be safe)
                foreach (var player in networkPlayers)
                {
                    if (!orderedIds.Contains(player.NetworkPlayerId))
                    {
                        orderedPlayers.Add(player);
                    }
                }
                
                Debug.Log($"[MultiplayerPlayerManager] After applying track order (LocalPlayersFirst={trackOrderManager.LocalPlayersFirst}):");
                for (int i = 0; i < orderedPlayers.Count; i++)
                {
                    var op = orderedPlayers[i];
                    Debug.Log($"[MultiplayerPlayerManager] orderedPlayers[{i}]: Name={op.PlayerName}, NetworkPlayerId={op.NetworkPlayerId}, IsLocalUser={op.IsLocalUser}");
                }
            }
            else
            {
                Debug.Log("[MultiplayerPlayerManager] No TrackOrderManager available, using default order");
                orderedPlayers = networkPlayers.ToList();
                
                for (int i = 0; i < orderedPlayers.Count; i++)
                {
                    var op = orderedPlayers[i];
                    Debug.Log($"[MultiplayerPlayerManager] orderedPlayers[{i}] (default): Name={op.PlayerName}, NetworkPlayerId={op.NetworkPlayerId}, IsLocalUser={op.IsLocalUser}");
                }
            }

            foreach (var networkPlayer in orderedPlayers)
            {
                if (networkPlayer == null)
                {
                    Debug.LogWarning("[MultiplayerPlayerManager] Skipping null network player");
                    continue;
                }

                // Log debug info for each player being processed
                Debug.Log($"[MultiplayerPlayerManager] Processing network player: {networkPlayer.PlayerName} (ID: {networkPlayer.NetworkPlayerId}, IsLocalUser: {networkPlayer.IsLocalUser}, IsHost: {networkPlayer.IsHost})");

                // In band mode, skip players from other bands (they are only score-synced, not simulated)
                if (isBandModeActive && localBandId >= 0)
                {
                    int playerBandId = bandManager.GetPlayerBandId(networkPlayer.NetworkPlayerId);
                    if (playerBandId != localBandId)
                    {
                        Debug.Log($"[MultiplayerPlayerManager] Skipping player {networkPlayer.PlayerName} - different band (player band: {playerBandId}, local band: {localBandId})");
                        // Still cache the mapping for lookups, but don't create a YargPlayer
                        if (networkPlayer.NetworkPlayerId != Guid.Empty)
                        {
                            _playerIdToNetworkData[networkPlayer.NetworkPlayerId] = networkPlayer;
                        }
                        continue;
                    }
                    Debug.Log($"[MultiplayerPlayerManager] Player {networkPlayer.PlayerName} is in local band {localBandId}");
                }

                // Cache the mapping
                if (networkPlayer.NetworkPlayerId != Guid.Empty)
                {
                    _playerIdToNetworkData[networkPlayer.NetworkPlayerId] = networkPlayer;
                }
                if (!string.IsNullOrEmpty(networkPlayer.PlayerName))
                {
                    _playerNameToNetworkData[networkPlayer.PlayerName] = networkPlayer;
                }

                // For local players, we use the PlayerContainer's existing player instances
                if (networkPlayer.IsLocalUser)
                {
                    // Find matching local player
                    var localPlayer = FindLocalPlayerForNetworkData(networkPlayer);
                    if (localPlayer != null)
                    {
                        result.Add(localPlayer);
                        _yargPlayerToNetworkData[localPlayer] = networkPlayer;
                        Debug.Log($"[MultiplayerPlayerManager] Added local player: {networkPlayer.PlayerName} (mapped to NetworkPlayerId: {networkPlayer.NetworkPlayerId})");
                    }
                    else
                    {
                        Debug.LogWarning($"[MultiplayerPlayerManager] Could not find local player for: {networkPlayer.PlayerName}");
                    }
                }
                else
                {
                    // For remote players, create a YargPlayer from the network data
                    var remotePlayer = CreateRemotePlayer(networkPlayer);
                    if (remotePlayer != null)
                    {
                        result.Add(remotePlayer);
                        _yargPlayerToNetworkData[remotePlayer] = networkPlayer;
                        Debug.Log($"[MultiplayerPlayerManager] Added remote player: {networkPlayer.PlayerName} (mapped to NetworkPlayerId: {networkPlayer.NetworkPlayerId})");
                    }
                }
            }

            Debug.Log($"[MultiplayerPlayerManager] Created {result.Count} total players (band mode: {isBandModeActive})");
            return result;
        }

        /// <summary>
        /// Tries to get the NetworkPlayerData for a given YargPlayer.
        /// </summary>
        public static bool TryGetNetworkPlayer(YargPlayer yargPlayer, out NetworkPlayerData networkPlayer)
        {
            networkPlayer = null;
            
            if (yargPlayer == null || yargPlayer.Profile == null)
            {
                return false;
            }

            // FIRST: Try the direct mapping (most reliable, handles same-name players correctly)
            if (_yargPlayerToNetworkData.TryGetValue(yargPlayer, out networkPlayer))
            {
                Debug.Log($"[MultiplayerPlayerManager] TryGetNetworkPlayer: Found direct mapping for {yargPlayer.Profile.Name} -> NetworkPlayerId: {networkPlayer.NetworkPlayerId}, IsLocalUser: {networkPlayer.IsLocalUser}");
                return true;
            }

            // Try to find by profile ID first - NetworkPlayerData uses Guid NetworkPlayerId
            // while YargProfile uses Guid Id, so we compare directly
            foreach (var kvp in _playerIdToNetworkData)
            {
                if (kvp.Value.NetworkPlayerId == yargPlayer.Profile.Id)
                {
                    networkPlayer = kvp.Value;
                    Debug.Log($"[MultiplayerPlayerManager] TryGetNetworkPlayer: Found by profile ID for {yargPlayer.Profile.Name}");
                    return true;
                }
            }

            // Try to find by name (less reliable if multiple players have same name)
            if (!string.IsNullOrEmpty(yargPlayer.Profile.Name) &&
                _playerNameToNetworkData.TryGetValue(yargPlayer.Profile.Name, out networkPlayer))
            {
                Debug.Log($"[MultiplayerPlayerManager] TryGetNetworkPlayer: Found by name for {yargPlayer.Profile.Name} (WARNING: may be wrong if multiple players have same name)");
                return true;
            }

            // Try to find from live network data
            var networkService = NetworkingServiceFactory.Instance;
            if (networkService != null && networkService.IsNetworkActive)
            {
                foreach (var player in networkService.GetAllPlayers())
                {
                    if (player == null) continue;

                    // Match by ID
                    if (player.NetworkPlayerId == yargPlayer.Profile.Id)
                    {
                        networkPlayer = player;
                        return true;
                    }

                    // Match by name
                    if (string.Equals(player.PlayerName, yargPlayer.Profile.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        networkPlayer = player;
                        return true;
                    }
                }
            }

            Debug.LogWarning($"[MultiplayerPlayerManager] TryGetNetworkPlayer: No mapping found for {yargPlayer.Profile.Name}");
            return false;
        }

        /// <summary>
        /// Tries to get the NetworkPlayerData for a given NetworkPlayerId.
        /// This is useful for looking up remote players who may not have YargPlayer instances.
        /// </summary>
        public static bool TryGetNetworkPlayerData(Guid networkPlayerId, out NetworkPlayerData networkPlayer)
        {
            if (_playerIdToNetworkData.TryGetValue(networkPlayerId, out networkPlayer))
            {
                return true;
            }

            // Try to find from live network data as fallback
            var networkService = NetworkingServiceFactory.Instance;
            if (networkService != null && networkService.IsNetworkActive)
            {
                foreach (var player in networkService.GetAllPlayers())
                {
                    if (player == null) continue;

                    if (player.NetworkPlayerId == networkPlayerId)
                    {
                        networkPlayer = player;
                        return true;
                    }
                }
            }

            networkPlayer = null;
            return false;
        }

        /// <summary>
        /// Creates a minimal YargPlayer for a remote network player.
        /// Used for displaying remote players on the score screen when full stats aren't available.
        /// </summary>
        public static YargPlayer CreatePlaceholderPlayer(NetworkPlayerData networkPlayer)
        {
            if (networkPlayer == null) return null;

            var instrument = networkPlayer.Instrument >= 0 ? (Instrument)networkPlayer.Instrument : Instrument.FiveFretGuitar;
            
            var profile = new YargProfile
            {
                Name = networkPlayer.PlayerName ?? "Remote Player",
                CurrentInstrument = instrument,
                CurrentDifficulty = (Difficulty)networkPlayer.Difficulty,
                GameMode = instrument.ToNativeGameMode(),
            };

            // Remote players don't have bindings
            var player = new YargPlayer(profile, bindings: null);
            return player;
        }
        
        /// <summary>
        /// Creates BaseStats from NetworkPlayerData for remote players.
        /// Uses the appropriate stats type based on the player's instrument.
        /// Prioritizes cached ScoreResults (which contain the final score) over live network data.
        /// </summary>
        public static BaseStats CreateStatsFromNetworkData(NetworkPlayerData networkPlayer)
        {
            if (networkPlayer == null) return null;
            
            var instrument = networkPlayer.Instrument >= 0 ? (Instrument)networkPlayer.Instrument : Instrument.FiveFretGuitar;
            var gameMode = instrument.ToNativeGameMode();
            
            // Check for cached ScoreResults first - these have the final score
            // This is important because the NetworkPlayerData.Score may not be updated correctly
            // due to name-based player matching issues in multiplayer
            PlayerScoreResult cachedResult = null;
            var networkService = NetworkingServiceFactory.Instance;
            if (networkService is LiteNetNetworkingAdapter liteNetAdapter)
            {
                var cachedResults = liteNetAdapter.GetCachedScoreResults();
                if (cachedResults != null && cachedResults.TryGetValue(networkPlayer.PlayerName, out var result))
                {
                    cachedResult = result;
                    Debug.Log($"[MultiplayerPlayerManager] Found cached ScoreResult for {networkPlayer.PlayerName}: " +
                              $"Score={result.Score}, NotesHit={result.NotesHit}, NotesMissed={result.NotesMissed}, MaxCombo={result.MaxCombo}");
                }
            }
            
            BaseStats stats;
            
            // Create the appropriate stats type based on game mode
            switch (gameMode)
            {
                case GameMode.FourLaneDrums:
                case GameMode.FiveLaneDrums:
                case GameMode.EliteDrums:
                    var drumStats = new DrumsStats();
                    PopulateBaseStats(drumStats, networkPlayer, cachedResult);
                    // Populate drum-specific stats from network data
                    drumStats.Overhits = networkPlayer.Overhits;
                    drumStats.GhostsHit = networkPlayer.GhostsHit;
                    drumStats.AccentsHit = networkPlayer.AccentsHit;
                    drumStats.DynamicsBonus = networkPlayer.DynamicsBonus;
                    stats = drumStats;
                    break;
                    
                case GameMode.Vocals:
                    var vocalStats = new VocalsStats();
                    PopulateBaseStats(vocalStats, networkPlayer, cachedResult);
                    // Populate vocal-specific stats from network data
                    vocalStats.TicksHit = (uint)networkPlayer.VocalsTicksHit;
                    vocalStats.TicksMissed = (uint)networkPlayer.VocalsTicksMissed;
                    stats = vocalStats;
                    break;
                    
                default:
                    // Guitar/Bass/Keys all use GuitarStats
                    var guitarStats = new GuitarStats();
                    PopulateBaseStats(guitarStats, networkPlayer, cachedResult);
                    // Populate guitar-specific stats from network data
                    guitarStats.Overstrums = networkPlayer.Overstrums;
                    guitarStats.HoposStrummed = networkPlayer.HoposStrummed;
                    guitarStats.GhostInputs = networkPlayer.GhostInputs;
                    stats = guitarStats;
                    break;
            }
            
            Debug.Log($"[MultiplayerPlayerManager] Created stats for remote player {networkPlayer.PlayerName}: " +
                      $"Score={stats.CommittedScore}, NotesHit={stats.NotesHit}/{stats.TotalNotes}, MaxCombo={stats.MaxCombo}");
            
            return stats;
        }
        
        /// <summary>
        /// Populates base stats fields from NetworkPlayerData.
        /// Prioritizes cached ScoreResult data when available (for final scores).
        /// </summary>
        private static void PopulateBaseStats(BaseStats stats, NetworkPlayerData networkPlayer, PlayerScoreResult cachedResult = null)
        {
            // Use cached ScoreResult for final scores if available, fall back to network data
            if (cachedResult != null)
            {
                // Use the final score from the cached result - this is reliable
                stats.CommittedScore = cachedResult.Score;
                stats.PendingScore = 0;
                
                // Use note stats from cached result
                stats.NotesHit = cachedResult.NotesHit;
                stats.TotalNotes = cachedResult.NotesHit + cachedResult.NotesMissed;
                
                // Use max combo from cached result
                stats.MaxCombo = cachedResult.MaxCombo;
                stats.Combo = 0; // Combo is 0 at end of song
            }
            else
            {
                // Fall back to network player data (may be stale)
                stats.CommittedScore = networkPlayer.Score;
                stats.PendingScore = 0;
                
                stats.NotesHit = networkPlayer.NotesHit;
                stats.TotalNotes = networkPlayer.NotesHit + networkPlayer.NotesMissed;
                
                stats.Combo = networkPlayer.Combo;
                stats.MaxCombo = networkPlayer.MaxCombo;
            }
            
            // These stats are only available from live network data
            stats.StarPowerPhrasesHit = networkPlayer.StarPowerPhrasesHit;
            stats.TotalStarPowerPhrases = networkPlayer.TotalStarPowerPhrases;
            stats.IsStarPowerActive = networkPlayer.IsStarPowerActive;
            
            // Stars
            stats.Stars = networkPlayer.Stars;
            
            // Band bonus score
            stats.BandBonusScore = networkPlayer.BandBonusScore;
        }

        /// <summary>
        /// Finds the local YargPlayer that matches the given network player data.
        /// </summary>
        private static YargPlayer FindLocalPlayerForNetworkData(NetworkPlayerData networkPlayer)
        {
            if (networkPlayer == null) return null;

            Debug.Log($"[MultiplayerPlayerManager] FindLocalPlayerForNetworkData: Searching for player '{networkPlayer.PlayerName}' (NetworkPlayerId: {networkPlayer.NetworkPlayerId})");
            Debug.Log($"[MultiplayerPlayerManager] PlayerContainer has {PlayerContainer.Players.Count} players");

            foreach (var player in PlayerContainer.Players)
            {
                if (player == null || player.Profile == null) continue;

                Debug.Log($"[MultiplayerPlayerManager] Checking local player: {player.Profile.Name} (ProfileId: {player.Profile.Id})");

                // Match by profile ID - Note: This won't match if using session IDs!
                if (networkPlayer.NetworkPlayerId == player.Profile.Id)
                {
                    Debug.Log($"[MultiplayerPlayerManager] Found match by profile ID");
                    return player;
                }

                // Match by name as fallback
                if (string.Equals(networkPlayer.PlayerName, player.Profile.Name, StringComparison.OrdinalIgnoreCase))
                {
                    Debug.Log($"[MultiplayerPlayerManager] Found match by name");
                    return player;
                }
            }

            // If no exact match, try to find by instrument/difficulty match
            int selectedInstrument = networkPlayer.Instrument;
            int selectedDifficulty = networkPlayer.Difficulty;

            Debug.Log($"[MultiplayerPlayerManager] No name/ID match, trying instrument/difficulty ({selectedInstrument}/{selectedDifficulty})");

            foreach (var player in PlayerContainer.Players)
            {
                if (player?.Profile == null) continue;

                // Check if this player matches the network player's selection
                if ((int)player.Profile.CurrentInstrument == selectedInstrument &&
                    (int)player.Profile.CurrentDifficulty == selectedDifficulty)
                {
                    Debug.Log($"[MultiplayerPlayerManager] Found match by instrument/difficulty");
                    return player;
                }
            }

            Debug.LogWarning($"[MultiplayerPlayerManager] No local player found for network player '{networkPlayer.PlayerName}'");
            return null;
        }

        /// <summary>
        /// Creates a YargPlayer for a remote network player.
        /// Remote players don't have local input bindings.
        /// </summary>
        private static YargPlayer CreateRemotePlayer(NetworkPlayerData networkPlayer)
        {
            if (networkPlayer == null) return null;

            Debug.Log($"[MultiplayerPlayerManager] CreateRemotePlayer: name={networkPlayer.PlayerName}, " +
                      $"rawInstrument={networkPlayer.Instrument}, rawDifficulty={networkPlayer.Difficulty}, sittingOut={networkPlayer.SittingOut}");
            
            // Validate instrument value
            if (networkPlayer.Instrument < 0)
            {
                Debug.LogWarning($"[MultiplayerPlayerManager] WARNING: Remote player {networkPlayer.PlayerName} has invalid instrument={networkPlayer.Instrument}, defaulting to FiveFretGuitar (0)");
            }

            // Create a profile for the remote player
            // Handle invalid instrument values (-1 means not set)
            var instrument = networkPlayer.Instrument >= 0 ? (Instrument)networkPlayer.Instrument : Instrument.FiveFretGuitar;
            
            var profile = new YargProfile
            {
                Name = networkPlayer.PlayerName ?? "Remote Player",
                // Copy instrument/difficulty from network data (cast from int to enum)
                CurrentInstrument = instrument,
                CurrentDifficulty = (Difficulty)networkPlayer.Difficulty,
                // IMPORTANT: Derive GameMode from the instrument to ensure they match
                // Without this, GameMode defaults to FiveFretGuitar which causes crashes
                // when the remote player is using drums/vocals/etc.
                GameMode = instrument.ToNativeGameMode(),
            };

            Debug.Log($"[MultiplayerPlayerManager] Created profile for {profile.Name}: " +
                      $"Instrument={profile.CurrentInstrument}, GameMode={profile.GameMode}, Difficulty={profile.CurrentDifficulty}, SittingOut={networkPlayer.SittingOut}");

            // Remote players don't have bindings (they're controlled by network state)
            var player = new YargPlayer(profile, bindings: null);
            
            // Copy the sitting out state from network data
            player.SittingOut = networkPlayer.SittingOut;
            
            // Initialize presets for the remote player (engine, camera, colors, etc.)
            // Check if preset sync is enabled and we have synced preset data
            var gameplaySettings = MultiplayerGameplaySettings.Instance;
            bool enablePresetSync = gameplaySettings?.EnablePresetSync ?? false;
            bool hasSyncedPresets = networkPlayer.HasSyncedPresets;
            
            Debug.Log($"[MultiplayerPlayerManager] Preset sync check: EnablePresetSync={enablePresetSync}, HasSyncedPresets={hasSyncedPresets}");
            
            if (enablePresetSync && hasSyncedPresets)
            {
                // Apply the synced presets from the network player data
                Debug.Log($"[MultiplayerPlayerManager] Applying synced presets for {networkPlayer.PlayerName}");
                player.ApplySyncedPresets(
                    networkPlayer.CameraPresetId, networkPlayer.CameraPresetJson,
                    networkPlayer.HighwayPresetId, networkPlayer.HighwayPresetJson,
                    networkPlayer.ColorProfileId, networkPlayer.ColorProfileJson,
                    networkPlayer.ThemePresetId, networkPlayer.ThemePresetJson);
            }
            else
            {
                // Use default presets (original behavior)
                Debug.Log($"[MultiplayerPlayerManager] Using default presets for {networkPlayer.PlayerName}");
                player.RefreshPresets();
            }

            return player;
        }

        /// <summary>
        /// Clears the cached player mappings.
        /// Should be called when leaving multiplayer.
        /// </summary>
        public static void Clear()
        {
            _playerIdToNetworkData.Clear();
            _playerNameToNetworkData.Clear();
            _yargPlayerToNetworkData.Clear();
        }
    }
}
