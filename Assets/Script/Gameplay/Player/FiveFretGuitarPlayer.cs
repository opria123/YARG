using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using YARG.Core;
using YARG.Core.Audio;
using YARG.Core.Chart;
using YARG.Core.Engine.Guitar;
using YARG.Core.Engine.Guitar.Engines;
using YARG.Core.Input;
using YARG.Core.Logging;
using YARG.Core.Replays;
using YARG.Gameplay.HUD;
using YARG.Gameplay.Visuals;
using YARG.Helpers;
using YARG.Playback;
using YARG.Player;
using YARG.Settings;
using YARG.Themes;
using Random = UnityEngine.Random;

namespace YARG.Gameplay.Player
{
    public sealed class FiveFretGuitarPlayer : TrackPlayer<GuitarEngine, GuitarNote>
    {
        private const double SUSTAIN_END_MUTE_THRESHOLD = 0.1;

        private const int SHIFT_INDICATOR_MEASURES_BEFORE = 5;

        public override bool ShouldUpdateInputsOnResume => true;

        private static float[] GuitarStarMultiplierThresholds => new[]
        {
            0.21f, 0.46f, 0.77f, 1.85f, 3.08f, 4.52f
        };

        private static float[] BassStarMultiplierThresholds => new[]
        {
            0.21f, 0.50f, 0.90f, 2.77f, 4.62f, 6.78f
        };

        public GuitarEngineParameters EngineParams { get; private set; }

        private double TimeFromSpawnToStrikeline => SpawnTimeOffset - (-STRIKE_LINE_POS / NoteSpeed);

        public struct RangeShiftIndicator
        {
            public double Time;
            public bool LeftSide;
            public int Offset;
            public bool RangeIndicator;
        }

        private FiveFretRangeShift[] _allRangeShiftEvents;
        private readonly Queue<FiveFretRangeShift> _rangeShiftEventQueue = new();
        private FiveFretRangeShift CurrentRange { get; set; }
        private readonly Queue<RangeShiftIndicator> _shiftIndicators = new();
        private int _shiftIndicatorIndex;
        private bool _fretPulseStarting;
        private double _fretPulseStartTime;

        private bool[] _activeFrets = null;

        [Header("Five Fret Specific")]
        [SerializeField]
        private FretArray _fretArray;
        [SerializeField]
        private Pool _shiftIndicatorPool;
        [SerializeField]
        private Pool _rangeIndicatorPool;

        public override float[] StarMultiplierThresholds { get; protected set; } =
            GuitarStarMultiplierThresholds;

        public override int[] StarScoreThresholds { get; protected set; }

        public float WhammyFactor { get; private set; }

        /// <summary>
        /// Bitmask indicating which sustain lanes are currently being held.
        /// Bit 0 = fret 0 (Green), Bit 1 = fret 1 (Red), etc.
        /// </summary>
        public int SustainsHeldBitmask { get; private set; }

        private int _sustainCount;

        private SongStem _stem;
        private double _practiceSectionStartTime;

        public override void Initialize(int index, YargPlayer player, SongChart chart, TrackView trackView, StemMixer mixer, int? currentHighScore)
        {
            _stem = player.Profile.CurrentInstrument.ToSongStem();
            if (_stem == SongStem.Bass && mixer[SongStem.Bass] == null)
            {
                _stem = SongStem.Rhythm;
            }
            base.Initialize(index, player, chart, trackView, mixer, currentHighScore);
        }

        protected override InstrumentDifficulty<GuitarNote> GetNotes(SongChart chart)
        {
            var track = chart.GetFiveFretTrack(Player.Profile.CurrentInstrument).Clone();
            return track.GetDifficulty(Player.Profile.CurrentDifficulty);
        }

