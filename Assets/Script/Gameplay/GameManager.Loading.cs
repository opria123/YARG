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
using YARG.Core.IO;
using YARG.Core.Logging;
using YARG.Core.Replays;
using YARG.Gameplay.Player;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YARG.Menu.Settings;
using YARG.Playback;
using YARG.Player;
using YARG.Scores;
using YARG.Settings;
using YARG.Song;
using YARG.Networking;

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
                
                // Shorter initial delay since NetworkPlayerData now has explicit DontDestroyOnLoad
                Debug.Log("[GameManager] Initial wait: 500ms for network object stabilization...");
                await Cysharp.Threading.Tasks.UniTask.Delay(500);
                
                // Create players from network data
                Debug.Log("[GameManager] Attempting to create multiplayer players...");
                YargPlayers = Menu.Multiplayer.MultiplayerPlayerManager.CreateMultiplayerPlayers();
                Debug.Log($"[GameManager] Multiplayer mode - created {YargPlayers.Count} players from network data");
                
                // If still no players, retry with shorter intervals (objects should exist now)
                int attempts = 0;
                while (YargPlayers.Count == 0 && attempts < 3)
                {
                    attempts++;
                    int delayMs = 300; // Shorter retry since DontDestroyOnLoad is explicit
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

                var networkManager = Networking.YargNetworkManager.Instance;
                if (networkManager != null && networkManager.IsHosting && Mirror.NetworkServer.active)
                {
                    networkManager.ResetBandFailureTracking();
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

            // Initialize song runner
            _songRunner = new SongRunner(
                _mixer,
                startTime: 0,
                SONG_START_DELAY,
                GlobalVariables.State.SongSpeed,
                audioCalibration,
                SettingsManager.Settings.VideoCalibration.Value,
                Song.SongOffsetSeconds);

            // Spawn players
            CreatePlayers();
            
            // Setup multiplayer unison synchronization after players are created
            SetupMultiplayerUnisonSync();

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

            if (SettingsManager.Settings.NoFailMode.Value || IsPractice)
            {
                _failMeter.SetActive(false);
            }

            // This is not an else because we still want to subscribe in case the user disables no fail during the song
            // We check in the callback to determine whether we should actually run the fail routine
            if (ReplayInfo == null || GlobalVariables.State.PlayingWithReplay)
            {
                EngineManager.OnSongFailed += OnSongFailed;

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

            var networkManager = Networking.YargNetworkManager.Instance;
            if (networkManager == null || !networkManager.isNetworkActive)
            {
                return;
            }

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, this.GetCancellationTokenOnDestroy());

            try
            {
                await networkManager.WaitForMultiplayerGameplayStartAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                if (timeoutCts.IsCancellationRequested)
                {
                    Debug.LogWarning("[GameManager] Timed out waiting for all players to finish loading. Proceeding with song start.");
                    networkManager.ForceCompleteGameplayStartBarrier();
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

                    if (!player.IsReplay)
                    {
                        // Don't do this if it's a replay, because the replay
                        // would've already set its own presets at this point
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
                        var trackView = _trackViewManager.CreateTrackView(trackPlayer, player);
                        trackPlayer.Initialize(highwayIndex, player, Chart, trackView, _mixer, lastHighScore);

                        _players.Add(trackPlayer);
                        _trackViewManager._highwayCameraRendering.AddTrackPlayer(trackPlayer);
                        
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
                // Set the hud scale (position is handled by TrackPlayer)
                _trackViewManager.SetAllHUDScale();
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
            
            // For now, we only count local players for unison tracking
            // In a more complex scenario, we'd need to track remote player instruments
            // and filter out vocals from those too
            // TODO: Get instrument info from NetworkPlayerData and filter accordingly
            
            // Check if we're in multiplayer and get the total non-vocal player count
            var liteNetAdapter = Networking.Abstraction.NetworkingServiceFactory.Instance as Networking.Abstraction.LiteNetNetworkingAdapter;
            if (liteNetAdapter != null && liteNetAdapter.IsNetworkActive)
            {
                // In multiplayer, we assume all network players are non-vocal for now
                // This is a simplification - ideally we'd track instruments per network player
                var allPlayers = liteNetAdapter.GetAllPlayers();
                int totalCount = allPlayers?.Count ?? localNonVocalCount;
                
                // If all our local players are vocals, return 0 (no unison participants)
                if (localNonVocalCount == 0)
                {
                    YargLogger.LogInfo("[GameManager] All local players are vocals - no unison participation");
                    return 0;
                }
                
                return totalCount;
            }
            
            // Check Mirror
            if (Networking.YargNetworkManager.Instance != null && Networking.YargNetworkManager.Instance.isNetworkActive)
            {
                var allPlayers = Networking.YargNetworkManager.Instance.GetAllPlayers();
                int totalCount = allPlayers?.Count ?? localNonVocalCount;
                
                if (localNonVocalCount == 0)
                {
                    YargLogger.LogInfo("[GameManager] All local players are vocals - no unison participation");
                    return 0;
                }
                
                return totalCount;
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

            // Try Mirror first
            var manager = Networking.YargNetworkManager.Instance;
            if (manager != null && manager.isNetworkActive)
            {
                var networkPlayers = manager.GetAllPlayers();
                
                var result = FindNetworkPlayerData(networkPlayers, player, playerIndex, expectsLocalData);
                if (result != null)
                {
                    return result;
                }
            }
            
            // Try LiteNet if Mirror didn't work
            var liteNetAdapter = Networking.Abstraction.NetworkingServiceFactory.Instance as Networking.Abstraction.LiteNetNetworkingAdapter;
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
    }
}
