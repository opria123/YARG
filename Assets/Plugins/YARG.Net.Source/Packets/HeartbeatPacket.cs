namespace YARG.Net.Packets;

public sealed record HeartbeatPacket(long TimestampUnixMs) : IPacketPayload;
