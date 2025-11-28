using System;

namespace YARG.Net.Packets;

public sealed record LobbyReadyStatePacket(Guid SessionId, bool IsReady) : IPacketPayload;
