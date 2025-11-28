using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using YARG.Net.Packets;
using YARG.Net.Packets.Dispatch;
using YARG.Net.Serialization;
using YARG.Net.Sessions;
using YARG.Net.Transport;

namespace YARG.Net.Handlers.Server;

/// <summary>
/// Handles gameplay-related packets and broadcasts state to all clients.
/// </summary>
public sealed class ServerGameplayHandler
{
    private readonly SessionManager _sessionManager;
    private readonly LobbyStateManager _lobbyManager;
    private readonly INetSerializer _serializer;
    private readonly Dictionary<Guid, ReplaySyncDataPacket> _replayDataCache = new();
    private readonly object _replaySyncLock = new();

    /// <summary>
    /// Raised when a gameplay state is received from a client.
    /// </summary>
    public event EventHandler<GameplayStateReceivedEventArgs>? GameplayStateReceived;

    /// <summary>
    /// Raised when a pause state changes.
    /// </summary>
    public event EventHandler<GameplayPauseChangedEventArgs>? PauseStateChanged;

    /// <summary>
    /// Raised when replay data is received from a client.
    /// </summary>
    public event EventHandler<ReplaySyncDataReceivedEventArgs>? ReplaySyncDataReceived;

    /// <summary>
    /// Raised when all replay data has been collected and sync is complete.
    /// </summary>
    public event EventHandler<ReplaySyncCompleteEventArgs>? ReplaySyncComplete;

