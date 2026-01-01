using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using YARG.Net.Packets;
using YARG.Net.Transport;

namespace YARG.Networking.Abstraction.Handlers
{
    /// <summary>
    /// Handles gameplay state synchronization (score, combo, star power, etc.) for multiplayer.
    /// Builds and parses gameplay snapshot packets using YARG.Net.
    /// </summary>
    public class GameplayStateHandler
    {
        // Cached reflection info for NetworkPlayerData fields
        private readonly Type _playerDataType;
        private readonly BindingFlags _bindingFlags;
        private readonly Dictionary<string, FieldInfo> _fieldCache = new();
        
        // Local snapshot tracking
        private uint _localSnapshotSequence;
        
        /// <summary>
        /// Event fired when a snapshot is received from a remote player.
        /// Parameters: playerName, snapshot
        /// </summary>
        public event Action<string, GameplaySnapshot> OnSnapshotReceived;
        
        public GameplayStateHandler()
        {
            _playerDataType = typeof(NetworkPlayerData);
            _bindingFlags = BindingFlags.NonPublic | BindingFlags.Instance;
            
            // Pre-cache commonly used fields
            CacheField("lastGameplaySnapshotSequence");
            CacheField("currentScore");
            CacheField("currentCombo");
            CacheField("currentStreak");
            CacheField("isStarPowerActive");
            CacheField("starPowerAmount");
            CacheField("starPowerPhrasesHit");
            CacheField("totalStarPowerPhrases");
            CacheField("notesHit");
            CacheField("notesMissed");
            CacheField("overstrums");
            CacheField("hoposStrummed");
            CacheField("overhits");
            CacheField("ghostInputs");
            CacheField("ghostsHit");
            CacheField("accentsHit");
            CacheField("dynamicsBonus");
            CacheField("bandBonusScore");
            CacheField("vocalsTicksHit");
            CacheField("vocalsTicksMissed");
            CacheField("vocalsPhraseTicksHit");
            CacheField("vocalsPhraseTicksTotal");
            CacheField("soloActive");
            CacheField("soloSequence");
            CacheField("soloNoteCount");
            CacheField("soloNotesHit");
            CacheField("soloLastBonus");
            CacheField("soloTotalBonus");
            CacheField("sustainsHeld");
            CacheField("whammyValue");
            CacheField("stars");
            CacheField("lastGameplaySongTime");
            CacheField("lastGameplayNetworkTime");
            
            // Happiness and fail state (for Star Power revival system)
            CacheField("happiness");
            CacheField("hasFailed");
        }
        
        private void CacheField(string fieldName)
        {
            var field = _playerDataType.GetField(fieldName, _bindingFlags);
            if (field != null)
            {
                _fieldCache[fieldName] = field;
                UnityEngine.Debug.Log($"[GameplayStateHandler] Cached field '{fieldName}' successfully (type: {field.FieldType})");
            }
            else
            {
                UnityEngine.Debug.LogError($"[GameplayStateHandler] FAILED to cache field '{fieldName}' - field not found in {_playerDataType.Name}");
            }
        }
        
        #region Public API
        
        /// <summary>
        /// Gets the current snapshot sequence number.
        /// </summary>
        public uint CurrentSequence => _localSnapshotSequence;
        
        /// <summary>
        /// Increments and returns the next sequence number.
        /// </summary>
        public uint NextSequence() => ++_localSnapshotSequence;
        
        /// <summary>
        /// Resets the snapshot sequence (call when gameplay starts).
        /// </summary>
        public void ResetSequence()
        {
            _localSnapshotSequence = 0;
        }
        
        /// <summary>
        /// Whether to log this sequence number (for periodic logging).
        /// </summary>
        public bool ShouldLogSequence(uint sequence)
        {
            return sequence == 1 || sequence % 60 == 0;
        }
        
        #endregion
        
        #region Message Building
        
