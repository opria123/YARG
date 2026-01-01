using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using YARG.Core.Song;
using YARG.Multiplayer;
using YARG.Net.Packets;
using YARG.Net.Transport;
using YARG.Song;

namespace YARG.Networking.Abstraction.Handlers
{
    /// <summary>
    /// Handles song library synchronization between host and clients.
    /// Computes shared songs (intersection of all players' libraries) and broadcasts to clients.
    /// </summary>
    public sealed class SongLibrarySyncHandler : IDisposable
    {
        private const int MAX_SHARED_SONG_CHUNK_BYTES = 8192;
        private static readonly int SONG_HASHES_PER_CHUNK = MAX_SHARED_SONG_CHUNK_BYTES / HashWrapper.HASH_SIZE_IN_BYTES;

        // Server-side state
        private readonly Dictionary<Guid, HashSet<HashWrapper>> _playerSongLibraries = new();
        private readonly HashSet<Guid> _playersPendingSongSync = new();
        private HashSet<HashWrapper> _sharedSongHashes;
        private bool _sharedSongSyncComplete = true;

        // Client-side state
        private bool _songLibraryUploaded;
        private int _lastUploadedSongVersion = -1;

        /// <summary>
        /// Fired when the shared song sync state changes (all players synced or not).
        /// </summary>
        public event Action<bool> OnSyncStateChanged;

        /// <summary>
        /// Gets whether all players have completed their song library sync.
        /// </summary>
        public bool IsSyncComplete => _sharedSongSyncComplete;

        /// <summary>
        /// Gets the number of shared songs (songs all players have).
        /// </summary>
        public int SharedSongCount => _sharedSongHashes?.Count ?? 0;

        /// <summary>
        /// Gets the shared song hashes (for host to update its own filter).
        /// </summary>
        public IReadOnlyCollection<HashWrapper> SharedSongHashes => _sharedSongHashes;

        #region Server-Side (Host) Methods