    public ServerGameplayHandler(SessionManager sessionManager, LobbyStateManager lobbyManager, INetSerializer serializer)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _lobbyManager = lobbyManager ?? throw new ArgumentNullException(nameof(lobbyManager));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    }

    public void Register(IPacketDispatcher dispatcher)
    {
        if (dispatcher is null)
        {
            throw new ArgumentNullException(nameof(dispatcher));
        }

        dispatcher.RegisterHandler<GameplayStatePacket>(PacketType.GameplayState, HandleGameplayStateAsync);
        dispatcher.RegisterHandler<GameplayPausePacket>(PacketType.GameplayPause, HandleGameplayPauseAsync);
        dispatcher.RegisterHandler<ReplaySyncDataPacket>(PacketType.ReplaySyncData, HandleReplaySyncDataAsync);
    }

    /// <summary>
    /// Broadcasts a gameplay start signal to all clients.
    /// </summary>
    public void BroadcastGameplayStart(double serverTime, double songStartTime)
    {
        var packet = new GameplayStartPacket(_lobbyManager.LobbyId, serverTime, songStartTime);
        var envelope = PacketEnvelope<GameplayStartPacket>.Create(PacketType.GameplayStart, packet);
        BroadcastToAll(envelope);
    }

    /// <summary>
    /// Broadcasts a time sync packet to all clients.
    /// </summary>
    public void BroadcastTimeSync(double serverTime, double songTime)
    {
        var packet = new GameplayTimeSyncPacket(_lobbyManager.LobbyId, serverTime, songTime);
        var envelope = PacketEnvelope<GameplayTimeSyncPacket>.Create(PacketType.GameplayTimeSync, packet);
        BroadcastToAll(envelope);
    }

    /// <summary>
    /// Broadcasts a gameplay end signal to all clients.
    /// </summary>
    public void BroadcastGameplayEnd(GameplayEndReason reason)
    {
        var packet = new GameplayEndPacket(_lobbyManager.LobbyId, reason);
        var envelope = PacketEnvelope<GameplayEndPacket>.Create(PacketType.GameplayEnd, packet);
        BroadcastToAll(envelope);
    }

    /// <summary>
    /// Requests replay data from all connected clients.
    /// </summary>
    public void RequestReplayData()
    {
        lock (_replaySyncLock)
        {
            _replayDataCache.Clear();
        }

        var packet = new ReplaySyncRequestPacket(_lobbyManager.LobbyId);
        var envelope = PacketEnvelope<ReplaySyncRequestPacket>.Create(PacketType.ReplaySyncRequest, packet);
        BroadcastToAll(envelope);
    }

    /// <summary>
    /// Notifies all clients that replay sync is complete.
    /// </summary>
    public void BroadcastReplaySyncComplete()
    {
        var packet = new ReplaySyncCompletePacket(_lobbyManager.LobbyId);
        var envelope = PacketEnvelope<ReplaySyncCompletePacket>.Create(PacketType.ReplaySyncComplete, packet);
        BroadcastToAll(envelope);
    }

    /// <summary>
    /// Gets all collected replay data.
    /// </summary>
    public IReadOnlyDictionary<Guid, ReplaySyncDataPacket> GetCollectedReplayData()
    {
        lock (_replaySyncLock)
        {
            return new Dictionary<Guid, ReplaySyncDataPacket>(_replayDataCache);
        }
    }

    /// <summary>
    /// Clears cached replay data.
    /// </summary>
    public void ClearReplayData()
    {
        lock (_replaySyncLock)
        {
            _replayDataCache.Clear();
        }
    }

    private Task HandleReplaySyncDataAsync(PacketContext context, PacketEnvelope<ReplaySyncDataPacket> envelope, CancellationToken cancellationToken)
    {
        if (context.Role != PacketEndpointRole.Server)
        {
            return Task.CompletedTask;
        }

        if (!TryValidateSession(context, envelope.Payload.SessionId, out var session))
        {
            return Task.CompletedTask;
        }

        // Cache the replay data
        lock (_replaySyncLock)
        {
            _replayDataCache[session.SessionId] = envelope.Payload;
        }

        // Raise event
        ReplaySyncDataReceived?.Invoke(this, new ReplaySyncDataReceivedEventArgs(session.SessionId, envelope.Payload));

        // Check if all sessions have reported
        var allSessions = _sessionManager.GetSessionsSnapshot();
        bool allCollected;
        lock (_replaySyncLock)
        {
            allCollected = allSessions.Count == _replayDataCache.Count;
        }

        if (allCollected)
        {
            var collectedData = GetCollectedReplayData();
            ReplaySyncComplete?.Invoke(this, new ReplaySyncCompleteEventArgs(collectedData));
        }

        return Task.CompletedTask;
    }

    private Task HandleGameplayStateAsync(PacketContext context, PacketEnvelope<GameplayStatePacket> envelope, CancellationToken cancellationToken)
    {
        if (context.Role != PacketEndpointRole.Server)
        {
            return Task.CompletedTask;
        }

        if (!TryValidateSession(context, envelope.Payload.SessionId, out var session))
        {
            return Task.CompletedTask;
        }

        // Raise event for local handling
        GameplayStateReceived?.Invoke(this, new GameplayStateReceivedEventArgs(session.SessionId, envelope.Payload));

        // Broadcast to all other clients
        BroadcastToOthers(envelope, session.ConnectionId);

        return Task.CompletedTask;
    }

    private Task HandleGameplayPauseAsync(PacketContext context, PacketEnvelope<GameplayPausePacket> envelope, CancellationToken cancellationToken)
    {
        if (context.Role != PacketEndpointRole.Server)
        {
            return Task.CompletedTask;
        }

        if (!TryValidateSession(context, envelope.Payload.SessionId, out var session))
        {
            return Task.CompletedTask;
        }

        // Only host can pause for everyone
        if (!_lobbyManager.IsHost(session.SessionId))
        {
            return Task.CompletedTask;
        }

        PauseStateChanged?.Invoke(this, new GameplayPauseChangedEventArgs(session.SessionId, envelope.Payload.IsPaused, envelope.Payload.PauseTime));

        // Broadcast to all clients
        BroadcastToAll(envelope);

        return Task.CompletedTask;
    }

    private void BroadcastToAll<T>(PacketEnvelope<T> envelope) where T : IPacketPayload
    {
        var buffer = _serializer.Serialize(envelope);
        IReadOnlyList<SessionRecord> sessions = _sessionManager.GetSessionsSnapshot();
        foreach (var session in sessions)
        {
            session.Connection.Send(buffer.Span, ChannelType.ReliableSequenced);
        }
    }

    private void BroadcastToOthers<T>(PacketEnvelope<T> envelope, Guid excludeConnectionId) where T : IPacketPayload
    {
        var buffer = _serializer.Serialize(envelope);
        IReadOnlyList<SessionRecord> sessions = _sessionManager.GetSessionsSnapshot();
        foreach (var session in sessions)
        {
            if (session.ConnectionId != excludeConnectionId)
            {
                session.Connection.Send(buffer.Span, ChannelType.ReliableSequenced);
            }
        }
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

public sealed class GameplayStateReceivedEventArgs : EventArgs
{
    public Guid SessionId { get; }
    public GameplayStatePacket State { get; }

    public GameplayStateReceivedEventArgs(Guid sessionId, GameplayStatePacket state)
    {
        SessionId = sessionId;
        State = state;
    }
}

public sealed class GameplayPauseChangedEventArgs : EventArgs
{
    public Guid SessionId { get; }
    public bool IsPaused { get; }
    public double PauseTime { get; }

    public GameplayPauseChangedEventArgs(Guid sessionId, bool isPaused, double pauseTime)
    {
        SessionId = sessionId;
        IsPaused = isPaused;
        PauseTime = pauseTime;
    }
}

public sealed class ReplaySyncDataReceivedEventArgs : EventArgs
{
    public Guid SessionId { get; }
    public byte[] SerializedFrame { get; }
    public byte[] SerializedStats { get; }
    public Guid ColorProfileId { get; }
    public string ColorProfileJson { get; }
    public Guid CameraPresetId { get; }
    public string CameraPresetJson { get; }
    public double[] FrameTimes { get; }

    public ReplaySyncDataReceivedEventArgs(Guid sessionId, ReplaySyncDataPacket data)
    {
        SessionId = sessionId;
        SerializedFrame = data.SerializedReplayFrame;
        SerializedStats = data.SerializedReplayStats;
        ColorProfileId = data.ColorProfileId;
        ColorProfileJson = data.ColorProfileJson;
        CameraPresetId = data.CameraPresetId;
        CameraPresetJson = data.CameraPresetJson;
        FrameTimes = data.FrameTimes;
    }
}

public sealed class ReplaySyncCompleteEventArgs : EventArgs
{
    public IReadOnlyDictionary<Guid, ReplaySyncDataPacket> CollectedData { get; }

    public ReplaySyncCompleteEventArgs(IReadOnlyDictionary<Guid, ReplaySyncDataPacket> collectedData)
    {
        CollectedData = collectedData;
    }
}
