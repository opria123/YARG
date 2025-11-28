using System;
using System.Threading;
using System.Threading.Tasks;
using YARG.Net.Packets.Dispatch;
using YARG.Net.Transport;

namespace YARG.Net.Runtime;

public interface IServerRuntime
{
    void Configure(ServerRuntimeOptions options);
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public sealed record ServerRuntimeOptions
{
    public required INetTransport Transport { get; init; }
    public int Port { get; init; } = 7777;
    public string Address { get; init; } = "0.0.0.0";
    public bool EnableNatPunchThrough { get; init; }
    public IPacketDispatcher? PacketDispatcher { get; init; }
}
