using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using YARG.Networking.Abstraction;
using YARG.Networking.Settings;

namespace YARG.Networking.DedicatedServer
{
    /// <summary>
    /// Manages vote-to-kick sessions in dedicated server mode.
    /// Players can initiate votes to kick other players.
    /// </summary>
    public sealed class VoteToKickManager : IDisposable
    {
        /// <summary>
        /// Represents an active vote-to-kick session.
        /// </summary>
        public sealed class VoteSession
        {
            public Guid SessionId { get; set; }
            public Guid TargetPlayerId { get; set; }
            public string TargetPlayerName { get; set; }
            public Guid InitiatorPlayerId { get; set; }
            public string InitiatorPlayerName { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime ExpiresAt { get; set; }
            public HashSet<Guid> VotesFor { get; set; } = new();
            public HashSet<Guid> VotesAgainst { get; set; } = new();
            public bool IsComplete { get; set; }
            public bool WasSuccessful { get; set; }
            
            /// <summary>
            /// Gets the total number of votes cast.
            /// </summary>
            public int TotalVotes => VotesFor.Count + VotesAgainst.Count;
        }
        
        private readonly DedicatedServerConfig _config;
        private readonly Dictionary<Guid, VoteSession> _activeSessions = new();
        private readonly Dictionary<Guid, DateTime> _playerVoteCooldowns = new();
        private readonly object _lock = new();
        private bool _isDisposed;
        
        /// <summary>
        /// How long a vote session lasts before expiring (seconds).
        /// </summary>
        public const int VOTE_DURATION_SECONDS = 60;
        
        /// <summary>
        /// Cooldown before a player can initiate another vote (seconds).
        /// </summary>
        public const int VOTE_INITIATE_COOLDOWN_SECONDS = 120;
        
        /// <summary>
        /// Minimum number of players required to start a vote.
        /// </summary>
        public const int MIN_PLAYERS_FOR_VOTE = 3;
        
        /// <summary>
        /// Event fired when a new vote session starts.
        /// Parameters: session
        /// </summary>
        public event Action<VoteSession> OnVoteStarted;
        
        /// <summary>
        /// Event fired when a vote is cast.
        /// Parameters: session, voterPlayerId, votedYes
        /// </summary>
        public event Action<VoteSession, Guid, bool> OnVoteCast;
        
        /// <summary>
        /// Event fired when a vote session completes.
        /// Parameters: session, passed
        /// </summary>
        public event Action<VoteSession, bool> OnVoteCompleted;
        
        /// <summary>
        /// Event fired when a vote passes and a player should be kicked.
        /// Parameters: targetPlayerId, targetPlayerName, reason
        /// </summary>
        public event Action<Guid, string, string> OnKickRequired;
        
        /// <summary>
        /// Gets whether vote-to-kick is enabled.
        /// </summary>
        public bool IsEnabled => _config.Moderation.VoteToKickEnabled;
        
        /// <summary>
        /// Gets the vote threshold (0.5 = majority).
        /// </summary>
        public float VoteThreshold => _config.Moderation.VoteToKickThreshold;
        
        public VoteToKickManager(DedicatedServerConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }
        
