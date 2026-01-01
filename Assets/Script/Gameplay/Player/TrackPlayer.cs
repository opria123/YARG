using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Serialization;
using YARG.Assets.Script.Helpers;
using YARG.Core;
using YARG.Core.Audio;
using YARG.Core.Chart;
using YARG.Core.Engine;
using YARG.Core.Logging;
using YARG.Gameplay.HUD;
using YARG.Gameplay.Visuals;
using YARG.Playback;
using YARG.Player;
using YARG.Settings;
using YARG.Themes;

namespace YARG.Gameplay.Player
{
    public abstract class TrackPlayer : BasePlayer
    {
        protected internal readonly struct SoloSyncSnapshot
        {
            public readonly bool IsActive;
            public readonly int Sequence;
            public readonly int NoteCount;
            public readonly int NotesHit;
            public readonly int LastBonus;
            public readonly int TotalBonus;

            public SoloSyncSnapshot(bool isActive, int sequence, int noteCount, int notesHit, int lastBonus, int totalBonus)
            {
                IsActive = isActive;
                Sequence = sequence;
                NoteCount = noteCount;
                NotesHit = notesHit;
                LastBonus = lastBonus;
                TotalBonus = totalBonus;
            }
        }

        public const float STRIKE_LINE_POS       = -2f;
        public const float DEFAULT_ZERO_FADE_POS = 3f;
        public const float NOTE_SPAWN_OFFSET     = 5f;

        public const float TRACK_WIDTH  = 2f;
        public const float TRACK_HEIGHT = 100f;

        public const float HUD_TOP_ELEMENT_HEIGHT = 0.15f;
        public const float HUD_CENTER_ELEMENT_DEPTH = 1.5f;

        public static int HighwayCount = 1;

        public double SpawnTimeOffset => (ZeroFadePosition + _spawnAheadDelay + -STRIKE_LINE_POS) / NoteSpeed;

        protected internal TrackView TrackView { get; private set; }

        [field: Header("Visuals")]
        [field: SerializeField]
        public Camera TrackCamera { get; private set; }

        [SerializeField]
        protected CameraPositioner CameraPositioner;
        
        /// <summary>
        /// Gets the camera positioner for this track. Used for setting initial state on spectator tracks.
        /// </summary>
        public CameraPositioner TrackCameraPositioner => CameraPositioner;
        
        [SerializeField]
        protected HighwayCameraRendering HighwayCameraRendering;
        [SerializeField]
        protected TrackMaterial TrackMaterial;
        [SerializeField]
        protected ComboMeter ComboMeter;
        [SerializeField]
        protected StarpowerBar StarpowerBar;
        [SerializeField]
        protected SunburstEffects SunburstEffects;
        [SerializeField]
        protected IndicatorStripes IndicatorStripes;
        [SerializeField]
        protected HitWindowDisplay HitWindowDisplay;

        [Header("Multiplayer Player Name")]
        [SerializeField]
        [Tooltip("Optional: TextMeshPro for showing remote player name on the track. Position this where you want the name to appear.")]
        protected TextMeshPro _playerNameLabel;

        [SerializeField]
        private Transform _hudLocation;

        [Header("Pools")]
        [SerializeField]
        protected KeyedPool NotePool;
        [SerializeField]
        protected Pool BeatlinePool;
        [SerializeField]
        protected Pool EffectPool;

        public float ZeroFadePosition { get; private set; }
        public float FadeSize         { get; private set; }

        [field: Header("Star Power Trim Effect")]
        [SerializeField]
        protected StarPowerEffectElement StarPowerEffect;

        // Multiply by the reciprocal of 1 / player count to prevent the HUD from being too close to the highway;
        public Vector2 HUDTopElementViewportPosition =>
            TrackCamera.WorldToViewportPoint(_hudLocation.position.WithY(
                HUD_TOP_ELEMENT_HEIGHT * (1 / HighwayCameraRendering.CalculateScale(HighwayCount)) + TRACK_HEIGHT));

        public Vector2 HUDCenterElementViewportPosition =>
            TrackCamera.WorldToViewportPoint(_hudLocation.position
                .WithY(TRACK_HEIGHT)
                .WithZ(STRIKE_LINE_POS + HUD_CENTER_ELEMENT_DEPTH));

        protected List<Beatline> Beatlines;

        protected int BeatlineIndex;

        protected bool IsBass { get; private set; }

        private float _spawnAheadDelay;

        protected SoloSection _activeSoloSection;
        protected SoloSyncState _soloSyncState = new() { Sequence = -1 };

        protected struct SoloSyncState
        {
            public int Sequence;
            public bool IsActive;
            public int NoteCount;
            public int NotesHit;
            public int LastBonus;
        }

        protected float SongLength;
        
        // Revival countdown state
        protected double _revivalCountdownEndTime = -1;
        protected double _revivalCountdownDuration = 0;

