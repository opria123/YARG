using System;
using System.Collections.Generic;
using LiteNetLib.Utils;

namespace YARG.Networking.Bands
{
    /// <summary>
    /// Network message for synchronizing band assignments.
    /// Sent by host when players join/leave or bands are reorganized.
    /// </summary>
    public struct BandAssignmentMessage : INetSerializable
    {
        /// <summary>
        /// Map of player ID to band ID.
        /// </summary>
        public Dictionary<Guid, int> Assignments;

        /// <summary>
        /// The band size setting (0 = disabled).
        /// </summary>
        public int BandSize;
        
        /// <summary>
        /// The lobby seed used for deterministic band name generation.
        /// </summary>
        public int LobbySeed;
        
        /// <summary>
        /// Band names indexed by band ID.
        /// </summary>
        public Dictionary<int, string> BandNames;
        
        /// <summary>
        /// Name regeneration counts indexed by band ID (for deterministic regeneration).
        /// </summary>
        public Dictionary<int, int> BandNameRegenerations;
        
        /// <summary>
        /// Connection groups - maps connection ID to list of player IDs.
        /// </summary>
        public Dictionary<int, List<Guid>> ConnectionGroups;
        
        /// <summary>
        /// Map of player ID to player name.
        /// Used by clients to associate player names with IDs for displaying other band members.
        /// </summary>
        public Dictionary<Guid, string> PlayerNames;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(BandSize);
            writer.Put(LobbySeed);
            
            // Assignments
            writer.Put(Assignments?.Count ?? 0);
            if (Assignments != null)
            {
                foreach (var kvp in Assignments)
                {
                    writer.Put(kvp.Key.ToString());
                    writer.Put(kvp.Value);
                }
            }
            
            // Band names
            writer.Put(BandNames?.Count ?? 0);
            if (BandNames != null)
            {
                foreach (var kvp in BandNames)
                {
                    writer.Put(kvp.Key);
                    writer.Put(kvp.Value ?? string.Empty);
                }
            }
            
            // Name regeneration counts
            writer.Put(BandNameRegenerations?.Count ?? 0);
            if (BandNameRegenerations != null)
            {
                foreach (var kvp in BandNameRegenerations)
                {
                    writer.Put(kvp.Key);
                    writer.Put(kvp.Value);
                }
            }
            
            // Connection groups
            writer.Put(ConnectionGroups?.Count ?? 0);
            if (ConnectionGroups != null)
            {
                foreach (var kvp in ConnectionGroups)
                {
                    writer.Put(kvp.Key);
                    writer.Put(kvp.Value?.Count ?? 0);
                    if (kvp.Value != null)
                    {
                        foreach (var playerId in kvp.Value)
                        {
                            writer.Put(playerId.ToString());
                        }
                    }
                }
            }
            
            // Player names (maps player ID to name)
            writer.Put(PlayerNames?.Count ?? 0);
            if (PlayerNames != null)
            {
                foreach (var kvp in PlayerNames)
                {
                    writer.Put(kvp.Key.ToString());
                    writer.Put(kvp.Value ?? string.Empty);
                }
            }
        }

