using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;
using YARG.Networking.Abstraction;
using YARG.Networking.Settings;

namespace YARG.Networking.DedicatedServer
{
    /// <summary>
    /// Embedded HTTP server for dedicated server administration.
    /// Provides a REST API and web dashboard for server management.
    /// </summary>
    public sealed class DedicatedServerWebAdmin : IDisposable
    {
        private const string SessionCookieName = "YARG_ADMIN_SESSION";
        private const int SessionTimeoutMinutes = 60;

        private readonly DedicatedServerConfig _config;
        private readonly IpBanList _banList;
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts;
        private readonly Dictionary<string, SessionInfo> _sessions = new();
        private readonly object _sessionLock = new();

        private bool _isRunning;
        private Task _listenerTask;

        /// <summary>
        /// Event fired when an admin action is performed.
        /// </summary>
        public event Action<string, string> OnAdminAction; // action, details

        public DedicatedServerWebAdmin(DedicatedServerConfig config, IpBanList banList)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _banList = banList ?? throw new ArgumentNullException(nameof(banList));
            _listener = new HttpListener();
            _cts = new CancellationTokenSource();
        }

        /// <summary>
        /// Starts the admin web server.
        /// </summary>
        public void Start()
        {
            if (_isRunning) return;

            try
            {
                var prefix = _config.Admin.AllowRemoteAccess
                    ? $"http://+:{_config.Admin.WebPort}/"
                    : $"http://localhost:{_config.Admin.WebPort}/";

                _listener.Prefixes.Clear();
                _listener.Prefixes.Add(prefix);
                _listener.Start();
                _isRunning = true;

                _listenerTask = Task.Run(() => ListenLoop(_cts.Token));

                Debug.Log($"[DedicatedServerWebAdmin] Started on {prefix}");
            }
            catch (HttpListenerException ex)
            {
                Debug.LogError($"[DedicatedServerWebAdmin] Failed to start: {ex.Message}");
                Debug.LogWarning("[DedicatedServerWebAdmin] On Windows, you may need to run as Administrator or use netsh to grant permissions.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DedicatedServerWebAdmin] Failed to start: {ex}");
            }
        }

        /// <summary>
        /// Stops the admin web server.
        /// </summary>
        public void Stop()
        {
            if (!_isRunning) return;

            try
            {
                _cts.Cancel();
                _listener.Stop();
                _listenerTask?.Wait(TimeSpan.FromSeconds(5));
                _isRunning = false;
                Debug.Log("[DedicatedServerWebAdmin] Stopped");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[DedicatedServerWebAdmin] Error stopping: {ex.Message}");
            }
        }

        public void Dispose()
        {
            Stop();
            _cts.Dispose();
            (_listener as IDisposable)?.Dispose();
        }

