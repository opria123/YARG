using System;

namespace YARG.Net.Sessions;

/// <summary>
/// Provides configurable limits for a lobby instance.
/// </summary>
public sealed record LobbyConfiguration
{
    private int _maxPlayers = 8;

    /// <summary>
    /// Maximum number of active (non-spectator) players allowed in the lobby.
    /// </summary>
    public int MaxPlayers
    {
        get => _maxPlayers;
        init => _maxPlayers = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(MaxPlayers));
    }

    /// <summary>
    /// Whether spectators are allowed to join the lobby.
    /// </summary>
    public bool AllowSpectators { get; init; } = true;
}
