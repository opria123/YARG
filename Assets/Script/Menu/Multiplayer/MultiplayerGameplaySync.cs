using System;
using System.Collections.Generic;
using Mirror;
using UnityEngine;
using YARG.Networking;
using YARG.Networking.Abstraction;

namespace YARG.Menu.Multiplayer
{
    /// <summary>
    /// Handles local-authority gameplay snapshot replication for multiplayer sessions.
    /// </summary>
    public class MultiplayerGameplaySync : MonoBehaviour
    {
        private const double MIN_CHANGED_SNAPSHOT_INTERVAL = 0.02d; // ~50 Hz when state changes
        private const double MAX_UNCHANGED_SNAPSHOT_INTERVAL = 0.20d; // 5 Hz keep-alive when idle
        private const float STAR_POWER_DELTA_EPSILON = 0.0025f;
        private const float VOCAL_PHRASE_DELTA_EPSILON = 0.25f;

        private readonly struct GameplaySnapshot
        {
            public readonly int Score;
            public readonly int Combo;
            public readonly int Streak;
            public readonly bool StarPowerActive;
            public readonly float StarPowerAmount;
            public readonly int StarPowerPhrasesHit;
            public readonly int TotalStarPowerPhrases;
            public readonly int NotesHit;
            public readonly int NotesMissed;
            public readonly int Overstrums;
            public readonly int HoposStrummed;
            public readonly int Overhits;
            public readonly int GhostInputs;
            public readonly int GhostsHit;
            public readonly int AccentsHit;
            public readonly int DynamicsBonus;
            public readonly int BandBonusScore;
            public readonly int VocalsTicksHit;
            public readonly int VocalsTicksMissed;
            public readonly float VocalsPhraseTicksHit;
            public readonly int VocalsPhraseTicksTotal;
            public readonly bool SoloActive;
            public readonly int SoloSequence;
            public readonly int SoloNoteCount;
            public readonly int SoloNotesHit;
            public readonly int SoloLastBonus;
            public readonly int SoloTotalBonus;
            public readonly int SustainsHeld;
            public readonly float WhammyValue;
            public readonly double SongTime;
            public readonly double ClientNetworkTime;

            public GameplaySnapshot(int score, int combo, int streak, bool starPowerActive, float starPowerAmount,
                int starPowerPhrasesHit, int totalStarPowerPhrases, int notesHit, int notesMissed, int overstrums,
                int hoposStrummed, int overhits, int ghostInputs, int ghostsHit, int accentsHit, int dynamicsBonus,
                int bandBonusScore, int vocalsTicksHit, int vocalsTicksMissed, float vocalsPhraseTicksHit,
                int vocalsPhraseTicksTotal, bool soloActive, int soloSequence, int soloNoteCount, int soloNotesHit,
                int soloLastBonus, int soloTotalBonus, int sustainsHeld, float whammyValue, double songTime, double clientNetworkTime)
            {
                Score = score;
                Combo = combo;
                Streak = streak;
                StarPowerActive = starPowerActive;
                StarPowerAmount = starPowerAmount;
                StarPowerPhrasesHit = starPowerPhrasesHit;
                TotalStarPowerPhrases = totalStarPowerPhrases;
                NotesHit = notesHit;
                NotesMissed = notesMissed;
                Overstrums = overstrums;
                HoposStrummed = hoposStrummed;
                Overhits = overhits;
                GhostInputs = ghostInputs;
                GhostsHit = ghostsHit;
                AccentsHit = accentsHit;
                DynamicsBonus = dynamicsBonus;
                BandBonusScore = bandBonusScore;
                VocalsTicksHit = vocalsTicksHit;
                VocalsTicksMissed = vocalsTicksMissed;
                VocalsPhraseTicksHit = vocalsPhraseTicksHit;
                VocalsPhraseTicksTotal = vocalsPhraseTicksTotal;
                SoloActive = soloActive;
                SoloSequence = soloSequence;
                SoloNoteCount = soloNoteCount;
                SoloNotesHit = soloNotesHit;
                SoloLastBonus = soloLastBonus;
                SoloTotalBonus = soloTotalBonus;
                SustainsHeld = sustainsHeld;
                WhammyValue = whammyValue;
                SongTime = songTime;
                ClientNetworkTime = clientNetworkTime;
            }

            public bool DiffersFrom(in GameplaySnapshot other)
            {
                if (Score != other.Score || Combo != other.Combo || Streak != other.Streak)
                {
                    return true;
                }

                if (StarPowerActive != other.StarPowerActive)
                {
                    return true;
                }

                if (Mathf.Abs(StarPowerAmount - other.StarPowerAmount) > STAR_POWER_DELTA_EPSILON)
                {
                    return true;
                }

                if (StarPowerPhrasesHit != other.StarPowerPhrasesHit ||
                    TotalStarPowerPhrases != other.TotalStarPowerPhrases)
                {
                    return true;
                }

                if (NotesHit != other.NotesHit || NotesMissed != other.NotesMissed)
                {
                    return true;
                }

                if (Overstrums != other.Overstrums || HoposStrummed != other.HoposStrummed)
                {
                    return true;
                }

                if (Overhits != other.Overhits)
                {
                    return true;
                }

                if (GhostInputs != other.GhostInputs || GhostsHit != other.GhostsHit)
                {
                    return true;
                }

                if (AccentsHit != other.AccentsHit || DynamicsBonus != other.DynamicsBonus)
                {
                    return true;
                }

                if (BandBonusScore != other.BandBonusScore)
                {
                    return true;
                }

                if (VocalsTicksHit != other.VocalsTicksHit || VocalsTicksMissed != other.VocalsTicksMissed)
                {
                    return true;
                }

                if (VocalsPhraseTicksTotal != other.VocalsPhraseTicksTotal)
                {
                    return true;
                }

                if (Mathf.Abs(VocalsPhraseTicksHit - other.VocalsPhraseTicksHit) > VOCAL_PHRASE_DELTA_EPSILON)
                {
                    return true;
                }

                if (SoloActive != other.SoloActive || SoloSequence != other.SoloSequence)
                {
                    return true;
                }

                if (SoloNoteCount != other.SoloNoteCount || SoloNotesHit != other.SoloNotesHit)
                {
                    return true;
                }

                if (SoloLastBonus != other.SoloLastBonus || SoloTotalBonus != other.SoloTotalBonus)
                {
                    return true;
                }

                // Sustain state changed
                if (SustainsHeld != other.SustainsHeld)
                {
                    return true;
                }

                // Whammy value changed (use small epsilon to avoid sending for tiny fluctuations)
                if (Mathf.Abs(WhammyValue - other.WhammyValue) > 0.05f)
                {
                    return true;
                }

                return false;
            }
        }