        protected override GuitarEngine CreateEngine()
        {
            // If on bass, replace the star multiplier threshold
            bool isBass = Player.Profile.CurrentInstrument == Instrument.FiveFretBass;
            if (isBass)
            {
                StarMultiplierThresholds = BassStarMultiplierThresholds;
            }

            if (!Player.IsReplay)
            {
                // Create the engine params from the engine preset
                EngineParams = Player.EnginePreset.FiveFretGuitar.Create(StarMultiplierThresholds, isBass);
                //EngineParams = EnginePreset.Precision.FiveFretGuitar.Create(StarMultiplierThresholds, isBass);
            }
            else
            {
                // Otherwise, get from the replay
                EngineParams = (GuitarEngineParameters) Player.EngineParameterOverride;
            }

            var engine = new YargFiveFretGuitarEngine(NoteTrack, SyncTrack, EngineParams, Player.Profile.IsBot);
            EngineContainer = GameManager.EngineManager.Register(engine, NoteTrack.Instrument, Chart, Player.RockMeterPreset);

            HitWindow = EngineParams.HitWindow;

            YargLogger.LogFormatDebug("Note count: {0}", NoteTrack.Notes.Count);

            engine.OnNoteHit += OnNoteHit;
            engine.OnNoteMissed += OnNoteMissed;
            engine.OnOverstrum += OnOverhit;

            engine.OnSustainStart += OnSustainStart;
            engine.OnSustainEnd += OnSustainEnd;

            engine.OnSoloStart += OnSoloStart;
            engine.OnSoloEnd += OnSoloEnd;

            engine.OnStarPowerPhraseHit += OnStarPowerPhraseHit;
            engine.OnStarPowerPhraseMissed += OnStarPowerPhraseMissed;
            engine.OnStarPowerStatus += OnStarPowerStatus;

            engine.OnCountdownChange += OnCountdownChange;

            return engine;
        }

        protected override void FinishInitialization()
        {
            base.FinishInitialization();

            StarScoreThresholds = PopulateStarScoreThresholds(StarMultiplierThresholds, Engine.BaseScore);

            IndicatorStripes.Initialize(Player.EnginePreset.FiveFretGuitar);
            _fretArray.Initialize(
                Player.ThemePreset,
                VisualStyle.FiveFretGuitar,
                Player.ColorProfile.FiveFretGuitar,
                Player.Profile.LeftyFlip,
                false, // Not applicable to five fret
                false, // Not applicable to five fret
                false  // Not applicable to five fret
                );

            if (Player.Profile.RangeEnabled)
            {
                _activeFrets = new bool[_fretArray.FretCount];
                _allRangeShiftEvents = FiveFretRangeShift.GetRangeShiftEvents(NoteTrack);
                InitializeRangeShift();
            }

            GameManager.BeatEventHandler.Visual.Subscribe(_fretArray.PulseFretColors, BeatEventType.StrongBeat);
        }

        public override void ResetPracticeSection()
        {
            base.ResetPracticeSection();
            ResetRangeShift(_practiceSectionStartTime);

            _fretArray.ResetAll();
        }

        public override void SetPracticeSection(uint start, uint end)
        {
            base.SetPracticeSection(start, end);

            // This will set the current range correctly
            _practiceSectionStartTime = SyncTrack.TickToTime(start);
            ResetRangeShift(_practiceSectionStartTime);
        }

        public override void SetReplayTime(double time)
        {
            ResetRangeShift(time);
            base.SetReplayTime(time);
        }

        protected override void UpdateVisuals(double visualTime)
        {
            base.UpdateVisuals(visualTime);
            UpdateRangeShift(visualTime);
            UpdateFretArray();
        }

        public void UpdateRangeShift(double visualTime)
        {
            if (!_rangeShiftEventQueue.TryPeek(out var nextShift))
            {
                return;
            }

            if (_shiftIndicators.TryPeek(out var shiftIndicator) && shiftIndicator.Time <= visualTime + SpawnTimeOffset)
            {
                // The range indicator is dealt with in its own function
                if (shiftIndicator.RangeIndicator)
                {
                    SpawnRangeIndicator(nextShift);
                    return;
                }
                if (!_shiftIndicatorPool.CanSpawnAmount(1))
                {
                    return;
                }

                var poolable = _shiftIndicatorPool.TakeWithoutEnabling();
                if (poolable == null)
                {
                    YargLogger.LogWarning("Attempted to spawn shift indicator, but it's at its cap!");
                    return;
                }

                YargLogger.LogDebug("Shift indicator spawned!");

                ((GuitarShiftIndicatorElement) poolable).RangeShiftIndicator = shiftIndicator;
                poolable.EnableFromPool();

                _shiftIndicators.Dequeue();

                if (!_fretPulseStarting)
                {
                    _fretPulseStarting = true;
                    _fretPulseStartTime = nextShift.Time - (nextShift.BeatDuration * SHIFT_INDICATOR_MEASURES_BEFORE);
                }
            }

            if (_fretPulseStarting && _fretPulseStartTime <= visualTime)
            {
                for (var i = nextShift.Position - 1; i < nextShift.Position + nextShift.Size - 1; i++)
                {
                    _fretArray.SetFretColorPulse(i, true, (float) nextShift.BeatDuration);
                }

                _fretPulseStarting = false;
            }


            // Turn off the pulsing and switch active frets now that we're in the new range
            if (nextShift.Time <= visualTime)
            {
                _rangeShiftEventQueue.Dequeue();
                for (var i = 0; i < _fretArray.FretCount; i++)
                {
                    _fretArray.SetFretColorPulse(i, false, (float) nextShift.BeatDuration);
                }

                _fretPulseStarting = false;
                CurrentRange = nextShift;
                SetActiveFretsForShiftEvent(nextShift);
            }
        }

