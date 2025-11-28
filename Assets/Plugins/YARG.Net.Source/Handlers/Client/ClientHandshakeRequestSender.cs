using System;
using YARG.Net.Packets;
using YARG.Net.Serialization;
using YARG.Net.Transport;

namespace YARG.Net.Handlers.Client;

/// <summary>
/// Emits <see cref="HandshakeRequestPacket"/> messages once a client transport connects.
/// </summary>
public sealed class ClientHandshakeRequestSender
{
    private readonly INetSerializer _serializer;

    public ClientHandshakeRequestSender(INetSerializer serializer)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    }

    /// <summary>
    /// Sends a handshake request using the provided connection and metadata.
    /// </summary>
    public void SendHandshake(INetConnection connection, string clientVersion, string playerName, string? password = null)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        if (string.IsNullOrWhiteSpace(clientVersion))
        {
            throw new ArgumentException("Client version must be provided.", nameof(clientVersion));
        }

        if (string.IsNullOrWhiteSpace(playerName))
        {
            throw new ArgumentException("Player name must be provided.", nameof(playerName));
        }

        var sanitizedName = playerName.Trim();
        var packet = PacketEnvelope<HandshakeRequestPacket>.Create(
            PacketType.HandshakeRequest,
            new HandshakeRequestPacket(clientVersion.Trim(), sanitizedName, password));

        var payload = _serializer.Serialize(packet);
        connection.Send(payload.Span, ChannelType.ReliableOrdered);
    }

    /// <summary>
    /// Sends a handshake using <see cref="ProtocolVersion.Current"/>.
    /// </summary>
    public void SendHandshake(INetConnection connection, string playerName, string? password = null)
    {
        SendHandshake(connection, ProtocolVersion.Current, playerName, password);
    }
}
