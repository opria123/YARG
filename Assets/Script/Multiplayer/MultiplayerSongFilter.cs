using System;
using System.Collections.Generic;
using System.Linq;
using YARG.Core;
using YARG.Core.Logging;
using YARG.Core.Song;
using YARG.Menu.MusicLibrary;
using YARG.Networking.Abstraction;
using YARG.Song;

namespace YARG.Multiplayer
{
    public static class MultiplayerSongFilter
    {
        private static HashSet<HashWrapper>? _sharedSongs;
        private static List<byte>? _incomingBuffer;
        
        /// <summary>
        /// When true, also filter to songs that have parts for all connected profiles.
        /// When false, only filter to songs all users have (ignore parts).
        /// </summary>
        private static bool _requirePartsForAllProfiles = true;

        public static event Action? SharedSongsUpdated;

        public static bool IsActive => _sharedSongs != null;

        public static IReadOnlyCollection<HashWrapper>? SharedSongs => _sharedSongs;
        
        /// <summary>
        /// Gets or sets whether the filter requires songs to have parts for all connected profiles.
        /// When true (SharedSongsOnly ON): songs must have parts for all profiles.
        /// When false (SharedSongsOnly OFF): songs just need to be owned by all users.
        /// </summary>
        public static bool RequirePartsForAllProfiles
        {
            get => _requirePartsForAllProfiles;
            set
            {
                if (_requirePartsForAllProfiles != value)
                {
                    _requirePartsForAllProfiles = value;
                    // Trigger a refresh when the setting changes
                    MusicLibraryMenu.SetReload(MusicLibraryReloadState.Partial);
                    SharedSongsUpdated?.Invoke();
                }
            }
        }

        public static void BeginSharedSongsUpload()
        {
            _incomingBuffer = new List<byte>();
        }

        public static void AppendSharedSongsChunk(byte[] chunk)
        {
            if (chunk == null || chunk.Length == 0)
            {
                return;
            }

            _incomingBuffer ??= new List<byte>();
            _incomingBuffer.AddRange(chunk);
        }

        public static void CommitSharedSongsUpload()
        {
            if (_incomingBuffer == null)
            {
                SetSharedSongs(Array.Empty<HashWrapper>());
                return;
            }

            var buffer = _incomingBuffer;
            _incomingBuffer = null;

            int hashSize = HashWrapper.HASH_SIZE_IN_BYTES;
            if (buffer.Count % hashSize != 0)
            {
                YargLogger.LogWarning("Received shared song data with invalid length; clearing filter.");
                SetSharedSongs(Array.Empty<HashWrapper>());
                return;
            }

            var bytes = buffer.ToArray();
            var hashes = new HashSet<HashWrapper>(bytes.Length / hashSize);
            for (int offset = 0; offset < bytes.Length; offset += hashSize)
            {
                var hash = HashWrapper.Create(new ReadOnlySpan<byte>(bytes, offset, hashSize));
                hashes.Add(hash);
            }

            SetSharedSongs(hashes);
        }

        public static void SetSharedSongs(IEnumerable<HashWrapper> hashes)
        {
            if (hashes is HashSet<HashWrapper> hashSet)
            {
                _sharedSongs = new HashSet<HashWrapper>(hashSet);
            }
            else
            {
                _sharedSongs = new HashSet<HashWrapper>(hashes);
            }

            MusicLibraryMenu.SetReload(MusicLibraryReloadState.Partial);
            SharedSongsUpdated?.Invoke();
        }

        public static void ClearSharedSongs()
        {
            if (_sharedSongs == null && _incomingBuffer == null)
            {
                return;
            }

            _sharedSongs = null;
            _incomingBuffer = null;
            MusicLibraryMenu.SetReload(MusicLibraryReloadState.Partial);
            SharedSongsUpdated?.Invoke();
        }

