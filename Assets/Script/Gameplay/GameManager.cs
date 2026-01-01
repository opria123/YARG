using System;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.InputSystem;
using YARG.Core.Audio;
using YARG.Core.Chart;
using YARG.Core.Engine;
using YARG.Core.Engine.Drums;
using YARG.Core.Engine.Guitar;
using YARG.Core.Engine.Keys;
using YARG.Core.Engine.Vocals;
using YARG.Core.Game;
using YARG.Core.Input;
using YARG.Core.Logging;
using YARG.Core.Replays;
using YARG.Core.Song;
using YARG.Gameplay.HUD;
using YARG.Gameplay.Player;
using YARG.Integration;
using YARG.Menu.Navigation;
using YARG.Menu.Multiplayer;
using YARG.Menu.Persistent;
using YARG.Menu.ScoreScreen;
using YARG.Networking.Abstraction;
using YARG.Networking.Gameplay;
using YARG.Playback;
using YARG.Player;
using YARG.Replays;
using YARG.Scores;
using YARG.Settings;
using YARG.Venue.Characters;
using YARG.Venue.VenueCamera;

namespace YARG.Gameplay
{
    [DefaultExecutionOrder(-1)]
    public partial class GameManager : MonoBehaviour
    {
        private static GameManager _instance;
        public const double SONG_START_DELAY = SongRunner.SONG_START_DELAY;
        public const double SONG_END_DELAY = SONG_START_DELAY;

        public const float TRACK_SPACING_X = 100f;

        public bool IsSeekingReplay;

        [Header("References")]
        [SerializeField]
        private TrackViewManager _trackViewManager;
        [SerializeField]
        private ReplayController _replayController;
        [SerializeField]
        private PauseMenuManager _pauseMenu;
        [SerializeField]
        private DraggableHudManager _draggableHud;

        [SerializeField]
        private GameObject _lyricBar;

        [SerializeField]
        private FailMeter _failMeter;

        [field: SerializeField]
        public VocalTrack VocalTrack { get; private set; }

        /// <summary>
        /// Equal to either <see cref="PlayerContainer.Players"/> or the players in the replay.
        /// </summary>
        public IReadOnlyList<YargPlayer> YargPlayers { get; private set;}

        private List<BasePlayer> _players;

        public int TotalPlayers => _players.Count;

        public bool IsSongStarted { get; private set; } = false;

        private SongRunner _songRunner;

        /// <remarks>
        /// This is not initialized on awake, but rather, in
        /// <see cref="GameplayBehaviour.OnChartLoaded"/>.
        /// </remarks>
        public BeatEventHandler BeatEventHandler { get;    private set; }
        public CrowdEventHandler CrowdEventHandler  { get; private set; }
        public CameraManager     VenueCameraManager { get; private set; }
        public CharacterManager  VenueCharacterManager { get; private set; }

        public PracticeManager  PracticeManager  { get; private set; }
        public BackgroundManager BackgroundManager { get; private set; }
        public EngineManager EngineManager { get; private set; }

        public SongEntry Song  { get; private set; }
        public SongChart    Chart { get; private set; }

        // For clarity, try to avoid using these properties inside GameManager itself
        // These are just to expose properties from the song runner to the outside
        /// <inheritdoc cref="SongRunner.SongTime"/>
        public double SongTime => _songRunner.SongTime;

        /// <inheritdoc cref="SongRunner.AudioTime"/>
        public double AudioTime => _songRunner.AudioTime;

        /// <inheritdoc cref="SongRunner.VisualTime"/>
        public double VisualTime => _songRunner.VisualTime;

        /// <inheritdoc cref="SongRunner.InputTime"/>
        public double InputTime => _songRunner.InputTime;

        /// <inheritdoc cref="SongRunner.SongSpeed"/>
        public float SongSpeed => _songRunner.SongSpeed;

        /// <inheritdoc cref="SongRunner.Started"/>
        public bool Started => _songRunner.Started;

        /// <inheritdoc cref="SongRunner.Paused"/>
        public bool Paused => _songRunner.Paused;

        public double SongLength { get; private set; }

        public bool IsPractice      { get; private set; }

        /// <summary>
        /// Gets whether No Fail mode is currently active.
        /// In multiplayer, uses the session's NoFail setting; otherwise uses local settings.
        /// </summary>
        public bool IsNoFailActive
        {
            get
            {
                // In multiplayer, use session settings (host-controlled)
                if (IsMultiplayerActive())
                {
                    var multiplayerSettings = MultiplayerGameplaySettings.Instance;
                    if (multiplayerSettings != null)
                    {
                        return multiplayerSettings.NoFailMode;
                    }
                }
                
                // Fall back to local settings
                return SettingsManager.Settings.NoFailMode.Value;
            }
        }

        public int BandScore
        {
            get => EngineManager.Score;
            set => EngineManager.Score = value;
        }

        public int BandCombo
        {
            get => EngineManager.Combo;
            set => EngineManager.Combo = value;
        }

        public float BandStars
        {
            get => EngineManager.Stars;
            set => EngineManager.Stars = value;
        }

        public int   BandMultiplier => EngineManager.BandMultiplier;

        public double FirstNoteTime { get; private set; }
        public double LastNoteTime  { get; private set; }

        public ReplayInfo ReplayInfo { get; private set; }
        public ReplayData ReplayData { get; private set; }

        public IReadOnlyList<BasePlayer> Players => _players;

        public int StarPowerActivations { get; private set; } = 0;

        private bool _isReplaySaved;

        private int _originalSleepTimeout;

        private StemMixer _mixer;

        private List<double> _frameTimes;

        public bool PlayingAShow => GlobalVariables.State.PlayingAShow;
        public int  ShowIndex = 0;

        private BandComboType _bandComboType;
        private MultiplayerGameplaySync _multiplayerSync;
        private MultiplayerUnisonSync _multiplayerUnisonSync;
        
        // Track remote player Star Power states for revival detection
        private Dictionary<string, bool> _remotePlayerStarPowerStates = new();

        private void Awake()
        {
            // MULTIPLAYER FIX: Singleton pattern to prevent duplicate GameManagers
            if (_instance != null && _instance != this)
            {
                YargLogger.LogWarning("[GameManager] Duplicate GameManager detected! Destroying this duplicate.");
                DestroyImmediate(gameObject);
                return;
            }
            _instance = this;

            // Set references
            PracticeManager = GetComponent<PracticeManager>();
            BackgroundManager = GetComponent<BackgroundManager>();
            EngineManager = new EngineManager();
            YargLogger.LogFormatInfo("[GameManager] Created new EngineManager with hash: {0}", EngineManager.GetHashCode());

            // Check if we're in multiplayer mode (LiteNet)
            bool isMultiplayer = NetworkingServiceFactory.Instance?.IsNetworkActive == true;

            if (isMultiplayer)
            {
                // In multiplayer, mark that we need to create players in Start() after network objects spawn
                // Initialize multiplayer sync components now
                _multiplayerSync = gameObject.AddComponent<MultiplayerGameplaySync>();
                _multiplayerUnisonSync = gameObject.AddComponent<MultiplayerUnisonSync>();
                Debug.Log($"[GameManager] Multiplayer sync components added - will create players in Start() (LiteNet: {isMultiplayer})");
                
                // Reset band failure states for new song
                var bandManager = Networking.Bands.BandManager.Instance;
                if (bandManager != null)
                {
                    bandManager.ResetFailureStates();
                }
                
                // Register LiteNet event handlers for multiplayer gameplay
                var liteNetAdapter = NetworkingServiceFactory.Instance as LiteNetNetworkingAdapter;
                if (liteNetAdapter != null)
                {
                    liteNetAdapter.OnRestartGameplayRequested += OnLiteNetRestartGameplayRequested;
                    liteNetAdapter.OnPlayerLeftDuringGameplay += OnLiteNetPlayerLeftDuringGameplay;
                    liteNetAdapter.OnQuitToLibraryRequested += OnLiteNetQuitToLibraryRequested;
                    
                    // Host needs to handle player disconnects directly (OnPlayerLeftDuringGameplay is only for clients)
                    if (liteNetAdapter.IsHosting)
                    {
                        liteNetAdapter.OnPlayerLeft += OnLiteNetPlayerLeft;
                        Debug.Log("[GameManager] Registered OnPlayerLeft handler for host");
                    }
                    
                    Debug.Log("[GameManager] Registered event handlers for multiplayer (LiteNet)");
                }
            }
            else
            {
                // In single player, use PlayerContainer as normal
                YargPlayers = PlayerContainer.Players;
            }

            Song = GlobalVariables.State.CurrentSong;
            ReplayInfo = GlobalVariables.State.CurrentReplay;
            IsPractice = GlobalVariables.State.IsPractice && ReplayInfo == null;
            _bandComboType = SettingsManager.Settings.BandComboTypeSetting.Value;

            // Check if Navigator still exists (might be destroyed during scene transition)
            if (Navigator.Instance != null)
            {
                Navigator.Instance.PopAllSchemes();
            }
            GameStateFetcher.SetSongEntry(Song);

            if (Song is null)
            {
                YargLogger.LogError("Null song set when loading gameplay!");

                GlobalVariables.Instance.LoadScene(SceneIndex.Menu);
                return;
            }

            // Hide vocals track (will be shown when players are initialized)
            VocalTrack.gameObject.SetActive(false);

            // Prevent screen from sleeping
            _originalSleepTimeout = Screen.sleepTimeout;
            Screen.sleepTimeout = SleepTimeout.NeverSleep;

            // Update countdown display style from global settings
            CountdownDisplay.DisplayStyle = SettingsManager.Settings.CountdownDisplay.Value;

            _frameTimes = new List<double>();
        }

