using System;
using System.Collections.Generic;
using PlasticBand.Haptics;
using UnityEngine;
using UnityEngine.InputSystem;
using YARG.Core.Audio;
using YARG.Core.Chart;
using YARG.Core.Engine;
using YARG.Core.Input;
using YARG.Core.Logging;
using YARG.Core.Replays;
using YARG.Gameplay.HUD;
using YARG.Helpers.Extensions;
using YARG.Input;
using YARG.Playback;
using YARG.Player;
using YARG.Settings;
using YARG.Networking.Abstraction;

namespace YARG.Gameplay.Player
{
    public abstract class BasePlayer : GameplayBehaviour
    {
        public int HighwayIndex { get; private set; }

        public YargPlayer Player { get; private set; }
        
        /// <summary>
        /// Network player data for multiplayer. Only set for multiplayer games.
        /// </summary>
        private NetworkPlayerData _networkPlayerData;
        private IRemotePlayerSimulation _remoteSimulation;

        public float NoteSpeed
        {
            get
            {
                float noteSpeed = Player.Profile.NoteSpeed * _noteSpeedDifficultyScale;

                // If we're in a replay, don't change the note speed (it should be like a video
                // slowing down/speeding up). The actual song speed should be taken into account though,
                // which is saved in the engine parameter override.
                if (Player.IsReplay)
                {
                    return noteSpeed / (float) Player.EngineParameterOverride.SongSpeed;
                }

                if (GameManager.IsPractice && GameManager.SongSpeed < 1)
                {
                    return noteSpeed;
                }

                return noteSpeed / GameManager.SongSpeed;
            }
        }

        /// <summary>
        /// The player's input calibration, in seconds.
        /// </summary>
        /// <remarks>
        /// Be aware that this value is negated!
        /// Positive calibration settings will result in a negative number here.
        /// </remarks>
        public double InputCalibration => -Player.Profile.InputCalibrationSeconds;

        public abstract BaseEngine BaseEngine { get; }

        public BaseStats BaseStats => BaseEngine.BaseStats;
        public BaseEngineParameters BaseParameters => BaseEngine.BaseParameters;

        public abstract float[] StarMultiplierThresholds { get; protected set; }
        public abstract int[] StarScoreThresholds { get; protected set; }

        public abstract bool ShouldUpdateInputsOnResume { get; }

        public HitWindowSettings HitWindow { get; protected set; }

        public float Stars => BaseStats.Stars;

        public int Score => BaseStats.TotalScore;
        public int BandBonusScore => BaseStats.BandBonusScore;
        public int Combo => BaseStats.Combo;
        public int NotesHit => BaseStats.NotesHit;

        public int TotalNotes { get; protected set; }

        public bool IsFc { get; protected set; }

        public int? LastHighScore { get; private set; }

        public IReadOnlyList<GameInput> ReplayInputs => _replayInputs.AsReadOnly();

        private Dictionary<int, GameInput> LastInputs { get; } = new();
        private Dictionary<int, GameInput> InputsToSendOnResume { get; } = new();

        protected SyncTrack SyncTrack { get; private set; }

        protected bool IsInitialized { get; private set; }

        protected List<ISantrollerHaptics> SantrollerHaptics { get; private set; } = new();

        protected BaseInputViewer InputViewer { get; private set; }

        protected int  LastCombo;
        protected bool IsStemMuted;

        protected bool IsRemotePlayer => Player.Bindings == null;
        
        /// <summary>
        /// Whether this player has been marked as disconnected during gameplay.
        /// Disconnected players are grayed out but not removed to preserve layout.
        /// </summary>
        public bool IsDisconnected { get; protected set; }
        
        /// <summary>
        /// Whether this specific player has failed (happiness at or below fail threshold).
        /// A failed player can be revived via Star Power from another player.
        /// In NoFail mode, this always returns false since players cannot actually fail.
        /// </summary>
        public bool HasPlayerFailed()
        {
            // In NoFail mode, players cannot fail - return false regardless of engine state
            if (GameManager != null && GameManager.IsNoFailActive)
            {
                return false;
            }
            
            // For remote players (spectator tracks), use only the engine's fail state from network sync.
            // Don't check GameManager.PlayerHasFailed because that reflects the LOCAL band's state,
            // not the spectated player's state. The remote player's fail state is synced via
            // SyncRemoteHappiness() which sets EngineContainer.HasFailed.
            if (IsRemotePlayer)
            {
                if (EngineContainer != null)
                {
                    return EngineContainer.HasFailed;
                }
                return false;
            }
            
            // For local players: if the whole band has failed, this player is definitely failed
            if (GameManager.PlayerHasFailed)
                return true;
                
            // Check individual player fail state
            if (EngineContainer != null)
            {
                return EngineContainer.HasFailed;
            }
            
            return false;
        }

