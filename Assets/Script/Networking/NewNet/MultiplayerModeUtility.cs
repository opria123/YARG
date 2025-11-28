using YARG.Networking;

namespace YARG.Networking.NewNet
{
    /// <summary>
    /// Utility to detect and check multiplayer modes consistently across the application.
    /// Helps manage the transition from Mirror to LiteNet networking.
    /// </summary>
    public static class MultiplayerModeUtility
    {
        /// <summary>
        /// Returns true if connected via LiteNet multiplayer.
        /// </summary>
        public static bool IsLiteNetMultiplayer =>
            ClientNetworkingService.HasInstance && ClientNetworkingService.Instance.IsConnected;

        /// <summary>
        /// Returns true if connected via Mirror (legacy) multiplayer.
        /// </summary>
        public static bool IsMirrorMultiplayer =>
            YargNetworkManager.Instance != null && YargNetworkManager.Instance.isNetworkActive;

        /// <summary>
        /// Returns true if in any multiplayer mode (LiteNet or Mirror).
        /// </summary>
        public static bool IsAnyMultiplayer => IsLiteNetMultiplayer || IsMirrorMultiplayer;

        /// <summary>
        /// Returns true if hosting a LiteNet server.
        /// </summary>
        public static bool IsLiteNetHost =>
            ServerNetworkingService.HasInstance && ServerNetworkingService.Instance.IsRunning;

        /// <summary>
        /// Returns true if hosting a Mirror server.
        /// </summary>
        public static bool IsMirrorHost =>
            YargNetworkManager.Instance != null && YargNetworkManager.Instance.IsHosting;

        /// <summary>
        /// Returns true if hosting any multiplayer server (LiteNet or Mirror).
        /// </summary>
        public static bool IsAnyHost => IsLiteNetHost || IsMirrorHost;

        /// <summary>
        /// Gets the current player count. Returns 0 if not in multiplayer.
        /// </summary>
        public static int GetPlayerCount()
        {
            if (IsLiteNetHost && ServerNetworkingService.HasInstance)
            {
                // For LiteNet host, get active session count
                var lobbyManager = ServerNetworkingService.Instance.LobbyManager;
                return lobbyManager?.PlayerCount ?? 0;
            }
            else if (IsMirrorMultiplayer && YargNetworkManager.Instance != null)
            {
                // For Mirror, use the existing player count method
                var allPlayers = YargNetworkManager.Instance.GetAllPlayers();
                return allPlayers?.Count ?? 0;
            }

            return 0;
        }
    }
}
