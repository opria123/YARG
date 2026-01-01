using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using YARG.Core.Song;
using YARG.Menu;
using YARG.Net.Packets;
using YARG.Net.Sessions;
using YARG.Net.Transport;
using YARG.Song;

namespace YARG.Networking.Abstraction.Handlers
{
    /// <summary>
    /// Handles setlist management (adding/removing songs, starting shows) for multiplayer.
    /// </summary>
    public sealed class SetlistHandler : IDisposable
    {
        private readonly SetlistManager _setlistManager;

        /// <summary>
        /// Fired when a song is added to the setlist.
        /// Parameters: playerName, songName, songArtist
        /// </summary>
        public event Action<string, string, string> OnSongAdded;

        /// <summary>
        /// Fired when a song is removed from the setlist.
        /// Parameters: playerName, songName, songArtist
        /// </summary>
        public event Action<string, string, string> OnSongRemoved;

        /// <summary>
        /// Fired when the setlist is updated (any change).
        /// </summary>
        public event Action OnSetlistUpdated;

        /// <summary>
        /// Fired when the host starts the show.
        /// </summary>
        public event Action OnShowStarted;

        public SetlistHandler(SetlistManager setlistManager)
        {
            _setlistManager = setlistManager ?? throw new ArgumentNullException(nameof(setlistManager));

            // Wire up manager events
            _setlistManager.SongAdded += HandleManagerSongAdded;
            _setlistManager.SongRemoved += HandleManagerSongRemoved;
            _setlistManager.SetlistCleared += HandleManagerCleared;
            _setlistManager.SetlistSynced += HandleManagerSynced;
        }

        /// <summary>
        /// Gets the setlist manager.
        /// </summary>
        public SetlistManager Manager => _setlistManager;

        /// <summary>
        /// Gets whether a song is in the setlist.
        /// </summary>
        public bool IsInSetlist(string songHash) => _setlistManager?.Contains(songHash) ?? false;

        /// <summary>
        /// Gets the current song hashes in the setlist.
        /// </summary>
        public IReadOnlyList<string> SongHashes => _setlistManager?.SongHashes ?? Array.Empty<string>();

        /// <summary>
        /// Gets the count of songs in the setlist.
        /// </summary>
        public int Count => _setlistManager?.Count ?? 0;

        /// <summary>
        /// Gets whether the setlist is empty.
        /// </summary>
        public bool IsEmpty => _setlistManager?.IsEmpty ?? true;

        #region Host-Side Methods

        /// <summary>
        /// Adds a song to the setlist (host only, called after validation).
        /// </summary>
        /// <returns>True if the song was added, false if already in setlist or full.</returns>
        public bool AddSong(string songHash, string playerName, string songName, string songArtist)
        {
            if (_setlistManager == null)
                return false;

            if (!_setlistManager.TryAdd(songHash, songName, songArtist, playerName, out var entry))
            {
                Debug.Log($"[SetlistHandler] Song {songHash} already in setlist or setlist full");
                return false;
            }

            Debug.Log($"[SetlistHandler] Added {songHash} to setlist (now {_setlistManager.Count} songs)");
            return true;
        }

        /// <summary>
        /// Removes a song from the setlist (host only).
        /// </summary>
        /// <returns>True if the song was removed.</returns>
        public bool RemoveSong(string songHash)
        {
            if (_setlistManager == null)
                return false;

            if (!_setlistManager.TryRemove(songHash, out var removedEntry))
            {
                Debug.Log($"[SetlistHandler] Song {songHash} not in setlist");
                return false;
            }

            Debug.Log($"[SetlistHandler] Removed {songHash} from setlist (now {_setlistManager.Count} songs)");
            return true;
        }

        /// <summary>
        /// Pops the first song from the setlist (after completing a song).
        /// </summary>
        public SetlistEntry PopFirst() => _setlistManager?.PopFirst();

        /// <summary>
        /// Clears the setlist.
        /// </summary>
        public void Clear() => _setlistManager?.Clear();

        /// <summary>
        /// Builds a setlist add broadcast packet.
        /// </summary>
        public static byte[] BuildAddPacket(string songHash, string playerName, string songName, string songArtist)
        {
            return SetlistBinaryPackets.BuildAddPacket(songHash, playerName, songName, songArtist);
        }

        /// <summary>
        /// Builds a setlist remove broadcast packet.
        /// </summary>
        public static byte[] BuildRemovePacket(string songHash, string playerName, string songName, string songArtist)
        {
            return SetlistBinaryPackets.BuildRemovePacket(songHash, playerName, songName, songArtist);
        }

        /// <summary>
        /// Builds a setlist sync packet for sending to a newly joined client.
        /// </summary>
        public byte[] BuildSyncPacket()
        {
            var entries = _setlistManager.GetAllEntries()
                .Select(e => new SetlistEntry(e.SongHash, e.SongName, e.SongArtist, e.AddedByPlayerName))
                .ToList();
            return SetlistBinaryPackets.BuildSyncPacket(entries);
        }

        /// <summary>
        /// Builds a start show packet.
        /// </summary>
        public byte[] BuildStartPacket()
        {
            var songHashes = _setlistManager.SongHashes.ToList();
            return SetlistBinaryPackets.BuildStartPacket(songHashes);
        }

        #endregion

        #region Client-Side Message Handlers

