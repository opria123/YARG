using System;
using UnityEngine;

namespace YARG.Networking.Abstraction
{
    /// <summary>
    /// Lightweight player data class for the networking abstraction layer.
    /// Replaces the Mirror NetworkBehaviour-based NetworkPlayerData.
    /// Can be used without Mirror dependencies.
    /// </summary>
    public class NetworkPlayerData : MonoBehaviour
    {
        #region Private Fields

        [SerializeField]
        private string playerName = "Player";
        
        [SerializeField]
        private bool isHost;
        
        [SerializeField]
        private int playerIndex;
        
        [SerializeField]
        private bool isReady;
        
        [SerializeField]
        private int instrument = -1;
        
        [SerializeField]
        private int difficulty;
        
        [SerializeField]
        private float ping;

        [SerializeField]
        private bool sittingOut;

        // For LiteNet compatibility
        private bool _useLocalUserOverride;
        private bool _isLocalUserOverride;
        private Guid _networkPlayerId;
        private Guid _connectionId;
        private DateTime _joinTime = DateTime.UtcNow;

        // Synced preset data (for EnablePresetSync feature)
        private Guid _cameraPresetId = Guid.Empty;
        private Guid _highwayPresetId = Guid.Empty;
        private Guid _colorProfileId = Guid.Empty;
        private Guid _themePresetId = Guid.Empty;
        private string _cameraPresetJson = string.Empty;
        private string _highwayPresetJson = string.Empty;
        private string _colorProfileJson = string.Empty;
        private string _themePresetJson = string.Empty;

        // Gameplay state
        private int currentScore;
        private int currentCombo;
        private int currentStreak;
        private bool isStarPowerActive;
        private float starPowerAmount;
        private int starPowerPhrasesHit;
        private int totalStarPowerPhrases;
        private int notesHit;
        private int notesMissed;
        private int bandBonusScore;
        private int overstrums;
        private int hoposStrummed;
        private int overhits;
        private int ghostInputs;
        private int ghostsHit;
        private int accentsHit;
        private int dynamicsBonus;
        private int vocalsTicksHit;
        private int vocalsTicksMissed;
        private float vocalsPhraseTicksHit;
        private int vocalsPhraseTicksTotal;
        private uint lastGameplaySnapshotSequence;
        private double lastGameplaySongTime;
        private double lastGameplayNetworkTime;
        private float lastGameplayLatencyMs;
        private bool soloActive;
        private int soloSequence;
        private int soloNoteCount;
        private int soloNotesHit;
        private int soloLastBonus;
        private int soloTotalBonus;
        private int sustainsHeld;
        private float whammyValue;
        private float stars;
        private bool gameplayReady;
        private double gameplayReadyServerTime;
        private bool hasFailed;
        private float happiness = 1.0f;

        #endregion

        #region Events

        public event Action<string> OnPlayerNameChanged;
        public event Action<bool> OnReadyStateChanged;
        public event Action<int, int> OnInstrumentChanged;
        public event Action<bool> OnGameplayReadyChanged;
        
        // Event aliases for compatibility with UI code expecting "Event" suffix
        public event Action<string> OnPlayerNameChangedEvent
        {
            add => OnPlayerNameChanged += value;
            remove => OnPlayerNameChanged -= value;
        }
        
        public event Action<bool> OnReadyStateChangedEvent
        {
            add => OnReadyStateChanged += value;
            remove => OnReadyStateChanged -= value;
        }
        
        public event Action<int, int> OnInstrumentChangedEvent
        {
            add => OnInstrumentChanged += value;
            remove => OnInstrumentChanged -= value;
        }
        
        public event Action<int, int> OnDifficultyChangedEvent
        {
            add => OnInstrumentChanged += value;
            remove => OnInstrumentChanged -= value;
        }

        #endregion

        #region Properties

        public string PlayerName
        {
            get => playerName;
            set
            {
                if (playerName != value)
                {
                    playerName = value;
                    OnPlayerNameChanged?.Invoke(value);
                }
            }
        }

        public bool IsHost
        {
            get => isHost;
            set => isHost = value;
        }

        public int PlayerIndex
        {
            get => playerIndex;
            set => playerIndex = value;
        }

        public bool IsReady
        {
            get => isReady;
            set
            {
                if (isReady != value)
                {
                    isReady = value;
                    OnReadyStateChanged?.Invoke(value);
                }
            }
        }

        public int Instrument
        {
            get => instrument;
            set
            {
                if (instrument != value)
                {
                    instrument = value;
                    OnInstrumentChanged?.Invoke(value, difficulty);
                }
            }
        }

