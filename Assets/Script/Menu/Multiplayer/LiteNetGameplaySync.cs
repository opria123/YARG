using System;
using System.Collections.Generic;
using UnityEngine;
using YARG.Net.Handlers.Client;
using YARG.Net.Packets;
using YARG.Networking.NewNet;

namespace YARG.Menu.Multiplayer
{
    /// <summary>
    /// Handles LiteNet-based gameplay state replication for multiplayer sessions.
    /// Supplements or replaces MultiplayerGameplaySync when using LiteNetLib transport.
    /// </summary>
    public class LiteNetGameplaySync : MonoBehaviour
    {
        private const double MIN_CHANGED_SNAPSHOT_INTERVAL = 0.02d; // ~50 Hz when state changes
        private const double MAX_UNCHANGED_SNAPSHOT_INTERVAL = 0.20d; // 5 Hz keep-alive when idle
        private const float STAR_POWER_DELTA_EPSILON = 0.0025f;
        private const float VOCAL_PHRASE_DELTA_EPSILON = 0.25f;

        private bool _isActive;
        private uint _sequence;
        private GameplayStatePacket? _lastSentState;
        private double _lastSendTime;
        private readonly Dictionary<Guid, GameplayStatePacket> _remotePlayerStates = new();
        private readonly object _stateLock = new();

        /// <summary>
        /// Raised when a remote player's gameplay state is updated.
        /// </summary>
        public event Action<Guid, GameplayStatePacket>? OnRemoteStateUpdated;

        /// <summary>
        /// Raised when gameplay start signal is received.
        /// </summary>
        public event Action<double, double>? OnGameplayStart;

        /// <summary>
        /// Raised when time sync is received.
        /// </summary>
        public event Action<double, double>? OnTimeSync;

        /// <summary>
        /// Raised when pause state changes.
        /// </summary>
        public event Action<Guid, bool, double>? OnPauseChanged;

        /// <summary>
        /// Raised when gameplay ends.
        /// </summary>
        public event Action<GameplayEndReason>? OnGameplayEnded;

        /// <summary>
        /// Raised when server requests replay data.
        /// </summary>
        public event Action? OnReplaySyncRequested;

        /// <summary>
        /// Raised when replay sync is complete.
        /// </summary>
        public event Action? OnReplaySyncComplete;

        private void Start()
        {
            if (!ClientNetworkingService.HasInstance || !ClientNetworkingService.Instance.IsConnected)
            {
                _isActive = false;
                Destroy(this);
                return;
            }

            _isActive = true;
            _sequence = 0;
            _lastSentState = null;
            _lastSendTime = 0d;

            var gameplayHandler = ClientNetworkingService.Instance.GameplayHandler;
            if (gameplayHandler != null)
            {
                gameplayHandler.GameplayStateReceived += HandleGameplayStateReceived;
                gameplayHandler.GameplayStartReceived += HandleGameplayStartReceived;
                gameplayHandler.TimeSyncReceived += HandleTimeSyncReceived;
                gameplayHandler.PauseStateChanged += HandlePauseStateChanged;
                gameplayHandler.GameplayEnded += HandleGameplayEnded;
                gameplayHandler.ReplaySyncRequested += HandleReplaySyncRequested;
                gameplayHandler.ReplaySyncComplete += HandleReplaySyncComplete;
            }

            Debug.Log("[LiteNetGameplaySync] Initialized - using LiteNetLib for gameplay state replication");
        }

        private void OnDestroy()
        {
            if (ClientNetworkingService.HasInstance)
            {
                var gameplayHandler = ClientNetworkingService.Instance.GameplayHandler;
                if (gameplayHandler != null)
                {
                    gameplayHandler.GameplayStateReceived -= HandleGameplayStateReceived;
                    gameplayHandler.GameplayStartReceived -= HandleGameplayStartReceived;
                    gameplayHandler.TimeSyncReceived -= HandleTimeSyncReceived;
                    gameplayHandler.PauseStateChanged -= HandlePauseStateChanged;
                    gameplayHandler.GameplayEnded -= HandleGameplayEnded;
                    gameplayHandler.ReplaySyncRequested -= HandleReplaySyncRequested;
                    gameplayHandler.ReplaySyncComplete -= HandleReplaySyncComplete;
                }
            }
        }

