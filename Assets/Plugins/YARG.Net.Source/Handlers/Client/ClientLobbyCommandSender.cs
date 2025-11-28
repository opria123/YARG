using System;
using YARG.Net.Packets;
using YARG.Net.Runtime;
using YARG.Net.Serialization;
using YARG.Net.Transport;

namespace YARG.Net.Handlers.Client;

/// <summary>
/// Helper for sending lobby-related commands from a client to the server.
/// </summary>
public sealed class ClientLobbyCommandSender
{
    private readonly INetSerializer _serializer;

    public ClientLobbyCommandSender(INetSerializer serializer)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    }

    public void SendReadyState(INetConnection connection, Guid sessionId, bool isReady)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        var envelope = PacketEnvelope<LobbyReadyStatePacket>.Create(
            PacketType.LobbyReadyState,
            new LobbyReadyStatePacket(sessionId, isReady));

        connection.Send(_serializer.Serialize(envelope).Span, ChannelType.ReliableOrdered);
    }

    public void SendReadyState(INetConnection connection, ClientSessionContext sessionContext, bool isReady)
    {
        if (sessionContext is null)
        {
            throw new ArgumentNullException(nameof(sessionContext));
        }

        SendReadyState(connection, RequireSessionId(sessionContext), isReady);
    }

    public void SendSongSelection(INetConnection connection, Guid sessionId, SongSelectionState selection)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        if (selection is null)
        {
            throw new ArgumentNullException(nameof(selection));
        }

        var envelope = PacketEnvelope<SongSelectionPacket>.Create(
            PacketType.SongSelection,
            new SongSelectionPacket(sessionId, selection));

        connection.Send(_serializer.Serialize(envelope).Span, ChannelType.ReliableOrdered);
    }

    public void SendSongSelection(INetConnection connection, ClientSessionContext sessionContext, SongSelectionState selection)
    {
        if (sessionContext is null)
        {
            throw new ArgumentNullException(nameof(sessionContext));
        }

        SendSongSelection(connection, RequireSessionId(sessionContext), selection);
    }

    private static Guid RequireSessionId(ClientSessionContext sessionContext)
    {
        var sessionId = sessionContext.SessionId;
        if (!sessionId.HasValue || sessionId.Value == Guid.Empty)
        {
            throw new InvalidOperationException("Client session has not been established yet.");
        }

        return sessionId.Value;
    }
}