        /// <summary>
        /// Attempts to start a vote-to-kick session.
        /// </summary>
        /// <param name="initiatorId">The player initiating the vote.</param>
        /// <param name="initiatorName">The initiator's display name.</param>
        /// <param name="targetId">The player to be kicked.</param>
        /// <param name="targetName">The target's display name.</param>
        /// <param name="error">Error message if vote cannot be started.</param>
        /// <returns>The vote session if started, null otherwise.</returns>
        public VoteSession StartVote(Guid initiatorId, string initiatorName, Guid targetId, string targetName, out string error)
        {
            error = null;
            
            if (!IsEnabled)
            {
                error = "Vote-to-kick is disabled on this server";
                return null;
            }
            
            if (initiatorId == targetId)
            {
                error = "You cannot vote to kick yourself";
                return null;
            }
            
            // Check player count
            var networkService = NetworkingServiceFactory.Instance;
            var allPlayers = networkService?.GetAllPlayers() ?? new List<NetworkPlayerData>();
            if (allPlayers.Count < MIN_PLAYERS_FOR_VOTE)
            {
                error = $"At least {MIN_PLAYERS_FOR_VOTE} players are required to start a vote";
                return null;
            }
            
            lock (_lock)
            {
                // Check if there's already an active vote against this target
                if (_activeSessions.Values.Any(s => !s.IsComplete && s.TargetPlayerId == targetId))
                {
                    error = $"There is already an active vote against {targetName}";
                    return null;
                }
                
                // Check initiator cooldown
                if (_playerVoteCooldowns.TryGetValue(initiatorId, out var cooldownEnd) && DateTime.UtcNow < cooldownEnd)
                {
                    var remaining = (int)(cooldownEnd - DateTime.UtcNow).TotalSeconds;
                    error = $"You must wait {remaining} seconds before starting another vote";
                    return null;
                }
                
                // Create the vote session
                var session = new VoteSession
                {
                    SessionId = Guid.NewGuid(),
                    TargetPlayerId = targetId,
                    TargetPlayerName = targetName,
                    InitiatorPlayerId = initiatorId,
                    InitiatorPlayerName = initiatorName,
                    StartTime = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddSeconds(VOTE_DURATION_SECONDS)
                };
                
                // Initiator automatically votes yes
                session.VotesFor.Add(initiatorId);
                
                _activeSessions[session.SessionId] = session;
                _playerVoteCooldowns[initiatorId] = DateTime.UtcNow.AddSeconds(VOTE_INITIATE_COOLDOWN_SECONDS);
                
                Debug.Log($"[VoteToKickManager] Vote started by {initiatorName} against {targetName}");
                
                OnVoteStarted?.Invoke(session);
                
                return session;
            }
        }
        
        /// <summary>
        /// Records a vote from a player.
        /// </summary>
        /// <param name="sessionId">The vote session ID.</param>
        /// <param name="voterId">The player casting the vote.</param>
        /// <param name="voteYes">True to vote for kicking, false to vote against.</param>
        /// <param name="error">Error message if vote cannot be recorded.</param>
        /// <returns>True if vote was recorded successfully.</returns>
        public bool CastVote(Guid sessionId, Guid voterId, bool voteYes, out string error)
        {
            error = null;
            
            lock (_lock)
            {
                if (!_activeSessions.TryGetValue(sessionId, out var session))
                {
                    error = "Vote session not found or has expired";
                    return false;
                }
                
                if (session.IsComplete)
                {
                    error = "This vote has already concluded";
                    return false;
                }
                
                if (DateTime.UtcNow >= session.ExpiresAt)
                {
                    CompleteSession(session, false);
                    error = "This vote has expired";
                    return false;
                }
                
                // Target player cannot vote
                if (voterId == session.TargetPlayerId)
                {
                    error = "The target of a vote cannot participate in that vote";
                    return false;
                }
                
                // Check if already voted
                if (session.VotesFor.Contains(voterId) || session.VotesAgainst.Contains(voterId))
                {
                    error = "You have already voted in this session";
                    return false;
                }
                
                // Record the vote
                if (voteYes)
                {
                    session.VotesFor.Add(voterId);
                }
                else
                {
                    session.VotesAgainst.Add(voterId);
                }
                
                Debug.Log($"[VoteToKickManager] Vote cast: {(voteYes ? "YES" : "NO")} ({session.VotesFor.Count} for, {session.VotesAgainst.Count} against)");
                
                OnVoteCast?.Invoke(session, voterId, voteYes);
                
                // Check if vote should conclude
                CheckVoteCompletion(session);
                
                return true;
            }
        }
        
        /// <summary>
        /// Checks if a vote session should complete and handles completion.
        /// </summary>
        private void CheckVoteCompletion(VoteSession session)
        {
            var networkService = NetworkingServiceFactory.Instance;
            var allPlayers = networkService?.GetAllPlayers() ?? new List<NetworkPlayerData>();
            
            // Eligible voters = all players except the target
            int eligibleVoters = allPlayers.Count(p => p.NetworkPlayerId != session.TargetPlayerId);
            int votesNeeded = (int)Math.Ceiling(eligibleVoters * VoteThreshold);
            
            // Check if enough votes to pass
            if (session.VotesFor.Count >= votesNeeded)
            {
                CompleteSession(session, true);
                return;
            }
            
            // Check if impossible to pass (too many against or not enough players left to vote)
            int remainingPossibleVotes = eligibleVoters - session.TotalVotes;
            if (session.VotesFor.Count + remainingPossibleVotes < votesNeeded)
            {
                CompleteSession(session, false);
                return;
            }
            
            // Check if everyone has voted
            if (session.TotalVotes >= eligibleVoters)
            {
                bool passed = session.VotesFor.Count >= votesNeeded;
                CompleteSession(session, passed);
            }
        }
        
