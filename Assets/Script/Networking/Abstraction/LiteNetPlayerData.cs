using System;
using UnityEngine;
using YARG.Net.Sessions;

namespace YARG.Networking.Abstraction
{
    /// <summary>
    /// Unity MonoBehaviour wrapper around NetworkPlayer from YARG.Net.
    /// Provides Unity lifecycle management while delegating all player state to the core class.
    /// </summary>
    public class LiteNetPlayerData : MonoBehaviour
    {
        /// <summary>
        /// The underlying player data from YARG.Net.
        /// Can be used by dedicated servers without Unity dependencies.
        /// </summary>
        public NetworkPlayer Player { get; private set; }

        #region Events

        // Unity-side events that forward from NetworkPlayer
        public event Action<string> OnPlayerNameChanged;
        public event Action<bool> OnReadyStateChanged;
        public event Action<int, int> OnInstrumentChanged; // (instrument, difficulty)
        public event Action<int, int> OnDifficultyChanged; // (instrument, difficulty) - same as OnInstrumentChanged
        public event Action<bool> OnGameplayReadyChanged;

        #endregion

        #region Properties - Delegate to NetworkPlayer

        public string PlayerName
        {
            get => Player.DisplayName;
            set => Player.DisplayName = value;
        }

        public int PlayerIndex
        {
            get => Player.PlayerIndex;
            set => Player.PlayerIndex = value;
        }

        public bool IsHost
        {
            get => Player.IsHost;
            set => Player.IsHost = value;
        }

        public bool IsLocal
        {
            get => Player.IsLocal;
            set => Player.IsLocal = value;
        }

        public Guid ConnectionId
        {
            get => Player.ConnectionId;
            set => Player.ConnectionId = value;
        }

        public float Ping
        {
            get => Player.Ping;
            set => Player.Ping = value;
        }

        public bool IsReady
        {
            get => Player.IsReady;
            set => Player.IsReady = value;
        }

        public int Instrument
        {
            get => Player.Instrument;
            set => Player.Instrument = value;
        }

        public int Difficulty
        {
            get => Player.Difficulty;
            set => Player.Difficulty = value;
        }

        // Gameplay state properties - delegate to NetworkPlayer
        public int CurrentScore => Player.CurrentScore;
        public int CurrentCombo => Player.CurrentCombo;
        public int CurrentStreak => Player.CurrentStreak;
        public bool IsStarPowerActive => Player.IsStarPowerActive;
        public float StarPowerAmount => Player.StarPowerAmount;
        public int StarPowerPhrasesHit => Player.StarPowerPhrasesHit;
        public int TotalStarPowerPhrases => Player.TotalStarPowerPhrases;
        public int NotesHit => Player.NotesHit;
        public int NotesMissed => Player.NotesMissed;
        public int BandBonusScore => Player.BandBonusScore;
        public int Overstrums => Player.Overstrums;
        public int HoposStrummed => Player.HoposStrummed;
        public int Overhits => Player.Overhits;
        public int GhostInputs => Player.GhostInputs;
        public int GhostsHit => Player.GhostsHit;
        public int AccentsHit => Player.AccentsHit;
        public int DynamicsBonus => Player.DynamicsBonus;
        public int VocalsTicksHit => Player.VocalsTicksHit;
        public int VocalsTicksMissed => Player.VocalsTicksMissed;
        public float VocalsPhraseTicksHit => Player.VocalsPhraseTicksHit;
        public int VocalsPhraseTicksTotal => Player.VocalsPhraseTicksTotal;
        public uint LastGameplaySnapshotSequence => Player.LastGameplaySnapshotSequence;
        public double LastGameplaySongTime => Player.LastGameplaySongTime;
        public double LastGameplayNetworkTime => Player.LastGameplayNetworkTime;
        public float LastGameplayLatencyMs => Player.LastGameplayLatencyMs;
        public bool SoloActive => Player.SoloActive;
        public int SoloSequence => Player.SoloSequence;
        public int SoloNoteCount => Player.SoloNoteCount;
        public int SoloNotesHit => Player.SoloNotesHit;
        public int SoloLastBonus => Player.SoloLastBonus;
        public int SoloTotalBonus => Player.SoloTotalBonus;
        public int SustainsHeld => Player.SustainsHeld;
        public float WhammyValue => Player.WhammyValue;