        private void ResetRangeShift(double time)
        {
            if (!Player.Profile.RangeEnabled)
            {
                return;
            }

            // Despawn shift indicators and rebuild the shift queues based on the replay time
            _rangeShiftEventQueue.Clear();
            _shiftIndicators.Clear();
            _shiftIndicatorPool.ReturnAllObjects();
            _rangeIndicatorPool.ReturnAllObjects();
            InitializeRangeShift(time);

        }

        private void UpdateFretArray()
        {
            // For remote players, we use the sustain bitmask to determine which frets are "pressed"
            // This keeps the glow/pressed effect active while sustaining
            if (IsRemotePlayer)
            {
                for (int fretIndex = 0; fretIndex < 5; fretIndex++)
                {
                    int fretBit = 1 << fretIndex;
                    bool isHeld = (SustainsHeldBitmask & fretBit) != 0;
                    _fretArray.SetPressed(fretIndex, isHeld);
                }
                return;
            }

            for (var fret = GuitarAction.GreenFret; fret <= GuitarAction.OrangeFret; fret++)
            {
                _fretArray.SetPressed((int) fret, Engine.IsFretHeld(fret));
            }
        }

        private void SpawnRangeIndicator(FiveFretRangeShift nextShift)
        {
            if (!_rangeIndicatorPool.CanSpawnAmount(1))
            {
                return;
            }

            var poolable = _rangeIndicatorPool.TakeWithoutEnabling();
            if (poolable == null)
            {
                YargLogger.LogWarning("Attempted to spawn range indicator, but it's at its cap!");
                return;
            }

            YargLogger.LogDebug("Range indicator spawned!");

            ((GuitarRangeIndicatorElement) poolable).RangeShift = nextShift;
            poolable.EnableFromPool();

            _shiftIndicators.Dequeue();
        }

        public override void SetStemMuteState(bool muted)
        {
            if (IsStemMuted != muted)
            {
                GameManager.ChangeStemMuteState(_stem, muted);
                IsStemMuted = muted;
            }
        }

        public override void SetStarPowerFX(bool active)
        {
            GameManager.ChangeStemReverbState(_stem, active);
        }

        public override void MarkAsDisconnected()
        {
            base.MarkAsDisconnected();
            
            // Hide the fret array (frets/strike zone)
            if (_fretArray != null)
            {
                _fretArray.gameObject.SetActive(false);
            }
            
            // Return shift and range indicator pools
            if (_shiftIndicatorPool != null)
            {
                _shiftIndicatorPool.ReturnAllObjects();
            }
            
            if (_rangeIndicatorPool != null)
            {
                _rangeIndicatorPool.ReturnAllObjects();
            }
        }

        protected override void ResetVisuals()
        {
            base.ResetVisuals();

            _fretArray.ResetAll();
        }

        protected override void InitializeSpawnedNote(IPoolable poolable, GuitarNote note)
        {
            ((FiveFretGuitarNoteElement) poolable).NoteRef = note;
        }

