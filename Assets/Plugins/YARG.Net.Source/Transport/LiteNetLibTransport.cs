using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Utils;

namespace YARG.Net.Transport;

/// <summary>
/// LiteNetLib-backed implementation of <see cref="INetTransport"/> for production networking.
/// </summary>
public sealed class LiteNetLibTransport : INetTransport, INetEventListener
{
    private readonly ConcurrentDictionary<NetPeer, LiteNetLibConnection> _connections = new();
    private NetManager? _netManager;

    public event Action<INetConnection>? OnPeerConnected;
    public event Action<INetConnection>? OnPeerDisconnected;
    public event Action<INetConnection, ReadOnlyMemory<byte>, ChannelType>? OnPayloadReceived;

    public bool IsRunning => _netManager?.IsRunning == true;

    public void Start(TransportStartOptions options)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("LiteNetLib transport already started.");
        }

        _netManager = new NetManager(this)
        {
            NatPunchEnabled = options.EnableNatPunchThrough,
        };

        bool started = options.IsServer
            ? _netManager.Start(options.Port)
            : _netManager.Start();

        if (!started)
        {
            _netManager = null;
            throw new InvalidOperationException("LiteNetLib failed to start with the provided options.");
        }

        if (!options.IsServer)
        {
            if (string.IsNullOrWhiteSpace(options.Address))
            {
                throw new ArgumentException("Client mode requires a destination address.", nameof(options));
            }

            var peer = _netManager.Connect(options.Address, options.Port, string.Empty);
            if (peer is null)
            {
                Shutdown("Unable to create LiteNetLib client peer.");
                throw new InvalidOperationException("LiteNetLib failed to initiate the client connection.");
            }
        }
    }

    public void Poll(TimeSpan timeout)
    {
        if (_netManager is null)
        {
            return;
        }

        if (timeout <= TimeSpan.Zero)
        {
            _netManager.PollEvents();
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        do
        {
            _netManager.PollEvents();

            if (stopwatch.Elapsed >= timeout)
            {
                break;
            }

            Thread.Sleep(1);
        } while (true);
    }

    public void Shutdown(string? reason = null)
    {
        if (_netManager is null)
        {
            return;
        }

        foreach (var peer in _netManager.ConnectedPeerList)
        {
            if (!string.IsNullOrEmpty(reason))
            {
                peer.Disconnect(Encoding.UTF8.GetBytes(reason));
            }
            else
            {
                peer.Disconnect();
            }
        }

        _netManager.Stop();
        _netManager = null;
        _connections.Clear();
    }

    public void Dispose()
    {
        Shutdown();
    }

    private LiteNetLibConnection GetOrAddConnection(NetPeer peer)
    {
        return _connections.GetOrAdd(peer, static netPeer => new LiteNetLibConnection(netPeer));
    }

    void INetEventListener.OnPeerConnected(NetPeer peer)
    {
        var connection = GetOrAddConnection(peer);
        OnPeerConnected?.Invoke(connection);
    }

    void INetEventListener.OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        if (_connections.TryRemove(peer, out var connection))
        {
            OnPeerDisconnected?.Invoke(connection);
        }
    }

    void INetEventListener.OnNetworkError(IPEndPoint endPoint, SocketError socketError)
    {
        // For now we simply drop the error; higher layers can add logging hooks later.
    }

    void INetEventListener.OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        var connection = GetOrAddConnection(peer);
        var payload = reader.GetRemainingBytes();
        reader.Recycle();

        var channel = deliveryMethod switch
        {
            DeliveryMethod.ReliableOrdered => ChannelType.ReliableOrdered,
            DeliveryMethod.ReliableSequenced => ChannelType.ReliableSequenced,
            DeliveryMethod.Sequenced => ChannelType.ReliableSequenced,
            DeliveryMethod.Unreliable => ChannelType.Unreliable,
            DeliveryMethod.ReliableUnordered => ChannelType.ReliableOrdered,
            _ => ChannelType.Unreliable,
        };

        OnPayloadReceived?.Invoke(connection, payload, channel);
    }

    void INetEventListener.OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
        reader.Recycle();
    }

    void INetEventListener.OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {
        // No-op for now.
    }

    void INetEventListener.OnConnectionRequest(ConnectionRequest request)
    {
        request.AcceptIfKey(string.Empty);
    }
}