        private void OnDestroy()
        {
            YargLogger.LogInfo("Exiting song");

            if (Navigator.Instance != null)
            {
                Navigator.Instance.NavigationEvent -= OnNavigationEvent;
            }

            // Unsubscribe from LiteNet events
            var liteNetAdapter = NetworkingServiceFactory.Instance as LiteNetNetworkingAdapter;
            if (liteNetAdapter != null)
            {
                liteNetAdapter.OnRestartGameplayRequested -= OnLiteNetRestartGameplayRequested;
                liteNetAdapter.OnPlayerLeftDuringGameplay -= OnLiteNetPlayerLeftDuringGameplay;
                liteNetAdapter.OnQuitToLibraryRequested -= OnLiteNetQuitToLibraryRequested;
                liteNetAdapter.OnPlayerLeft -= OnLiteNetPlayerLeft;
            }

            // Unsubscribe from other events (null checks for duplicate GameManager case)
            if (SettingsManager.Settings?.NoFailMode != null)
            {
                SettingsManager.Settings.NoFailMode.OnChange -= OnNoFailModeChanged;
            }
            if (EngineManager != null)
            {
                EngineManager.OnSongFailed -= OnSongFailed;
                EngineManager.OnPlayerFailed -= OnPlayerFailed;
                EngineManager.OnPlayerRevived -= OnPlayerRevived;
                EngineManager.OnUnisonPhraseHit -= OnUnisonPhraseHit;
            }

            //Restore stem volumes to their original state
            if (_stemStates != null)
            {
                foreach (var (stem, state) in _stemStates)
                {
                    GlobalAudioHandler.SetVolumeSetting(stem, state.Volume);
                }
            }

            DisposeDebug();
            _pauseMenu?.PopAllMenus();
            _mixer?.Dispose();
            _songRunner?.Dispose();
            BackgroundManager?.Dispose();
            CrowdEventHandler?.Dispose();
            
            // Clear Star Power tracking state
            _remotePlayerStarPowerStates?.Clear();

            // Reset the time scale back, as it would be 0 at this point (because of pausing)
            Time.timeScale = 1f;

            // Reset sleep timeout setting
            Screen.sleepTimeout = _originalSleepTimeout;
            
            // Reset late-join spectate state so we don't stay in spectate mode
            GlobalVariables.State.IsSpectating = false;
            GlobalVariables.State.SpectateStartTime = 0;
        }

        private void Update()
        {
            // Pause/unpause
            if (Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                // Allow pause during spectate mode in multiplayer (so players can leave lobby)
                // but block pause when player has failed in single player (handled by fail screen)
                bool canPause = (!IsPractice || PracticeManager.HasSelectedSection) &&
                    !DialogManager.Instance.IsDialogShowing;
                
                // Check if we're in multiplayer (LiteNet)
                bool isMultiplayer = NetworkingServiceFactory.Instance?.IsNetworkActive == true;
                
                // In single player, don't allow pause if failed (fail screen handles it)
                // In multiplayer, allow pause even if failed/spectating so player can leave
                if (!isMultiplayer && PlayerHasFailed)
                {
                    canPause = false;
                }
                
                if (canPause)
                {
                    if (isMultiplayer)
                    {
                        // In multiplayer, pause menu shows but song keeps playing
                        if (_pauseMenu.IsOpen)
                        {
                            // Close pause menu
                            _pauseMenu.PopAllMenus();
                        }
                        else
                        {
                            // Show pause menu but keep song playing
                            // Use FailPause if spectating (PlayerHasFailed + _isSpectating)
                            // so the correct buttons are shown (Leave Lobby vs Resume)
                            PauseCore(showMenu: true, freezeGameplay: false);
                        }
                    }
                    else
                    {
                        // Single player - normal pause behavior
                        SetPaused(!_pauseMenu.IsOpen);
                    }
                }
            }

            // Toggle debug text
            if (Keyboard.current.ctrlKey.isPressed && Keyboard.current.tabKey.wasPressedThisFrame)
            {
                ToggleDebugEnabled();
            }

            // Skip the rest if paused or not initialized yet
            if (_songRunner == null || _songRunner.Paused)
            {
                return;
            }

            // Update handlers
            _songRunner.Update();
            BeatEventHandler.Update(_songRunner.SongTime, _songRunner.VisualTime);
            CrowdEventHandler.Update(_songRunner.SongTime);

            // Update players (skip if not initialized yet)
            if (_players == null || _players.Count == 0)
            {
                return;
            }
            
            int totalScore = 0;
            float totalStars = 0f;
            int activePlayerCount = 0;
            
            // Copy the list to avoid collection modification during iteration
            // (spectate mode can add/remove tracks while Update runs)
            var playersCopy = _players.ToList();
            foreach (var player in playersCopy)
            {
                // Skip null/destroyed players
                if (player == null || player.gameObject == null)
                {
                    continue;
                }
                
                // Skip update for inactive players (hidden during spectate mode)
                if (!player.gameObject.activeSelf)
                {
                    continue;
                }
                
                player.GameplayUpdate();

                totalScore += player.Score;
                totalScore += player.BandBonusScore;
                totalStars += player.Stars;
                activePlayerCount++;
            }

            if (GlobalVariables.VerboseReplays)
            {
                _frameTimes.Add(_songRunner.InputTime);
            }

            BandScore = totalScore;
            // Use active player count for average to handle spectate mode correctly
            BandStars = activePlayerCount > 0 ? totalStars / activePlayerCount : 0f;
            
            // Check for remote player Star Power activations (for revival)
            CheckRemoteStarPowerActivations();
            
            SendMultiplayerSnapshot();
            
            // Send band score updates in multiplayer band mode
            SendBandScoreUpdate();

            // End song if needed (required for the [end] event)
            if (_songRunner.SongTime >= SongLength)
            {
                if (EndSong())
                {
                    return;
                }
            }
        }
        
        // Band score sync tracking
        private float _lastBandScoreSyncTime;
        private const float BAND_SCORE_SYNC_INTERVAL = 0.5f; // Sync every 500ms
        
        // Track final band score when entering spectate mode (so we can still send it to network)
        private int _savedBandScoreForNetwork = 0;
        private bool _hasSavedBandScore = false;
        
        // Track local band score/stars for score screen (preserved when entering spectate mode)
        // These are separate from the network sync values because they need to persist to the score screen
        private int _localBandScoreForScoreScreen = 0;
        private float _localBandStarsForScoreScreen = 0f;
        private bool _hasLocalBandScoreSaved = false;
        
        /// <summary>
        /// Sends band score updates to network in multiplayer band mode.
        /// </summary>
        private void SendBandScoreUpdate()
        {
            var networkService = NetworkingServiceFactory.Instance;
            if (networkService == null || !networkService.IsNetworkActive)
                return;
            
            var bandManager = Networking.Bands.BandManager.Instance;
            if (bandManager == null || !bandManager.IsBandSystemActive)
                return;
            
            // Throttle updates
            if (Time.time - _lastBandScoreSyncTime < BAND_SCORE_SYNC_INTERVAL)
                return;
            
            _lastBandScoreSyncTime = Time.time;
            
            int localBandId = bandManager.LocalPlayerBandId;
            if (localBandId < 0)
                return;
            
            // Use saved score if we're in spectate mode, otherwise use current band score
            // This ensures we keep sending our final score even after entering spectate mode
            int scoreToSend = _hasSavedBandScore ? _savedBandScoreForNetwork : BandScore;
            
            // Send our band's score
            if (networkService is LiteNetNetworkingAdapter liteNetAdapter)
            {
                liteNetAdapter.SendBandScoreUpdate(localBandId, scoreToSend);
            }
        }
        