        public int Difficulty
        {
            get => difficulty;
            set
            {
                if (difficulty != value)
                {
                    difficulty = value;
                    OnInstrumentChanged?.Invoke(instrument, value);
                }
            }
        }

        public float Ping
        {
            get => ping;
            set => ping = value;
        }

        /// <summary>
        /// Whether this player is sitting out the current song.
        /// </summary>
        public bool SittingOut
        {
            get => sittingOut;
            set => sittingOut = value;
        }

        /// <summary>
        /// Unique identifier for this player across the network.
        /// </summary>
        public Guid NetworkPlayerId
        {
            get => _networkPlayerId;
            set => _networkPlayerId = value;
        }

        /// <summary>
        /// Unique identifier for the connection this player belongs to.
        /// This allows distinguishing players even if they have the same name or ProfileId.
        /// For the host, this is a well-known "host" GUID. For clients, it's the connection ID.
        /// </summary>
        public Guid ConnectionId
        {
            get => _connectionId;
            set => _connectionId = value;
        }

        /// <summary>
        /// The time when this player joined the session.
        /// Used for determining host promotion order in dedicated server mode.
        /// </summary>
        public DateTime JoinTime
        {
            get => _joinTime;
            set => _joinTime = value;
        }

        /// <summary>
        /// Whether this player data represents the local user.
        /// Uses override if set, otherwise defaults to false.
        /// </summary>
        public bool IsLocalUser
        {
            get => _useLocalUserOverride ? _isLocalUserOverride : false;
            set
            {
                _useLocalUserOverride = true;
                _isLocalUserOverride = value;
            }
        }

        #region Synced Preset Properties

        /// <summary>
        /// The camera preset ID for this player (used when EnablePresetSync is true).
        /// </summary>
        public Guid CameraPresetId
        {
            get => _cameraPresetId;
            set => _cameraPresetId = value;
        }

        /// <summary>
        /// The highway preset ID for this player (used when EnablePresetSync is true).
        /// </summary>
        public Guid HighwayPresetId
        {
            get => _highwayPresetId;
            set => _highwayPresetId = value;
        }

        /// <summary>
        /// The color profile ID for this player (used when EnablePresetSync is true).
        /// </summary>
        public Guid ColorProfileId
        {
            get => _colorProfileId;
            set => _colorProfileId = value;
        }

        /// <summary>
        /// The theme preset ID for this player (used when EnablePresetSync is true).
        /// </summary>
        public Guid ThemePresetId
        {
            get => _themePresetId;
            set => _themePresetId = value;
        }

        /// <summary>
        /// JSON serialized camera preset data for this player.
        /// Used when the preset ID is not found locally.
        /// </summary>
        public string CameraPresetJson
        {
            get => _cameraPresetJson;
            set => _cameraPresetJson = value ?? string.Empty;
        }

        /// <summary>
        /// JSON serialized highway preset data for this player.
        /// Used when the preset ID is not found locally.
        /// </summary>
        public string HighwayPresetJson
        {
            get => _highwayPresetJson;
            set => _highwayPresetJson = value ?? string.Empty;
        }

        /// <summary>
        /// JSON serialized color profile data for this player.
        /// Used when the preset ID is not found locally.
        /// </summary>
        public string ColorProfileJson
        {
            get => _colorProfileJson;
            set => _colorProfileJson = value ?? string.Empty;
        }

        /// <summary>
        /// JSON serialized theme preset data for this player.
        /// Used when the preset ID is not found locally.
        /// </summary>
        public string ThemePresetJson
        {
            get => _themePresetJson;
            set => _themePresetJson = value ?? string.Empty;
        }

        /// <summary>
        /// Returns true if this player has synced preset data available.
        /// </summary>
        public bool HasSyncedPresets => _cameraPresetId != Guid.Empty || 
                                        _highwayPresetId != Guid.Empty || 
                                        _colorProfileId != Guid.Empty ||
                                        _themePresetId != Guid.Empty;

        #endregion

        // Gameplay state properties
        public int CurrentScore => currentScore;
        public int CurrentCombo => currentCombo;
        public int CurrentStreak => currentStreak;
        
        public bool IsStarPowerActive
        {
            get => isStarPowerActive;
            set => isStarPowerActive = value;
        }
        
        public float StarPowerAmount
        {
            get => starPowerAmount;
            set => starPowerAmount = value;
        }
        
        // Writable aliases for gameplay sync
        public int Score
        {
            get => currentScore;
            set => currentScore = value;
        }
        
        public int Combo
        {
            get => currentCombo;
            set => currentCombo = value;
        }
        
        public int MaxCombo
        {
            get => currentStreak;
            set => currentStreak = value;
        }
        
