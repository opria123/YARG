using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using YARG.Core.Game;
using YARG.Core.Logging;
using YARG.Menu.MusicLibrary;
using YARG.Net.Packets;
using YARG.Player;
using YARG.Helpers;

namespace YARG.Networking.Abstraction.Handlers
{
    /// <summary>
    /// Handles ready state management for networked players.
    /// Encapsulates ready state broadcasting, receiving, and all-players-ready detection.
    /// </summary>
    public class ReadyStateHandler
    {
        // Cached reflection fields for performance
        private readonly FieldInfo _isReadyField;
        private readonly FieldInfo _instrumentField;
        private readonly FieldInfo _difficultyField;
        private readonly FieldInfo _onReadyStateChangedEventField;
        private readonly FieldInfo _onInstrumentChangedEventField;
        
        /// <summary>
        /// Event fired when a player's ready state changes.
        /// </summary>
        public event Action<string, bool> OnPlayerReadyStateChanged;
        
        /// <summary>
        /// Event fired when all players are ready.
        /// </summary>
        public event Action OnAllPlayersReady;

        public ReadyStateHandler()
        {
            // Cache reflection fields once
            var playerDataType = typeof(NetworkPlayerData);
            var bindingFlags = BindingFlags.NonPublic | BindingFlags.Instance;
            
            _isReadyField = playerDataType.GetField("isReady", bindingFlags);
            _instrumentField = playerDataType.GetField("instrument", bindingFlags);
            _difficultyField = playerDataType.GetField("difficulty", bindingFlags);
            _onReadyStateChangedEventField = playerDataType.GetField("OnReadyStateChanged", bindingFlags);
            _onInstrumentChangedEventField = playerDataType.GetField("OnInstrumentChanged", bindingFlags);
            
            if (_isReadyField == null)
                Debug.LogWarning("[ReadyStateHandler] Could not find isReady field on NetworkPlayerData");
            if (_onInstrumentChangedEventField == null)
                Debug.LogWarning("[ReadyStateHandler] Could not find OnInstrumentChanged field on NetworkPlayerData");
        }

        #region Public API

        /// <summary>
        /// Gets the local player's name and instrument/difficulty for ready state messages.
        /// </summary>
        public (string name, int instrument, int difficulty) GetLocalPlayerInfo()
        {
            string playerName = GetPlayerNameFromProfile();
            int instrumentValue = 0;
            int difficultyValue = 0;
            
            if (PlayerContainer.Players.Count > 0)
            {
                var localPlayer = PlayerContainer.Players[0];
                instrumentValue = (int)localPlayer.Profile.CurrentInstrument;
                difficultyValue = (int)localPlayer.Profile.CurrentDifficulty;
            }
            
            return (playerName, instrumentValue, difficultyValue);
        }

        /// <summary>
        /// Sets a player's ready state using reflection.
        /// </summary>
        public void SetPlayerReadyState(NetworkPlayerData player, bool isReady)
        {
            if (player == null || _isReadyField == null) return;
            
            _isReadyField.SetValue(player, isReady);
            Debug.Log($"[ReadyStateHandler] Set player '{player.PlayerName}' isReady to {isReady}");
            
            // Fire the event on the player data
            if (_onReadyStateChangedEventField != null)
            {
                var eventDelegate = _onReadyStateChangedEventField.GetValue(player) as Action<bool>;
                eventDelegate?.Invoke(isReady);
            }
        }

        /// <summary>
        /// Sets a player's instrument and difficulty using reflection.
        /// </summary>
        public void SetPlayerInstrumentAndDifficulty(NetworkPlayerData player, int instrument, int difficulty)
        {
            if (player == null) return;
            
            if (_instrumentField != null)
            {
                _instrumentField.SetValue(player, instrument);
                Debug.Log($"[ReadyStateHandler] Set instrument field to {instrument}");
            }
            else
            {
                Debug.LogWarning("[ReadyStateHandler] _instrumentField is null!");
            }
            if (_difficultyField != null)
            {
                _difficultyField.SetValue(player, difficulty);
                Debug.Log($"[ReadyStateHandler] Set difficulty field to {difficulty}");
            }
            else
            {
                Debug.LogWarning("[ReadyStateHandler] _difficultyField is null!");
            }
            
            Debug.Log($"[ReadyStateHandler] Set player '{player.PlayerName}' instrument to {instrument}, difficulty to {difficulty}");
            
            // Fire the events
            if (_onInstrumentChangedEventField != null)
            {
                var eventDelegate = _onInstrumentChangedEventField.GetValue(player) as Action<int, int>;
                if (eventDelegate != null)
                {
                    Debug.Log($"[ReadyStateHandler] Firing OnInstrumentChangedEvent with ({instrument}, {difficulty})");
                    eventDelegate.Invoke(instrument, difficulty);
                }
                else
                {
                    Debug.LogWarning($"[ReadyStateHandler] OnInstrumentChangedEvent delegate is null for player '{player.PlayerName}'");
                }
            }
            else
            {
                Debug.LogWarning("[ReadyStateHandler] _onInstrumentChangedEventField is null!");
            }
        }