        private List<GameInput> _replayInputs;

        private int _replayInputIndex;

        private float _noteSpeedDifficultyScale;

        protected EngineManager.EngineContainer EngineContainer;
        
        /// <summary>
        /// Gets the engine container for this player. Used for multiplayer unison sync.
        /// </summary>
        public EngineManager.EngineContainer PlayerEngineContainer => EngineContainer;

        protected override void GameplayAwake()
        {
            _replayInputs = new List<GameInput>();

            // TODO: Couldn't there be more than one input viewer?
            //  We were using FindObjectOfType<BaseInputViewer> before anyway, so we're no worse off in that respect
            InputViewer = FindFirstObjectByType<BaseInputViewer>();

            IsFc = true;
        }

        protected void Start()
        {
            if (Player.Bindings is not null)
            {
                SantrollerHaptics = Player.Bindings.GetDevicesByType<ISantrollerHaptics>();
            }

            if (!Player.IsReplay)
            {
                SubscribeToInputEvents();
            }
        }

        protected void Initialize(int index, YargPlayer player, SongChart chart, int? lastHighScore)
        {
            if (IsInitialized)
            {
                return;
            }

            HighwayIndex = index;
            Player = player;

            SyncTrack = chart.SyncTrack;

            LastHighScore = lastHighScore;

            _noteSpeedDifficultyScale = Player.Profile.CurrentDifficulty.NoteSpeedScale();

            if (Player.IsReplay && GameManager.ReplayInfo != null)
            {
                _replayInputs = new List<GameInput>(GameManager.ReplayData.Frames[player.ReplayIndex].Inputs);
                YargLogger.LogFormatDebug("Initialized replay inputs with {0} inputs", _replayInputs.Count);
            }

            if (InputViewer != null)
            {
                InputViewer.SetColors(player.ColorProfile);
                InputViewer.ResetButtons();
            }

            IsInitialized = true;
        }

        public virtual void GameplayUpdate()
        {
            if (!GameManager.Started || GameManager.Paused)
            {
                return;
            }

            // Skip input processing for disconnected players, but still update visuals
            // so the track keeps scrolling (looks better than frozen)
            if (!IsDisconnected)
            {
                // All players (local and remote) now process inputs:
                // - Local players: inputs from controller (via OnGameInput callback)
                // - Remote players: inputs from network queue (via UpdateInputs)
                UpdateInputs(GameManager.InputTime);
            }
            
            UpdateVisuals(GameManager.VisualTime);
        }

        protected abstract void UpdateVisuals(double visualTime);
        protected abstract void ResetVisuals();

        public virtual void ResetPracticeSection()
        {
            LastCombo = 0;

            IsFc = true;

            ResetVisuals();
        }

        public abstract void SetPracticeSection(uint start, uint end);

        // TODO Make this more generic
        public abstract void SetStemMuteState(bool muted);

        public virtual void SetStarPowerFX(bool active)
        {
            GameManager.ChangeStemReverbState(SongStem.Song, active);
        }

        public virtual void SetReplayTime(double time)
        {
            IsFc = true;

            _replayInputIndex = BaseEngine.ProcessUpToTime(time, ReplayInputs);

            SetStemMuteState(false);

            ResetVisuals();
            UpdateVisuals(time);
        }

        protected override void GameplayDestroy()
        {
            if (!Player.IsReplay)
            {
                UnsubscribeFromInputEvents();
            }

            FinishDestruction();
        }

        protected virtual void FinishDestruction()
        {
        }

