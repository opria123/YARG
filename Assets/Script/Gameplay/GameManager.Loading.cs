using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Linq;
using Cysharp.Threading.Tasks;
using UnityEngine;
using YARG.Core;
using YARG.Core.Audio;
using YARG.Core.Chart;
using YARG.Core.Logging;
using YARG.Core.Replays;
using YARG.Gameplay.HUD;
using YARG.Gameplay.Player;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YARG.Menu.Settings;
using YARG.Playback;
using YARG.Player;
using YARG.Scores;
using YARG.Settings;
using YARG.Song;
using YARG.Networking.Abstraction;
using YARG.Networking.Gameplay;
using YARG.Core.Game;

namespace YARG.Gameplay
{
    public partial class GameManager
    {
        private enum LoadFailureState
        {
            None,
            Rescan,
            Error
        }

        [Header("Instrument Prefabs")]
        [SerializeField]
        private GameObject _fiveFretGuitarPrefab;
        [SerializeField]
        private GameObject _sixFretGuitarPrefab;
        [SerializeField]
        private GameObject _fourLaneDrumsPrefab;
        [SerializeField]
        private GameObject _fiveLaneDrumsPrefab;
        [SerializeField]
        private GameObject _proKeysPrefab;
        [SerializeField]
        private GameObject _fiveLaneKeysPrefab;
        [SerializeField]
        private GameObject _proGuitarPrefab;

        private LoadFailureState _loadState;
        private string _loadFailureMessage;

        // All access to chart data must be done through this event,
        // since things are loaded asynchronously
        // Players are initialized by hand and don't go through this event
        private event Action<SongChart> _chartLoaded;

        public event Action<SongChart> ChartLoaded
        {
            add
            {
                _chartLoaded += value;

                // Invoke now if already loaded, this event is only fired once
                var chart = Chart;
                if (chart != null) value?.Invoke(chart);
            }
            remove => _chartLoaded -= value;
        }

        private event Action _songLoaded;

        public event Action SongLoaded
        {
            add
            {
                _songLoaded += value;

                // Invoke now if already loaded, this event is only fired once
                if (_mixer != null)
                {
                    value?.Invoke();
                }
            }
            remove => _songLoaded -= value;
        }

        private event Action _songStarted;

        public event Action SongStarted
        {
            add
            {
                _songStarted += value;

                // Invoke now if already loaded, this event is only fired once
                if (IsSongStarted) value?.Invoke();
            }
            remove => _songStarted -= value;
        }