        /// <summary>
        /// Sends the final band score at song end to ensure all players have accurate band standings.
        /// This is separate from the throttled SendBandScoreUpdate to guarantee final score sync.
        /// </summary>
        private void SendFinalBandScore()
        {
            var networkService = NetworkingServiceFactory.Instance;
            if (networkService == null || !networkService.IsNetworkActive)
                return;
            
            var bandManager = Networking.Bands.BandManager.Instance;
            if (bandManager == null || !bandManager.IsBandSystemActive)
                return;
            
            int localBandId = bandManager.LocalPlayerBandId;
            if (localBandId < 0)
                return;
            
            // Use saved score if we were spectating, otherwise use current band score
            int finalScore = _hasSavedBandScore ? _savedBandScoreForNetwork : BandScore;
            
            // Update local BandManager with final score
            bandManager.UpdateBandScore(localBandId, finalScore);
            
            // Send final score to network
            if (networkService is LiteNetNetworkingAdapter liteNetAdapter)
            {
                // Send as reliable to ensure it arrives
                liteNetAdapter.BroadcastBandScoreUpdate(localBandId, finalScore, isFinal: true, excludeConnection: null);
                YargLogger.LogDebug($"[GameManager] Sent final band score: BandId={localBandId}, Score={finalScore}");
            }
        }
        
        /// <summary>
        /// Monitors remote player Star Power activations and triggers revival when detected.
        /// This allows remote players' Star Power to revive failed band members.
        /// When band mode is active, only triggers if the activating player is in the same band.
        /// </summary>
        private void CheckRemoteStarPowerActivations()
        {
            // Only check in multiplayer with No Fail enabled
            var networkService = NetworkingServiceFactory.Instance;
            if (networkService == null || !networkService.IsNetworkActive)
                return;
                
            if (!IsNoFailActive)
                return;
                
            // Get all network players
            var networkPlayers = networkService.GetAllPlayers();
            if (networkPlayers == null || networkPlayers.Count == 0)
                return;
            
            // Check if band mode is active
            var bandManager = Networking.Bands.BandManager.Instance;
            bool isBandModeActive = bandManager != null && bandManager.IsBandSystemActive;
            int localBandId = bandManager?.LocalPlayerBandId ?? -1;
                
            foreach (var networkPlayer in networkPlayers)
            {
                // Skip local players (their SP is handled by ChangeStarPowerStatus)
                if (networkPlayer.IsLocalUser)
                    continue;
                
                // In band mode, skip players not in our band
                if (isBandModeActive)
                {
                    Guid playerGuid = networkPlayer.NetworkPlayerId;
                    if (playerGuid != Guid.Empty)
                    {
                        int playerBandId = bandManager.GetPlayerBandId(playerGuid);
                        if (playerBandId != localBandId)
                        {
                            continue; // Different band, ignore their Star Power
                        }
                    }
                }
                    
                string playerId = networkPlayer.NetworkPlayerId != Guid.Empty 
                    ? networkPlayer.NetworkPlayerId.ToString() 
                    : networkPlayer.PlayerName;
                bool currentStarPower = networkPlayer.IsStarPowerActive;
                
                // Check if we have a previous state for this player
                if (_remotePlayerStarPowerStates.TryGetValue(playerId, out bool wasActive))
                {
                    // Star Power just activated (was off, now on)
                    if (!wasActive && currentStarPower)
                    {
                        Debug.Log($"[GameManager] Remote player {networkPlayer.PlayerName} activated Star Power - checking for revival");
                        TryReviveFailedPlayersWithStarPower();
                    }
                }
                
                // Update tracked state
                _remotePlayerStarPowerStates[playerId] = currentStarPower;
            }
        }

        private void SendMultiplayerSnapshot(bool forceSend = false)
        {
            if (_multiplayerSync == null || _players == null || _players.Count == 0 || _songRunner == null)
            {
                return;
            }

            // DEBUG: Log all players and their scores periodically
            if (UnityEngine.Random.value < 0.01f) // Log ~1% of calls
            {
                var debugInfo = new System.Text.StringBuilder();
                debugInfo.Append($"[SendMultiplayerSnapshot DEBUG] _players.Count={_players.Count}, ");
                for (int i = 0; i < _players.Count; i++)
                {
                    var p = _players[i];
                    bool hasBindings = p.Player?.Bindings != null;
                    bool isReplay = p.Player?.IsReplay ?? true;
                    debugInfo.Append($"Player{i}=[Name={p.Player?.Profile?.Name ?? "NULL"}, Score={p.Score}, HasBindings={hasBindings}, IsReplay={isReplay}, Active={p.gameObject.activeSelf}] ");
                }
                YargLogger.LogInfo(debugInfo.ToString());
            }

            double songTime = _songRunner.SongTime;
            
            // Check if we're using LiteNet (which handles player locality differently)
            bool useLiteNet = Networking.Abstraction.NetworkingServiceFactory.Instance?.IsNetworkActive == true;
            
            // Use Unity's realtime clock for network time
            double clientNetworkTime = Time.realtimeSinceStartupAsDouble;
            
            int localPlayerCount = 0;
            foreach (var player in _players)
            {
                var networkData = player.NetworkPlayerData;
                
                // Determine if this is a local player
                // A local player is one with bindings (can receive input)
                bool isLocalPlayer;
                if (useLiteNet)
                {
                    // For LiteNet, a local player is one with bindings (can receive input)
                    isLocalPlayer = player.Player?.Bindings != null && !player.Player.IsReplay;
                }
                else
                {
                    // In single player, all players are local
                    isLocalPlayer = true;
                }
                
                if (!isLocalPlayer)
                {
                    continue;
                }
                
                localPlayerCount++;

                var baseStats = player.BaseStats;

                float starPowerAmount = 0f;
                uint gaugeTicks = player.BaseEngine != null ? player.BaseEngine.TicksPerFullSpBar : 0u;
                if (gaugeTicks > 0)
                {
                    starPowerAmount = Mathf.Clamp01((float) baseStats.StarPowerTickAmount / gaugeTicks);
                }
                else if (baseStats.TotalStarPowerTicks > 0)
                {
                    starPowerAmount = Mathf.Clamp01((float) baseStats.StarPowerTickAmount / baseStats.TotalStarPowerTicks);
                }

                var trackPlayer = player as TrackPlayer;

                int notesMissed = trackPlayer != null
                    ? Mathf.Max(0, trackPlayer.GetResolvedMissCount())
                    : Mathf.Max(0, player.TotalNotes - player.NotesHit);

                int overstrums = 0;
                int hoposStrummed = 0;
                int overhits = 0;
                int ghostInputs = 0;
                int ghostsHit = 0;
                int accentsHit = 0;
                int dynamicsBonus = 0;
                int bandBonusScore = Mathf.Max(0, player.BandBonusScore);
                int vocalsTicksHit = 0;
                int vocalsTicksMissed = 0;
                float vocalsPhraseTicksHit = 0f;
                int vocalsPhraseTicksTotal = 0;

                switch (baseStats)
                {
                    case GuitarStats guitarStats:
                        overstrums = Mathf.Max(0, guitarStats.Overstrums);
                        hoposStrummed = Mathf.Max(0, guitarStats.HoposStrummed);
                        ghostInputs = Mathf.Max(0, guitarStats.GhostInputs);
                        break;
                    case DrumsStats drumsStats:
                        overhits = Mathf.Max(0, drumsStats.Overhits);
                        ghostsHit = Mathf.Max(0, drumsStats.GhostsHit);
                        accentsHit = Mathf.Max(0, drumsStats.AccentsHit);
                        dynamicsBonus = Mathf.Max(0, drumsStats.DynamicsBonus);
                        break;
                    case KeysStats keysStats:
                        overhits = Mathf.Max(0, keysStats.Overhits);
                        break;
                    case VocalsStats vocalsStats:
                        vocalsTicksHit = (int)Math.Min(vocalsStats.TicksHit, (uint)int.MaxValue);
                        vocalsTicksMissed = (int)Math.Min(vocalsStats.TicksMissed, (uint)int.MaxValue);
                        if (player is VocalsPlayer vocalsPlayer && vocalsPlayer.Engine != null)
                        {
                            var phraseTotal = vocalsPlayer.Engine.PhraseTicksTotal;
                            if (phraseTotal.HasValue && phraseTotal.Value > 0)
                            {
                                vocalsPhraseTicksTotal = Mathf.Clamp((int)phraseTotal.Value, 0, int.MaxValue);
                                float clampedHit = Mathf.Clamp((float)vocalsPlayer.Engine.PhraseTicksHit, 0f,
                                    vocalsPhraseTicksTotal > 0 ? vocalsPhraseTicksTotal : float.MaxValue);
                                vocalsPhraseTicksHit = clampedHit;
                            }
                            else
                            {
                                vocalsPhraseTicksHit = 0f;
                                vocalsPhraseTicksTotal = 0;
                            }
                        }
                        break;
                }

                bool soloActive = false;
                int soloSequence = -1;
                int soloNoteCount = 0;
                int soloNotesHit = 0;
                int soloLastBonus = 0;
                int soloTotalBonus = Mathf.Max(0, baseStats.SoloBonuses);

                if (trackPlayer != null)
                {
                    var soloSnapshot = trackPlayer.GetSoloSyncSnapshot();
                    soloActive = soloSnapshot.IsActive;
                    soloSequence = soloSnapshot.Sequence;
                    soloNoteCount = soloSnapshot.NoteCount;
                    soloNotesHit = soloSnapshot.NotesHit;
                    soloLastBonus = soloSnapshot.LastBonus;
                    soloTotalBonus = soloSnapshot.TotalBonus;
                }

                // Collect sustain and whammy state for guitar players
                int sustainsHeld = 0;
                float whammyValue = 0f;
                if (player is FiveFretGuitarPlayer fiveFretPlayer)
                {
                    sustainsHeld = fiveFretPlayer.SustainsHeldBitmask;
                    whammyValue = fiveFretPlayer.WhammyFactor;
                }

                // Get happiness and fail state from the player's engine container
                float happiness = 1.0f;
                bool hasFailed = false;
                var engineContainer = player.PlayerEngineContainer;
                if (engineContainer != null)
                {
                    happiness = engineContainer.Happiness;
                    hasFailed = engineContainer.HasFailed;
                    
                    // Ensure consistent state: if happiness is at fail threshold, hasFailed should be true
                    // This prevents race conditions where happiness drops but HasFailed hasn't been set yet
                    if (happiness <= 0f && !hasFailed)
                    {
                        hasFailed = true;
                    }
                    
                    // In NoFail mode, never report the player as failed over the network.
                    // The engine internally may still track fail state (for UI purposes when NoFail
                    // is toggled mid-song), but we shouldn't broadcast this to other players.
                    // This prevents the bug where changing NoFail setting between songs doesn't
                    // actually prevent failing in subsequent songs.
                    if (IsNoFailActive && hasFailed)
                    {
                        hasFailed = false;
                    }
                }

                // DEBUG: Log player score details for network sync investigation
                if (localPlayerCount == 1 && UnityEngine.Random.value < 0.02f) // Log ~2% of snapshots
                {
                    YargLogger.LogInfo($"[SendMultiplayerSnapshot DEBUG] Player={player.Player?.Profile?.Name ?? "NULL"}, " +
                        $"Score={player.Score}, Combo={player.Combo}, NotesHit={player.NotesHit}, " +
                        $"BaseEngine={player.BaseEngine?.GetType().Name ?? "NULL"}, " +
                        $"TotalScore={player.BaseEngine?.BaseStats?.TotalScore ?? -1}, " +
                        $"gameObject.activeSelf={player.gameObject.activeSelf}");
                }

                _multiplayerSync.SubmitLocalSnapshot(networkData, player.Score, player.Combo, baseStats.MaxCombo,
                    baseStats.IsStarPowerActive, starPowerAmount, baseStats.StarPowerPhrasesHit,
                    baseStats.TotalStarPowerPhrases, player.NotesHit, notesMissed, overstrums, hoposStrummed,
                    overhits, ghostInputs, ghostsHit, accentsHit, dynamicsBonus, bandBonusScore, vocalsTicksHit,
                    vocalsTicksMissed, vocalsPhraseTicksHit, vocalsPhraseTicksTotal, soloActive, soloSequence,
                    soloNoteCount, soloNotesHit, soloLastBonus, soloTotalBonus, sustainsHeld, whammyValue,
                    player.Stars, songTime, clientNetworkTime, happiness, hasFailed, forceSend);
            }
        }

