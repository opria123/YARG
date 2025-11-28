using System;

namespace YARG.Net.Runtime;

/// <summary>
/// Tracks the local client's session identifier once the server accepts the handshake.
/// </summary>
public sealed class ClientSessionContext
{
    private readonly object _gate = new();
    private Guid? _sessionId;

    public Guid? SessionId
    {
        get
        {
            lock (_gate)
            {
                return _sessionId;
            }
        }
    }

    public bool HasSession
    {
        get
        {
            lock (_gate)
            {
                return _sessionId.HasValue;
            }
        }
    }

    public event EventHandler<ClientSessionChangedEventArgs>? SessionChanged;

    public bool TrySetSession(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionId), "SessionId cannot be empty.");
        }

        ClientSessionChangedEventArgs? args = null;

        lock (_gate)
        {
            if (_sessionId == sessionId)
            {
                return false;
            }

            args = new ClientSessionChangedEventArgs(_sessionId, sessionId);
            _sessionId = sessionId;
        }

        SessionChanged?.Invoke(this, args);
        return true;
    }

    public bool ClearSession()
    {
        ClientSessionChangedEventArgs? args = null;

        lock (_gate)
        {
            if (_sessionId is null)
            {
                return false;
            }

            args = new ClientSessionChangedEventArgs(_sessionId, null);
            _sessionId = null;
        }

        SessionChanged?.Invoke(this, args);
        return true;
    }
}

public sealed class ClientSessionChangedEventArgs : EventArgs
{
    public ClientSessionChangedEventArgs(Guid? previousSessionId, Guid? currentSessionId)
    {
        PreviousSessionId = previousSessionId;
        CurrentSessionId = currentSessionId;
    }

    public Guid? PreviousSessionId { get; }
    public Guid? CurrentSessionId { get; }
    public bool HasSession => CurrentSessionId.HasValue;
}
