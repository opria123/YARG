using System;
using System.Collections.Generic;
using YARG.Net.Packets;

namespace YARG.Networking.Abstraction
{
    /// <summary>
    /// Tracks the state of a pending late joiner on the host side.
    /// </summary>
    public class LateJoinPlayerState
    {
        /// <summary>
        /// The connection ID of the late joiner.
        /// </summary>
        public Guid ConnectionId { get; set; }
        
        /// <summary>
        /// The player IDs for all players from this connection.
        /// </summary>
        public List<Guid> PlayerIds { get; set; } = new();
        
        /// <summary>
        /// Player names from this connection.
        /// </summary>
        public List<string> PlayerNames { get; set; } = new();
        
        /// <summary>
        /// Whether we've received the song check response from this connection.
        /// </summary>
        public bool HasSongCheckResponse { get; set; }
        
        /// <summary>
        /// Whether the late joiner has the current song.
        /// </summary>
        public bool HasCurrentSong { get; set; }
        
        /// <summary>
        /// The setlist song hashes this late joiner owns.
        /// </summary>
        public HashSet<string> OwnedSetlistHashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        
        /// <summary>
        /// Whether this late joiner is missing any required setlist songs.
        /// </summary>
        public bool IsMissingSetlistSongs { get; set; }
        
        /// <summary>
        /// The action decided for this late joiner.
        /// </summary>
        public LateJoinAction DecidedAction { get; set; } = LateJoinAction.NormalJoin;
        
        /// <summary>
        /// Timestamp when this late joiner connected (for timeout).
        /// </summary>
        public DateTime ConnectedTime { get; set; } = DateTime.UtcNow;
    }
}