        public void SetSongTime(double time, double delayTime = SONG_START_DELAY)
        {
            _songRunner.SetSongTime(time, delayTime);

            BeatEventHandler.Reset();
            BackgroundManager.SetTime(_songRunner.SongTime + Song.SongOffsetSeconds);
            VenueCameraManager?.ResetTime(time);
            VenueCharacterManager?.ResetTime(time);
        }

        public void SetSongSpeed(float speed)
        {
            _songRunner.SetSongSpeed(speed);

            BackgroundManager.SetSpeed(_songRunner.SongSpeed);
        }

        public int GetMixerFFTData(float[] buffer, int fftSize, bool complex)
        {
            return _mixer.GetFFTData(buffer, fftSize, complex);
        }

        public int GetMixerSampleData(float[] buffer)
        {
            return _mixer.GetSampleData(buffer);
        }

        public void AdjustSongSpeed(float deltaSpeed)
        {
            _songRunner.AdjustSongSpeed(deltaSpeed);

            // Only scale the player speed in practice
            if (IsPractice && _songRunner.SongSpeed >= 1)
            {
                // Scale only if the speed is greater than 1
                var speed = _songRunner.SongSpeed >= 1 ? _songRunner.SongSpeed : 1;
                foreach (var player in _players)
                {
                    player.BaseEngine.SetSpeed(speed);
                }
            }

            BackgroundManager.SetSpeed(_songRunner.SongSpeed);
        }

        public void Pause(bool showMenu = true)
        {
            // Check if we're in LiteNet multiplayer - if so, don't actually pause gameplay
            bool isLiteNetMultiplayer = NetworkingServiceFactory.Instance?.IsNetworkActive == true;
            
            if (isLiteNetMultiplayer)
            {
                // In multiplayer, show the pause menu but don't stop gameplay
                // This keeps the song synced across all players
                PauseCore(showMenu, freezeGameplay: false);
            }
            else
            {
                _songRunner.Pause();
                PauseCore(showMenu);
            }
        }

        private void PauseCore(bool showMenu, bool freezeGameplay = true)
        {
            if (showMenu)
            {
                // Check if we're in multiplayer (LiteNet)
                bool isMultiplayer = NetworkingServiceFactory.Instance?.IsNetworkActive == true;
                
                if (!GlobalVariables.State.PlayingWithReplay && ReplayInfo != null)
                {
                    _pauseMenu.PushMenu(PauseMenuManager.Menu.ReplayPause);
                }
                else if (PlayerHasFailed)
                {
                    _pauseMenu.PushMenu(PauseMenuManager.Menu.FailPause);
                }
                else if (isMultiplayer)
                {
                    // Multiplayer pause takes priority over practice/setlist modes
                    // Fall back to QuickPlayPause if MultiplayerPause isn't configured in scene
                    try
                    {
                        _pauseMenu.PushMenu(PauseMenuManager.Menu.MultiplayerPause);
                    }
                    catch (System.InvalidOperationException)
                    {
                        YargLogger.LogWarning("[GameManager] MultiplayerPause menu not found, using QuickPlayPause");
                        _pauseMenu.PushMenu(PauseMenuManager.Menu.QuickPlayPause);
                    }
                }
                else if (IsPractice)
                {
                    _pauseMenu.PushMenu(PauseMenuManager.Menu.PracticePause);
                }
                else if (GlobalVariables.State.PlayingAShow)
                {
                    _pauseMenu.PushMenu(PauseMenuManager.Menu.SetlistPause);
                }
                else
                {
                    _pauseMenu.PushMenu(PauseMenuManager.Menu.QuickPlayPause);
                }
            }

            if (freezeGameplay)
            {
                // Pause the background/venue
                Time.timeScale = 0f;
                BackgroundManager.SetPaused(true);
                GameStateFetcher.SetPaused(true);

                // Pause any audio samples that are currently playing
                GlobalAudioHandler.PauseAllSfx();

                // Allow sleeping
                Screen.sleepTimeout = _originalSleepTimeout;
            }
        }

        public bool PlayerHasFailed { get; set; } = false;

        private bool _multiplayerFailureReported;

        public void Resume()
        {
            _songRunner.Resume();
            ResumeCore();
        }

        public void ResumeCore()
        {
            if (_draggableHud.EditMode)
            {
                SetEditHUD(false);
            }

            _pauseMenu.PopAllMenus();
            if (_songRunner.SongTime >= SongLength + SONG_END_DELAY)
            {
                return;
            }

            // Unpause the background/venue
            Time.timeScale = 1f;
            BackgroundManager.SetPaused(false);
            GameStateFetcher.SetPaused(false);

            // Unpause any audio samples that are currently playing
            GlobalAudioHandler.ResumeAllSfx();

            // Disallow sleeping
            Screen.sleepTimeout = SleepTimeout.NeverSleep;

            _isReplaySaved = false;

            foreach (var player in _players)
            {
                player.SendInputsOnResume();
            }
        }

        public void SetPaused(bool paused)
        {
            // Does not delegate out to _songRunner.SetPaused since we need extra logic
            if (paused)
            {
                Pause();
            }
            else
            {
                Resume();
            }
        }