        /// <summary>
        /// Submit the current local gameplay state for replication.
        /// </summary>
        public void SubmitLocalSnapshot(
            int score, int combo, int streak, bool starPowerActive, float starPowerAmount,
            int starPowerPhrasesHit, int totalStarPowerPhrases, int notesHit, int notesMissed, int overstrums,
            int hoposStrummed, int overhits, int ghostInputs, int ghostsHit, int accentsHit, int dynamicsBonus,
            int bandBonusScore, int vocalsTicksHit, int vocalsTicksMissed, float vocalsPhraseTicksHit,
            int vocalsPhraseTicksTotal, bool soloActive, int soloSequence, int soloNoteCount, int soloNotesHit,
            int soloLastBonus, int soloTotalBonus, double songTime, double clientNetworkTime, bool forceSend = false)
        {
            if (!_isActive || !ClientNetworkingService.HasInstance || !ClientNetworkingService.Instance.IsConnected)
            {
                return;
            }

            var sessionContext = ClientNetworkingService.Instance.SessionContext;
            if (sessionContext == null || !sessionContext.SessionId.HasValue)
            {
                return;
            }

            var now = Time.unscaledTimeAsDouble;
            var newState = new GameplayStatePacket(
                sessionContext.SessionId.Value,
                _sequence,
                Mathf.Max(0, score),
                Mathf.Max(0, combo),
                Mathf.Max(0, streak),
                starPowerActive,
                Mathf.Clamp01(starPowerAmount),
                Mathf.Max(0, starPowerPhrasesHit),
                Mathf.Max(0, totalStarPowerPhrases),
                Mathf.Max(0, notesHit),
                Mathf.Max(0, notesMissed),
                Mathf.Max(0, overstrums),
                Mathf.Max(0, hoposStrummed),
                Mathf.Max(0, overhits),
                Mathf.Max(0, ghostInputs),
                Mathf.Max(0, ghostsHit),
                Mathf.Max(0, accentsHit),
                Mathf.Max(0, dynamicsBonus),
                Mathf.Max(0, bandBonusScore),
                Mathf.Max(0, vocalsTicksHit),
                Mathf.Max(0, vocalsTicksMissed),
                Mathf.Max(0f, vocalsPhraseTicksHit),
                Mathf.Max(0, vocalsPhraseTicksTotal),
                soloActive,
                soloSequence,
                Mathf.Max(0, soloNoteCount),
                Mathf.Max(0, soloNotesHit),
                Mathf.Max(0, soloLastBonus),
                Mathf.Max(0, soloTotalBonus),
                songTime,
                clientNetworkTime);

            // Check rate limiting
            bool shouldSend = forceSend;
            if (!shouldSend)
            {
                if (_lastSentState == null)
                {
                    shouldSend = true;
                }
                else if (StateDiffers(newState, _lastSentState))
                {
                    shouldSend = (now - _lastSendTime) >= MIN_CHANGED_SNAPSHOT_INTERVAL;
                }
                else
                {
                    shouldSend = (now - _lastSendTime) >= MAX_UNCHANGED_SNAPSHOT_INTERVAL;
                }
            }

            if (!shouldSend)
            {
                return;
            }

            // Send the state
            var connection = ClientNetworkingService.Instance.ActiveConnection;
            var gameplayHandler = ClientNetworkingService.Instance.GameplayHandler;
            if (connection != null && gameplayHandler != null)
            {
                // Increment sequence for next send
                _sequence++;
                var stateToSend = newState with { Sequence = _sequence };
                
                gameplayHandler.SendGameplayState(connection, sessionContext, stateToSend);
                _lastSentState = stateToSend;
                _lastSendTime = now;
            }
        }

        /// <summary>
        /// Gets the latest state for a remote player.
        /// </summary>
        public bool TryGetRemoteState(Guid sessionId, out GameplayStatePacket? state)
        {
            lock (_stateLock)
            {
                if (_remotePlayerStates.TryGetValue(sessionId, out var cached))
                {
                    state = cached;
                    return true;
                }

                state = null;
                return false;
            }
        }

        /// <summary>
        /// Gets all remote player states.
        /// </summary>
        public IReadOnlyDictionary<Guid, GameplayStatePacket> GetAllRemoteStates()
        {
            lock (_stateLock)
            {
                return new Dictionary<Guid, GameplayStatePacket>(_remotePlayerStates);
            }
        }

