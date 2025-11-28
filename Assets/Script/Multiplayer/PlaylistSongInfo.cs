using System;
using YARG.Core.Song;
using YARG.Song;

namespace YARG.Multiplayer
{
    /// <summary>
    /// Snapshot of a playlist entry change, including metadata resolved by the host.
    /// </summary>
    public class PlaylistSongInfo
    {
        public HashWrapper Hash { get; }
        public string SongName { get; }
        public string ArtistName { get; }
        public string QueuedBy { get; }
        public SongEntry ResolvedSong { get; }
        public DateTime TimestampUtc { get; }
        public bool HasResolvedSong => ResolvedSong != null;

        public PlaylistSongInfo(
            HashWrapper hash,
            string songName,
            string artistName,
            string queuedBy,
            SongEntry resolvedSong,
            DateTime timestampUtc)
        {
            Hash = hash;
            SongName = string.IsNullOrWhiteSpace(songName) ? hash.ToString() : songName;
            ArtistName = string.IsNullOrWhiteSpace(artistName) ? "Unknown Artist" : artistName;
            QueuedBy = string.IsNullOrWhiteSpace(queuedBy) ? "Player" : queuedBy;
            ResolvedSong = resolvedSong;
            TimestampUtc = timestampUtc;
        }
    }
}
