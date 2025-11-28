using System;
using System.Threading;
using System.Threading.Tasks;
using YARG.Net.Packets;
using YARG.Net.Packets.Dispatch;
using YARG.Net.Transport;

namespace YARG.Net.Handlers.Client;

/// <summary>
/// Handles countdown packets received on the client.
/// </summary>
public sealed class ClientCountdownHandler
{
    /// <summary>
    /// Raised when a gameplay countdown is received from the server.
    /// </summary>
    public event EventHandler<CountdownReceivedEventArgs>? CountdownReceived;

    /// <summary>
    /// Registers this handler with the provided dispatcher.
    /// </summary>
    public void Register(IPacketDispatcher dispatcher)
    {
        if (dispatcher is null)
        {
            throw new ArgumentNullException(nameof(dispatcher));
        }

        dispatcher.RegisterHandler<GameplayCountdownPacket>(PacketType.GameplayCountdown, HandleAsync);
    }

    /// <summary>
    /// Handles a countdown packet dispatched via <see cref="IPacketDispatcher"/>.
    /// </summary>
    public Task HandleAsync(PacketContext context, PacketEnvelope<GameplayCountdownPacket> envelope, CancellationToken cancellationToken)
    {
        if (context.Role != PacketEndpointRole.Client)
        {
            return Task.CompletedTask;
        }

        var packet = envelope.Payload;
        CountdownReceived?.Invoke(this, new CountdownReceivedEventArgs(packet.SessionId, packet.SecondsRemaining));

        return Task.CompletedTask;
    }
}

public sealed class CountdownReceivedEventArgs : EventArgs
{
    public CountdownReceivedEventArgs(Guid lobbyId, int secondsRemaining)
    {
        LobbyId = lobbyId;
        SecondsRemaining = secondsRemaining;
    }

    public Guid LobbyId { get; }
    public int SecondsRemaining { get; }
}
