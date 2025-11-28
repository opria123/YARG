using System;
using System.Threading;
using System.Threading.Tasks;
using YARG.Net.Packets;
using YARG.Net.Transport;

namespace YARG.Net.Runtime;

/// <summary>
/// Basic polling-based server runtime that drives an <see cref="INetTransport"/>.
/// </summary>
public sealed class DefaultServerRuntime : IServerRuntime
{
    private readonly object _gate = new();
    private readonly TimeSpan _pollInterval;

    private ServerRuntimeOptions? _configuredOptions;
    private CancellationTokenSource? _loopCancellation;
    private Task? _loopTask;
    private bool _isRunning;
    private bool _transportEventsAttached;

    public DefaultServerRuntime(TimeSpan? pollInterval = null)
    {
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(15);
    }

    public void Configure(ServerRuntimeOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (options.Transport is null)
        {
            throw new ArgumentNullException(nameof(options.Transport));
        }

        lock (_gate)
        {
            if (_isRunning)
            {
                throw new InvalidOperationException("Cannot configure the runtime while it is running.");
            }

            _configuredOptions = options;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_isRunning)
            {
                throw new InvalidOperationException("Server runtime already started.");
            }

            if (_configuredOptions is null)
            {
                throw new InvalidOperationException("Call Configure before StartAsync.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            _loopCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _isRunning = true;
        }

        try
        {
            var options = _configuredOptions!;
            var transport = options.Transport;

            transport.Start(new TransportStartOptions
            {
                Port = options.Port,
                Address = options.Address,
                EnableNatPunchThrough = options.EnableNatPunchThrough,
                IsServer = true,
            });

            AttachTransportEvents(transport);

            var loopToken = _loopCancellation!.Token;
            _loopTask = Task.Run(() => RunLoop(transport, loopToken), CancellationToken.None);
        }
        catch
        {
            lock (_gate)
            {
                _isRunning = false;
                _loopCancellation?.Dispose();
                _loopCancellation = null;
            }

            throw;
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? loopTask;
        CancellationTokenSource? loopCancellation;
        INetTransport? transport = null;

        lock (_gate)
        {
            if (!_isRunning)
            {
                return;
            }

            loopTask = _loopTask;
            loopCancellation = _loopCancellation;
            transport = _configuredOptions?.Transport;

            _loopTask = null;
            _loopCancellation = null;
            _isRunning = false;
        }

        loopCancellation?.Cancel();

        if (loopTask is not null)
        {
            await WaitForLoopAsync(loopTask, cancellationToken).ConfigureAwait(false);
        }

        if (transport is not null)
        {
            DetachTransportEvents(transport);
            transport.Shutdown();
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

    private void AttachTransportEvents(INetTransport transport)
    {
        if (_transportEventsAttached)
        {
            return;
        }

        transport.OnPayloadReceived += HandlePayloadReceived;
        _transportEventsAttached = true;
    }

    private void DetachTransportEvents(INetTransport transport)
    {
        if (!_transportEventsAttached)
        {
            return;
        }

        transport.OnPayloadReceived -= HandlePayloadReceived;
        _transportEventsAttached = false;
    }

    private void HandlePayloadReceived(INetConnection connection, ReadOnlyMemory<byte> payload, ChannelType channel)
    {
        var dispatcher = _configuredOptions?.PacketDispatcher;
        if (dispatcher is null)
        {
            return;
        }

        var context = new PacketContext(connection, channel, PacketEndpointRole.Server);
        _ = dispatcher.DispatchAsync(payload, context, CancellationToken.None);
    }
}