        public virtual void Initialize(int index, YargPlayer player, SongChart chart, TrackView trackView,
            StemMixer mixer, int? lastHighScore)
        {
            if (IsInitialized)
            {
                return;
            }

            Initialize(index, player, chart, lastHighScore);

            TrackView = trackView;

            Beatlines = SyncTrack.Beatlines;
            BeatlineIndex = 0;

            var preset = player.EnginePreset;
            IndicatorStripes.Initialize(preset);

            // Set fade information and highway length
            ZeroFadePosition = DEFAULT_ZERO_FADE_POS * Player.Profile.HighwayLength;
            FadeSize = Player.CameraPreset.FadeLength;

            _spawnAheadDelay = GameManager.IsPractice ? SettingsManager.Settings.PracticeRestartDelay.Value : 2;
            if (player.Profile.HighwayLength > 1)
            {
                FadeSize *= player.Profile.HighwayLength;
            }

            // Move the HUD location based on the highway length
            var change = ZeroFadePosition - DEFAULT_ZERO_FADE_POS;
            _hudLocation.position = _hudLocation.position.AddZ(change);

            // Must be done after the HUD location is set
            StarPowerEffect.Initialize();
            StarPowerEffect.gameObject.SetActive(false);

            // Determine if a track is bass or not for the BASS GROOVE text notification
            IsBass = Player.Profile.CurrentInstrument
                is Instrument.FiveFretBass
                or Instrument.SixFretBass
                or Instrument.ProBass_17Fret
                or Instrument.ProBass_22Fret;

            TrackView.ShowPlayerName(player);
        }

        protected override void ResetVisuals()
        {
            // "Muting a stem" isn't technically a visual,
            // but it's a form of feedback so we'll put it here.
            SetStemMuteState(false);

            ComboMeter.SetFullCombo(IsFc);
            TrackView.ForceReset();

            NotePool.ReturnAllObjects();
            BeatlinePool.ReturnAllObjects();

            HitWindowDisplay.SetHitWindowSize();
        }

        internal virtual int GetRemoteNoteCount()
        {
            return 0;
        }

        internal virtual bool ResolveRemoteNote(ref int cursor, bool wasHit, double localSongTime,
            double remoteSongTime, double localLeadSeconds, double remoteLeadSeconds,
            out int resolvedWeight)
        {
            resolvedWeight = 0;
            return false;
        }

        internal virtual void ResetRemoteSimulationState()
        {
        }

        internal virtual int GetResolvedMissCount()
        {
            return 0;
        }

        internal SoloSyncSnapshot GetSoloSyncSnapshot()
        {
            if (_activeSoloSection != null)
            {
                _soloSyncState.NotesHit = Mathf.Clamp(_activeSoloSection.NotesHit, 0, _soloSyncState.NoteCount);
            }

            int totalBonus = Mathf.Max(0, BaseStats.SoloBonuses);
            return new SoloSyncSnapshot(_soloSyncState.IsActive, _soloSyncState.Sequence,
                _soloSyncState.NoteCount, _soloSyncState.NotesHit, _soloSyncState.LastBonus, totalBonus);
        }

        internal virtual void ApplyRemoteStarPowerState(bool isActive)
        {
        }

        internal virtual void UpdateRemoteCountdown()
        {
        }

        /// <summary>
        /// Syncs happiness and fail state from network for remote players.
        /// </summary>
        /// <param name="happiness">The happiness value from the authoritative client.</param>
        /// <param name="hasFailed">Whether the player has failed according to the authoritative client.</param>
        internal virtual void SyncRemoteHappiness(float happiness, bool hasFailed)
        {
        }
        
        /// <summary>
        /// Sets the initial fail state for spectator tracks.
        /// This ensures the track starts in the correct visual state (lowered if failed).
        /// </summary>
        /// <param name="hasFailed">Whether the player has already failed.</param>
        public virtual void SetInitialFailState(bool hasFailed)
        {
            // Override in derived class to set _didLowerTrack flag
        }

        /// <summary>
        /// Initializes the player name label for multiplayer.
        /// Shows the player's name on the track for remote players only.
        /// </summary>
        protected void InitializePlayerNameLabel(YargPlayer player)
        {
            if (_playerNameLabel == null)
                return;
                
            // Only show for remote players in multiplayer
            if (IsRemotePlayer && player?.Profile != null)
            {
                _playerNameLabel.text = player.Profile.Name;
                _playerNameLabel.gameObject.SetActive(true);
            }
            else
            {
                _playerNameLabel.gameObject.SetActive(false);
            }
        }
        
        /// <summary>
        /// Marks this track as disconnected. The track will be grayed out and stop updating,
        /// but will remain in place to avoid shifting other players' tracks.
        /// </summary>
        public virtual void MarkAsDisconnected()
        {
            IsDisconnected = true;
            
            // Gray out the track material
            if (TrackMaterial != null)
            {
                TrackMaterial.SetGrayedOut(true);
            }
            
            // Update the player name label to show disconnected state
            if (_playerNameLabel != null)
            {
                _playerNameLabel.color = new Color(0.5f, 0.5f, 0.5f, 0.7f); // Gray color
                _playerNameLabel.text = $"[DISCONNECTED] {_playerNameLabel.text}";
                _playerNameLabel.gameObject.SetActive(true);
            }
            
            // Return all pooled objects and stop spawning
            if (NotePool != null)
            {
                NotePool.ReturnAllObjects();
            }
            
            if (BeatlinePool != null)
            {
                BeatlinePool.ReturnAllObjects();
            }
            
            if (EffectPool != null)
            {
                EffectPool.ReturnAllObjects();
            }
            
            // Hide/disable visual components
            if (ComboMeter != null)
            {
                ComboMeter.gameObject.SetActive(false);
            }
            
            if (StarPowerEffect != null)
            {
                StarPowerEffect.gameObject.SetActive(false);
            }
            
            if (StarpowerBar != null)
            {
                StarpowerBar.gameObject.SetActive(false);
            }
            
            if (SunburstEffects != null)
            {
                SunburstEffects.gameObject.SetActive(false);
            }
            
            if (IndicatorStripes != null)
            {
                IndicatorStripes.gameObject.SetActive(false);
            }
            
            if (HitWindowDisplay != null)
            {
                HitWindowDisplay.gameObject.SetActive(false);
            }
            
            Debug.Log($"[TrackPlayer] Marked track as disconnected for player: {Player?.Profile?.Name}");
        }
    }

