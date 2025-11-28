using System;
using System.Collections.Generic;

namespace YARG.Net.Packets;

public sealed record LobbyStatePacket(Guid LobbyId, IReadOnlyList<LobbyPlayer> Players, LobbyStatus Status, SongSelectionState? Selection) : IPacketPayload;

public sealed record LobbyInvitePacket(Guid LobbyId, LobbyPlayer Inviter, string InviteCode) : IPacketPayload;

public sealed record LobbyPlayer(Guid PlayerId, string DisplayName, LobbyRole Role, bool IsReady);

public enum LobbyStatus
{
    Idle,
    SelectingSong,
    ReadyToPlay,
    InCountdown,
}

public enum LobbyRole
{
    Host,
    Member,
    Spectator,
}
