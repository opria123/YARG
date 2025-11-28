using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using YARG.Core.Song;
using YARG.Multiplayer;

namespace YARG.Networking.Abstraction
{
    /// <summary>
    /// Abstraction layer for networking services. Allows swapping between different
    /// networking implementations (Mirror, LiteNetLib, etc.) without changing game code.
    /// </summary>
    public interface INetworkingService
    {
        #region Properties

        /// <summary>
        /// Maximum number of players allowed in a lobby.
        /// </summary>
        int MaxPlayers { get; }

        /// <summary>
        /// Maximum number of local players per client.
        /// </summary>
        int MaxLocalPlayersPerClient { get; }

        /// <summary>
        /// Information about the current lobby (if hosting or connected).
        /// </summary>
        LobbyInfo CurrentLobby { get; }

        /// <summary>
        /// The player's display name.
        /// </summary>
        string PlayerName { get; }

        /// <summary>
        /// Whether the local player is hosting a lobby.
        /// </summary>
        bool IsHosting { get; }

        /// <summary>
        /// Whether this is a dedicated server instance.
        /// </summary>
        bool IsDedicatedServer { get; }

        /// <summary>
        /// Whether networking is active (hosting or connected to a lobby).
        /// </summary>
        bool IsNetworkActive { get; }

        /// <summary>
        /// Whether the local player is connected to a remote lobby (not hosting).
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// Whether a join operation is currently in progress.
        /// </summary>
        bool IsJoinInProgress { get; }

        /// <summary>
        /// Default port for hosting/joining.
        /// </summary>
        int DefaultPort { get; }

        #endregion

        #region Events

        /// <summary>
        /// Fired when a lobby is successfully created by this client.
        /// </summary>
        event Action<LobbyInfo> OnLobbyCreated;

        /// <summary>
        /// Fired when the client successfully joins a lobby.
        /// </summary>
        event Action<LobbyInfo> OnLobbyJoined;

        /// <summary>
        /// Fired when the client leaves a lobby.
        /// </summary>
        event Action OnLobbyLeft;

        /// <summary>
        /// Fired when the list of discovered lobbies is updated.
        /// </summary>
        event Action<List<LobbyInfo>> OnLobbyListUpdated;

        /// <summary>
        /// Fired when a player joins the lobby.
        /// </summary>
        event Action<NetworkPlayerData> OnPlayerJoined;

        /// <summary>
        /// Fired when a player leaves the lobby.
        /// </summary>
        event Action<NetworkPlayerData> OnPlayerLeft;

        /// <summary>
        /// Fired when a network error occurs.
        /// </summary>
        event Action<string> OnNetworkError;

        #endregion

        #region Lobby Management

        /// <summary>
        /// Create and host a new lobby.
        /// </summary>
        /// <param name="lobbyName">Display name for the lobby</param>
        /// <param name="maxPlayers">Maximum number of players</param>
        /// <param name="privacyMode">Public or private lobby</param>
        /// <param name="password">Password for private lobbies</param>
        /// <returns>Information about the created lobby</returns>
        LobbyInfo CreateLobby(string lobbyName, int maxPlayers, LobbyPrivacyMode privacyMode, string password = "");

        /// <summary>
        /// Join an existing lobby by address.
        /// </summary>
        /// <param name="endpoint">IP:Port or hostname to connect to</param>
        /// <param name="password">Password if required</param>
        void JoinLobby(string endpoint, string password = "");

        /// <summary>
        /// Join a discovered lobby.
        /// </summary>
        /// <param name="lobby">Lobby information from discovery</param>
        /// <param name="password">Password if required</param>
        void JoinDiscoveredLobby(LobbyInfo lobby, string password = "");

        /// <summary>
        /// Leave the current lobby.
        /// </summary>
        void LeaveLobby();

        /// <summary>
        /// Probe a lobby to get information without joining.
        /// </summary>
        /// <param name="address">IP address</param>
        /// <param name="port">Port number</param>
        /// <returns>Lobby information if successful, null otherwise</returns>
        Task<LobbyInfo?> ProbeLobby(string address, int port);

        #endregion

        #region Player Management

        /// <summary>
        /// Update the local player's display name.
        /// </summary>
        void SetPlayerName(string name);

        /// <summary>
        /// Get all connected players with their data.
        /// </summary>
        Dictionary<object, List<NetworkPlayerData>> GetConnectedPlayers();

        #endregion

        #region Song Selection and Gameplay

        /// <summary>
        /// Start the song selection phase.
        /// </summary>
        void StartSongSelection();

        /// <summary>
        /// Select a song for multiplayer gameplay.
        /// </summary>
        void StartMultiplayerSong(SongEntry song);

        /// <summary>
        /// Begin multiplayer gameplay for the selected song.
        /// </summary>
        void StartMultiplayerGameplay();

        #endregion

        #region Lifecycle

        /// <summary>
        /// Initialize the networking service.
        /// </summary>
        void Initialize();

        /// <summary>
        /// Update tick for the networking service (called each frame).
        /// </summary>
        void Update();

        /// <summary>
        /// Cleanup when the service is destroyed.
        /// </summary>
        void Shutdown();

        #endregion
    }
}
