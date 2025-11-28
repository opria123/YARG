using System;
using System.Collections.Generic;
using YARG.Net.Packets;
using YARG.Net.Serialization;
using YARG.Net.Transport;

namespace YARG.Net.Sessions;

/// <summary>
/// Bridges handshake/session events to lobby state broadcasts.
/// </summary>
public sealed class ServerLobbyCoordinator : IDisposable
{
    private readonly SessionManager _sessionManager;
    private readonly LobbyStateManager _lobbyManager;
    private readonly INetSerializer _serializer;
    private bool _disposed;

    public ServerLobbyCoordinator(SessionManager sessionManager, LobbyStateManager lobbyManager, INetSerializer serializer)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _lobbyManager = lobbyManager ?? throw new ArgumentNullException(nameof(lobbyManager));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));

        _lobbyManager.PlayerJoined += OnLobbyChanged;
        _lobbyManager.PlayerLeft += OnLobbyChanged;
        _lobbyManager.PlayerReadyStateChanged += OnLobbyChanged;
        _lobbyManager.PlayerRoleChanged += OnLobbyChanged;
        _lobbyManager.SongSelectionChanged += OnLobbyChanged;
        _lobbyManager.StatusChanged += OnLobbyChanged;
        _lobbyManager.CountdownStarted += OnCountdownStarted;
        _lobbyManager.CountdownCancelled += OnLobbyChanged;
    }

    public void HandleHandshakeAccepted(SessionRecord session)
    {
        if (session is null)
        {
            throw new ArgumentNullException(nameof(session));
        }

        if (!_lobbyManager.TryAddPlayer(session.SessionId, LobbyRole.Member, out _, out var error))
        {
            var reason = error switch
            {
                LobbyJoinError.LobbyFull => "Lobby is full.",
                LobbyJoinError.SpectatorsDisabled => "Spectators are disabled.",
                _ => "Unable to join lobby.",
            };

            session.Connection.Disconnect(reason);
            _sessionManager.TryRemoveSession(session.SessionId, out _);
            return;
        }

        // PlayerJoined event broadcast will propagate the snapshot.
    }

    public void HandlePeerDisconnected(Guid connectionId)
    {
        if (_sessionManager.TryRemoveSessionByConnection(connectionId, out var session))
        {
            _lobbyManager.TryRemovePlayer(session.SessionId, out _);
        }
    }

    private void OnLobbyChanged(object? sender, EventArgs e)
    {
        BroadcastSnapshot();
    }

    private void OnCountdownStarted(object? sender, LobbyCountdownEventArgs e)
    {
        // Broadcast countdown packet to all clients
        var packet = new GameplayCountdownPacket(_lobbyManager.LobbyId, e.CountdownSeconds);
        var envelope = PacketEnvelope<GameplayCountdownPacket>.Create(PacketType.GameplayCountdown, packet);
        var buffer = _serializer.Serialize(envelope);

        IReadOnlyList<SessionRecord> sessions = _sessionManager.GetSessionsSnapshot();
        foreach (var session in sessions)
        {
            session.Connection.Send(buffer.Span, ChannelType.ReliableOrdered);
        }

        // Also broadcast the updated state (now InCountdown)
        BroadcastSnapshot();
    }

    private void BroadcastSnapshot()
    {
        var packet = _lobbyManager.BuildPacket();
        var envelope = PacketEnvelope<LobbyStatePacket>.Create(PacketType.LobbyState, packet);
        var buffer = _serializer.Serialize(envelope);

        IReadOnlyList<SessionRecord> sessions = _sessionManager.GetSessionsSnapshot();
        foreach (var session in sessions)
        {
            session.Connection.Send(buffer.Span, ChannelType.ReliableOrdered);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _lobbyManager.PlayerJoined -= OnLobbyChanged;
        _lobbyManager.PlayerLeft -= OnLobbyChanged;
        _lobbyManager.PlayerReadyStateChanged -= OnLobbyChanged;
        _lobbyManager.PlayerRoleChanged -= OnLobbyChanged;
        _lobbyManager.SongSelectionChanged -= OnLobbyChanged;
        _lobbyManager.StatusChanged -= OnLobbyChanged;
        _lobbyManager.CountdownStarted -= OnCountdownStarted;
        _lobbyManager.CountdownCancelled -= OnLobbyChanged;
        _disposed = true;
    }
}