        /// <summary>
        /// Completes a vote session.
        /// </summary>
        private void CompleteSession(VoteSession session, bool passed)
        {
            if (session.IsComplete) return;
            
            session.IsComplete = true;
            session.WasSuccessful = passed;
            
            Debug.Log($"[VoteToKickManager] Vote completed: {(passed ? "PASSED" : "FAILED")} " +
                      $"({session.VotesFor.Count} for, {session.VotesAgainst.Count} against)");
            
            OnVoteCompleted?.Invoke(session, passed);
            
            if (passed)
            {
                string reason = $"Vote-kicked by players ({session.VotesFor.Count} votes)";
                OnKickRequired?.Invoke(session.TargetPlayerId, session.TargetPlayerName, reason);
            }
        }
        
        /// <summary>
        /// Called periodically to expire stale vote sessions.
        /// </summary>
        public void Update()
        {
            if (_isDisposed) return;
            
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                var expiredSessions = _activeSessions.Values
                    .Where(s => !s.IsComplete && now >= s.ExpiresAt)
                    .ToList();
                
                foreach (var session in expiredSessions)
                {
                    Debug.Log($"[VoteToKickManager] Vote expired against {session.TargetPlayerName}");
                    CompleteSession(session, false);
                }
                
                // Clean up old completed sessions (keep for 5 minutes for history)
                var oldSessions = _activeSessions
                    .Where(kvp => kvp.Value.IsComplete && (now - kvp.Value.ExpiresAt).TotalMinutes > 5)
                    .Select(kvp => kvp.Key)
                    .ToList();
                
                foreach (var id in oldSessions)
                {
                    _activeSessions.Remove(id);
                }
            }
        }
        
        /// <summary>
        /// Gets all active (non-completed) vote sessions.
        /// </summary>
        public List<VoteSession> GetActiveSessions()
        {
            lock (_lock)
            {
                return _activeSessions.Values
                    .Where(s => !s.IsComplete && DateTime.UtcNow < s.ExpiresAt)
                    .ToList();
            }
        }
        
        /// <summary>
        /// Gets a specific vote session by ID.
        /// </summary>
        public VoteSession GetSession(Guid sessionId)
        {
            lock (_lock)
            {
                return _activeSessions.TryGetValue(sessionId, out var session) ? session : null;
            }
        }
        
        /// <summary>
        /// Gets vote history (recent completed sessions).
        /// </summary>
        public List<VoteSession> GetRecentVotes(int count = 10)
        {
            lock (_lock)
            {
                return _activeSessions.Values
                    .Where(s => s.IsComplete)
                    .OrderByDescending(s => s.StartTime)
                    .Take(count)
                    .ToList();
            }
        }
        
        /// <summary>
        /// Cancels an active vote session (admin only).
        /// </summary>
        public bool CancelVote(Guid sessionId)
        {
            lock (_lock)
            {
                if (!_activeSessions.TryGetValue(sessionId, out var session))
                    return false;
                
                if (session.IsComplete)
                    return false;
                
                session.IsComplete = true;
                session.WasSuccessful = false;
                
                Debug.Log($"[VoteToKickManager] Vote cancelled by admin against {session.TargetPlayerName}");
                OnVoteCompleted?.Invoke(session, false);
                
                return true;
            }
        }
        
        /// <summary>
        /// Called when a player leaves to clean up their vote data.
        /// </summary>
        public void OnPlayerLeft(Guid playerId)
        {
            lock (_lock)
            {
                // If the target of an active vote leaves, cancel the vote
                foreach (var session in _activeSessions.Values.Where(s => !s.IsComplete))
                {
                    if (session.TargetPlayerId == playerId)
                    {
                        session.IsComplete = true;
                        session.WasSuccessful = false;
                        Debug.Log($"[VoteToKickManager] Vote cancelled - target player left");
                        OnVoteCompleted?.Invoke(session, false);
                    }
                }
                
                // Remove from cooldowns
                _playerVoteCooldowns.Remove(playerId);
            }
        }
        
        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            
            lock (_lock)
            {
                _activeSessions.Clear();
                _playerVoteCooldowns.Clear();
            }
        }
    }
}