        protected override void OnNoteHit(int index, GuitarNote chordParent)
        {
            base.OnNoteHit(index, chordParent);

            if (GameManager.Paused) return;

            foreach (var note in chordParent.AllNotes)
            {
                (NotePool.GetByKey(note) as FiveFretGuitarNoteElement)?.HitNote();

                if (note.Fret != (int) FiveFretGuitarFret.Open)
                {
                    _fretArray.PlayHitAnimation(note.Fret - 1);
                    
                    // For remote players, also light up the fret briefly since we're not
                    // simulating their button presses. This gives the same visual feedback
                    // as local players get when they hold the fret and hit a note.
                    // For sustain notes, the sustain sync will handle the pressed state.
                    if (IsRemotePlayer)
                    {
                        // For non-sustain notes, briefly flash the fret light
                        // For sustain notes, also set pressed here - SyncSustainVisualsWithNetwork will maintain it
                        _fretArray.SetPressed(note.Fret - 1, true);
                        
                        if (!note.IsSustain)
                        {
                            // Schedule the fret to turn off after a brief flash for non-sustains
                            StartCoroutine(ReleaseFretAfterDelay(note.Fret - 1, 0.1f));
                        }
                    }
                }
                else
                {
                    _fretArray.PlayOpenHitAnimation();
                }
            }
        }

        /// <summary>
        /// Coroutine to release a fret after a brief delay.
        /// Used for remote players to flash the fret light when they hit non-sustain notes.
        /// </summary>
        private IEnumerator ReleaseFretAfterDelay(int fretIndex, float delay)
        {
            yield return new WaitForSeconds(delay);
            
            // Only release if not currently being held by a sustain
            int fretBit = 1 << fretIndex;
            if ((SustainsHeldBitmask & fretBit) == 0)
            {
                _fretArray.SetPressed(fretIndex, false);
            }
        }

        protected override void OnNoteMissed(int index, GuitarNote chordParent)
        {
            base.OnNoteMissed(index, chordParent);

            foreach (var note in chordParent.AllNotes)
            {
                (NotePool.GetByKey(note) as FiveFretGuitarNoteElement)?.MissNote();
            }
        }

        protected override void OnOverhit()
        {
            base.OnOverhit();

            if (GameManager.IsSeekingReplay)
            {
                return;
            }

            if (SettingsManager.Settings.OverstrumAndOverhitSoundEffects.Value)
            {
                const int MIN = (int) SfxSample.Overstrum1;
                const int MAX = (int) SfxSample.Overstrum4;

                var randomOverstrum = (SfxSample) Random.Range(MIN, MAX + 1);
                GlobalAudioHandler.PlaySoundEffect(randomOverstrum);
            }

            // To check if held frets are valid
            GuitarNote currentNote = null;
            if (Engine.NoteIndex < Notes.Count)
            {
                var note = Notes[Engine.NoteIndex];

                // Don't take the note if it's not within the hit window
                // TODO: Make BaseEngine.IsNoteInWindow public and use that instead
                var (frontEnd, backEnd) = Engine.CalculateHitWindow();
                if (Engine.CurrentTime >= (note.Time + frontEnd) && Engine.CurrentTime <= (note.Time + backEnd))
                {
                    currentNote = note;
                }
            }

            // Play miss animation for every held fret that does not match the current note
            bool anyHeld = false;
            for (var fret = GuitarAction.GreenFret; fret <= GuitarAction.OrangeFret; fret++)
            {
                if (!Engine.IsFretHeld(fret))
                {
                    continue;
                }

                anyHeld = true;

                if (currentNote == null || (currentNote.NoteMask & (1 << (int) fret)) == 0)
                {
                    _fretArray.PlayMissAnimation((int) fret);
                }
            }

            // Play open-strum miss if no frets are held
            if (!anyHeld)
            {
                _fretArray.PlayOpenMissAnimation();
            }
        }

        private void OnSustainStart(GuitarNote parent)
        {
            foreach (var note in parent.AllNotes)
            {
                // If the note is disjoint, only iterate the parent as sustains are added separately
                if (parent.IsDisjoint && parent != note)
                {
                    continue;
                }

                if (note.Fret != (int) FiveFretGuitarFret.Open)
                {
                    int fretIndex = note.Fret - 1;
                    _fretArray.SetSustained(fretIndex, true);
                    // Set the bit for this fret in the bitmask
                    SustainsHeldBitmask |= (1 << fretIndex);
                }

                _sustainCount++;
            }
        }

