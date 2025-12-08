using System;
using System.Linq;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Mirror;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using YARG.Core;
using YARG.Core.Audio;
using YARG.Core.Engine.Drums;
using YARG.Core.Engine.Guitar;
using YARG.Core.Engine.Keys;
using YARG.Core.Engine.Vocals;
using YARG.Core.Input;
using YARG.Core.Logging;
using YARG.Core.Replays;
using YARG.Core.Replays.Analyzer;
using YARG.Core.Song;
using YARG.Localization;
using YARG.Networking;
using YARG.Networking.Abstraction;
using YARG.Net.Sessions;
using YARG.Menu.MusicLibrary;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YARG.Scores;
using YARG.Song;
using YARG.Playlists;
using YARG.Helpers.Extensions;
using YARG.Core.Engine;
using YARG.Playback;
using YARG.Settings;

namespace YARG.Menu.ScoreScreen
{
    public class ScoreScreenMenu : MonoBehaviour
    {
        [SerializeField]
        private Transform _cardContainer;
        [SerializeField]
        private Image _sourceIcon;
        [SerializeField]
        private TextMeshProUGUI _songTitle;
        [SerializeField]
        private TextMeshProUGUI _artistName;
        [SerializeField]
        private StarView _bandStarView;
        [SerializeField]
        private TextMeshProUGUI _bandScore;
        [SerializeField]
        private TextMeshProUGUI _bandScoreNotSavedMessage;
        [SerializeField]
        private ScrollRect _cardScrollRect;
        [SerializeField]
        private float _horizontalScrollRate = 30f;
        [SerializeField]
        private float _verticalScrollRate = 15f;

        [Space]
        [SerializeField]
        private GuitarScoreCard _guitarCardPrefab;
        [SerializeField]
        private DrumsScoreCard _drumsCardPrefab;
        [SerializeField]
        private VocalsScoreCard _vocalsCardPrefab;
        [SerializeField]
        private ProKeysScoreCard _proKeysCardPrefab;
        [SerializeField]
        private ProKeysScoreCard _fiveLaneKeysCardPrefab;

        private bool _analyzingReplay;

        private bool _restartingSong;

        private readonly List<IScoreCard<BaseStats>> _scoreCards = new();

        private bool _isMultiplayer;
        private bool _isHost;
        private bool _advancing;
        private NetworkPlayerData _localNetworkPlayer;
        private readonly List<NetworkPlayerData> _networkPlayers = new();
        private TextMeshProUGUI _readyStatusLabel;
        
        // Track received remote player score results
        private readonly Dictionary<string, RemotePlayerScoreResult> _remoteScoreResults = new();
        
        private struct RemotePlayerScoreResult
        {
            public string PlayerName;
            public bool IsHighScore;
            public bool IsFullCombo;
            public int Score;
            public int MaxCombo;
            public int NotesHit;
            public int NotesMissed;
        }

        private void OnEnable()
        {
            var song = GlobalVariables.State.CurrentSong;

            SetNavigationScheme();

            if (GlobalVariables.State.ScoreScreenStats is null)
            {
                YargLogger.LogError("Score screen stats was null!");
                return;
            }

            var scoreScreenStats = GlobalVariables.State.ScoreScreenStats.Value;
            
            // Subscribe to remote score results for LiteNet multiplayer
            SubscribeToRemoteScoreResults();

#if UNITY_EDITOR || YARG_NIGHTLY_BUILD || YARG_TEST_BUILD
            // Do analysis of replay before showing any score data
            // This will make it so that if the analysis takes a while the screen is blank
            // (kinda like a loading screen)
            // 
            // NOTE: In multiplayer, unison bonuses are coordinated across the network,
            // so the replay analysis (which uses a single EngineManager for all players)
            // should produce consistent results with networked gameplay.
            try
            {
                if (!AnalyzeReplay(song, scoreScreenStats.ReplayInfo))
                {
                    DialogManager.Instance.ShowMessage("Inconsistent Replay Results!",
                        "The replay analysis for this run produced inconsistent results to the actual gameplay.\n" +
                        "Please report this issue to the YARG developers on GitHub or Discord.\n\n" +
                        $"Chart Hash: {song.Hash}");
                }
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex, $"Failed to analyze replay! Song hash: {song.Hash}");
                DialogManager.Instance.ShowMessage("Failed To Analyze Replay!",
                    "The replay analysis for this run resulted in an unexpected error.\n" +
                    "Please report this issue to the YARG developers on GitHub or Discord.\n\n" +
                    $"Chart Hash: {song.Hash}");
            }
#endif