        /// <summary>
        /// Builds a gameplay snapshot packet using YARG.Net.
        /// </summary>
        public byte[] BuildSnapshotPacket(
            string playerName,
            int score, int combo, int streak,
            bool starPowerActive, float starPowerAmount, int starPowerPhrasesHit, int totalStarPowerPhrases,
            int notesHit, int notesMissed,
            int overstrums, int hoposStrummed, int overhits, int ghostInputs,
            int ghostsHit, int accentsHit, int dynamicsBonus,
            int bandBonusScore,
            int vocalsTicksHit, int vocalsTicksMissed, float vocalsPhraseTicksHit, int vocalsPhraseTicksTotal,
            bool soloActive, int soloSequence, int soloNoteCount, int soloNotesHit, int soloLastBonus, int soloTotalBonus,
            int sustainsHeld, float whammyValue,
            float stars,
            double songTime,
            float happiness = 1.0f, bool hasFailed = false)
        {
            var snapshot = new GameplaySnapshot
            {
                Sequence = _localSnapshotSequence,
                Score = score,
                Combo = combo,
                Streak = streak,
                StarPowerActive = starPowerActive,
                StarPowerAmount = starPowerAmount,
                StarPowerPhrasesHit = starPowerPhrasesHit,
                TotalStarPowerPhrases = totalStarPowerPhrases,
                NotesHit = notesHit,
                NotesMissed = notesMissed,
                Overstrums = overstrums,
                HoposStrummed = hoposStrummed,
                Overhits = overhits,
                GhostInputs = ghostInputs,
                GhostsHit = ghostsHit,
                AccentsHit = accentsHit,
                DynamicsBonus = dynamicsBonus,
                BandBonusScore = bandBonusScore,
                VocalsTicksHit = vocalsTicksHit,
                VocalsTicksMissed = vocalsTicksMissed,
                VocalsPhraseTicksHit = vocalsPhraseTicksHit,
                VocalsPhraseTicksTotal = vocalsPhraseTicksTotal,
                SoloActive = soloActive,
                SoloSequence = soloSequence,
                SoloNoteCount = soloNoteCount,
                SoloNotesHit = soloNotesHit,
                SoloLastBonus = soloLastBonus,
                SoloTotalBonus = soloTotalBonus,
                SustainsHeld = sustainsHeld,
                WhammyValue = whammyValue,
                Stars = stars,
                SongTime = songTime,
                Happiness = happiness,
                HasFailed = hasFailed,
                PlayerName = playerName
            };
            
            return GameplayStateBinaryPackets.BuildSnapshotPacket(in snapshot);
        }
        
        #endregion
        
        #region Message Parsing
        
        /// <summary>
        /// Parses a gameplay snapshot packet.
        /// Returns the parsed snapshot if valid, null otherwise.
        /// </summary>
        public GameplaySnapshot? ParseSnapshotPacket(ReadOnlySpan<byte> payload)
        {
            var parsed = GameplayStateBinaryPackets.ParseSnapshotPacket(payload);
            if (!parsed.IsValid)
                return null;
            
            return parsed.Snapshot;
        }
        
        #endregion
        
        #region Player Data Application
        