        private async void Start()
        {
            Debug.Log("[GameManager.Start] BEGIN - Entering Start method");

            _multiplayerFailureReported = false;
            
            // If in multiplayer, create players BEFORE anything else
            if (_multiplayerSync != null)
            {
                Debug.Log("[GameManager] Multiplayer mode detected - creating players from network objects...");
                
                // Brief delay to ensure network objects are fully initialized
                // NetworkPlayerData now uses explicit DontDestroyOnLoad, so this can be minimal
                await Cysharp.Threading.Tasks.UniTask.Delay(100);
                
                // Create players from network data
                Debug.Log("[GameManager] Attempting to create multiplayer players...");
                YargPlayers = Menu.Multiplayer.MultiplayerPlayerManager.CreateMultiplayerPlayers();
                Debug.Log($"[GameManager] Multiplayer mode - created {YargPlayers.Count} players from network data");
                
                // If still no players, retry with short intervals
                int attempts = 0;
                while (YargPlayers.Count == 0 && attempts < 3)
                {
                    attempts++;
                    int delayMs = 100; // Quick retries since objects should exist
                    Debug.LogWarning($"[GameManager] No players found (attempt {attempts}/3), waiting {delayMs}ms for network sync...");
                    await Cysharp.Threading.Tasks.UniTask.Delay(delayMs);
                    YargPlayers = Menu.Multiplayer.MultiplayerPlayerManager.CreateMultiplayerPlayers();
                    Debug.Log($"[GameManager] After delay - created {YargPlayers.Count} players from network data");
                }
                
                // If still no players after all attempts, log error but continue
                if (YargPlayers.Count == 0)
                {
                    Debug.LogError("[GameManager] FAILED to create multiplayer players after 3 attempts! Cannot start gameplay.");
                    ToastManager.ToastError("Failed to create players for multiplayer game!");
                    GlobalVariables.Instance.LoadScene(SceneIndex.Menu);
                    return;
                }
                else
                {
                    Debug.Log($"[GameManager] Successfully created {YargPlayers.Count} multiplayer players");
                }

                // Reset band failure tracking for host in LiteNet multiplayer
                var networkService = NetworkingServiceFactory.Instance;
                if (networkService != null && networkService.IsHosting)
                {
                    // Band failure tracking is managed by the adapter
                }
            }
            
            Debug.Log("[GameManager.Start] Creating LoadingContext to show loading screen");
            // Displays the loading screen
            using var context = new LoadingContext();
            var global = GlobalVariables.Instance;

            // Disable until everything's loaded
            enabled = false;

            YargLogger.LogFormatInfo("Loading song {0} - {1}", Song.Name, Song.Artist);

            if (ReplayInfo != null)
            {
                if (!SongContainer.SongsByHash.TryGetValue(GlobalVariables.State.CurrentReplay.SongChecksum, out var songs))
                {
                    ToastManager.ToastWarning("Song not present in library");
                    global.LoadScene(SceneIndex.Menu);
                    return;
                }
                Song = songs[0];

                context.SetLoadingText("Loading replay...");
                if (!LoadReplay())
                {
                    ToastManager.ToastError("Failed to load replay!");
                    global.LoadScene(SceneIndex.Menu);
                    return;
                }

                if (!GlobalVariables.State.PlayingWithReplay)
                {
                    _replayController.gameObject.SetActive(true);
                }
                else
                {
                    // var players = new YargPlayer[YargPlayers.Count + PlayerContainer.Players.Count];
                    _replayController.gameObject.SetActive(false);
                    var players = new List<YargPlayer>();
                    players.AddRange(PlayerContainer.Players);
                    for (int i = 0; i < YargPlayers.Count; i++)
                    {
                         // YargPlayers[i].ReplayIndex = i;
                         players.Add(YargPlayers[i]);
                    }

                    YargPlayers = players.ToArray();
                }

                var replayIndex = 0;
                foreach (var player in YargPlayers)
                {
                    if (player.IsReplay)
                    {
                        player.ReplayIndex = replayIndex;
                        replayIndex++;
                    }
                }
            }

            Debug.Log("[GameManager.Start] Queueing chart and audio loading tasks");
            context.Queue(UniTask.RunOnThreadPool(LoadChart), "Loading chart...");
            context.Queue(UniTask.RunOnThreadPool(LoadAudio), "Loading audio...");
            Debug.Log("[GameManager.Start] Waiting for loading tasks to complete...");
            await context.Wait();
            Debug.Log("[GameManager.Start] Loading tasks completed");

            if (_loadState == LoadFailureState.Rescan)
            {
                ToastManager.ToastWarning("Chart requires a rescan!", () =>
                {
                    SettingsMenu.Instance.gameObject.SetActive(true);
                    SettingsMenu.Instance.SelectTabByName("SongManager");
                });

                global.LoadScene(SceneIndex.Menu);
                return;
            }

            if (_loadState == LoadFailureState.Error)
            {
                YargLogger.LogError(_loadFailureMessage);
                ToastManager.ToastError(_loadFailureMessage);

                global.LoadScene(SceneIndex.Menu);
                return;
            }

            FinalizeChart();

            // Get audio calibration
            int audioCalibration = SettingsManager.Settings.AudioCalibration.Value;
            if (SettingsManager.Settings.AccountForHardwareLatency.Value)
                audioCalibration += GlobalAudioHandler.PlaybackLatency;

            // Check if this is a late-join spectator and get start time
            bool isLateJoinSpectator = GlobalVariables.State.IsSpectating;
            double spectateStartTime = GlobalVariables.State.SpectateStartTime;
            double songStartTime = isLateJoinSpectator && spectateStartTime > 0 ? spectateStartTime : 0;
            double songStartDelay = isLateJoinSpectator && spectateStartTime > 0 ? 0 : SONG_START_DELAY;
            
            if (isLateJoinSpectator && spectateStartTime > 0)
            {
                Debug.Log($"[GameManager] Late-join spectator: Starting song at {spectateStartTime:F2}s (no delay)");
            }

            // Initialize song runner
            _songRunner = new SongRunner(
                _mixer,
                startTime: songStartTime,
                songStartDelay,
                GlobalVariables.State.SongSpeed,
                audioCalibration,
                SettingsManager.Settings.VideoCalibration.Value,
                Song.SongOffsetSeconds);

            if (isLateJoinSpectator)
            {
                Debug.Log("[GameManager] Late-join spectator mode - creating spectator tracks only");
                _isSpectating = true;
                PlayerHasFailed = false; // Not failed, just spectating
                
                // Create empty players list (no local tracks)
                _players = new List<BasePlayer>();
                
                // Create spectator tracks for all active bands
                CreateLateJoinSpectatorTracks();
            }
            else
            {
                // Spawn players normally
                CreatePlayers();
            }
            
            // Setup multiplayer unison synchronization after players are created
            SetupMultiplayerUnisonSync();
            
            // Initialize band leaderboard HUD for multiplayer band mode
            InitializeBandLeaderboardHUD();

            // Set up the crowd stem so it can be restored after muting (if it exists)
            if (_stemStates.TryGetValue(SongStem.Crowd, out var state))
            {
                state.Total = 1;
                state.Audible = 1;
            }

            if (_loadState == LoadFailureState.Error)
            {
                ToastManager.ToastError(_loadFailureMessage);

                global.LoadScene(SceneIndex.Menu);
                return;
            }

            // Listen for menu inputs
            Navigator.Instance.NavigationEvent += OnNavigationEvent;

            // Debug info
            InitializeDebug();
#if UNITY_EDITOR
            SetDebugEnabled(true);
#endif

            // Initialize/destroy practice mode
            if (IsPractice)
            {
                PracticeManager.DisplayPracticeMenu();
            }
            else
            {
                Destroy(PracticeManager);
            }

            _failMeter.Initialize(EngineManager, this);

            if (IsNoFailActive || IsPractice)
            {
                _failMeter.SetActive(false);
            }

            // This is not an else because we still want to subscribe in case the user disables no fail during the song
            // We check in the callback to determine whether we should actually run the fail routine
            if (ReplayInfo == null || GlobalVariables.State.PlayingWithReplay)
            {
                EngineManager.OnSongFailed += OnSongFailed;
                EngineManager.OnPlayerFailed += OnPlayerFailed;
                EngineManager.OnPlayerRevived += OnPlayerRevived;

                EngineManager.InitializeHappiness();

                SettingsManager.Settings.NoFailMode.OnChange += OnNoFailModeChanged;
            }

            // Log constant values
            YargLogger.LogFormatDebug("Audio calibration: {0}, video calibration: {1}, song offset: {2}",
                _songRunner.AudioCalibration, _songRunner.VideoCalibration, _songRunner.SongOffset);

            await WaitForMultiplayerSongStartAsync();

            Debug.Log("[GameManager.Start] About to exit using block - LoadingContext will be disposed");
            // Loaded, enable updates
            enabled = true;
            IsSongStarted = true;
            _songStarted?.Invoke();
            Debug.Log("[GameManager.Start] END - Exiting Start method, loading screen should be hidden");
        }

        private async UniTask WaitForMultiplayerSongStartAsync()
        {
            if (_multiplayerSync == null)
            {
                return;
            }

            var networkService = NetworkingServiceFactory.Instance;
            if (networkService == null || !networkService.IsNetworkActive)
            {
                return;
            }

            var liteNetAdapter = networkService as LiteNetNetworkingAdapter;
            if (liteNetAdapter == null)
            {
                return;
            }
            
            // Signal that this player has finished loading
            Debug.Log("[GameManager] Signaling gameplay load ready to network");
            liteNetAdapter.SendGameplayLoadReady();

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, this.GetCancellationTokenOnDestroy());

            try
            {
                await liteNetAdapter.WaitForMultiplayerGameplayStartAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                if (timeoutCts.IsCancellationRequested)
                {
                    Debug.LogWarning("[GameManager] Timed out waiting for all players to finish loading. Proceeding with song start.");
                    liteNetAdapter.ForceCompleteGameplayStartBarrier();
                }
            }

            await UniTask.SwitchToMainThread();
        }

        private bool LoadReplay()
        {
            var readOptions = new ReplayReadOptions { KeepFrameTimes = GlobalVariables.VerboseReplays };
            var (result, data) = ReplayIO.TryLoadData(ReplayInfo, readOptions);
            if (result != ReplayReadResult.Valid)
            {
                YargLogger.LogFormatError("Failed to load replay! Result: {0}", result);
                return false;
            }

            // Create YargPlayers from the replay frames
            var players = new YargPlayer[data.Frames.Length];
            for (int i = 0; i < data.Frames.Length; ++i)
            {
                players[i] = new YargPlayer(data.Frames[i], data);
            }

            ReplayData = data;
            YargPlayers = players;
            return true;
        }

        private void LoadChart()
        {
            try
            {
                Chart = Song.LoadChart();
                if (Chart != null)
                {
                    GenerateVenueTrack();
                }
                else
                {
                    _loadState = LoadFailureState.Rescan;
                }
            }
            catch (Exception ex)
            {
                _loadState = LoadFailureState.Error;
                _loadFailureMessage = "Failed to load chart!";
                YargLogger.LogException(ex, "Failed to load chart!");
            }
        }

