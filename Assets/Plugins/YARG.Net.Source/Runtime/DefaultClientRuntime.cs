using System;
using System.Threading;
using System.Threading.Tasks;
using YARG.Net.Handlers.Client;
using YARG.Net.Packets;
using YARG.Net.Packets.Dispatch;
using YARG.Net.Transport;

namespace YARG.Net.Runtime;

/// <summary>
/// Default client runtime that drives an <see cref="INetTransport"/> on the Unity side.
/// </summary>
public sealed class DefaultClientRuntime : IClientRuntime
{
    private readonly object _gate = new();
    private readonly TimeSpan _pollInterval;

    private INetTransport? _transport;
    private CancellationTokenSource? _pollCancellation;
    private Task? _pollTask;
    private bool _isConnecting;
    private bool _isConnected;
    private TaskCompletionSource<bool>? _pendingConnectTcs;
    private IPacketDispatcher? _packetDispatcher;
    private INetConnection? _connectedPeer;
    private ClientSessionContext? _sessionContext;
    private ClientHandshakeResponseHandler? _handshakeHandler;
    private bool _handshakeHandlerRegistered;

    public DefaultClientRuntime(TimeSpan? pollInterval = null)
    {
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(15);
    }

    public event EventHandler<ClientConnectedEventArgs>? Connected;
    public event EventHandler<ClientDisconnectedEventArgs>? Disconnected;
    public event EventHandler<ClientHandshakeCompletedEventArgs>? HandshakeCompleted;

    public INetConnection? ActiveConnection
    {
        get
        {
            lock (_gate)
            {
                return _connectedPeer;
            }
        }
    }

    public ClientSessionContext? SessionContext
    {
        get
        {
            lock (_gate)
            {
                return _sessionContext;
            }
        }
    }

    public void RegisterTransport(INetTransport transport)
    {
        if (transport is null)
        {
            throw new ArgumentNullException(nameof(transport));
        }

        lock (_gate)
        {
            if (_transport is not null && !ReferenceEquals(_transport, transport))
            {
                throw new InvalidOperationException("A transport has already been registered.");
            }

            if (_transport is null)
            {
                transport.OnPeerConnected += HandlePeerConnected;
                transport.OnPeerDisconnected += HandlePeerDisconnected;
                transport.OnPayloadReceived += HandlePayloadReceived;
            }

            _transport = transport;
        }
    }

    public void RegisterPacketDispatcher(IPacketDispatcher dispatcher)
    {
        if (dispatcher is null)
        {
            throw new ArgumentNullException(nameof(dispatcher));
        }

        lock (_gate)
        {
            if (_packetDispatcher is not null && !ReferenceEquals(_packetDispatcher, dispatcher))
            {
                throw new InvalidOperationException("A packet dispatcher has already been registered.");
            }

            _packetDispatcher = dispatcher;
        }

        TryAttachHandshakeHandler();
    }

    public void RegisterSessionContext(ClientSessionContext sessionContext)
    {
        if (sessionContext is null)
        {
            throw new ArgumentNullException(nameof(sessionContext));
        }

        lock (_gate)
        {
            if (_sessionContext is not null && !ReferenceEquals(_sessionContext, sessionContext))
            {
                throw new InvalidOperationException("A session context has already been registered.");
            }

            if (_sessionContext is null)
            {
                _sessionContext = sessionContext;
                _handshakeHandler = new ClientHandshakeResponseHandler(sessionContext);
                _handshakeHandler.HandshakeCompleted += HandleHandshakeCompleted;
                _handshakeHandlerRegistered = false;
            }
        }

        TryAttachHandshakeHandler();
    }

    public async Task ConnectAsync(string address, int port, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new ArgumentException("Address must be provided.", nameof(address));
        }

        if (port <= 0 || port > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        INetTransport transport;
        TaskCompletionSource<bool> connectTcs;

        lock (_gate)
        {
            transport = _transport ?? throw new InvalidOperationException("Call RegisterTransport before ConnectAsync.");

            if (_isConnecting || _isConnected)
            {
                throw new InvalidOperationException("Client is already connecting or connected.");
            }

            _isConnecting = true;
            _pendingConnectTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            connectTcs = _pendingConnectTcs;
        }

        try
        {
            transport.Start(new TransportStartOptions
            {
                Address = address,
                Port = port,
                IsServer = false,
            });
        }
        catch
        {
            lock (_gate)
            {
                _isConnecting = false;
                _pendingConnectTcs = null;
            }

            transport.Shutdown();
            throw;
        }

        StartPollLoop(transport);

        using var registration = cancellationToken.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(), connectTcs);

