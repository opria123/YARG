using System;
using System.Collections.Generic;
using UnityEngine;
using YARG.Core.Engine;
using YARG.Networking.Abstraction;

namespace YARG.Menu.Multiplayer
{
    /// <summary>
    /// Handles unison phrase synchronization for multiplayer.
    /// Coordinates with the network layer to determine when all players in a band have hit a unison phrase.
    /// Supports per-band unison tracking.
    /// </summary>
    public class MultiplayerUnisonSync : MonoBehaviour
    {
        private LiteNetNetworkingAdapter _networkAdapter;
        private int _totalPlayerCount;
        private readonly List<EngineManager.EngineContainer> _localEngineContainers = new();
        
        // Map of engine container to its band ID
        private readonly Dictionary<EngineManager.EngineContainer, int> _containerBandMap = new();

        /// <summary>
        /// Event fired when a unison bonus should be awarded to players in a band.
        /// Parameters: bandId, phraseTime, bonusMultiplier
        /// </summary>
        public event Action<int, double, float> OnUnisonBonusAwarded;

        private void Start()
        {
            _networkAdapter = NetworkingServiceFactory.Instance as LiteNetNetworkingAdapter;
            if (_networkAdapter == null)
            {
                Debug.LogWarning("[MultiplayerUnisonSync] No LiteNet adapter available - unison sync disabled");
            }
        }

        /// <summary>
        /// Registers a local engine container for unison tracking.
        /// </summary>
        /// <param name="container">The engine container.</param>
        /// <param name="bandId">The band ID this container belongs to.</param>
        public void RegisterEngineContainer(EngineManager.EngineContainer container, int bandId = 0)
        {
            if (container != null && !_localEngineContainers.Contains(container))
            {
                _localEngineContainers.Add(container);
                _containerBandMap[container] = bandId;
                Debug.Log($"[MultiplayerUnisonSync] Registered engine container for band {bandId} (total: {_localEngineContainers.Count})");
            }
        }
        
        /// <summary>
        /// Unregisters an engine container from unison tracking.
        /// </summary>
        public void UnregisterEngineContainer(EngineManager.EngineContainer container)
        {
            if (container != null)
            {
                _localEngineContainers.Remove(container);
                _containerBandMap.Remove(container);
            }
        }
        
        /// <summary>
        /// Clears all registered engine containers.
        /// </summary>
        public void ClearEngineContainers()
        {
            _localEngineContainers.Clear();
            _containerBandMap.Clear();
        }

        /// <summary>
        /// Sets the total number of players for unison tracking (includes remote players).
        /// </summary>
        public void SetTotalPlayerCount(int count)
        {
            _totalPlayerCount = count;
            Debug.Log($"[MultiplayerUnisonSync] Total player count set to {count}");
        }
        
        /// <summary>
        /// Sets the expected player count for a specific band.
        /// </summary>
        public void SetBandPlayerCount(int bandId, int count)
        {
            _networkAdapter?.SetBandUnisonPlayerCount(bandId, count);
            Debug.Log($"[MultiplayerUnisonSync] Band {bandId} player count set to {count}");
        }

        /// <summary>
        /// Called when a local player hits a unison phrase.
        /// </summary>
        /// <param name="bandId">The band the player belongs to.</param>
        /// <param name="phraseTime">The start time of the unison phrase.</param>
        /// <param name="phraseEndTime">The end time of the unison phrase.</param>
        public void OnLocalUnisonPhraseHit(int bandId, double phraseTime, double phraseEndTime)
        {
            if (_networkAdapter == null)
                return;

            // Get local player key
            var localPlayer = _networkAdapter.GetLocalPlayer();
            if (localPlayer == null)
            {
                Debug.LogWarning("[MultiplayerUnisonSync] No local player found for unison hit");
                return;
            }

            // Report to network layer
            if (!_networkAdapter.IsHosting)
            {
                // Client sends to host
                _networkAdapter.SendUnisonPhraseHit(bandId, phraseTime, phraseEndTime);
            }
            // Host handles unison coordination through the handler
        }
        
        /// <summary>
        /// Called when a local player hits a unison phrase (legacy, uses bandId=0).
        /// </summary>
        public void OnLocalUnisonPhraseHit(double phraseTime, double phraseEndTime)
        {
            OnLocalUnisonPhraseHit(0, phraseTime, phraseEndTime);
        }

        /// <summary>
        /// Called by the network layer when a unison bonus is awarded for a band.
        /// </summary>
        public void HandleNetworkUnisonBonus(int bandId, double phraseTime)
        {
            Debug.Log($"[MultiplayerUnisonSync] Received network unison bonus for band {bandId}, phrase at {phraseTime}");
            
            // Award bonus to local engine containers that belong to this band
            foreach (var container in _localEngineContainers)
            {
                if (_containerBandMap.TryGetValue(container, out var containerBandId) && containerBandId == bandId)
                {
                    container?.SendCommand(EngineManager.EngineCommandType.AwardUnisonBonus);
                }
            }
            
            OnUnisonBonusAwarded?.Invoke(bandId, phraseTime, 1.0f);
        }
        
        /// <summary>
        /// Called by the network layer when a unison bonus is awarded (legacy, uses bandId=0).
        /// </summary>
        public void HandleNetworkUnisonBonus(double phraseTime)
        {
            HandleNetworkUnisonBonus(0, phraseTime);
        }
    }
}
