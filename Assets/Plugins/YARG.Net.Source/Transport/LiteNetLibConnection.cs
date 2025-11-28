using System;
using System.Text;
using LiteNetLib;

namespace YARG.Net.Transport;

internal sealed class LiteNetLibConnection : INetConnection
{
    private readonly NetPeer _peer;
    private readonly Guid _id = Guid.NewGuid();

    public LiteNetLibConnection(NetPeer peer)
    {
        _peer = peer;
    }

    public Guid Id => _id;

    public string EndPoint => _peer.EndPoint?.ToString() ?? string.Empty;

    public void Disconnect(string? reason = null)
    {
        if (_peer.ConnectionState == ConnectionState.Disconnected)
        {
            return;
        }

        if (string.IsNullOrEmpty(reason))
        {
            _peer.Disconnect();
        }
        else
        {
            var payload = Encoding.UTF8.GetBytes(reason);
            _peer.Disconnect(payload);
        }
    }

    public void Send(ReadOnlySpan<byte> payload, ChannelType channel = ChannelType.ReliableOrdered)
    {
        if (payload.Length == 0)
        {
            return;
        }

        var method = channel switch
        {
            ChannelType.ReliableOrdered => DeliveryMethod.ReliableOrdered,
            ChannelType.ReliableSequenced => DeliveryMethod.ReliableSequenced,
            ChannelType.Unreliable => DeliveryMethod.Unreliable,
            _ => DeliveryMethod.ReliableOrdered,
        };

        var buffer = payload.ToArray();
        _peer.Send(buffer, method);
    }
}
