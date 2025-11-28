using System;

namespace YARG.Net.Transport;

/// <summary>
/// Represents a logical peer connected to the transport.
/// </summary>
public interface INetConnection
{
    Guid Id { get; }
    string EndPoint { get; }

    void Disconnect(string? reason = null);
    void Send(ReadOnlySpan<byte> payload, ChannelType channel = ChannelType.ReliableOrdered);
}
