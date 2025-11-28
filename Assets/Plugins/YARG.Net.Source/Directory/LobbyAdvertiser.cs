using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace YARG.Net.Directory;

/// <summary>
/// Client interface for advertising a lobby to the introducer service.
/// </summary>
public interface ILobbyAdvertiser : IDisposable
{
    /// <summary>
    /// Whether the advertiser is currently running heartbeats.
    /// </summary>
    bool IsAdvertising { get; }

    /// <summary>
    /// Starts advertising the lobby with periodic heartbeats.
    /// </summary>
    void StartAdvertising(LobbyAdvertisementRequest advertisement, TimeSpan heartbeatInterval);

    /// <summary>
    /// Stops advertising and removes the lobby from the directory.
    /// </summary>
    Task StopAdvertisingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the advertisement data (e.g., player count changed).
    /// </summary>
    void UpdateAdvertisement(LobbyAdvertisementRequest advertisement);
}

/// <summary>
/// HTTP-based client for advertising lobbies to the introducer service.
/// </summary>
public sealed class LobbyAdvertiser : ILobbyAdvertiser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _httpClient;
    private readonly Uri _advertiseUri;
    private readonly Uri _removeUri;
    private readonly object _gate = new();

    private LobbyAdvertisementRequest? _currentAdvertisement;
    private CancellationTokenSource? _heartbeatCts;
    private Task? _heartbeatTask;
    private bool _disposed;

    /// <summary>
    /// Creates a new lobby advertiser.
    /// </summary>
    /// <param name="introducerBaseUri">Base URI of the introducer service.</param>
    /// <param name="httpClient">Optional HttpClient instance.</param>
    public LobbyAdvertiser(Uri introducerBaseUri, HttpClient? httpClient = null)
    {
        if (introducerBaseUri is null)
        {
            throw new ArgumentNullException(nameof(introducerBaseUri));
        }

        _httpClient = httpClient ?? new HttpClient();
        _advertiseUri = new Uri(introducerBaseUri, "lobbies");
        _removeUri = new Uri(introducerBaseUri, "lobbies/");
    }

    /// <inheritdoc />
    public bool IsAdvertising
    {
        get
        {
            lock (_gate)
            {
                return _heartbeatCts is not null && !_heartbeatCts.IsCancellationRequested;
            }
        }
    }

    /// <inheritdoc />
    public void StartAdvertising(LobbyAdvertisementRequest advertisement, TimeSpan heartbeatInterval)
    {
        if (advertisement is null)
        {
            throw new ArgumentNullException(nameof(advertisement));
        }

        if (heartbeatInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(heartbeatInterval), "Heartbeat interval must be positive.");
        }

        lock (_gate)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(LobbyAdvertiser));
            }

            if (_heartbeatCts is not null)
            {
                throw new InvalidOperationException("Already advertising. Stop first before starting again.");
            }

            _currentAdvertisement = advertisement;
            _heartbeatCts = new CancellationTokenSource();
            var token = _heartbeatCts.Token;
            _heartbeatTask = HeartbeatLoopAsync(heartbeatInterval, token);
        }
    }

    /// <inheritdoc />
    public async Task StopAdvertisingAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? cts;
        Task? task;
        Guid? lobbyId;

        lock (_gate)
        {
            cts = _heartbeatCts;
            task = _heartbeatTask;
            lobbyId = _currentAdvertisement?.LobbyId;

            _heartbeatCts = null;
            _heartbeatTask = null;
            _currentAdvertisement = null;
        }

        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }

        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }

        // Try to remove the lobby from the directory
        if (lobbyId.HasValue)
        {
            await TryRemoveLobbyAsync(lobbyId.Value, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void UpdateAdvertisement(LobbyAdvertisementRequest advertisement)
    {
        if (advertisement is null)
        {
            throw new ArgumentNullException(nameof(advertisement));
        }

        lock (_gate)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(LobbyAdvertiser));
            }

            _currentAdvertisement = advertisement;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _heartbeatCts;
            _heartbeatCts = null;
            _heartbeatTask = null;
            _currentAdvertisement = null;
        }

        cts?.Cancel();
        cts?.Dispose();
        _httpClient.Dispose();
    }

    private async Task HeartbeatLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        // Send initial advertisement immediately
        await SendHeartbeatAsync(cancellationToken).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await SendHeartbeatAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        LobbyAdvertisementRequest? advertisement;
        lock (_gate)
        {
            advertisement = _currentAdvertisement;
        }

        if (advertisement is null)
        {
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(advertisement, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(_advertiseUri, content, cancellationToken).ConfigureAwait(false);
            // We don't require success - introducer might be down temporarily
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Swallow network errors during heartbeat
        }
    }

    private async Task TryRemoveLobbyAsync(Guid lobbyId, CancellationToken cancellationToken)
    {
        try
        {
            var deleteUri = new Uri(_removeUri, lobbyId.ToString());
            using var response = await _httpClient.DeleteAsync(deleteUri, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Best effort removal
        }
    }
}
