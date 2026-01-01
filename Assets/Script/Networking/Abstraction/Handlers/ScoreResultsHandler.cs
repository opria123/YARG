using System;
using System.Collections.Generic;
using UnityEngine;
using YARG.Net;
using YARG.Net.Packets;
using YARG.Net.Sessions;

namespace YARG.Networking.Abstraction.Handlers
{
    /// <summary>
    /// Handles score results synchronization for multiplayer.
    /// Wraps the YARG.Net ScoreResultsManager and handles message building/parsing.
    /// </summary>
    public class ScoreResultsHandler
    {
        private readonly ScoreResultsManager _manager;
        
        /// <summary>
        /// Event fired when a score result is received from another player.
        /// Parameters: playerName, isHighScore, isFullCombo, score, maxCombo, notesHit, notesMissed
        /// </summary>
        public event Action<string, bool, bool, int, int, int, int> OnResultReceived;
        
        public ScoreResultsHandler()
        {
            _manager = new ScoreResultsManager();
            _manager.ResultReceived += (sender, e) =>
            {
                var r = e.Result;
                OnResultReceived?.Invoke(r.PlayerName, r.IsHighScore, r.IsFullCombo, r.Score, r.MaxCombo, r.NotesHit, r.NotesMissed);
            };
        }

        #region Public API

        /// <summary>
        /// Gets all cached score results from remote players.
        /// Use this to retrieve results that may have arrived before subscribing to events.
        /// </summary>
        public Dictionary<string, PlayerScoreResult> GetCachedResults()
        {
            return _manager.GetResultsDictionary();
        }

        /// <summary>
        /// Clears cached score results. Call this when starting new gameplay.
        /// </summary>
        public void Clear()
        {
            _manager.Clear();
            Debug.Log("[ScoreResultsHandler] Cleared cached score results");
        }

        /// <summary>
        /// Records a score result. Called when receiving score results from remote players.
        /// </summary>
        public void RecordResult(string playerName, bool isHighScore, bool isFullCombo, 
            int score, int maxCombo, int notesHit, int notesMissed)
        {
            _manager.RecordResult(playerName, isHighScore, isFullCombo, score, maxCombo, notesHit, notesMissed);
        }

        #endregion

        #region Message Building

        /// <summary>
        /// Builds a score results message for sending to other players.
        /// </summary>
        public byte[] BuildResultsMessage(string playerName, bool isHighScore, bool isFullCombo, 
            int score, int maxCombo, int notesHit, int notesMissed)
        {
            // Note: starCount field is used to pass isHighScore (1 or 0)
            return ScoreBinaryPackets.BuildResultsPacket(
                playerName,
                score,
                notesHit,
                notesMissed,
                maxCombo,
                isHighScore ? 1 : 0,
                isFullCombo);
        }

        /// <summary>
        /// Builds a score screen advance message.
        /// </summary>
        public byte[] BuildAdvanceMessage(bool hasMoreSongs)
        {
            return ScoreBinaryPackets.BuildAdvancePacket(hasMoreSongs ? 1 : 0);
        }

        #endregion

        #region Message Parsing

        /// <summary>
        /// Result of parsing a score results message.
        /// </summary>
        public struct ScoreResultData
        {
            public bool IsValid;
            public string PlayerName;
            public bool IsHighScore;
            public bool IsFullCombo;
            public int Score;
            public int MaxCombo;
            public int NotesHit;
            public int NotesMissed;
        }

        /// <summary>
        /// Parses a score results message received from another player.
        /// </summary>
        public ScoreResultData ParseResultsMessage(ReadOnlySpan<byte> payload)
        {
            var result = new ScoreResultData { IsValid = false };
            
            if (ScoreBinaryPackets.TryParseResultsPacket(payload, out var parsed))
            {
                result.IsValid = true;
                result.PlayerName = parsed.PlayerName;
                result.IsHighScore = parsed.StarCount > 0;
                result.IsFullCombo = parsed.FullCombo;
                result.Score = parsed.FinalScore;
                result.MaxCombo = parsed.MaxCombo;
                result.NotesHit = parsed.NotesHit;
                result.NotesMissed = parsed.NotesMissed;
            }
            
            return result;
        }

        /// <summary>
        /// Parses a score screen advance message.
        /// </summary>
        public (bool isValid, bool hasMoreSongs) ParseAdvanceMessage(ReadOnlySpan<byte> payload)
        {
            if (ScoreBinaryPackets.TryParseAdvancePacket(payload, out int index))
            {
                return (true, index > 0);
            }
            return (false, false);
        }

        #endregion
    }
}