        private void GenerateVenueTrack()
        {
            // If we have no venue events, attempt to load from milo
            if (Chart.VenueTrack.IsEmpty)
            {
                    SongChart.LoadVenueFromMilo(Chart, Song);

                    YargLogger.LogFormatWarning("Loaded {0} lighting events from milo", Chart.VenueTrack.Lighting.Count);
            }

            if (File.Exists(VenueAutoGenerationPreset.DefaultPath))
            {
                var preset = new VenueAutoGenerationPreset(VenueAutoGenerationPreset.DefaultPath);
                if (!preset.ChartHasFog(Chart)) // This is separate because we may want to add fog even if venue is authored
                {
                    Chart = preset.GenerateFogEvents(Chart);
                }

                if (Chart.VenueTrack.Lighting.Count == 0)
                {
                    Chart = preset.GenerateLightingEvents(Chart);
                }
            }
        }

        private void FinalizeChart()
        {
            double audioLength = _mixer.Length;
            double chartLength = Chart.GetEndTime();
            double endTime = Chart.GetEndEvent()?.Time ?? -1;

            // - Chart < Audio < [end] -> Audio
            // - Chart < [end] < Audio -> [end]
            // - [end] < Chart < Audio -> Audio
            // - Audio < Chart         -> Chart
            if (audioLength <= chartLength)
            {
                SongLength = chartLength;
            }
            else if (endTime <= chartLength || audioLength <= endTime)
            {
                SongLength = audioLength;
            }
            else
            {
                SongLength = endTime;
            }

            // Get the first and last note times for the chart
            FirstNoteTime = Chart.GetFirstNoteStartTime();
            LastNoteTime = Chart.GetLastNoteEndTime();

            // Make sure enough beatlines have been generated to cover the song end delay
            Chart.SyncTrack.GenerateBeatlines(SongLength + SONG_END_DELAY, true);

            BeatEventHandler = new BeatEventHandler(Chart.SyncTrack);
            CrowdEventHandler = new CrowdEventHandler(Chart, this);

            _chartLoaded?.Invoke(Chart);

            _songLoaded?.Invoke();
        }

        private void CreatePlayers()
        {
            try
            {
                _players = new List<BasePlayer>();

                bool vocalTrackInitialized = false;

                int index = -1;
                int highwayIndex = -1;
                int vocalIndex = -1;
                
                YargLogger.LogInfo($"[GameManager.CreatePlayers] Processing {YargPlayers?.Count ?? 0} YargPlayers");
                
                foreach (var player in YargPlayers)
                {
                    YargLogger.LogInfo($"[GameManager.CreatePlayers] Processing player: {player?.Profile?.Name ?? "NULL"}, " +
                                       $"IsReplay={player?.IsReplay}, SittingOut={player?.SittingOut}, " +
                                       $"HasBindings={player?.Bindings != null}, " +
                                       $"GameMode={player?.Profile?.GameMode}, Instrument={player?.Profile?.CurrentInstrument}");
                    
                    if (!player.IsReplay && player.Bindings != null)
                    {
                        // Reset microphone (resets channel buffers)
                        // We probably wanna do this no matter what, so put it up here
                        player.Bindings.Microphone?.Reset();
                    }

                    // Skip if the player is sitting out
                    if (player.SittingOut)
                    {
                        YargLogger.LogInfo($"[GameManager.CreatePlayers] Skipping player {player.Profile.Name} - SittingOut=true");
                        continue;
                    }
                    index++;

                    if (!player.IsReplay && !player.HasSyncedPresets)
                    {
                        // Don't do this if it's a replay (replay already has presets)
                        // or if synced presets were already applied (multiplayer remote players)
                        player.RefreshPresets();
                    }

                    var lastHighScore = ScoreContainer.GetHighScore(Song.Hash, player.Profile.Id, player.Profile.CurrentInstrument, false)?.Score;
                    YargLogger.LogFormatInfo("Current high score for player {0} on {1}: {2}",
                        player.Profile.Name, player.Profile.CurrentInstrument, lastHighScore ?? 0);
                    
                    // DEBUG: Log chart instance before each player creation
                    var chartStatus = Chart != null ? "NOT NULL" : "NULL";
                    var chartHash = Chart?.GetHashCode() ?? -1;
                    YargLogger.LogInfo($"[GameManager] Creating player {player.Profile.Name}, Chart instance: {chartStatus}, GetHashCode: {chartHash}");

                    if (player.Profile.GameMode != GameMode.Vocals)
                    {
                        highwayIndex++;
                        var prefab = player.Profile.GameMode switch
                        {
                            GameMode.FiveFretGuitar => _fiveFretGuitarPrefab,
                            GameMode.SixFretGuitar  => _sixFretGuitarPrefab,
                            GameMode.FourLaneDrums  => _fourLaneDrumsPrefab,
                            GameMode.FiveLaneDrums  => _fiveLaneDrumsPrefab,
                            GameMode.EliteDrums     => Song.HasInstrument(Instrument.FiveLaneDrums) ? _fiveLaneDrumsPrefab : _fourLaneDrumsPrefab,
                            GameMode.ProKeys        => player.Profile.CurrentInstrument is Instrument.ProKeys ? _proKeysPrefab : _fiveLaneKeysPrefab,
                            GameMode.ProGuitar      => _proGuitarPrefab,
                            _                       => null
                        };

                        // Skip if there's no prefab for the game mode
                        if (prefab == null)
                        {
                            YargLogger.LogWarning($"[GameManager.CreatePlayers] Skipping player {player.Profile.Name} - no prefab for GameMode {player.Profile.GameMode}");
                            continue;
                        }
                        
                        YargLogger.LogInfo($"[GameManager.CreatePlayers] Creating track for player {player.Profile.Name}, highwayIndex={highwayIndex}");

                        var playerObject = Instantiate(prefab,
                            new Vector3(highwayIndex * TRACK_SPACING_X, 100f, 0f), prefab.transform.rotation);

                        // Setup player
                        var trackPlayer = playerObject.GetComponent<TrackPlayer>();
                        var trackView = _trackViewManager.CreateTrackView();
                        trackPlayer.Initialize(highwayIndex, player, Chart, trackView, _mixer, lastHighScore);

                        _players.Add(trackPlayer);
                        _trackViewManager.AddTrackPlayer(trackPlayer);
                        
                        // MULTIPLAYER: Attach visualizer for remote players
                        if (_multiplayerSync != null)
                        {
                            AttachRemotePlayerVisualizer(trackPlayer, index); // Use index directly (already incremented above)
                        }
                    }
                    else
                    {
                        // Initialize the vocal track if it hasn't been already, and hide lyric bar
                        if (!vocalTrackInitialized)
                        {
                            VocalTrack.gameObject.SetActive(true);
                            _trackViewManager.CreateVocalTrackView();

                            // Since all players have to select the same vocals
                            // type (solo/harmony) this works no problem.
                            var chart = player.Profile.CurrentInstrument == Instrument.Vocals
                                ? Chart.Vocals
                                : Chart.Harmony;
                            VocalTrack.Initialize(chart, player, Song.VocalScrollSpeedScalingFactor);

                            _lyricBar.SetActive(false);
                            vocalTrackInitialized = true;
                        }

                        // Create the player on the vocal track

                        var vocalsPlayer = VocalTrack.CreatePlayer();
                        vocalIndex++;
                        var playerHud = _trackViewManager.CreateVocalsPlayerHUD();

                        var percussionTrack = VocalTrack.CreatePercussionTrack();
                        percussionTrack.TrackSpeed = VocalTrack.TrackSpeed;
                        vocalsPlayer.Initialize(index, vocalIndex, player, Chart, playerHud, percussionTrack, lastHighScore, VocalTrack.TrackSpeed);

                        _players.Add(vocalsPlayer);

                        // MULTIPLAYER: Ensure remote vocal players also receive network visualization/input mapping
                        if (_multiplayerSync != null)
                        {
                            AttachRemotePlayerVisualizer(vocalsPlayer, index);
                        }
                    }

                    // Add (or increase total of) the stem state
                    var stem = player.Profile.CurrentInstrument.ToSongStem();
                    if (stem == SongStem.Bass && !_stemStates.ContainsKey(SongStem.Bass))
                    {
                        stem = SongStem.Rhythm;
                    }

                    if (stem != _backgroundStem && _stemStates.TryGetValue(stem, out var state))
                    {
                        ++state.Total;
                        ++state.Audible;
                    }
                    else if (_stemStates.TryGetValue(_backgroundStem, out state))
                    {
                        // Ensures the stem will still play at a minimum of 50%, even if all players mute
                        state.Total += 2;
                        state.Audible += 2;
                    }
                }
            }
            catch (Exception ex)
            {
                _loadState = LoadFailureState.Error;
                _loadFailureMessage = "Failed to load song!";
                YargLogger.LogException(ex, "Failed to load song!");
            }
        }
        