        /// <summary>
        /// Resets all players' ready states to false without firing events.
        /// Used when transitioning to gameplay so ready states don't persist to score screen.
        /// </summary>
        public void ResetAllPlayerReadyStates(Dictionary<object, List<NetworkPlayerData>> connectedPlayers)
        {
            if (_isReadyField == null)
            {
                Debug.LogWarning("[ReadyStateHandler] Could not find isReady field for reset");
                return;
            }
            
            foreach (var kvp in connectedPlayers)
            {
                foreach (var player in kvp.Value)
                {
                    if (player != null)
                    {
                        _isReadyField.SetValue(player, false);
                        Debug.Log($"[ReadyStateHandler] Reset ready state for player '{player.PlayerName}'");
                    }
                }
            }
        }

        /// <summary>
        /// Resets all players' gameplay state (score, combo, etc.) for starting a new song.
        /// </summary>
        public void ResetAllPlayersGameState(Dictionary<object, List<NetworkPlayerData>> connectedPlayers)
        {
            Debug.Log("[ReadyStateHandler] Resetting all players' gameplay state for new song");
            
            foreach (var kvp in connectedPlayers)
            {
                foreach (var player in kvp.Value)
                {
                    if (player != null)
                    {
                        player.ResetGameState();
                        Debug.Log($"[ReadyStateHandler] Reset gameplay state for player '{player.PlayerName}'");
                    }
                }
            }
        }

        /// <summary>
        /// Checks if all players are ready and fires the OnAllPlayersReady event if so.
        /// </summary>
        public bool CheckAllPlayersReady(Dictionary<object, List<NetworkPlayerData>> connectedPlayers)
        {
            if (_isReadyField == null) return false;
            
            bool allReady = true;
            int playerCount = 0;
            
            foreach (var kvp in connectedPlayers)
            {
                foreach (var player in kvp.Value)
                {
                    if (player == null) continue;
                    playerCount++;
                    
                    bool isReady = (bool)_isReadyField.GetValue(player);
                    if (!isReady)
                    {
                        allReady = false;
                        Debug.Log($"[ReadyStateHandler] Player '{player.PlayerName}' is NOT ready");
                    }
                }
            }
            
            if (allReady && playerCount > 0)
            {
                Debug.Log($"[ReadyStateHandler] All {playerCount} players are ready!");
                OnAllPlayersReady?.Invoke();
                return true;
            }
            
            return false;
        }

        #endregion

        #region Message Building

        /// <summary>
        /// Builds a ready state message to send from client to host.
        /// Uses YARG.Net binary packet builder.
        /// </summary>
        public byte[] BuildClientReadyMessage(string playerName, bool isReady, int instrument, int difficulty, bool sittingOut = false)
        {
            return ReadyStateBinaryPackets.BuildClientReadyPacket(playerName, isReady, instrument, difficulty, sittingOut);
        }

        /// <summary>
        /// Builds a ready state broadcast message from host to clients.
        /// Uses YARG.Net binary packet builder.
        /// </summary>
        public byte[] BuildHostBroadcastMessage(string playerName, bool isReady, bool isLocalPlayer, int instrument, int difficulty, bool sittingOut = false, Guid networkPlayerId = default)
        {
            return ReadyStateBinaryPackets.BuildHostBroadcastPacket(playerName, isReady, isLocalPlayer, instrument, difficulty, sittingOut, networkPlayerId);
        }

        /// <summary>
        /// Builds an all-players-ready notification message.
        /// Uses YARG.Net binary packet builder.
        /// </summary>
        public byte[] BuildAllPlayersReadyMessage()
        {
            return ReadyStateBinaryPackets.BuildAllPlayersReadyPacket();
        }

        #endregion

        #region Message Parsing

        /// <summary>
        /// Result of parsing a ready state message from a client.
        /// </summary>
        public struct ClientReadyMessageData
        {
            public bool IsValid;
            public bool IsReady;
            public string PlayerName;
            public int Instrument;
            public int Difficulty;
            public bool HasInstrumentData;
            public bool SittingOut;
        }

