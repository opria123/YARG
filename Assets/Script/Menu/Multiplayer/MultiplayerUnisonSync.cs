using System;
using System.Collections.Generic;
using UnityEngine;
using YARG.Core.Engine;
using YARG.Gameplay;
using YARG.Gameplay.Player;
using YARG.Networking;
using YARG.Networking.Abstraction;

namespace YARG.Menu.Multiplayer
{
    /// <summary>
    /// Handles synchronization of unison phrase completions and bonus awards in networked multiplayer.
    /// 
    /// In local play, the EngineManager handles unisons internally since all engines are in one process.
    /// In networked play, each client has its own EngineManager with only local engines, so we need to:
    /// 1. Send unison phrase completions to the host
    /// 2. Have the host track completions from all players
    /// 3. Broadcast bonus awards when all players complete a unison phrase
    /// </summary>
    public class MultiplayerUnisonSync : MonoBehaviour
    {
        private bool _isInitialized;
        private bool _isMultiplayer;
        private bool _useLiteNet;
        private LiteNetNetworkingAdapter _liteNetAdapter;
        
        // Track which unison phrases have been locally completed to avoid duplicate sends
        private HashSet<double> _locallyCompletedPhrases = new();
        
        // Track which bonus awards we've already processed to avoid duplicates
        private HashSet<double> _processedBonusAwards = new();
        
        // Reference to the local player's engine container (for awarding bonuses)
        private List<EngineManager.EngineContainer> _localEngineContainers = new();
        
        private void Start()
        {
            // Check if we're in multiplayer mode
            bool isLiteNetActive = NetworkingServiceFactory.Instance?.IsNetworkActive == true;
            bool isMirrorActive = !isLiteNetActive && 
                                  YargNetworkManager.Instance != null && 
                                  YargNetworkManager.Instance.isNetworkActive;
            
            if (!isLiteNetActive && !isMirrorActive)
            {
                _isMultiplayer = false;
                Debug.Log("[MultiplayerUnisonSync] Not in multiplayer mode, disabling");
                Destroy(this);
                return;
            }
            
            _isMultiplayer = true;
            _useLiteNet = isLiteNetActive;
            
            if (_useLiteNet)
            {
                _liteNetAdapter = NetworkingServiceFactory.Instance as LiteNetNetworkingAdapter;
                if (_liteNetAdapter != null)
                {
                    // Subscribe to unison bonus awards from the network
                    _liteNetAdapter.OnUnisonBonusAwarded += HandleUnisonBonusAwarded;
                    Debug.Log("[MultiplayerUnisonSync] Initialized for LiteNet multiplayer");
                }
                else
                {
                    Debug.LogError("[MultiplayerUnisonSync] LiteNet adapter is null!");
                    Destroy(this);
                    return;
                }
            }
            else
            {
                // TODO: Mirror support for unison sync
                Debug.LogWarning("[MultiplayerUnisonSync] Mirror unison sync not yet implemented");
            }
            
            _isInitialized = true;
        }
        
        private void OnDestroy()
        {
            if (_liteNetAdapter != null)
            {
                _liteNetAdapter.OnUnisonBonusAwarded -= HandleUnisonBonusAwarded;
                _liteNetAdapter.ResetUnisonTracking();
            }
            
            _localEngineContainers.Clear();
            _locallyCompletedPhrases.Clear();
            _processedBonusAwards.Clear();
        }
        
        /// <summary>
        /// Registers an engine container for unison bonus tracking.
        /// Should be called when players are initialized.
        /// </summary>
        public void RegisterEngineContainer(EngineManager.EngineContainer container)
        {
            if (container == null) return;
            
            if (!_localEngineContainers.Contains(container))
            {
                _localEngineContainers.Add(container);
                Debug.Log($"[MultiplayerUnisonSync] Registered engine container {container.EngineId}");
            }
        }
        
        /// <summary>
        /// Sets the total number of players participating in unisons.
        /// Should be called when gameplay starts.
        /// </summary>
        public void SetTotalPlayerCount(int count)
        {
            if (_liteNetAdapter != null)
            {
                _liteNetAdapter.SetUnisonPlayerCount(count);
            }
            
            // Clear tracking for new game
            _locallyCompletedPhrases.Clear();
            _processedBonusAwards.Clear();
            
            Debug.Log($"[MultiplayerUnisonSync] Total player count set to {count}");
        }
        
        /// <summary>
        /// Called when the local player completes a star power phrase that is part of a unison.
        /// </summary>
        /// <param name="phraseTime">The start time of the unison phrase</param>
        /// <param name="phraseEndTime">The end time of the unison phrase</param>
        public void OnLocalUnisonPhraseHit(double phraseTime, double phraseEndTime)
        {
            if (!_isInitialized || !_isMultiplayer)
                return;
            
            // Use rounded key for deduplication
            double phraseKey = Math.Round(phraseTime * 10) / 10;
            
            // Check if we've already sent this phrase
            if (_locallyCompletedPhrases.Contains(phraseKey))
            {
                Debug.Log($"[MultiplayerUnisonSync] Already sent phrase completion for {phraseKey:F3}");
                return;
            }
            
            _locallyCompletedPhrases.Add(phraseKey);
            
            Debug.Log($"[MultiplayerUnisonSync] Local unison phrase hit at {phraseTime:F3}");
            
            if (_useLiteNet && _liteNetAdapter != null)
            {
                _liteNetAdapter.SendUnisonPhraseHit(phraseTime, phraseEndTime);
            }
            // TODO: Mirror support
        }
        
        /// <summary>
        /// Handles the network event when a unison bonus should be awarded.
        /// </summary>
        private void HandleUnisonBonusAwarded(double phraseTime)
        {
            // Use rounded key for deduplication
            double phraseKey = Math.Round(phraseTime * 10) / 10;
            
            // Check if we've already processed this bonus
            if (_processedBonusAwards.Contains(phraseKey))
            {
                Debug.Log($"[MultiplayerUnisonSync] Already processed bonus for phrase {phraseKey:F3}");
                return;
            }
            
            _processedBonusAwards.Add(phraseKey);
            
            Debug.Log($"[MultiplayerUnisonSync] Awarding unison bonus for phrase at {phraseTime:F3} to {_localEngineContainers.Count} local engines");
            
            // Award bonus to all local engine containers
            foreach (var container in _localEngineContainers)
            {
                try
                {
                    // Directly award the bonus through the engine
                    container.Engine.AwardUnisonBonus();
                    Debug.Log($"[MultiplayerUnisonSync] Awarded unison bonus to engine {container.EngineId}");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[MultiplayerUnisonSync] Failed to award unison bonus to engine {container.EngineId}: {ex.Message}");
                }
            }
        }
        
        /// <summary>
        /// Resets the sync state. Called when practice section resets or song restarts.
        /// </summary>
        public void Reset()
        {
            _locallyCompletedPhrases.Clear();
            _processedBonusAwards.Clear();
            
            if (_liteNetAdapter != null)
            {
                _liteNetAdapter.ResetUnisonTracking();
            }
            
            Debug.Log("[MultiplayerUnisonSync] Reset unison tracking state");
        }
    }
}
