using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using YARG.Net.Packets;
using YARG.Net.Packets.Dispatch;
using YARG.Net.Runtime;
using YARG.Net.Serialization;
using YARG.Net.Sessions;
using YARG.Net.Transport;

namespace YARG.Net.Handlers.Client;

/// <summary>
/// Handles gameplay-related packets on the client side.
/// </summary>
public sealed class ClientGameplayHandler
{
    private readonly INetSerializer _serializer;
    private readonly object _stateLock = new();
    private readonly Dictionary<Guid, GameplayStatePacket> _playerStates = new();

    /// <summary>
    /// Raised when gameplay state is received from another player.
    /// </summary>
    public event EventHandler<ClientGameplayStateReceivedEventArgs>? GameplayStateReceived;

    /// <summary>
    /// Raised when gameplay start signal is received.
    /// </summary>
    public event EventHandler<ClientGameplayStartEventArgs>? GameplayStartReceived;

    /// <summary>
    /// Raised when time sync is received from the server.
    /// </summary>
    public event EventHandler<ClientTimeSyncReceivedEventArgs>? TimeSyncReceived;

    /// <summary>
    /// Raised when pause state changes.
    /// </summary>
    public event EventHandler<ClientPauseChangedEventArgs>? PauseStateChanged;

    /// <summary>
    /// Raised when gameplay ends.
    /// </summary>
    public event EventHandler<ClientGameplayEndEventArgs>? GameplayEnded;

    /// <summary>
    /// Raised when server requests replay data.
    /// </summary>
    public event EventHandler<ClientReplaySyncRequestEventArgs>? ReplaySyncRequested;

    /// <summary>
    /// Raised when replay sync is complete.
    /// </summary>
    public event EventHandler<ClientReplaySyncCompleteEventArgs>? ReplaySyncComplete;

