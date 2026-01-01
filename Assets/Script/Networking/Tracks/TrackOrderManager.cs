using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using YARG.Networking.Abstraction;

namespace YARG.Networking.Tracks
{
    /// <summary>
    /// Manages track display ordering for multiplayer sessions.
    /// Supports local players first, host-defined custom order, and band-based grouping.
    /// </summary>
    public sealed class TrackOrderManager : MonoBehaviour
    {
        public static TrackOrderManager Instance { get; private set; }

        private readonly List<Guid> _customOrder = new();
        private readonly HashSet<Guid> _localPlayerIds = new();
        private bool _localPlayersFirst;
        private bool _groupByBand;

        /// <summary>
        /// Gets or sets whether local players should be displayed first.
        /// </summary>
        public bool LocalPlayersFirst
        {
            get => _localPlayersFirst;
            set
            {
                if (_localPlayersFirst != value)
                {
                    _localPlayersFirst = value;
                    OnOrderChanged?.Invoke();
                }
            }
        }

        /// <summary>
        /// Gets or sets whether players should be grouped by band.
        /// Only applies when band system is active.
        /// </summary>
        public bool GroupByBand
        {
            get => _groupByBand;
            set
            {
                if (_groupByBand != value)
                {
                    _groupByBand = value;
                    OnOrderChanged?.Invoke();
                }
            }
        }

        /// <summary>
        /// Fired when track order changes.
        /// </summary>
        public event Action OnOrderChanged;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        /// <summary>
        /// Registers a player as local (for local-first ordering).
        /// </summary>
        public void RegisterLocalPlayer(Guid playerId)
        {
            if (_localPlayerIds.Add(playerId))
            {
                OnOrderChanged?.Invoke();
            }
        }

        /// <summary>
        /// Unregisters a local player.
        /// </summary>
        public void UnregisterLocalPlayer(Guid playerId)
        {
            if (_localPlayerIds.Remove(playerId))
            {
                OnOrderChanged?.Invoke();
            }
        }

        /// <summary>
        /// Clears all local player registrations.
        /// </summary>
        public void ClearLocalPlayers()
        {
            _localPlayerIds.Clear();
        }

        /// <summary>
        /// Sets a custom track order (host only).
        /// </summary>
        public void SetCustomOrder(IEnumerable<Guid> playerIds)
        {
            _customOrder.Clear();
            _customOrder.AddRange(playerIds);
            OnOrderChanged?.Invoke();
        }

        /// <summary>
        /// Clears the custom order.
        /// </summary>
        public void ClearCustomOrder()
        {
            _customOrder.Clear();
            OnOrderChanged?.Invoke();
        }

        /// <summary>
        /// Moves a player up in the custom order.
        /// Requires InitializeOrderFromPlayers to be called first if _customOrder is empty.
        /// </summary>
        public void MovePlayerUp(Guid playerId)
        {
            Debug.Log($"[TrackOrderManager] MovePlayerUp called for {playerId}, current order has {_customOrder.Count} players");
            int index = _customOrder.IndexOf(playerId);
            if (index > 0)
            {
                _customOrder.RemoveAt(index);
                _customOrder.Insert(index - 1, playerId);
                Debug.Log($"[TrackOrderManager] Moved player {playerId} up from index {index} to {index - 1}");
                OnOrderChanged?.Invoke();
            }
            else if (index == 0)
            {
                Debug.Log($"[TrackOrderManager] MovePlayerUp: Player {playerId} is already at top (index 0)");
            }
            else if (index < 0)
            {
                Debug.LogWarning($"[TrackOrderManager] MovePlayerUp: Player {playerId} not found in custom order. Call InitializeOrderFromPlayers first.");
            }
        }

        /// <summary>
        /// Moves a player down in the custom order.
        /// Requires InitializeOrderFromPlayers to be called first if _customOrder is empty.
        /// </summary>
        public void MovePlayerDown(Guid playerId)
        {
            Debug.Log($"[TrackOrderManager] MovePlayerDown called for {playerId}, current order has {_customOrder.Count} players");
            int index = _customOrder.IndexOf(playerId);
            if (index >= 0 && index < _customOrder.Count - 1)
            {
                _customOrder.RemoveAt(index);
                _customOrder.Insert(index + 1, playerId);
                Debug.Log($"[TrackOrderManager] Moved player {playerId} down from index {index} to {index + 1}");
                OnOrderChanged?.Invoke();
            }
            else if (index == _customOrder.Count - 1)
            {
                Debug.Log($"[TrackOrderManager] MovePlayerDown: Player {playerId} is already at bottom (index {index} of {_customOrder.Count})");
            }
            else if (index < 0)
            {
                Debug.LogWarning($"[TrackOrderManager] MovePlayerDown: Player {playerId} not found in custom order. Call InitializeOrderFromPlayers first.");
            }
        }
        
