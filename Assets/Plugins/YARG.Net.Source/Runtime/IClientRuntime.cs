using System;
using System.Threading;
using System.Threading.Tasks;
using YARG.Net.Handlers.Client;
using YARG.Net.Transport;

namespace YARG.Net.Runtime;

public interface IClientRuntime
{
    event EventHandler<ClientConnectedEventArgs>? Connected;
    event EventHandler<ClientDisconnectedEventArgs>? Disconnected;
    event EventHandler<ClientHandshakeCompletedEventArgs>? HandshakeCompleted;

    Task ConnectAsync(string address, int port, CancellationToken cancellationToken = default);
    Task DisconnectAsync(string? reason = null, CancellationToken cancellationToken = default);
    void RegisterTransport(INetTransport transport);
    void RegisterSessionContext(ClientSessionContext sessionContext);
    ClientSessionContext? SessionContext { get; }
}
