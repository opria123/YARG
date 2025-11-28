using System;

namespace YARG.Networking.Abstraction
{
    /// <summary>
    /// Common lobby information structure used across all networking implementations.
    /// </summary>
    [Serializable]
    public class LobbyInfo
    {
        public string LobbyId { get; set; }
        public string LobbyName { get; set; }
        public string HostName { get; set; }
        public int CurrentPlayers { get; set; }
        public int MaxPlayers { get; set; }
        public LobbyPrivacyMode PrivacyMode { get; set; }
        public bool HasPassword { get; set; }
        public string Password { get; set; }
        public bool IsActive { get; set; }
        public string IpAddress { get; set; }
        public int Port { get; set; }
        public int PublicPort { get; set; }
        public string PublicAddress { get; set; }
        public string TransportId { get; set; }
        public string[] PlayerNames { get; set; }
        public int[] PlayerInstruments { get; set; }
    }

    /// <summary>
    /// Privacy mode for lobbies.
    /// </summary>
    public enum LobbyPrivacyMode
    {
        Public,
        Private
    }
}
