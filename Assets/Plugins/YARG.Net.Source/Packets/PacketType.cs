namespace YARG.Net.Packets;

/// <summary>
/// Enumerates the well-known packet categories exchanged between peers.
/// </summary>
public enum PacketType
{
    HandshakeRequest = 1,
    HandshakeResponse = 2,
    Heartbeat = 3,
    LobbyState = 10,
    LobbyInvite = 11,
    LobbyReadyState = 12,
    SongSelection = 20,
    GameplayCountdown = 30,
    GameplayInputFrame = 31,
    GameplayState = 32,
    GameplayStart = 33,
    GameplayTimeSync = 34,
    GameplayPause = 35,
    GameplayEnd = 36,
    ReplaySyncRequest = 40,
    ReplaySyncData = 41,
    ReplaySyncComplete = 42,
}
