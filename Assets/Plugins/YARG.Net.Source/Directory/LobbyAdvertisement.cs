using System;
using System.Text.Json.Serialization;

namespace YARG.Net.Directory;

/// <summary>
/// Payload sent by hosts to advertise their lobby to the introducer service.
/// </summary>
public sealed record LobbyAdvertisementRequest(
    [property: JsonPropertyName("lobbyId")] Guid LobbyId,
    [property: JsonPropertyName("lobbyName")] string LobbyName,
    [property: JsonPropertyName("hostName")] string HostName,
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("currentPlayers")] int CurrentPlayers,
    [property: JsonPropertyName("maxPlayers")] int MaxPlayers,
    [property: JsonPropertyName("hasPassword")] bool HasPassword,
    [property: JsonPropertyName("version")] string Version);

/// <summary>
/// Lobby entry returned to clients when querying the introducer.
/// </summary>
public sealed record LobbyDirectoryEntry(
    [property: JsonPropertyName("lobbyId")] Guid LobbyId,
    [property: JsonPropertyName("lobbyName")] string LobbyName,
    [property: JsonPropertyName("hostName")] string HostName,
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("currentPlayers")] int CurrentPlayers,
    [property: JsonPropertyName("maxPlayers")] int MaxPlayers,
    [property: JsonPropertyName("hasPassword")] bool HasPassword,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("lastHeartbeatUtc")] DateTimeOffset LastHeartbeatUtc)
{
    public bool IsActive(TimeSpan ttl) => DateTimeOffset.UtcNow - LastHeartbeatUtc <= ttl;
}
