using System;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using Mirror;
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
using YARG.Menu.Persistent;
using YARG.Menu.ScoreScreen;
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
        private Menu.Multiplayer.MultiplayerGameplaySync _multiplayerSync;
        private Menu.Multiplayer.MultiplayerUnisonSync _multiplayerUnisonSync;

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

            // Check if we're in multiplayer mode (Mirror or LiteNet)
            // NOTE: These must be mutually exclusive - check LiteNet first since it's the preferred transport
            bool isLiteNetMultiplayer = YARG.Networking.Abstraction.NetworkingServiceFactory.Instance?.IsNetworkActive == true;
            // Only consider Mirror active if LiteNet is NOT active (prevents false positives from Mirror singleton existing)
            bool isMirrorMultiplayer = !isLiteNetMultiplayer && 
                                       Networking.YargNetworkManager.Instance != null && 
                                       Networking.YargNetworkManager.Instance.isNetworkActive;
            bool isMultiplayer = isMirrorMultiplayer || isLiteNetMultiplayer;

            if (isMultiplayer)
            {
                // In multiplayer, mark that we need to create players in Start() after network objects spawn
                // Initialize multiplayer sync components now
                _multiplayerSync = gameObject.AddComponent<Menu.Multiplayer.MultiplayerGameplaySync>();
                _multiplayerUnisonSync = gameObject.AddComponent<Menu.Multiplayer.MultiplayerUnisonSync>();
                Debug.Log($"[GameManager] Multiplayer sync components added - will create players in Start() (Mirror: {isMirrorMultiplayer}, LiteNet: {isLiteNetMultiplayer})");
                
                // Register disconnect event handlers for multiplayer gameplay
                if (isMirrorMultiplayer)
                {
                    Networking.YargNetworkManager.Instance.OnClientDisconnected += OnClientDisconnectedDuringGameplay;
                    Networking.YargNetworkManager.Instance.OnLobbyLeft += OnLobbyLeftDuringGameplay;
                    Debug.Log("[GameManager] Registered disconnect event handlers for multiplayer (Mirror)");
                }
                
                // Register LiteNet event handlers for multiplayer gameplay
                if (isLiteNetMultiplayer)
                {
                    var liteNetAdapter = YARG.Networking.Abstraction.NetworkingServiceFactory.Instance as YARG.Networking.Abstraction.LiteNetNetworkingAdapter;
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

            // Unsubscribe from Mirror disconnect events
            if (Networking.YargNetworkManager.Instance != null)
            {
                if (Networking.YargNetworkManager.Instance.isNetworkActive)
                {
                    Networking.YargNetworkManager.Instance.ReportLocalGameplayReady(false);
                }
                Networking.YargNetworkManager.Instance.OnClientDisconnected -= OnClientDisconnectedDuringGameplay;
                Networking.YargNetworkManager.Instance.OnLobbyLeft -= OnLobbyLeftDuringGameplay;
            }
            
            // Unsubscribe from LiteNet events
            var liteNetAdapter = YARG.Networking.Abstraction.NetworkingServiceFactory.Instance as YARG.Networking.Abstraction.LiteNetNetworkingAdapter;
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

            // Reset the time scale back, as it would be 0 at this point (because of pausing)
            Time.timeScale = 1f;

            // Reset sleep timeout setting
            Screen.sleepTimeout = _originalSleepTimeout;
        }

        private void Update()
        {
            // Pause/unpause
            if (Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                if ((!IsPractice || PracticeManager.HasSelectedSection) &&
                    !DialogManager.Instance.IsDialogShowing &&
                    !PlayerHasFailed)
                {
                    // Check if we're in multiplayer (LiteNet or Mirror)
                    bool isLiteNetMultiplayer = YARG.Networking.Abstraction.NetworkingServiceFactory.Instance?.IsNetworkActive == true;
                    bool isMirrorMultiplayer = !isLiteNetMultiplayer && 
                                               Networking.YargNetworkManager.Instance != null && 
                                               Networking.YargNetworkManager.Instance.isNetworkActive;
                    bool isMultiplayer = isLiteNetMultiplayer || isMirrorMultiplayer;
                    
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
            foreach (var player in _players)
            {
                player.GameplayUpdate();

                totalScore += player.Score;
                totalScore += player.BandBonusScore;
                totalStars += player.Stars;
            }

            if (GlobalVariables.VerboseReplays)
            {
                _frameTimes.Add(_songRunner.InputTime);
            }

            BandScore = totalScore;
            BandStars = totalStars / _players.Count;
            
            SendMultiplayerSnapshot();

            // End song if needed (required for the [end] event)
            if (_songRunner.SongTime >= SongLength)
            {
                if (EndSong())
                {
                    return;
                }
            }
        }

        private void SendMultiplayerSnapshot(bool forceSend = false)
        {
            if (_multiplayerSync == null || _players == null || _players.Count == 0 || _songRunner == null)
            {
                return;
            }

            double songTime = _songRunner.SongTime;
            
            // Check if we're using LiteNet (which handles player locality differently)
            bool useLiteNet = Networking.Abstraction.NetworkingServiceFactory.Instance?.IsNetworkActive == true;
            
            // Use appropriate time source for network time
            // Mirror uses NetworkTime.time which requires Mirror to be active
            // For LiteNet, use Unity's realtime clock
            double clientNetworkTime = useLiteNet ? Time.realtimeSinceStartupAsDouble : NetworkTime.time;
            
            int localPlayerCount = 0;
            foreach (var player in _players)
            {
                var networkData = player.NetworkPlayerData;
                
                // Determine if this is a local player
                // For Mirror: check NetworkPlayerData.IsLocalUser
                // For LiteNet: check if player has local bindings (a "real" local player with input)
                bool isLocalPlayer;
                if (useLiteNet)
                {
                    // For LiteNet, a local player is one with bindings (can receive input)
                    isLocalPlayer = player.Player?.Bindings != null && !player.Player.IsReplay;
                }
                else
                {
                    // For Mirror, use NetworkPlayerData
                    isLocalPlayer = networkData != null && networkData.IsLocalUser;
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

                _multiplayerSync.SubmitLocalSnapshot(networkData, player.Score, player.Combo, baseStats.MaxCombo,
                    baseStats.IsStarPowerActive, starPowerAmount, baseStats.StarPowerPhrasesHit,
                    baseStats.TotalStarPowerPhrases, player.NotesHit, notesMissed, overstrums, hoposStrummed,
                    overhits, ghostInputs, ghostsHit, accentsHit, dynamicsBonus, bandBonusScore, vocalsTicksHit,
                    vocalsTicksMissed, vocalsPhraseTicksHit, vocalsPhraseTicksTotal, soloActive, soloSequence,
                    soloNoteCount, soloNotesHit, soloLastBonus, soloTotalBonus, sustainsHeld, whammyValue,
                    songTime, clientNetworkTime, forceSend);
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
            bool isLiteNetMultiplayer = YARG.Networking.Abstraction.NetworkingServiceFactory.Instance?.IsNetworkActive == true;
            
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
                // Check if we're in multiplayer (LiteNet or Mirror)
                // NOTE: Check LiteNet first - if LiteNet is active, consider Mirror inactive
                bool isLiteNetMultiplayer = YARG.Networking.Abstraction.NetworkingServiceFactory.Instance?.IsNetworkActive == true;
                bool isMirrorMultiplayer = !isLiteNetMultiplayer && 
                                           Networking.YargNetworkManager.Instance != null && 
                                           Networking.YargNetworkManager.Instance.isNetworkActive;
                bool isMultiplayer = isLiteNetMultiplayer || isMirrorMultiplayer;
                
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
            bool isLiteNetMultiplayer = YARG.Networking.Abstraction.NetworkingServiceFactory.Instance?.IsNetworkActive == true;
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
        /// Called when a client disconnects during gameplay (host perspective).
        /// Brings everyone back to music library with notification.
        /// </summary>
        private void OnClientDisconnectedDuringGameplay(Mirror.NetworkConnectionToClient conn)
        {
            // Only handle if we're actually in gameplay
            if (GlobalVariables.Instance.CurrentScene != SceneIndex.Gameplay)
            {
                return;
            }
            
            // Only host receives this event
            if (Networking.YargNetworkManager.Instance == null || !Networking.YargNetworkManager.Instance.IsHosting)
            {
                return;
            }
            
            YargLogger.LogInfo($"[GameManager] Client disconnected during gameplay - stopping song and returning all players to music library");
            
            // Stop the song
            SetPaused(true);
            
            // Sync all remaining clients back to music library
            if (Networking.YargNetworkManager.Instance != null)
            {
                Networking.YargNetworkManager.Instance.SyncMenuNavigation(popMenu: false, targetMenu: Menu.MenuManager.Menu.MusicLibrary);
                
                // Host should also go directly to MusicLibrary after scene loads
                Networking.YargNetworkManager.SetMenuNavigationAfterSceneLoad(
                    Menu.MenuManager.Menu.OnlineMultiplayer,
                    Menu.MenuManager.Menu.LobbyRoom,
                    Menu.MenuManager.Menu.MusicLibrary);
            }
            
            // Host also goes back to menu
            GlobalVariables.Instance.LoadScene(SceneIndex.Menu);
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
            Networking.YargNetworkManager.SetMenuNavigationAfterSceneLoad(Menu.MenuManager.Menu.OnlineMultiplayer);
            
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
            
            // Remove the player's track from the game
            RemoveDisconnectedPlayer(playerName);
        }
        
        /// <summary>
        /// Removes a disconnected player's track from gameplay.
        /// Called when a remote player disconnects during a song.
        /// </summary>
        private void RemoveDisconnectedPlayer(string playerName)
        {
            if (_players == null || _players.Count == 0)
            {
                YargLogger.LogWarning($"[GameManager] Cannot remove player '{playerName}' - no players in list");
                return;
            }
            
            // Find the player by name (remote players have their name in Player.Profile.Name)
            // IMPORTANT: Only remove REMOTE players (those without input bindings)
            // Local players have Bindings != null, remote players have Bindings == null
            BasePlayer playerToRemove = null;
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
                    playerToRemove = player;
                    break;
                }
            }
            
            if (playerToRemove == null)
            {
                YargLogger.LogWarning($"[GameManager] Could not find REMOTE player '{playerName}' to remove (local players are not removed)");
                return;
            }
            
            YargLogger.LogInfo($"[GameManager] Removing disconnected remote player '{playerName}' track");
            
            // Remove from players list
            _players.Remove(playerToRemove);
            
            // If it's a TrackPlayer, remove from rendering system
            if (playerToRemove is TrackPlayer trackPlayer)
            {
                // Remove from highway camera rendering
                _trackViewManager._highwayCameraRendering.RemoveTrackPlayer(trackPlayer);
                
                // Remove the track view
                _trackViewManager.RemoveTrackView(trackPlayer.TrackView);
                
                YargLogger.LogInfo($"[GameManager] Removed track view and camera for '{playerName}'");
            }
            
            // Destroy the player GameObject
            if (playerToRemove.gameObject != null)
            {
                Destroy(playerToRemove.gameObject);
                YargLogger.LogInfo($"[GameManager] Destroyed player GameObject for '{playerName}'");
            }
            
            YargLogger.LogInfo($"[GameManager] Player '{playerName}' removed, {_players.Count} players remaining");
        }
        
        /// <summary>
        /// Called when a player disconnects from the LiteNet lobby (host only).
        /// The host uses this to remove the player's track locally.
        /// </summary>
        private void OnLiteNetPlayerLeft(Networking.NetworkPlayerData playerData)
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
            
            // Remove the player's track
            RemoveDisconnectedPlayer(playerName);
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
            
            // Set navigation target to music library
            var liteNetAdapter = YARG.Networking.Abstraction.NetworkingServiceFactory.Instance as YARG.Networking.Abstraction.LiteNetNetworkingAdapter;
            if (liteNetAdapter != null)
            {
                // The host already broadcasted navigate to music library, but set local state too
                liteNetAdapter.SetBrowsingState(true);
            }
            Networking.YargNetworkManager.SetMenuNavigationAfterSceneLoad(Menu.MenuManager.Menu.MusicLibrary);
            
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
            ApplyAuthoritativeNetworkStats();

            // Pass the score info to the stats screen
            GlobalVariables.State.ScoreScreenStats = new ScoreScreenStats
            {
                PlayerScores = _players.Select(player => new PlayerScoreCard
                {
                    IsHighScore = player.Score > player.LastHighScore,
                    Player = player.Player,
                    Stats = player.BaseStats
                }).ToArray(),
                BandScore = BandScore,
                BandStars = (int) BandStars,
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

        private void ApplyAuthoritativeNetworkStats()
        {
            // This method is for Mirror networking only - LiteNet uses different approach via MultiplayerGameplaySync
            bool isLiteNetActive = YARG.Networking.Abstraction.NetworkingServiceFactory.Instance?.IsNetworkActive == true;
            if (isLiteNetActive)
            {
                // LiteNet handles this differently - skip Mirror-specific logic
                return;
            }
            
            if (Networking.YargNetworkManager.Instance == null || !Networking.YargNetworkManager.Instance.isNetworkActive)
            {
                return;
            }

            foreach (var player in _players)
            {
                var networkData = player.NetworkPlayerData;
                if (networkData == null)
                {
                    continue;
                }

                var stats = player.BaseStats;
                int sanitizedScore = Mathf.Max(0, networkData.CurrentScore);
                int bandBonusScore = Mathf.Max(0, networkData.BandBonusScore);
                int totalSoloBonus = Mathf.Max(0, networkData.SoloTotalBonus);

                int authoritativeHits = Mathf.Max(0, networkData.NotesHit);
                int authoritativeMisses = Mathf.Max(0, networkData.NotesMissed);
                int authoritativeTotalNotes = authoritativeHits + authoritativeMisses;
                if (authoritativeTotalNotes > 0)
                {
                    stats.TotalNotes = Mathf.Max(stats.TotalNotes, authoritativeTotalNotes);
                }

                int totalNotes = stats.TotalNotes;
                stats.NotesHit = Mathf.Clamp(authoritativeHits, 0, totalNotes);
                stats.Combo = Mathf.Max(0, networkData.CurrentCombo);
                stats.MaxCombo = Mathf.Max(stats.MaxCombo, networkData.CurrentStreak);

                uint gaugeTicks = player.BaseEngine != null ? player.BaseEngine.TicksPerFullSpBar : 0u;
                if (gaugeTicks > 0)
                {
                    stats.StarPowerTickAmount = (uint) Mathf.Clamp(
                        Mathf.RoundToInt(networkData.StarPowerAmount * gaugeTicks), 0, (int) gaugeTicks);
                }
                else if (stats.TotalStarPowerTicks > 0)
                {
                    stats.StarPowerTickAmount = (uint) Mathf.Clamp(
                        Mathf.RoundToInt(networkData.StarPowerAmount * stats.TotalStarPowerTicks),
                        0, (int) stats.TotalStarPowerTicks);
                }
                else
                {
                    stats.StarPowerTickAmount = 0;
                }

                stats.IsStarPowerActive = networkData.IsStarPowerActive;

                int totalStarPowerPhrases = Mathf.Max(stats.TotalStarPowerPhrases, networkData.TotalStarPowerPhrases);
                if (totalStarPowerPhrases > 0)
                {
                    stats.TotalStarPowerPhrases = totalStarPowerPhrases;
                    stats.StarPowerPhrasesHit = Mathf.Clamp(networkData.StarPowerPhrasesHit, 0, totalStarPowerPhrases);
                }
                else
                {
                    stats.StarPowerPhrasesHit = Mathf.Max(0, networkData.StarPowerPhrasesHit);
                }

                int maxMultiplier = player.BaseEngine?.BaseParameters?.MaxMultiplier ?? 4;
                int baseMultiplier = Mathf.Clamp((stats.Combo / 10) + 1, 1, maxMultiplier);
                int effectiveMultiplier = baseMultiplier;
                if (networkData.IsStarPowerActive)
                {
                    effectiveMultiplier = Mathf.Min(baseMultiplier * 2, maxMultiplier * 2);
                }

                stats.ScoreMultiplier = effectiveMultiplier;
                stats.BandMultiplier = effectiveMultiplier;

                stats.SoloBonuses = totalSoloBonus;

                stats.PendingScore = 0;
                stats.SustainScore = 0;
                stats.MultiplierScore = 0;

                int committedScore = sanitizedScore - totalSoloBonus - bandBonusScore;
                if (committedScore < 0)
                {
                    committedScore = Mathf.Max(0, sanitizedScore - totalSoloBonus);
                }

                if (committedScore + totalSoloBonus + bandBonusScore > sanitizedScore)
                {
                    bandBonusScore = Mathf.Max(0, sanitizedScore - (committedScore + totalSoloBonus));
                }

                stats.CommittedScore = committedScore;
                stats.NoteScore = committedScore;
                stats.BandBonusScore = bandBonusScore;

                if (stats.TotalNotes > 0)
                {
                    stats.Stars = Mathf.Clamp01(stats.Percent) * 5f;
                }

                switch (stats)
                {
                    case GuitarStats guitarStats:
                        guitarStats.Overstrums = Mathf.Max(0, networkData.Overstrums);
                        guitarStats.HoposStrummed = Mathf.Max(0, networkData.HoposStrummed);
                        guitarStats.GhostInputs = Mathf.Max(0, networkData.GhostInputs);
                        break;
                    case DrumsStats drumsStats:
                        drumsStats.Overhits = Mathf.Max(0, networkData.Overhits);
                        drumsStats.GhostsHit = Mathf.Clamp(networkData.GhostsHit, 0, drumsStats.TotalGhosts);
                        drumsStats.AccentsHit = Mathf.Clamp(networkData.AccentsHit, 0, drumsStats.TotalAccents);
                        drumsStats.DynamicsBonus = Mathf.Max(0, networkData.DynamicsBonus);
                        break;
                    case KeysStats keysStats:
                        keysStats.Overhits = Mathf.Max(0, networkData.Overhits);
                        break;
                }
            }

            int authoritativeBandScore = 0;
            float authoritativeBandStars = 0f;

            foreach (var player in _players)
            {
                authoritativeBandScore += player.Score;
                authoritativeBandScore += player.BaseStats.BandBonusScore;
                authoritativeBandStars += player.Stars;
            }

            if (_players.Count > 0)
            {
                BandScore = authoritativeBandScore;
                BandStars = authoritativeBandStars / _players.Count;
            }
        }

        /// <summary>
        /// Sends local player score results to other players via LiteNet networking.
        /// </summary>
        private void SendLiteNetScoreResults()
        {
            var networkService = YARG.Networking.Abstraction.NetworkingServiceFactory.Instance;
            if (networkService == null || !networkService.IsNetworkActive)
            {
                return;
            }

            var liteNetAdapter = networkService as YARG.Networking.Abstraction.LiteNetNetworkingAdapter;
            if (liteNetAdapter == null)
            {
                return;
            }

            foreach (var player in _players)
            {
                // Only send results for local (non-remote) players
                // Remote players have null Bindings
                if (player.Player.Bindings == null)
                {
                    continue;
                }

                // Skip bots
                if (player.Player.Profile.IsBot)
                {
                    continue;
                }

                var stats = player.BaseStats;
                bool isHighScore = player.Score > player.LastHighScore;
                bool isFullCombo = stats.IsFullCombo;

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
                        // Check if we're in multiplayer (LiteNet or Mirror)
                        bool isLiteNetMultiplayer = YARG.Networking.Abstraction.NetworkingServiceFactory.Instance?.IsNetworkActive == true;
                        bool isMirrorMultiplayer = !isLiteNetMultiplayer && 
                                                   Networking.YargNetworkManager.Instance != null &&
                                                   Networking.YargNetworkManager.Instance.isNetworkActive;
                        bool isMultiplayer = isLiteNetMultiplayer || isMirrorMultiplayer;

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
                // Check if we're in multiplayer (LiteNet or Mirror)
                bool isLiteNetMultiplayer = YARG.Networking.Abstraction.NetworkingServiceFactory.Instance?.IsNetworkActive == true;
                bool isMirrorMultiplayer = !isLiteNetMultiplayer && 
                                           Networking.YargNetworkManager.Instance != null && 
                                           Networking.YargNetworkManager.Instance.isNetworkActive;
                bool isMultiplayer = isLiteNetMultiplayer || isMirrorMultiplayer;
                
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
            if (SettingsManager.Settings.NoFailMode.Value || IsPractice)
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

        private bool IsMultiplayerActive()
        {
            // Check if we're in multiplayer (LiteNet or Mirror)
            bool isLiteNetMultiplayer = YARG.Networking.Abstraction.NetworkingServiceFactory.Instance?.IsNetworkActive == true;
            bool isMirrorMultiplayer = !isLiteNetMultiplayer && 
                                       Networking.YargNetworkManager.Instance != null &&
                                       Networking.YargNetworkManager.Instance.isNetworkActive;
            return _multiplayerSync != null && (isLiteNetMultiplayer || isMirrorMultiplayer);
        }

        private void HandleMultiplayerSongFailed()
        {
            // Check which networking backend is active
            bool isLiteNetActive = YARG.Networking.Abstraction.NetworkingServiceFactory.Instance?.IsNetworkActive == true;
            
            if (isLiteNetActive)
            {
                // TODO: Implement LiteNet failure reporting
                // For now, just run the failure sequence
                _ = RunBandFailureSequenceAsync();
                return;
            }
            
            // Mirror path
            var networkManager = Networking.YargNetworkManager.Instance;
            if (networkManager == null || !networkManager.isNetworkActive)
            {
                _ = RunBandFailureSequenceAsync();
                return;
            }

            if (_multiplayerFailureReported)
            {
                return;
            }

            _multiplayerFailureReported = true;

            bool sentReport = false;
            foreach (var playerData in networkManager.GetAllPlayers())
            {
                if (playerData != null && playerData.IsLocalUser)
                {
                    playerData.CmdReportSongFailed();
                    sentReport = true;
                }
            }

            if (!sentReport)
            {
                _ = RunBandFailureSequenceAsync();
            }
        }

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