        /// <summary>
        /// Applies a snapshot to a NetworkPlayerData instance using reflection.
        /// Returns true if the snapshot was applied (not stale).
        /// </summary>
        public bool ApplySnapshotToPlayer(NetworkPlayerData player, in GameplaySnapshot snapshot, double networkTime)
        {
            if (player == null)
                return false;
            
            // Log incoming snapshot fail state
            if (snapshot.HasFailed)
            {
                UnityEngine.Debug.Log($"[GameplayStateHandler] ApplySnapshotToPlayer: Received snapshot for '{snapshot.PlayerName}' with HasFailed=TRUE, Happiness={snapshot.Happiness:F2}, Seq={snapshot.Sequence}");
            }
            
            // Check sequence to avoid applying old snapshots
            if (_fieldCache.TryGetValue("lastGameplaySnapshotSequence", out var seqField))
            {
                uint lastSeq = (uint)seqField.GetValue(player);
                if (snapshot.Sequence <= lastSeq)
                    return false; // Old snapshot, ignore
                seqField.SetValue(player, snapshot.Sequence);
            }
            
            // Apply all fields
            SetField(player, "currentScore", snapshot.Score);
            SetField(player, "currentCombo", snapshot.Combo);
            SetField(player, "currentStreak", snapshot.Streak);
            SetField(player, "isStarPowerActive", snapshot.StarPowerActive);
            SetField(player, "starPowerAmount", snapshot.StarPowerAmount);
            SetField(player, "starPowerPhrasesHit", snapshot.StarPowerPhrasesHit);
            SetField(player, "totalStarPowerPhrases", snapshot.TotalStarPowerPhrases);
            SetField(player, "notesHit", snapshot.NotesHit);
            SetField(player, "notesMissed", snapshot.NotesMissed);
            SetField(player, "overstrums", snapshot.Overstrums);
            SetField(player, "hoposStrummed", snapshot.HoposStrummed);
            SetField(player, "overhits", snapshot.Overhits);
            SetField(player, "ghostInputs", snapshot.GhostInputs);
            SetField(player, "ghostsHit", snapshot.GhostsHit);
            SetField(player, "accentsHit", snapshot.AccentsHit);
            SetField(player, "dynamicsBonus", snapshot.DynamicsBonus);
            SetField(player, "bandBonusScore", snapshot.BandBonusScore);
            SetField(player, "vocalsTicksHit", snapshot.VocalsTicksHit);
            SetField(player, "vocalsTicksMissed", snapshot.VocalsTicksMissed);
            SetField(player, "vocalsPhraseTicksHit", snapshot.VocalsPhraseTicksHit);
            SetField(player, "vocalsPhraseTicksTotal", snapshot.VocalsPhraseTicksTotal);
            SetField(player, "soloActive", snapshot.SoloActive);
            SetField(player, "soloSequence", snapshot.SoloSequence);
            SetField(player, "soloNoteCount", snapshot.SoloNoteCount);
            SetField(player, "soloNotesHit", snapshot.SoloNotesHit);
            SetField(player, "soloLastBonus", snapshot.SoloLastBonus);
            SetField(player, "soloTotalBonus", snapshot.SoloTotalBonus);
            SetField(player, "sustainsHeld", snapshot.SustainsHeld);
            SetField(player, "whammyValue", snapshot.WhammyValue);
            SetField(player, "stars", snapshot.Stars);
            SetField(player, "lastGameplaySongTime", snapshot.SongTime);
            SetField(player, "lastGameplayNetworkTime", networkTime);
            
            // Happiness and fail state - synced from authoritative client
            SetFieldWithLog(player, "happiness", snapshot.Happiness, snapshot.PlayerName);
            SetFieldWithLog(player, "hasFailed", snapshot.HasFailed, snapshot.PlayerName);
            
            OnSnapshotReceived?.Invoke(snapshot.PlayerName, snapshot);
            return true;
        }
        
        private void SetField(NetworkPlayerData player, string fieldName, object value)
        {
            if (_fieldCache.TryGetValue(fieldName, out var field))
            {
                field.SetValue(player, value);
            }
        }
        
        private void SetFieldWithLog(NetworkPlayerData player, string fieldName, object value, string playerName)
        {
            if (_fieldCache.TryGetValue(fieldName, out var field))
            {
                var oldValue = field.GetValue(player);
                field.SetValue(player, value);
                
                // Only log when value changes or on fail state
                bool shouldLog = !Equals(oldValue, value) || (fieldName == "hasFailed" && value is bool b && b);
                if (shouldLog)
                {
                    UnityEngine.Debug.Log($"[GameplayStateHandler] Set {fieldName} for '{playerName}': {oldValue} -> {value}");
                }
            }
            else
            {
                UnityEngine.Debug.LogError($"[GameplayStateHandler] Field '{fieldName}' not found in cache! Cannot set value.");
            }
        }
        
        #endregion
    }
}
