using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using YARG.Net.Packets;

namespace YARG.Net.Sessions;

/// <summary>
/// Maintains lobby membership, readiness, and selection state.
/// </summary>
public sealed class LobbyStateManager
{
    private readonly object _gate = new();
    private readonly SessionManager _sessionManager;
    private readonly LobbyConfiguration _configuration;
    private readonly Dictionary<Guid, LobbyPlayerState> _players = new();

    private SongSelectionState? _selectionState;
    private LobbyStatus _status = LobbyStatus.Idle;
    private Guid? _hostSessionId;
    private bool _countdownActive;

    public LobbyStateManager(SessionManager sessionManager, LobbyConfiguration? configuration = null)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _configuration = configuration ?? new LobbyConfiguration();

        if (_configuration.MaxPlayers <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(configuration.MaxPlayers), "MaxPlayers must be greater than zero.");
        }
    }

    public Guid LobbyId { get; } = Guid.NewGuid();

    public LobbyStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public string? SelectedSongId
    {
        get
        {
            lock (_gate)
            {
                return _selectionState?.SongId;
            }
        }
    }

    public SongSelectionState? SelectionState
    {
        get
        {
            lock (_gate)
            {
                return _selectionState;
            }
        }
    }

    public int PlayerCount
    {
        get
        {
            lock (_gate)
            {
                return _players.Count;
            }
        }
    }

    public bool IsHost(Guid sessionId)
    {
        lock (_gate)
        {
            return _hostSessionId == sessionId;
        }
    }

    public bool TryGetPlayer(Guid sessionId, [NotNullWhen(true)] out LobbyPlayer? player)
    {
        lock (_gate)
        {
            if (_players.TryGetValue(sessionId, out var state))
            {
                player = state.ToLobbyPlayer();
                return true;
            }
        }

        player = null;
        return false;
    }

    public bool TryAddPlayer(Guid sessionId, LobbyRole requestedRole, [NotNullWhen(true)] out LobbyPlayer? player, out LobbyJoinError error)
    {
        lock (_gate)
        {
            if (_players.ContainsKey(sessionId))
            {
                player = null;
                error = LobbyJoinError.AlreadyInLobby;
                return false;
            }

            if (!_sessionManager.TryGetSession(sessionId, out var session))
            {
                player = null;
                error = LobbyJoinError.SessionNotFound;
                return false;
            }

            if (!ValidateCapacityLocked(requestedRole))
            {
                player = null;
                error = requestedRole == LobbyRole.Spectator
                    ? LobbyJoinError.SpectatorsDisabled
                    : LobbyJoinError.LobbyFull;
                return false;
            }

            var assignedRole = AssignRoleLocked(requestedRole);
            var state = new LobbyPlayerState(session.SessionId, session.PlayerName, assignedRole);
            _players.Add(session.SessionId, state);

            if (assignedRole == LobbyRole.Host)
            {
                _hostSessionId = session.SessionId;
            }

            player = state.ToLobbyPlayer();
            error = LobbyJoinError.None;
            PlayerJoined?.Invoke(this, new LobbyPlayerChangedEventArgs(player));
            UpdateStatusLocked();
            return true;
        }
    }

    public bool TryRemovePlayer(Guid sessionId, [NotNullWhen(true)] out LobbyPlayer? removedPlayer)
    {
        lock (_gate)
        {
            if (!_players.Remove(sessionId, out var state))
            {
                removedPlayer = null;
                return false;
            }

            if (_hostSessionId == sessionId)
            {
                PromoteNextHostLocked();
            }

            // Cancel countdown if a player leaves
            if (_countdownActive)
            {
                CancelCountdownLocked();
            }

            removedPlayer = state.ToLobbyPlayer();
            PlayerLeft?.Invoke(this, new LobbyPlayerChangedEventArgs(removedPlayer));
            UpdateStatusLocked();
            return true;
        }
    }

    public bool TrySetReady(Guid sessionId, bool isReady, [NotNullWhen(true)] out LobbyPlayer? updatedPlayer)
    {
        lock (_gate)
        {
            if (!_players.TryGetValue(sessionId, out var state))
            {
                updatedPlayer = null;
                return false;
            }

            if (state.Role == LobbyRole.Spectator)
            {
                updatedPlayer = null;
                return false;
            }

            if (state.IsReady == isReady)
            {
                updatedPlayer = state.ToLobbyPlayer();
                return false;
            }

            state.SetReady(isReady);
            updatedPlayer = state.ToLobbyPlayer();
            PlayerReadyStateChanged?.Invoke(this, new LobbyPlayerChangedEventArgs(updatedPlayer));
            
            // Cancel countdown if a player becomes unready
            if (!isReady && _countdownActive)
            {
                CancelCountdownLocked();
            }
            
            UpdateStatusLocked();
            return true;
        }
    }

    public bool TryApplySongSelection(SongSelectionState? selection)
    {
        lock (_gate)
        {
            if (selection is null || string.IsNullOrWhiteSpace(selection.SongId))
            {
                return ClearSelectionLocked();
            }

            var normalizedId = selection.SongId.Trim();
            var normalizedAssignments = NormalizeAssignmentsLocked(selection.Assignments);
            var normalizedState = new SongSelectionState(normalizedId, normalizedAssignments, selection.AllReady);

            if (_selectionState is not null && SongSelectionEquals(_selectionState, normalizedState))
            {
                return false;
            }

            _selectionState = normalizedState;
            ResetReadyStatesLocked();
            SongSelectionChanged?.Invoke(this, new LobbySongSelectionChangedEventArgs(normalizedId, CloneSelectionLocked()));
            UpdateStatusLocked();
            return true;
        }
    }

    /// <summary>
    /// Starts the countdown to begin gameplay. Only valid when status is ReadyToPlay.
    /// </summary>
    public bool TryStartCountdown(int seconds = 3)
    {
        lock (_gate)
        {
            if (_status != LobbyStatus.ReadyToPlay)
            {
                return false;
            }

            if (_countdownActive)
            {
                return false;
            }

            _countdownActive = true;
            _status = LobbyStatus.InCountdown;
            StatusChanged?.Invoke(this, new LobbyStatusChangedEventArgs(LobbyStatus.ReadyToPlay, LobbyStatus.InCountdown));
            CountdownStarted?.Invoke(this, new LobbyCountdownEventArgs(seconds));
            return true;
        }
    }

    /// <summary>
    /// Cancels the countdown. Called if a player becomes unready or leaves.
    /// </summary>
    public void CancelCountdown()
    {
        lock (_gate)
        {
            CancelCountdownLocked();
        }
    }

    private void CancelCountdownLocked()
    {
        if (!_countdownActive)
        {
            return;
        }

        _countdownActive = false;
        UpdateStatusLocked();
        CountdownCancelled?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Completes the countdown and signals that gameplay should start.
    /// Returns true if the status is still InCountdown (valid to start).
    /// </summary>
    public bool CompleteCountdown()
    {
        lock (_gate)
        {
            if (_status != LobbyStatus.InCountdown)
            {
                return false;
            }

            _countdownActive = false;
            // Status stays InCountdown until reset after gameplay
            return true;
        }
    }

    /// <summary>
    /// Resets the lobby after gameplay ends.
    /// </summary>
    public void ResetAfterGameplay()
    {
        lock (_gate)
        {
            _countdownActive = false;
            _selectionState = null;
            ResetReadyStatesLocked();
            UpdateStatusLocked();
        }
    }

    public LobbyStateSnapshot BuildSnapshot()
    {
        lock (_gate)
        {
            var players = _players.Values
                .OrderBy(static p => p.Role == LobbyRole.Host ? 0 : 1)
                .ThenBy(static p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(static p => p.ToLobbyPlayer())
                .ToList();

            var selectionClone = CloneSelectionLocked();
            return new LobbyStateSnapshot(LobbyId, players, _status, selectionClone);
        }
    }

    public LobbyStatePacket BuildPacket()
    {
        var snapshot = BuildSnapshot();
        return new LobbyStatePacket(snapshot.LobbyId, snapshot.Players, snapshot.Status, snapshot.Selection);
    }

    private bool ValidateCapacityLocked(LobbyRole requestedRole)
    {
        if (requestedRole == LobbyRole.Spectator)
        {
            return _configuration.AllowSpectators;
        }

        var activePlayers = _players.Values.Count(static player => player.Role != LobbyRole.Spectator);
        return activePlayers < _configuration.MaxPlayers;
    }

    private LobbyRole AssignRoleLocked(LobbyRole requestedRole)
    {
        if (_hostSessionId is null && requestedRole != LobbyRole.Spectator)
        {
            return LobbyRole.Host;
        }

        if (requestedRole == LobbyRole.Host && _hostSessionId is not null)
        {
            return LobbyRole.Member;
        }

        return requestedRole;
    }

    private void PromoteNextHostLocked()
    {
        var nextHost = _players.Values.FirstOrDefault(static state => state.Role == LobbyRole.Member);
        if (nextHost is null)
        {
            _hostSessionId = null;
            return;
        }

        nextHost.SetRole(LobbyRole.Host);
        _hostSessionId = nextHost.SessionId;
        PlayerRoleChanged?.Invoke(this, new LobbyPlayerChangedEventArgs(nextHost.ToLobbyPlayer()));
    }

    private void ResetReadyStatesLocked()
    {
        foreach (var state in _players.Values)
        {
            if (state.Role == LobbyRole.Spectator)
            {
                continue;
            }

            if (state.IsReady)
            {
                state.SetReady(false);
                PlayerReadyStateChanged?.Invoke(this, new LobbyPlayerChangedEventArgs(state.ToLobbyPlayer()));
            }
        }
    }

    private void UpdateStatusLocked()
    {
        var newStatus = DetermineStatusLocked();
        if (newStatus == _status)
        {
            return;
        }

        var previous = _status;
        _status = newStatus;
        StatusChanged?.Invoke(this, new LobbyStatusChangedEventArgs(previous, newStatus));
    }

    private LobbyStatus DetermineStatusLocked()
    {
        // Countdown takes priority
        if (_countdownActive)
        {
            return LobbyStatus.InCountdown;
        }

        if (_selectionState is null || string.IsNullOrEmpty(_selectionState.SongId))
        {
            return LobbyStatus.Idle;
        }

        return AllRequiredPlayersReadyLocked() ? LobbyStatus.ReadyToPlay : LobbyStatus.SelectingSong;
    }

    private bool AllRequiredPlayersReadyLocked()
    {
        var eligiblePlayers = _players.Values.Where(static state => state.Role != LobbyRole.Spectator).ToList();
        if (eligiblePlayers.Count == 0)
        {
            return false;
        }

        return eligiblePlayers.All(static state => state.IsReady);
    }

    private bool ClearSelectionLocked()
    {
        if (_selectionState is null)
        {
            return false;
        }

        _selectionState = null;
        ResetReadyStatesLocked();
        SongSelectionChanged?.Invoke(this, new LobbySongSelectionChangedEventArgs(null, null));
        UpdateStatusLocked();
        return true;
    }

    private IReadOnlyList<SongInstrumentAssignment> NormalizeAssignmentsLocked(IReadOnlyList<SongInstrumentAssignment> assignments)
    {
        if (assignments is null || assignments.Count == 0)
        {
            return Array.Empty<SongInstrumentAssignment>();
        }

        var normalized = new List<SongInstrumentAssignment>(assignments.Count);
        var seenPlayers = new HashSet<Guid>();

        foreach (var assignment in assignments)
        {
            if (!_players.TryGetValue(assignment.PlayerId, out var playerState))
            {
                continue;
            }

            if (playerState.Role == LobbyRole.Spectator)
            {
                continue;
            }

            if (!seenPlayers.Add(assignment.PlayerId))
            {
                continue;
            }

            var instrument = assignment.Instrument?.Trim();
            var difficulty = assignment.Difficulty?.Trim();

            if (string.IsNullOrEmpty(instrument) || string.IsNullOrEmpty(difficulty))
            {
                continue;
            }

            normalized.Add(new SongInstrumentAssignment(assignment.PlayerId, instrument, difficulty));
        }

        if (normalized.Count == 0)
        {
            return Array.Empty<SongInstrumentAssignment>();
        }

        return normalized;
    }

    private SongSelectionState? CloneSelectionLocked()
    {
        if (_selectionState is null)
        {
            return null;
        }

        var assignments = CloneAssignments(_selectionState.Assignments);
        return new SongSelectionState(_selectionState.SongId, assignments, _selectionState.AllReady);
    }

    private static IReadOnlyList<SongInstrumentAssignment> CloneAssignments(IReadOnlyList<SongInstrumentAssignment> assignments)
    {
        if (assignments is null || assignments.Count == 0)
        {
            return Array.Empty<SongInstrumentAssignment>();
        }

        var clone = new List<SongInstrumentAssignment>(assignments.Count);
        foreach (var assignment in assignments)
        {
            clone.Add(new SongInstrumentAssignment(assignment.PlayerId, assignment.Instrument, assignment.Difficulty));
        }

        return clone;
    }

    private static bool SongSelectionEquals(SongSelectionState left, SongSelectionState right)
    {
        if (!string.Equals(left.SongId, right.SongId, StringComparison.Ordinal))
        {
            return false;
        }

        if (left.AllReady != right.AllReady)
        {
            return false;
        }

        if (left.Assignments.Count != right.Assignments.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Assignments.Count; i++)
        {
            var l = left.Assignments[i];
            var r = right.Assignments[i];

            if (l.PlayerId != r.PlayerId ||
                !string.Equals(l.Instrument, r.Instrument, StringComparison.Ordinal) ||
                !string.Equals(l.Difficulty, r.Difficulty, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public event EventHandler<LobbyPlayerChangedEventArgs>? PlayerJoined;
    public event EventHandler<LobbyPlayerChangedEventArgs>? PlayerLeft;
    public event EventHandler<LobbyPlayerChangedEventArgs>? PlayerReadyStateChanged;
    public event EventHandler<LobbyPlayerChangedEventArgs>? PlayerRoleChanged;
    public event EventHandler<LobbySongSelectionChangedEventArgs>? SongSelectionChanged;
    public event EventHandler<LobbyStatusChangedEventArgs>? StatusChanged;
    public event EventHandler<LobbyCountdownEventArgs>? CountdownStarted;
    public event EventHandler? CountdownCancelled;

    private sealed class LobbyPlayerState
    {
        public LobbyPlayerState(Guid sessionId, string displayName, LobbyRole role)
        {
            SessionId = sessionId;
            DisplayName = displayName;
            Role = role;
        }

        public Guid SessionId { get; }
        public string DisplayName { get; }
        public LobbyRole Role { get; private set; }
        public bool IsReady { get; private set; }

        public void SetReady(bool ready) => IsReady = ready;
        public void SetRole(LobbyRole role)
        {
            Role = role;
            if (role == LobbyRole.Spectator)
            {
                IsReady = false;
            }
        }

        public LobbyPlayer ToLobbyPlayer() => new(SessionId, DisplayName, Role, IsReady);
    }
}

public enum LobbyJoinError
{
    None = 0,
    SessionNotFound,
    AlreadyInLobby,
    LobbyFull,
    SpectatorsDisabled,
}

public sealed class LobbyPlayerChangedEventArgs : EventArgs
{
    public LobbyPlayerChangedEventArgs(LobbyPlayer player)
    {
        Player = player;
    }

    public LobbyPlayer Player { get; }
}

public sealed class LobbySongSelectionChangedEventArgs : EventArgs
{
    public LobbySongSelectionChangedEventArgs(string? songId, SongSelectionState? selection)
    {
        SongId = songId;
        Selection = selection;
    }

    public string? SongId { get; }
    public SongSelectionState? Selection { get; }
}

public sealed class LobbyStatusChangedEventArgs : EventArgs
{
    public LobbyStatusChangedEventArgs(LobbyStatus previous, LobbyStatus current)
    {
        Previous = previous;
        Current = current;
    }

    public LobbyStatus Previous { get; }
    public LobbyStatus Current { get; }
}

public sealed class LobbyCountdownEventArgs : EventArgs
{
    public LobbyCountdownEventArgs(int countdownSeconds)
    {
        CountdownSeconds = countdownSeconds;
    }

    public int CountdownSeconds { get; }
}
