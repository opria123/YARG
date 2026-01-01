using System;
using UnityEngine;
using YARG.Player;
using YARG.Networking.Abstraction;

namespace YARG.Menu.Multiplayer
{
    /// <summary>
    /// Handles difficulty selection synchronization for multiplayer.
    /// Syncs player instrument and difficulty choices with the network.
    /// </summary>
    public class MultiplayerDifficultySync : MonoBehaviour
    {
        private LiteNetNetworkingAdapter _networkAdapter;

        /// <summary>
        /// Event fired when waiting for other players to make their selections.
        /// Parameter is the message to display.
        /// </summary>
        public event Action<string> OnWaitingForPlayers;

        private void Start()
        {
            _networkAdapter = NetworkingServiceFactory.Instance as LiteNetNetworkingAdapter;
        }

        /// <summary>
        /// Forces a refresh of the network state.
        /// </summary>
        public void ForceRefreshNetworkState()
        {
            // The LiteNet adapter maintains its own state; this is a no-op for now
            // Could be used to request a state sync from the host if needed
            Debug.Log("[MultiplayerDifficultySync] Network state refresh requested");
        }

        /// <summary>
        /// Syncs the player's profile when entering the difficulty select screen.
        /// </summary>
        public void SyncPlayerProfileOnEntry(YargPlayer player)
        {
            if (_networkAdapter == null || player?.Profile == null)
                return;

            // Update network state with current profile info
            var localNetworkPlayer = _networkAdapter.GetLocalPlayer();
            if (localNetworkPlayer != null)
            {
                localNetworkPlayer.Instrument = (int)player.Profile.CurrentInstrument;
                localNetworkPlayer.Difficulty = (int)player.Profile.CurrentDifficulty;
                
                // Broadcast the update
                _networkAdapter.BroadcastPlayerStateUpdate();
                
                Debug.Log($"[MultiplayerDifficultySync] Synced profile: {player.Profile.CurrentInstrument} @ {player.Profile.CurrentDifficulty}");
            }
        }

        /// <summary>
        /// Called when a player completes their selection.
        /// </summary>
        public void OnPlayerSelectionComplete(YargPlayer player)
        {
            if (_networkAdapter == null || player?.Profile == null)
                return;

            var localNetworkPlayer = _networkAdapter.GetLocalPlayer();
            if (localNetworkPlayer != null)
            {
                localNetworkPlayer.Instrument = (int)player.Profile.CurrentInstrument;
                localNetworkPlayer.Difficulty = (int)player.Profile.CurrentDifficulty;
                
                // Broadcast the update
                _networkAdapter.BroadcastPlayerStateUpdate();
                
                Debug.Log($"[MultiplayerDifficultySync] Player selection complete: {player.Profile.CurrentInstrument} @ {player.Profile.CurrentDifficulty}");
            }
        }

        /// <summary>
        /// Called when all local players are ready.
        /// </summary>
        public void OnAllLocalPlayersReady()
        {
            if (_networkAdapter == null)
                return;

            // Set ready state on network
            _networkAdapter.SetPlayerReady(true);
            
            Debug.Log("[MultiplayerDifficultySync] All local players ready");
            
            // Check if we need to wait for others
            if (!_networkAdapter.AreAllPlayersReady())
            {
                OnWaitingForPlayers?.Invoke("Waiting for other players to be ready...");
            }
        }
    }
}
