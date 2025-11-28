using YARG.Net.Transport;

namespace YARG.Net.Packets
{

/// <summary>
/// Provides transport metadata to packet handlers.
/// </summary>
public readonly struct PacketContext
{
    public INetConnection Connection { get; }
    public ChannelType Channel { get; }
    public PacketEndpointRole Role { get; }

    public PacketContext(INetConnection connection, ChannelType channel, PacketEndpointRole role)
    {
        Connection = connection;
        Channel = channel;
        Role = role;
    }
}
}