        /// <summary>
        /// Parses a ready state message sent from a client.
        /// Uses YARG.Net binary packet parser.
        /// </summary>
        public ClientReadyMessageData ParseClientReadyMessage(ReadOnlyMemory<byte> payload)
        {
            var parsed = ReadyStateBinaryPackets.ParseClientReadyPacket(payload.Span);
            
            return new ClientReadyMessageData
            {
                IsValid = parsed.IsValid,
                IsReady = parsed.IsReady,
                PlayerName = parsed.PlayerName,
                Instrument = parsed.Instrument,
                Difficulty = parsed.Difficulty,
                HasInstrumentData = parsed.HasInstrumentData,
                SittingOut = parsed.SittingOut
            };
        }

        /// <summary>
        /// Result of parsing a ready state broadcast message from the host.
        /// </summary>
        public struct HostBroadcastMessageData
        {
            public bool IsValid;
            public bool IsReady;
            public string PlayerName;
            public bool IsLocalPlayer;
            public int Instrument;
            public int Difficulty;
            public bool SittingOut;
            public Guid NetworkPlayerId;
        }

        /// <summary>
        /// Parses a ready state broadcast message received from the host.
        /// Uses YARG.Net binary packet parser.
        /// </summary>
        public HostBroadcastMessageData ParseHostBroadcastMessage(ReadOnlyMemory<byte> payload)
        {
            var parsed = ReadyStateBinaryPackets.ParseHostBroadcastPacket(payload.Span);
            
            return new HostBroadcastMessageData
            {
                IsValid = parsed.IsValid,
                IsReady = parsed.IsReady,
                PlayerName = parsed.PlayerName,
                IsLocalPlayer = parsed.IsLocalPlayer,
                Instrument = parsed.Instrument,
                Difficulty = parsed.Difficulty,
                SittingOut = parsed.SittingOut,
                NetworkPlayerId = parsed.NetworkPlayerId
            };
        }

        #endregion

        #region Host-side Handling

        /// <summary>
        /// Handles a ready state message on the host, updating the player and broadcasting.
        /// Returns the player that was updated, or null if not found.
        /// </summary>
        public NetworkPlayerData HandleReadyMessageOnHost(
            string connectionKey,
            ClientReadyMessageData data,
            Dictionary<object, List<NetworkPlayerData>> connectedPlayers)
        {
            NetworkPlayerData targetPlayer = null;
            
            if (connectedPlayers.TryGetValue(connectionKey, out var players))
            {
                // Find the player in this connection's player list
                foreach (var player in players)
                {
                    if (player.PlayerName == data.PlayerName)
                    {
                        targetPlayer = player;
                        break;
                    }
                }
                
                // If no exact name match, just use the first player from this connection
                if (targetPlayer == null && players.Count > 0)
                {
                    targetPlayer = players[0];
                    Debug.Log($"[ReadyStateHandler] Host: No exact name match for '{data.PlayerName}' in connection {connectionKey}, using first player: {targetPlayer.PlayerName}");
                }
            }
            
            if (targetPlayer != null)
            {
                Debug.Log($"[ReadyStateHandler] Host: Setting ready state for player '{targetPlayer.PlayerName}' (connection: {connectionKey}) to {data.IsReady}");
                SetPlayerReadyState(targetPlayer, data.IsReady);
                
                // Also update instrument/difficulty if we received that data
                if (data.HasInstrumentData)
                {
                    SetPlayerInstrumentAndDifficulty(targetPlayer, data.Instrument, data.Difficulty);
                }
                
                // Fire the event
                OnPlayerReadyStateChanged?.Invoke(targetPlayer.PlayerName, data.IsReady);
            }
            else
            {
                Debug.LogWarning($"[ReadyStateHandler] Host: Could not find player for connection {connectionKey}");
            }
            
            return targetPlayer;
        }

        /// <summary>
        /// Updates the local player's ready state (for host's own player).
        /// </summary>
        public void UpdateLocalPlayerReadyState(
            bool isHosting,
            bool isReady,
            int instrument,
            int difficulty,
            Dictionary<object, List<NetworkPlayerData>> connectedPlayers)
        {
            // Find local player and update ready state
            foreach (var kvp in connectedPlayers)
            {
                foreach (var player in kvp.Value)
                {
                    // Find the local player (not host, or host's own player)
                    bool isLocalPlayer = false;
                    var keyStr = kvp.Key.ToString();
                    if (isHosting && keyStr == "host")
                    {
                        isLocalPlayer = true;
                    }
                    else if (!isHosting && keyStr == "local")
                    {
                        isLocalPlayer = true;
                    }
                    
                    if (isLocalPlayer)
                    {
                        SetPlayerReadyState(player, isReady);
                        if (instrument >= 0 && difficulty >= 0)
                        {
                            SetPlayerInstrumentAndDifficulty(player, instrument, difficulty);
                        }
                        return;
                    }
                }
            }
        }

