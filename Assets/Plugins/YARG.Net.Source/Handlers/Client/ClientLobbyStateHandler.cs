using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using YARG.Net.Packets;
using YARG.Net.Packets.Dispatch;
using YARG.Net.Sessions;
using YARG.Net.Transport;

namespace YARG.Net.Handlers.Client;

/// <summary>
/// Caches lobby state snapshots received on the client via <see cref="PacketType.LobbyState"/> packets.
/// </summary>
public sealed class ClientLobbyStateHandler
{
    private readonly object _gate = new();
    private LobbyStateSnapshot? _latestSnapshot;

    /// <summary>
    /// Raised when the cached lobby snapshot changes.
    /// </summary>
    public event EventHandler<ClientLobbyStateChangedEventArgs>? LobbyStateChanged;

    /// <summary>
    /// Registers this handler with the provided dispatcher so it can receive lobby state packets.
    /// </summary>
    public void Register(IPacketDispatcher dispatcher)
    {
        if (dispatcher is null)
        {
            throw new ArgumentNullException(nameof(dispatcher));
        }

        dispatcher.RegisterHandler<LobbyStatePacket>(PacketType.LobbyState, HandleAsync);
    }

    /// <summary>
    /// Attempts to retrieve the latest cached lobby snapshot.
    /// </summary>
    public bool TryGetSnapshot([NotNullWhen(true)] out LobbyStateSnapshot? snapshot)
    {
        lock (_gate)
        {
            snapshot = _latestSnapshot;
            return snapshot is not null;
        }
    }

    /// <summary>
    /// Handles a lobby state packet dispatched via <see cref="IPacketDispatcher"/>.
    /// </summary>
    public Task HandleAsync(PacketContext context, PacketEnvelope<LobbyStatePacket> envelope, CancellationToken cancellationToken)
    {
        if (context.Role != PacketEndpointRole.Client)
        {
            return Task.CompletedTask;
        }

        var snapshot = CreateSnapshot(envelope.Payload);
        bool shouldRaise;

        lock (_gate)
        {
            if (_latestSnapshot is not null && SnapshotsEqual(_latestSnapshot, snapshot))
            {
                shouldRaise = false;
            }
            else
            {
                _latestSnapshot = snapshot;
                shouldRaise = true;
            }
        }

        if (shouldRaise)
        {
            LobbyStateChanged?.Invoke(this, new ClientLobbyStateChangedEventArgs(snapshot));
        }

        return Task.CompletedTask;
    }

    private static LobbyStateSnapshot CreateSnapshot(LobbyStatePacket packet)
    {
        var players = packet.Players.Count == 0
            ? (IReadOnlyList<LobbyPlayer>)Array.Empty<LobbyPlayer>()
            : packet.Players.ToList();

        var selection = CloneSelection(packet.Selection);
        return new LobbyStateSnapshot(packet.LobbyId, players, packet.Status, selection);
    }

    private static bool SnapshotsEqual(LobbyStateSnapshot left, LobbyStateSnapshot right)
    {
        if (left.LobbyId != right.LobbyId)
        {
            return false;
        }

        if (left.Status != right.Status)
        {
            return false;
        }

        if (!SelectionsEqual(left.Selection, right.Selection))
        {
            return false;
        }

        return PlayersEqual(left.Players, right.Players);
    }

    private static SongSelectionState? CloneSelection(SongSelectionState? selection)
    {
        if (selection is null)
        {
            return null;
        }

        var assignments = selection.Assignments.Count == 0
            ? (IReadOnlyList<SongInstrumentAssignment>)Array.Empty<SongInstrumentAssignment>()
            : selection.Assignments
                .Select(static assignment => new SongInstrumentAssignment(assignment.PlayerId, assignment.Instrument, assignment.Difficulty))
                .ToList();

        return new SongSelectionState(selection.SongId, assignments, selection.AllReady);
    }

    private static bool SelectionsEqual(SongSelectionState? left, SongSelectionState? right)
    {
        if (left is null && right is null)
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        if (!string.Equals(left.SongId, right.SongId, StringComparison.Ordinal))
        {
            return false;
        }

        if (left.AllReady != right.AllReady)
        {
            return false;
        }

        return AssignmentsEqual(left.Assignments, right.Assignments);
    }

    private static bool AssignmentsEqual(IReadOnlyList<SongInstrumentAssignment> left, IReadOnlyList<SongInstrumentAssignment> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            var currentLeft = left[i];
            var currentRight = right[i];

            if (currentLeft.PlayerId != currentRight.PlayerId ||
                !string.Equals(currentLeft.Instrument, currentRight.Instrument, StringComparison.Ordinal) ||
                !string.Equals(currentLeft.Difficulty, currentRight.Difficulty, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool PlayersEqual(IReadOnlyList<LobbyPlayer> left, IReadOnlyList<LobbyPlayer> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            var leftPlayer = left[i];
            var rightPlayer = right[i];

            if (leftPlayer.PlayerId != rightPlayer.PlayerId ||
                !string.Equals(leftPlayer.DisplayName, rightPlayer.DisplayName, StringComparison.Ordinal) ||
                leftPlayer.Role != rightPlayer.Role ||
                leftPlayer.IsReady != rightPlayer.IsReady)
            {
                return false;
            }
        }

        return true;
    }
}

public sealed class ClientLobbyStateChangedEventArgs : EventArgs
{
    public ClientLobbyStateChangedEventArgs(LobbyStateSnapshot snapshot)
    {
        Snapshot = snapshot;
    }

    public LobbyStateSnapshot Snapshot { get; }
}