        protected virtual void UpdateInputs(double time)
        {
            // Apply input offset
            // Video offset is already accounted for
            time += InputCalibration;

            double evaluationTime = time;
            bool runEngineUpdate = true;

            if (Player.IsReplay && GameManager.ReplayInfo != null)
            {
                // REPLAY MODE: Process replay inputs
                while (_replayInputIndex < ReplayInputs.Count)
                {
                    var input = ReplayInputs[_replayInputIndex];

                    // Current input does not meet the time requirement
                    if (time < input.Time)
                    {
                        break;
                    }

                    BaseEngine.QueueInput(ref input);
                    OnInputQueued(input);

                    _replayInputIndex++;
                }
            }
            else if (Player.Bindings == null)
            {
                // Remote multiplayer players are simulated locally via NetworkPlayerData snapshots.
                // Skip engine input processing to avoid generating artificial misses.
                // Use VisualTime so notes resolve when they visually pass the strikeline.
                _remoteSimulation?.ApplyRemoteState(GameManager.VisualTime);
                evaluationTime = time;
                runEngineUpdate = false;
            }
            // If Player.Bindings != null, inputs are queued via OnGameInput callback

            if (runEngineUpdate)
            {
                BaseEngine.Update(evaluationTime);
            }
        }

        private void SubscribeToInputEvents()
        {
            // Remote multiplayer players don't have Bindings (inputs are on their own machines)
            if (Player.Bindings == null) return;
            
            Player.Bindings.SubscribeToGameplayInputs(Player.Profile.GameMode, OnGameInput);

            Player.Bindings.DeviceAdded += OnDeviceAdded;
            Player.Bindings.DeviceRemoved += OnDeviceRemoved;
        }

        private void UnsubscribeFromInputEvents()
        {
            // Remote multiplayer players don't have Bindings (inputs are on their own machines)
            if (Player.Bindings == null) return;
            
            Player.Bindings.UnsubscribeFromGameplayInputs(Player.Profile.GameMode, OnGameInput);

            Player.Bindings.DeviceAdded -= OnDeviceAdded;
            Player.Bindings.DeviceRemoved -= OnDeviceRemoved;
        }

        private void OnDeviceAdded(InputDevice device)
        {
            if (device is ISantrollerHaptics haptics)
            {
                SantrollerHaptics.Add(haptics);
            }
        }

        private void OnDeviceRemoved(InputDevice device)
        {
            if (device is ISantrollerHaptics haptics)
            {
                SantrollerHaptics.Remove(haptics);
            }

            if (!GameManager.Paused && SettingsManager.Settings.PauseOnDeviceDisconnect.Value)
            {
                GameManager.SetPaused(true);
            }
        }

        public void SendInputsOnResume()
        {
            foreach (var originalInput in InputsToSendOnResume.Values)
            {
                var input = new GameInput(InputManager.CurrentInputTime, originalInput.Action, originalInput.Integer);
                OnGameInput(ref input);
            }

            InputsToSendOnResume.Clear();
        }
        
        /// <summary>
        /// Sets the NetworkPlayerData reference for this player (used in multiplayer).
        /// </summary>
        public void SetNetworkPlayerData(NetworkPlayerData networkPlayerData)
        {
            _networkPlayerData = networkPlayerData;
        }

        internal NetworkPlayerData NetworkPlayerData => _networkPlayerData;

        internal void RegisterRemoteSimulation(IRemotePlayerSimulation simulation)
        {
            _remoteSimulation = simulation;
        }

        /// <summary>
        /// Find the NetworkPlayerData that corresponds to this BasePlayer.
        /// Used for remote players to receive network inputs.
        /// </summary>
        private NetworkPlayerData FindNetworkPlayerDataForThisPlayer()
        {
            var liteNetAdapter = NetworkingServiceFactory.Instance as LiteNetNetworkingAdapter;
            if (liteNetAdapter == null) return null;
            
            var allNetworkPlayers = liteNetAdapter.GetAllPlayers();
            var allGamePlayers = GameManager.Players;
            
            // Find our index in the GameManager.Players list
            int ourIndex = -1;
            for (int i = 0; i < allGamePlayers.Count; i++)
            {
                if (allGamePlayers[i] == this)
                {
                    ourIndex = i;
                    break;
                }
            }
            
            // Return corresponding NetworkPlayerData (same index)
            if (ourIndex >= 0 && ourIndex < allNetworkPlayers.Count)
            {
                return allNetworkPlayers[ourIndex];
            }
            
            return null;
        }
        
