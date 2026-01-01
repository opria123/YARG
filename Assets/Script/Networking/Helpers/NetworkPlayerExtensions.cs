using System;
using System.Collections.Generic;
using System.Linq;
using YARG.Multiplayer;
using YARG.Networking.Abstraction;

namespace YARG.Networking.Helpers
{
    /// <summary>
    /// LINQ extension methods for working with network players.
    /// Reduces boilerplate code for common player iteration patterns.
    /// </summary>
    public static class NetworkPlayerExtensions
    {
        #region Safe Iteration

        /// <summary>
        /// Gets all players from the connected players dictionary, safely handling nulls.
        /// </summary>
        /// <param name="connectedPlayers">The connected players dictionary.</param>
        /// <returns>Enumerable of all players across all connections.</returns>
        public static IEnumerable<NetworkPlayerData> GetAllPlayersSafe(
            this Dictionary<object, List<NetworkPlayerData>> connectedPlayers)
        {
            if (connectedPlayers == null) yield break;
            
            foreach (var kvp in connectedPlayers)
            {
                if (kvp.Value == null) continue;
                foreach (var player in kvp.Value)
                {
                    if (player != null)
                        yield return player;
                }
            }
        }

        /// <summary>
        /// Gets all players from the networking service, safely handling nulls.
        /// </summary>
        /// <param name="service">The networking service.</param>
        /// <returns>Enumerable of all players.</returns>
        public static IEnumerable<NetworkPlayerData> GetAllPlayersSafe(this INetworkingService service)
        {
            if (service == null) return Enumerable.Empty<NetworkPlayerData>();
            return service.GetConnectedPlayers().GetAllPlayersSafe();
        }

        #endregion

        #region Filtering

        /// <summary>
        /// Gets all local players (players on this machine).
        /// </summary>
        public static IEnumerable<NetworkPlayerData> GetLocalPlayers(
            this Dictionary<object, List<NetworkPlayerData>> connectedPlayers)
        {
            return connectedPlayers.GetAllPlayersSafe().Where(p => p.IsLocalUser);
        }

        /// <summary>
        /// Gets all local players from the networking service.
        /// </summary>
        public static IEnumerable<NetworkPlayerData> GetLocalPlayers(this INetworkingService service)
        {
            return service.GetAllPlayersSafe().Where(p => p.IsLocalUser);
        }

        /// <summary>
        /// Gets all remote players (players on other machines).
        /// </summary>
        public static IEnumerable<NetworkPlayerData> GetRemotePlayers(
            this Dictionary<object, List<NetworkPlayerData>> connectedPlayers)
        {
            return connectedPlayers.GetAllPlayersSafe().Where(p => !p.IsLocalUser);
        }

        /// <summary>
        /// Gets all remote players from the networking service.
        /// </summary>
        public static IEnumerable<NetworkPlayerData> GetRemotePlayers(this INetworkingService service)
        {
            return service.GetAllPlayersSafe().Where(p => !p.IsLocalUser);
        }

        /// <summary>
        /// Gets all players who are ready.
        /// </summary>
        public static IEnumerable<NetworkPlayerData> GetReadyPlayers(
            this Dictionary<object, List<NetworkPlayerData>> connectedPlayers)
        {
            return connectedPlayers.GetAllPlayersSafe().Where(p => p.IsReady);
        }

        /// <summary>
        /// Gets all players who are not ready.
        /// </summary>
        public static IEnumerable<NetworkPlayerData> GetNotReadyPlayers(
            this Dictionary<object, List<NetworkPlayerData>> connectedPlayers)
        {
            return connectedPlayers.GetAllPlayersSafe().Where(p => !p.IsReady);
        }

        /// <summary>
        /// Gets all players who are sitting out.
        /// </summary>
        public static IEnumerable<NetworkPlayerData> GetSittingOutPlayers(
            this Dictionary<object, List<NetworkPlayerData>> connectedPlayers)
        {
            return connectedPlayers.GetAllPlayersSafe().Where(p => p.SittingOut);
        }

        /// <summary>
        /// Gets all players who are actively playing (ready and not sitting out).
        /// </summary>
        public static IEnumerable<NetworkPlayerData> GetActivePlayers(
            this Dictionary<object, List<NetworkPlayerData>> connectedPlayers)
        {
            return connectedPlayers.GetAllPlayersSafe().Where(p => p.IsReady && !p.SittingOut);
        }

        #endregion

        #region Lookup

        /// <summary>
        /// Finds a player by their network player ID.
        /// </summary>
        public static NetworkPlayerData FindByNetworkId(
            this Dictionary<object, List<NetworkPlayerData>> connectedPlayers,
            Guid networkPlayerId)
        {
            return connectedPlayers.GetAllPlayersSafe()
                .FirstOrDefault(p => p.NetworkPlayerId == networkPlayerId);
        }

        /// <summary>
        /// Finds a player by their name.
        /// </summary>
        public static NetworkPlayerData FindByName(
            this Dictionary<object, List<NetworkPlayerData>> connectedPlayers,
            string playerName)
        {
            return connectedPlayers.GetAllPlayersSafe()
                .FirstOrDefault(p => string.Equals(p.PlayerName, playerName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Finds all players from a specific connection.
        /// </summary>
        public static IEnumerable<NetworkPlayerData> GetPlayersFromConnection(
            this Dictionary<object, List<NetworkPlayerData>> connectedPlayers,
            object connectionKey)
        {
            if (connectedPlayers == null || connectionKey == null) 
                return Enumerable.Empty<NetworkPlayerData>();
            
            if (connectedPlayers.TryGetValue(connectionKey, out var players) && players != null)
                return players.Where(p => p != null);
            
            return Enumerable.Empty<NetworkPlayerData>();
        }

        #endregion

        #region Aggregation

        /// <summary>
        /// Gets the total player count.
        /// </summary>
        public static int GetPlayerCount(
            this Dictionary<object, List<NetworkPlayerData>> connectedPlayers)
        {
            return connectedPlayers.GetAllPlayersSafe().Count();
        }

        /// <summary>
        /// Gets the count of ready players.
        /// </summary>
        public static int GetReadyPlayerCount(
            this Dictionary<object, List<NetworkPlayerData>> connectedPlayers)
        {
            return connectedPlayers.GetReadyPlayers().Count();
        }



        #endregion

        #region Actions

        /// <summary>
        /// Executes an action on each player.
        /// </summary>
        public static void ForEachPlayer(
            this Dictionary<object, List<NetworkPlayerData>> connectedPlayers,
            Action<NetworkPlayerData> action)
        {
            if (action == null) return;
            foreach (var player in connectedPlayers.GetAllPlayersSafe())
            {
                action(player);
            }
        }

        /// <summary>
        /// Executes an action on each player with their connection key.
        /// </summary>
        public static void ForEachPlayerWithConnection(
            this Dictionary<object, List<NetworkPlayerData>> connectedPlayers,
            Action<object, NetworkPlayerData> action)
        {
            if (connectedPlayers == null || action == null) return;
            
            foreach (var kvp in connectedPlayers)
            {
                if (kvp.Value == null) continue;
                foreach (var player in kvp.Value)
                {
                    if (player != null)
                        action(kvp.Key, player);
                }
            }
        }

        /// <summary>
        /// Sets all players to not ready.
        /// </summary>
        public static void ResetAllReadyStates(
            this Dictionary<object, List<NetworkPlayerData>> connectedPlayers)
        {
            connectedPlayers.ForEachPlayer(p => p.IsReady = false);
        }

        #endregion
    }
}
