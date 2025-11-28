using System;
using System.Threading;
using System.Threading.Tasks;
using YARG.Net.Packets;
using YARG.Net.Packets.Dispatch;
using YARG.Net.Serialization;
using YARG.Net.Sessions;
using YARG.Net.Transport;

namespace YARG.Net.Handlers;

/// <summary>
/// Handles <see cref="HandshakeRequestPacket"/> messages for server runtimes.
/// </summary>
public sealed class ServerHandshakeHandler
{
    private readonly SessionManager _sessionManager;
    private readonly INetSerializer _serializer;
    private readonly HandshakeServerOptions _options;

    public event EventHandler<SessionRecord>? HandshakeAccepted;
    public event EventHandler<HandshakeRejectedEventArgs>? HandshakeRejected;

    public ServerHandshakeHandler(SessionManager sessionManager, INetSerializer serializer, HandshakeServerOptions? options = null)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _options = options ?? new HandshakeServerOptions();

        if (_options.MinPlayerNameLength < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(HandshakeServerOptions.MinPlayerNameLength),
                _options.MinPlayerNameLength,
                "Minimum player name length must be at least 1.");
        }

        if (_options.MinPlayerNameLength > _options.MaxPlayerNameLength)
        {
            throw new ArgumentException("Minimum player name length cannot exceed maximum length.");
        }
    }

    public void Register(IPacketDispatcher dispatcher)
    {
        if (dispatcher is null)
        {
            throw new ArgumentNullException(nameof(dispatcher));
        }

        dispatcher.RegisterHandler<HandshakeRequestPacket>(PacketType.HandshakeRequest, HandleAsync);
    }

    public Task HandleAsync(PacketContext context, PacketEnvelope<HandshakeRequestPacket> envelope, CancellationToken cancellationToken)
    {
        if (context.Role != PacketEndpointRole.Server)
        {
            return Task.CompletedTask;
        }

        var request = envelope.Payload;
        var sanitizedName = (request.PlayerName ?? string.Empty).Trim();

        if (!ValidateProtocol(request.ClientVersion, out var reason))
        {
            Reject(context, reason);
            return Task.CompletedTask;
        }

        if (!ValidatePlayerName(sanitizedName, out reason))
        {
            Reject(context, reason);
            return Task.CompletedTask;
        }

        if (!ValidatePassword(request.Password, out reason))
        {
            Reject(context, reason);
            return Task.CompletedTask;
        }

        if (_sessionManager.TryCreateSession(context.Connection, sanitizedName, out var session, out var error))
        {
            var createdSession = session!;
            SendResponse(context.Connection, new HandshakeResponsePacket(true, null, createdSession.SessionId));
            HandshakeAccepted?.Invoke(this, createdSession);
            return Task.CompletedTask;
        }

        reason = error switch
        {
            SessionCreationError.AlreadyRegistered => "Connection already completed handshake.",
            SessionCreationError.ServerFull => "Server is full.",
            _ => "Unable to join server.",
        };

        Reject(context, reason);
        return Task.CompletedTask;
    }

    private bool ValidateProtocol(string clientVersion, out string? reason)
    {
        if (string.Equals(clientVersion, _options.ExpectedProtocolVersion, StringComparison.Ordinal))
        {
            reason = null;
            return true;
        }

        reason = $"Protocol mismatch. Server requires {_options.ExpectedProtocolVersion}.";
        return false;
    }

    private bool ValidatePlayerName(string playerName, out string? reason)
    {
        if (playerName.Length < _options.MinPlayerNameLength)
        {
            reason = $"Player name must be at least {_options.MinPlayerNameLength} characters.";
            return false;
        }

        if (playerName.Length > _options.MaxPlayerNameLength)
        {
            reason = $"Player name must be {_options.MaxPlayerNameLength} characters or fewer.";
            return false;
        }

        foreach (var character in playerName)
        {
            if (character is < ' ' or > '~')
            {
                reason = "Player name contains unsupported characters.";
                return false;
            }
        }

        if (_options.PlayerNameFilter is not null && !_options.PlayerNameFilter(playerName))
        {
            reason = "Player name is not allowed.";
            return false;
        }

        reason = null;
        return true;
    }

    private bool ValidatePassword(string? providedPassword, out string? reason)
    {
        if (string.IsNullOrEmpty(_options.Password))
        {
            reason = null;
            return true;
        }

        if (string.Equals(providedPassword, _options.Password, StringComparison.Ordinal))
        {
            reason = null;
            return true;
        }

        reason = "Invalid password.";
        return false;
    }

    private void Reject(PacketContext context, string? reason)
    {
        SendResponse(context.Connection, new HandshakeResponsePacket(false, reason, Guid.Empty));
        HandshakeRejected?.Invoke(this, new HandshakeRejectedEventArgs(context, reason ?? string.Empty));

        if (_options.DisconnectOnReject)
        {
            context.Connection.Disconnect(reason);
        }
    }

    private void SendResponse(INetConnection connection, HandshakeResponsePacket response)
    {
        var envelope = PacketEnvelope<HandshakeResponsePacket>.Create(PacketType.HandshakeResponse, response);
        var buffer = _serializer.Serialize(envelope);
        connection.Send(buffer.Span, ChannelType.ReliableOrdered);
    }
}

public sealed record HandshakeServerOptions
{
    public string ExpectedProtocolVersion { get; init; } = ProtocolVersion.Current;
    public int MinPlayerNameLength { get; init; } = 2;
    public int MaxPlayerNameLength { get; init; } = 24;
    public string? Password { get; init; }

    public bool DisconnectOnReject { get; init; } = true;
    public Func<string, bool>? PlayerNameFilter { get; init; }
}

public sealed class HandshakeRejectedEventArgs : EventArgs
{
    public HandshakeRejectedEventArgs(PacketContext context, string reason)
    {
        Context = context;
        Reason = reason;
    }

    public PacketContext Context { get; }
    public string Reason { get; }
}