        private async Task ListenLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync().ConfigureAwait(false);
                    _ = Task.Run(() => HandleRequest(context), ct);
                }
                catch (HttpListenerException) when (ct.IsCancellationRequested)
                {
                    // Expected during shutdown
                }
                catch (ObjectDisposedException)
                {
                    // Listener was disposed
                    break;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[DedicatedServerWebAdmin] Error in listen loop: {ex.Message}");
                }
            }
        }

        private async Task HandleRequest(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            try
            {
                var path = request.Url?.AbsolutePath ?? "/";
                var method = request.HttpMethod;

                // CORS headers for development
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Authorization");

                if (method == "OPTIONS")
                {
                    response.StatusCode = 204;
                    response.Close();
                    return;
                }

                // Route requests
                if (path == "/" || path == "/index.html")
                {
                    await ServeAdminDashboard(response);
                }
                else if (path == "/api/auth/login" && method == "POST")
                {
                    await HandleLogin(request, response);
                }
                else if (path == "/api/auth/logout" && method == "POST")
                {
                    await HandleLogout(request, response);
                }
                else if (path.StartsWith("/api/"))
                {
                    // All other API endpoints require authentication
                    if (!IsAuthenticated(request, out var session))
                    {
                        await SendJsonResponse(response, new { error = "Unauthorized" }, 401);
                        return;
                    }

                    // Refresh session on activity
                    RefreshSession(session);

                    await RouteApiRequest(path, method, request, response, session);
                }
                else
                {
                    response.StatusCode = 404;
                    await WriteResponse(response, "Not Found");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DedicatedServerWebAdmin] Request error: {ex}");
                try
                {
                    response.StatusCode = 500;
                    await WriteResponse(response, $"Internal Server Error: {ex.Message}");
                }
                catch { }
            }
            finally
            {
                try { response.Close(); } catch { }
            }
        }

        private async Task RouteApiRequest(string path, string method, HttpListenerRequest request, HttpListenerResponse response, SessionInfo session)
        {
            switch (path)
            {
                case "/api/status":
                    if (method == "GET") await HandleGetStatus(response);
                    else response.StatusCode = 405;
                    break;

                case "/api/players":
                    if (method == "GET") await HandleGetPlayers(response);
                    else response.StatusCode = 405;
                    break;

                case "/api/settings":
                    if (method == "GET") await HandleGetSettings(response);
                    else if (method == "PUT") await HandleUpdateSettings(request, response, session);
                    else response.StatusCode = 405;
                    break;

                case "/api/bans":
                    if (method == "GET") await HandleGetBans(response);
                    else response.StatusCode = 405;
                    break;

                case "/api/server/shutdown":
                    if (method == "POST") await HandleShutdown(response, session);
                    else response.StatusCode = 405;
                    break;

                case "/api/timeouts":
                    if (method == "GET") await HandleGetTimeouts(response);
                    else response.StatusCode = 405;
                    break;

                case "/api/timeouts/readyup/start":
                    if (method == "POST") await HandleStartReadyUpPhase(response, session);
                    else response.StatusCode = 405;
                    break;

                case "/api/timeouts/readyup/cancel":
                    if (method == "POST") await HandleCancelReadyUpPhase(response, session);
                    else response.StatusCode = 405;
                    break;

                case "/api/votes":
                    if (method == "GET") await HandleGetVotes(response);
                    else response.StatusCode = 405;
                    break;

                default:
                    // Check for parameterized routes like /api/players/{id}/kick
                    if (path.StartsWith("/api/players/") && method == "POST")
                    {
                        await HandlePlayerAction(path, request, response, session);
                    }
                    else if (path.StartsWith("/api/bans/") && method == "DELETE")
                    {
                        await HandleRemoveBan(path, response, session);
                    }
                    else if (path.StartsWith("/api/votes/") && method == "POST")
                    {
                        await HandleVoteAction(path, response, session);
                    }
                    else
                    {
                        response.StatusCode = 404;
                        await SendJsonResponse(response, new { error = "Endpoint not found" }, 404);
                    }
                    break;
            }
        }

        #region Authentication

        private async Task HandleLogin(HttpListenerRequest request, HttpListenerResponse response)
        {
            var body = await ReadRequestBody(request);
            var login = JsonConvert.DeserializeAnonymousType(body, new { username = "", password = "" });

            if (login == null ||
                !string.Equals(login.username, _config.Admin.Username, StringComparison.OrdinalIgnoreCase) ||
                login.password != _config.Admin.Password)
            {
                await SendJsonResponse(response, new { error = "Invalid credentials" }, 401);
                Debug.Log($"[DedicatedServerWebAdmin] Failed login attempt from {request.RemoteEndPoint}");
                return;
            }

            var sessionToken = GenerateSessionToken();
            var sessionInfo = new SessionInfo
            {
                Token = sessionToken,
                Username = login.username,
                CreatedAt = DateTime.UtcNow,
                LastActivity = DateTime.UtcNow,
                RemoteAddress = request.RemoteEndPoint?.Address.ToString() ?? "unknown"
            };

            lock (_sessionLock)
            {
                _sessions[sessionToken] = sessionInfo;
                CleanupExpiredSessions();
            }

            // Set session cookie
            response.SetCookie(new Cookie(SessionCookieName, sessionToken, "/")
            {
                HttpOnly = true,
                Expires = DateTime.Now.AddMinutes(SessionTimeoutMinutes)
            });

            Debug.Log($"[DedicatedServerWebAdmin] Admin login from {request.RemoteEndPoint}");
            OnAdminAction?.Invoke("Login", $"Admin logged in from {request.RemoteEndPoint}");

            await SendJsonResponse(response, new { success = true, token = sessionToken });
        }

        private async Task HandleLogout(HttpListenerRequest request, HttpListenerResponse response)
        {
            var token = GetSessionToken(request);
            if (!string.IsNullOrEmpty(token))
            {
                lock (_sessionLock)
                {
                    _sessions.Remove(token);
                }
            }

            response.SetCookie(new Cookie(SessionCookieName, "", "/") { Expires = DateTime.Now.AddDays(-1) });
            await SendJsonResponse(response, new { success = true });
        }

        private bool IsAuthenticated(HttpListenerRequest request, out SessionInfo session)
        {
            session = null;
            var token = GetSessionToken(request);
            if (string.IsNullOrEmpty(token)) return false;

            lock (_sessionLock)
            {
                if (!_sessions.TryGetValue(token, out session)) return false;
                if (DateTime.UtcNow - session.LastActivity > TimeSpan.FromMinutes(SessionTimeoutMinutes))
                {
                    _sessions.Remove(token);
                    return false;
                }
                return true;
            }
        }

        private void RefreshSession(SessionInfo session)
        {
            session.LastActivity = DateTime.UtcNow;
        }

        private string GetSessionToken(HttpListenerRequest request)
        {
            // Check cookie first
            var cookie = request.Cookies[SessionCookieName];
            if (cookie != null && !string.IsNullOrEmpty(cookie.Value))
                return cookie.Value;

            // Check Authorization header (Bearer token)
            var auth = request.Headers["Authorization"];
            if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return auth.Substring(7);

            return null;
        }

        private static string GenerateSessionToken()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
        }

        private void CleanupExpiredSessions()
        {
            var expired = _sessions.Where(kvp =>
                DateTime.UtcNow - kvp.Value.LastActivity > TimeSpan.FromMinutes(SessionTimeoutMinutes))
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expired)
                _sessions.Remove(key);
        }

        #endregion

        #region API Handlers

        private async Task HandleGetStatus(HttpListenerResponse response)
        {
            var networkService = NetworkingServiceFactory.Instance;
            var lobby = networkService?.CurrentLobby;
            var allPlayers = networkService?.GetAllPlayers() ?? new List<NetworkPlayerData>();

            var status = new
            {
                serverName = _config.Server.SessionName,
                isRunning = networkService?.IsHosting == true,
                playerCount = allPlayers.Count,
                maxPlayers = _config.Server.MaxPlayers,
                currentState = GetServerState(),
                uptime = DedicatedServerBootstrap.IsRunning ? (DateTime.UtcNow - GetStartTime()).TotalSeconds : 0,
                lobbyId = lobby?.LobbyId ?? "",
                privacy = _config.Server.PrivacyMode,
                bandSize = _config.Gameplay.BandSize
            };

            await SendJsonResponse(response, status);
        }

        private async Task HandleGetPlayers(HttpListenerResponse response)
        {
            var networkService = NetworkingServiceFactory.Instance;
            var allPlayers = networkService?.GetAllPlayers() ?? new List<NetworkPlayerData>();
            
            // Get timeout status if available
            var timeoutStatuses = DedicatedServerManager.Instance?.TimeoutTracker?.GetAllPlayerStatus() 
                ?? new List<PlayerTimeoutStatus>();

            var players = allPlayers.Select(p => 
            {
                var timeoutStatus = timeoutStatuses.FirstOrDefault(ts => ts.PlayerId == p.NetworkPlayerId);
                return new
                {
                    id = p.NetworkPlayerId.ToString(),
                    connectionId = p.ConnectionId.ToString(),
                    name = p.PlayerName,
                    isHost = p.IsHost,
                    isReady = p.IsReady,
                    instrument = p.Instrument,
                    difficulty = p.Difficulty,
                    ping = p.Ping,
                    sittingOut = p.SittingOut,
                    score = p.CurrentScore,
                    combo = p.CurrentCombo,
                    stars = p.Stars,
                    hasFailed = p.HasFailed,
                    // Timeout tracking fields
                    idleSeconds = timeoutStatus?.IdleSeconds ?? 0,
                    idleTimeoutSeconds = timeoutStatus?.IdleTimeoutSeconds ?? 0,
                    readyUpSecondsRemaining = timeoutStatus?.ReadyUpSecondsRemaining,
                    readyUpTimeoutSeconds = timeoutStatus?.ReadyUpTimeoutSeconds ?? 0
                };
            }).ToList();

            await SendJsonResponse(response, new { players });
        }

        private async Task HandleGetSettings(HttpListenerResponse response)
        {
            var settings = new
            {
                server = new
                {
                    sessionName = _config.Server.SessionName,
                    port = _config.Server.Port,
                    maxPlayers = _config.Server.MaxPlayers,
                    privacyMode = _config.Server.PrivacyMode,
                    visibleOnLan = _config.Server.VisibleOnLan,
                    registerWithIntroducers = _config.Server.RegisterWithIntroducers
                },
                gameplay = new
                {
                    bandSize = _config.Gameplay.BandSize,
                    noFailMode = _config.Gameplay.NoFailMode,
                    sharedSongsOnly = _config.Gameplay.SharedSongsOnly,
                    allowModifiers = _config.Gameplay.AllowModifiers,
                    enablePresetSync = _config.Gameplay.EnablePresetSync,
                    allowLateJoin = _config.Gameplay.AllowLateJoin
                },
                timeouts = new
                {
                    idleMinutes = _config.Timeouts.IdleMinutes,
                    readyUpMinutes = _config.Timeouts.ReadyUpMinutes
                },
                moderation = new
                {
                    voteToKickEnabled = _config.Moderation.VoteToKickEnabled,
                    voteToKickThreshold = _config.Moderation.VoteToKickThreshold
                }
            };

            await SendJsonResponse(response, settings);
        }

        private async Task HandleUpdateSettings(HttpListenerRequest request, HttpListenerResponse response, SessionInfo session)
        {
            var body = await ReadRequestBody(request);

            try
            {
                // Parse partial settings update
                var updates = JsonConvert.DeserializeObject<Dictionary<string, object>>(body);
                if (updates == null)
                {
                    await SendJsonResponse(response, new { error = "Invalid request body" }, 400);
                    return;
                }

                // Apply updates to config (limited set of runtime-changeable settings)
                bool changed = false;

                if (updates.TryGetValue("gameplay", out var gameplayObj) && gameplayObj is Newtonsoft.Json.Linq.JObject gameplay)
                {
                    if (gameplay.TryGetValue("noFailMode", out var nf))
                    {
                        _config.Gameplay.NoFailMode = nf.ToObject<bool>();
                        changed = true;
                    }
                    if (gameplay.TryGetValue("allowModifiers", out var am))
                    {
                        _config.Gameplay.AllowModifiers = am.ToObject<bool>();
                        changed = true;
                    }
                    if (gameplay.TryGetValue("allowLateJoin", out var alj))
                    {
                        _config.Gameplay.AllowLateJoin = alj.ToObject<bool>();
                        changed = true;
                    }
                    if (gameplay.TryGetValue("bandSize", out var bs))
                    {
                        _config.Gameplay.BandSize = Mathf.Clamp(bs.ToObject<int>(), 0, 8);
                        changed = true;
                    }
                }

                if (changed)
                {
                    // Save config to disk
                    _config.Save();

                    // NOTE: Settings changes via web admin are applied on next session start.
                    // Live broadcasting would require additional INetworkingService methods.
                    OnAdminAction?.Invoke("SettingsChanged", $"Settings updated by {session.Username}");
                    Debug.Log($"[DedicatedServerWebAdmin] Settings updated by {session.Username}");
                }

                await SendJsonResponse(response, new { success = true });
            }
            catch (Exception ex)
            {
                await SendJsonResponse(response, new { error = ex.Message }, 400);
            }
        }

        private async Task HandlePlayerAction(string path, HttpListenerRequest request, HttpListenerResponse response, SessionInfo session)
        {
            // Parse /api/players/{id}/{action}
            var parts = path.Split('/');
            if (parts.Length < 5)
            {
                await SendJsonResponse(response, new { error = "Invalid path" }, 400);
                return;
            }

            var playerIdStr = parts[3];
            var action = parts[4];

            if (!Guid.TryParse(playerIdStr, out var playerId))
            {
                await SendJsonResponse(response, new { error = "Invalid player ID" }, 400);
                return;
            }

            var networkService = NetworkingServiceFactory.Instance;
            var allPlayers = networkService?.GetAllPlayers() ?? new List<NetworkPlayerData>();
            var player = allPlayers.FirstOrDefault(p => p.NetworkPlayerId == playerId);

            if (player == null)
            {
                await SendJsonResponse(response, new { error = "Player not found" }, 404);
                return;
            }

            switch (action.ToLowerInvariant())
            {
                case "kick":
                    await HandleKickPlayer(player, request, response, session);
                    break;

                case "ban":
                    await HandleBanPlayer(player, request, response, session);
                    break;

                case "promote":
                    await HandlePromotePlayer(player, response, session);
                    break;

                default:
                    await SendJsonResponse(response, new { error = "Unknown action" }, 400);
                    break;
            }
        }

        private async Task HandleKickPlayer(NetworkPlayerData player, HttpListenerRequest request, HttpListenerResponse response, SessionInfo session)
        {
            var body = await ReadRequestBody(request);
            var data = JsonConvert.DeserializeAnonymousType(body, new { reason = "Kicked by admin" });
            var reason = data?.reason ?? "Kicked by admin";

            var networkService = NetworkingServiceFactory.Instance;
            networkService?.KickPlayer(player);

            OnAdminAction?.Invoke("KickPlayer", $"{session.Username} kicked {player.PlayerName}: {reason}");
            Debug.Log($"[DedicatedServerWebAdmin] {session.Username} kicked {player.PlayerName}: {reason}");

            await SendJsonResponse(response, new { success = true });
        }

        private async Task HandleBanPlayer(NetworkPlayerData player, HttpListenerRequest request, HttpListenerResponse response, SessionInfo session)
        {
            var body = await ReadRequestBody(request);
            var data = JsonConvert.DeserializeAnonymousType(body, new { reason = "Banned by admin", durationMinutes = 0 });
            var reason = data?.reason ?? "Banned by admin";
            var duration = data?.durationMinutes ?? 0;

            // Get IP from connection - we need to find it through the server stack
            var ipAddress = GetPlayerIpAddress(player);
            if (string.IsNullOrEmpty(ipAddress))
            {
                await SendJsonResponse(response, new { error = "Could not determine player IP address" }, 400);
                return;
            }

            // Add to ban list
            _banList.AddBan(ipAddress, player.PlayerName, reason, session.Username, duration);

            // Kick the player
            var networkService = NetworkingServiceFactory.Instance;
            networkService?.KickPlayer(player);

            OnAdminAction?.Invoke("BanPlayer", $"{session.Username} banned {player.PlayerName} ({ipAddress}): {reason}");
            Debug.Log($"[DedicatedServerWebAdmin] {session.Username} banned {player.PlayerName} ({ipAddress}): {reason}");

            await SendJsonResponse(response, new { success = true, ip = ipAddress });
        }

        private async Task HandlePromotePlayer(NetworkPlayerData player, HttpListenerResponse response, SessionInfo session)
        {
            var manager = DedicatedServerManager.Instance;
            if (manager == null)
            {
                await SendJsonResponse(response, new { error = "Dedicated server manager not available" }, 500);
                return;
            }
            
            bool success = manager.PromoteToHost(player.NetworkPlayerId);
            
            if (success)
            {
                OnAdminAction?.Invoke("PromotePlayer", $"{session.Username} promoted {player.PlayerName} to host");
                Debug.Log($"[DedicatedServerWebAdmin] {session.Username} promoted {player.PlayerName} to host");
                await SendJsonResponse(response, new { success = true });
            }
            else
            {
                await SendJsonResponse(response, new { error = "Failed to promote player to host" }, 400);
            }
        }

        private async Task HandleGetBans(HttpListenerResponse response)
        {
            var bans = _banList.BannedIps.Select(b => new
            {
                ip = b.IpAddress,
                lastKnownNames = b.LastKnownNames,
                reason = b.Reason,
                bannedAt = b.BannedAt,
                bannedBy = b.BannedBy,
                expiresAt = b.ExpiresAt,
                isExpired = b.IsExpired
            }).ToList();

            await SendJsonResponse(response, new { bans });
        }

        private async Task HandleRemoveBan(string path, HttpListenerResponse response, SessionInfo session)
        {
            // Parse /api/bans/{ip}
            var parts = path.Split('/');
            if (parts.Length < 4)
            {
                await SendJsonResponse(response, new { error = "Invalid path" }, 400);
                return;
            }

            var ip = Uri.UnescapeDataString(parts[3]);
            var removed = _banList.RemoveBan(ip);

            if (removed)
            {
                OnAdminAction?.Invoke("RemoveBan", $"{session.Username} unbanned IP {ip}");
                Debug.Log($"[DedicatedServerWebAdmin] {session.Username} unbanned IP {ip}");
                await SendJsonResponse(response, new { success = true });
            }
            else
            {
                await SendJsonResponse(response, new { error = "IP not found in ban list" }, 404);
            }
        }

        private async Task HandleShutdown(HttpListenerResponse response, SessionInfo session)
        {
            OnAdminAction?.Invoke("Shutdown", $"Server shutdown initiated by {session.Username}");
            Debug.Log($"[DedicatedServerWebAdmin] Server shutdown initiated by {session.Username}");

            await SendJsonResponse(response, new { success = true, message = "Server shutting down..." });

            // Schedule shutdown after response is sent
            _ = Task.Run(async () =>
            {
                await Task.Delay(1000);
                Application.Quit();
            });
        }

        private async Task HandleGetTimeouts(HttpListenerResponse response)
        {
            var tracker = DedicatedServerManager.Instance?.TimeoutTracker;
            var playerStatuses = tracker?.GetAllPlayerStatus() ?? new List<PlayerTimeoutStatus>();
            
            var timeoutInfo = new
            {
                config = new
                {
                    idleTimeoutMinutes = _config.Timeouts.IdleMinutes,
                    readyUpTimeoutMinutes = _config.Timeouts.ReadyUpMinutes,
                    idleTimeoutEnabled = _config.Timeouts.IdleMinutes > 0,
                    readyUpTimeoutEnabled = _config.Timeouts.ReadyUpMinutes > 0
                },
                readyUpPhaseActive = tracker?.IsReadyUpPhaseActive ?? false,
                players = playerStatuses.Select(p => new
                {
                    playerId = p.PlayerId.ToString(),
                    playerName = p.PlayerName,
                    idleSeconds = p.IdleSeconds,
                    idleTimeoutSeconds = p.IdleTimeoutSeconds,
                    isReady = p.IsReady,
                    readyUpSecondsRemaining = p.ReadyUpSecondsRemaining,
                    readyUpTimeoutSeconds = p.ReadyUpTimeoutSeconds,
                    idlePercentage = p.IdleTimeoutSeconds > 0 ? Math.Min(100, (int)(p.IdleSeconds * 100.0 / p.IdleTimeoutSeconds)) : 0,
                    readyUpPercentage = p.ReadyUpSecondsRemaining.HasValue && p.ReadyUpTimeoutSeconds > 0
                        ? Math.Min(100, (int)((p.ReadyUpTimeoutSeconds - p.ReadyUpSecondsRemaining.Value) * 100.0 / p.ReadyUpTimeoutSeconds))
                        : 0
                }).ToList()
            };
            
            await SendJsonResponse(response, timeoutInfo);
        }

        private async Task HandleStartReadyUpPhase(HttpListenerResponse response, SessionInfo session)
        {
            var manager = DedicatedServerManager.Instance;
            if (manager == null)
            {
                await SendJsonResponse(response, new { error = "Dedicated server manager not available" }, 500);
                return;
            }
            
            if (_config.Timeouts.ReadyUpMinutes <= 0)
            {
                await SendJsonResponse(response, new { error = "Ready-up timeout is disabled in config (set to 0)" }, 400);
                return;
            }
            
            manager.StartReadyUpPhase();
            
            OnAdminAction?.Invoke("StartReadyUp", $"Ready-up phase started by {session.Username}");
            Debug.Log($"[DedicatedServerWebAdmin] Ready-up phase started by admin {session.Username}");
            
            await SendJsonResponse(response, new 
            { 
                success = true, 
                message = $"Ready-up phase started. Players have {_config.Timeouts.ReadyUpMinutes} minutes to ready up." 
            });
        }

        private async Task HandleCancelReadyUpPhase(HttpListenerResponse response, SessionInfo session)
        {
            var manager = DedicatedServerManager.Instance;
            if (manager == null)
            {
                await SendJsonResponse(response, new { error = "Dedicated server manager not available" }, 500);
                return;
            }
            
            manager.EndReadyUpPhase();
            
            OnAdminAction?.Invoke("CancelReadyUp", $"Ready-up phase cancelled by {session.Username}");
            Debug.Log($"[DedicatedServerWebAdmin] Ready-up phase cancelled by admin {session.Username}");
            
            await SendJsonResponse(response, new 
            { 
                success = true, 
                message = "Ready-up phase cancelled." 
            });
        }

        private async Task HandleGetVotes(HttpListenerResponse response)
        {
            var manager = DedicatedServerManager.Instance;
            var voteManager = manager?.VoteToKick;
            
            var activeSessions = voteManager?.GetActiveSessions() ?? new List<VoteToKickManager.VoteSession>();
            var recentVotes = voteManager?.GetRecentVotes(10) ?? new List<VoteToKickManager.VoteSession>();
            
            var voteInfo = new
            {
                enabled = voteManager?.IsEnabled ?? false,
                threshold = voteManager?.VoteThreshold ?? 0.5f,
                voteDurationSeconds = VoteToKickManager.VOTE_DURATION_SECONDS,
                minPlayersRequired = VoteToKickManager.MIN_PLAYERS_FOR_VOTE,
                activeSessions = activeSessions.Select(s => new
                {
                    sessionId = s.SessionId.ToString(),
                    targetPlayerId = s.TargetPlayerId.ToString(),
                    targetPlayerName = s.TargetPlayerName,
                    initiatorPlayerName = s.InitiatorPlayerName,
                    startTime = new DateTimeOffset(s.StartTime).ToUnixTimeSeconds(),
                    expiresAt = new DateTimeOffset(s.ExpiresAt).ToUnixTimeSeconds(),
                    votesFor = s.VotesFor.Count,
                    votesAgainst = s.VotesAgainst.Count,
                    secondsRemaining = Math.Max(0, (int)(s.ExpiresAt - DateTime.UtcNow).TotalSeconds)
                }).ToList(),
                recentVotes = recentVotes.Select(s => new
                {
                    targetPlayerName = s.TargetPlayerName,
                    initiatorPlayerName = s.InitiatorPlayerName,
                    votesFor = s.VotesFor.Count,
                    votesAgainst = s.VotesAgainst.Count,
                    wasSuccessful = s.WasSuccessful,
                    startTime = new DateTimeOffset(s.StartTime).ToUnixTimeSeconds()
                }).ToList()
            };
            
            await SendJsonResponse(response, voteInfo);
        }

        private async Task HandleVoteAction(string path, HttpListenerResponse response, SessionInfo session)
        {
            // Parse /api/votes/{sessionId}/cancel
            var segments = path.Split('/');
            if (segments.Length < 5)
            {
                await SendJsonResponse(response, new { error = "Invalid vote action path" }, 400);
                return;
            }
            
            var sessionIdStr = segments[3];
            var action = segments[4];
            
            if (!Guid.TryParse(sessionIdStr, out var voteSessionId))
            {
                await SendJsonResponse(response, new { error = "Invalid session ID" }, 400);
                return;
            }
            
            var manager = DedicatedServerManager.Instance;
            var voteManager = manager?.VoteToKick;
            
            if (voteManager == null)
            {
                await SendJsonResponse(response, new { error = "Vote manager not available" }, 500);
                return;
            }
            
            switch (action.ToLowerInvariant())
            {
                case "cancel":
                    if (voteManager.CancelVote(voteSessionId))
                    {
                        OnAdminAction?.Invoke("CancelVote", $"Vote cancelled by {session.Username}");
                        await SendJsonResponse(response, new { success = true, message = "Vote cancelled" });
                    }
                    else
                    {
                        await SendJsonResponse(response, new { error = "Vote not found or already completed" }, 404);
                    }
                    break;
                    
                default:
                    await SendJsonResponse(response, new { error = "Unknown vote action" }, 400);
                    break;
            }
        }

        #endregion

        #region Dashboard

        private async Task ServeAdminDashboard(HttpListenerResponse response)
        {
            response.ContentType = "text/html; charset=utf-8";
            await WriteResponse(response, GetDashboardHtml());
        }

        private string GetDashboardHtml()
        {
            return @"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>YARG Server Admin</title>
    <style>
        :root {
            --bg-primary: #1a1a2e;
            --bg-secondary: #16213e;
            --bg-card: #0f3460;
            --accent: #e94560;
            --accent-hover: #ff6b6b;
            --text-primary: #eee;
            --text-secondary: #aaa;
            --success: #4ade80;
            --warning: #fbbf24;
            --danger: #ef4444;
        }
        * { box-sizing: border-box; margin: 0; padding: 0; }
        body {
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
            background: var(--bg-primary);
            color: var(--text-primary);
            min-height: 100vh;
        }
        .login-container {
            display: flex;
            justify-content: center;
            align-items: center;
            min-height: 100vh;
            padding: 20px;
        }
        .login-box {
            background: var(--bg-secondary);
            padding: 40px;
            border-radius: 12px;
            box-shadow: 0 4px 20px rgba(0,0,0,0.3);
            width: 100%;
            max-width: 400px;
        }
        .login-box h1 {
            text-align: center;
            margin-bottom: 30px;
            color: var(--accent);
        }
        .form-group {
            margin-bottom: 20px;
        }
        .form-group label {
            display: block;
            margin-bottom: 8px;
            color: var(--text-secondary);
        }
        .form-group input {
            width: 100%;
            padding: 12px;
            border: 2px solid var(--bg-card);
            border-radius: 8px;
            background: var(--bg-primary);
            color: var(--text-primary);
            font-size: 16px;
        }
        .form-group input:focus {
            outline: none;
            border-color: var(--accent);
        }
        .btn {
            padding: 12px 24px;
            border: none;
            border-radius: 8px;
            cursor: pointer;
            font-size: 16px;
            font-weight: 600;
            transition: all 0.2s;
        }
        .btn-primary {
            background: var(--accent);
            color: white;
            width: 100%;
        }
        .btn-primary:hover { background: var(--accent-hover); }
        .btn-danger { background: var(--danger); color: white; }
        .btn-danger:hover { background: #dc2626; }
        .btn-warning { background: var(--warning); color: #000; }
        .btn-success { background: var(--success); color: #000; }
        .btn-small { padding: 6px 12px; font-size: 14px; }
        .error-msg {
            color: var(--danger);
            text-align: center;
            margin-top: 15px;
        }
        /* Dashboard styles */
        .dashboard { display: none; }
        .dashboard.active { display: block; }
        .header {
            background: var(--bg-secondary);
            padding: 15px 30px;
            display: flex;
            justify-content: space-between;
            align-items: center;
            border-bottom: 2px solid var(--bg-card);
        }
        .header h1 { color: var(--accent); font-size: 24px; }
        .content { padding: 30px; max-width: 1400px; margin: 0 auto; }
        .stats-grid {
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
            gap: 20px;
            margin-bottom: 30px;
        }
        .stat-card {
            background: var(--bg-card);
            padding: 20px;
            border-radius: 12px;
            text-align: center;
        }
        .stat-card .value {
            font-size: 36px;
            font-weight: bold;
            color: var(--accent);
        }
        .stat-card .label {
            color: var(--text-secondary);
            margin-top: 5px;
        }
        .card {
            background: var(--bg-secondary);
            border-radius: 12px;
            padding: 20px;
            margin-bottom: 20px;
        }
        .card h2 {
            margin-bottom: 15px;
            color: var(--accent);
            border-bottom: 1px solid var(--bg-card);
            padding-bottom: 10px;
        }
        .tabs {
            display: flex;
            gap: 10px;
            margin-bottom: 20px;
        }
        .tab {
            padding: 10px 20px;
            background: var(--bg-card);
            border: none;
            border-radius: 8px;
            color: var(--text-primary);
            cursor: pointer;
            font-size: 14px;
        }
        .tab.active { background: var(--accent); }
        .tab-content { display: none; }
        .tab-content.active { display: block; }
        table {
            width: 100%;
            border-collapse: collapse;
        }
        th, td {
            padding: 12px;
            text-align: left;
            border-bottom: 1px solid var(--bg-card);
        }
        th { color: var(--text-secondary); font-weight: 600; }
        .status-badge {
            display: inline-block;
            padding: 4px 10px;
            border-radius: 20px;
            font-size: 12px;
            font-weight: 600;
        }
        .status-ready { background: var(--success); color: #000; }
        .status-not-ready { background: var(--warning); color: #000; }
        .status-host { background: var(--accent); color: #fff; }
        .actions { display: flex; gap: 8px; }
        .hidden { display: none !important; }
        .loading { opacity: 0.6; pointer-events: none; }
    </style>
</head>
<body>
    <!-- Login Screen -->
    <div id=""loginScreen"" class=""login-container"">
        <div class=""login-box"">
            <h1>🎸 YARG Server Admin</h1>
            <form id=""loginForm"">
                <div class=""form-group"">
                    <label for=""username"">Username</label>
                    <input type=""text"" id=""username"" required autocomplete=""username"">
                </div>
                <div class=""form-group"">
                    <label for=""password"">Password</label>
                    <input type=""password"" id=""password"" required autocomplete=""current-password"">
                </div>
                <button type=""submit"" class=""btn btn-primary"">Login</button>
                <p id=""loginError"" class=""error-msg hidden""></p>
            </form>
        </div>
    </div>

    <!-- Dashboard -->
    <div id=""dashboard"" class=""dashboard"">
        <header class=""header"">
            <h1>🎸 YARG Server Admin</h1>
            <div>
                <span id=""serverName""></span>
                <button class=""btn btn-danger btn-small"" onclick=""logout()"" style=""margin-left: 15px;"">Logout</button>
            </div>
        </header>
        <div class=""content"">
            <div class=""stats-grid"">
                <div class=""stat-card"">
                    <div class=""value"" id=""playerCount"">0</div>
                    <div class=""label"">Players Online</div>
                </div>
                <div class=""stat-card"">
                    <div class=""value"" id=""serverState"">-</div>
                    <div class=""label"">Server State</div>
                </div>
                <div class=""stat-card"">
                    <div class=""value"" id=""uptime"">0m</div>
                    <div class=""label"">Uptime</div>
                </div>
                <div class=""stat-card"">
                    <div class=""value"" id=""banCount"">0</div>
                    <div class=""label"">Banned IPs</div>
                </div>
            </div>

            <div class=""tabs"">
                <button class=""tab active"" onclick=""showTab('players')"">Players</button>
                <button class=""tab"" onclick=""showTab('votes')"">Votes</button>
                <button class=""tab"" onclick=""showTab('settings')"">Settings</button>
                <button class=""tab"" onclick=""showTab('bans')"">Ban List</button>
            </div>

            <div id=""playersTab"" class=""tab-content active"">
                <div class=""card"">
                    <h2>Connected Players</h2>
                    <table>
                        <thead>
                            <tr>
                                <th>Name</th>
                                <th>Status</th>
                                <th>Instrument</th>
                                <th>Score</th>
                                <th>Ping</th>
                                <th>Timeout</th>
                                <th>Actions</th>
                            </tr>
                        </thead>
                        <tbody id=""playersTable""></tbody>
                    </table>
                    <div style=""margin-top:16px;"">
                        <button class=""btn btn-warning"" onclick=""startReadyUp()"" style=""width:auto;"">Start Ready-Up Timer</button>
                        <button class=""btn btn-secondary"" onclick=""cancelReadyUp()"" style=""width:auto;margin-left:8px;"">Cancel Ready-Up</button>
                    </div>
                </div>
            </div>

            <div id=""votesTab"" class=""tab-content"">
                <div class=""card"">
                    <h2>Vote-to-Kick</h2>
                    <div id=""voteStatus"" style=""margin-bottom:16px;padding:10px;background:var(--bg-secondary);border-radius:8px;"">
                        <span id=""voteEnabled""></span> | Threshold: <span id=""voteThreshold""></span>
                    </div>
                    <h3>Active Votes</h3>
                    <div id=""activeVotes"" style=""margin-bottom:24px;""></div>
                    <h3>Recent Vote History</h3>
                    <table>
                        <thead>
                            <tr>
                                <th>Target</th>
                                <th>Initiated By</th>
                                <th>Votes</th>
                                <th>Result</th>
                            </tr>
                        </thead>
                        <tbody id=""voteHistory""></tbody>
                    </table>
                </div>
            </div>

            <div id=""settingsTab"" class=""tab-content"">
                <div class=""card"">
                    <h2>Gameplay Settings</h2>
                    <form id=""settingsForm"">
                        <div class=""form-group"">
                            <label><input type=""checkbox"" id=""noFailMode""> No Fail Mode</label>
                        </div>
                        <div class=""form-group"">
                            <label><input type=""checkbox"" id=""allowModifiers""> Allow Modifiers</label>
                        </div>
                        <div class=""form-group"">
                            <label><input type=""checkbox"" id=""allowLateJoin""> Allow Late Join</label>
                        </div>
                        <div class=""form-group"">
                            <label>Band Size (0 = disabled)</label>
                            <input type=""number"" id=""bandSize"" min=""0"" max=""8"" style=""width: 100px;"">
                        </div>
                        <button type=""submit"" class=""btn btn-primary"" style=""width: auto;"">Save Settings</button>
                    </form>
                </div>
            </div>

            <div id=""bansTab"" class=""tab-content"">
                <div class=""card"">
                    <h2>Banned IPs</h2>
                    <table>
                        <thead>
                            <tr>
                                <th>IP Address</th>
                                <th>Last Known Names</th>
                                <th>Reason</th>
                                <th>Banned By</th>
                                <th>Expires</th>
                                <th>Actions</th>
                            </tr>
                        </thead>
                        <tbody id=""bansTable""></tbody>
                    </table>
                </div>
            </div>
        </div>
    </div>

    <script>
        let authToken = localStorage.getItem('authToken');
        const INSTRUMENTS = ['None', '5-Fret Guitar', '6-Fret Guitar', '4-Fret Guitar', 'Pro Guitar', 'Drums', 'Pro Drums', 'Vocals', 'Keys', 'Pro Keys'];

        async function api(endpoint, method = 'GET', body = null) {
            const opts = {
                method,
                headers: { 'Content-Type': 'application/json' }
            };
            if (authToken) opts.headers['Authorization'] = 'Bearer ' + authToken;
            if (body) opts.body = JSON.stringify(body);
            const res = await fetch('/api/' + endpoint, opts);
            if (res.status === 401) {
                logout();
                throw new Error('Unauthorized');
            }
            return res.json();
        }

        document.getElementById('loginForm').addEventListener('submit', async (e) => {
            e.preventDefault();
            const errEl = document.getElementById('loginError');
            errEl.classList.add('hidden');
            try {
                const res = await api('auth/login', 'POST', {
                    username: document.getElementById('username').value,
                    password: document.getElementById('password').value
                });
                if (res.token) {
                    authToken = res.token;
                    localStorage.setItem('authToken', authToken);
                    showDashboard();
                } else {
                    errEl.textContent = res.error || 'Login failed';
                    errEl.classList.remove('hidden');
                }
            } catch (err) {
                errEl.textContent = 'Connection error';
                errEl.classList.remove('hidden');
            }
        });

        function logout() {
            api('auth/logout', 'POST').catch(() => {});
            localStorage.removeItem('authToken');
            authToken = null;
            document.getElementById('loginScreen').style.display = 'flex';
            document.getElementById('dashboard').classList.remove('active');
        }

        function showDashboard() {
            document.getElementById('loginScreen').style.display = 'none';
            document.getElementById('dashboard').classList.add('active');
            refreshAll();
            setInterval(refreshAll, 5000);
        }

        async function refreshAll() {
            try {
                const [status, players, bans, votes] = await Promise.all([
                    api('status'),
                    api('players'),
                    api('bans'),
                    api('votes')
                ]);
                updateStatus(status);
                updatePlayers(players.players);
                updateBans(bans.bans);
                updateVotes(votes);
            } catch (err) {
                console.error('Refresh error:', err);
            }
        }

        function updateStatus(status) {
            document.getElementById('serverName').textContent = status.serverName;
            document.getElementById('playerCount').textContent = status.playerCount + '/' + status.maxPlayers;
            document.getElementById('serverState').textContent = status.currentState;
            document.getElementById('uptime').textContent = formatUptime(status.uptime);
        }

        function updatePlayers(players) {
            const tbody = document.getElementById('playersTable');
            if (!players.length) {
                tbody.innerHTML = '<tr><td colspan=""7"" style=""text-align:center;color:var(--text-secondary)"">No players connected</td></tr>';
                return;
            }
            tbody.innerHTML = players.map(p => {
                let timeoutBar = '';
                if (p.idleTimeoutSeconds > 0) {
                    const idlePct = Math.min(100, Math.round(p.idleSeconds * 100 / p.idleTimeoutSeconds));
                    const idleColor = idlePct > 75 ? 'var(--danger)' : idlePct > 50 ? 'var(--warning)' : 'var(--success)';
                    timeoutBar = `<div style=""width:60px;height:6px;background:#333;border-radius:3px;margin-top:4px"">
                        <div style=""width:${idlePct}%;height:100%;background:${idleColor};border-radius:3px""></div>
                    </div><small style=""color:var(--text-secondary)"">Idle: ${Math.floor(p.idleSeconds/60)}m</small>`;
                }
                if (p.readyUpSecondsRemaining !== null && !p.isReady) {
                    const remainMin = Math.floor(p.readyUpSecondsRemaining / 60);
                    const remainSec = p.readyUpSecondsRemaining % 60;
                    timeoutBar += `<div style=""color:var(--warning);font-size:11px"">⏱️ ${remainMin}:${remainSec.toString().padStart(2, '0')}</div>`;
                }
                return `
                <tr>
                    <td>${escapeHtml(p.name)} ${p.isHost ? '<span class=""status-badge status-host"">Host</span>' : ''}</td>
                    <td><span class=""status-badge ${p.isReady ? 'status-ready' : 'status-not-ready'}"">${p.isReady ? 'Ready' : 'Not Ready'}</span></td>
                    <td>${INSTRUMENTS[p.instrument + 1] || '-'}</td>
                    <td>${p.score.toLocaleString()}</td>
                    <td>${Math.round(p.ping)}ms</td>
                    <td>${timeoutBar || '-'}</td>
                    <td class=""actions"">
                        <button class=""btn btn-warning btn-small"" onclick=""kickPlayer('${p.id}', '${escapeHtml(p.name)}')"">Kick</button>
                        <button class=""btn btn-danger btn-small"" onclick=""banPlayer('${p.id}', '${escapeHtml(p.name)}')"">Ban</button>
                        ${!p.isHost ? `<button class=""btn btn-success btn-small"" onclick=""promotePlayer('${p.id}', '${escapeHtml(p.name)}')"">Promote</button>` : ''}
                    </td>
                </tr>
            `}).join('');
        }

        function updateBans(bans) {
            document.getElementById('banCount').textContent = bans.filter(b => !b.isExpired).length;
            const tbody = document.getElementById('bansTable');
            if (!bans.length) {
                tbody.innerHTML = '<tr><td colspan=""6"" style=""text-align:center;color:var(--text-secondary)"">No banned IPs</td></tr>';
                return;
            }
            tbody.innerHTML = bans.map(b => `
                <tr ${b.isExpired ? 'style=""opacity:0.5""' : ''}>
                    <td>${escapeHtml(b.ip)}</td>
                    <td>${b.lastKnownNames.map(escapeHtml).join(', ')}</td>
                    <td>${escapeHtml(b.reason)}</td>
                    <td>${escapeHtml(b.bannedBy)}</td>
                    <td>${b.expiresAt ? new Date(b.expiresAt * 1000).toLocaleString() : 'Never'}</td>
                    <td><button class=""btn btn-success btn-small"" onclick=""unban('${encodeURIComponent(b.ip)}')"">Unban</button></td>
                </tr>
            `).join('');
        }

        function updateVotes(votes) {
            // Update status
            document.getElementById('voteEnabled').innerHTML = votes.enabled 
                ? '<span style=""color:var(--success)"">✓ Enabled</span>' 
                : '<span style=""color:var(--danger)"">✗ Disabled</span>';
            document.getElementById('voteThreshold').textContent = Math.round(votes.threshold * 100) + '%';
            
            // Active votes
            const activeDiv = document.getElementById('activeVotes');
            if (!votes.activeSessions.length) {
                activeDiv.innerHTML = '<p style=""color:var(--text-secondary)"">No active votes</p>';
            } else {
                activeDiv.innerHTML = votes.activeSessions.map(v => `
                    <div style=""padding:12px;background:var(--bg-card);border-radius:8px;margin-bottom:8px;"">
                        <div style=""display:flex;justify-content:space-between;align-items:center;"">
                            <div>
                                <strong>Vote to kick: ${escapeHtml(v.targetPlayerName)}</strong><br>
                                <small style=""color:var(--text-secondary)"">Started by ${escapeHtml(v.initiatorPlayerName)}</small>
                            </div>
                            <div style=""text-align:right;"">
                                <span style=""color:var(--success)"">${v.votesFor} yes</span> / 
                                <span style=""color:var(--danger)"">${v.votesAgainst} no</span><br>
                                <small style=""color:var(--warning)"">${v.secondsRemaining}s remaining</small>
                            </div>
                            <button class=""btn btn-danger btn-small"" onclick=""cancelVote('${v.sessionId}')"">Cancel</button>
                        </div>
                    </div>
                `).join('');
            }
            
            // Vote history
            const historyTbody = document.getElementById('voteHistory');
            if (!votes.recentVotes.length) {
                historyTbody.innerHTML = '<tr><td colspan=""4"" style=""text-align:center;color:var(--text-secondary)"">No recent votes</td></tr>';
            } else {
                historyTbody.innerHTML = votes.recentVotes.map(v => `
                    <tr>
                        <td>${escapeHtml(v.targetPlayerName)}</td>
                        <td>${escapeHtml(v.initiatorPlayerName)}</td>
                        <td>${v.votesFor} yes / ${v.votesAgainst} no</td>
                        <td><span class=""status-badge ${v.wasSuccessful ? 'status-ready' : 'status-not-ready'}"">${v.wasSuccessful ? 'Passed' : 'Failed'}</span></td>
                    </tr>
                `).join('');
            }
        }

        async function cancelVote(sessionId) {
            if (!confirm('Cancel this vote?')) return;
            try {
                await api(`votes/${sessionId}/cancel`, 'POST');
                refreshAll();
            } catch (err) {
                alert('Failed to cancel vote');
            }
        }

        async function kickPlayer(id, name) {
            if (!confirm(`Kick ${name}?`)) return;
            const reason = prompt('Reason (optional):') || 'Kicked by admin';
            await api(`players/${id}/kick`, 'POST', { reason });
            refreshAll();
        }

        async function banPlayer(id, name) {
            if (!confirm(`Ban ${name}? This will also kick them.`)) return;
            const reason = prompt('Reason:') || 'Banned by admin';
            const duration = parseInt(prompt('Duration in minutes (0 = permanent):', '0')) || 0;
            await api(`players/${id}/ban`, 'POST', { reason, durationMinutes: duration });
            refreshAll();
        }

        async function promotePlayer(id, name) {
            if (!confirm(`Make ${name} the new host?`)) return;
            await api(`players/${id}/promote`, 'POST');
            refreshAll();
        }

        async function startReadyUp() {
            if (!confirm('Start the ready-up timer? Players who do not ready up in time will be kicked.')) return;
            try {
                const res = await api('timeouts/readyup/start', 'POST');
                alert(res.message || 'Ready-up phase started!');
                refreshAll();
            } catch (err) {
                alert('Failed to start ready-up phase');
            }
        }

        async function cancelReadyUp() {
            if (!confirm('Cancel the ready-up timer?')) return;
            try {
                const res = await api('timeouts/readyup/cancel', 'POST');
                alert(res.message || 'Ready-up phase cancelled');
                refreshAll();
            } catch (err) {
                alert('Failed to cancel ready-up phase');
            }
        }

        async function unban(ip) {
            if (!confirm(`Unban ${decodeURIComponent(ip)}?`)) return;
            await api(`bans/${ip}`, 'DELETE');
            refreshAll();
        }

        document.getElementById('settingsForm').addEventListener('submit', async (e) => {
            e.preventDefault();
            await api('settings', 'PUT', {
                gameplay: {
                    noFailMode: document.getElementById('noFailMode').checked,
                    allowModifiers: document.getElementById('allowModifiers').checked,
                    allowLateJoin: document.getElementById('allowLateJoin').checked,
                    bandSize: parseInt(document.getElementById('bandSize').value) || 0
                }
            });
            alert('Settings saved!');
        });

        async function loadSettings() {
            const settings = await api('settings');
            document.getElementById('noFailMode').checked = settings.gameplay.noFailMode;
            document.getElementById('allowModifiers').checked = settings.gameplay.allowModifiers;
            document.getElementById('allowLateJoin').checked = settings.gameplay.allowLateJoin;
            document.getElementById('bandSize').value = settings.gameplay.bandSize;
        }

        function showTab(name) {
            document.querySelectorAll('.tab').forEach(t => t.classList.remove('active'));
            document.querySelectorAll('.tab-content').forEach(t => t.classList.remove('active'));
            document.querySelectorAll('.tab').forEach(t => {
                if (t.getAttribute('onclick').includes(name)) t.classList.add('active');
            });
            document.getElementById(name + 'Tab').classList.add('active');
            if (name === 'settings') loadSettings();
        }

        function formatUptime(seconds) {
            const h = Math.floor(seconds / 3600);
            const m = Math.floor((seconds % 3600) / 60);
            return h > 0 ? `${h}h ${m}m` : `${m}m`;
        }

        function escapeHtml(str) {
            const div = document.createElement('div');
            div.textContent = str;
            return div.innerHTML;
        }

        // Check if already logged in
        if (authToken) {
            api('status').then(() => showDashboard()).catch(() => {
                localStorage.removeItem('authToken');
                authToken = null;
            });
        }
    </script>
</body>
</html>";
        }

        #endregion

        #region Helpers

        private string GetServerState()
        {
            // Determine server state from networking service and game manager
            var networkService = NetworkingServiceFactory.Instance;
            if (networkService == null || !networkService.IsHosting) return "Offline";

            // Check if in gameplay
            var gameManager = UnityEngine.Object.FindObjectOfType<YARG.Gameplay.GameManager>();
            if (gameManager != null) return "Playing";

            return "Lobby";
        }

        private DateTime GetStartTime()
        {
            // Get start time from bootstrap
            return DedicatedServerBootstrap.StartTime;
        }

        private string GetPlayerIpAddress(NetworkPlayerData player)
        {
            // Try to get IP from connection endpoint
            // This requires access to the server stack's connection manager
            try
            {
                var adapter = NetworkingServiceFactory.Instance as LiteNetNetworkingAdapter;
                if (adapter != null)
                {
                    // The adapter should expose a way to get connection endpoint
                    // For now, we'll need to add this method to the adapter
                    return adapter.GetPlayerEndpoint(player.ConnectionId);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[DedicatedServerWebAdmin] Failed to get player IP: {ex.Message}");
            }

            return null;
        }

        private static async Task<string> ReadRequestBody(HttpListenerRequest request)
        {
            using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
            return await reader.ReadToEndAsync();
        }

        private static async Task SendJsonResponse(HttpListenerResponse response, object data, int statusCode = 200)
        {
            response.StatusCode = statusCode;
            response.ContentType = "application/json; charset=utf-8";
            var json = JsonConvert.SerializeObject(data);
            await WriteResponse(response, json);
        }

        private static async Task WriteResponse(HttpListenerResponse response, string content)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        }

        #endregion

        #region Session Info

        private sealed class SessionInfo
        {
            public string Token { get; set; }
            public string Username { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime LastActivity { get; set; }
            public string RemoteAddress { get; set; }
        }

        #endregion
    }
}
