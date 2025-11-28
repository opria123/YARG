using YARG.Net.Transport;

namespace YARG.Net.Packets;

/// <summary>
/// Provides transport metadata to packet handlers.
/// </summary>
public readonly record struct PacketContext(INetConnection Connection, ChannelType Channel, PacketEndpointRole Role);