        try
        {
            await connectTcs.Task.ConfigureAwait(false);
        }
        catch
        {
            await DisconnectInternalAsync(null, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public Task DisconnectAsync(string? reason = null, CancellationToken cancellationToken = default)
    {
        return DisconnectInternalAsync(reason, cancellationToken);
    }

    private async Task DisconnectInternalAsync(string? reason, CancellationToken cancellationToken)
    {
        INetTransport? transport;
        Task? pollTask;
        CancellationTokenSource? pollCancellation;
        bool wasRunning;
        ClientSessionContext? sessionContext;

        lock (_gate)
        {
            transport = _transport;
            pollTask = _pollTask;
            pollCancellation = _pollCancellation;
            wasRunning = _isConnected || _isConnecting || (transport?.IsRunning ?? false);
            sessionContext = _sessionContext;

            _isConnected = false;
            _isConnecting = false;
            _pendingConnectTcs?.TrySetCanceled();
            _pendingConnectTcs = null;
            _pollTask = null;
            _pollCancellation = null;
            _connectedPeer = null;
        }

        if (!wasRunning || transport is null)
        {
            return;
        }

        pollCancellation?.Cancel();

        if (pollTask is not null)
        {
            await WaitForLoopAsync(pollTask, cancellationToken).ConfigureAwait(false);
        }

        pollCancellation?.Dispose();
        transport.Shutdown(reason);
        sessionContext?.ClearSession();
    }

    private void StartPollLoop(INetTransport transport)
    {
        var cts = new CancellationTokenSource();
        var loopToken = cts.Token;

        lock (_gate)
        {
            _pollCancellation = cts;
            _pollTask = Task.Run(() => RunLoop(transport, loopToken), CancellationToken.None);
        }
    }

    private void RunLoop(INetTransport transport, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                transport.Poll(_pollInterval);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during shutdown.
        }
    }

    private static async Task WaitForLoopAsync(Task loopTask, CancellationToken cancellationToken)
    {
        if (loopTask.IsCompleted)
        {
            await loopTask.ConfigureAwait(false);
            return;
        }

        var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), completionSource))
        {
            var completedTask = await Task.WhenAny(loopTask, completionSource.Task).ConfigureAwait(false);
            if (completedTask == loopTask)
            {
                await loopTask.ConfigureAwait(false);
                return;
            }
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private void HandlePeerConnected(INetConnection connection)
    {
        TaskCompletionSource<bool>? connectTcs;
        ClientConnectedEventArgs? connectedArgs = null;

        lock (_gate)
        {
            _isConnecting = false;
            _isConnected = true;
            _connectedPeer = connection;
            connectTcs = _pendingConnectTcs;
            _pendingConnectTcs = null;
            connectedArgs = new ClientConnectedEventArgs(connection);
        }

        connectTcs?.TrySetResult(true);
        Connected?.Invoke(this, connectedArgs);
    }

    private void HandlePeerDisconnected(INetConnection connection)
    {
        TaskCompletionSource<bool>? connectTcs = null;
        bool wasConnecting;
        ClientDisconnectedEventArgs? disconnectedArgs = null;
        ClientSessionContext? sessionContext;

        lock (_gate)
        {
            wasConnecting = _isConnecting;
            _isConnecting = false;
            _isConnected = false;
            _connectedPeer = null;
            sessionContext = _sessionContext;

            if (wasConnecting)
            {
                connectTcs = _pendingConnectTcs;
                _pendingConnectTcs = null;
            }
            else
            {
                disconnectedArgs = new ClientDisconnectedEventArgs(connection, initiatedDuringConnect: false);
            }
        }

        if (wasConnecting)
        {
            connectTcs?.TrySetException(new InvalidOperationException("Disconnected before the client finished connecting."));
        }
        else if (disconnectedArgs is not null)
        {
            Disconnected?.Invoke(this, disconnectedArgs);
        }

        sessionContext?.ClearSession();
    }

    private void HandlePayloadReceived(INetConnection connection, ReadOnlyMemory<byte> payload, ChannelType channel)
    {
        IPacketDispatcher? dispatcher;

        lock (_gate)
        {
            dispatcher = _packetDispatcher;
        }

        if (dispatcher is null)
        {
            return;
        }

        var context = new PacketContext(connection, channel, PacketEndpointRole.Client);
        _ = dispatcher.DispatchAsync(payload, context, CancellationToken.None);
    }

    private void TryAttachHandshakeHandler()
    {
        ClientHandshakeResponseHandler? handler;
        IPacketDispatcher? dispatcher;

        lock (_gate)
        {
            if (_handshakeHandlerRegistered || _handshakeHandler is null || _packetDispatcher is null)
            {
                return;
            }

            handler = _handshakeHandler;
            dispatcher = _packetDispatcher;
        }

        handler.Register(dispatcher);

        lock (_gate)
        {
            _handshakeHandlerRegistered = true;
        }
    }

    private void HandleHandshakeCompleted(object? sender, ClientHandshakeCompletedEventArgs e)
    {
        HandshakeCompleted?.Invoke(this, e);
    }
}
