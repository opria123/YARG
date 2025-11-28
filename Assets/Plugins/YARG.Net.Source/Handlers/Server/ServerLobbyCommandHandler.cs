using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using YARG.Net.Packets;
using YARG.Net.Packets.Dispatch;
using YARG.Net.Sessions;

namespace YARG.Net.Handlers.Server;

/// <summary>
/// Handles lobby-related commands sent by clients (ready toggles, song selection).
/// </summary>
public sealed class ServerLobbyCommandHandler
{
    private readonly SessionManager _sessionManager;
    private readonly LobbyStateManager _lobbyManager;

    public ServerLobbyCommandHandler(SessionManager sessionManager, LobbyStateManager lobbyManager)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _lobbyManager = lobbyManager ?? throw new ArgumentNullException(nameof(lobbyManager));
    }

    public void Register(IPacketDispatcher dispatcher)
    {
        if (dispatcher is null)
        {
            throw new ArgumentNullException(nameof(dispatcher));
        }

        dispatcher.RegisterHandler<LobbyReadyStatePacket>(PacketType.LobbyReadyState, HandleReadyCommandAsync);
        dispatcher.RegisterHandler<SongSelectionPacket>(PacketType.SongSelection, HandleSongSelectionAsync);
    }

    private Task HandleReadyCommandAsync(PacketContext context, PacketEnvelope<LobbyReadyStatePacket> envelope, CancellationToken cancellationToken)
    {
        if (context.Role != PacketEndpointRole.Server)
        {
            return Task.CompletedTask;
        }

        if (!TryValidateSession(context, envelope.Payload.SessionId, out var session))
        {
            return Task.CompletedTask;
        }

        _lobbyManager.TrySetReady(session.SessionId, envelope.Payload.IsReady, out _);
        return Task.CompletedTask;
    }

    private Task HandleSongSelectionAsync(PacketContext context, PacketEnvelope<SongSelectionPacket> envelope, CancellationToken cancellationToken)
    {
        if (context.Role != PacketEndpointRole.Server)
        {
            return Task.CompletedTask;
        }

        if (!TryValidateSession(context, envelope.Payload.SessionId, out var session))
        {
            return Task.CompletedTask;
        }

        if (!_lobbyManager.IsHost(session.SessionId))
        {
            return Task.CompletedTask;
        }

        _ = _lobbyManager.TryApplySongSelection(envelope.Payload.State);
        return Task.CompletedTask;
    }

    private bool TryValidateSession(PacketContext context, Guid sessionId, [NotNullWhen(true)] out SessionRecord? session)
    {
        if (!_sessionManager.TryGetSession(sessionId, out session))
        {
            return false;
        }

        if (session.ConnectionId != context.Connection.Id)
        {
            session = null;
            return false;
        }

        return true;
    }
}