        public void OverridePause()
        {
            // In multiplayer, don't force pause - video sync is less important than gameplay sync
            bool isLiteNetMultiplayer = NetworkingServiceFactory.Instance?.IsNetworkActive == true;
            if (isLiteNetMultiplayer)
            {
                return;
            }
            
            _songRunner.OverridePause();
            PauseCore(showMenu: false);
        }

        public bool OverrideResume()
        {
            bool resumed = _songRunner.OverrideResume();
            if (resumed)
            {
                ResumeCore();
            }

            return resumed;
        }
        
        /// <summary>
        /// Called when the lobby is left during gameplay (client perspective when host disconnects).
        /// Brings client back to the lobby browser.
        /// </summary>
        private void OnLobbyLeftDuringGameplay()
        {
            // Only handle if we're actually in gameplay
            if (GlobalVariables.Instance.CurrentScene != SceneIndex.Gameplay)
            {
                return;
            }
            
            YargLogger.LogInfo($"[GameManager] Host disconnected during gameplay - stopping song and returning to lobby browser");
            
            // Stop the song
            SetPaused(true);
            
            // Set navigation target to OnlineMultiplayer (lobby browser)
            // This ensures client goes to lobby browser instead of MusicLibrary
            MenuNavigationHelper.SetMenuNavigationAfterSceneLoad(Menu.MenuManager.Menu.OnlineMultiplayer);
            
            // Client goes back to menu
            GlobalVariables.Instance.LoadScene(SceneIndex.Menu);
        }
        
        /// <summary>
        /// Called when the host broadcasts a restart gameplay command (LiteNet).
        /// Client should restart the current song.
        /// </summary>
        private void OnLiteNetRestartGameplayRequested()
        {
            // Only handle if we're actually in gameplay
            if (GlobalVariables.Instance.CurrentScene != SceneIndex.Gameplay)
            {
                return;
            }
            
            YargLogger.LogInfo("[GameManager] LiteNet: Received restart gameplay command from host");
            
            // Restart the song via PauseMenuManager
            _pauseMenu?.Restart();
        }
        
        /// <summary>
        /// Called when a player leaves during gameplay (LiteNet).
        /// The player's track should be removed but other players continue.
        /// </summary>
        private void OnLiteNetPlayerLeftDuringGameplay(string playerName)
        {
            // Only handle if we're actually in gameplay
            if (GlobalVariables.Instance.CurrentScene != SceneIndex.Gameplay)
            {
                return;
            }
            
            YargLogger.LogInfo($"[GameManager] LiteNet: Player '{playerName}' left during gameplay");
            
            // Show a toast notification that the player left
            Menu.Persistent.ToastManager.ToastInformation($"{playerName} left the game");
            
            // Mark the player's track as disconnected (grayed out, but not removed to preserve layout)
            MarkPlayerAsDisconnected(playerName);
        }
        
        /// <summary>
        /// Marks a disconnected player's track as inactive during gameplay.
        /// The track is grayed out but NOT removed to preserve the layout of other players' tracks.
        /// </summary>
        private void MarkPlayerAsDisconnected(string playerName)
        {
            if (_players == null || _players.Count == 0)
            {
                YargLogger.LogWarning($"[GameManager] Cannot mark player '{playerName}' as disconnected - no players in list");
                return;
            }
            
            // Find the player by name (remote players have their name in Player.Profile.Name)
            // IMPORTANT: Only mark REMOTE players (those without input bindings)
            // Local players have Bindings != null, remote players have Bindings == null
            BasePlayer playerToMark = null;
            foreach (var player in _players)
            {
                if (player == null)
                {
                    continue;
                }
                
                // Check if this is a REMOTE player with matching name
                // Remote players have no input bindings (Bindings == null)
                bool isRemotePlayer = player.Player?.Bindings == null && !player.Player.IsReplay;
                if (isRemotePlayer && player.Player?.Profile?.Name == playerName)
                {
                    playerToMark = player;
                    break;
                }
            }
            
            if (playerToMark == null)
            {
                YargLogger.LogWarning($"[GameManager] Could not find REMOTE player '{playerName}' to mark as disconnected");
                return;
            }
            
            YargLogger.LogInfo($"[GameManager] Marking remote player '{playerName}' track as disconnected (preserving layout)");
            
            // If it's a TrackPlayer, mark it as disconnected (gray out, but keep in place)
            if (playerToMark is TrackPlayer trackPlayer)
            {
                trackPlayer.MarkAsDisconnected();
                YargLogger.LogInfo($"[GameManager] Track for '{playerName}' marked as disconnected");
            }
            else
            {
                // For other player types (e.g., vocals), we still just hide/gray out
                YargLogger.LogInfo($"[GameManager] Player '{playerName}' is not a TrackPlayer, skipping visual disconnect");
            }
            
            // NOTE: We do NOT remove the player from _players list or destroy the GameObject
            // This preserves the track layout so other players' tracks don't shift
            YargLogger.LogInfo($"[GameManager] Player '{playerName}' disconnected, {_players.Count} players still in list (track preserved)");
        }
        
        /// <summary>
        /// Called when a player disconnects from the LiteNet lobby (host only).
        /// The host uses this to remove the player's track locally.
        /// </summary>
        private void OnLiteNetPlayerLeft(NetworkPlayerData playerData)
        {
            // Only handle if we're actually in gameplay
            if (GlobalVariables.Instance.CurrentScene != SceneIndex.Gameplay)
            {
                return;
            }
            
            if (playerData == null)
            {
                return;
            }
            
            string playerName = playerData.PlayerName;
            YargLogger.LogInfo($"[GameManager] LiteNet Host: Player '{playerName}' disconnected during gameplay");
            
            // Show toast notification
            Menu.Persistent.ToastManager.ToastInformation($"{playerName} left the game");
            
            // Mark the player's track as disconnected (grayed out, but not removed to preserve layout)
            MarkPlayerAsDisconnected(playerName);
        }
        
        /// <summary>
        /// Called when the host broadcasts a quit to library command (LiteNet).
        /// Client should stop and return to music library.
        /// </summary>
        private void OnLiteNetQuitToLibraryRequested()
        {
            // Only handle if we're actually in gameplay
            if (GlobalVariables.Instance.CurrentScene != SceneIndex.Gameplay)
            {
                return;
            }
            
            YargLogger.LogInfo("[GameManager] LiteNet: Received quit to library command from host");
            
            // Stop the song
            SetPaused(true);
            
            // Set navigation target to full multiplayer stack so back button works correctly
            // Stack will be: MainMenu > OnlineMultiplayer > LobbyRoom > MusicLibrary
            var liteNetAdapter = NetworkingServiceFactory.Instance as LiteNetNetworkingAdapter;
            if (liteNetAdapter != null)
            {
                // The host already broadcasted navigate to music library, but set local state too
                liteNetAdapter.SetBrowsingState(true);
            }
            MenuNavigationHelper.SetMenuNavigationAfterSceneLoad(
                Menu.MenuManager.Menu.OnlineMultiplayer,
                Menu.MenuManager.Menu.LobbyRoom,
                Menu.MenuManager.Menu.MusicLibrary);
            
            // Go back to menu
            GlobalVariables.Instance.LoadScene(SceneIndex.Menu);
        }

        public double GetRelativeInputTime(double timeFromInputSystem)
            => _songRunner.GetRelativeInputTime(timeFromInputSystem);

        private bool EndSong()
        {
            if (IsPractice)
            {
                PracticeManager.ResetPractice();
                return false;
            }

            if (_songRunner.SongTime < SongLength + SONG_END_DELAY)
            {
                return false;
            }

            if (!GlobalVariables.State.PlayingWithReplay && ReplayInfo != null)
            {
                Pause(false);
                return true;
            }
#nullable enable
            ReplayInfo? replayInfo = null;
#nullable disable
            try
            {
                _isReplaySaved = false;
                replayInfo = SaveReplay(_songRunner.InputTime, ScoreContainer.ScoreReplayDirectory);
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Failed to save replay!");
            }

            SendMultiplayerSnapshot(forceSend: true);
            
            // Send final band score update to ensure other players have our final score
            SendFinalBandScore();

            // Pass the score info to the stats screen
            // Use saved local band score/stars if we were spectating (to show OUR band's score, not spectated band's)
            int scoreForScreen = _hasLocalBandScoreSaved ? _localBandScoreForScoreScreen : BandScore;
            int starsForScreen = _hasLocalBandScoreSaved ? (int)_localBandStarsForScoreScreen : (int)BandStars;
            
            GlobalVariables.State.ScoreScreenStats = new ScoreScreenStats
            {
                PlayerScores = _players.Select(player => new PlayerScoreCard
                {
                    IsHighScore = player.Score > player.LastHighScore,
                    Player = player.Player,
                    Stats = player.BaseStats
                }).ToArray(),
                BandScore = scoreForScreen,
                BandStars = starsForScreen,
                ReplayInfo = replayInfo,
            };

            // Send score results to other players in LiteNet multiplayer
            SendLiteNetScoreResults();

            RecordScores(replayInfo);

            // Dispose the crowd handler
            CrowdEventHandler.Dispose();

            // Go to the score screen
            GlobalVariables.Instance.LoadScene(SceneIndex.Score);
            return true;
        }

