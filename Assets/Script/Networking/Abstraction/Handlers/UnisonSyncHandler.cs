using System;
using UnityEngine;
using YARG.Net;
using YARG.Net.Packets;
using YARG.Net.Sessions;

namespace YARG.Networking.Abstraction.Handlers
{
    /// <summary>
    /// Handles unison phrase synchronization for multiplayer gameplay.
    /// Wraps the YARG.Net UnisonCoordinator and handles message building/parsing.
    /// Supports per-band unison tracking.
    /// </summary>
    public class UnisonSyncHandler
    {
        private readonly UnisonCoordinator _coordinator;
        
        /// <summary>
        /// Event fired when a unison bonus is awarded.
        /// Parameters: phraseTime, bandId
        /// </summary>
        public event Action<double, int> OnBonusAwarded;
        
        public UnisonSyncHandler()
        {
            _coordinator = new UnisonCoordinator();
            _coordinator.UnisonBonusAwarded += (sender, e) =>
            {
                OnBonusAwarded?.Invoke(e.PhraseTime, e.BandId);
            };
        }

        #region Public API

        /// <summary>
        /// Gets the underlying coordinator for direct access if needed.
        /// </summary>
        public UnisonCoordinator Coordinator => _coordinator;

        /// <summary>
        /// Sets the expected number of players for unison tracking (legacy global).
        /// Should be called when gameplay starts.
        /// </summary>
        public void SetPlayerCount(int count)
        {
            _coordinator.ExpectedPlayerCount = count;
            _coordinator.Reset();
            Debug.Log($"[UnisonSyncHandler] Global player count set to {count}");
        }
        
        /// <summary>
        /// Sets the expected number of players for a specific band.
        /// </summary>
        /// <param name="bandId">The band ID.</param>
        /// <param name="playerCount">Number of players in this band.</param>
        public void SetBandPlayerCount(int bandId, int playerCount)
        {
            _coordinator.SetBandPlayerCount(bandId, playerCount);
            Debug.Log($"[UnisonSyncHandler] Band {bandId} player count set to {playerCount}");
        }
        
        /// <summary>
        /// Clears all per-band player counts.
        /// </summary>
        public void ClearBandPlayerCounts()
        {
            _coordinator.ClearBandPlayerCounts();
            Debug.Log("[UnisonSyncHandler] Band player counts cleared");
        }

        /// <summary>
        /// Resets unison tracking state. Should be called when gameplay ends or restarts.
        /// </summary>
        public void Reset()
        {
            _coordinator.Reset();
            _coordinator.ExpectedPlayerCount = 0;
            Debug.Log("[UnisonSyncHandler] Tracking reset");
        }
        
        /// <summary>
        /// Fully resets all state including band counts. Should be called when leaving lobby.
        /// </summary>
        public void FullReset()
        {
            _coordinator.FullReset();
            Debug.Log("[UnisonSyncHandler] Full reset (including band counts)");
        }

        /// <summary>
        /// Records a phrase hit on the host (per-band). Returns true if all players in the band have completed and bonus was awarded.
        /// </summary>
        public bool RecordPhraseHit(string playerKey, int bandId, double phraseTime, double phraseEndTime)
        {
            return _coordinator.RecordPhraseHit(playerKey, bandId, phraseTime, phraseEndTime);
        }
        
        /// <summary>
        /// Records a phrase hit on the host (legacy global). Returns true if all players have completed and bonus was awarded.
        /// </summary>
        public bool RecordPhraseHit(string playerKey, double phraseTime, double phraseEndTime)
        {
            return _coordinator.RecordPhraseHit(playerKey, phraseTime, phraseEndTime);
        }

        #endregion

        #region Message Building

        /// <summary>
        /// Builds a phrase hit message for sending to the host (with band ID).
        /// </summary>
        public byte[] BuildPhraseHitMessage(string playerName, int bandId, double phraseTime, double phraseEndTime)
        {
            return UnisonBinaryPackets.BuildPhraseHitPacket(playerName, bandId, phraseTime, phraseEndTime);
        }
        
        /// <summary>
        /// Builds a phrase hit message for sending to the host (legacy, no band ID).
        /// </summary>
        public byte[] BuildPhraseHitMessage(string playerName, double phraseTime, double phraseEndTime)
        {
            return UnisonBinaryPackets.BuildPhraseHitPacket(playerName, phraseTime, phraseEndTime);
        }

        /// <summary>
        /// Builds a bonus award message for broadcasting to clients (with band ID).
        /// </summary>
        public byte[] BuildBonusAwardMessage(int bandId, double phraseTime)
        {
            return UnisonBinaryPackets.BuildBonusAwardPacket(bandId, phraseTime);
        }
        
        /// <summary>
        /// Builds a bonus award message for broadcasting to clients (legacy, no band ID).
        /// </summary>
        public byte[] BuildBonusAwardMessage(double phraseTime)
        {
            return UnisonBinaryPackets.BuildBonusAwardPacket(phraseTime);
        }

        #endregion

        #region Message Parsing

        /// <summary>
        /// Result of parsing a phrase hit message.
        /// </summary>
        public struct PhraseHitData
        {
            public bool IsValid;
            public string PlayerName;
            public int BandId;
            public double PhraseStartTime;
            public double PhraseEndTime;
        }

        /// <summary>
        /// Parses a phrase hit message received from a client.
        /// </summary>
        public PhraseHitData ParsePhraseHitMessage(ReadOnlySpan<byte> payload)
        {
            var result = new PhraseHitData { IsValid = false };
            
            if (UnisonBinaryPackets.TryParsePhraseHitPacket(payload, out var parsed))
            {
                result.IsValid = true;
                result.PlayerName = parsed.PlayerName;
                result.BandId = parsed.BandId;
                result.PhraseStartTime = parsed.PhraseStartTime;
                result.PhraseEndTime = parsed.PhraseEndTime;
            }
            
            return result;
        }

        /// <summary>
        /// Parses a bonus award message received from the host.
        /// </summary>
        public (bool isValid, int bandId, double phraseTime) ParseBonusAwardMessage(ReadOnlySpan<byte> payload)
        {
            if (UnisonBinaryPackets.TryParseBonusAwardPacket(payload, out int bandId, out double phraseTime))
            {
                return (true, bandId, phraseTime);
            }
            return (false, 0, 0);
        }

        #endregion
    }
}
