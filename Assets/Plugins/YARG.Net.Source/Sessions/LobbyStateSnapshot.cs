using System;
using System.Collections.Generic;
using YARG.Net.Packets;

namespace YARG.Net.Sessions;

/// <summary>
/// Immutable view of the lobby state for broadcasting or querying.
/// </summary>
public sealed record LobbyStateSnapshot(Guid LobbyId, IReadOnlyList<LobbyPlayer> Players, LobbyStatus Status, SongSelectionState? Selection)
{
	public string? SelectedSongId => Selection?.SongId;
}