        /// <summary>
        /// Sets up unison phrase synchronization for multiplayer.
        /// In networked multiplayer, each client has its own EngineManager that only sees local players.
        /// This method configures the system to coordinate unison bonuses across the network.
        /// </summary>
        private void SetupMultiplayerUnisonSync()
        {
            if (_multiplayerUnisonSync == null)
            {
                // Not in multiplayer mode - unisons work normally through local EngineManager
                return;
            }
            
            // Disable automatic unison bonus awarding in the EngineManager
            // The network layer will handle coordinating bonuses across all players
            EngineManager.DisableAutomaticUnisonBonuses = true;
            YargLogger.LogInfo("[GameManager] Disabled automatic unison bonuses for networked multiplayer");
            
            // Subscribe to unison phrase hit events from the EngineManager
            EngineManager.OnUnisonPhraseHit += OnUnisonPhraseHit;
            
            // Register local engine containers with the unison sync
            foreach (var player in _players)
            {
                if (player != null && player.PlayerEngineContainer != null)
                {
                    _multiplayerUnisonSync.RegisterEngineContainer(player.PlayerEngineContainer);
                }
            }
            
            // Set the total player count for unison tracking
            // This includes both local and remote players
            int totalPlayers = GetTotalNetworkPlayerCount();
            _multiplayerUnisonSync.SetTotalPlayerCount(totalPlayers);
            
            YargLogger.LogInfo($"[GameManager] Multiplayer unison sync initialized with {totalPlayers} total players");
        }
        
        /// <summary>
        /// Gets the total number of players across the network (for unison tracking).
        /// </summary>
        /// <summary>
        /// Gets the total number of players who can participate in unisons across the network.
        /// Vocals players are excluded since they don't participate in unisons.
        /// </summary>
        private int GetTotalNetworkPlayerCount()
        {
            // Count local non-vocal players
            int localNonVocalCount = 0;
            foreach (var player in _players)
            {
                if (player != null && !(player is VocalsPlayer))
                {
                    localNonVocalCount++;
                }
            }
            
            // Check if we're in multiplayer and count non-vocal network players
            var liteNetAdapter = NetworkingServiceFactory.Instance as LiteNetNetworkingAdapter;
            if (liteNetAdapter != null && liteNetAdapter.IsNetworkActive)
            {
                var allPlayers = liteNetAdapter.GetAllPlayers();
                if (allPlayers == null || allPlayers.Count == 0)
                {
                    return localNonVocalCount;
                }
                
                // Count non-vocal players by checking their instrument
                // Vocals instrument type is typically 4 (Instrument.Vocals)
                const int VocalsInstrument = 4;
                int nonVocalNetworkPlayers = 0;
                
                foreach (var player in allPlayers)
                {
                    if (player != null && player.Instrument != VocalsInstrument)
                    {
                        nonVocalNetworkPlayers++;
                    }
                }
                
                // If no non-vocal players, return 0
                if (nonVocalNetworkPlayers == 0)
                {
                    YargLogger.LogInfo("[GameManager] All network players are vocals - no unison participation");
                    return 0;
                }
                
                return nonVocalNetworkPlayers;
            }
            
            // Fallback to local non-vocal player count
            return localNonVocalCount;
        }
        
        /// <summary>
        /// Called when a local player hits a unison phrase.
        /// Forwards to the network sync component.
        /// </summary>
        private void OnUnisonPhraseHit(double phraseTime, double phraseEndTime)
        {
            if (_multiplayerUnisonSync != null)
            {
                _multiplayerUnisonSync.OnLocalUnisonPhraseHit(phraseTime, phraseEndTime);
            }
        }
        
