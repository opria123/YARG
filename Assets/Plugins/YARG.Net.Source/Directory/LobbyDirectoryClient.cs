using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace YARG.Net.Directory;

/// <summary>
/// HTTP-based client for polling the lobby directory/introducer service.
/// </summary>
public sealed class LobbyDirectoryClient : ILobbyDirectoryClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly Uri _directoryUri;
    private readonly TimeSpan _lobbyTtl;
    private readonly object _gate = new();

    private List<LobbyDirectoryEntry> _lobbies = new();
    private CancellationTokenSource? _pollingCts;
    private Task? _pollingTask;
    private bool _disposed;

    /// <inheritdoc />
    public event EventHandler<LobbyDirectoryChangedEventArgs>? LobbiesChanged;

    /// <summary>
    /// Creates a new directory client pointing at the given introducer endpoint.
    /// </summary>
    /// <param name="directoryUri">Base URI of the introducer's lobby list endpoint.</param>
    /// <param name="lobbyTtl">How long a lobby should be considered active after its last heartbeat.</param>
    /// <param name="httpClient">Optional HttpClient instance (for testing or reuse).</param>
    public LobbyDirectoryClient(Uri directoryUri, TimeSpan? lobbyTtl = null, HttpClient? httpClient = null)
    {
        _directoryUri = directoryUri ?? throw new ArgumentNullException(nameof(directoryUri));
        _lobbyTtl = lobbyTtl ?? TimeSpan.FromSeconds(30);
        _httpClient = httpClient ?? new HttpClient();
    }

    /// <inheritdoc />
    public IReadOnlyList<LobbyDirectoryEntry> Lobbies
    {
        get
        {
            lock (_gate)
            {
                return _lobbies.AsReadOnly();
            }
        }
    }

    /// <inheritdoc />
    public void StartPolling(TimeSpan interval)
    {
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(LobbyDirectoryClient));
            if (_pollingCts is not null) return; // Already polling

            _pollingCts = new CancellationTokenSource();
            var token = _pollingCts.Token;
            _pollingTask = PollLoopAsync(interval, token);
        }
    }

    /// <inheritdoc />
    public void StopPolling()
    {
        CancellationTokenSource? cts;
        Task? task;

        lock (_gate)
        {
            cts = _pollingCts;
            task = _pollingTask;
            _pollingCts = null;
            _pollingTask = null;
        }

        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }

        // Don't await the task; let it wind down naturally
    }

    /// <inheritdoc />
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LobbyDirectoryClient));

        try
        {
            var response = await _httpClient.GetAsync(_directoryUri, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var entries = JsonSerializer.Deserialize<List<LobbyDirectoryEntry>>(json, JsonOptions) ?? new List<LobbyDirectoryEntry>();

            // Filter to active lobbies only
            var activeLobbies = new List<LobbyDirectoryEntry>();
            foreach (var entry in entries)
            {
                if (entry.IsActive(_lobbyTtl))
                {
                    activeLobbies.Add(entry);
                }
            }

            bool changed;
            lock (_gate)
            {
                changed = !ListsEqual(_lobbies, activeLobbies);
                if (changed)
                {
                    _lobbies = activeLobbies;
                }
            }

            if (changed)
            {
                LobbiesChanged?.Invoke(this, new LobbyDirectoryChangedEventArgs(activeLobbies.AsReadOnly()));
            }
        }
        catch (OperationCanceledException)
        {
            // Polling was cancelled, that's expected
            throw;
        }
        catch (Exception)
        {
            // Log or handle HTTP/parsing errors as appropriate
            // For now, swallow and let polling continue
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopPolling();
        _httpClient.Dispose();
    }

    private async Task PollLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Swallow individual poll failures
            }

            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static bool ListsEqual(List<LobbyDirectoryEntry> left, List<LobbyDirectoryEntry> right)
    {
        if (left.Count != right.Count) return false;

        for (int i = 0; i < left.Count; i++)
        {
            if (!EntriesEqual(left[i], right[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool EntriesEqual(LobbyDirectoryEntry a, LobbyDirectoryEntry b)
    {
        return a.LobbyId == b.LobbyId
            && a.LobbyName == b.LobbyName
            && a.HostName == b.HostName
            && a.Address == b.Address
            && a.Port == b.Port
            && a.CurrentPlayers == b.CurrentPlayers
            && a.MaxPlayers == b.MaxPlayers
            && a.HasPassword == b.HasPassword
            && a.Version == b.Version
            && a.LastHeartbeatUtc == b.LastHeartbeatUtc;
    }
}