        /// <summary>
        /// Initializes the custom order from all connected players if it's empty.
        /// This should be called before any reordering operations when the custom order hasn't been set.
        /// </summary>
        /// <param name="allPlayers">All players currently in the session.</param>
        public void InitializeOrderFromPlayers(IEnumerable<NetworkPlayerData> allPlayers)
        {
            if (_customOrder.Count > 0)
            {
                // Already initialized, just ensure all players are included
                var playerIds = allPlayers.Select(p => p.NetworkPlayerId).ToList();
                foreach (var playerId in playerIds)
                {
                    if (!_customOrder.Contains(playerId))
                    {
                        _customOrder.Add(playerId);
                    }
                }
                return;
            }
            
            // Initialize from scratch, ordered by player index
            var orderedPlayers = allPlayers.OrderBy(p => p.PlayerIndex).Select(p => p.NetworkPlayerId);
            _customOrder.AddRange(orderedPlayers);
            Debug.Log($"[TrackOrderManager] Initialized custom order with {_customOrder.Count} players");
        }
        
        /// <summary>
        /// Adds a player to the custom order (at the end) if not already present.
        /// Call this when a new player joins the session.
        /// </summary>
        public void AddPlayerToOrder(Guid playerId)
        {
            if (playerId == Guid.Empty)
            {
                Debug.LogWarning($"[TrackOrderManager] Attempted to add player with Guid.Empty - ignoring");
                return;
            }
            
            if (!_customOrder.Contains(playerId))
            {
                _customOrder.Add(playerId);
                Debug.Log($"[TrackOrderManager] Added player {playerId} to custom order (now {_customOrder.Count} players)");
                OnOrderChanged?.Invoke();
            }
            else
            {
                Debug.Log($"[TrackOrderManager] Player {playerId} already in custom order");
            }
        }

        /// <summary>
        /// Adds multiple players from the same connection as a group (adjacent to each other).
        /// If afterPlayerId is specified, the group is inserted after that player's position.
        /// Otherwise, the group is added at the end.
        /// </summary>
        /// <param name="playerIds">The player IDs to add as a group</param>
        /// <param name="afterPlayerId">Optional: Insert the group after this player</param>
        public void AddPlayersAsGroup(IEnumerable<Guid> playerIds, Guid? afterPlayerId = null)
        {
            var newPlayers = playerIds.Where(id => id != Guid.Empty && !_customOrder.Contains(id)).ToList();
            if (newPlayers.Count == 0)
            {
                return;
            }
            
            int insertPosition;
            if (afterPlayerId.HasValue)
            {
                int afterIndex = _customOrder.IndexOf(afterPlayerId.Value);
                insertPosition = afterIndex >= 0 ? afterIndex + 1 : _customOrder.Count;
            }
            else
            {
                insertPosition = _customOrder.Count;
            }
            
            foreach (var playerId in newPlayers)
            {
                _customOrder.Insert(insertPosition, playerId);
                insertPosition++;
                Debug.Log($"[TrackOrderManager] Added player {playerId} to custom order at position {insertPosition - 1}");
            }
            
            Debug.Log($"[TrackOrderManager] Added {newPlayers.Count} players as group (now {_customOrder.Count} players total)");
            OnOrderChanged?.Invoke();
        }
        
        /// <summary>
        /// Removes a player from the custom order.
        /// Call this when a player leaves the session.
        /// </summary>
        public void RemovePlayerFromOrder(Guid playerId)
        {
            if (_customOrder.Remove(playerId))
            {
                Debug.Log($"[TrackOrderManager] Removed player {playerId} from custom order (now {_customOrder.Count} players)");
                OnOrderChanged?.Invoke();
            }
        }

        /// <summary>
        /// Moves a player to a specific position in the custom order.
        /// </summary>
        public void MovePlayerToPosition(Guid playerId, int position)
        {
            int currentIndex = _customOrder.IndexOf(playerId);
            if (currentIndex < 0)
            {
                // Player not in custom order, add them
                _customOrder.Insert(Math.Min(position, _customOrder.Count), playerId);
            }
            else if (currentIndex != position)
            {
                _customOrder.RemoveAt(currentIndex);
                _customOrder.Insert(Math.Min(position, _customOrder.Count), playerId);
            }
            else
            {
                return; // No change
            }

            OnOrderChanged?.Invoke();
        }