        public int StarPowerPhrasesHit => starPowerPhrasesHit;
        public int TotalStarPowerPhrases => totalStarPowerPhrases;
        
        public int NotesHit
        {
            get => notesHit;
            set => notesHit = value;
        }
        
        public int NotesMissed
        {
            get => notesMissed;
            set => notesMissed = value;
        }
        
        public int BandBonusScore => bandBonusScore;
        public int Overstrums => overstrums;
        public int HoposStrummed => hoposStrummed;
        public int Overhits => overhits;
        public int GhostInputs => ghostInputs;
        public int GhostsHit => ghostsHit;
        public int AccentsHit => accentsHit;
        public int DynamicsBonus => dynamicsBonus;
        public int VocalsTicksHit => vocalsTicksHit;
        public int VocalsTicksMissed => vocalsTicksMissed;
        public float VocalsPhraseTicksHit => vocalsPhraseTicksHit;
        public int VocalsPhraseTicksTotal => vocalsPhraseTicksTotal;
        public uint LastGameplaySnapshotSequence => lastGameplaySnapshotSequence;
        public double LastGameplaySongTime => lastGameplaySongTime;
        public double LastGameplayNetworkTime => lastGameplayNetworkTime;
        public float LastGameplayLatencyMs => lastGameplayLatencyMs;
        public bool SoloActive => soloActive;
        public int SoloSequence => soloSequence;
        public int SoloNoteCount => soloNoteCount;
        public int SoloNotesHit => soloNotesHit;
        public int SoloLastBonus => soloLastBonus;
        public int SoloTotalBonus => soloTotalBonus;
        public int SustainsHeld => sustainsHeld;
        public float WhammyValue => whammyValue;
        public float Stars => stars;

        public bool GameplayReady
        {
            get => gameplayReady;
            set
            {
                if (gameplayReady != value)
                {
                    gameplayReady = value;
                    OnGameplayReadyChanged?.Invoke(value);
                }
            }
        }

        public double GameplayReadyServerTime
        {
            get => gameplayReadyServerTime;
            set => gameplayReadyServerTime = value;
        }

        public bool HasFailed
        {
            get => hasFailed;
            set => hasFailed = value;
        }

        public float Happiness
        {
            get => happiness;
            set => happiness = value;
        }

        #endregion

        #region Lifecycle

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
        }

        #endregion

        #region Methods

        /// <summary>
        /// Set instrument and difficulty together.
        /// </summary>
        public void SetInstrumentAndDifficulty(int instrumentValue, int difficultyValue)
        {
            bool changed = instrument != instrumentValue || difficulty != difficultyValue;
            instrument = instrumentValue;
            difficulty = difficultyValue;
            if (changed)
            {
                OnInstrumentChanged?.Invoke(instrumentValue, difficultyValue);
            }
        }

        /// <summary>
        /// Sets the gameplay ready state on the server side (host only).
        /// </summary>
        /// <param name="ready">Whether the player is ready for gameplay</param>
        /// <param name="serverTime">Server timestamp when ready state was set</param>
        public void SetGameplayReadyServer(bool ready, double serverTime)
        {
            gameplayReady = ready;
            gameplayReadyServerTime = serverTime;
            OnGameplayReadyChanged?.Invoke(ready);
        }
        
        /// <summary>
        /// Sets the gameplay ready state on the server without a timestamp.
        /// </summary>
        /// <param name="ready">Whether the player is ready for gameplay</param>
        public void SetGameplayReadyServer(bool ready)
        {
            SetGameplayReadyServer(ready, 0.0);
        }