        /// <summary>
        /// Handles song library chunk from a client.
        /// </summary>
        /// <returns>True if all players have completed their song library sync.</returns>
        public bool HandleSongLibraryChunk(Guid connectionId, ReadOnlySpan<byte> payload)
        {
            var parsed = SongLibraryBinaryPackets.ParseChunkHeader(payload);
            if (!parsed.IsValid)
            {
                Debug.LogWarning("[SongLibrarySyncHandler] Invalid song library chunk");
                return false;
            }

            try
            {
                // Initialize or reset library for this player on first chunk
                if (parsed.IsFirstChunk || !_playerSongLibraries.ContainsKey(connectionId))
                {
                    _playerSongLibraries[connectionId] = new HashSet<HashWrapper>();
                    _playersPendingSongSync.Add(connectionId);
                    UpdateSyncState();
                    Debug.Log($"[SongLibrarySyncHandler] Started receiving song library from {connectionId}");
                }

                var library = _playerSongLibraries[connectionId];

                // Parse hashes from payload
                int hashSize = HashWrapper.HASH_SIZE_IN_BYTES;
                int offset = parsed.DataOffset;
                int endOffset = parsed.DataOffset + parsed.DataLength;
                int processedHashes = 0;

                while (offset + hashSize <= endOffset)
                {
                    var hash = HashWrapper.Create(payload.Slice(offset, hashSize));
                    library.Add(hash);
                    offset += hashSize;
                    processedHashes++;
                }

                Debug.Log($"[SongLibrarySyncHandler] Processed {processedHashes} hashes from {connectionId} (total: {library.Count})");

                if (parsed.IsFinalChunk)
                {
                    _playersPendingSongSync.Remove(connectionId);
                    Debug.Log($"[SongLibrarySyncHandler] Completed song library sync for {connectionId} ({library.Count} songs)");
                    RecalculateSharedSongs();
                    
                    // Return true if all players have synced
                    return _playersPendingSongSync.Count == 0;
                }
                
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SongLibrarySyncHandler] Error processing song library chunk: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Registers the host's own song library.
        /// </summary>
        public void RegisterHostLibrary()
        {
            var hostId = Guid.Empty; // Use empty GUID to represent host
            var hashList = SongContainer.SongHashes;

            _playerSongLibraries[hostId] = new HashSet<HashWrapper>(hashList);
            Debug.Log($"[SongLibrarySyncHandler] Host: Registered own song library ({hashList.Count} songs)");

            RecalculateSharedSongs();
        }

        /// <summary>
        /// Removes a player's song library (when they disconnect).
        /// </summary>
        public void RemovePlayerLibrary(Guid connectionId)
        {
            _playerSongLibraries.Remove(connectionId);
            _playersPendingSongSync.Remove(connectionId);
            UpdateSyncState();
        }

        /// <summary>
        /// Recalculates the intersection of all player song libraries.
        /// Returns the shared songs to be broadcast to clients.
        /// </summary>
        public void RecalculateSharedSongs()
        {
            // Wait until all players have finished uploading
            if (_playersPendingSongSync.Count > 0)
            {
                Debug.Log($"[SongLibrarySyncHandler] Skipping shared song calculation ({_playersPendingSongSync.Count} players still syncing)");
                return;
            }

            int playerCount = _playerSongLibraries.Count;

            if (playerCount == 0)
            {
                _sharedSongHashes = null;
                UpdateSyncState();
                Debug.Log("[SongLibrarySyncHandler] No player libraries, clearing shared songs");
                return;
            }

            // Compute intersection
            HashSet<HashWrapper> intersection = null;
            foreach (var library in _playerSongLibraries.Values)
            {
                if (intersection == null)
                {
                    intersection = new HashSet<HashWrapper>(library);
                }
                else
                {
                    intersection.IntersectWith(library);
                }

                // Early exit if no common songs
                if (intersection.Count == 0)
                    break;
            }

            _sharedSongHashes = intersection ?? new HashSet<HashWrapper>();
            Debug.Log($"[SongLibrarySyncHandler] Computed shared songs for {playerCount} players: {_sharedSongHashes.Count} songs");

            UpdateSyncState();
        }

        /// <summary>
        /// Builds chunks of shared songs for sending to clients.
        /// </summary>
        public IReadOnlyList<byte[]> BuildSharedSongChunks()
        {
            if (_sharedSongHashes == null || _sharedSongHashes.Count == 0)
            {
                return new List<byte[]> { Array.Empty<byte>() };
            }

            int hashSize = HashWrapper.HASH_SIZE_IN_BYTES;
            int hashesPerChunk = Math.Max(1, MAX_SHARED_SONG_CHUNK_BYTES / hashSize);

            var hashArray = _sharedSongHashes.ToArray();
            var chunks = new List<byte[]>();
            int index = 0;

            while (index < hashArray.Length)
            {
                int chunkCount = Math.Min(hashesPerChunk, hashArray.Length - index);
                using var stream = new MemoryStream(chunkCount * hashSize);
                for (int i = 0; i < chunkCount; i++)
                {
                    hashArray[index + i].Serialize(stream);
                }

                chunks.Add(stream.ToArray());
                index += chunkCount;
            }

            return chunks;
        }

        /// <summary>
        /// Builds a SharedSongsChunk packet using YARG.Net packet builder.
        /// </summary>
        public static byte[] BuildSharedSongsChunkPacket(byte[] hashData, bool isFirstChunk, bool isFinalChunk)
        {
            return SongLibraryBinaryPackets.BuildSharedSongsChunkPacket(hashData, isFirstChunk, isFinalChunk);
        }

        /// <summary>
        /// Builds a ClearSharedSongs packet using YARG.Net packet builder.
        /// </summary>
        public static byte[] BuildClearSharedSongsPacket()
        {
            return SongLibraryBinaryPackets.BuildClearSharedSongsPacket();
        }

        #endregion

        #region Client-Side Methods

        /// <summary>
        /// Handles shared songs chunk from the server.
        /// </summary>
        public void HandleSharedSongsChunk(ReadOnlySpan<byte> payload)
        {
            var parsed = SongLibraryBinaryPackets.ParseChunkHeader(payload);
            if (!parsed.IsValid)
            {
                Debug.LogWarning("[SongLibrarySyncHandler] Client: Invalid shared songs chunk");
                return;
            }

            try
            {
                if (parsed.IsFirstChunk)
                {
                    MultiplayerSongFilter.BeginSharedSongsUpload();
                }

                if (parsed.DataLength > 0)
                {
                    byte[] chunk = payload.Slice(parsed.DataOffset, parsed.DataLength).ToArray();
                    MultiplayerSongFilter.AppendSharedSongsChunk(chunk);
                }

                if (parsed.IsFinalChunk)
                {
                    MultiplayerSongFilter.CommitSharedSongsUpload();
                    Debug.Log("[SongLibrarySyncHandler] Client: Shared songs sync complete");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SongLibrarySyncHandler] Client: Error processing shared songs chunk: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles clear shared songs message from the server.
        /// </summary>
        public void HandleClearSharedSongs()
        {
            Debug.Log("[SongLibrarySyncHandler] Client: Received clear shared songs command");
            MultiplayerSongFilter.ClearSharedSongs();
        }

        /// <summary>
        /// Uploads the local song library to the server.
        /// </summary>
        /// <param name="connection">The server connection to send to.</param>
        /// <returns>True if upload was initiated, false if already uploaded or no songs.</returns>
        public bool UploadSongLibrary(INetConnection connection)
        {
            int currentVersion = SongContainer.RefreshVersion;
            if (_lastUploadedSongVersion == currentVersion && _songLibraryUploaded)
            {
                Debug.Log("[SongLibrarySyncHandler] Song library already uploaded for this version");
                return false;
            }

            var hashList = SongContainer.SongHashes;
            int totalSongs = hashList.Count;

            Debug.Log($"[SongLibrarySyncHandler] Client: Uploading song library ({totalSongs} songs)");

            SendSongLibraryChunks(connection, hashList);

            _lastUploadedSongVersion = currentVersion;
            _songLibraryUploaded = true;
            return true;
        }

        private void SendSongLibraryChunks(INetConnection connection, IReadOnlyList<HashWrapper> hashList)
        {
            int hashSize = HashWrapper.HASH_SIZE_IN_BYTES;
            int totalSongs = hashList.Count;

            if (totalSongs == 0)
            {
                SendSongLibraryChunk(connection, Array.Empty<byte>(), true, true);
                return;
            }

            // Build all hash bytes
            byte[] allHashes = new byte[totalSongs * hashSize];
            for (int i = 0; i < totalSongs; i++)
            {
                var hash = hashList[i];
                var hashSpan = System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                    System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref hash, 1));
                hashSpan.Slice(0, hashSize).CopyTo(allHashes.AsSpan(i * hashSize, hashSize));
            }

            // Send in chunks
            int offset = 0;
            int chunkIndex = 0;
            int bytesPerChunk = SONG_HASHES_PER_CHUNK * hashSize;

            while (offset < allHashes.Length)
            {
                int remaining = allHashes.Length - offset;
                int chunkSize = Math.Min(bytesPerChunk, remaining);

                byte[] chunk = new byte[chunkSize];
                Array.Copy(allHashes, offset, chunk, 0, chunkSize);

                bool isFirst = chunkIndex == 0;
                bool isFinal = offset + chunkSize >= allHashes.Length;

                SendSongLibraryChunk(connection, chunk, isFirst, isFinal);

                offset += chunkSize;
                chunkIndex++;
            }

            Debug.Log($"[SongLibrarySyncHandler] Client: Sent {chunkIndex} song library chunks ({totalSongs} songs)");
        }

        private static void SendSongLibraryChunk(INetConnection connection, byte[] hashData, bool isFirstChunk, bool isFinalChunk)
        {
            byte[] message = SongLibraryBinaryPackets.BuildSongLibraryChunkPacket(hashData, isFirstChunk, isFinalChunk);
            connection.Send(message, ChannelType.ReliableOrdered);
        }

        #endregion

        #region State Management

        /// <summary>
        /// Resets all song library sync state.
        /// </summary>
        public void Reset()
        {
            _playerSongLibraries.Clear();
            _playersPendingSongSync.Clear();
            _sharedSongHashes = null;
            _songLibraryUploaded = false;
            _lastUploadedSongVersion = -1;
            _sharedSongSyncComplete = true;
            MultiplayerSongFilter.ClearSharedSongs();
        }

        private void UpdateSyncState()
        {
            bool isComplete = _playersPendingSongSync.Count == 0;

            if (isComplete != _sharedSongSyncComplete)
            {
                _sharedSongSyncComplete = isComplete;
                OnSyncStateChanged?.Invoke(isComplete);
            }
        }

        /// <summary>
        /// Updates the host's local song filter with shared songs.
        /// </summary>
        public void UpdateHostFilter()
        {
            if (_sharedSongHashes != null)
            {
                MultiplayerSongFilter.SetSharedSongs(_sharedSongHashes);
            }
            else
            {
                MultiplayerSongFilter.ClearSharedSongs();
            }
        }

        public void Dispose()
        {
            Reset();
        }

        #endregion
    }
}