        public static bool IsSongAllowed(SongEntry song)
        {
            // First check: song must be in shared songs list (all users have it)
            if (_sharedSongs != null && !_sharedSongs.Contains(song.Hash))
            {
                return false;
            }
            
            // Second check: if RequirePartsForAllProfiles is enabled, check instruments
            if (_requirePartsForAllProfiles && _sharedSongs != null)
            {
                return SongHasPartsForAllProfiles(song);
            }
            
            return true;
        }

        public static SongEntry[] FilterSongs(IEnumerable<SongEntry> songs)
        {
            if (_sharedSongs == null)
            {
                return songs as SongEntry[] ?? songs.ToArray();
            }

            return songs.Where(IsSongAllowed).ToArray();
        }

        public static SongCategory[] FilterCategories(IEnumerable<SongCategory> categories)
        {
            if (_sharedSongs == null)
            {
                return categories as SongCategory[] ?? categories.ToArray();
            }

            var filteredCategories = new List<SongCategory>();
            foreach (var category in categories)
            {
                var filteredSongs = category.Songs.Where(IsSongAllowed).ToArray();
                if (filteredSongs.Length > 0)
                {
                    filteredCategories.Add(new SongCategory(category.Category, filteredSongs, category.CategoryGroup));
                }
            }

            return filteredCategories.ToArray();
        }
        
        /// <summary>
        /// Triggers a refresh of the song filter. Call this when player state changes
        /// (player joins, leaves, or changes instrument).
        /// </summary>
        public static void RefreshFilter()
        {
            if (!IsActive || !_requirePartsForAllProfiles)
            {
                return;
            }
            
            MusicLibraryMenu.SetReload(MusicLibraryReloadState.Partial);
            SharedSongsUpdated?.Invoke();
        }
        
        /// <summary>
        /// Checks if a song has playable parts for all connected profiles that are not sitting out.
        /// </summary>
        private static bool SongHasPartsForAllProfiles(SongEntry song)
        {
            var networkService = NetworkingServiceFactory.Instance;
            if (networkService == null || !networkService.IsNetworkActive)
            {
                // Not in a network session, allow all songs
                return true;
            }
            
            var connectedPlayers = networkService.GetConnectedPlayers();
            if (connectedPlayers == null || connectedPlayers.Count == 0)
            {
                return true;
            }
            
            // Check each connected profile
            foreach (var connectionEntry in connectedPlayers)
            {
                foreach (var playerData in connectionEntry.Value)
                {
                    // Skip players who are sitting out
                    if (playerData.SittingOut)
                    {
                        continue;
                    }
                    
                    // Get the instrument for this profile
                    int instrumentValue = playerData.Instrument;
                    if (instrumentValue < 0)
                    {
                        // No instrument selected yet, skip this profile
                        continue;
                    }
                    
                    var instrument = (Instrument)instrumentValue;
                    
                    // Check if the song has this instrument
                    if (!SongHasInstrumentOrEquivalent(song, instrument))
                    {
                        return false;
                    }
                }
            }
            
            return true;
        }
        
        /// <summary>
        /// Checks if a song has a specific instrument or an equivalent (e.g., ProDrums ↔ FiveLaneDrums).
        /// </summary>
        private static bool SongHasInstrumentOrEquivalent(SongEntry song, Instrument instrument)
        {
            // Direct check
            if (song.HasInstrument(instrument))
            {
                return true;
            }
            
            // Check for equivalent instruments
            return instrument switch
            {
                // Drums equivalences
                Instrument.ProDrums => song.HasInstrument(Instrument.FourLaneDrums) || 
                                       song.HasInstrument(Instrument.FiveLaneDrums),
                Instrument.FourLaneDrums => song.HasInstrument(Instrument.ProDrums) || 
                                            song.HasInstrument(Instrument.FiveLaneDrums),
                Instrument.FiveLaneDrums => song.HasInstrument(Instrument.ProDrums) || 
                                            song.HasInstrument(Instrument.FourLaneDrums),
                
                // Vocals equivalences
                Instrument.Vocals => song.HasInstrument(Instrument.Harmony),
                Instrument.Harmony => song.HasInstrument(Instrument.Vocals),
                
                _ => false
            };
        }
    }
}