    public abstract class TrackPlayer<TEngine, TNote> : TrackPlayer
        where TEngine : BaseEngine
        where TNote : Note<TNote>
    {
        public TEngine Engine { get; private set; }

        public override BaseEngine BaseEngine => Engine;

        protected List<TNote> Notes { get; set; }

        protected int NoteIndex { get; private set; }

        public InstrumentDifficulty<TNote> NoteTrack { get; private set; }

        private InstrumentDifficulty<TNote> OriginalNoteTrack { get; set; }

        private int _currentMultiplier;
        private int _previousMultiplier;

        private bool _isHotStartChecked;
        private bool _previousBassGrooveState;
        private bool _newHighScoreShown;

        private double _previousStarPowerAmount;

        private bool _wasStarPowerActive;
        private bool _remoteStarPowerActive;
        private bool _didLowerTrack;

        private Queue<TrackEffect> _upcomingEffects = new();
        private List<TrackEffectElement> _currentEffects = new();
        protected List<TrackEffect> _trackEffects = new();

        protected SongChart Chart;

        public override void Initialize(int index, YargPlayer player, SongChart chart, TrackView trackView,
            StemMixer mixer, int? currentHighScore)
        {
            if (IsInitialized)
            {
                return;
            }

            // Get player count
            if (index == 0)
            {
                // Reset
                HighwayCount = 1;
            }
            else if (index + 1 > HighwayCount)
            {
                HighwayCount = index + 1;
            }

            // Consolidate tracks into a parent object for animation purposes
            transform.SetParent(GameObject.Find("Visuals").transform);

            base.Initialize(index, player, chart, trackView, mixer, currentHighScore);

            SetupTheme();

            Chart = chart;

            OriginalNoteTrack = GetNotes(chart);
            player.Profile.ApplyModifiers(OriginalNoteTrack);

            NoteTrack = OriginalNoteTrack;
            Notes = NoteTrack.Notes;

            var events = NoteTrack.TextEvents;

            Engine = CreateEngine();

            base.ComboMeter.Initialize(player.EnginePreset, Engine.BaseParameters.MaxMultiplier);
            
            // Initialize persistent player name label for multiplayer (shows for remote players)
            InitializePlayerNameLabel(player);

            Engine.OnComboIncrement += OnComboIncrement;
            Engine.OnComboReset += OnComboReset;
            if (GameManager.IsPractice)
            {
                Engine.SetSpeed(GameManager.SongSpeed >= 1 ? GameManager.SongSpeed : 1);
            }
            else if (Player.IsReplay)
            {
                // If it's a replay, the "SongSpeed" parameter should be set properly
                // when it gets deserialized. Transfer this over to the engine.
                Engine.SetSpeed(Player.EngineParameterOverride.SongSpeed);
            }
            else
            {
                Engine.SetSpeed(GameManager.SongSpeed);
            }

            GameManager.BeatEventHandler.Visual.Subscribe(SunburstEffects.PulseSunburst, BeatEventType.StrongBeat);
            InitializeTrackEffects();

            ResetNoteCounters();

            FinishInitialization();

            SongLength = (float) chart.GetEndTime();
        }

        protected override void FinishDestruction()
        {
            GameManager.BeatEventHandler.Visual.Unsubscribe(SunburstEffects.PulseSunburst);

            base.FinishDestruction();
        }

        private void InitializeTrackEffects()
        {

            // If the user doesn't want track effects, generate no effects
            if (!SettingsManager.Settings.EnableTrackEffects.Value)
            {
                return;
            }

            var phrases = new List<Phrase>();

            foreach (var phrase in NoteTrack.Phrases)
            {
                // We only want solo and drum fill here. Unisons are added later
                // and there are no track effects for the other phrase types
                if (phrase.Type is PhraseType.Solo or PhraseType.DrumFill)
                {
                    // It turns out that some charts have drum fill phrases that aren't SP activation
                    // (they have no notes), so we need to ignore those
                    if (phrase.Type is PhraseType.DrumFill)
                    {
                        foreach (var note in Notes)
                        {
                            if (note.Time >= phrase.Time && note.Time <= phrase.TimeEnd)
                            {
                                phrases.Add(phrase);
                                break;
                            }
                        }
                    }
                    else
                    {
                        phrases.Add(phrase);
                    }
                }
            }

            phrases.AddRange(EngineContainer.UnisonPhrases);

            var effects = TrackEffect.PhrasesToEffects(phrases);
            _trackEffects.AddRange(effects);
        }

        private void FinalizeTrackEffects()
        {
            foreach (var effect in TrackEffect.SliceEffects(NoteSpeed, _trackEffects))
            {
                _upcomingEffects.Enqueue(effect);
            }
        }

        private void SetupTheme()
        {
            var (gameMode, instrument) = (Player.Profile.GameMode, Player.Profile.CurrentInstrument);

            var style = VisualStyleHelpers.GetVisualStyle(gameMode, instrument);

            var themePrefab = ThemeManager.Instance.CreateNotePrefabFromTheme(
                Player.ThemePreset, style, NotePool.Prefab);
            NotePool.SetPrefabAndReset(themePrefab);
        }

        protected abstract InstrumentDifficulty<TNote> GetNotes(SongChart chart);
        protected abstract TEngine CreateEngine();

        protected virtual void FinishInitialization()
        {
            TrackMaterial.Initialize(Player.HighwayPreset);
            // For spectator tracks (late-join or spectate mode), skip the auto-raise animation.
            // Their initial state will be set via SetInitialState() after initialization.
            // However, at song START (before IsSongStarted), remote players should animate too.
            bool skipAutoRaise = IsRemotePlayer && GameManager.IsSongStarted;
            CameraPositioner.Initialize(Player.CameraPreset, skipAutoRaise: skipAutoRaise);
            FinalizeTrackEffects();
        }

        protected void ResetNoteCounters()
        {
            NoteIndex = 0;
            TotalNotes = Notes.Sum(i => Engine.GetNumberOfNotes(i));

            _activeSoloSection = null;
            _soloSyncState = new SoloSyncState
            {
                Sequence = -1,
                IsActive = false,
                NoteCount = 0,
                NotesHit = 0,
                LastBonus = 0
            };
        }

        public override void ResetPracticeSection()
        {
            Engine.Reset(true);

            if (NoteTrack.Notes.Count > 0)
            {
                NoteTrack.Notes[0].OverridePreviousNote();
                NoteTrack.Notes[^1].OverrideNextNote();
            }

            BeatlineIndex = 0;
            ResetNoteCounters();
            
            // Clear revival countdown state
            _revivalCountdownEndTime = -1;
            _revivalCountdownDuration = 0;

            base.ResetPracticeSection();
        }

        protected override void UpdateVisuals(double visualTime)
        {
            // For disconnected players, only scroll the track background - skip everything else
            if (IsDisconnected)
            {
                TrackMaterial.SetTrackScroll(visualTime, NoteSpeed);
                return;
            }
            
            // Allow the HUD to track the highway with animations
            TrackView.UpdateHUDPosition(HighwayIndex, HighwayCount);

            UpdateNotes(visualTime);
            UpdateBeatlines(visualTime);
            UpdateTrackEffects(visualTime);
            UpdateRevivalCountdown();

            var stats = Engine.BaseStats;

            int maxMultiplier = Engine.BaseParameters.MaxMultiplier;
            if (stats.IsStarPowerActive)
            {
                maxMultiplier *= 2;
            }

            double currentStarPowerAmount = Engine.GetStarPowerBarAmount();

            bool groove = stats.ScoreMultiplier == maxMultiplier;

            _currentMultiplier = stats.ScoreMultiplier;

            TrackMaterial.SetTrackScroll(visualTime, NoteSpeed);
            TrackMaterial.GrooveMode = groove;
            TrackMaterial.StarpowerMode = stats.IsStarPowerActive;

            // In multiplayer, don't double the score multiplier in the strikeline element
            // Otherwise, it looks like the band multiplier applies on top of the score multiplier
            int displayMultiplier = GameManager.TotalPlayers > 1 && stats.IsStarPowerActive
                ? stats.ScoreMultiplier / 2
                : stats.ScoreMultiplier;

            ComboMeter.SetCombo(stats.ScoreMultiplier, displayMultiplier, maxMultiplier, stats.Combo);
            StarpowerBar.SetStarpower(currentStarPowerAmount, stats.IsStarPowerActive);
            StarpowerBar.UpdateFlash(GameManager.BeatEventHandler.Visual.StrongBeat.CurrentPercentage);
            SunburstEffects.SetSunburstEffects(groove, stats.IsStarPowerActive, _currentMultiplier);

            TrackView.UpdateNoteStreak(stats.Combo);


            // Could be if (!_isHotStartChecked && groove), but that would make it so hot start doesn't show
            // for bass until 6x.
            if (!_isHotStartChecked && stats.ScoreMultiplier == (!stats.IsStarPowerActive ? 4 : 8))
            {
                _isHotStartChecked = true;

                if (IsFc)
                {
                    TrackView.ShowHotStart();
                }
            }

            bool currentBassGrooveState = IsBass && groove;

            if (!_previousBassGrooveState && currentBassGrooveState)
            {
                TrackView.ShowBassGroove();
            }

            _previousBassGrooveState = currentBassGrooveState;

            if (!stats.IsStarPowerActive && _previousStarPowerAmount < 0.5 && currentStarPowerAmount >= 0.5)
            {
                TrackView.ShowStarPowerReady();
            }

            if (stats.IsStarPowerActive && !_wasStarPowerActive && !_didLowerTrack)
            {
                CameraPositioner.Scoop();
            }

            _previousStarPowerAmount = currentStarPowerAmount;
            _wasStarPowerActive = stats.IsStarPowerActive;

            foreach (var haptics in SantrollerHaptics)
            {
                haptics.SetStarPowerFill((float) currentStarPowerAmount);
            }

            bool isSongEnd = visualTime > SongLength;
            // Lower track if song ended OR if this specific player has failed (or whole band)
            bool shouldLowerTrack = isSongEnd || HasPlayerFailed();
            if (!_didLowerTrack && shouldLowerTrack)
            {
                _didLowerTrack = true;
                CameraPositioner.Lower(isSongEnd);
                if (IsRemotePlayer)
                {
                    Debug.Log($"[TrackPlayer] Lowering spectator track for '{Player?.Profile?.Name ?? "Unknown"}' (failed={HasPlayerFailed()}, songEnd={isSongEnd})");
                }
            }
            // Raise track back up if player was revived (but not at song end)
            else if (_didLowerTrack && !shouldLowerTrack && !isSongEnd)
            {
                _didLowerTrack = false;
                CameraPositioner.Raise();
                if (IsRemotePlayer)
                {
                    Debug.Log($"[TrackPlayer] Raising spectator track for '{Player?.Profile?.Name ?? "Unknown"}' (revived)");
                }
            }
        }

        private void UpdateNotes(double visualTime)
        {
            while (NoteIndex < Notes.Count && Notes[NoteIndex].Time <= visualTime + SpawnTimeOffset)
            {
                var note = Notes[NoteIndex];

                // Skip this frame if the pool is full
                if (!NotePool.CanSpawnAmount(note.ChildNotes.Count + 1))
                {
                    break;
                }

                NoteIndex++;

                OnNoteSpawned(note);

                // Don't spawn hit or missed notes
                if (note.WasHit || note.WasMissed)
                {
                    continue;
                }

                // Spawn all of the notes and child notes
                foreach (var child in note.AllNotes)
                {
                    SpawnNote(child);
                }
            }
        }

        private void UpdateBeatlines(double time)
        {
            while (BeatlineIndex < Beatlines.Count && Beatlines[BeatlineIndex].Time <= time + SpawnTimeOffset)
            {
                if (BeatlineIndex + 1 < Beatlines.Count && Beatlines[BeatlineIndex + 1].Time <= time + SpawnTimeOffset)
                {
                    BeatlineIndex++;
                    continue;
                }

                var beatline = Beatlines[BeatlineIndex];

                if (Notes.Count > 0 && beatline.Time > Notes[^1].TimeEnd)
                {
                    return;
                }

                // Skip this frame if the pool is full
                if (!BeatlinePool.CanSpawnAmount(1))
                {
                    break;
                }

                var poolable = BeatlinePool.TakeWithoutEnabling();
                if (poolable == null)
                {
                    YargLogger.LogWarning("Attempted to spawn beatline, but it's at its cap!");
                    break;
                }

                ((BeatlineElement) poolable).BeatlineRef = beatline;
                poolable.EnableFromPool();

                BeatlineIndex++;
            }
        }

        private void UpdateTrackEffects(double time)
        {
            if (_upcomingEffects.TryPeek(out var nextEffect) && nextEffect.Time <= time + SpawnTimeOffset)
            {
                SpawnEffect(nextEffect, false);
            }

            // If any of the current effects are drum fill, we need to react
            // when starpower goes from unavailable to available

            // Remove past effects from current list
            // This may actually fail if an effect is reused from the pool
            // too quickly, but as long as it is only being used for setting
            // drum fill visibility, it shouldn't break.
            for (var i = 0; i < _currentEffects.Count; i++)
            {
                if (!_currentEffects[i].Active)
                {
                    _currentEffects.RemoveAt(i);
                }
                else
                {
                    // See if it's an invisible drum fill and if starpower has become available
                    // Since we never change visibility on anything but drum fills, there's no need to check
                    // the effect type.
                    // TODO: We also need to change effects that were originally a UnisonAndDrumFill or SoloAndDrumFill
                    //  back from Unison or Solo, although I'm not even sure those exist. Maybe SoloAndDrumFill does..
                    if ((_currentEffects[i].Visibility < 1.0f && Engine.CanStarPowerActivate) && !Engine.BaseStats.IsStarPowerActive)
                    {
                        _currentEffects[i].MakeVisible();
                        // If start transition is disabled, previous should be disabled
                        if (!_currentEffects[i].EffectRef.StartTransitionEnable)
                        {
                            _currentEffects[i - 1].SetEndTransitionVisible(false);
                        }

                        // If end transition is disabled, next should be disabled if it is spawned
                        if (_currentEffects.Count > i + 1 && !_currentEffects[i].EffectRef.EndTransitionEnable)
                        {
                            _currentEffects[i + 1].SetStartTransitionVisible(false);
                        }
                    }
                    // We also need to make already spawned drum fills disappear if the player activated SP
                    // And we do need to check effect type here
                    if (_currentEffects[i].EffectRef.EffectType == TrackEffectType.DrumFill &&
                        (_currentEffects[i].Visibility == 1.0f && Engine.BaseStats.IsStarPowerActive))
                    {
                        _currentEffects[i].MakeVisible(false);

                        if (!_currentEffects[i].EffectRef.StartTransitionEnable && i > 0)
                        {
                            // Previous maybe needs end transition enabled since we're disappearing
                            // (if the effect type doesn't have an end transition set, it won't
                            //  be active regardless of what we do here, so a hard enable is ok)
                            _currentEffects[i - 1].SetEndTransitionVisible(true);
                        }

                        if (!_currentEffects[i].EffectRef.EndTransitionEnable)
                        {
                            // next needs start transition enabled, if it is spawned
                            // if it isn't yet spawned, it should already be set correctly
                            if (_currentEffects.Count > i + 1)
                            {
                                _currentEffects[i + 1].SetStartTransitionVisible(true);
                            }
                        }
                    }
                }
            }
        }

        private void SpawnEffect(TrackEffect nextEffect, bool seeking)
        {
            var poolable = EffectPool.TakeWithoutEnabling();
            if (poolable == null)
            {
                YargLogger.LogWarning("Attempted to spawn track effect, but it's at its cap!");
                return;
            }

            // The seeking code handles this for us if we're seeking
            if (!seeking)
            {
                _upcomingEffects.Dequeue();
            }

            // Do some magic to vanish drum fills if the player doesn't have enough SP to activate
            // or if SP is already active.

            if (Engine.BaseStats.IsStarPowerActive || !Engine.CanStarPowerActivate)
            {
                if (nextEffect.EffectType is TrackEffectType.DrumFill)
                {
                    nextEffect.Visibility = 0.0f;
                    if (!nextEffect.StartTransitionEnable)
                    {
                        if (_currentEffects.Count > 0)
                        {
                            _currentEffects[^1].SetEndTransitionVisible(true);
                            _currentEffects[^1].SetTransitionState();
                        }
                    }
                    if (!nextEffect.EndTransitionEnable)
                    {
                        // Get next next and turn on its start transition
                        // Since we are only spawning now, it shouldn't be possible
                        // for next next to be spawned yet.
                        if (_upcomingEffects.TryPeek(out var nextNextEffect))
                        {
                            nextNextEffect.StartTransitionEnable = true;
                        }
                    }

                    if (!nextEffect.StartTransitionEnable)
                    {
                        // Turn on end transition for previous effect

                        // Previous effect is by definition already spawned,
                        // but we'll check that _currentEffects isn't length zero
                        if (_currentEffects.Count > 0)
                        {
                            _currentEffects[^1].SetEndTransitionVisible(true);
                        }
                    }
                }

                if (nextEffect.EffectType is TrackEffectType.DrumFillAndUnison)
                {
                    nextEffect.EffectType = TrackEffectType.Unison;
                }

                if (nextEffect.EffectType is TrackEffectType.SoloAndDrumFill)
                {
                    nextEffect.EffectType = TrackEffectType.Solo;
                }
            }

            ((TrackEffectElement) poolable).EffectRef = nextEffect;
            _currentEffects.Add((TrackEffectElement) poolable);
            poolable.EnableFromPool();
        }

        protected virtual void OnNoteSpawned(TNote parentNote)
        {
        }

        public override void SetPracticeSection(uint start, uint end)
        {
            var practiceNotes = OriginalNoteTrack.Notes.Where(n => n.Tick >= start && n.Tick < end).ToList();

            YargLogger.LogFormatDebug("Practice notes: {0}", practiceNotes.Count);

            var instrument = OriginalNoteTrack.Instrument;
            var difficulty = OriginalNoteTrack.Difficulty;
            var phrases = OriginalNoteTrack.Phrases;
            var textEvents = OriginalNoteTrack.TextEvents;
            var shiftEvents = OriginalNoteTrack.RangeShiftEvents;

            NoteTrack = new InstrumentDifficulty<TNote>(instrument, difficulty, practiceNotes, phrases, textEvents, shiftEvents);
            Notes = NoteTrack.Notes;

            ResetNoteCounters();

            BeatlineIndex = 0;

            Engine = CreateEngine();

            if (GameManager.IsPractice)
            {
                Engine.SetSpeed(GameManager.SongSpeed >= 1 ? GameManager.SongSpeed : 1);
            }
            else
            {
                Engine.SetSpeed(GameManager.SongSpeed);
            }

            ResetPracticeSection();
        }

        public override void SetReplayTime(double time)
        {
            BeatlineIndex = 0;
            ResetNoteCounters();

            // Reset the track effect overlay
            ResetTrackEffectOverlay(time);

            base.SetReplayTime(time);
        }

        private void ResetTrackEffectOverlay(double time)
        {
            // despawn any existing track effects, rebuild track effect structures, spawn any that are now in current
            _upcomingEffects.Clear();
            for(var i = 0; i < EffectPool.AllSpawned.Count; i++)
            {
                var poolable = EffectPool.AllSpawned[i];
                poolable.ParentPool.Return(poolable);
            }

            foreach (var effect in TrackEffect.SliceEffects(NoteSpeed, _trackEffects))
            {
                if (effect.Time >= time)
                {
                    _upcomingEffects.Enqueue(effect);
                } else if (effect.Time < time && time < effect.TimeEnd)
                {
                    // current effect, spawn it
                    SpawnEffect(effect, true);
                }
            }
        }

        protected void SpawnNote(TNote note)
        {
            var poolable = NotePool.KeyedTakeWithoutEnabling(note);
            if (poolable == null)
            {
                YargLogger.LogWarning("Attempted to spawn note, but it's at its cap!");
                return;
            }

            InitializeSpawnedNote(poolable, note);
            poolable.EnableFromPool();
        }

        protected abstract void InitializeSpawnedNote(IPoolable poolable, TNote note);

        protected virtual void OnNoteHit(int index, TNote note)
        {
            // Skip expensive effects for remote players (haptics, stem mute changes)
            if (IsRemotePlayer)
            {
                LastCombo = Combo;
                return;
            }

            if (!GameManager.IsSeekingReplay)
            {
                SetStemMuteState(false);
                if (_currentMultiplier != _previousMultiplier)
                {
                    _previousMultiplier = _currentMultiplier;

                    foreach (var haptics in SantrollerHaptics)
                    {
                        haptics.SetMultiplier((byte) Math.Clamp(_currentMultiplier, 1, byte.MaxValue));
                    }
                }

                if (index >= Notes.Count - 1 && note.ParentOrSelf.WasFullyHit())
                {
                    if (IsFc)
                    {
                        TrackView.ShowFullCombo();
                    }
                    else if (Combo >= 30) // 30 to coincide with 4x multiplier (including on bass)
                    {
                        TrackView.ShowStrongFinish();
                    }
                }
            }

            LastCombo = Combo;
        }

        protected virtual void OnNoteMissed(int index, TNote note)
        {
            if (IsFc)
            {
                ComboMeter.SetFullCombo(false);
                IsFc = false;
            }

            // Skip expensive effects for remote players (sound, camera punch, haptics)
            if (IsRemotePlayer)
            {
                LastCombo = Combo;
                return;
            }

            if (!GameManager.IsSeekingReplay)
            {
                SetStemMuteState(true);

                if (LastCombo >= 10)
                {
                    GlobalAudioHandler.PlaySoundEffect(SfxSample.NoteMiss);
                    CameraPositioner.Punch();
                }

                foreach (var haptics in SantrollerHaptics)
                {
                    haptics.SetMultiplier(0);
                }
            }

            LastCombo = Combo;
        }

        internal override void ApplyRemoteStarPowerState(bool isActive)
        {
            if (!IsRemotePlayer)
            {
                return;
            }

            if (_remoteStarPowerActive == isActive)
            {
                return;
            }

            _remoteStarPowerActive = isActive;

            EngineContainer?.SyncRemoteStarPowerState(isActive);
            
            // Call OnStarPowerStatusRemote instead of OnStarPowerStatus to avoid triggering
            // revival logic. Remote player Star Power should only affect visuals/audio,
            // not game mechanics like reviving players in the local player's band.
            OnStarPowerStatusRemote(isActive);
        }

        internal override void SyncRemoteHappiness(float happiness, bool hasFailed)
        {
            if (!IsRemotePlayer || EngineContainer == null)
            {
                return;
            }

            // In NoFail mode, ignore fail state from network - players can't fail when NoFail is active.
            // This ensures that if NoFail is enabled mid-session, remote players won't appear as failed.
            if (GameManager != null && GameManager.IsNoFailActive)
            {
                hasFailed = false;
            }

            bool wasFailedBefore = EngineContainer.HasFailed;
            EngineContainer.SyncRemoteHappiness(happiness, hasFailed);
            
            // Log state transitions for debugging
            if (wasFailedBefore != hasFailed)
            {
                Debug.Log($"[TrackPlayer] Remote player '{Player?.Profile?.Name ?? "Unknown"}' fail state changed: {wasFailedBefore} -> {hasFailed} (happiness={happiness:F2})");
            }
        }
        
        /// <inheritdoc/>
        public override void SetInitialFailState(bool hasFailed)
        {
            _didLowerTrack = hasFailed;
            
            // Also sync the engine container's fail state
            if (EngineContainer != null)
            {
                // Use a low happiness value for failed state, normal for alive
                float happiness = hasFailed ? 0f : 1f;
                EngineContainer.SyncRemoteHappiness(happiness, hasFailed);
            }
            
            Debug.Log($"[TrackPlayer] Set initial fail state for '{Player?.Profile?.Name ?? "Unknown"}': " +
                $"hasFailed={hasFailed}, _didLowerTrack={_didLowerTrack}");
        }

        private void ApplyRemoteNoteResolution(TNote note, bool wasHit)
        {
            if (!IsRemotePlayer || EngineContainer == null)
            {
                return;
            }

            int noteWeight = Engine.GetNumberOfNotes(note);
            if (noteWeight <= 0)
            {
                noteWeight = 1;
            }

            EngineContainer.ApplyRemoteNoteResult(noteWeight, wasHit);
        }

        internal override void UpdateRemoteCountdown()
        {
            if (!IsRemotePlayer)
            {
                return;
            }

            var countdowns = Engine.WaitCountdownsReadOnly;
            if (countdowns == null || countdowns.Count == 0)
            {
                return;
            }

            double songTime = GameManager.SongTime;
            for (int i = 0; i < countdowns.Count; i++)
            {
                var countdown = countdowns[i];
                if (songTime >= countdown.Time && songTime < countdown.TimeEnd)
                {
                    OnCountdownChange(countdown.TimeLength, countdown.TimeEnd);
                    break;
                }
            }
        }

        internal override bool ResolveRemoteNote(ref int cursor, bool wasHit, double localSongTime,
            double remoteSongTime, double localLeadSeconds, double remoteLeadSeconds,
            out int resolvedWeight)
        {
            resolvedWeight = 0;

            while (cursor < Notes.Count && Notes[cursor].ParentOrSelf.WasFullyHitOrMissed())
            {
                cursor++;
            }

            if (cursor >= Notes.Count)
            {
                return false;
            }

            var note = Notes[cursor].ParentOrSelf;
            double noteTime = note.Time;

            if (noteTime > localSongTime + localLeadSeconds)
            {
                return false;
            }

            if (noteTime > remoteSongTime + remoteLeadSeconds)
            {
                return false;
            }

            resolvedWeight = Engine.GetNumberOfNotes(note);
            if (resolvedWeight <= 0)
            {
                resolvedWeight = 1;
            }

            if (wasHit)
            {
                note.SetHitState(true, true);
                OnNoteHit(cursor, note);
            }
            else
            {
                note.SetMissState(true, true);
                OnNoteMissed(cursor, note);
                
                // For remote players, check if this was a star power note that was missed.
                // If so, strip the star power flags from all notes in the phrase and trigger
                // the visual update so the SP highlight is removed from remaining notes.
                if (IsRemotePlayer && note.IsStarPower)
                {
                    StripStarPowerFromPhrase(note);
                    OnStarPowerPhraseMissed(note);
                }
            }

            if (IsRemotePlayer)
            {
                ApplyRemoteNoteResolution(note, wasHit);
            }

            cursor++;
            return true;
        }

        internal override void ResetRemoteSimulationState()
        {
            foreach (var note in Notes)
            {
                note.SetHitState(false, true);
                note.SetMissState(false, true);
            }
        }

        internal override int GetResolvedMissCount()
        {
            if (Notes == null || Notes.Count == 0)
            {
                return 0;
            }

            int missCount = 0;

            foreach (var note in Notes)
            {
                if (!note.IsParent)
                {
                    continue;
                }

                if (note.WasFullyMissed())
                {
                    missCount += Engine.GetNumberOfNotes(note);
                }
            }

            return missCount;
        }

        internal override int GetRemoteNoteCount()
        {
            return Notes.Count;
        }

        protected virtual void OnOverhit()
        {
            if (IsFc)
            {
                ComboMeter.SetFullCombo(false);
                IsFc = false;
            }

            if (LastCombo >= 10)
            {
                CameraPositioner.Punch();
            }

            LastCombo = Combo;
        }

        protected virtual void OnSoloStart(SoloSection solo)
        {
            _activeSoloSection = solo;
            _soloSyncState.Sequence = unchecked(_soloSyncState.Sequence + 1);
            _soloSyncState.IsActive = true;
            _soloSyncState.NoteCount = solo.NoteCount;
            _soloSyncState.NotesHit = 0;
            _soloSyncState.LastBonus = 0;

            TrackView.StartSolo(solo);

            foreach (var haptic in SantrollerHaptics)
            {
                haptic.SetSoloActive(true);
            }
        }

        protected virtual void OnSoloEnd(SoloSection solo)
        {
            if (_activeSoloSection != null)
            {
                _soloSyncState.NotesHit = Mathf.Clamp(_activeSoloSection.NotesHit, 0, _soloSyncState.NoteCount);
            }

            _soloSyncState.IsActive = false;
            _soloSyncState.LastBonus = Mathf.Max(0, solo.SoloBonus);
            _activeSoloSection = null;

            TrackView.EndSolo(solo.SoloBonus);

            foreach (var haptic in SantrollerHaptics)
            {
                haptic.SetSoloActive(false);
            }
        }

        protected virtual void OnCountdownChange(double countdownLength, double endTime)
        {
            TrackView.UpdateCountdown(countdownLength, endTime);
        }
        
        /// <summary>
        /// Shows a revival countdown when the player is revived via Star Power.
        /// This gives the player visual warning before notes resume.
        /// </summary>
        /// <param name="gracePeriod">The grace period duration in seconds.</param>
        public virtual void ShowRevivalCountdown(double gracePeriod)
        {
            // Calculate end time from current song time + grace period
            double endTime = GameManager.SongTime + gracePeriod;
            
            // Store the revival countdown state for continuous updates
            _revivalCountdownEndTime = endTime;
            _revivalCountdownDuration = gracePeriod;
            
            // Initial update
            TrackView.UpdateCountdown(gracePeriod, endTime);
            
            YargLogger.LogFormatDebug("[TrackPlayer] Showing revival countdown: {0}s, endTime={1}", gracePeriod, endTime);
        }
        
        /// <summary>
        /// Updates the revival countdown display if active.
        /// Called every frame during UpdateVisuals.
        /// </summary>
        private void UpdateRevivalCountdown()
        {
            // Check if revival countdown is active
            if (_revivalCountdownEndTime <= 0)
            {
                return;
            }
            
            double currentTime = GameManager.SongTime;
            double timeRemaining = _revivalCountdownEndTime - currentTime;
            
            if (timeRemaining <= 0)
            {
                // Countdown finished - clear state
                _revivalCountdownEndTime = -1;
                _revivalCountdownDuration = 0;
                return;
            }
            
            // Continue updating the countdown display
            TrackView.UpdateCountdown(_revivalCountdownDuration, _revivalCountdownEndTime);
        }

        /// <summary>
        /// Strips the star power flag from a note and all notes in its phrase.
        /// This mirrors what the engine does when a star power phrase is missed.
        /// </summary>
        protected void StripStarPowerFromPhrase(TNote note)
        {
            // Strip star power from the note and all its children
            note.Flags &= ~NoteFlags.StarPower;
            foreach (var childNote in note.ChildNotes)
            {
                childNote.Flags &= ~NoteFlags.StarPower;
            }

            // Look back until finding the start of the phrase
            if (!note.IsStarPowerStart)
            {
                var prevNote = note.PreviousNote;
                while (prevNote != null && prevNote.IsStarPower)
                {
                    prevNote.Flags &= ~NoteFlags.StarPower;
                    foreach (var childNote in prevNote.ChildNotes)
                    {
                        childNote.Flags &= ~NoteFlags.StarPower;
                    }

                    if (prevNote.IsStarPowerStart)
                    {
                        break;
                    }

                    prevNote = prevNote.PreviousNote;
                }
            }

            // Look forward until finding the end of the phrase
            if (!note.IsStarPowerEnd)
            {
                var nextNote = note.NextNote;
                while (nextNote != null && nextNote.IsStarPower)
                {
                    nextNote.Flags &= ~NoteFlags.StarPower;
                    foreach (var childNote in nextNote.ChildNotes)
                    {
                        childNote.Flags &= ~NoteFlags.StarPower;
                    }

                    if (nextNote.IsStarPowerEnd)
                    {
                        break;
                    }

                    nextNote = nextNote.NextNote;
                }
            }
        }

        protected virtual void OnStarPowerPhraseMissed(TNote note)
        {
            OnStarPowerPhraseMissed();
        }

        protected virtual void OnStarPowerPhraseHit(TNote note)
        {
            if (SettingsManager.Settings.EnableTrackEffects.Value)
            {
                StarPowerEffect.gameObject.SetActive(true);
                StarPowerEffect.PlayAnimation();
            }

            OnStarPowerPhraseHit();
        }

        public override void GameplayUpdate()
        {
            base.GameplayUpdate();

            if (LastHighScore != null && !_newHighScoreShown && Score > LastHighScore)
            {
                _newHighScoreShown = true;
                TrackView.ShowNewHighScore();
            }
        }
    }
}