        /// <summary>
        /// Handles setlist add message.
        /// </summary>
        /// <param name="payload">The packet payload.</param>
        /// <param name="isHost">Whether the local player is the host.</param>
        /// <returns>Parsed entry if successful, null otherwise.</returns>
        public (string songHash, string playerName, string songName, string songArtist)? HandleAddMessage(ReadOnlySpan<byte> payload)
        {
            if (!SetlistBinaryPackets.TryParseAddOrRemove(payload, out var entry))
            {
                Debug.LogWarning("[SetlistHandler] Invalid setlist add message");
                return null;
            }

            Debug.Log($"[SetlistHandler] Received setlist add: {entry.SongHash} from {entry.PlayerName}");
            return (entry.SongHash, entry.PlayerName, entry.SongName, entry.ArtistName);
        }

        /// <summary>
        /// Handles setlist remove message.
        /// </summary>
        public (string songHash, string playerName, string songName, string songArtist)? HandleRemoveMessage(ReadOnlySpan<byte> payload)
        {
            if (!SetlistBinaryPackets.TryParseAddOrRemove(payload, out var entry))
            {
                Debug.LogWarning("[SetlistHandler] Invalid setlist remove message");
                return null;
            }

            Debug.Log($"[SetlistHandler] Received setlist remove: {entry.SongHash} from {entry.PlayerName}");
            return (entry.SongHash, entry.PlayerName, entry.SongName, entry.ArtistName);
        }

        /// <summary>
        /// Handles setlist sync message (client receives full setlist from host).
        /// </summary>
        public bool HandleSyncMessage(ReadOnlySpan<byte> payload)
        {
            if (!SetlistBinaryPackets.TryParseSyncPacket(payload, out var entries))
            {
                Debug.LogWarning("[SetlistHandler] Invalid setlist sync message");
                return false;
            }

            Debug.Log($"[SetlistHandler] Client: Received setlist sync: {entries.Count} entries");

            _setlistManager.Clear();
            foreach (var entry in entries)
            {
                _setlistManager.TryAdd(entry.SongHash, entry.SongName, entry.ArtistName, entry.PlayerName, out _);
            }

            Debug.Log($"[SetlistHandler] Client: Setlist synced with {_setlistManager.Count} songs");
            return true;
        }

        /// <summary>
        /// Handles start show message.
        /// </summary>
        public IReadOnlyList<string> HandleStartMessage(ReadOnlySpan<byte> payload)
        {
            if (!SetlistBinaryPackets.TryParseStartPacket(payload, out var songHashes))
            {
                Debug.LogWarning("[SetlistHandler] Invalid setlist start message");
                return null;
            }

            Debug.Log($"[SetlistHandler] Client: Received start show with {songHashes.Count} songs");
            return songHashes;
        }

        /// <summary>
        /// Adds a song to the client's local setlist (from server broadcast).
        /// </summary>
        public void AddSongLocally(string songHash, string playerName, string songName, string songArtist)
        {
            if (_setlistManager.TryAdd(songHash, songName, songArtist, playerName, out _))
            {
                OnSongAdded?.Invoke(playerName, songName, songArtist);
                OnSetlistUpdated?.Invoke();
            }
        }

        /// <summary>
        /// Removes a song from the client's local setlist (from server broadcast).
        /// </summary>
        public void RemoveSongLocally(string songHash, string playerName, string songName, string songArtist)
        {
            if (_setlistManager.TryRemove(songHash, out _))
            {
                OnSongRemoved?.Invoke(playerName, songName, songArtist);
                OnSetlistUpdated?.Invoke();
            }
        }

        #endregion

        #region State Application

        /// <summary>
        /// Applies the current setlist to GlobalVariables.State for gameplay.
        /// </summary>
        public void ApplyToGlobalState()
        {
            var showSongs = new List<SongEntry>();
            foreach (var hash in _setlistManager.SongHashes)
            {
                var hashWrapper = HashWrapper.FromString(hash);
                if (SongContainer.SongsByHash.TryGetValue(hashWrapper, out var songList) && songList.Count > 0)
                {
                    showSongs.Add(songList[0]);
                }
            }

            GlobalVariables.State.ShowSongs = showSongs;
            GlobalVariables.State.PlayingAShow = true;
            GlobalVariables.State.ShowIndex = 0;

            if (showSongs.Count > 0)
            {
                GlobalVariables.State.CurrentSong = showSongs[0];
            }

            Debug.Log($"[SetlistHandler] Applied setlist to GlobalState: {showSongs.Count} songs");
        }

        /// <summary>
        /// Triggers the show started event and applies state.
        /// </summary>
        public void TriggerShowStarted()
        {
            ApplyToGlobalState();
            OnShowStarted?.Invoke();
        }

        #endregion

        #region Manager Event Bridges

        private void HandleManagerSongAdded(object sender, SetlistSongEventArgs e)
        {
            OnSongAdded?.Invoke(e.Entry.AddedByPlayerName, e.Entry.SongName, e.Entry.SongArtist);
            OnSetlistUpdated?.Invoke();
        }

        private void HandleManagerSongRemoved(object sender, SetlistSongEventArgs e)
        {
            OnSongRemoved?.Invoke(e.Entry.AddedByPlayerName, e.Entry.SongName, e.Entry.SongArtist);
            OnSetlistUpdated?.Invoke();
        }

        private void HandleManagerCleared(object sender, EventArgs e)
        {
            OnSetlistUpdated?.Invoke();
        }

        private void HandleManagerSynced(object sender, EventArgs e)
        {
            OnSetlistUpdated?.Invoke();
        }

        #endregion

        public void Dispose()
        {
            if (_setlistManager != null)
            {
                _setlistManager.SongAdded -= HandleManagerSongAdded;
                _setlistManager.SongRemoved -= HandleManagerSongRemoved;
                _setlistManager.SetlistCleared -= HandleManagerCleared;
                _setlistManager.SetlistSynced -= HandleManagerSynced;
            }
        }
    }
}