            // Play audience chatter
            if (SettingsManager.Settings.UseCrowdFx.Value == CrowdFxMode.Enabled)
            {
                GlobalAudioHandler.PlaySoundEffect(SfxSample.Chatter, 1.0);
            }

            // Set text
            _songTitle.text = song.Name;
            _artistName.text = song.Artist;
            _bandScoreNotSavedMessage.gameObject.SetActive(
                !ScoreContainer.IsBandScoreValid(PersistentState.Default.SongSpeed));

            // Set speed text (if not at 100% speed)
            if (!Mathf.Approximately(GlobalVariables.State.SongSpeed, 1f))
            {
                var speed = Localize.Percent(GlobalVariables.State.SongSpeed);

                _songTitle.text += $" ({speed})";
            }

            // Set the band score and stars
            _bandStarView.SetStars(scoreScreenStats.BandStars);
            _bandScore.text = scoreScreenStats.BandScore.ToString("N0");

            // Put the scores in!
            CreateScoreCards(scoreScreenStats);
            
            // Apply any remote score results that were received before/during card creation
            ApplyStoredRemoteScoreResults();

            _sourceIcon.sprite = SongSources.SourceToIcon(song.Source);

            //set restarting state
            _restartingSong = false;

            InitializeMultiplayerReady();
        }

        private void OnDisable()
        {
            CleanupMultiplayerReady();
            UnsubscribeFromRemoteScoreResults();
            _remoteScoreResults.Clear();

            MusicLibraryMenu.CurrentlyPlaying = GlobalVariables.State.CurrentSong;
            if (!GlobalVariables.State.PlayingAShow && !_restartingSong)
            {
                GlobalVariables.State = PersistentState.Default;
            }

            if (SettingsManager.Settings.UseCrowdFx.Value == CrowdFxMode.Enabled)
            {
                GlobalAudioHandler.StopSoundEffect(SfxSample.Chatter, 1.0);
            }

            Navigator.Instance.PopScheme();
        }

        private void CreateScoreCards(ScoreScreenStats scoreScreenStats)
        {
            int fcCount = 0;
            int highScoreCount = 0;

            foreach (var score in scoreScreenStats.PlayerScores)
            {
                // Bots don't get vox
                if (!score.Player.Profile.IsBot)
                {
                    // We intentionally don't count both high score and full combo
                    if (score.Stats.IsFullCombo)
                    {
                        fcCount++;
                    }
                    else if (score.IsHighScore)
                    {
                        highScoreCount++;
                    }
                }

                IScoreCard<BaseStats> card = null;

                switch (score.Player.Profile.GameMode)
                {
                    case GameMode.FiveFretGuitar:
                    {
                        card = Instantiate(_guitarCardPrefab, _cardContainer);
                        ((ScoreCard<GuitarStats>)card).Initialize(score.IsHighScore, score.Player, score.Stats as GuitarStats);
                        break;
                    }
                    case GameMode.FourLaneDrums:
                    case GameMode.FiveLaneDrums:
                    case GameMode.EliteDrums:
                    {
                        card = Instantiate(_drumsCardPrefab, _cardContainer);
                        ((ScoreCard<DrumsStats>)card).Initialize(score.IsHighScore, score.Player, score.Stats as DrumsStats);
                        break;
                    }
                    case GameMode.Vocals:
                    {
                        card = Instantiate(_vocalsCardPrefab, _cardContainer);
                        ((ScoreCard<VocalsStats>)card).Initialize(score.IsHighScore, score.Player, score.Stats as VocalsStats);
                        break;
                    }
                    case GameMode.ProKeys:
                    {
                        if (score.Player.Profile.CurrentInstrument is Instrument.ProKeys)
                        {
                            card = Instantiate(_proKeysCardPrefab, _cardContainer);
                        }
                        else
                        {
                            card = Instantiate(_fiveLaneKeysCardPrefab, _cardContainer);
                        }
                        ((ScoreCard<KeysStats>) card).Initialize(score.IsHighScore, score.Player,
                            score.Stats as KeysStats);
                        break;
                    }
                }

                Debug.Assert(card != null, $"ScoreCard not initialized for GameMode: {score.Player.Profile.GameMode}");
                card.SetCardContents();
                _scoreCards.Add(card);
            }

            // Mark that the music library should refresh when next opened
            if (GlobalVariables.State.ScoreScreenStats.Value.PlayerScores.Any(e => !e.Player.Profile.IsBot))
            {
                MusicLibraryMenu.NeedsReload();
            }

            // Make sure to update the canvases since we *just* added the score cards
            Canvas.ForceUpdateCanvases();

            // If the scroll bar is active, make it all the way to the left
            InitializeScrollRect();

            // As a final bonus, play the appropriate full combo/high score vox samples
            PlayScoreVox(fcCount, highScoreCount);
        }

