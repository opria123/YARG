using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;
using YARG.Core;
using YARG.Core.Game;
using YARG.Core.Replays;
using YARG.Core.Song;
using YARG.Menu.Multiplayer;
using YARG.Networking.NewNet;

namespace YARG.Replays
{
    /// <summary>
    /// Manages replay data synchronization in multiplayer sessions.
    /// Collects replay frames from all clients and merges them into a complete replay.
    /// </summary>
    public class MultiplayerReplaySync : MonoBehaviour
    {
        private static MultiplayerReplaySync _instance;
        public static MultiplayerReplaySync Instance => _instance;

        private bool _isSyncing;
        private Dictionary<Guid, ClientReplayData> _collectedData;
        private ReplayInfo _originalReplayInfo;
        private ReplayInfo _mergedReplayInfo;
        private string _replayDirectory;

        public bool IsSyncing => _isSyncing;
        public ReplayInfo MergedReplayInfo => _mergedReplayInfo;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);

            // Subscribe to server gameplay handler events
            if (ServerNetworkingService.HasInstance && ServerNetworkingService.Instance.GameplayHandler != null)
            {
                ServerNetworkingService.Instance.GameplayHandler.ReplaySyncDataReceived += OnServerReplaySyncDataReceived;
                Debug.Log("[MultiplayerReplaySync] Subscribed to server replay sync events");
            }
        }

        private void OnDestroy()
        {
            // Unsubscribe from server gameplay handler events
            if (ServerNetworkingService.HasInstance && ServerNetworkingService.Instance.GameplayHandler != null)
            {
                ServerNetworkingService.Instance.GameplayHandler.ReplaySyncDataReceived -= OnServerReplaySyncDataReceived;
            }

            if (_instance == this)
            {
                _instance = null;
            }
        }

        private void OnServerReplaySyncDataReceived(object sender, YARG.Net.Handlers.Server.ReplaySyncDataReceivedEventArgs e)
        {
            OnReplayDataReceived(e.SessionId, e.SerializedFrame, e.SerializedStats, e.ColorProfileId, e.ColorProfileJson, e.CameraPresetId, e.CameraPresetJson, e.FrameTimes);
        }

        /// <summary>
        /// Starts the replay sync process as the host.
        /// </summary>
        public void StartReplaySync(ReplayInfo originalReplay, string replayDirectory)
        {
            if (_isSyncing)
            {
                Debug.LogWarning("[MultiplayerReplaySync] Already syncing replay data");
                return;
            }

            _isSyncing = true;
            _collectedData = new Dictionary<Guid, ClientReplayData>();
            _originalReplayInfo = originalReplay;
            _mergedReplayInfo = null; // Clear previous merged replay
            _replayDirectory = replayDirectory;

            Debug.Log("[MultiplayerReplaySync] Starting replay sync - requesting data from all clients");

            // Request replay data from all clients
            if (ServerNetworkingService.HasInstance && ServerNetworkingService.Instance.GameplayHandler != null)
            {
                ServerNetworkingService.Instance.GameplayHandler.RequestReplayData();
            }
        }

        /// <summary>
        /// Called when a client's replay data is received (host only).
        /// </summary>
        public void OnReplayDataReceived(Guid sessionId, byte[] replayFrame, byte[] replayStats, Guid colorProfileId, string colorProfileJson, Guid cameraPresetId, string cameraPresetJson, double[] frameTimes)
        {
            if (!_isSyncing)
            {
                Debug.LogWarning($"[MultiplayerReplaySync] Received replay data but not syncing - ignoring");
                return;
            }

            Debug.Log($"[MultiplayerReplaySync] Received replay data from session {sessionId}");

            _collectedData[sessionId] = new ClientReplayData
            {
                SessionId = sessionId,
                SerializedFrame = replayFrame,
                SerializedStats = replayStats,
                ColorProfileId = colorProfileId,
                ColorProfileJson = colorProfileJson,
                CameraPresetId = cameraPresetId,
                CameraPresetJson = cameraPresetJson,
                FrameTimes = frameTimes
            };
        }

        /// <summary>
        /// Called when all clients have sent their replay data (host only).
        /// </summary>
        public async UniTask<bool> MergeAndSaveReplay(SongEntry songEntry, float speed, double length, int score, StarAmount stars)
        {
            if (!_isSyncing)
            {
                Debug.LogError("[MultiplayerReplaySync] Cannot merge replay - not syncing");
                return false;
            }

            if (_collectedData.Count == 0)
            {
                Debug.LogWarning("[MultiplayerReplaySync] No replay data collected");
                _isSyncing = false;
                return false;
            }

            Debug.Log($"[MultiplayerReplaySync] Merging replay data from {_collectedData.Count} clients");

            try
            {
                // Deserialize all replay frames and stats
                var frames = new List<ReplayFrame>();
                var replayStatsList = new List<ReplayStats>();
                var colorProfiles = new Dictionary<Guid, ColorProfile>();
                var cameraPresets = new Dictionary<Guid, CameraPreset>();
                var allFrameTimes = new List<double>();

                foreach (var (sessionId, clientData) in _collectedData)
                {
                    // Deserialize replay frame
                    using var frameMemStream = new MemoryStream(clientData.SerializedFrame);
                    using var frameArray = YARG.Core.IO.FixedArray.Read(frameMemStream, clientData.SerializedFrame.Length);
                    var frameStream = new YARG.Core.IO.FixedArrayStream(frameArray);
                    var frame = new ReplayFrame(ref frameStream, _originalReplayInfo.ReplayVersion);
                    frames.Add(frame);

                    // Deserialize replay stats - use GameMode from the frame's profile
                    using var statsMemStream = new MemoryStream(clientData.SerializedStats);
                    using var statsArray = YARG.Core.IO.FixedArray.Read(statsMemStream, clientData.SerializedStats.Length);
                    var statsStream = new YARG.Core.IO.FixedArrayStream(statsArray);
                    
                    // Use GameMode from ReplayFrame.Profile to determine which ReplayStats subclass to construct
                    var gameMode = frame.Profile.GameMode;
                    ReplayStats replayStats = gameMode switch
                    {
                        GameMode.FiveFretGuitar or
                        GameMode.SixFretGuitar => new Core.Replays.GuitarReplayStats(ref statsStream, _originalReplayInfo.ReplayVersion),
                        GameMode.FourLaneDrums or
                        GameMode.FiveLaneDrums or
                        GameMode.EliteDrums => new Core.Replays.DrumsReplayStats(ref statsStream, _originalReplayInfo.ReplayVersion),
                        GameMode.ProKeys => new Core.Replays.ProKeysReplayStats(ref statsStream, _originalReplayInfo.ReplayVersion),
                        GameMode.Vocals => new Core.Replays.VocalsReplayStats(ref statsStream, _originalReplayInfo.ReplayVersion),
                        _ => throw new Exception($"Stats for {gameMode} not supported")
                    };
                    replayStatsList.Add(replayStats);

                    // Deserialize color profile
                    if (!colorProfiles.ContainsKey(clientData.ColorProfileId))
                    {
                        var colorProfile = JsonConvert.DeserializeObject<ColorProfile>(clientData.ColorProfileJson);
                        if (colorProfile != null)
                        {
                            colorProfiles[clientData.ColorProfileId] = colorProfile;
                        }
                    }

                    // Deserialize camera preset
                    if (!cameraPresets.ContainsKey(clientData.CameraPresetId))
                    {
                        var cameraPreset = JsonConvert.DeserializeObject<CameraPreset>(clientData.CameraPresetJson);
                        if (cameraPreset != null)
                        {
                            cameraPresets[clientData.CameraPresetId] = cameraPreset;
                        }
                    }

                    // Collect frame times
                    if (clientData.FrameTimes != null && clientData.FrameTimes.Length > 0)
                    {
                        allFrameTimes.AddRange(clientData.FrameTimes);
                    }

                    Debug.Log($"[MultiplayerReplaySync] Deserialized frame for session {sessionId}: {frame.Profile.Name} ({frame.Profile.GameMode})");
                }

                // Create merged replay data
                var mergedFrameTimes = allFrameTimes.Count > 0 ? allFrameTimes.ToArray() : Array.Empty<double>();
                var replayData = new ReplayData(colorProfiles, cameraPresets, frames.ToArray(), mergedFrameTimes);

                // Save the merged replay
                var (success, mergedInfo) = ReplayIO.TrySerialize(_replayDirectory, songEntry, speed, length, score, stars, replayStatsList.ToArray(), replayData);

                if (success)
                {
                    Debug.Log($"[MultiplayerReplaySync] Successfully saved merged replay: {mergedInfo.FilePath}");

                    // Store the merged replay info for clients to use
                    _mergedReplayInfo = mergedInfo;

                    // Notify all clients that sync is complete
                    if (ServerNetworkingService.HasInstance && ServerNetworkingService.Instance.GameplayHandler != null)
                    {
                        ServerNetworkingService.Instance.GameplayHandler.BroadcastReplaySyncComplete();
                    }

                    _isSyncing = false;
                    return true;
                }
                else
                {
                    Debug.LogError("[MultiplayerReplaySync] Failed to save merged replay");
                    _isSyncing = false;
                    return false;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MultiplayerReplaySync] Error merging replay data: {ex.Message}\n{ex.StackTrace}");
                _isSyncing = false;
                return false;
            }
        }

        /// <summary>
        /// Cancels the current sync operation.
        /// </summary>
        public void CancelSync()
        {
            if (_isSyncing)
            {
                Debug.Log("[MultiplayerReplaySync] Cancelling replay sync");
                _isSyncing = false;
                _collectedData?.Clear();
            }
        }

        private struct ClientReplayData
        {
            public Guid SessionId;
            public byte[] SerializedFrame;
            public byte[] SerializedStats;
            public Guid ColorProfileId;
            public string ColorProfileJson;
            public Guid CameraPresetId;
            public string CameraPresetJson;
            public double[] FrameTimes;
        }
    }
}
