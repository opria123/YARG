using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace YARG.Net.Directory;

/// <summary>
/// Client interface for querying available lobbies from the introducer service.
/// </summary>
public interface ILobbyDirectoryClient : IDisposable
{
    /// <summary>
    /// Raised when the lobby list has been refreshed from the introducer.
    /// </summary>
    event EventHandler<LobbyDirectoryChangedEventArgs>? LobbiesChanged;

    /// <summary>
    /// Returns the most recently fetched list of lobbies.
    /// </summary>
    IReadOnlyList<LobbyDirectoryEntry> Lobbies { get; }

    /// <summary>
    /// Starts periodic polling of the introducer service.
    /// </summary>
    void StartPolling(TimeSpan interval);

    /// <summary>
    /// Stops periodic polling.
    /// </summary>
    void StopPolling();

    /// <summary>
    /// Performs a single refresh of the lobby list.
    /// </summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Event args containing the updated lobby list.
/// </summary>
public sealed class LobbyDirectoryChangedEventArgs : EventArgs
{
    public LobbyDirectoryChangedEventArgs(IReadOnlyList<LobbyDirectoryEntry> lobbies)
    {
        Lobbies = lobbies;
    }

    public IReadOnlyList<LobbyDirectoryEntry> Lobbies { get; }
}