        private async void InitializeScrollRect()
        {
            await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate);
            _cardScrollRect.horizontalNormalizedPosition = 0f;
        }

        private static void PlayScoreVox(int fcCount, int highScoreCount)
        {
            if (fcCount > 0)
            {
                GlobalAudioHandler.PlayVoxSample(VoxSample.FullCombo);
                YargLogger.LogInfo("Playing full combo vox sample");
            }

            if (fcCount > 1)
            {
                YargLogger.LogDebug($"Playing full combo vox sample for {fcCount} times");
                switch (fcCount)
                {
                    case 2:
                        GlobalAudioHandler.PlayVoxSample(VoxSample.Times2);
                        break;
                    case 3:
                        GlobalAudioHandler.PlayVoxSample(VoxSample.Times3);
                        break;
                    case 4:
                        GlobalAudioHandler.PlayVoxSample(VoxSample.Times4);
                        break;
                    case 5:
                        GlobalAudioHandler.PlayVoxSample(VoxSample.Times5);
                        break;
                    case 6:
                        GlobalAudioHandler.PlayVoxSample(VoxSample.Times6);
                        break;
                    case > 6:
                        GlobalAudioHandler.PlayVoxSample(VoxSample.TimesMany);
                        break;
                }
            }

            if (highScoreCount > 0)
            {
                GlobalAudioHandler.PlayVoxSample(VoxSample.HighScore);
                YargLogger.LogInfo("Playing high score vox sample");
            }

            if (highScoreCount > 1)
            {
                switch (highScoreCount)
                {
                    case 2:
                        GlobalAudioHandler.PlayVoxSample(VoxSample.Times2);
                        break;
                    case 3:
                        GlobalAudioHandler.PlayVoxSample(VoxSample.Times3);
                        break;
                    case 4:
                        GlobalAudioHandler.PlayVoxSample(VoxSample.Times4);
                        break;
                    case 5:
                        GlobalAudioHandler.PlayVoxSample(VoxSample.Times5);
                        break;
                    case 6:
                        GlobalAudioHandler.PlayVoxSample(VoxSample.Times6);
                        break;
                    case > 6:
                        GlobalAudioHandler.PlayVoxSample(VoxSample.TimesMany);
                        break;
                }
            }
        }

#nullable enable
        private bool AnalyzeReplay(SongEntry songEntry, ReplayInfo? replayEntry)
