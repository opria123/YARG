using System;

namespace YARG.Net.Packets;

public sealed record HandshakeRequestPacket(string ClientVersion, string PlayerName, string? Password = null) : IPacketPayload;

public sealed record HandshakeResponsePacket(bool Accepted, string? Reason, Guid SessionId) : IPacketPayload;
