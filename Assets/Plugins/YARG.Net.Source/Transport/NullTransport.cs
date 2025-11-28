using System;

namespace YARG.Net.Transport;

/// <summary>
/// Minimal placeholder transport so early unit tests can run without LiteNetLib.
/// </summary>
public sealed class NullTransport : INetTransport
{
#pragma warning disable CS0067 // Placeholder transport intentionally exposes unused events
    public event Action<INetConnection>? OnPeerConnected;
    public event Action<INetConnection>? OnPeerDisconnected;
    public event Action<INetConnection, ReadOnlyMemory<byte>, ChannelType>? OnPayloadReceived;
#pragma warning restore CS0067

    public bool IsRunning { get; private set; }

    public void Start(TransportStartOptions options)
    {
        IsRunning = true;
    }

    public void Poll(TimeSpan timeout)
    {
        // No-op.
    }

    public void Shutdown(string? reason = null)
    {
        IsRunning = false;
    }

    public void Dispose()
    {
        Shutdown();
    }
}