        /// <summary>
        /// Gets the ordered list of player IDs for display.
        /// </summary>
        /// <param name="allPlayers">All players in the session.</param>
        /// <param name="bandManager">Optional band manager for band grouping.</param>
        /// <returns>Ordered list of player IDs.</returns>
        public IReadOnlyList<Guid> GetOrderedPlayerIds(
            IEnumerable<NetworkPlayerData> allPlayers,
            Bands.BandManager? bandManager = null)
        {
            var playerList = allPlayers.ToList();
            var result = new List<Guid>(playerList.Count);

            // If we have a custom order, use it as the base
            if (_customOrder.Count > 0)
            {
                // Add players in custom order
                foreach (var id in _customOrder)
                {
                    if (playerList.Any(p => p.NetworkPlayerId == id))
                    {
                        result.Add(id);
                    }
                }

                // Add any players not in custom order at the end
                foreach (var player in playerList)
                {
                    if (!_customOrder.Contains(player.NetworkPlayerId))
                    {
                        result.Add(player.NetworkPlayerId);
                    }
                }
            }
            else
            {
                // Default order - by player index
                result.AddRange(playerList.OrderBy(p => p.PlayerIndex).Select(p => p.NetworkPlayerId));
            }

            // Apply band grouping if enabled
            if (_groupByBand && bandManager != null && bandManager.IsBandSystemActive)
            {
                result = GroupPlayersByBand(result, bandManager);
            }

            // Apply local players first if enabled
            if (_localPlayersFirst && _localPlayerIds.Count > 0)
            {
                Debug.Log($"[TrackOrderManager] Applying LocalPlayersFirst: {_localPlayerIds.Count} local player IDs registered");
                foreach (var localId in _localPlayerIds)
                {
                    Debug.Log($"[TrackOrderManager]   - Local player ID: {localId}");
                }
                result = MoveLocalPlayersFirst(result);
                Debug.Log($"[TrackOrderManager] After MoveLocalPlayersFirst, order: [{string.Join(", ", result)}]");
            }
            else if (_localPlayersFirst)
            {
                Debug.LogWarning($"[TrackOrderManager] LocalPlayersFirst=true but _localPlayerIds is empty!");
            }

            return result;
        }

        /// <summary>
        /// Gets the track display index for a player.
        /// </summary>
        public int GetTrackIndex(Guid playerId, IEnumerable<NetworkPlayerData> allPlayers)
        {
            var ordered = GetOrderedPlayerIds(allPlayers);
            for (int i = 0; i < ordered.Count; i++)
            {
                if (ordered[i] == playerId)
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Exports the current custom order for network sync.
        /// </summary>
        public List<Guid> ExportCustomOrder()
        {
            return new List<Guid>(_customOrder);
        }

        /// <summary>
        /// Imports a custom order from network sync.
        /// </summary>
        public void ImportCustomOrder(List<Guid> order)
        {
            _customOrder.Clear();
            if (order != null)
            {
                _customOrder.AddRange(order);
            }
            OnOrderChanged?.Invoke();
        }

        /// <summary>
        /// Clears all state.
        /// </summary>
        public void Clear()
        {
            _customOrder.Clear();
            _localPlayerIds.Clear();
            _localPlayersFirst = false;
            _groupByBand = false;
            OnOrderChanged?.Invoke();
        }

        private List<Guid> MoveLocalPlayersFirst(List<Guid> players)
        {
            var local = players.Where(id => _localPlayerIds.Contains(id)).ToList();
            var remote = players.Where(id => !_localPlayerIds.Contains(id)).ToList();

            local.AddRange(remote);
            return local;
        }

        private List<Guid> GroupPlayersByBand(List<Guid> players, Bands.BandManager bandManager)
        {
            // Get local band first, then other bands
            int localBandId = bandManager.LocalPlayerBandId;

            var grouped = players
                .GroupBy(id => bandManager.GetPlayerBandId(id))
                .OrderBy(g => g.Key == localBandId ? 0 : 1) // Local band first
                .ThenBy(g => g.Key) // Then by band ID
                .SelectMany(g => g);

            return grouped.ToList();
        }
    }
}