        private bool _isMultiplayer;
        private bool _useLiteNet;
        private bool _firstSnapshotLogged;
        private int _submitCallCount; // Debug counter
        private readonly Dictionary<NetworkPlayerData, SnapshotState> _snapshotStates = new();
        private SnapshotState _liteNetFallbackState; // Used when NetworkPlayerData is null for LiteNet

        private sealed class SnapshotState
        {
            public bool HasLastSnapshot;
            public GameplaySnapshot LastSnapshot;
            public uint Sequence;
        }

        private void Start()
        {
            // Check if we're in multiplayer mode (either Mirror or LiteNet)
            // NOTE: Check LiteNet first - if LiteNet is active, consider Mirror inactive
            // This prevents false positives from Mirror singleton existing in scene
            bool isLiteNetActive = NetworkingServiceFactory.Instance?.IsNetworkActive == true;
            // Only consider Mirror active if LiteNet is NOT active
            bool isMirrorActive = !isLiteNetActive && 
                                  YargNetworkManager.Instance != null && 
                                  YargNetworkManager.Instance.isNetworkActive;
            
            if (!isMirrorActive && !isLiteNetActive)
            {
                _isMultiplayer = false;
                Destroy(this);
                return;
            }

            _isMultiplayer = true;
            _useLiteNet = isLiteNetActive; // If LiteNet is active, use it (Mirror check now excludes LiteNet)

            if (_useLiteNet)
            {
                // For LiteNet, we don't need to validate NetworkPlayerData here since
                // the adapter manages players differently
                Debug.Log("[MultiplayerGameplaySync] Initialized for LiteNet - using local authority gameplay snapshots");
                ResetSnapshotCache();
                return;
            }

            // Mirror path - validate players
            var allPlayers = YargNetworkManager.Instance.GetAllPlayers();
            bool foundLocalPlayer = false;

            foreach (var playerData in allPlayers)
            {
                if (playerData != null && playerData.IsLocalUser)
                {
                    playerData.CmdResetGameState();
                    foundLocalPlayer = true;
                }
            }

            if (!foundLocalPlayer)
            {
                Debug.LogWarning("[MultiplayerGameplaySync] Could not find local NetworkPlayerData");
                Destroy(this);
                return;
            }

            ResetSnapshotCache();

            Debug.Log("[MultiplayerGameplaySync] Initialized (Mirror) - using local authority gameplay snapshots");
        }