    public ClientGameplayHandler(INetSerializer serializer)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    }

    public void Register(IPacketDispatcher dispatcher)
    {
        if (dispatcher is null)
        {
            throw new ArgumentNullException(nameof(dispatcher));
        }

        dispatcher.RegisterHandler<GameplayStatePacket>(PacketType.GameplayState, HandleGameplayStateAsync);
        dispatcher.RegisterHandler<GameplayStartPacket>(PacketType.GameplayStart, HandleGameplayStartAsync);
        dispatcher.RegisterHandler<GameplayTimeSyncPacket>(PacketType.GameplayTimeSync, HandleTimeSyncAsync);
        dispatcher.RegisterHandler<GameplayPausePacket>(PacketType.GameplayPause, HandlePauseAsync);
        dispatcher.RegisterHandler<GameplayEndPacket>(PacketType.GameplayEnd, HandleGameplayEndAsync);
        dispatcher.RegisterHandler<ReplaySyncRequestPacket>(PacketType.ReplaySyncRequest, HandleReplaySyncRequestAsync);
        dispatcher.RegisterHandler<ReplaySyncCompletePacket>(PacketType.ReplaySyncComplete, HandleReplaySyncCompleteAsync);
    }

    /// <summary>
    /// Sends local gameplay state to the server for broadcast.
    /// </summary>
    public void SendGameplayState(INetConnection connection, ClientSessionContext sessionContext, GameplayStatePacket state)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        if (sessionContext is null)
        {
            throw new ArgumentNullException(nameof(sessionContext));
        }

        var envelope = PacketEnvelope<GameplayStatePacket>.Create(PacketType.GameplayState, state);
        var buffer = _serializer.Serialize(envelope);
        connection.Send(buffer.Span, ChannelType.ReliableSequenced);
    }

    /// <summary>
    /// Sends a pause request to the server.
    /// </summary>
    public void SendPauseState(INetConnection connection, Guid sessionId, bool isPaused, double pauseTime)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        var packet = new GameplayPausePacket(sessionId, isPaused, pauseTime);
        var envelope = PacketEnvelope<GameplayPausePacket>.Create(PacketType.GameplayPause, packet);
        var buffer = _serializer.Serialize(envelope);
        connection.Send(buffer.Span, ChannelType.ReliableOrdered);
    }

    /// <summary>
    /// Sends replay data to the server.
    /// </summary>
    public void SendReplayData(INetConnection connection, Guid sessionId, byte[] replayFrame, byte[] replayStats, Guid colorProfileId, string colorProfileJson, Guid cameraPresetId, string cameraPresetJson, double[] frameTimes)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        var packet = new ReplaySyncDataPacket(sessionId, replayFrame, replayStats, colorProfileId, colorProfileJson, cameraPresetId, cameraPresetJson, frameTimes);
        var envelope = PacketEnvelope<ReplaySyncDataPacket>.Create(PacketType.ReplaySyncData, packet);
        var buffer = _serializer.Serialize(envelope);
        connection.Send(buffer.Span, ChannelType.ReliableOrdered);
    }

    /// <summary>
    /// Gets the latest gameplay state for a specific player.
    /// </summary>
    public bool TryGetPlayerState(Guid sessionId, out GameplayStatePacket? state)
    {
        lock (_stateLock)
        {
            if (_playerStates.TryGetValue(sessionId, out var cached))
            {
                state = cached;
                return true;
            }

            state = null;
            return false;
        }
    }

    /// <summary>
    /// Gets all current player states.
    /// </summary>
    public IReadOnlyDictionary<Guid, GameplayStatePacket> GetAllPlayerStates()
    {
        lock (_stateLock)
        {
            return new Dictionary<Guid, GameplayStatePacket>(_playerStates);
        }
    }

    /// <summary>
    /// Clears all cached player states.
    /// </summary>
    public void ClearPlayerStates()
    {
        lock (_stateLock)
        {
            _playerStates.Clear();
        }
    }

    private Task HandleGameplayStateAsync(PacketContext context, PacketEnvelope<GameplayStatePacket> envelope, CancellationToken cancellationToken)
    {
        if (context.Role != PacketEndpointRole.Client)
        {
            return Task.CompletedTask;
        }

        var state = envelope.Payload;

        lock (_stateLock)
        {
            // Only update if this is a newer sequence
            if (_playerStates.TryGetValue(state.SessionId, out var existing) && existing.Sequence >= state.Sequence)
            {
                return Task.CompletedTask;
            }

            _playerStates[state.SessionId] = state;
        }

        GameplayStateReceived?.Invoke(this, new ClientGameplayStateReceivedEventArgs(state.SessionId, state));

        return Task.CompletedTask;
    }

    private Task HandleGameplayStartAsync(PacketContext context, PacketEnvelope<GameplayStartPacket> envelope, CancellationToken cancellationToken)
    {
        if (context.Role != PacketEndpointRole.Client)
        {
            return Task.CompletedTask;
        }

        var packet = envelope.Payload;
        GameplayStartReceived?.Invoke(this, new ClientGameplayStartEventArgs(packet.LobbyId, packet.ServerTime, packet.SongStartTime));

        return Task.CompletedTask;
    }

    private Task HandleTimeSyncAsync(PacketContext context, PacketEnvelope<GameplayTimeSyncPacket> envelope, CancellationToken cancellationToken)
    {
        if (context.Role != PacketEndpointRole.Client)
        {
            return Task.CompletedTask;
        }

        var packet = envelope.Payload;
        TimeSyncReceived?.Invoke(this, new ClientTimeSyncReceivedEventArgs(packet.LobbyId, packet.ServerTime, packet.SongTime));

        return Task.CompletedTask;
    }

    private Task HandlePauseAsync(PacketContext context, PacketEnvelope<GameplayPausePacket> envelope, CancellationToken cancellationToken)
    {
        if (context.Role != PacketEndpointRole.Client)
        {
            return Task.CompletedTask;
        }

        var packet = envelope.Payload;
        PauseStateChanged?.Invoke(this, new ClientPauseChangedEventArgs(packet.SessionId, packet.IsPaused, packet.PauseTime));

        return Task.CompletedTask;
    }

    private Task HandleGameplayEndAsync(PacketContext context, PacketEnvelope<GameplayEndPacket> envelope, CancellationToken cancellationToken)
    {
        if (context.Role != PacketEndpointRole.Client)
        {
            return Task.CompletedTask;
        }

        var packet = envelope.Payload;
        GameplayEnded?.Invoke(this, new ClientGameplayEndEventArgs(packet.LobbyId, packet.Reason));

        return Task.CompletedTask;
    }

    private Task HandleReplaySyncRequestAsync(PacketContext context, PacketEnvelope<ReplaySyncRequestPacket> envelope, CancellationToken cancellationToken)
    {
        if (context.Role != PacketEndpointRole.Client)
        {
            return Task.CompletedTask;
        }

        var packet = envelope.Payload;
        ReplaySyncRequested?.Invoke(this, new ClientReplaySyncRequestEventArgs(packet.LobbyId));

        return Task.CompletedTask;
    }

    private Task HandleReplaySyncCompleteAsync(PacketContext context, PacketEnvelope<ReplaySyncCompletePacket> envelope, CancellationToken cancellationToken)
    {
        if (context.Role != PacketEndpointRole.Client)
        {
            return Task.CompletedTask;
        }

        var packet = envelope.Payload;
        ReplaySyncComplete?.Invoke(this, new ClientReplaySyncCompleteEventArgs(packet.LobbyId));

        return Task.CompletedTask;
    }
}