        /// <summary>
        /// Sends local player score results to other players via LiteNet networking.
        /// </summary>
        private void SendLiteNetScoreResults()
        {
            var networkService = NetworkingServiceFactory.Instance;
            if (networkService == null || !networkService.IsNetworkActive)
            {
                Debug.Log("[GameManager] SendLiteNetScoreResults - network service inactive, skipping");
                return;
            }

            var liteNetAdapter = networkService as LiteNetNetworkingAdapter;
            if (liteNetAdapter == null)
            {
                Debug.Log("[GameManager] SendLiteNetScoreResults - not using LiteNet adapter, skipping");
                return;
            }

            Debug.Log($"[GameManager] SendLiteNetScoreResults - sending results for {_players.Count} players");

            foreach (var player in _players)
            {
                // Only send results for local (non-remote) players
                // Remote players have null Bindings
                if (player.Player.Bindings == null)
                {
                    Debug.Log($"[GameManager] SendLiteNetScoreResults - skipping {player.Player.Profile?.Name ?? "?"} (remote/no bindings)");
                    continue;
                }

                // Skip bots
                if (player.Player.Profile.IsBot)
                {
                    Debug.Log($"[GameManager] SendLiteNetScoreResults - skipping {player.Player.Profile?.Name ?? "?"} (bot)");
                    continue;
                }

                var stats = player.BaseStats;
                bool isHighScore = player.Score > player.LastHighScore;
                bool isFullCombo = stats.IsFullCombo;

                Debug.Log($"[GameManager] SendLiteNetScoreResults - sending for {player.Player.Profile.Name}: " +
                          $"Score={stats.TotalScore}, NotesHit={stats.NotesHit}, NotesMissed={stats.NotesMissed}, " +
                          $"MaxCombo={stats.MaxCombo}, IsHighScore={isHighScore}, IsFullCombo={isFullCombo}");

                liteNetAdapter.SendScoreResults(
                    player.Player.Profile.Name,
                    isHighScore,
                    isFullCombo,
                    stats.TotalScore,
                    stats.MaxCombo,
                    stats.NotesHit,
                    stats.NotesMissed
                );
            }
        }

        private void RecordScores(ReplayInfo replayInfo)
        {
            if (!ScoreContainer.IsBandScoreValid(SongSpeed))
            {
                return;
            }

            // Get all of the individual player score entries
            var playerEntries = new List<PlayerScoreRecord>();

            foreach (var player in _players)
            {
                var profile = player.Player.Profile;

                // Skip bots and anyone that's obviously cheating.
                if (!ScoreContainer.IsSoloScoreValid(SongSpeed, player.Player))
                {
                    continue;
                }

                playerEntries.Add(new PlayerScoreRecord
                {
                    PlayerId = profile.Id,

                    Instrument = profile.CurrentInstrument,
                    Difficulty = profile.CurrentDifficulty,

                    EnginePresetId = profile.EnginePreset,

                    Score = player.Score,
                    Stars = StarAmountHelper.GetStarsFromInt((int) player.Stars),

                    NotesHit = player.BaseStats.NotesHit,
                    NotesMissed = player.BaseStats.NotesMissed,
                    IsFc = player.IsFc,
                    IsReplay = player.Player.IsReplay,

                    Percent = player.BaseStats.Percent,

                    PlayerDisplayName = profile.Name
                });
            }

            // Record the score into the database (but only if there are no bots, and Song Speed is at least 100%)
            ScoreContainer.RecordScore(new GameRecord
            {
                Date = DateTime.Now,

                SongChecksum = Song.Hash.HashBytes,
                SongName = Song.Name,
                SongArtist = Song.Artist,
                SongCharter = Song.Charter,

                ReplayFileName = replayInfo?.ReplayName,
                ReplayChecksum = replayInfo?.ReplayChecksum.HashBytes,

                BandScore = BandScore,
                BandStars = StarAmountHelper.GetStarsFromInt((int) BandStars),

                SongSpeed = SongSpeed,
                PlayedWithReplay = GlobalVariables.State.PlayingWithReplay,
            }, playerEntries);
        }

        public void ForceQuitSong()
        {
            GlobalVariables.State = PersistentState.Default;
            GlobalVariables.Instance.LoadScene(SceneIndex.Menu);
        }

        public void SetVenueCameraManager(CameraManager cameraManager)
        {
            VenueCameraManager = cameraManager;
        }

        public void SetVenueCharacterManager(CharacterManager characterManager)
        {
            VenueCharacterManager = characterManager;
        }

        public void SetEditHUD(bool on)
        {
            if (on)
            {
                _pauseMenu.gameObject.SetActive(false);
                _draggableHud.SetEditHUD(true);
            }
            else
            {
                _draggableHud.SetEditHUD(false);
                _pauseMenu.gameObject.SetActive(true);
            }
        }

#nullable enable
        public ReplayInfo? SaveReplay(double length, string directory)
#nullable disable
        {
            if (_isReplaySaved)
            {
                return null;
            }

            var frames = new List<ReplayFrame>(_players.Count);
            var replayStats = new List<ReplayStats>(_players.Count);
            var colorProfiles = new Dictionary<Guid, ColorProfile>();
            var cameraPresets = new Dictionary<Guid, CameraPreset>();

            int bandScore = 0;
            float bandStars = 0f;
            for (int i = 0; i < _players.Count; i++)
            {
                var player = _players[i];
                
                // Skip bot players (no inputs recorded)
                if (player.Player.Profile.IsBot)
                {
                    continue;
                }
                
                // Skip remote players (no inputs recorded - they're simulated from network state)
                if (player.Player.Bindings == null && !player.Player.IsReplay)
                {
                    continue;
                }

                var (frame, stats) = player.ConstructReplayData();
                frames.Add(frame);
                replayStats.Add(stats);
                bandScore += player.Score;
                bandStars += player.Stars;

                if (!player.Player.ColorProfile.DefaultPreset)
                {
                    colorProfiles.TryAdd(player.Player.ColorProfile.Id, player.Player.ColorProfile);
                }

                if (!player.Player.CameraPreset.DefaultPreset)
                {
                    cameraPresets.TryAdd(player.Player.CameraPreset.Id, player.Player.CameraPreset);
                }
            }

            if (frames.Count == 0)
            {
                return null;
            }

            var stars = StarAmountHelper.GetStarsFromInt((int) (bandStars / frames.Count));
            var data = new ReplayData(colorProfiles, cameraPresets, frames.ToArray(), _frameTimes.ToArray());

            var (success, replayInfo) = ReplayIO.TrySerialize(directory, Song, SongSpeed, length, bandScore, stars, replayStats.ToArray(), data);
            if (!success)
            {
                return null;
            }

            ReplayContainer.AddEntry(replayInfo);
            _isReplaySaved = true;
            return replayInfo;
        }

        private void OnNavigationEvent(NavigationContext context)
        {
            switch (context.Action)
            {
                // Pause
                case MenuAction.Start:
                    if ((!IsPractice || PracticeManager.HasSelectedSection) && !DialogManager.Instance.IsDialogShowing && !PlayerHasFailed)
                    {
                        // Check if we're in multiplayer (LiteNet)
                        bool isMultiplayer = NetworkingServiceFactory.Instance?.IsNetworkActive == true;

                        if (isMultiplayer)
                        {
                            if (_pauseMenu != null && _pauseMenu.IsOpen)
                            {
                                _pauseMenu.PopAllMenus();
                            }
                            else
                            {
                                PauseCore(showMenu: true, freezeGameplay: false);
                            }
                        }
                        else
                        {
                            SetPaused(!_songRunner.Paused);
                        }
                    }
                    break;
            }
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            // Guard against null settings during scene load
            if (SettingsManager.Settings?.PauseOnFocusLoss == null)
                return;
                
            if (!hasFocus && !Paused && SettingsManager.Settings.PauseOnFocusLoss.Value)
            {
                // Check if we're in multiplayer (LiteNet)
                bool isMultiplayer = NetworkingServiceFactory.Instance?.IsNetworkActive == true;
                
                if (isMultiplayer)
                {
                    // In multiplayer, show pause menu but keep song playing
                    if (_pauseMenu != null && !_pauseMenu.IsOpen)
                    {
                        PauseCore(showMenu: true, freezeGameplay: false);
                    }
                }
                else
                {
                    // Single player - normal pause behavior
                    SetPaused(true);
                }
            }
        }