        public void Deserialize(NetDataReader reader)
        {
            BandSize = reader.GetInt();
            LobbySeed = reader.GetInt();
            
            // Assignments
            int assignmentCount = reader.GetInt();
            Assignments = new Dictionary<Guid, int>(assignmentCount);
            for (int i = 0; i < assignmentCount; i++)
            {
                var playerId = Guid.Parse(reader.GetString());
                var bandId = reader.GetInt();
                Assignments[playerId] = bandId;
            }
            
            // Band names
            int bandNameCount = reader.GetInt();
            BandNames = new Dictionary<int, string>(bandNameCount);
            for (int i = 0; i < bandNameCount; i++)
            {
                var bandId = reader.GetInt();
                var name = reader.GetString();
                BandNames[bandId] = name;
            }
            
            // Name regeneration counts
            int regenCount = reader.GetInt();
            BandNameRegenerations = new Dictionary<int, int>(regenCount);
            for (int i = 0; i < regenCount; i++)
            {
                var bandId = reader.GetInt();
                var count = reader.GetInt();
                BandNameRegenerations[bandId] = count;
            }
            
            // Connection groups
            int groupCount = reader.GetInt();
            ConnectionGroups = new Dictionary<int, List<Guid>>(groupCount);
            for (int i = 0; i < groupCount; i++)
            {
                var connectionId = reader.GetInt();
                int playerCount = reader.GetInt();
                var players = new List<Guid>(playerCount);
                for (int j = 0; j < playerCount; j++)
                {
                    players.Add(Guid.Parse(reader.GetString()));
                }
                ConnectionGroups[connectionId] = players;
            }
            
            // Player names (maps player ID to name) - handle backward compatibility
            if (reader.AvailableBytes > 0)
            {
                int playerNameCount = reader.GetInt();
                PlayerNames = new Dictionary<Guid, string>(playerNameCount);
                for (int i = 0; i < playerNameCount; i++)
                {
                    var playerId = Guid.Parse(reader.GetString());
                    var playerName = reader.GetString();
                    PlayerNames[playerId] = playerName;
                }
            }
            else
            {
                PlayerNames = new Dictionary<Guid, string>();
            }
        }
    }

    /// <summary>
    /// Network message for band score updates during gameplay.
    /// Sent periodically by clients to host, host aggregates and broadcasts.
    /// </summary>
    public struct BandScoreUpdateMessage : INetSerializable
    {
        /// <summary>
        /// The band ID being updated.
        /// </summary>
        public int BandId;

        /// <summary>
        /// Total score for the band.
        /// </summary>
        public long TotalScore;

        /// <summary>
        /// Individual player scores for this band (for final results).
        /// </summary>
        public Dictionary<Guid, int> PlayerScores;

        /// <summary>
        /// Whether this is a final score (end of song).
        /// </summary>
        public bool IsFinal;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(BandId);
            writer.Put(TotalScore);
            writer.Put(IsFinal);

            writer.Put(PlayerScores?.Count ?? 0);
            if (PlayerScores != null)
            {
                foreach (var kvp in PlayerScores)
                {
                    writer.Put(kvp.Key.ToString());
                    writer.Put(kvp.Value);
                }
            }
        }

        public void Deserialize(NetDataReader reader)
        {
            BandId = reader.GetInt();
            TotalScore = reader.GetLong();
            IsFinal = reader.GetBool();

            int count = reader.GetInt();
            PlayerScores = new Dictionary<Guid, int>(count);
            for (int i = 0; i < count; i++)
            {
                var playerId = Guid.Parse(reader.GetString());
                var score = reader.GetInt();
                PlayerScores[playerId] = score;
            }
        }
    }

    /// <summary>
    /// Network message for band leaderboard (sent by host during/after gameplay).
    /// </summary>
    public struct BandLeaderboardMessage : INetSerializable
    {
        /// <summary>
        /// Band scores ordered by rank.
        /// </summary>
        public BandRankEntry[] Rankings;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(Rankings?.Length ?? 0);

            if (Rankings != null)
            {
                foreach (var entry in Rankings)
                {
                    writer.Put(entry.BandId);
                    writer.Put(entry.TotalScore);
                    writer.Put(entry.PlayerCount);
                }
            }
        }

        public void Deserialize(NetDataReader reader)
        {
            int count = reader.GetInt();
            Rankings = new BandRankEntry[count];

            for (int i = 0; i < count; i++)
            {
                Rankings[i] = new BandRankEntry
                {
                    BandId = reader.GetInt(),
                    TotalScore = reader.GetLong(),
                    PlayerCount = reader.GetInt()
                };
            }
        }
    }

    /// <summary>
    /// Entry in the band leaderboard.
    /// </summary>
    public struct BandRankEntry
    {
        public int BandId;
        public long TotalScore;
        public int PlayerCount;

        /// <summary>
        /// Average score per player in the band.
        /// </summary>
        public readonly long AverageScore => PlayerCount > 0 ? TotalScore / PlayerCount : 0;
    }
    
    /// <summary>
    /// Network message for band name changes (regeneration).
    /// Sent by host when a band name is regenerated.
    /// </summary>
    public struct BandNameChangeMessage : INetSerializable
    {
        /// <summary>
        /// The band ID whose name was changed.
        /// </summary>
        public int BandId;
        
        /// <summary>
        /// The new band name.
        /// </summary>
        public string NewName;
        
        /// <summary>
        /// The regeneration count (for deterministic sync).
        /// </summary>
        public int RegenerationCount;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(BandId);
            writer.Put(NewName ?? string.Empty);
            writer.Put(RegenerationCount);
        }

        public void Deserialize(NetDataReader reader)
        {
            BandId = reader.GetInt();
            NewName = reader.GetString();
            RegenerationCount = reader.GetInt();
        }
    }
    
    /// <summary>
    /// Network message for requesting a band name change (client to host).
    /// Sent by client when they click the regenerate button on their band.
    /// </summary>
    public struct BandNameChangeRequestMessage : INetSerializable
    {
        /// <summary>
        /// The band ID to regenerate name for.
        /// </summary>
        public int BandId;
        
        /// <summary>
        /// The player ID making the request (for validation).
        /// </summary>
        public Guid RequestingPlayerId;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(BandId);
            writer.Put(RequestingPlayerId.ToString());
        }

        public void Deserialize(NetDataReader reader)
        {
            BandId = reader.GetInt();
            RequestingPlayerId = Guid.Parse(reader.GetString());
        }
    }
    
    /// <summary>
    /// Network message for band failure notification.
    /// Sent by client when their band fails, host broadcasts to all.
    /// </summary>
    public struct BandFailedMessage : INetSerializable
    {
        /// <summary>
        /// The band ID that failed.
        /// </summary>
        public int BandId;
        
        /// <summary>
        /// Final score of the band at time of failure.
        /// </summary>
        public long FinalScore;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(BandId);
            writer.Put(FinalScore);
        }

        public void Deserialize(NetDataReader reader)
        {
            BandId = reader.GetInt();
            FinalScore = reader.GetLong();
        }
    }
}