        private void OnSustainEnd(GuitarNote parent, double timeEnded, bool finished)
        {
            foreach (var note in parent.AllNotes)
            {
                // If the note is disjoint, only iterate the parent as sustains are added separately
                if (parent.IsDisjoint && parent != note)
                {
                    continue;
                }

                (NotePool.GetByKey(note) as FiveFretGuitarNoteElement)?.SustainEnd(finished);

                if (note.Fret != (int) FiveFretGuitarFret.Open)
                {
                    int fretIndex = note.Fret - 1;
                    _fretArray.SetSustained(fretIndex, false);
                    // Clear the bit for this fret in the bitmask
                    SustainsHeldBitmask &= ~(1 << fretIndex);
                }

                _sustainCount--;
            }

            // Mute the stem if you let go of the sustain too early.
            // Leniency is handled by the engine's sustain burst threshold.
            if (!finished)
            {
                if (!parent.IsDisjoint || _sustainCount == 0)
                {
                    SetStemMuteState(true);
                }
            }

            if (_sustainCount == 0)
            {
                WhammyFactor = 0;
                GameManager.ChangeStemWhammyPitch(_stem, 0);
            }
        }

        protected override void OnStarPowerPhraseMissed()
        {
            base.OnStarPowerPhraseMissed();
            foreach (var note in NotePool.AllSpawned)
            {
                (note as FiveFretGuitarNoteElement)?.OnStarPowerUpdated();
            }
        }

        protected override bool InterceptInput(ref GameInput input)
        {
            // Ignore SP in practice mode
            if (input.GetAction<GuitarAction>() == GuitarAction.StarPower && GameManager.IsPractice) return true;

            return false;
        }

        protected override void OnInputQueued(GameInput input)
        {
            base.OnInputQueued(input);

            // Update the whammy factor
            if (_sustainCount > 0 && input.GetAction<GuitarAction>() == GuitarAction.Whammy)
            {
                WhammyFactor = Mathf.Clamp01(input.Axis);
                GameManager.ChangeStemWhammyPitch(_stem, WhammyFactor);
            }
        }

        public override (ReplayFrame Frame, ReplayStats Stats) ConstructReplayData()
        {
            var frame = new ReplayFrame(Player.Profile, EngineParams, Engine.EngineStats, ReplayInputs.ToArray());
            return (frame, Engine.EngineStats.ConstructReplayStats(Player.Profile.Name));
        }


        private void InitializeRangeShift(double time = 0)
        {
            var firstShiftAfterFirstNote = false;
            _rangeShiftEventQueue.Clear();
            // Default to all frets on
            SetDefaultActiveFrets();

            // No range shifts, so just return
            if (_allRangeShiftEvents.Length < 1)
            {
                return;
            }

            // Now that we know there is at least one range shift, figure out if it is after the first note
            if (_allRangeShiftEvents[0].Time > Notes[0].Time)
            {
                firstShiftAfterFirstNote = true;
            }

            if (_allRangeShiftEvents.Length == 1)
            {
                // There are no actual shifts (or we aren't shifting because of range compression), but we should dim unused frets
                CurrentRange = _allRangeShiftEvents[0];
                // If the range shift is after the first note, leave all the frets on because chart is broke
                if (!firstShiftAfterFirstNote)
                {
                    SetActiveFretsForShiftEvent(CurrentRange);
                }

                return;
            }

            // Turns out that we have range shifts that need indicators
            var firstEvent = _allRangeShiftEvents[0];

            FiveFretRangeShift mostRecentEvent = firstEvent;

            // Only queue range shifts that happen after time
            for (int i = 1; i < _allRangeShiftEvents.Length; i++)
            {
                FiveFretRangeShift e = _allRangeShiftEvents[i];
                // These have no visible effect on the track, so we just
                // want to make sure any that are current or in the future are queued
                // and to figure out which was the most recent event
                if (e.Time >= time)
                {
                    _rangeShiftEventQueue.Enqueue(e);
                    continue;
                }

                if (e.Time > mostRecentEvent.Time)
                {
                    mostRecentEvent = e;
                }
            }

            CurrentRange = mostRecentEvent;
            if (time < mostRecentEvent.Time)
            {
                // If we get here, the only range shifts are in the future
                SetDefaultActiveFrets();
            }
            else
            {
                SetActiveFretsForShiftEvent(CurrentRange);
            }

            // Figure out where the indicators should go
            var beatlines = Beatlines
                .Where(i => i.Type is BeatlineType.Measure or BeatlineType.Strong)
                .ToList();

            _shiftIndicators.Clear();
            var lastShiftRange = mostRecentEvent;
            int beatlineIndex = 0;

            foreach (var shift in _rangeShiftEventQueue.ToList())
            {
                if (shift.Position == lastShiftRange.Position && shift.Size == lastShiftRange.Size)
                {
                    continue;
                }

                // When shift.Position and lastShiftRange.Position are the same, this result doesn't matter because
                // the shift indicator won't be displayed, so it's OK that neither of these are <= or >=
                var shiftLeft = Player.Profile.LeftyFlip
                    ? shift.Position < lastShiftRange.Position
                    : shift.Position > lastShiftRange.Position;

                double lastBeatTime = 0;
                double firstBeatTime = double.MaxValue;

                // Find the first beatline index after the range shift
                for (; beatlineIndex < beatlines.Count; beatlineIndex++)
                {
                    if (beatlines[beatlineIndex].Time > shift.Time)
                    {
                        lastBeatTime = beatlines[beatlineIndex].Time;
                        break;
                    }
                }

                // Add the indicators before the range shift
                // While we're doing this, figure out the time between beats
                for (int i = SHIFT_INDICATOR_MEASURES_BEFORE; i > 0; i--)
                {
                    var realIndex = beatlineIndex - i;

                    // If the indicator is before any measures, skip
                    if (realIndex < 0)
                    {
                        break;
                    }

                    firstBeatTime = beatlines[realIndex].Time < firstBeatTime ? beatlines[realIndex].Time : firstBeatTime;

                    _shiftIndicators.Enqueue(new RangeShiftIndicator
                    {
                        Time = beatlines[realIndex].Time,
                        LeftSide = shiftLeft,
                        Offset = shiftLeft ? ((shift.Position + shift.Size) - 6) * -1 : shift.Position - 1,
                        RangeIndicator = i == 1 && !(shift.Position == lastShiftRange.Position && shift.Size == lastShiftRange.Size),
                    });
                }

                lastShiftRange = shift;

                // In case we have no samples for this shift event, 0.5 is a reasonable default
                shift.BeatDuration = firstBeatTime < double.MaxValue ? (lastBeatTime - firstBeatTime) / SHIFT_INDICATOR_MEASURES_BEFORE : 0.5;
            }
        }