        protected void OnGameInput(ref GameInput input)
        {
            // Ignore completely if the song hasn't started yet
            if (!GameManager.Started)
                return;
                
            // Ignore if this specific player has failed (can still be revived)
            // Check individual player fail state, not just band-wide fail
            if (HasPlayerFailed())
                return;

            // Ignore while paused
            if (GameManager.Paused)
            {
                if (!ShouldUpdateInputsOnResume)
                {
                    return;
                }

                if (LastInputs.TryGetValue(input.Action, out var lastInput))
                {
                    if (lastInput.Button != input.Button)
                    {
                        InputsToSendOnResume[input.Action] = input;
                    }
                    else
                    {
                        InputsToSendOnResume.Remove(input.Action);
                    }
                }

                return;
            }

            LastInputs[input.Action] = input;

            double adjustedTime = GameManager.GetRelativeInputTime(input.Time);
            // Apply input offset
            adjustedTime += InputCalibration;
            input = new(adjustedTime, input.Action, input.Integer);

            // Allow the input to be explicitly ignored before processing it
            if (InterceptInput(ref input)) return;

            BaseEngine.QueueInput(ref input);
            OnInputQueued(input);
            _replayInputs.Add(input);
        }

        protected virtual void OnStarPowerPhraseHit()
        {
            if (!GameManager.Paused && !GameManager.IsSeekingReplay)
            {
                GlobalAudioHandler.PlaySoundEffect(SfxSample.StarPowerAward);
            }
        }

        protected virtual void OnStarPowerPhraseMissed()
        {

        }

        protected virtual void OnStarPowerStatus(bool active)
        {
            var deploySample = SfxSample.StarPowerDeploy;
            if (SettingsManager.Settings.UseCrowdFx.Value == CrowdFxMode.Enabled)
            {
                deploySample = SfxSample.StarPowerDeployCrowd;
            }

            if (!GameManager.Paused)
            {
                GlobalAudioHandler.PlaySoundEffect(active
                    ? deploySample
                    : SfxSample.StarPowerRelease);

                SetStarPowerFX(active);
            }

            GameManager.ChangeStarPowerStatus(active);

            foreach (var haptics in SantrollerHaptics)
            {
                haptics.SetStarPowerActive(active);
            }
        }

        /// <summary>
        /// Called when a remote/spectator player's Star Power status changes.
        /// Similar to OnStarPowerStatus but does NOT trigger revival logic.
        /// Remote player Star Power should only affect visuals/audio, not game mechanics
        /// like reviving players in the local player's band.
        /// </summary>
        protected virtual void OnStarPowerStatusRemote(bool active)
        {
            var deploySample = SfxSample.StarPowerDeploy;
            if (SettingsManager.Settings.UseCrowdFx.Value == CrowdFxMode.Enabled)
            {
                deploySample = SfxSample.StarPowerDeployCrowd;
            }

            if (!GameManager.Paused)
            {
                GlobalAudioHandler.PlaySoundEffect(active
                    ? deploySample
                    : SfxSample.StarPowerRelease);

                SetStarPowerFX(active);
            }

            // Pass isFromRemotePlayer=true so revival logic is NOT triggered
            GameManager.ChangeStarPowerStatus(active, isFromRemotePlayer: true);
            
            // Don't update haptics for remote players - it's not their controller
        }

        protected abstract bool InterceptInput(ref GameInput input);

        protected virtual void OnInputQueued(GameInput input)
        {
            if (InputViewer != null)
            {
                InputViewer.OnInput(input);
            }
        }

        protected void OnComboIncrement(int amount)
        {
            GameManager.AddBandCombo(amount);
        }

        protected void OnComboReset()
        {
            GameManager.ResetBandCombo();
        }

        protected static int[] PopulateStarScoreThresholds(float[] multiplierThresh, int baseScore)
        {
            var starScoreThresh = new int[multiplierThresh.Length];

            for (int i = 0; i < multiplierThresh.Length; i++)
            {
                starScoreThresh[i] = Mathf.FloorToInt(baseScore * multiplierThresh[i]);
            }

            return starScoreThresh;
        }
        
        /// <summary>
        /// Shows a revival countdown when the player is revived via Star Power.
        /// Override in derived classes to implement visual feedback.
        /// </summary>
        /// <param name="gracePeriod">The grace period duration in seconds.</param>
        public virtual void ShowRevivalCountdown(double gracePeriod)
        {
            // Base implementation does nothing - derived classes can override
            // TrackPlayer implements this to show the countdown on the track
        }

        public abstract (ReplayFrame Frame, ReplayStats Stats) ConstructReplayData();
    }
}