        /// <summary>
        /// Sends a pause request to the server.
        /// </summary>
        public void SendPause(bool isPaused, double pauseTime)
        {
            if (!_isActive || !ClientNetworkingService.HasInstance || !ClientNetworkingService.Instance.IsConnected)
            {
                return;
            }

            var sessionContext = ClientNetworkingService.Instance.SessionContext;
            var connection = ClientNetworkingService.Instance.ActiveConnection;
            var gameplayHandler = ClientNetworkingService.Instance.GameplayHandler;

            if (sessionContext != null && sessionContext.SessionId.HasValue && connection != null && gameplayHandler != null)
            {
                gameplayHandler.SendPauseState(connection, sessionContext.SessionId.Value, isPaused, pauseTime);
            }
        }

        /// <summary>
        /// Sends replay data to the server.
        /// </summary>
        public void SendReplayData(byte[] replayFrame, byte[] replayStats, System.Guid colorProfileId, string colorProfileJson, System.Guid cameraPresetId, string cameraPresetJson, double[] frameTimes)
        {
            if (!_isActive || !ClientNetworkingService.HasInstance || !ClientNetworkingService.Instance.IsConnected)
            {
                return;
            }

            var sessionContext = ClientNetworkingService.Instance.SessionContext;
            var connection = ClientNetworkingService.Instance.ActiveConnection;
            var gameplayHandler = ClientNetworkingService.Instance.GameplayHandler;

            if (sessionContext != null && sessionContext.SessionId.HasValue && connection != null && gameplayHandler != null)
            {
                gameplayHandler.SendReplayData(connection, sessionContext.SessionId.Value, replayFrame, replayStats, colorProfileId, colorProfileJson, cameraPresetId, cameraPresetJson, frameTimes);
            }
        }

        /// <summary>
        /// Clears all cached states (call when restarting gameplay).
        /// </summary>
        public void ClearStates()
        {
            lock (_stateLock)
            {
                _remotePlayerStates.Clear();
            }
            _lastSentState = null;
            _sequence = 0;
        }

        private bool StateDiffers(GameplayStatePacket a, GameplayStatePacket b)
        {
            if (a.Score != b.Score || a.Combo != b.Combo || a.Streak != b.Streak)
                return true;

            if (a.StarPowerActive != b.StarPowerActive)
                return true;

            if (Mathf.Abs(a.StarPowerAmount - b.StarPowerAmount) > STAR_POWER_DELTA_EPSILON)
                return true;

            if (a.NotesHit != b.NotesHit || a.NotesMissed != b.NotesMissed)
                return true;

            if (a.SoloActive != b.SoloActive || a.SoloSequence != b.SoloSequence)
                return true;

            if (Mathf.Abs(a.VocalsPhraseTicksHit - b.VocalsPhraseTicksHit) > VOCAL_PHRASE_DELTA_EPSILON)
                return true;

            return false;
        }

        private void HandleGameplayStateReceived(object? sender, ClientGameplayStateReceivedEventArgs e)
        {
            // Skip our own state
            var localSession = ClientNetworkingService.Instance.SessionContext?.SessionId;
            if (localSession.HasValue && e.SessionId == localSession.Value)
            {
                return;
            }

            lock (_stateLock)
            {
                _remotePlayerStates[e.SessionId] = e.State;
            }

            // Invoke on main thread
            OnRemoteStateUpdated?.Invoke(e.SessionId, e.State);
        }

        private void HandleGameplayStartReceived(object? sender, ClientGameplayStartEventArgs e)
        {
            OnGameplayStart?.Invoke(e.ServerTime, e.SongStartTime);
        }

        private void HandleTimeSyncReceived(object? sender, ClientTimeSyncReceivedEventArgs e)
        {
            OnTimeSync?.Invoke(e.ServerTime, e.SongTime);
        }

        private void HandlePauseStateChanged(object? sender, ClientPauseChangedEventArgs e)
        {
            OnPauseChanged?.Invoke(e.SessionId, e.IsPaused, e.PauseTime);
        }

        private void HandleGameplayEnded(object? sender, ClientGameplayEndEventArgs e)
        {
            OnGameplayEnded?.Invoke(e.Reason);
        }

        private void HandleReplaySyncRequested(object? sender, ClientReplaySyncRequestEventArgs e)
        {
            OnReplaySyncRequested?.Invoke();
        }

        private void HandleReplaySyncComplete(object? sender, ClientReplaySyncCompleteEventArgs e)
        {
            OnReplaySyncComplete?.Invoke();
        }
    }
}