        private void SetActiveFretsForShiftEvent(FiveFretRangeShift range)
        {
            bool[] newFrets = new bool[5];

            int start = range.Position - 1;
            int end = start + range.Size;
            for (int i = start; i < end; i++)
            {
                newFrets[i] = true;
            }

            if (!newFrets.SequenceEqual(_activeFrets))
            {
                _activeFrets = newFrets;
                _fretArray.UpdateFretActiveState(_activeFrets);
            }
        }

        private void SetDefaultActiveFrets()
        {
            bool[] newFrets = { true, true, true, true, true };

            if (!newFrets.SequenceEqual(_activeFrets))
            {
                _activeFrets = newFrets;
                _fretArray.UpdateFretActiveState(_activeFrets);
            }
        }

        /// <summary>
        /// Applies remote player's sustain state changes.
        /// Called by RemotePlayerSimulation when the network sustain bitmask changes.
        /// Note: Fret visual states are now managed by SyncSustainVisualsWithNetwork to ensure
        /// fret burning and note element states stay synchronized.
        /// This method handles sustain drop events (when remote player releases a sustain).
        /// </summary>
        /// <param name="oldSustains">Previous sustain bitmask</param>
        /// <param name="newSustains">New sustain bitmask</param>
        public void ApplyRemoteSustainState(int oldSustains, int newSustains)
        {
            // Check each fret for sustain drops (transitions from held to not-held)
            for (int fretIndex = 0; fretIndex < 5; fretIndex++)
            {
                int fretBit = 1 << fretIndex;
                bool wasHeld = (oldSustains & fretBit) != 0;
                bool isHeld = (newSustains & fretBit) != 0;

                if (wasHeld && !isHeld)
                {
                    // Sustain ended (1→0) - gray out the sustain note elements
                    // Fret index is 0-4, but note.Fret is 1-5 (0 = open)
                    int noteFret = fretIndex + 1;
                    ApplySustainEndToNoteElements(noteFret);
                }
            }
            
            // Note: We don't update fret visual states or SustainsHeldBitmask here anymore.
            // SyncSustainVisualsWithNetwork handles synchronized fret + note element state
            // to prevent visual desync (e.g., fret burning but note grey, or vice versa).
        }