        public void ResetBandCombo()
        {
            switch (_bandComboType)
            {
                case BandComboType.Strict:
                    BandCombo = 0;
                break;
                case BandComboType.Lenient:
                    BandCombo = Players.Sum(e => e.Combo * e.BaseStats.BandComboUnits);
                break;
            }
        }

        public void AddBandCombo(int amount)
        {
            BandCombo += amount;
        }

        private void OnSongFailed()
        {
            if (IsNoFailActive || IsPractice)
            {
                return;
            }

            if (IsMultiplayerActive())
            {
                HandleMultiplayerSongFailed();
                return;
            }

            _ = RunBandFailureSequenceAsync();
        }

        /// <summary>
        /// Called when an individual player fails (happiness drops to 0).
        /// The player can still be revived via Star Power.
        /// </summary>
        private void OnPlayerFailed(int engineId)
        {
            if (IsNoFailActive || IsPractice)
            {
                return;
            }
            
            // Find the player who failed
            BasePlayer failedPlayer = null;
            foreach (var player in _players)
            {
                var container = EngineManager.Engines.Find(e => e.Engine == player.BaseEngine);
                if (container != null && container.EngineId == engineId)
                {
                    failedPlayer = player;
                    break;
                }
            }
            
            if (failedPlayer != null)
            {
                YargLogger.LogFormatInfo("[GameManager] Player '{0}' has failed! Can be revived via Star Power. Alive players: {1}",
                    failedPlayer.Player.Profile.Name, EngineManager.GetAlivePlayerCount());
                    
                // Mute the failed player's stems
                MutePlayerStems(failedPlayer, true);
                
                // In multiplayer, mark player as failed
                if (IsMultiplayerActive())
                {
                    var localPlayer = NetworkingServiceFactory.Instance?.GetLocalPlayer();
                    if (localPlayer != null)
                    {
                        // Check if the failed player is the local player
                        var networkPlayer = GetNetworkPlayerForEngine(engineId);
                        if (networkPlayer?.IsLocalUser == true)
                        {
                            localPlayer.HasFailed = true;
                        }
                    }
                }
            }
        }
        
        /// <summary>
        /// Called when a player is revived via Star Power.
        /// </summary>
        private void OnPlayerRevived(int engineId, float newHappiness)
        {
            // Find the player who was revived
            BasePlayer revivedPlayer = null;
            foreach (var player in _players)
            {
                var container = EngineManager.Engines.Find(e => e.Engine == player.BaseEngine);
                if (container != null && container.EngineId == engineId)
                {
                    revivedPlayer = player;
                    break;
                }
            }
            
            if (revivedPlayer != null)
            {
                YargLogger.LogFormatInfo("[GameManager] Player '{0}' has been revived with {1:P0} happiness!",
                    revivedPlayer.Player.Profile.Name, newHappiness);
                    
                // Check if this is a local player being revived while we're spectating
                bool isLocalPlayer = revivedPlayer.Player?.Bindings != null && !revivedPlayer.Player.IsReplay;
                
                // If we're spectating and a local player was revived, exit spectate mode
                if (_isSpectating && isLocalPlayer)
                {
                    YargLogger.LogFormatInfo("[GameManager] Local player '{0}' revived while spectating - exiting spectate mode",
                        revivedPlayer.Player.Profile.Name);
                    
                    _isSpectating = false;
                    PlayerHasFailed = false;
                    
                    // Remove spectator tracks and restore local tracks
                    RemoveSpectatorTracks(restoreLocalTracks: true);
                    
                    // Clear saved band score flags since we're back in play
                    _hasSavedBandScore = false;
                    _hasLocalBandScoreSaved = false;
                    
                    // Restore audio for local playback
                    _mixer.SetVolume(1.0f);
                    
                    // Hide spectate UI overlay
                    OnSpectateEnded?.Invoke();
                }
                    
                // Unmute the revived player's stems
                MutePlayerStems(revivedPlayer, false);
                
                // Calculate 4-beat grace period based on current tempo
                // This gives the player time to see the countdown and prepare
                double gracePeriod = GetRevivalGracePeriod();
                revivedPlayer.ShowRevivalCountdown(gracePeriod);
                
                // In multiplayer, mark player as alive
                if (IsMultiplayerActive())
                {
                    var localPlayer = NetworkingServiceFactory.Instance?.GetLocalPlayer();
                    if (localPlayer != null)
                    {
                        // Check if the revived player is the local player
                        var networkPlayer = GetNetworkPlayerForEngine(engineId);
                        if (networkPlayer?.IsLocalUser == true)
                        {
                            localPlayer.HasFailed = false;
                        }
                    }
                }
            }
        }
        
        /// <summary>
        /// Gets the revival grace period based on the current song tempo.
        /// Returns 4 beats worth of time (minimum 2 seconds, maximum 4 seconds).
        /// </summary>
        private double GetRevivalGracePeriod()
        {
            const int REVIVAL_BEATS = 4;
            const double MIN_GRACE_PERIOD = 2.0;
            const double MAX_GRACE_PERIOD = 4.0;
            
            // Get the current tempo at the current song time
            var syncTrack = Chart?.SyncTrack;
            if (syncTrack == null || syncTrack.Tempos.Count == 0)
            {
                return MAX_GRACE_PERIOD;
            }
            
            // Find the tempo at current song time
            var currentTempo = syncTrack.Tempos[0];
            foreach (var tempo in syncTrack.Tempos)
            {
                if (tempo.Time > SongTime)
                    break;
                currentTempo = tempo;
            }
            
            // Calculate 4 beats worth of time
            double gracePeriod = currentTempo.SecondsPerBeat * REVIVAL_BEATS;
            
            // Clamp to reasonable bounds
            return Math.Clamp(gracePeriod, MIN_GRACE_PERIOD, MAX_GRACE_PERIOD);
        }
        
        /// <summary>
        /// Gets the NetworkPlayerData for a given engine ID.
        /// </summary>
        private NetworkPlayerData GetNetworkPlayerForEngine(int engineId)
        {
            foreach (var player in _players)
            {
                var container = EngineManager.Engines.Find(e => e.Engine == player.BaseEngine);
                if (container != null && container.EngineId == engineId)
                {
                    // Try to find network player data for this player
                    if (Menu.Multiplayer.MultiplayerPlayerManager.TryGetNetworkPlayer(player.Player, out var networkPlayer))
                    {
                        return networkPlayer;
                    }
                    return null;
                }
            }
            return null;
        }
        
        /// <summary>
        /// Mutes or unmutes a player's audio stems.
        /// </summary>
        private void MutePlayerStems(BasePlayer player, bool mute)
        {
            // Get the stems for this player based on their instrument
            var instrument = player.Player.Profile.CurrentInstrument;
            var stems = GetStemsForInstrument(instrument);
            
            foreach (var stem in stems)
            {
                if (mute)
                {
                    GlobalAudioHandler.SetVolumeSetting(stem, 0.0);
                }
                else
                {
                    // Restore to default volume
                    if (_stemStates != null && _stemStates.TryGetValue(stem, out var state))
                    {
                        GlobalAudioHandler.SetVolumeSetting(stem, state.Volume);
                    }
                    else
                    {
                        GlobalAudioHandler.SetVolumeSetting(stem, 1.0);
                    }
                }
            }
        }
        
        /// <summary>
        /// Gets the audio stems associated with an instrument.
        /// </summary>
        private SongStem[] GetStemsForInstrument(YARG.Core.Instrument instrument)
        {
            return instrument switch
            {
                YARG.Core.Instrument.FiveFretGuitar or YARG.Core.Instrument.SixFretGuitar => 
                    new[] { SongStem.Guitar },
                YARG.Core.Instrument.FiveFretBass or YARG.Core.Instrument.SixFretBass => 
                    new[] { SongStem.Bass },
                YARG.Core.Instrument.FourLaneDrums or YARG.Core.Instrument.FiveLaneDrums or YARG.Core.Instrument.ProDrums => 
                    new[] { SongStem.Drums, SongStem.Drums1, SongStem.Drums2, SongStem.Drums3, SongStem.Drums4 },
                YARG.Core.Instrument.Keys or YARG.Core.Instrument.ProKeys => 
                    new[] { SongStem.Keys },
                YARG.Core.Instrument.Vocals or YARG.Core.Instrument.Harmony => 
                    new[] { SongStem.Vocals, SongStem.Vocals1, SongStem.Vocals2 },
                _ => Array.Empty<SongStem>()
            };
        }

        private bool IsMultiplayerActive()
        {
            // Check if we're in multiplayer (LiteNet)
            bool isMultiplayer = NetworkingServiceFactory.Instance?.IsNetworkActive == true;
            return _multiplayerSync != null && isMultiplayer;
        }

