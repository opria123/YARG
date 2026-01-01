using System;
using System.Collections.Generic;
using UnityEngine;
using YARG.Core.Engine;
using YARG.Networking.Abstraction;

namespace YARG.Menu.Multiplayer
{
    /// <summary>
    /// Handles gameplay state synchronization for multiplayer.
    /// Sends local player snapshots to the network and processes incoming snapshots from remote players.
    /// </summary>
    public class MultiplayerGameplaySync : MonoBehaviour
    {
        private LiteNetNetworkingAdapter _networkAdapter;
        
        // Per-player rate limiting to ensure all local players get their snapshots sent
        private readonly Dictionary<Guid, float> _lastSnapshotTimeByPlayer = new Dictionary<Guid, float>();
        private const float SNAPSHOT_INTERVAL = 0.1f; // 10 Hz snapshot rate per player
        
        /// <summary>
        /// Event fired when waiting for other players (e.g., during loading or ready checks).
        /// Parameter is the status message to display.
        /// </summary>
        public event Action<string> OnWaitingForPlayers;

        private void Start()
        {
            _networkAdapter = NetworkingServiceFactory.Instance as LiteNetNetworkingAdapter;
            if (_networkAdapter == null)
            {
                Debug.LogWarning("[MultiplayerGameplaySync] No LiteNet adapter available - sync disabled");
            }
        }
        
        /// <summary>
        /// Forces a refresh of the current network state from the adapter.
        /// </summary>
        public void ForceRefreshNetworkState()
        {
            // Request latest state from the network adapter if available
            if (_networkAdapter != null && _networkAdapter.IsNetworkActive)
            {
                Debug.Log("[MultiplayerGameplaySync] Network state refresh requested");
            }
        }
        
        /// <summary>
        /// Notifies subscribers that we're waiting for other players.
        /// </summary>
        public void NotifyWaitingForPlayers(string message)
        {
            OnWaitingForPlayers?.Invoke(message);
        }

        /// <summary>
        /// Submit a local player snapshot to be sent to other players.
        /// </summary>
        public void SubmitLocalSnapshot(
            NetworkPlayerData playerData,
            int score,
            int combo,
            int maxCombo,
            bool isStarPowerActive,
            float starPowerAmount,
            int starPowerPhrasesHit,
            int totalStarPowerPhrases,
            int notesHit,
            int notesMissed,
            int overstrums,
            int hoposStrummed,
            int overhits,
            int ghostInputs,
            int ghostsHit,
            int accentsHit,
            int dynamicsBonus,
            int bandBonusScore,
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
            float stars,
            double songTime,
            double clientNetworkTime,
            float happiness = 1.0f,
            bool hasFailed = false,
            bool forceSend = false)
        {
            if (_networkAdapter == null || playerData == null)
                return;

            // Get unique identifier for this player to enable per-player rate limiting
            var playerId = playerData.NetworkPlayerId;
            
            // Per-player rate limiting - each player gets their own snapshot interval
            if (!forceSend)
            {
                if (_lastSnapshotTimeByPlayer.TryGetValue(playerId, out float lastTime) 
                    && Time.time - lastTime < SNAPSHOT_INTERVAL)
                {
                    return;
                }
            }

            _lastSnapshotTimeByPlayer[playerId] = Time.time;

            // Update the NetworkPlayerData with current stats
            playerData.Score = score;
            playerData.Combo = combo;
            playerData.MaxCombo = maxCombo;
            playerData.NotesHit = notesHit;
            playerData.NotesMissed = notesMissed;
            playerData.IsStarPowerActive = isStarPowerActive;
            playerData.StarPowerAmount = starPowerAmount;
            playerData.HasFailed = hasFailed;

            // Send the snapshot to all connected players, passing the specific player's name
            _networkAdapter.SendGameplaySnapshot(
                playerData.PlayerName,  // Pass the correct player's name
                score,
                combo,
                maxCombo,
                isStarPowerActive,
                starPowerAmount,
                starPowerPhrasesHit,
                totalStarPowerPhrases,
                notesHit,
                notesMissed,
                overstrums,
                hoposStrummed,
                overhits,
                ghostInputs,
                ghostsHit,
                accentsHit,
                dynamicsBonus,
                bandBonusScore,
                vocalsTicksHit,
                vocalsTicksMissed,
                vocalsPhraseTicksHit,
                vocalsPhraseTicksTotal,
                soloActive,
                soloSequence,
                soloNoteCount,
                soloNotesHit,
                soloLastBonus,
                soloTotalBonus,
                sustainsHeld,
                whammyValue,
                stars,
                songTime,
                happiness,
                hasFailed
            );
        }
    }
}