        private void ResetSnapshotCache()
        {
            _snapshotStates.Clear();
        }

        private SnapshotState GetOrCreateState(NetworkPlayerData networkPlayerData)
        {
            if (!_snapshotStates.TryGetValue(networkPlayerData, out var state))
            {
                state = new SnapshotState();
                _snapshotStates[networkPlayerData] = state;
            }

            return state;
        }

        /// <summary>
        /// Submit the current local gameplay state for replication to other clients.
        /// The caller is responsible for providing song/network timestamps in the same frame.
        /// </summary>
        public void SubmitLocalSnapshot(NetworkPlayerData networkPlayerData, int score, int combo, int streak, bool starPowerActive, float starPowerAmount,
            int starPowerPhrasesHit, int totalStarPowerPhrases, int notesHit, int notesMissed, int overstrums,
            int hoposStrummed, int overhits, int ghostInputs, int ghostsHit, int accentsHit, int dynamicsBonus,
            int bandBonusScore, int vocalsTicksHit, int vocalsTicksMissed, float vocalsPhraseTicksHit,
            int vocalsPhraseTicksTotal, bool soloActive, int soloSequence, int soloNoteCount, int soloNotesHit,
            int soloLastBonus, int soloTotalBonus, int sustainsHeld, float whammyValue, double songTime, double clientNetworkTime, bool forceSend = false)
        {
            _submitCallCount++;
            
            // First-call debug and periodic trace
            if (!_firstSnapshotLogged)
            {
                Debug.Log($"[MultiplayerGameplaySync] SubmitLocalSnapshot called first time - _isMultiplayer={_isMultiplayer}, _useLiteNet={_useLiteNet}, networkPlayerData={(networkPlayerData != null ? networkPlayerData.PlayerName : "null")}");
                _firstSnapshotLogged = true;
            }
            else if (_submitCallCount % 300 == 0)
            {
                // Log every 300 calls (~5 seconds at 60fps)
                Debug.Log($"[MultiplayerGameplaySync] SubmitLocalSnapshot call #{_submitCallCount} - score={score}, combo={combo}, notesHit={notesHit}, songTime={songTime:F2}");
            }
            
            if (!_isMultiplayer)
            {
                return;
            }
            
            // For LiteNet, we don't require NetworkPlayerData - we just send the snapshot
            // For Mirror, we need valid NetworkPlayerData with IsLocalUser
            bool skipDueToNetworkData = false;
            if (_useLiteNet)
            {
                // Continue with LiteNet path - networkPlayerData might be null and that's OK
            }
            else if (networkPlayerData == null || !networkPlayerData.IsLocalUser)
            {
                skipDueToNetworkData = true;
            }
            
            if (skipDueToNetworkData)
            {
                return;
            }

            // Get snapshot state - use fallback for LiteNet when NetworkPlayerData is null
            SnapshotState state;
            if (_useLiteNet && networkPlayerData == null)
            {
                if (_liteNetFallbackState == null)
                {
                    _liteNetFallbackState = new SnapshotState();
                }
                state = _liteNetFallbackState;
            }
            else
            {
                state = GetOrCreateState(networkPlayerData);
            }

            int sanitizedScore = Math.Max(0, score);
            int sanitizedCombo = Math.Max(0, combo);
            int sanitizedStreak = Math.Max(0, streak);
            int sanitizedNotesHit = Math.Max(0, notesHit);
            int sanitizedNotesMissed = Math.Max(0, notesMissed);
            int sanitizedOverstrums = Math.Max(0, overstrums);
            int sanitizedHoposStrummed = Math.Max(0, hoposStrummed);
            int sanitizedOverhits = Math.Max(0, overhits);
            int sanitizedGhostInputs = Math.Max(0, ghostInputs);
            int sanitizedGhostsHit = Math.Max(0, ghostsHit);
            int sanitizedAccentsHit = Math.Max(0, accentsHit);
            int sanitizedDynamicsBonus = Math.Max(0, dynamicsBonus);
            int sanitizedBandBonusScore = Math.Max(0, bandBonusScore);
            int sanitizedVocalsTicksHit = Math.Max(0, vocalsTicksHit);
            int sanitizedVocalsTicksMissed = Math.Max(0, vocalsTicksMissed);
            float sanitizedVocalsPhraseTicksHit = Mathf.Max(0f, vocalsPhraseTicksHit);
            int sanitizedVocalsPhraseTicksTotal = Math.Max(0, vocalsPhraseTicksTotal);
            int sanitizedTotalStarPowerPhrases = Math.Max(0, totalStarPowerPhrases);
            int sanitizedStarPowerPhrasesHit = Math.Max(0, starPowerPhrasesHit);
            if (sanitizedTotalStarPowerPhrases > 0)
            {
                sanitizedStarPowerPhrasesHit = Math.Min(sanitizedStarPowerPhrasesHit, sanitizedTotalStarPowerPhrases);
            }
            int sanitizedSoloSequence = soloSequence;
            int sanitizedSoloNoteCount = Math.Max(0, soloNoteCount);
            int sanitizedSoloNotesHit = Mathf.Clamp(soloNotesHit, 0, sanitizedSoloNoteCount == 0 ? 0 : sanitizedSoloNoteCount);
            int sanitizedSoloLastBonus = Math.Max(0, soloLastBonus);
            int sanitizedSoloTotalBonus = Math.Max(0, soloTotalBonus);

            // Ensure values stay monotonic so the server doesn't reject snapshots due to engine bookkeeping churn.
            if (state.HasLastSnapshot)
            {
                var lastSnapshot = state.LastSnapshot;

                sanitizedScore = Math.Max(sanitizedScore, lastSnapshot.Score);
                sanitizedStreak = Math.Max(sanitizedStreak, lastSnapshot.Streak);
                sanitizedNotesHit = Math.Max(sanitizedNotesHit, lastSnapshot.NotesHit);
                sanitizedNotesMissed = Math.Max(sanitizedNotesMissed, lastSnapshot.NotesMissed);
                sanitizedOverstrums = Math.Max(sanitizedOverstrums, lastSnapshot.Overstrums);
                sanitizedHoposStrummed = Math.Max(sanitizedHoposStrummed, lastSnapshot.HoposStrummed);
                sanitizedOverhits = Math.Max(sanitizedOverhits, lastSnapshot.Overhits);
                sanitizedGhostInputs = Math.Max(sanitizedGhostInputs, lastSnapshot.GhostInputs);
                sanitizedGhostsHit = Math.Max(sanitizedGhostsHit, lastSnapshot.GhostsHit);
                sanitizedAccentsHit = Math.Max(sanitizedAccentsHit, lastSnapshot.AccentsHit);
                sanitizedDynamicsBonus = Math.Max(sanitizedDynamicsBonus, lastSnapshot.DynamicsBonus);
                sanitizedBandBonusScore = Math.Max(sanitizedBandBonusScore, lastSnapshot.BandBonusScore);
                sanitizedVocalsTicksHit = Math.Max(sanitizedVocalsTicksHit, lastSnapshot.VocalsTicksHit);
                sanitizedVocalsTicksMissed = Math.Max(sanitizedVocalsTicksMissed, lastSnapshot.VocalsTicksMissed);
                sanitizedStarPowerPhrasesHit = Math.Max(sanitizedStarPowerPhrasesHit, lastSnapshot.StarPowerPhrasesHit);
                sanitizedTotalStarPowerPhrases = Math.Max(sanitizedTotalStarPowerPhrases, lastSnapshot.TotalStarPowerPhrases);
                sanitizedSoloTotalBonus = Math.Max(sanitizedSoloTotalBonus, lastSnapshot.SoloTotalBonus);

                if (sanitizedSoloSequence == lastSnapshot.SoloSequence)
                {
                    sanitizedSoloNotesHit = Math.Max(sanitizedSoloNotesHit, lastSnapshot.SoloNotesHit);
                    sanitizedSoloNoteCount = Math.Max(sanitizedSoloNoteCount, lastSnapshot.SoloNoteCount);
                }
            }

            if (sanitizedVocalsPhraseTicksTotal > 0)
            {
                sanitizedVocalsPhraseTicksHit = Mathf.Min(sanitizedVocalsPhraseTicksHit, sanitizedVocalsPhraseTicksTotal);
            }
            else
            {
                sanitizedVocalsPhraseTicksHit = 0f;
            }

            float clampedStarPower = Mathf.Clamp01(starPowerAmount);
            float clampedWhammy = Mathf.Clamp01(whammyValue);

            var snapshot = new GameplaySnapshot(sanitizedScore, sanitizedCombo, sanitizedStreak, starPowerActive,
                clampedStarPower, sanitizedStarPowerPhrasesHit, sanitizedTotalStarPowerPhrases, sanitizedNotesHit,
                sanitizedNotesMissed, sanitizedOverstrums, sanitizedHoposStrummed, sanitizedOverhits,
                sanitizedGhostInputs, sanitizedGhostsHit, sanitizedAccentsHit, sanitizedDynamicsBonus,
                sanitizedBandBonusScore, sanitizedVocalsTicksHit, sanitizedVocalsTicksMissed,
                sanitizedVocalsPhraseTicksHit, sanitizedVocalsPhraseTicksTotal, soloActive, sanitizedSoloSequence,
                sanitizedSoloNoteCount, sanitizedSoloNotesHit, sanitizedSoloLastBonus, sanitizedSoloTotalBonus,
                sustainsHeld, clampedWhammy, songTime, clientNetworkTime);

            bool shouldSend = forceSend || !state.HasLastSnapshot;
            string rateLimitReason = "";

            if (!shouldSend && state.HasLastSnapshot)
            {
                double elapsed = snapshot.ClientNetworkTime - state.LastSnapshot.ClientNetworkTime;
                bool changed = snapshot.DiffersFrom(state.LastSnapshot);

                if (changed)
                {
                    shouldSend = elapsed >= MIN_CHANGED_SNAPSHOT_INTERVAL;
                    if (!shouldSend) rateLimitReason = $"changed but elapsed={elapsed:F3}<{MIN_CHANGED_SNAPSHOT_INTERVAL}";
                }
                else
                {
                    shouldSend = elapsed >= MAX_UNCHANGED_SNAPSHOT_INTERVAL;
                    if (!shouldSend) rateLimitReason = $"unchanged elapsed={elapsed:F3}<{MAX_UNCHANGED_SNAPSHOT_INTERVAL}";
                }
            }

            if (!shouldSend)
            {
                // Log occasionally when rate limited
                if (_submitCallCount % 600 == 0 && !string.IsNullOrEmpty(rateLimitReason))
                {
                    Debug.Log($"[MultiplayerGameplaySync] Snapshot rate-limited (call #{_submitCallCount}): {rateLimitReason}");
                }
                return;
            }

            state.Sequence++;
            
            // Send via LiteNet if active, otherwise use Mirror
            if (_useLiteNet)
            {
                var liteNetAdapter = NetworkingServiceFactory.Instance as LiteNetNetworkingAdapter;
                if (liteNetAdapter != null)
                {
                    // Debug log first snapshot and then periodically
                    if (state.Sequence == 1 || state.Sequence % 60 == 0)
                    {
                        Debug.Log($"[MultiplayerGameplaySync] Sending LiteNet snapshot #{state.Sequence} (score={snapshot.Score}, combo={snapshot.Combo}, notesHit={snapshot.NotesHit}, songTime={snapshot.SongTime:F2})");
                    }
                    
                    liteNetAdapter.SendGameplaySnapshot(
                        snapshot.Score, snapshot.Combo, snapshot.Streak,
                        snapshot.StarPowerActive, snapshot.StarPowerAmount,
                        snapshot.StarPowerPhrasesHit, snapshot.TotalStarPowerPhrases,
                        snapshot.NotesHit, snapshot.NotesMissed,
                        snapshot.Overstrums, snapshot.HoposStrummed, snapshot.Overhits, snapshot.GhostInputs,
                        snapshot.GhostsHit, snapshot.AccentsHit, snapshot.DynamicsBonus, snapshot.BandBonusScore,
                        snapshot.VocalsTicksHit, snapshot.VocalsTicksMissed,
                        snapshot.VocalsPhraseTicksHit, snapshot.VocalsPhraseTicksTotal,
                        snapshot.SoloActive, snapshot.SoloSequence, snapshot.SoloNoteCount, snapshot.SoloNotesHit,
                        snapshot.SoloLastBonus, snapshot.SoloTotalBonus,
                        snapshot.SustainsHeld, snapshot.WhammyValue,
                        snapshot.SongTime);
                }
                else
                {
                    Debug.LogWarning("[MultiplayerGameplaySync] LiteNet adapter is null!");
                }
            }
            else
            {
                networkPlayerData.CmdSubmitGameplaySnapshot(snapshot.Score, snapshot.Combo, snapshot.Streak,
                    snapshot.StarPowerActive, snapshot.StarPowerAmount, snapshot.StarPowerPhrasesHit,
                    snapshot.TotalStarPowerPhrases, snapshot.NotesHit, snapshot.NotesMissed, snapshot.Overstrums,
                    snapshot.HoposStrummed, snapshot.Overhits, snapshot.GhostInputs, snapshot.GhostsHit,
                    snapshot.AccentsHit, snapshot.DynamicsBonus, snapshot.BandBonusScore, snapshot.VocalsTicksHit,
                    snapshot.VocalsTicksMissed, snapshot.VocalsPhraseTicksHit, snapshot.VocalsPhraseTicksTotal,
                    snapshot.SoloActive, snapshot.SoloSequence, snapshot.SoloNoteCount, snapshot.SoloNotesHit,
                    snapshot.SoloLastBonus, snapshot.SoloTotalBonus, snapshot.SongTime, snapshot.ClientNetworkTime,
                    state.Sequence);
            }

            state.LastSnapshot = snapshot;
            state.HasLastSnapshot = true;
        }
        
        private void OnDestroy()
        {
            ResetSnapshotCache();
        }
    }
}