        private void HandleMultiplayerSongFailed()
        {
            // Check if LiteNet is active
            bool isLiteNetActive = NetworkingServiceFactory.Instance?.IsNetworkActive == true;
            
            if (!isLiteNetActive)
            {
                // No multiplayer active, just run failure sequence
                _ = RunBandFailureSequenceAsync();
                return;
            }
            
            // Mark local player as failed in network state
            var localPlayer = NetworkingServiceFactory.Instance?.GetLocalPlayer();
            if (localPlayer != null)
            {
                localPlayer.HasFailed = true;
            }
            
            // Check if we're in band mode
            var bandManager = Networking.Bands.BandManager.Instance;
            if (bandManager != null && bandManager.IsBandSystemActive)
            {
                // Save the final score to the local BandManager before sending
                long finalScore = BandScore;
                int localBandId = bandManager.LocalPlayerBandId;
                
                // Update local BandManager with final score so leaderboard shows correct score during spectate
                bandManager.UpdateBandScore(localBandId, finalScore);
                
                // Send band failure notification to network
                var adapter = NetworkingServiceFactory.Instance as Networking.Abstraction.LiteNetNetworkingAdapter;
                if (adapter != null)
                {
                    adapter.SendBandFailed(localBandId, finalScore);
                }
                
                // Check if other bands are still alive - if so, enter spectate mode
                _ = HandleBandModeFailureAsync();
                return;
            }
            
            // Not in band mode, run standard failure sequence
            _ = RunBandFailureSequenceAsync();
        }
        
        /// <summary>
        /// Handles failure in band mode - enters spectate if other bands alive, else ends game.
        /// </summary>
        private async UniTask HandleBandModeFailureAsync()
        {
            if (PlayerHasFailed)
            {
                return;
            }
            
            var bandManager = Networking.Bands.BandManager.Instance;
            if (bandManager == null)
            {
                // Fallback to standard failure
                _ = RunBandFailureSequenceAsync();
                return;
            }
            
            // Mark local band as failed
            bandManager.MarkBandAsFailed(bandManager.LocalPlayerBandId);
            
            // Check if other bands are still alive
            int aliveBands = bandManager.GetAliveBandCount();
            
            if (aliveBands > 0)
            {
                // Other bands still alive - enter spectate mode
                Debug.Log($"[GameManager] Local band failed but {aliveBands} other band(s) still alive - entering spectate mode");
                PlayerHasFailed = true;
                _isSpectating = true;
                
                // Subscribe to all bands failed event IMMEDIATELY - before any async work
                // This ensures we catch the event even if other bands fail during our transition
                bool allBandsFailedDuringTransition = false;
                void TransitionFailHandler()
                {
                    Debug.Log("[GameManager] All bands failed during spectate transition!");
                    allBandsFailedDuringTransition = true;
                }
                bandManager.OnAllBandsFailed += TransitionFailHandler;
                
                // IMPORTANT: Save the band score BEFORE hiding tracks
                // After hiding, BandScore will be 0 because inactive players are skipped in Update()
                _savedBandScoreForNetwork = BandScore;
                _hasSavedBandScore = true;
                
                // Also save for score screen display (so we show OUR band's score, not spectated band's)
                _localBandScoreForScoreScreen = BandScore;
                _localBandStarsForScoreScreen = BandStars;
                _hasLocalBandScoreSaved = true;
                
                Debug.Log($"[GameManager] Saved band score for network sync and score screen: {_savedBandScoreForNetwork}");
                
                // STEP 1: Create spectator tracks FIRST (hidden) before any visual changes
                // This pre-loads the tracks so we can swap them in smoothly
                int spectateBandId = bandManager.GetNextAliveBandToSpectate();
                bool tracksCreated = false;
                if (spectateBandId >= 0)
                {
                    tracksCreated = CreateSpectatorTracksForBand(spectateBandId);
                    Debug.Log($"[GameManager] Pre-created spectator tracks for band {spectateBandId}: {tracksCreated}");
                }
                
                // STEP 2: Do the visual swap - hide local and reveal spectator in quick succession
                HideLocalTracks();
                
                // Wait one frame for the local track removal to complete
                await UniTask.Yield();
                
                // Check if all bands failed during the frame wait
                if (allBandsFailedDuringTransition)
                {
                    Debug.Log("[GameManager] Aborting spectate - all bands failed during transition");
                    bandManager.OnAllBandsFailed -= TransitionFailHandler;
                    _isSpectating = false;
                    RemoveSpectatorTracks(restoreLocalTracks: false);
                    _ = RunBandFailureSequenceAsync();
                    return;
                }
                
                // Reveal spectator tracks immediately after hiding local
                if (tracksCreated)
                {
                    RevealSpectatorTracks();
                }
                
                // STEP 3: Now do the audio fade (tracks are already swapped)
                _mixer.FadeOut(0.5f);
                await UniTask.Delay(TimeSpan.FromSeconds(0.5));
                
                // Check again after the delay
                if (allBandsFailedDuringTransition)
                {
                    Debug.Log("[GameManager] Aborting spectate after audio fade - all bands failed during transition");
                    bandManager.OnAllBandsFailed -= TransitionFailHandler;
                    _isSpectating = false;
                    RemoveSpectatorTracks(restoreLocalTracks: false);
                    _ = RunBandFailureSequenceAsync();
                    return;
                }
                
                GlobalAudioHandler.PlayVoxSample(VoxSample.FailSound);
                
                // Restore audio for spectating (mute local player tracks, keep band/crowd)
                _mixer.SetVolume(1.0f);
                
                // Show spectate UI overlay (tracks are already visible)
                OnSpectateStarted?.Invoke();
                
                // Replace temporary handler with the permanent one
                bandManager.OnAllBandsFailed -= TransitionFailHandler;
                bandManager.OnAllBandsFailed += OnAllBandsFailedHandler;
            }
            else
            {
                // All bands failed - end the game
                _ = RunBandFailureSequenceAsync();
            }
        }
        
        /// <summary>
        /// Called when all bands have failed - ends spectate mode and shows results.
        /// </summary>
        private void OnAllBandsFailedHandler()
        {
            // This may be called from a network thread, so we need to ensure it runs on the main thread
            // Use Unity's main thread dispatcher pattern
            _ = EndSpectateAndShowFailureAsync();
        }
        
        private async UniTask EndSpectateAndShowFailureAsync()
        {
            // Ensure we're on the main thread
            await UniTask.SwitchToMainThread();
            
            var bandManager = Networking.Bands.BandManager.Instance;
            if (bandManager != null)
            {
                bandManager.OnAllBandsFailed -= OnAllBandsFailedHandler;
            }
            
            Debug.Log("[GameManager] All bands have failed - ending spectate mode and showing failure");
            _isSpectating = false;
            
            // Remove spectator tracks and clean up
            RemoveSpectatorTracks(restoreLocalTracks: false);
            
            // Exit spectate mode on the fail meter
            RestoreFailMeterFromSpectate();
            
            // Show the failure/results screen
            Pause();
        }
        
        /// <summary>
        /// Whether the local player is spectating after their band failed.
        /// </summary>
        private bool _isSpectating;
        
        /// <summary>
        /// Event fired when spectate mode starts (local band failed but others alive).
        /// </summary>
        public event Action OnSpectateStarted;
        
        /// <summary>
        /// Event fired when spectate mode ends (local player revived or song ended).
        /// </summary>
        public event Action OnSpectateEnded;

        internal void HandleNetworkBandFailed()
        {
            if (PlayerHasFailed)
            {
                return;
            }

            _multiplayerFailureReported = true;
            _ = RunBandFailureSequenceAsync();
        }

        private async UniTask RunBandFailureSequenceAsync()
        {
            if (PlayerHasFailed)
            {
                return;
            }

            PlayerHasFailed = true;
            _mixer.FadeOut(SONG_END_DELAY);
            await UniTask.Delay(TimeSpan.FromSeconds(SONG_END_DELAY));
            GlobalAudioHandler.PlayVoxSample(VoxSample.FailSound);
            Pause();
        }

        // If we go from no fail to fail, we need to reinitialize the happiness state so we avoid
        // the possibility of an instant fail. Yes, this is cheeseable since toggling no fail resets happiness.
        private void OnNoFailModeChanged(bool noFail)
        {
            // If we're going from no fail to fail and happiness would result in an insta-fail, reset happiness,
            // but also inhibit score saving to avoid cheesing
            if (!noFail && EngineManager.Happiness <= 0f)
            {
                foreach (var player in _players)
                {
                    player.Player.IsScoreValid = false;
                }

                EngineManager.InitializeHappiness();
            }
        }
    }
}