#nullable disable
        {
            _analyzingReplay = true;

            var chart = songEntry.LoadChart();
            if (chart == null)
            {
                YargLogger.LogError("Chart did not load");
                _analyzingReplay = false;
                return true;
            }

            if (GlobalVariables.State.ScoreScreenStats.Value.PlayerScores.All(e => e.Player.Profile.IsBot))
            {
                YargLogger.LogInfo("No human players in ReplayEntry.");
                _analyzingReplay = false;
                return true;
            }

            if (replayEntry == null)
            {
                YargLogger.LogError("ReplayEntry is null");
                _analyzingReplay = false;
                return true;
            }

            var replayOptions = new ReplayReadOptions
            {
                KeepFrameTimes = GlobalVariables.VerboseReplays
            };
            var (result, data) = ReplayIO.TryLoadData(replayEntry, replayOptions);
            if (result != ReplayReadResult.Valid)
            {
                YargLogger.LogFormatError("Replay did not load. {0}", result);
                _analyzingReplay = false;
                return true;
            }

            var results = ReplayAnalyzer.AnalyzeReplay(chart, replayEntry, data);
            bool allPass = true;

            for (int i = 0; i < results.Length; i++)
            {
                var analysisResult = results[i];

                // Always print the stats in debug mode
#if UNITY_EDITOR || YARG_TEST_BUILD
                YargLogger.LogFormatInfo("({0}, {1}/{2}) Verification Result: {3}. Stats:\n{4}",
                    data.Frames[i].Profile.Name, data.Frames[i].Profile.CurrentInstrument,
                    data.Frames[i].Profile.CurrentDifficulty, item4: analysisResult.Passed ? "Passed" : "Failed",
                    item5: analysisResult.StatLog);
#endif

                if (!analysisResult.Passed)
                {
#if !(UNITY_EDITOR || YARG_TEST_BUILD)
                    YargLogger.LogFormatWarning("({0}, {1}/{2}) FAILED verification. Stats:\n{3}",
                        data.Frames[i].Profile.Name, data.Frames[i].Profile.CurrentInstrument,
                        data.Frames[i].Profile.CurrentDifficulty, item4: analysisResult.StatLog);
#endif
                    _analyzingReplay = false;
                    allPass = false;
                }
            }

            _analyzingReplay = false;
            return allPass;
        }

        private NavigationScheme.Entry _continueButtonEntry;
        private NavigationScheme.Entry _endEarlyButtonEntry;
        private NavigationScheme.Entry _restartButtonEntry;
        private NavigationScheme.Entry _removeFavoriteButtonEntry;
        private NavigationScheme.Entry _addFavoriteButtonEntry;
        private NavigationScheme.Entry _scrollLeftEntry;
        private NavigationScheme.Entry _scrollRightEntry;
        private NavigationScheme.Entry _scrollUpEntry;
        private NavigationScheme.Entry _scrollDownEntry;

        private void SetNavigationScheme()
        {
            var song = GlobalVariables.State.CurrentSong;

            _continueButtonEntry = new NavigationScheme.Entry(MenuAction.Green, "Menu.Common.Continue", () =>
                {
                    if (!_analyzingReplay)
                    {
                        GlobalVariables.State.ShowIndex++;
                        if (GlobalVariables.State.PlayingAShow &&
                            GlobalVariables.State.ShowIndex < GlobalVariables.State.ShowSongs.Count)
                        {
                            // Reset CurrentSong and launch back into the Gameplay scene
                            GlobalVariables.State.CurrentSong =
                                GlobalVariables.State.ShowSongs[GlobalVariables.State.ShowIndex];
                            GlobalVariables.Instance.LoadScene(SceneIndex.Gameplay);
                        }
                        else
                        {
                            GlobalVariables.State.PlayingAShow = false;
                            GlobalVariables.Instance.LoadScene(SceneIndex.Menu);
                        }
                    }
                });

            _endEarlyButtonEntry = new NavigationScheme.Entry(MenuAction.Red, "Menu.ScoreScreen.EndSetlistEarly", () =>
            {
                GlobalVariables.State.PlayingAShow = false;
                GlobalVariables.Instance.LoadScene(SceneIndex.Menu);
            });

            _restartButtonEntry = new NavigationScheme.Entry(MenuAction.Yellow, "Menu.ScoreScreen.RestartSong", () =>
            {
                _restartingSong = true;
                GlobalVariables.Instance.LoadScene(SceneIndex.Gameplay);
            });

            _addFavoriteButtonEntry = new NavigationScheme.Entry(MenuAction.Blue, "Menu.MusicLibrary.Popup.Item.AddToFavorites", () =>
                {
                    YargLogger.LogInfo("added favorite");
                    PlaylistContainer.FavoritesPlaylist.AddSong(song);
                    UpdateNavigationScheme(true);
                });

            _removeFavoriteButtonEntry = new NavigationScheme.Entry(MenuAction.Blue, "Menu.MusicLibrary.Popup.Item.RemoveFromFavorites", () =>
                {
                    YargLogger.LogInfo("removed favorite");
                    PlaylistContainer.FavoritesPlaylist.RemoveSong(song);
                    UpdateNavigationScheme(true);
                });

            _scrollLeftEntry = new NavigationScheme.Entry(MenuAction.Left, "Menu.Common.Scroll", context =>
                {
                    _cardScrollRect.MoveHorizontalInUnits(-1 * _horizontalScrollRate);
                });

            _scrollRightEntry = new NavigationScheme.Entry(MenuAction.Right, "Menu.Common.Scroll", context =>
                {
                    _cardScrollRect.MoveHorizontalInUnits(_horizontalScrollRate);
                });

            _scrollUpEntry = new NavigationScheme.Entry(MenuAction.Up, "Menu.Common.Scroll", context =>
                {
                    ScrollScoreCard(context.Player, _verticalScrollRate);
                });

            _scrollDownEntry = new NavigationScheme.Entry(MenuAction.Down, "Menu.Common.Scroll", context =>
                {
                    ScrollScoreCard(context.Player, -1 * _verticalScrollRate);
                });

            UpdateNavigationScheme();
        }

        private void ScrollScoreCard(Player.YargPlayer player, float delta)
        {
            var card = _scoreCards.FirstOrDefault(card => card.Player == player);
            card?.ScrollStats(delta);
        }

        private void InitializeMultiplayerReady()
        {
            var networkService = NetworkingServiceFactory.Instance;
            if (networkService == null || !networkService.IsNetworkActive)
            {
                _isMultiplayer = false;
                return;
            }

            _isMultiplayer = true;
            _isHost = networkService.IsHosting;
            _advancing = false;

            _networkPlayers.Clear();

            // CRITICAL: Reset all player ready states BEFORE subscribing to events
            // This prevents stale ready states from immediately triggering the all-ready check
            // when events are subscribed. Without this, players who were ready in difficulty select
            // would still appear ready when entering the score screen.
            Debug.Log("[ScoreScreenMenu] InitializeMultiplayerReady - resetting all player ready states before subscribing");
            networkService.ResetAllPlayersReadyState();

            // Use abstraction layer for getting all players
            var players = networkService.GetAllPlayers();
            foreach (var player in players)
            {
                if (player == null)
                {
                    continue;
                }

                _networkPlayers.Add(player);
                player.OnReadyStateChangedEvent += HandleReadyStateChanged;

                if (player.IsLocalUser)
                {
                    _localNetworkPlayer = player;
                }
            }

            // No need to explicitly set local player to not-ready since we just reset everyone above
            // This also avoids firing an event that could cause issues

            CreateReadyStatusLabel();
            UpdateReadyStatusLabel();

            if (_isMultiplayer)
            {
                ToastManager.ToastInformation(Localize.Key("Menu.ScoreScreen.ReadyPrompt"));
                UpdateNavigationScheme(true);
            }
        }

        private void CleanupMultiplayerReady()
        {
            if (!_isMultiplayer)
            {
                return;
            }

            foreach (var player in _networkPlayers)
            {
                if (player != null)
                {
                    player.OnReadyStateChangedEvent -= HandleReadyStateChanged;
                }
            }

            _networkPlayers.Clear();
            _localNetworkPlayer = null;
            _isMultiplayer = false;
            _isHost = false;
            _advancing = false;

            if (_readyStatusLabel != null)
            {
                Destroy(_readyStatusLabel.gameObject);
                _readyStatusLabel = null;
            }
        }
        
        #region Remote Score Results
        
        private LiteNetNetworkingAdapter _liteNetAdapter;
        
        private void SubscribeToRemoteScoreResults()
        {
            var networkService = NetworkingServiceFactory.Instance;
            if (networkService == null || !networkService.IsNetworkActive)
            {
                return;
            }
            
            _liteNetAdapter = networkService as LiteNetNetworkingAdapter;
            if (_liteNetAdapter != null)
            {
                _liteNetAdapter.OnScoreResultsReceived += HandleRemoteScoreResults;
                
                // Process any cached results that arrived before we subscribed
                // (score results are often sent during scene transition before the score screen loads)
                var cachedResults = _liteNetAdapter.GetCachedScoreResults();
                foreach (var kvp in cachedResults)
                {
                    var result = kvp.Value;
                    Debug.Log($"[ScoreScreenMenu] Processing cached score result: player={kvp.Key}, highScore={result.IsHighScore}, FC={result.IsFullCombo}");
                    HandleRemoteScoreResults(kvp.Key, result.IsHighScore, result.IsFullCombo, result.Score, result.MaxCombo, result.NotesHit, result.NotesMissed);
                }
            }
        }
        
        private void UnsubscribeFromRemoteScoreResults()
        {
            if (_liteNetAdapter != null)
            {
                _liteNetAdapter.OnScoreResultsReceived -= HandleRemoteScoreResults;
                _liteNetAdapter = null;
            }
        }
        
        private void HandleRemoteScoreResults(string playerName, bool isHighScore, bool isFullCombo, int score, int maxCombo, int notesHit, int notesMissed)
        {
            Debug.Log($"[ScoreScreenMenu] Received remote score results: player={playerName}, highScore={isHighScore}, FC={isFullCombo}, score={score}");
            
            // Store the result for any late-arriving data
            _remoteScoreResults[playerName] = new RemotePlayerScoreResult
            {
                PlayerName = playerName,
                IsHighScore = isHighScore,
                IsFullCombo = isFullCombo,
                Score = score,
                MaxCombo = maxCombo,
                NotesHit = notesHit,
                NotesMissed = notesMissed
            };
            
            // Update the score card for this player if it already exists
            UpdateScoreCardForRemotePlayer(playerName, isHighScore, isFullCombo);
        }
        
        /// <summary>
        /// Updates an existing score card with remote player achievement data.
        /// </summary>
        private void UpdateScoreCardForRemotePlayer(string playerName, bool isHighScore, bool isFullCombo)
        {
            foreach (var card in _scoreCards)
            {
                if (card.Player?.Profile?.Name == playerName)
                {
                    // Skip cards that belong to local players - they already have correct data from local gameplay.
                    // Remote players have no bindings (null), while local players have bindings assigned.
                    // This check is critical when players have the same name (e.g., same profile on different machines).
                    if (card.Player.Bindings != null)
                    {
                        Debug.Log($"[ScoreScreenMenu] Skipping score card for '{playerName}' - this is a local player (has bindings)");
                        continue;
                    }
                    
                    // Found a remote player's card - update its display
                    Debug.Log($"[ScoreScreenMenu] Updating score card for remote player '{playerName}': highScore={isHighScore}, FC={isFullCombo}");
                    
                    // Get the underlying MonoBehaviour to access the colorizer and tag
                    var cardComponent = card as MonoBehaviour;
                    Debug.Log($"[ScoreScreenMenu] cardComponent as MonoBehaviour: {(cardComponent != null ? cardComponent.name : "NULL")}");
                    
                    if (cardComponent != null)
                    {
                        var colorizer = cardComponent.GetComponent<ScoreCardColorizer>();
                        Debug.Log($"[ScoreScreenMenu] colorizer found: {colorizer != null}");
                        
                        if (colorizer != null)
                        {
                            // Update the card appearance based on achievements
                            // Use hardcoded strings to match local player ScoreCard behavior
                            if (isFullCombo)
                            {
                                Debug.Log($"[ScoreScreenMenu] Setting card color to GOLD for FC");
                                colorizer.SetCardColor(ScoreCardColorizer.ScoreCardColor.Gold);
                                SetScoreCardTag(cardComponent, "Full Combo");
                            }
                            else if (isHighScore)
                            {
                                Debug.Log($"[ScoreScreenMenu] Setting card color to BLUE for high score");
                                colorizer.SetCardColor(ScoreCardColorizer.ScoreCardColor.Blue);
                                SetScoreCardTag(cardComponent, "High Score");
                            }
                            else
                            {
                                Debug.Log($"[ScoreScreenMenu] No special achievement, not updating card appearance");
                            }
                        }
                        else
                        {
                            Debug.LogWarning($"[ScoreScreenMenu] ScoreCardColorizer not found on card component!");
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"[ScoreScreenMenu] Card could not be cast to MonoBehaviour!");
                    }
                    // Found and updated the remote player's card - done
                    return;
                }
            }
        }
        
        /// <summary>
        /// Sets the tag text on a score card via reflection since the method is private.
        /// </summary>
        private void SetScoreCardTag(MonoBehaviour cardComponent, string tagText)
        {
            Debug.Log($"[ScoreScreenMenu] SetScoreCardTag called with tagText='{tagText}'");
            
            // Try to find and invoke the ShowTag method via reflection
            // The method is defined in ScoreCard<T> base class
            System.Reflection.MethodInfo showTagMethod = null;
            
            var currentType = cardComponent.GetType();
            while (currentType != null && currentType != typeof(MonoBehaviour))
            {
                Debug.Log($"[ScoreScreenMenu] Searching type for ShowTag: {currentType.Name}");
                
                showTagMethod = currentType.GetMethod("ShowTag", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
                
                if (showTagMethod != null)
                {
                    Debug.Log($"[ScoreScreenMenu] Found ShowTag method in type: {currentType.Name}");
                    break;
                }
                
                currentType = currentType.BaseType;
            }
            
            if (showTagMethod != null)
            {
                try
                {
                    showTagMethod.Invoke(cardComponent, new object[] { tagText });
                    Debug.Log($"[ScoreScreenMenu] Successfully invoked ShowTag with '{tagText}'");
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"[ScoreScreenMenu] Failed to invoke ShowTag: {ex.Message}");
                }
            }
            else
            {
                Debug.LogWarning($"[ScoreScreenMenu] Could not find ShowTag method via reflection");
                
                // Fallback: try to access the fields directly
                System.Reflection.FieldInfo tagField = null;
                System.Reflection.FieldInfo tagTextField = null;
                
                currentType = cardComponent.GetType();
                while (currentType != null && currentType != typeof(MonoBehaviour))
                {
                    tagField = currentType.GetField("_tagGameObject", 
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
                    tagTextField = currentType.GetField("_tagText", 
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
                    
                    if (tagField != null && tagTextField != null)
                        break;
                    
                    currentType = currentType.BaseType;
                }
                
                if (tagField != null && tagTextField != null)
                {
                    var tagObject = tagField.GetValue(cardComponent) as GameObject;
                    var tagTextComponent = tagTextField.GetValue(cardComponent) as TextMeshProUGUI;
                    
                    if (tagObject != null && tagTextComponent != null)
                    {
                        tagObject.SetActive(true);
                        tagTextComponent.text = tagText;
                        Debug.Log($"[ScoreScreenMenu] Successfully set tag via field access to '{tagText}'");
                    }
                }
            }
        }
        
        /// <summary>
        /// Checks if we have received high score/FC data for a remote player.
        /// </summary>
        public bool TryGetRemoteScoreResult(string playerName, out bool isHighScore, out bool isFullCombo)
        {
            if (_remoteScoreResults.TryGetValue(playerName, out var result))
            {
                isHighScore = result.IsHighScore;
                isFullCombo = result.IsFullCombo;
                return true;
            }
            
            isHighScore = false;
            isFullCombo = false;
            return false;
        }
        
        /// <summary>
        /// Applies all stored remote score results to the score cards.
        /// Called after score cards are created to update their appearance.
        /// </summary>
        private void ApplyStoredRemoteScoreResults()
        {
            foreach (var kvp in _remoteScoreResults)
            {
                var result = kvp.Value;
                Debug.Log($"[ScoreScreenMenu] Applying stored remote score result: player={result.PlayerName}, highScore={result.IsHighScore}, FC={result.IsFullCombo}");
                UpdateScoreCardForRemotePlayer(result.PlayerName, result.IsHighScore, result.IsFullCombo);
            }
        }
        
        #endregion

        private void CreateReadyStatusLabel()
        {
            if (!_isMultiplayer || _readyStatusLabel != null)
            {
                return;
            }

            var anchorParent = _bandScore != null ? _bandScore.transform.parent : transform;
            var labelObject = new GameObject("ReadyStatus", typeof(RectTransform));
            labelObject.transform.SetParent(anchorParent, false);

            var rect = labelObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0f);
            rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = new Vector2(0f, 32f);

            _readyStatusLabel = labelObject.AddComponent<TextMeshProUGUI>();
            _readyStatusLabel.alignment = TextAlignmentOptions.Center;
            _readyStatusLabel.enableWordWrapping = false;
            _readyStatusLabel.text = string.Empty;

            if (_bandScore != null)
            {
                _readyStatusLabel.font = _bandScore.font;
                _readyStatusLabel.fontSize = _bandScore.fontSize;
                _readyStatusLabel.color = _bandScore.color;
            }
        }

        private void UpdateReadyStatusLabel()
        {
            if (!_isMultiplayer || _readyStatusLabel == null)
            {
                return;
            }

            int totalPlayers = 0;
            int readyPlayers = 0;

            foreach (var player in _networkPlayers)
            {
                if (player == null)
                {
                    continue;
                }

                totalPlayers++;
                if (player.IsReady)
                {
                    readyPlayers++;
                }
            }

            if (totalPlayers == 0)
            {
                _readyStatusLabel.text = string.Empty;
                return;
            }

            _readyStatusLabel.text = Localize.KeyFormat("Menu.ScoreScreen.ReadyStatus", readyPlayers, totalPlayers);
        }

        private NavigationScheme.Entry CreateReadyEntry()
        {
            string localizationKey = (_localNetworkPlayer != null && _localNetworkPlayer.IsReady)
                ? "Menu.ScoreScreen.Unready"
                : "Menu.ScoreScreen.ReadyUp";

            return new NavigationScheme.Entry(MenuAction.Green, localizationKey, ToggleReady);
        }

        private void ToggleReady()
        {
            if (!_isMultiplayer || _localNetworkPlayer == null || _advancing)
            {
                return;
            }

            var networkService = NetworkingServiceFactory.Instance;
            if (networkService == null)
            {
                return;
            }

            bool targetState = !_localNetworkPlayer.IsReady;
            networkService.SetPlayerReady(targetState);
            UpdateNavigationScheme(true);
        }

        private void HandleReadyStateChanged(bool newReadyState)
        {
            Debug.Log($"[ScoreScreenMenu] HandleReadyStateChanged called with newReadyState={newReadyState}, _isMultiplayer={_isMultiplayer}, _isHost={_isHost}, _advancing={_advancing}");
            
            if (!_isMultiplayer)
            {
                return;
            }

            UpdateReadyStatusLabel();
            UpdateNavigationScheme(true);

            var networkService = NetworkingServiceFactory.Instance;
            if (!_isHost || _advancing || networkService == null)
            {
                Debug.Log($"[ScoreScreenMenu] HandleReadyStateChanged early return - isHost={_isHost}, advancing={_advancing}, networkService null={networkService == null}");
                return;
            }

            // Only check if all players are ready when someone BECOMES ready
            // If someone went to not-ready (newReadyState == false), don't bother checking
            if (!newReadyState)
            {
                Debug.Log($"[ScoreScreenMenu] HandleReadyStateChanged - player went to not-ready, skipping all-ready check");
                return;
            }

            bool allReady = networkService.AreAllPlayersReady();
            Debug.Log($"[ScoreScreenMenu] HandleReadyStateChanged - AreAllPlayersReady returned {allReady}");
            
            if (allReady)
            {
                _advancing = true;
                ToastManager.ToastSuccess(Localize.Key("Menu.ScoreScreen.AllReady"));

                // Use abstraction layer to advance - it handles both Mirror and LiteNet
                networkService.AdvanceAfterScoreScreen();
            }
        }

        private void UpdateNavigationScheme(bool reset = false)
        {
            if (reset)
            {
                Navigator.Instance.PopScheme();
            }

            var buttons = new List<NavigationScheme.Entry>();

            if (_isMultiplayer)
            {
                buttons.Add(CreateReadyEntry());

                if (_isHost && GlobalVariables.State.PlayingAShow &&
                    GlobalVariables.State.ShowIndex + 1 < GlobalVariables.State.ShowSongs.Count)
                {
                    buttons.Add(_endEarlyButtonEntry);
                }
            }
            else
            {
                buttons.Add(_continueButtonEntry);
                buttons.Add(_restartButtonEntry);

                if (GlobalVariables.State.PlayingAShow &&
                    GlobalVariables.State.ShowIndex + 1 < GlobalVariables.State.ShowSongs.Count)
                {
                    buttons.Insert(1, _endEarlyButtonEntry);
                }
            }

            var song = GlobalVariables.State.CurrentSong;
            var isFavorited = PlaylistContainer.FavoritesPlaylist.ContainsSong(song);

            if (isFavorited)
            {
                buttons.Add(_removeFavoriteButtonEntry);
            }
            else
            {
                buttons.Add(_addFavoriteButtonEntry);
            }

            buttons.Add(_scrollLeftEntry);
            buttons.Add(_scrollRightEntry);
            buttons.Add(_scrollUpEntry);
            buttons.Add(_scrollDownEntry);

            Navigator.Instance.PushScheme(new(buttons, true));
        }
    }
}