        /// <summary>
        /// Attach RemotePlayerVisualizer to a player for multiplayer network sync.
        /// </summary>
        private void AttachRemotePlayerVisualizer(BasePlayer player, int playerIndex)
        {
            try
            {
                var networkPlayerData = ResolveNetworkPlayerData(player, playerIndex);
                if (networkPlayerData == null)
                {
                    YargLogger.LogFormatWarning("[GameManager] Unable to resolve NetworkPlayerData for player {0} (index {1})",
                        player.Player.Profile.Name, playerIndex);
                    return;
                }

                player.SetNetworkPlayerData(networkPlayerData);

                var profile = player.Player.Profile;
                if (!networkPlayerData.IsLocalUser)
                {
                    var simulation = player.gameObject.AddComponent<Player.RemotePlayerSimulation>();
                    simulation.Initialize(player, networkPlayerData);
                    
                    // Register the simulation with the BasePlayer so ApplyRemoteState gets called
                    player.RegisterRemoteSimulation(simulation);

                    var instrument = profile != null ? profile.CurrentInstrument.ToString() : "Unknown";
                    YargLogger.LogInfo($"[GameManager] Attached RemotePlayerSimulation to player {profile?.Name ?? "Unknown"} ({instrument}) (Network: {networkPlayerData.PlayerName})");
                }
                else
                {
                    YargLogger.LogInfo($"[GameManager] Player {profile?.Name ?? "Unknown"} mapped to local NetworkPlayerData {networkPlayerData.PlayerName}; skipping remote simulation.");
                }
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex, "Failed to attach RemotePlayerVisualizer");
            }
        }

        private static NetworkPlayerData ResolveNetworkPlayerData(BasePlayer player, int playerIndex)
        {
            bool expectsLocalData = player.Player.Bindings != null && !player.Player.IsReplay;

            if (Menu.Multiplayer.MultiplayerPlayerManager.TryGetNetworkPlayer(player.Player, out var mappedNetworkPlayer))
            {
                if (mappedNetworkPlayer != null && mappedNetworkPlayer.IsLocalUser == expectsLocalData)
                {
                    return mappedNetworkPlayer;
                }

                YargLogger.LogFormatWarning("[GameManager] NetworkPlayerData mismatch for player {0} (expected local={1}, mapped local={2}).",
                    player.Player.Profile.Name, expectsLocalData, mappedNetworkPlayer?.IsLocalUser);
            }

            // Try LiteNet
            var liteNetAdapter = NetworkingServiceFactory.Instance as LiteNetNetworkingAdapter;
            if (liteNetAdapter != null && liteNetAdapter.IsNetworkActive)
            {
                var networkPlayers = liteNetAdapter.GetAllPlayers();
                
                var result = FindNetworkPlayerData(networkPlayers, player, playerIndex, expectsLocalData);
                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }
        
        private static NetworkPlayerData FindNetworkPlayerData(List<NetworkPlayerData> networkPlayers, BasePlayer player, int playerIndex, bool expectsLocalData)
        {
            if (networkPlayers == null || networkPlayers.Count == 0)
            {
                return null;
            }

            if (playerIndex >= 0 && playerIndex < networkPlayers.Count)
            {
                var indexedPlayer = networkPlayers[playerIndex];
                if (indexedPlayer != null && indexedPlayer.IsLocalUser == expectsLocalData)
                {
                    return indexedPlayer;
                }
            }

            // As a final fallback, try to match by player name and locality
            var byName = networkPlayers.FirstOrDefault(p => p != null &&
                                                            p.IsLocalUser == expectsLocalData &&
                                                            string.Equals(p.PlayerName, player.Player.Profile.Name, StringComparison.Ordinal));
            if (byName != null)
            {
                return byName;
            }

            // If we still didn't find a match, look for any entry that matches the expected locality
            return networkPlayers.FirstOrDefault(p => p != null && p.IsLocalUser == expectsLocalData);
        }
        
        /// <summary>
        /// Holds reference to the band leaderboard HUD instance.
        /// </summary>
        private BandLeaderboardHUD _bandLeaderboardHUD;
        
        /// <summary>
        /// Holds reference to the spectate overlay HUD instance.
        /// </summary>
        private SpectateOverlayHUD _spectateOverlayHUD;
        
        /// <summary>
        /// Initializes the band leaderboard HUD for multiplayer band mode.
        /// The HUD displays band rankings, scores, and position changes during gameplay.
        /// </summary>
        private void InitializeBandLeaderboardHUD()
        {
            // Only create in multiplayer with band system active
            var networkService = NetworkingServiceFactory.Instance;
            if (networkService == null || !networkService.IsNetworkActive)
            {
                return;
            }
            
            var bandManager = Networking.Bands.BandManager.Instance;
            if (bandManager == null || !bandManager.IsBandSystemActive)
            {
                Debug.Log("[GameManager] Band system not active, skipping BandLeaderboardHUD creation");
                return;
            }
            
            // Don't show leaderboard if there's only 1 band (nothing to compete against)
            if (bandManager.Bands.Count < 2)
            {
                Debug.Log("[GameManager] Only 1 band, skipping BandLeaderboardHUD creation");
                return;
            }
            
            // Find a suitable canvas parent (use the draggable HUD's canvas)
            Canvas hudCanvas = null;
            if (_draggableHud != null)
            {
                hudCanvas = _draggableHud.GetComponentInParent<Canvas>();
            }
            
            if (hudCanvas == null)
            {
                hudCanvas = FindObjectOfType<Canvas>();
            }
            
            if (hudCanvas == null)
            {
                Debug.LogWarning("[GameManager] No Canvas found for BandLeaderboardHUD");
                return;
            }
            
            // Create the HUD GameObject with proper RectTransform setup
            var hudObj = new GameObject("BandLeaderboardHUD");
            hudObj.transform.SetParent(hudCanvas.transform, false);
            
            // Setup RectTransform to stretch to fill canvas (so child anchors work correctly)
            var hudRect = hudObj.AddComponent<RectTransform>();
            hudRect.anchorMin = Vector2.zero;
            hudRect.anchorMax = Vector2.one;
            hudRect.offsetMin = Vector2.zero;
            hudRect.offsetMax = Vector2.zero;
            
            // Add the component (it will build its own UI)
            _bandLeaderboardHUD = hudObj.AddComponent<BandLeaderboardHUD>();
            
            Debug.Log($"[GameManager] Created BandLeaderboardHUD for band mode with {bandManager.Bands.Count} bands");
            
            // Also create the spectate overlay HUD for when local band fails
            var spectateObj = new GameObject("SpectateOverlayHUD");
            spectateObj.transform.SetParent(hudCanvas.transform, false);
            
            // Setup RectTransform to stretch to fill canvas
            var spectateRect = spectateObj.AddComponent<RectTransform>();
            spectateRect.anchorMin = Vector2.zero;
            spectateRect.anchorMax = Vector2.one;
            spectateRect.offsetMin = Vector2.zero;
            spectateRect.offsetMax = Vector2.zero;
            
            // Ensure the overlay is on top of other UI
            spectateObj.transform.SetAsLastSibling();
            
            _spectateOverlayHUD = spectateObj.AddComponent<SpectateOverlayHUD>();
            
            Debug.Log("[GameManager] Created SpectateOverlayHUD for band mode");
        }
        
        /// <summary>
        /// Hides all local (non-spectator) track players.
        /// Called when entering spectate mode to make room for spectator tracks.
        /// Safe to call multiple times (idempotent).
        /// </summary>
        public void HideLocalTracks()
        {
            bool anyHidden = false;
            
            foreach (var player in _players)
            {
                if (player is TrackPlayer trackPlayer && !trackPlayer.gameObject.name.StartsWith("SpectatorTrack_"))
                {
                    // Only hide if currently visible
                    if (trackPlayer.gameObject.activeSelf)
                    {
                        // Remove from rendering but keep alive (we might need to show again)
                        _trackViewManager._highwayCameraRendering.RemoveTrackPlayer(trackPlayer);
                        trackPlayer.gameObject.SetActive(false);
                        
                        // Also hide the TrackView UI element to prevent layout issues
                        if (trackPlayer.TrackView != null)
                        {
                            trackPlayer.TrackView.gameObject.SetActive(false);
                        }
                        
                        Debug.Log($"[GameManager] Hidden local track for {trackPlayer.Player.Profile.Name}");
                        anyHidden = true;
                    }
                }
            }
            
            // Also hide vocals if active
            if (VocalTrack != null && VocalTrack.gameObject.activeSelf)
            {
                VocalTrack.gameObject.SetActive(false);
                Debug.Log("[GameManager] Hidden vocal track");
                anyHidden = true;
            }
            
            // Update the camera rendering only if we actually hid something
            if (anyHidden)
            {
                _trackViewManager.SetAllHUDScale();
            }
        }
        
        /// <summary>
        /// Shows all local (non-spectator) track players.
        /// Called when exiting spectate mode.
        /// Safe to call multiple times (idempotent).
        /// </summary>
        public void ShowLocalTracks()
        {
            bool anyShown = false;
            int maxLocalIndex = 0;
            
            foreach (var player in _players)
            {
                if (player is TrackPlayer trackPlayer && !trackPlayer.gameObject.name.StartsWith("SpectatorTrack_"))
                {
                    // Track the highest index among local tracks for HighwayCount
                    if (trackPlayer.HighwayIndex > maxLocalIndex)
                    {
                        maxLocalIndex = trackPlayer.HighwayIndex;
                    }
                    
                    // Only show if currently hidden
                    if (!trackPlayer.gameObject.activeSelf)
                    {
                        trackPlayer.gameObject.SetActive(true);
                        _trackViewManager._highwayCameraRendering.AddTrackPlayer(trackPlayer);
                        
                        // Also show the TrackView UI element
                        if (trackPlayer.TrackView != null)
                        {
                            trackPlayer.TrackView.gameObject.SetActive(true);
                        }
                        
                        Debug.Log($"[GameManager] Shown local track for {trackPlayer.Player.Profile.Name}");
                        anyShown = true;
                    }
                }
            }
            
            // Restore HighwayCount based on local tracks (now that they're visible again)
            if (anyShown)
            {
                TrackPlayer.HighwayCount = maxLocalIndex + 1;
            }
            
            // Show vocals track if we have vocal players and it's currently hidden
            if (VocalTrack != null && !VocalTrack.gameObject.activeSelf && _players.Any(p => p is VocalsPlayer))
            {
                VocalTrack.gameObject.SetActive(true);
                Debug.Log("[GameManager] Shown vocal track");
                anyShown = true;
            }
            
            // Update the camera rendering only if we actually showed something
            if (anyShown)
            {
                _trackViewManager.SetAllHUDScale();
            }
        }
        
        /// <summary>
        /// Creates spectator tracks for all active bands for a late-join spectator.
        /// Called when a player joins during gameplay with the song and wants to spectate.
        /// </summary>
        private void CreateLateJoinSpectatorTracks()
        {
            var networkService = NetworkingServiceFactory.Instance;
            if (networkService == null || !networkService.IsNetworkActive)
            {
                Debug.LogWarning("[GameManager] Cannot create late-join spectator tracks - network not active");
                return;
            }
            
            var bandManager = Networking.Bands.BandManager.Instance;
            
            // Get all network players (excluding self since we're spectating)
            var allNetworkPlayers = networkService.GetAllPlayers()
                .Where(p => !p.IsLocalUser)
                .ToList();
            
            if (allNetworkPlayers.Count == 0)
            {
                Debug.LogWarning("[GameManager] No remote players found for late-join spectate");
                return;
            }
            
            Debug.Log($"[GameManager] Creating late-join spectator tracks for {allNetworkPlayers.Count} remote players");
            
            // Reset HighwayCount for spectator tracks
            TrackPlayer.HighwayCount = 1;
            
            int highwayIndex = -1;
            int createdCount = 0;
            
            foreach (var networkPlayer in allNetworkPlayers)
            {
                // Skip if player has invalid instrument
                if (networkPlayer.Instrument < 0)
                {
                    Debug.Log($"[GameManager] Skipping late-join spectator track for {networkPlayer.PlayerName} - no instrument");
                    continue;
                }
                
                var instrument = (Instrument)networkPlayer.Instrument;
                var gameMode = instrument.ToNativeGameMode();
                
                // Skip vocals for now (more complex setup)
                if (gameMode == GameMode.Vocals)
                {
                    Debug.Log($"[GameManager] Skipping late-join spectator track for {networkPlayer.PlayerName} - vocals not yet supported for spectate");
                    continue;
                }
                
                // Create a YargPlayer for this remote player
                var profile = new YargProfile
                {
                    Name = networkPlayer.PlayerName ?? "Remote Player",
                    CurrentInstrument = instrument,
                    CurrentDifficulty = (Difficulty)networkPlayer.Difficulty,
                    GameMode = gameMode,
                };
                
                var yargPlayer = new YargPlayer(profile, bindings: null);
                
                // Check if preset sync is enabled
                var gameplaySettings = MultiplayerGameplaySettings.Instance;
                bool enablePresetSync = gameplaySettings?.EnablePresetSync ?? false;
                bool hasSyncedPresets = networkPlayer.HasSyncedPresets;
                
                if (enablePresetSync && hasSyncedPresets)
                {
                    Debug.Log($"[GameManager] Applying synced presets for late-join spectator track: {networkPlayer.PlayerName}");
                    yargPlayer.ApplySyncedPresets(
                        networkPlayer.CameraPresetId, networkPlayer.CameraPresetJson,
                        networkPlayer.HighwayPresetId, networkPlayer.HighwayPresetJson,
                        networkPlayer.ColorProfileId, networkPlayer.ColorProfileJson,
                        networkPlayer.ThemePresetId, networkPlayer.ThemePresetJson);
                }
                else
                {
                    yargPlayer.RefreshPresets();
                }
                
                // Get the prefab
                var prefab = gameMode switch
                {
                    GameMode.FiveFretGuitar => _fiveFretGuitarPrefab,
                    GameMode.SixFretGuitar  => _sixFretGuitarPrefab,
                    GameMode.FourLaneDrums  => _fourLaneDrumsPrefab,
                    GameMode.FiveLaneDrums  => _fiveLaneDrumsPrefab,
                    GameMode.ProKeys        => instrument is Instrument.ProKeys ? _proKeysPrefab : _fiveLaneKeysPrefab,
                    GameMode.ProGuitar      => _proGuitarPrefab,
                    _                       => null
                };
                
                if (prefab == null)
                {
                    Debug.LogWarning($"[GameManager] No prefab for late-join spectator track GameMode {gameMode}");
                    continue;
                }
                
                highwayIndex++;
                
                // Instantiate the track
                var playerObject = Instantiate(prefab,
                    new Vector3(highwayIndex * TRACK_SPACING_X, 100f, 0f), prefab.transform.rotation);
                
                // Setup player
                var trackPlayer = playerObject.GetComponent<TrackPlayer>();
                var trackView = _trackViewManager.CreateTrackView(trackPlayer, yargPlayer);
                trackPlayer.Initialize(highwayIndex, yargPlayer, Chart, trackView, _mixer, null);
                
                // Add RemotePlayerSimulation to receive network state
                var simulation = playerObject.AddComponent<Player.RemotePlayerSimulation>();
                simulation.Initialize(trackPlayer, networkPlayer);
                trackPlayer.RegisterRemoteSimulation(simulation);
                trackPlayer.SetNetworkPlayerData(networkPlayer);
                
                // Set initial camera state - start raised since we're just joining
                if (trackPlayer.TrackCameraPositioner != null)
                {
                    bool playerAlreadyFailed = networkPlayer.HasFailed;
                    trackPlayer.TrackCameraPositioner.SetInitialState(isRaised: !playerAlreadyFailed);
                    Debug.Log($"[GameManager] Late-join spectator track for {networkPlayer.PlayerName}: failed={playerAlreadyFailed}");
                }
                
                // Mark as spectator track
                playerObject.name = $"LateJoinSpectator_{networkPlayer.PlayerName}";
                
                _players.Add(trackPlayer);
                _trackViewManager._highwayCameraRendering.AddTrackPlayer(trackPlayer);
                
                createdCount++;
            }
            
            // Update HighwayCount to match actual spectator tracks
            if (createdCount > 0)
            {
                TrackPlayer.HighwayCount = createdCount;
                _trackViewManager.SetAllHUDScale();
            }
            
            Debug.Log($"[GameManager] Created {createdCount} late-join spectator tracks");
            
            // Show spectate UI overlay
            OnSpectateStarted?.Invoke();
        }
        
        /// <summary>
        /// Creates spectator tracks for players in the specified band.
        /// Called when entering spectate mode to show the spectated band's gameplay.
        /// </summary>
        /// <param name="bandId">The band ID to create spectator tracks for.</param>
        /// <returns>True if tracks were created successfully.</returns>
        public bool CreateSpectatorTracksForBand(int bandId)
        {
            var bandManager = Networking.Bands.BandManager.Instance;
            if (bandManager == null || !bandManager.Bands.TryGetValue(bandId, out var bandInfo))
            {
                Debug.LogWarning($"[GameManager] Cannot create spectator tracks - band {bandId} not found");
                return false;
            }
            
            var networkService = NetworkingServiceFactory.Instance;
            if (networkService == null || !networkService.IsNetworkActive)
            {
                Debug.LogWarning("[GameManager] Cannot create spectator tracks - network not active");
                return false;
            }
            
            // FIRST: Hide local tracks to make room for spectator tracks
            HideLocalTracks();
            
            // Get all network players
            var allNetworkPlayers = networkService.GetAllPlayers();
            
            // Find players in the target band
            var bandPlayerIds = bandInfo.PlayerIds;
            var bandNetworkPlayers = allNetworkPlayers
                .Where(p => bandPlayerIds.Contains(p.NetworkPlayerId))
                .ToList();
            
            if (bandNetworkPlayers.Count == 0)
            {
                Debug.LogWarning($"[GameManager] No network players found for band {bandId}");
                return false;
            }
            
            Debug.Log($"[GameManager] Creating spectator tracks for {bandNetworkPlayers.Count} players in band '{bandInfo.DisplayName}'");
            
            // Reset HighwayCount for spectator tracks (since local tracks are hidden)
            // This ensures HUD positioning is calculated correctly for the spectator tracks
            TrackPlayer.HighwayCount = 1;
            
            // Start spectator tracks at index 0 (local tracks are now hidden)
            int highwayIndex = -1;
            
            int createdCount = 0;
            
            foreach (var networkPlayer in bandNetworkPlayers)
            {
                // Skip if player has invalid instrument
                if (networkPlayer.Instrument < 0)
                {
                    Debug.Log($"[GameManager] Skipping spectator track for {networkPlayer.PlayerName} - no instrument");
                    continue;
                }
                
                var instrument = (Instrument)networkPlayer.Instrument;
                var gameMode = instrument.ToNativeGameMode();
                
                // Skip vocals for now (more complex setup)
                if (gameMode == GameMode.Vocals)
                {
                    Debug.Log($"[GameManager] Skipping spectator track for {networkPlayer.PlayerName} - vocals not yet supported for spectate");
                    continue;
                }
                
                // Create a YargPlayer for this remote player
                var profile = new YargProfile
                {
                    Name = networkPlayer.PlayerName ?? "Remote Player",
                    CurrentInstrument = instrument,
                    CurrentDifficulty = (Difficulty)networkPlayer.Difficulty,
                    GameMode = gameMode,
                };
                
                var yargPlayer = new YargPlayer(profile, bindings: null);
                
                // IMPORTANT: Initialize presets for the remote player
                // Check if preset sync is enabled and we have synced preset data
                var gameplaySettings = MultiplayerGameplaySettings.Instance;
                bool enablePresetSync = gameplaySettings?.EnablePresetSync ?? false;
                bool hasSyncedPresets = networkPlayer.HasSyncedPresets;
                
                if (enablePresetSync && hasSyncedPresets)
                {
                    // Apply the synced presets from the network player data
                    Debug.Log($"[GameManager] Applying synced presets for spectator {networkPlayer.PlayerName}");
                    yargPlayer.ApplySyncedPresets(
                        networkPlayer.CameraPresetId, networkPlayer.CameraPresetJson,
                        networkPlayer.HighwayPresetId, networkPlayer.HighwayPresetJson,
                        networkPlayer.ColorProfileId, networkPlayer.ColorProfileJson,
                        networkPlayer.ThemePresetId, networkPlayer.ThemePresetJson);
                }
                else
                {
                    // Use default presets (original behavior)
                    Debug.Log($"[GameManager] Using default presets for spectator {networkPlayer.PlayerName}");
                    yargPlayer.RefreshPresets();
                }
                
                // Get the prefab
                var prefab = gameMode switch
                {
                    GameMode.FiveFretGuitar => _fiveFretGuitarPrefab,
                    GameMode.SixFretGuitar  => _sixFretGuitarPrefab,
                    GameMode.FourLaneDrums  => _fourLaneDrumsPrefab,
                    GameMode.FiveLaneDrums  => _fiveLaneDrumsPrefab,
                    GameMode.ProKeys        => instrument is Instrument.ProKeys ? _proKeysPrefab : _fiveLaneKeysPrefab,
                    GameMode.ProGuitar      => _proGuitarPrefab,
                    _                       => null
                };
                
                if (prefab == null)
                {
                    Debug.LogWarning($"[GameManager] No prefab for spectator track GameMode {gameMode}");
                    continue;
                }
                
                highwayIndex++;
                
                // Instantiate the track
                var playerObject = Instantiate(prefab,
                    new Vector3(highwayIndex * TRACK_SPACING_X, 100f, 0f), prefab.transform.rotation);
                
                // Setup player
                var trackPlayer = playerObject.GetComponent<TrackPlayer>();
                var trackView = _trackViewManager.CreateTrackView(trackPlayer, yargPlayer);
                trackPlayer.Initialize(highwayIndex, yargPlayer, Chart, trackView, _mixer, null);
                
                // Add RemotePlayerSimulation to receive network state
                var simulation = playerObject.AddComponent<Player.RemotePlayerSimulation>();
                simulation.Initialize(trackPlayer, networkPlayer);
                trackPlayer.RegisterRemoteSimulation(simulation);
                trackPlayer.SetNetworkPlayerData(networkPlayer);
                
                // Set initial camera state for spectator track based on player's current fail state:
                // - If player is already failed, start with highway in LOWERED position
                // - If player is alive, start with highway in RAISED position
                // This ensures the track appears correctly from the first frame
                if (trackPlayer.TrackCameraPositioner != null)
                {
                    bool playerAlreadyFailed = networkPlayer.HasFailed;
                    trackPlayer.TrackCameraPositioner.SetInitialState(isRaised: !playerAlreadyFailed);
                    
                    // Also set the _didLowerTrack flag on the track player to match
                    // This prevents UpdateVisuals from trying to lower an already-lowered track
                    trackPlayer.SetInitialFailState(playerAlreadyFailed);
                    
                    Debug.Log($"[GameManager] Set initial camera state for {networkPlayer.PlayerName}: " +
                        $"isRaised={!playerAlreadyFailed}, hasFailed={playerAlreadyFailed}");
                }
                
                // Mark as spectator track for easy identification (include bandId for lookup)
                playerObject.name = $"SpectatorTrack_{bandId}_{networkPlayer.PlayerName}";
                
                // Start HIDDEN - will be revealed by RevealSpectatorTracks() after visual transition
                playerObject.SetActive(false);
                
                _players.Add(trackPlayer);
                // DON'T add to camera rendering yet - RevealSpectatorTracks will do that
                
                Debug.Log($"[GameManager] Created spectator track for {networkPlayer.PlayerName} ({gameMode}) " +
                    $"(hidden, initialFailed={networkPlayer.HasFailed})");
                createdCount++;
            }
            
            if (createdCount > 0)
            {
                Debug.Log($"[GameManager] Created {createdCount} spectator tracks for band '{bandInfo.DisplayName}' (hidden, pending reveal)");
            }
            
            // Update fail meter to show spectated band's happiness
            UpdateFailMeterForSpectate(bandId);
            
            return createdCount > 0;
        }
        
        /// <summary>
        /// Reveals spectator tracks that were created hidden.
        /// Called after the visual transition is ready to show tracks.
        /// </summary>
        public void RevealSpectatorTracks()
        {
            int revealedCount = 0;
            
            // Get all spectator tracks and sort by HighwayIndex to ensure cameras are added in correct order
            // This is critical for HUD alignment - camera list index must match HighwayIndex
            var spectatorTracks = _players
                .Where(p => p != null && p.gameObject.name.StartsWith("SpectatorTrack_") && !p.gameObject.activeSelf)
                .OfType<TrackPlayer>()
                .OrderBy(t => t.HighwayIndex)
                .ToList();
            
            foreach (var trackPlayer in spectatorTracks)
            {
                // Activate the track
                trackPlayer.gameObject.SetActive(true);
                
                // Add to camera rendering (order matters for HUD alignment)
                _trackViewManager._highwayCameraRendering.AddTrackPlayer(trackPlayer);
                
                revealedCount++;
            }
            
            if (revealedCount > 0)
            {
                // Update HUD scale for revealed tracks
                _trackViewManager.SetAllHUDScale();
                Debug.Log($"[GameManager] Revealed {revealedCount} spectator tracks");
            }
        }
        
        /// <summary>
        /// Updates the fail meter to display the spectated band's players and aggregate happiness.
        /// Called when entering spectate mode to show the correct band's state.
        /// </summary>
        /// <param name="bandId">The band ID being spectated.</param>
        public void UpdateFailMeterForSpectate(int bandId)
        {
            if (_failMeter == null)
            {
                return;
            }
            
            // Get the spectator tracks for this band
            var spectatorTracks = _players
                .Where(p => p != null && p.gameObject.name.StartsWith($"SpectatorTrack_{bandId}_"))
                .OfType<TrackPlayer>()
                .ToList();
            
            if (spectatorTracks.Count > 0)
            {
                _failMeter.EnterSpectateMode(spectatorTracks);
                Debug.Log($"[GameManager] Fail meter entered spectate mode for band {bandId} with {spectatorTracks.Count} players");
            }
            else
            {
                // No spectator tracks found, hide the fail meter
                _failMeter.SetActive(false);
                Debug.Log($"[GameManager] No spectator tracks found for band {bandId}, hiding fail meter");
            }
        }
        
        /// <summary>
        /// Restores the fail meter to normal mode (showing local players).
        /// </summary>
        public void RestoreFailMeterFromSpectate()
        {
            if (_failMeter != null)
            {
                _failMeter.ExitSpectateMode();
                Debug.Log("[GameManager] Fail meter restored from spectate mode");
            }
        }
        
        /// <summary>
        /// Removes all spectator tracks (call when switching spectated bands or ending spectate).
        /// </summary>
        /// <param name="restoreLocalTracks">Whether to show local tracks again after removing spectator tracks.</param>
        public void RemoveSpectatorTracks(bool restoreLocalTracks = false)
        {
            var spectatorTracks = _players
                .Where(p => p != null && p.gameObject.name.StartsWith("SpectatorTrack_"))
                .ToList();
            
            foreach (var track in spectatorTracks)
            {
                _players.Remove(track);
                
                if (track is TrackPlayer trackPlayer)
                {
                    _trackViewManager._highwayCameraRendering.RemoveTrackPlayer(trackPlayer);
                }
                
                Destroy(track.gameObject);
            }
            
            if (spectatorTracks.Count > 0)
            {
                Debug.Log($"[GameManager] Removed {spectatorTracks.Count} spectator tracks");
            }
            
            if (restoreLocalTracks)
            {
                ShowLocalTracks();
                
                // Restore fail meter from spectate mode
                RestoreFailMeterFromSpectate();
                
                // Re-enable fail meter if we have local players
                if (_failMeter != null && !IsNoFailActive && !IsPractice)
                {
                    _failMeter.SetActive(true);
                    Debug.Log("[GameManager] Restored fail meter after spectate mode");
                }
            }
        }
    }
}