        /// <summary>
        /// Apply a gameplay snapshot received from the network.
        /// </summary>
        public void ApplySnapshot(
            uint sequence,
            int score,
            int combo,
            int streak,
            bool starPowerActiveValue,
            float starPowerAmountValue,
            int starPowerPhrasesHitValue,
            int totalStarPowerPhrasesValue,
            int notesHitValue,
            int notesMissedValue,
            int bandBonusScoreValue,
            int overstromsValue,
            int hoposStrummedValue,
            int overhitsValue,
            int ghostInputsValue,
            int ghostsHitValue,
            int accentsHitValue,
            int dynamicsBonusValue,
            int vocalsTicksHitValue,
            int vocalsTicksMissedValue,
            float vocalsPhraseTicksHitValue,
            int vocalsPhraseTicksTotalValue,
            bool soloActiveValue,
            int soloSequenceValue,
            int soloNoteCountValue,
            int soloNotesHitValue,
            int soloLastBonusValue,
            int soloTotalBonusValue,
            int sustainsHeldValue,
            float whammyValueParam,
            double songTime,
            double networkTime,
            float latencyMs)
        {
            // Only apply if sequence is newer
            if (sequence > lastGameplaySnapshotSequence || lastGameplaySnapshotSequence == 0)
            {
                lastGameplaySnapshotSequence = sequence;
                currentScore = score;
                currentCombo = combo;
                currentStreak = streak;
                isStarPowerActive = starPowerActiveValue;
                starPowerAmount = starPowerAmountValue;
                starPowerPhrasesHit = starPowerPhrasesHitValue;
                totalStarPowerPhrases = totalStarPowerPhrasesValue;
                notesHit = notesHitValue;
                notesMissed = notesMissedValue;
                bandBonusScore = bandBonusScoreValue;
                overstrums = overstromsValue;
                hoposStrummed = hoposStrummedValue;
                overhits = overhitsValue;
                ghostInputs = ghostInputsValue;
                ghostsHit = ghostsHitValue;
                accentsHit = accentsHitValue;
                dynamicsBonus = dynamicsBonusValue;
                vocalsTicksHit = vocalsTicksHitValue;
                vocalsTicksMissed = vocalsTicksMissedValue;
                vocalsPhraseTicksHit = vocalsPhraseTicksHitValue;
                vocalsPhraseTicksTotal = vocalsPhraseTicksTotalValue;
                soloActive = soloActiveValue;
                soloSequence = soloSequenceValue;
                soloNoteCount = soloNoteCountValue;
                soloNotesHit = soloNotesHitValue;
                soloLastBonus = soloLastBonusValue;
                soloTotalBonus = soloTotalBonusValue;
                sustainsHeld = sustainsHeldValue;
                whammyValue = whammyValueParam;
                lastGameplaySongTime = songTime;
                lastGameplayNetworkTime = networkTime;
                lastGameplayLatencyMs = latencyMs;
            }
        }

        /// <summary>
        /// Reset gameplay state for a new song.
        /// </summary>
        public void ResetGameState()
        {
            currentScore = 0;
            currentCombo = 0;
            currentStreak = 0;
            isStarPowerActive = false;
            starPowerAmount = 0f;
            starPowerPhrasesHit = 0;
            totalStarPowerPhrases = 0;
            notesHit = 0;
            notesMissed = 0;
            bandBonusScore = 0;
            overstrums = 0;
            hoposStrummed = 0;
            overhits = 0;
            ghostInputs = 0;
            ghostsHit = 0;
            accentsHit = 0;
            dynamicsBonus = 0;
            vocalsTicksHit = 0;
            vocalsTicksMissed = 0;
            vocalsPhraseTicksHit = 0f;
            vocalsPhraseTicksTotal = 0;
            lastGameplaySnapshotSequence = 0;
            lastGameplaySongTime = 0;
            lastGameplayNetworkTime = 0;
            lastGameplayLatencyMs = 0;
            soloActive = false;
            soloSequence = 0;
            soloNoteCount = 0;
            soloNotesHit = 0;
            soloLastBonus = 0;
            soloTotalBonus = 0;
            sustainsHeld = 0;
            whammyValue = 0f;
            stars = 0f;
            gameplayReady = false;
            gameplayReadyServerTime = 0;
            hasFailed = false;
            happiness = 1.0f;
        }

        /// <summary>
        /// Sets all synced preset data at once.
        /// </summary>
        public void SetSyncedPresets(
            Guid cameraPresetId, string cameraPresetJson,
            Guid highwayPresetId, string highwayPresetJson,
            Guid colorProfileId, string colorProfileJson,
            Guid themePresetId, string themePresetJson)
        {
            _cameraPresetId = cameraPresetId;
            _cameraPresetJson = cameraPresetJson ?? string.Empty;
            _highwayPresetId = highwayPresetId;
            _highwayPresetJson = highwayPresetJson ?? string.Empty;
            _colorProfileId = colorProfileId;
            _colorProfileJson = colorProfileJson ?? string.Empty;
            _themePresetId = themePresetId;
            _themePresetJson = themePresetJson ?? string.Empty;
        }

        /// <summary>
        /// Clears all synced preset data.
        /// </summary>
        public void ClearSyncedPresets()
        {
            _cameraPresetId = Guid.Empty;
            _cameraPresetJson = string.Empty;
            _highwayPresetId = Guid.Empty;
            _highwayPresetJson = string.Empty;
            _colorProfileId = Guid.Empty;
            _colorProfileJson = string.Empty;
            _themePresetId = Guid.Empty;
            _themePresetJson = string.Empty;
        }

        #endregion
    }
}
