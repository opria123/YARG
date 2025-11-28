using System;
using System.Linq;
using YARG.Net.Directory;
using YARG.Net.Packets;
using YARG.Net.Sessions;

namespace YARG.Networking.NewNet
{
    /// <summary>
    /// Utility methods for converting between LiteNet directory/snapshot types
    /// and legacy <see cref="YargNetworkManager.LobbyInfo"/>.
    /// </summary>
    public static class LobbyInfoConverter
    {
        private const string LiteNetLibTransportId = "LiteNetLibTransport";

        /// <summary>
        /// Creates a <see cref="YargNetworkManager.LobbyInfo"/> from a
        /// <see cref="LobbyDirectoryEntry"/> and optionally enriches it
        /// with player data from a <see cref="LobbyStateSnapshot"/>.
        /// </summary>
        public static YargNetworkManager.LobbyInfo FromDirectoryEntry(
            LobbyDirectoryEntry entry,
            LobbyStateSnapshot? snapshot = null)
        {
            if (entry is null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            var players = snapshot?.Players;
            int currentPlayers = players?.Count ?? entry.CurrentPlayers;

            string[] playerNames = Array.Empty<string>();
            int[] playerInstruments = Array.Empty<int>();

            if (players is not null && players.Count > 0)
            {
                playerNames = new string[players.Count];
                playerInstruments = new int[players.Count];

                for (int i = 0; i < players.Count; i++)
                {
                    playerNames[i] = players[i].DisplayName ?? string.Empty;
                    // Instrument mapping not provided by LobbyPlayer; default to 0.
                    playerInstruments[i] = 0;
                }
            }

            return new YargNetworkManager.LobbyInfo
            {
                lobbyId = entry.LobbyId.ToString(),
                lobbyName = entry.LobbyName ?? string.Empty,
                hostName = entry.HostName ?? string.Empty,
                ipAddress = entry.Address ?? string.Empty,
                publicAddress = entry.Address ?? string.Empty,
                transportId = LiteNetLibTransportId,
                currentPlayers = currentPlayers,
                maxPlayers = entry.MaxPlayers,
                privacyMode = entry.HasPassword
                    ? YargNetworkManager.LobbyPrivacyMode.Private
                    : YargNetworkManager.LobbyPrivacyMode.Public,
                hasPassword = entry.HasPassword,
                password = string.Empty,
                isActive = true,
                port = entry.Port,
                publicPort = entry.Port,
                lastSeen = entry.LastHeartbeatUtc.ToUnixTimeMilliseconds(),
                playerNames = playerNames,
                playerInstruments = playerInstruments,
            };
        }

        /// <summary>
        /// Attempts to match a <see cref="LobbyStateSnapshot"/> by lobby ID
        /// to a set of directory entries and returns the merged <see cref="YargNetworkManager.LobbyInfo"/>.
        /// </summary>
        public static YargNetworkManager.LobbyInfo? TryMerge(
            LobbyDirectoryEntry entry,
            LobbyStateSnapshot? snapshot)
        {
            if (entry is null)
            {
                return null;
            }

            // Ensure snapshot is for the same lobby
            if (snapshot is not null && snapshot.LobbyId != entry.LobbyId)
            {
                snapshot = null;
            }

            return FromDirectoryEntry(entry, snapshot);
        }

        /// <summary>
        /// Enriches an existing <see cref="YargNetworkManager.LobbyInfo"/> with
        /// live player data from a <see cref="LobbyStateSnapshot"/>.
        /// </summary>
        public static void ApplySnapshot(
            YargNetworkManager.LobbyInfo lobbyInfo,
            LobbyStateSnapshot snapshot)
        {
            if (lobbyInfo is null || snapshot is null)
            {
                return;
            }

            var players = snapshot.Players;
            if (players is null || players.Count == 0)
            {
                return;
            }

            lobbyInfo.currentPlayers = players.Count;
            lobbyInfo.playerNames = new string[players.Count];
            lobbyInfo.playerInstruments = new int[players.Count];

            for (int i = 0; i < players.Count; i++)
            {
                lobbyInfo.playerNames[i] = players[i].DisplayName ?? string.Empty;
                lobbyInfo.playerInstruments[i] = 0;
            }

            // Update last seen to now
            lobbyInfo.lastSeen = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        /// <summary>
        /// Determines if the given <see cref="YargNetworkManager.LobbyInfo"/>
        /// was created from a LiteNet directory entry.
        /// </summary>
        public static bool IsLiteNetLobby(YargNetworkManager.LobbyInfo lobbyInfo)
        {
            if (lobbyInfo is null)
            {
                return false;
            }

            return string.Equals(
                lobbyInfo.transportId ?? string.Empty,
                LiteNetLibTransportId,
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
