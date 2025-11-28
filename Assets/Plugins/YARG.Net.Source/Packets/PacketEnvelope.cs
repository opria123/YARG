namespace YARG.Net.Packets;

/// <summary>
/// Wraps a strongly typed payload with metadata that helps routers dispatch packets.
/// </summary>
public sealed record PacketEnvelope<TPayload>(PacketType Type, TPayload Payload, string Version)
    where TPayload : IPacketPayload
{
    public static PacketEnvelope<TPayload> Create(PacketType type, TPayload payload, string? version = null)
    {
        return new PacketEnvelope<TPayload>(type, payload, version ?? ProtocolVersion.Current);
    }
}
