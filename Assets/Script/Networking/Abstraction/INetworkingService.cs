using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using YARG.Core.Song;
using YARG.Multiplayer;
using YARG.Networking.Settings;

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

        /// <summary>
        /// Whether any local player is the designated host.
        /// For dedicated servers, this is the player who can select songs.
        /// For regular lobbies, this is the same as IsHosting.
        /// </summary>
        bool IsLocalPlayerHost { get; }

        /// <summary>
        /// Whether this client has authority to perform host actions.
        /// This is the unified property that should be used by menu code instead of
        /// checking IsHosting or IsLocalPlayerHost directly.
        /// 
        /// Returns true if:
        /// - In-game hosting: We are the server (IsHosting)
        /// - Dedicated server: We are the designated host player (IsLocalPlayerHost)
        /// - Regular client: Always false
        /// 
        /// Use this for permission checks in UI code.
        /// </summary>
        bool HasHostAuthority { get; }

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

        /// <summary>
        /// Fired when session settings are received from the host.
        /// Parameters: lobbyName, maxPlayers, privacyMode, bandSize, noFailMode, sharedSongsOnly, 
        /// allowModifiers, enablePresetSync, allowLateJoin, allowedGameModes, localPlayersFirst
        /// </summary>
        event Action<string, int, byte, int, bool, bool, bool, bool, bool, List<int>, bool> OnSessionSettingsReceived;

        /// <summary>
        /// Fired when the designated host changes (for dedicated server mode).
        /// Parameter: The NetworkPlayerId of the new host player.
        /// </summary>
        event Action<Guid> OnHostChanged;

        #endregion

        #region Lobby Management

        /// <summary>
        /// Set whether this instance will operate as a dedicated server.
        /// Must be called before CreateLobby() to take effect.
        /// </summary>
        /// <param name="isDedicated">True for dedicated server mode</param>
        void SetDedicatedServerMode(bool isDedicated);

        /// <summary>
        /// Set the port to use for hosting.
        /// Must be called before CreateLobby() to take effect.
        /// </summary>
        /// <param name="port">The port number to listen on</param>
        void SetServerPort(int port);

        /// <summary>
        /// Create and host a new lobby.
        /// </summary>
        /// <param name="lobbyName">Display name for the lobby</param>
        /// <param name="maxPlayers">Maximum number of players</param>
        /// <param name="privacyMode">Public or private lobby</param>
        /// <param name="sessionType">Server (manual port forward) or Lobby (automatic UPnP)</param>
        /// <param name="password">Password for private lobbies</param>
        /// <returns>Information about the created lobby</returns>
        LobbyInfo CreateLobby(string lobbyName, int maxPlayers, LobbyPrivacyMode privacyMode, SessionType sessionType = SessionType.Lobby, string password = "");

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
        
        #region Discovery

        /// <summary>
        /// Start discovery to find lobbies on the network.
        /// </summary>
        void StartDiscovery();
        
        /// <summary>
        /// Stop discovery.
        /// </summary>
        void StopDiscovery();
        
        /// <summary>
        /// Send a discovery request to a specific address and port.
        /// </summary>
        /// <param name="address">IP address or hostname</param>
        /// <param name="port">Port number (0 to use default)</param>
        void SendDiscoveryRequest(string address, int port = 0);
        
        /// <summary>
        /// Send a broadcast discovery request to find lobbies on the local network.
        /// </summary>
        /// <param name="port">Port number (0 to use default)</param>
        void SendBroadcastDiscoveryRequest(int port = 0);
        
        /// <summary>
        /// Configure the discovery port.
        /// </summary>
        /// <param name="port">The port to use for discovery (same as game port for single-port forwarding)</param>
        void SetDiscoveryPort(int port);
        
        /// <summary>
        /// Get the current discovery port.
        /// </summary>
        int DiscoveryPort { get; }

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

        /// <summary>
        /// Kick a player from the lobby (host only).
        /// </summary>
        /// <param name="playerData">The player to kick</param>
        void KickPlayer(NetworkPlayerData playerData);

        /// <summary>
        /// Get all players in the lobby (flattened list from all connections).
        /// </summary>
        /// <returns>List of all NetworkPlayerData</returns>
        List<NetworkPlayerData> GetAllPlayers();

        /// <summary>
        /// Get the local player data.
        /// </summary>
        /// <returns>The local player's NetworkPlayerData, or null if not available</returns>
        NetworkPlayerData GetLocalPlayer();
        
        /// <summary>
        /// Validates that all connected profiles have game modes that are allowed by the given blacklist.
        /// The blacklist contains game modes that are DISABLED - any profile with a game mode in the blacklist is invalid.
        /// </summary>
        /// <param name="gameModeBlacklist">List of disabled game modes (empty means all allowed)</param>
        /// <param name="blockedProfiles">Out parameter containing names of profiles that are blocked</param>
        /// <returns>True if all profiles are valid, false if any profile has a blocked game mode</returns>
        bool ValidateProfilesAgainstGameModes(List<YARG.Core.GameMode> gameModeBlacklist, out List<string> blockedProfiles);

        /// <summary>
        /// Set the local player's ready state.
        /// When there are multiple local players, this sets the first player's ready state.
        /// Use SetPlayerReady(bool, string) overload to specify which player by name.
        /// </summary>
        /// <param name="isReady">Ready state to set</param>
        /// <param name="sittingOut">Whether the player is sitting out (ready but not playing)</param>
        void SetPlayerReady(bool isReady, bool sittingOut = false);

        /// <summary>
        /// Set a specific local player's ready state by their profile name.
        /// Use this when there are multiple local players in the same connection.
        /// </summary>
        /// <param name="isReady">Ready state to set</param>
        /// <param name="playerName">The profile name of the player to update</param>
        /// <param name="sittingOut">Whether the player is sitting out (ready but not playing)</param>
        void SetPlayerReady(bool isReady, string playerName, bool sittingOut = false);

        /// <summary>
        /// Check if all players in the lobby are ready.
        /// </summary>
        /// <returns>True if all players are ready</returns>
        bool AreAllPlayersReady();

        /// <summary>
        /// Reset all players' ready states to false without firing events.
        /// Used when entering score screen to ensure clean state before subscribing to events.
        /// </summary>
        void ResetAllPlayersReadyState();

        /// <summary>
        /// Event fired when any player's ready state changes.
        /// Parameters: playerName, isReady
        /// </summary>
        event Action<string, bool> OnPlayerReadyStateChanged;

        /// <summary>
        /// Event fired when all players have indicated they are ready.
        /// This is useful for auto-starting gameplay when everyone is ready.
        /// </summary>
        event Action OnAllPlayersReady;

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
        
        /// <summary>
        /// Request menu navigation synchronization for all clients (host only).
        /// </summary>
        /// <param name="popMenu">If true, pops the current menu. If false, navigates to current menu state.</param>
        void RequestSyncMenuNavigation(bool popMenu = false);

        #endregion

        #region Unified Host Actions (Message-Based Pattern)
        
        // These methods use a unified message-based pattern where ALL actions go through
        // the same code path regardless of hosting mode:
        // - In-game hosting: Request is processed locally by server logic
        // - Dedicated server: Request is sent to server, validated, and executed
        //
        // Menu code should use these methods instead of calling Broadcast* directly.
        
        /// <summary>
        /// Navigate all players to the Music Library.
        /// Requires host authority (HasHostAuthority must be true).
        /// </summary>
        void NavigateToMusicLibrary();
        
        /// <summary>
        /// Navigate all players to the Lobby Room.
        /// Requires host authority.
        /// </summary>
        void NavigateToLobbyRoom();
        
        /// <summary>
        /// Pop the current menu for all players.
        /// Requires host authority.
        /// </summary>
        void PopAllPlayersMenu();
        
        /// <summary>
        /// Start the show (begin playing the setlist).
        /// Requires host authority.
        /// </summary>
        void StartShow();
        
        /// <summary>
        /// Restart the current song for all players.
        /// Requires host authority.
        /// </summary>
        void RestartGameplay();
        
        /// <summary>
        /// Quit all players back to the music library.
        /// Requires host authority.
        /// </summary>
        void QuitToLibrary();
        
        /// <summary>
        /// Advance from the score screen after all players are ready.
        /// Called by host to proceed to next song or back to lobby.
        /// Requires host authority.
        /// </summary>
        void AdvanceAfterScoreScreen();
        
        #endregion

        #region Session Management

        /// <summary>
        /// Broadcasts session settings to all connected clients (host only).
        /// Clients will receive these via the OnSessionSettingsReceived event.
        /// </summary>
        bool BroadcastSessionSettings(
            string lobbyName,
            int maxPlayers,
            byte privacyMode,
            int bandSize,
            bool noFailMode,
            bool sharedSongsOnly,
            bool allowModifiers,
            bool enablePresetSync,
            bool allowLateJoin,
            List<int> allowedGameModes,
            bool localPlayersFirst);

        /// <summary>
        /// Initializes the band system for the current session (host only).
        /// This uses a deterministic seed based on the lobby ID so all clients get consistent band names.
        /// After initialization, any connected players are assigned to bands.
        /// </summary>
        /// <param name="bandSize">Max players per band. 0 disables the band system.</param>
        /// <param name="forceReinitialize">If true, forces full reinitialization even if already initialized.</param>
        /// <returns>True if initialization succeeded.</returns>
        bool InitializeBandsForSession(int bandSize, bool forceReinitialize = false);

        /// <summary>
        /// Assigns a group of players (from a single connection) to a band.
        /// All players from the same connection must stay in the same band.
        /// </summary>
        /// <param name="connectionId">The network connection ID (use 0 for host connection).</param>
        /// <param name="playerIds">All player IDs from this connection.</param>
        /// <param name="isLocalConnection">Whether this is the local client's connection.</param>
        /// <returns>The assigned band ID, or -1 if assignment failed.</returns>
        int AssignPlayersToBand(int connectionId, List<Guid> playerIds, bool isLocalConnection = false);

        /// <summary>
        /// Broadcasts the current band assignments to all connected clients.
        /// Should be called after players are assigned to bands.
        /// </summary>
        void BroadcastBandAssignments();

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