        #endregion

        #region Client-side Handling

        /// <summary>
        /// Handles a ready state broadcast on the client.
        /// Returns the player that was updated, or null if no matching player was found.
        /// Uses the IsLocalPlayer flag to determine which bucket to search - this is the key
        /// to distinguishing same-named players on different connections.
        /// </summary>
        public NetworkPlayerData HandleBroadcastOnClient(
            HostBroadcastMessageData data,
            Dictionary<object, List<NetworkPlayerData>> connectedPlayers)
        {
            // IsLocalPlayer tells us if this update is about OUR local players or about remote players
            NetworkPlayerData foundPlayer = null;
            
            if (data.IsLocalPlayer)
            {
                // This is about our own local player - search only in "local" bucket
                if (connectedPlayers.TryGetValue("local", out var localPlayers))
                {
                    foreach (var player in localPlayers)
                    {
                        if (player.PlayerName == data.PlayerName)
                        {
                            foundPlayer = player;
                            break;
                        }
                    }
                    
                    // Backward compat: if only one local player and no exact match, use it
                    if (foundPlayer == null && localPlayers.Count == 1)
                    {
                        foundPlayer = localPlayers[0];
                        Debug.Log($"[ReadyStateHandler] Client: No exact name match for local player '{data.PlayerName}', using first local player: {foundPlayer.PlayerName}");
                    }
                }
            }
            else
            {
                // This is about a remote player - search ALL non-local buckets
                // This works for both regular hosting (finds in "host" bucket) and 
                // dedicated server mode (finds in "remote" or connection-GUID buckets)
                foreach (var kvp in connectedPlayers)
                {
                    string keyStr = kvp.Key.ToString();
                    
                    // Skip the local bucket - we only want remote players
                    if (keyStr == "local")
                        continue;
                    
                    foreach (var player in kvp.Value)
                    {
                        if (player.PlayerName == data.PlayerName)
                        {
                            foundPlayer = player;
                            Debug.Log($"[ReadyStateHandler] Client: Found remote player '{data.PlayerName}' in bucket '{keyStr}'");
                            break;
                        }
                    }
                    
                    if (foundPlayer != null)
                        break;
                }
                
                // If no exact match found, check for "Unknown" placeholder in "host" bucket (regular hosting)
                if (foundPlayer == null && connectedPlayers.TryGetValue("host", out var hostPlayers))
                {
                    foreach (var player in hostPlayers)
                    {
                        if (player.PlayerName == "Unknown")
                        {
                            Debug.Log($"[ReadyStateHandler] Client: Updating 'Unknown' placeholder to '{data.PlayerName}'");
                            player.PlayerName = data.PlayerName;
                            foundPlayer = player;
                            break;
                        }
                    }
                }
            }
            
            if (foundPlayer != null)
            {
                Debug.Log($"[ReadyStateHandler] Client: Updating ready state for '{foundPlayer.PlayerName}' to {data.IsReady}");
                
                SetPlayerReadyState(foundPlayer, data.IsReady);
                
                // Update instrument/difficulty for remote players (not local)
                // Local players control their own instruments
                if (!data.IsLocalPlayer && data.Instrument >= 0)
                {
                    Debug.Log($"[ReadyStateHandler] Client: Updating instrument/difficulty for remote player");
                    SetPlayerInstrumentAndDifficulty(foundPlayer, data.Instrument, data.Difficulty);
                }
                
                OnPlayerReadyStateChanged?.Invoke(data.PlayerName, data.IsReady);
            }
            
            // If we didn't find a player and this is NOT a local player update,
            // return null so the caller can create a new remote entry
            if (foundPlayer == null && !data.IsLocalPlayer)
            {
                Debug.Log($"[ReadyStateHandler] Client: No matching remote player found for '{data.PlayerName}' - caller should create new entry");
            }
            
            return foundPlayer;
        }

        #endregion

        #region Helper Methods

        private string GetPlayerNameFromProfile()
        {
            if (PlayerContainer.Players.Count > 0)
            {
                return PlayerContainer.Players[0].Profile.Name;
            }
            return "Player";
        }

        #endregion
    }
}
