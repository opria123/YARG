using System;
using System.Threading;
using System.Threading.Tasks;
using YARG.Net.Packets;
using YARG.Net.Packets.Dispatch;
using YARG.Net.Runtime;

namespace YARG.Net.Handlers.Client;

/// <summary>
/// Handles <see cref="HandshakeResponsePacket"/> messages on the client and updates session context.
/// </summary>
public sealed class ClientHandshakeResponseHandler
{
    private readonly ClientSessionContext _sessionContext;

    public ClientHandshakeResponseHandler(ClientSessionContext sessionContext)
    {
        _sessionContext = sessionContext ?? throw new ArgumentNullException(nameof(sessionContext));
    }

    public event EventHandler<ClientHandshakeCompletedEventArgs>? HandshakeCompleted;

    public void Register(IPacketDispatcher dispatcher)
    {
        if (dispatcher is null)
        {
            throw new ArgumentNullException(nameof(dispatcher));
        }

        dispatcher.RegisterHandler<HandshakeResponsePacket>(PacketType.HandshakeResponse, HandleAsync);
    }

    public Task HandleAsync(PacketContext context, PacketEnvelope<HandshakeResponsePacket> envelope, CancellationToken cancellationToken)
    {
        if (context.Role != PacketEndpointRole.Client)
        {
            return Task.CompletedTask;
        }

        var payload = envelope.Payload;

        if (payload.Accepted && payload.SessionId != Guid.Empty)
        {
            _sessionContext.TrySetSession(payload.SessionId);
        }
        else
        {
            _sessionContext.ClearSession();
        }

        HandshakeCompleted?.Invoke(this, new ClientHandshakeCompletedEventArgs(payload.Accepted, payload.Reason, payload.SessionId));
        return Task.CompletedTask;
    }
}

public sealed class ClientHandshakeCompletedEventArgs : EventArgs
{
    public ClientHandshakeCompletedEventArgs(bool accepted, string? reason, Guid sessionId)
    {
        Accepted = accepted;
        Reason = reason;
        SessionId = sessionId;
    }

    public bool Accepted { get; }
    public string? Reason { get; }
    public Guid SessionId { get; }
}