        public bool GameplayReady
        {
            get => Player.GameplayReady;
            set => Player.GameplayReady = value;
        }

        public double GameplayReadyServerTime
        {
            get => Player.GameplayReadyServerTime;
            set => Player.GameplayReadyServerTime = value;
        }

        public bool HasFailed
        {
            get => Player.HasFailed;
            set => Player.HasFailed = value;
        }

        /// <summary>
        /// For compatibility with NetworkPlayerData.IsLocalUser
        /// </summary>
        public bool IsLocalUser => Player.IsLocal;

        #endregion

        #region Lifecycle

        private void Awake()
        {
            Player = new NetworkPlayer();
            
            // Wire up events from NetworkPlayer to Unity events
            Player.NameChanged += name => OnPlayerNameChanged?.Invoke(name);
            Player.ReadyStateChanged += ready => OnReadyStateChanged?.Invoke(ready);
            Player.InstrumentChanged += (instrument, difficulty) =>
            {
                OnInstrumentChanged?.Invoke(instrument, difficulty);
                OnDifficultyChanged?.Invoke(instrument, difficulty);
            };
            Player.GameplayReadyChanged += ready => OnGameplayReadyChanged?.Invoke(ready);
            
            DontDestroyOnLoad(gameObject);
        }

        private void OnDestroy()
        {
            // Clean up event subscriptions
            if (Player != null)
            {
                Player.NameChanged -= name => OnPlayerNameChanged?.Invoke(name);
                Player.ReadyStateChanged -= ready => OnReadyStateChanged?.Invoke(ready);
                Player.GameplayReadyChanged -= ready => OnGameplayReadyChanged?.Invoke(ready);
            }
        }

        #endregion

        #region Methods - Delegate to NetworkPlayer

        /// <summary>
        /// Apply a gameplay snapshot received from the network.
        /// </summary>
        public void ApplySnapshot(
            uint sequence,
            int score,
            int combo,
            int streak,
            bool starPowerActive,
            float starPowerAmount,
            int starPowerPhrasesHit,
            int totalStarPowerPhrases,
            int notesHit,
            int notesMissed,
            int bandBonusScore,
            int overstrums,
            int hoposStrummed,
            int overhits,
            int ghostInputs,
            int ghostsHit,
            int accentsHit,
            int dynamicsBonus,
            int vocalsTicksHit,
            int vocalsTicksMissed,
            float vocalsPhraseTicksHit,
            int vocalsPhraseTicksTotal,
            bool soloActive,
            int soloSequence,
            int soloNoteCount,
            int soloNotesHit,
            int soloLastBonus,
            int soloTotalBonus,
            int sustainsHeld,
            float whammyValue,
            double songTime,
            double networkTime,
            float latencyMs)
        {
            Player.ApplySnapshot(
                sequence, score, combo, streak,
                starPowerActive, starPowerAmount, starPowerPhrasesHit, totalStarPowerPhrases,
                notesHit, notesMissed, bandBonusScore,
                overstrums, hoposStrummed, overhits, ghostInputs, ghostsHit, accentsHit, dynamicsBonus,
                vocalsTicksHit, vocalsTicksMissed, vocalsPhraseTicksHit, vocalsPhraseTicksTotal,
                soloActive, soloSequence, soloNoteCount, soloNotesHit, soloLastBonus, soloTotalBonus,
                sustainsHeld, whammyValue,
                songTime, networkTime, latencyMs);
        }

        /// <summary>
        /// Reset gameplay state for a new song.
        /// </summary>
        public void ResetGameState()
        {
            Player.ResetGameState();
        }

        /// <summary>
        /// Set instrument and difficulty together.
        /// </summary>
        public void SetInstrumentAndDifficulty(int instrument, int difficulty)
        {
            Player.SetInstrumentAndDifficulty(instrument, difficulty);
        }

        #endregion
    }
}