public sealed class ClientGameplayStateReceivedEventArgs : EventArgs
{
    public Guid SessionId { get; }
    public GameplayStatePacket State { get; }

    public ClientGameplayStateReceivedEventArgs(Guid sessionId, GameplayStatePacket state)
    {
        SessionId = sessionId;
        State = state;
    }
}

public sealed class ClientGameplayStartEventArgs : EventArgs
{
    public Guid LobbyId { get; }
    public double ServerTime { get; }
    public double SongStartTime { get; }

    public ClientGameplayStartEventArgs(Guid lobbyId, double serverTime, double songStartTime)
    {
        LobbyId = lobbyId;
        ServerTime = serverTime;
        SongStartTime = songStartTime;
    }
}

public sealed class ClientTimeSyncReceivedEventArgs : EventArgs
{
    public Guid LobbyId { get; }
    public double ServerTime { get; }
    public double SongTime { get; }

    public ClientTimeSyncReceivedEventArgs(Guid lobbyId, double serverTime, double songTime)
    {
        LobbyId = lobbyId;
        ServerTime = serverTime;
        SongTime = songTime;
    }
}

public sealed class ClientPauseChangedEventArgs : EventArgs
{
    public Guid SessionId { get; }
    public bool IsPaused { get; }
    public double PauseTime { get; }

    public ClientPauseChangedEventArgs(Guid sessionId, bool isPaused, double pauseTime)
    {
        SessionId = sessionId;
        IsPaused = isPaused;
        PauseTime = pauseTime;
    }
}

public sealed class ClientGameplayEndEventArgs : EventArgs
{
    public Guid LobbyId { get; }
    public GameplayEndReason Reason { get; }

    public ClientGameplayEndEventArgs(Guid lobbyId, GameplayEndReason reason)
    {
        LobbyId = lobbyId;
        Reason = reason;
    }
}

public sealed class ClientReplaySyncRequestEventArgs : EventArgs
{
    public Guid LobbyId { get; }

    public ClientReplaySyncRequestEventArgs(Guid lobbyId)
    {
        LobbyId = lobbyId;
    }
}

public sealed class ClientReplaySyncCompleteEventArgs : EventArgs
{
    public Guid LobbyId { get; }

    public ClientReplaySyncCompleteEventArgs(Guid lobbyId)
    {
        LobbyId = lobbyId;
    }
}
