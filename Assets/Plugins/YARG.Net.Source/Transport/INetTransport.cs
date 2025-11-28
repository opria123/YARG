using System;

namespace YARG.Net.Transport;

public interface INetTransport : IDisposable
{
    event Action<INetConnection>? OnPeerConnected;
    event Action<INetConnection>? OnPeerDisconnected;
    event Action<INetConnection, ReadOnlyMemory<byte>, ChannelType>? OnPayloadReceived;

    bool IsRunning { get; }

    void Start(TransportStartOptions options);
    void Poll(TimeSpan timeout);
    void Shutdown(string? reason = null);
}
