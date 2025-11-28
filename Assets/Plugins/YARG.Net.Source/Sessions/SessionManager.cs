using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using YARG.Net.Transport;

namespace YARG.Net.Sessions;

/// <summary>
/// Tracks active player sessions for a server runtime.
/// </summary>
public sealed class SessionManager
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, SessionRecord> _sessionsById = new();
    private readonly Dictionary<Guid, Guid> _connectionToSession = new();
    private readonly int _capacity;

    public SessionManager(int capacity = 0)
    {
        if (capacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public IReadOnlyList<SessionRecord> GetSessionsSnapshot()
    {
        lock (_gate)
        {
            return _sessionsById.Values.ToArray();
        }
    }

    public int Capacity => _capacity;

    public int ActiveSessionCount
    {
        get
        {
            lock (_gate)
            {
                return _sessionsById.Count;
            }
        }
    }

    public bool TryCreateSession(
        INetConnection connection,
        string playerName,
        [NotNullWhen(true)] out SessionRecord? session,
        out SessionCreationError error)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        if (playerName is null)
        {
            throw new ArgumentNullException(nameof(playerName));
        }

        lock (_gate)
        {
            if (_connectionToSession.ContainsKey(connection.Id))
            {
                session = null;
                error = SessionCreationError.AlreadyRegistered;
                return false;
            }

            if (_capacity > 0 && _sessionsById.Count >= _capacity)
            {
                session = null;
                error = SessionCreationError.ServerFull;
                return false;
            }

            var record = new SessionRecord(Guid.NewGuid(), connection.Id, playerName, DateTimeOffset.UtcNow, connection);
            _sessionsById.Add(record.SessionId, record);
            _connectionToSession.Add(connection.Id, record.SessionId);

            session = record;
            error = SessionCreationError.None;
            return true;
        }
    }

    public bool TryGetSession(Guid sessionId, [NotNullWhen(true)] out SessionRecord? session)
    {
        lock (_gate)
        {
            return _sessionsById.TryGetValue(sessionId, out session);
        }
    }

    public bool TryGetSessionByConnection(Guid connectionId, [NotNullWhen(true)] out SessionRecord? session)
    {
        lock (_gate)
        {
            if (_connectionToSession.TryGetValue(connectionId, out var sessionId) &&
                _sessionsById.TryGetValue(sessionId, out var record))
            {
                session = record;
                return true;
            }
        }

        session = null;
        return false;
    }

    public bool ContainsConnection(Guid connectionId)
    {
        lock (_gate)
        {
            return _connectionToSession.ContainsKey(connectionId);
        }
    }

    public bool TryRemoveSession(Guid sessionId, [NotNullWhen(true)] out SessionRecord? session)
    {
        lock (_gate)
        {
            if (_sessionsById.TryGetValue(sessionId, out var record))
            {
                _sessionsById.Remove(sessionId);
                _connectionToSession.Remove(record.ConnectionId);
                session = record;
                return true;
            }
        }

        session = null;
        return false;
    }

    public bool TryRemoveSessionByConnection(Guid connectionId, [NotNullWhen(true)] out SessionRecord? session)
    {
        lock (_gate)
        {
            if (_connectionToSession.TryGetValue(connectionId, out var sessionId) &&
                _sessionsById.TryGetValue(sessionId, out var record))
            {
                _sessionsById.Remove(sessionId);
                _connectionToSession.Remove(connectionId);
                session = record;
                return true;
            }
        }

        session = null;
        return false;
    }
}

public enum SessionCreationError
{
    None = 0,
    AlreadyRegistered,
    ServerFull,
}
