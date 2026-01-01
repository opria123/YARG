using System;
using YARG.Multiplayer;
using YARG.Net.Transport;
using YARG.Networking.Abstraction.Handlers;

namespace YARG.Networking.Abstraction.Managers
{
    /// <summary>
    /// Container interface providing access to all network managers.
    /// Use this to access decomposed networking functionality.
    /// </summary>
    public interface INetworkManagers : IDisposable
    {
        /// <summary>Manages player tracking and state.</summary>
        NetworkPlayerManager Players { get; }
        
        /// <summary>Manages lobby lifecycle.</summary>
        NetworkLobbyManager Lobby { get; }
        
        /// <summary>Manages gameplay synchronization.</summary>
        NetworkGameplayManager Gameplay { get; }
    }

    /// <summary>
    /// Implementation of INetworkManagers that creates and wires up all managers.
    /// </summary>
    public sealed class NetworkManagers : INetworkManagers
    {
        public NetworkPlayerManager Players { get; }
        public NetworkLobbyManager Lobby { get; }
        public NetworkGameplayManager Gameplay { get; }

        /// <summary>
        /// Creates a new NetworkManagers instance with all managers initialized.
        /// </summary>
        /// <param name="discovery">LiteNet discovery instance for lobby discovery.</param>
        /// <param name="defaultPort">Default port for networking.</param>
        /// <param name="createPlayerData">Factory function for creating NetworkPlayerData.</param>
        public NetworkManagers(
            LiteNetDiscovery discovery,
            int defaultPort,
            Func<string, bool, bool, int, int, Guid, NetworkPlayerData> createPlayerData)
        {
            // Create managers
            Players = new NetworkPlayerManager(createPlayerData);
            Lobby = new NetworkLobbyManager(discovery, defaultPort);
            Gameplay = new NetworkGameplayManager(() => Players.GetConnectedPlayers());
            
            // Wire up cross-manager events
            WireManagerEvents();
            
            NetworkLogger.Info("NetworkManagers initialized");
        }

        private void WireManagerEvents()
        {
            // When a player joins, update lobby player count
            Players.OnPlayerJoined += player =>
            {
                Lobby.UpdatePlayerCount(1);
            };
            
            // When a player leaves, update lobby player count
            Players.OnPlayerLeft += player =>
            {
                Lobby.UpdatePlayerCount(-1);
            };
            
            // When all players are ready, check for auto-start
            Players.OnAllPlayersReady += () =>
            {
                Gameplay.CheckAutoStart(Lobby.IsHosting);
            };
        }

        public void Dispose()
        {
            Players?.Dispose();
            Lobby?.Dispose();
            Gameplay?.Dispose();
            
            NetworkLogger.Info("NetworkManagers disposed");
        }
    }
}