        /// <summary>
        /// Applies sustain end state to note elements on a specific fret.
        /// Called when a remote player releases a sustain.
        /// </summary>
        /// <param name="noteFret">The fret number (1-5, or 0 for open)</param>
        private void ApplySustainEndToNoteElements(int noteFret)
        {
            // Iterate through all spawned notes and find active sustain notes on this fret
            foreach (var poolable in NotePool.AllSpawned)
            {
                if (poolable is not FiveFretGuitarNoteElement noteElement)
                    continue;

                var note = noteElement.NoteRef;
                if (note == null || !note.IsSustain)
                    continue;

                // Only affect notes that have been hit (sustain is active)
                if (!note.WasHit)
                    continue;

                // Check if this note is on the target fret
                // Note: For chords, check all notes in the chord
                bool matchesFret = false;
                foreach (var childNote in note.AllNotes)
                {
                    if (childNote.Fret == noteFret)
                    {
                        matchesFret = true;
                        break;
                    }
                }

                if (!matchesFret)
                    continue;

                // Call SustainEnd to gray out the sustain
                noteElement.SustainEnd(false);
            }
        }

        /// <summary>
        /// Syncs sustain note visuals with network state.
        /// Handles the case where local simulation resolved a note differently than the actual remote player.
        /// For example, if local thought a sustain was hit but remote actually missed it (or vice versa).
        /// Also handles the case where network shows a sustain is held but local hasn't processed the hit yet.
        /// This also syncs the fret array "burning" state to ensure frets and note elements stay in sync.
        /// 
        /// OPTIMISTIC APPROACH: Once a note is resolved as "hit" by local simulation, we keep it
        /// in the "Hitting" visual state until either:
        /// 1. The sustain time window ends, OR
        /// 2. Network explicitly tells us the sustain was dropped (network had it held but now doesn't)
        /// This prevents the "pop-in" effect where notes start grey and then light up after network data arrives.
        /// </summary>
        /// <param name="networkSustainBitmask">The current sustain bitmask from network (frets 0-4 as bits 0-4)</param>
        public void SyncSustainVisualsWithNetwork(int networkSustainBitmask)
        {
            // Use GameManager.SongTime instead of Engine.CurrentTime because for remote players
            // the engine isn't updated (BaseEngine.Update is never called for remote players)
            double currentTime = GameManager.SongTime;
            
            // Track which frets SHOULD be burning based on note timing + network state
            // This ensures fret burning matches note element state
            int validFretBurnMask = 0;
            
            // Iterate through all spawned notes and check for mismatches
            foreach (var poolable in NotePool.AllSpawned)
            {
                if (poolable is not FiveFretGuitarNoteElement noteElement)
                    continue;

                var note = noteElement.NoteRef;
                if (note == null || !note.IsSustain)
                    continue;

                // Check timing constraints
                bool noteHasBeenReached = currentTime >= note.Time;
                bool sustainStillActive = currentTime < note.TimeEnd;
                bool withinSustainWindow = noteHasBeenReached && sustainStillActive;

                // Check if the note was resolved as hit by local simulation
                // This is set by ResolveRemoteNote -> OnNoteHit
                bool noteWasHit = note.WasHit;
                
                // Check if any fret of this note is supposed to be held according to network
                bool networkSaysHeld = false;
                int noteFretMask = 0; // Track which frets this note uses
                foreach (var childNote in note.AllNotes)
                {
                    int fret = childNote.Fret;
                    if (fret >= 1 && fret <= 5)
                    {
                        // Fret 1-5 maps to bits 0-4
                        int fretBit = 1 << (fret - 1);
                        noteFretMask |= fretBit;
                        if ((networkSustainBitmask & fretBit) != 0)
                        {
                            networkSaysHeld = true;
                        }
                    }
                    // Note: Open notes (fret 0) don't have a sustain bitmask representation
                }

                // OPTIMISTIC APPROACH:
                // - If note was hit and sustain is still active, show as "Hitting"
                // - Only show as dropped if network says sustain is not held AND either:
                //   1. Network previously had it held (wasBeingHeld), OR
                //   2. Enough time has passed for network to report (grace period expired)
                // This prevents the "pop-in" effect while still detecting drops properly.
                
                // First, check for sustain drops from network BEFORE deciding visual state
                // This prevents the "hit then immediately drop" visual glitch
                
                // Give network time to report before treating "not held" as a drop
                // This is needed because network data may arrive slightly after the note is hit
                const double NETWORK_GRACE_PERIOD = 0.15; // 150ms grace period for network latency
                bool hadTimeForNetworkUpdate = currentTime > note.Time + NETWORK_GRACE_PERIOD;
                
                bool networkExplicitlyDropped = false;
                if (noteWasHit && withinSustainWindow && !networkSaysHeld && noteFretMask != 0)
                {
                    // Check if this fret was previously marked as burning (meaning network previously said held)
                    bool wasBeingHeld = (SustainsHeldBitmask & noteFretMask) != 0;
                    
                    // Detect drop if:
                    // 1. Network previously said held but now doesn't (explicit drop), OR
                    // 2. Enough time has passed and network never said held (missed/quick drop)
                    if (wasBeingHeld || hadTimeForNetworkUpdate)
                    {
                        // Network says not held - treat as drop
                        networkExplicitlyDropped = true;
                    }
                }
                
                if (withinSustainWindow)
                {
                    if (networkExplicitlyDropped)
                    {
                        // Network explicitly told us this sustain was dropped
                        // Show the note as missed/dropped (grey state)
                        if (noteElement.SustainState == SustainState.Hitting)
                        {
                            noteElement.SustainEnd(false); // dropped, not finished
                        }
                        // Don't add to valid burn mask - fret should stop burning
                        validFretBurnMask &= ~noteFretMask;
                    }
                    else if (noteWasHit)
                    {
                        // Note was resolved as hit - show as "Hitting" since no drop signal received
                        if (noteElement.SustainState != SustainState.Hitting)
                        {
                            noteElement.HitNote();
                        }
                        
                        // Mark frets for burning if network confirms they're held
                        // (or if network hasn't told us otherwise yet - be optimistic for visuals)
                        if (networkSaysHeld || noteFretMask == 0)
                        {
                            validFretBurnMask |= noteFretMask;
                        }
                    }
                    else if (networkSaysHeld)
                    {
                        // Note wasn't resolved as hit yet, but network says it's being held.
                        // This can happen if network state arrives before local resolution.
                        // Show as hitting and mark for burning.
                        if (noteElement.SustainState != SustainState.Hitting)
                        {
                            noteElement.HitNote();
                        }
                        validFretBurnMask |= noteFretMask;
                    }
                }
                else if (noteHasBeenReached && !sustainStillActive)
                {
                    // Sustain time window has ended - make sure visual reflects this
                    if (noteElement.SustainState == SustainState.Hitting)
                    {
                        noteElement.SustainEnd(true); // finished = true since time ended
                    }
                }
            }
            
            // Sync fret array burning state to match note element state
            // This ensures frets only burn when there's a corresponding active sustain note
            for (int fretIndex = 0; fretIndex < 5; fretIndex++)
            {
                int fretBit = 1 << fretIndex;
                bool fretShouldBurn = (validFretBurnMask & fretBit) != 0;
                bool networkSaysBurning = (networkSustainBitmask & fretBit) != 0;
                bool currentlyBurning = (SustainsHeldBitmask & fretBit) != 0;
                
                // The fret should only burn if:
                // 1. We have a valid sustain note that's active AND
                // 2. Either network says the sustain is held OR we're being optimistic (note was hit)
                bool targetBurnState = fretShouldBurn;
                
                if (targetBurnState != currentlyBurning)
                {
                    _fretArray.SetSustained(fretIndex, targetBurnState);
                    _fretArray.SetPressed(fretIndex, targetBurnState);
                    
                    if (targetBurnState && !currentlyBurning)
                    {
                        // Fret started burning - play hit animation for the glow effect
                        _fretArray.PlayHitAnimation(fretIndex);
                        SustainsHeldBitmask |= fretBit;
                    }
                    else if (!targetBurnState && currentlyBurning)
                    {
                        // Fret stopped burning
                        SustainsHeldBitmask &= ~fretBit;
                    }
                }
            }
        }

        /// <summary>
        /// Applies remote player's whammy bar value.
        /// Called by RemotePlayerSimulation when the whammy value changes.
        /// </summary>
        /// <param name="whammyValue">Whammy bar position (0 = not pressed, 1 = fully pressed)</param>
        public void ApplyRemoteWhammyValue(float whammyValue)
        {
            WhammyFactor = Mathf.Clamp01(whammyValue);
            // Note: We don't call GameManager.ChangeStemWhammyPitch for remote players
            // because that would affect the local audio mix. The whammy visual effect
            // is driven by WhammyFactor which can be read by the track visuals.
        }
    }
}