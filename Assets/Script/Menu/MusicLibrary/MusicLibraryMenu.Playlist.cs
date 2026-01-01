using System;
using System.Collections.Generic;
using System.Linq;
using YARG.Core.Input;
using YARG.Localization;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YARG.Networking.Abstraction;
using YARG.Playlists;
using YARG.Player;
using YARG.Settings;
using YARG.Song;

namespace YARG.Menu.MusicLibrary
{
    public partial class MusicLibraryMenu
    {
        // LiteNet setlist as a Playlist for compatibility
        private Playlist _liteNetPlaylist = new(true);
        private bool _subscribedToLiteNetSetlist = false;

        public Playlist ShowPlaylist
        {
            get
            {
                // Check LiteNet networking
                var networkingService = NetworkingServiceFactory.Instance;
                if (networkingService is LiteNetNetworkingAdapter liteNetAdapter && liteNetAdapter.IsNetworkActive)
                {
                    // Subscribe to setlist updates if not already subscribed
                    if (!_subscribedToLiteNetSetlist)
                    {
                        liteNetAdapter.OnSetlistUpdated -= OnLiteNetSetlistUpdated;
                        liteNetAdapter.OnSetlistUpdated += OnLiteNetSetlistUpdated;
                        liteNetAdapter.OnSetlistSongAdded -= OnLiteNetSongAdded;
                        liteNetAdapter.OnSetlistSongAdded += OnLiteNetSongAdded;
                        liteNetAdapter.OnSetlistSongRemoved -= OnLiteNetSongRemoved;
                        liteNetAdapter.OnSetlistSongRemoved += OnLiteNetSongRemoved;
                        _subscribedToLiteNetSetlist = true;
                    }
                    
                    // Build Playlist from LiteNet setlist
                    SyncLiteNetPlaylist(liteNetAdapter);
                    return _liteNetPlaylist;
                }
                
                // Fallback to local playlist (single player)
                return _localShowPlaylist;
            }
            set
            {
                var networkingService = NetworkingServiceFactory.Instance;
                if (networkingService is LiteNetNetworkingAdapter liteNetAdapter && liteNetAdapter.IsNetworkActive)
                {
                    // For LiteNet, we manage setlist separately via API
                    UnityEngine.Debug.Log("[MusicLibraryMenu] Setting ShowPlaylist for LiteNet - syncing to setlist");
                    return;
                }
                
                _localShowPlaylist = value;
            }
        }
        
        private void SyncLiteNetPlaylist(LiteNetNetworkingAdapter liteNetAdapter)
        {
            // Clear and rebuild _liteNetPlaylist from the current setlist
            _liteNetPlaylist.SongHashes.Clear();
            foreach (var hash in liteNetAdapter.SetlistSongHashes)
            {
                var hashWrapper = YARG.Core.Song.HashWrapper.FromString(hash);
                _liteNetPlaylist.SongHashes.Add(hashWrapper);
            }
        }
        
        private void OnLiteNetSetlistUpdated()
        {
            UnityEngine.Debug.Log($"[MusicLibraryMenu] LiteNet setlist updated - refreshing UI and navigation");
            // Refresh the UI when setlist changes
            Refresh();
            // Update navigation scheme since button options depend on setlist state
            SetNavigationScheme(true);
        }
        
        private void OnLiteNetSongAdded(string playerName, string songName, string artistName)
        {
            // Show toast notification when a song is added
            string message = string.IsNullOrEmpty(artistName) 
                ? $"{playerName} added \"{songName}\" to the setlist"
                : $"{playerName} added \"{songName}\" by {artistName}";
            ToastManager.ToastInformation(message);
            UnityEngine.Debug.Log($"[MusicLibraryMenu] {message}");
        }
        
        private void OnLiteNetSongRemoved(string playerName, string songName, string artistName)
        {
            // Show toast notification when a song is removed
            string message = string.IsNullOrEmpty(artistName) 
                ? $"{playerName} removed \"{songName}\" from the setlist"
                : $"{playerName} removed \"{songName}\" by {artistName}";
            ToastManager.ToastInformation(message);
            UnityEngine.Debug.Log($"[MusicLibraryMenu] {message}");
        }

        private Playlist _localShowPlaylist = new(true);

        // Helper methods for multiplayer show playlist management
        public void AddSongToMultiplayerShow(string songHash)
        {
            // Get the player's name and song info
            string playerName = "Unknown";
            if (PlayerContainer.Players.Count > 0)
            {
                playerName = PlayerContainer.Players[0].Profile.Name;
            }
            string displayName = songHash;
            string displayArtist = string.Empty;
            var hashWrapper = YARG.Core.Song.HashWrapper.FromString(songHash);
            if (SongContainer.SongsByHash.TryGetValue(hashWrapper, out var songList) && songList.Count > 0)
            {
                displayName = songList[0].Name;
                displayArtist = songList[0].Artist;
            }
            
            var networkingService = NetworkingServiceFactory.Instance;
            if (networkingService is LiteNetNetworkingAdapter liteNetAdapter && liteNetAdapter.IsNetworkActive)
            {
                // Check if song is already in the setlist
                if (liteNetAdapter.IsInSetlist(songHash))
                {
                    UnityEngine.Debug.Log($"[MusicLibraryMenu] Song {songHash} already in LiteNet setlist, skipping add");
                    return;
                }
                
                UnityEngine.Debug.Log($"[MusicLibraryMenu] Adding song to LiteNet setlist: {displayName} by {displayArtist}");
                liteNetAdapter.RequestAddToSetlist(songHash, playerName, displayName, displayArtist);
            }
            else
            {
                // Single player - add to local playlist
                ShowPlaylist.AddSong(hashWrapper);
            }
        }

        public void RemoveSongFromMultiplayerShow(string songHash)
        {
            // Get the player's name and song info
            string playerName = "Unknown";
            if (PlayerContainer.Players.Count > 0)
            {
                playerName = PlayerContainer.Players[0].Profile.Name;
            }
            string displayName = songHash;
            string displayArtist = string.Empty;
            var hashWrapper = YARG.Core.Song.HashWrapper.FromString(songHash);
            if (SongContainer.SongsByHash.TryGetValue(hashWrapper, out var songList) && songList.Count > 0)
            {
                displayName = songList[0].Name;
                displayArtist = songList[0].Artist;
            }
            
            var networkingService = NetworkingServiceFactory.Instance;
            if (networkingService is LiteNetNetworkingAdapter liteNetAdapter && liteNetAdapter.IsNetworkActive)
            {
                UnityEngine.Debug.Log($"[MusicLibraryMenu] Removing song from LiteNet setlist: {displayName}");
                liteNetAdapter.RequestRemoveFromSetlist(songHash, playerName, displayName, displayArtist);
            }
            else
            {
                // Single player - remove from local playlist
                ShowPlaylist.RemoveSong(hashWrapper);
            }
        }

        public void StartMultiplayerShow()
        {
            var networkingService = NetworkingServiceFactory.Instance;
            if (networkingService is LiteNetNetworkingAdapter liteNetAdapter && liteNetAdapter.IsNetworkActive)
            {
                // Use the unified HasHostAuthority check and StartShow method
                // This works the same for in-game hosting and dedicated server designated hosts
                if (liteNetAdapter.HasHostAuthority)
                {
                    UnityEngine.Debug.Log("[MusicLibraryMenu] Starting show via unified StartShow (host/designated host)");
                    liteNetAdapter.StartShow();
                }
                else
                {
                    UnityEngine.Debug.Log("[MusicLibraryMenu] Only the host can start the show");
                    ToastManager.ToastWarning("Only the host can start the show");
                }
            }
            else
            {
                // Single player mode - start directly
                if (ShowPlaylist.Count > 0)
                {
                    GlobalVariables.State.PlayingAShow = true;
                    GlobalVariables.State.ShowSongs = ShowPlaylist.ToList();
                    GlobalVariables.State.CurrentSong = GlobalVariables.State.ShowSongs.First();
                    GlobalVariables.State.ShowIndex = 0;
                    MenuManager.Instance.PushMenu(MenuManager.Menu.DifficultySelect);
                }
            }
        }

        private List<ViewType> CreatePlaylistSelectViewList()
        {
            SongCategory[] emptyCategory = Array.Empty<SongCategory>();
            int id = BACK_ID + 1;
            var list = new List<ViewType>
            {
                new ButtonViewType(Localize.Key("Menu.MusicLibrary.Back"),
                    "MusicLibraryIcons[Back]", () =>
                    {
                        SelectedPlaylist = null;
                        MenuState = MenuState.Library;
                        Refresh();
                    }, BACK_ID)
            };

            list.Add(new ButtonViewType("YARG", "MusicLibraryIcons[Playlists]", () => { }));

            // Favorites is always on top
            list.Add(new PlaylistViewType(
                Localize.Key("Menu.MusicLibrary.Favorites"),
                PlaylistContainer.FavoritesPlaylist,
                () =>
                {
                    SelectedPlaylist = PlaylistContainer.FavoritesPlaylist;
                    MenuState = MenuState.Playlist;
                    Refresh();
                }, PLAYLIST_ID));

            list.Add(new ButtonViewType(Localize.Key("Menu.MusicLibrary.YourPlaylists"),
                "MusicLibraryIcons[Playlists]", () => { }));

            // Add the setlist "playlist" if there are any songs currently in it
            if (ShowPlaylist.Count > 0)
            {
                list.Add(new PlaylistViewType(Localize.Key("Menu.MusicLibrary.CurrentSetlist"), ShowPlaylist,
                    () =>
                    {
                        SelectedPlaylist = ShowPlaylist;
                        MenuState = MenuState.Playlist;
                        Refresh();
                    }, id));
                id++;
            }

            // Add any other user defined playlists
            foreach (var playlist in PlaylistContainer.Playlists)
            {
                list.Add(new PlaylistViewType(playlist.Name, playlist, () =>
                {
                    SelectedPlaylist = playlist;
                    MenuState = MenuState.Playlist;
                    Refresh();
                }, id));
                id++;
            }

            return list;
        }

        private List<ViewType> CreatePlaylistViewList()
        {
            SetNavigationScheme(true);
            var list = new List<ViewType>
            {
                new ButtonViewType(Localize.Key("Menu.MusicLibrary.Back"),
                    "MusicLibraryIcons[Back]", ExitPlaylistView, BACK_ID)
            };

            // If `_sortedSongs` is null, then this function is being called during very first initialization,
            // which means the song list hasn't been constructed yet.
            if (_sortedSongs is null || SongContainer.Count <= 0 ||
                !_sortedSongs.Any(section => section.Songs.Length > 0))
            {
                return list;
            }

            bool allowdupes = SettingsManager.Settings.AllowDuplicateSongs.Value;
            foreach (var section in _sortedSongs)
            {
                list.Add(new SortHeaderViewType(
                    section.Category.ToUpperInvariant(),
                    section.Songs.Length,
                    section.CategoryGroup));

                foreach (var song in section.Songs)
                {
                    if (allowdupes || !song.IsDuplicate)
                    {
                        list.Add(new SongViewType(this, song));
                    }
                }
            }

            CalculateCategoryHeaderIndices(list);
            return list;
        }

        private List<ViewType> CreateShowViewList()
        {
            var list = new List<ViewType>
            {
                new ButtonViewType(Localize.Key("Menu.MusicLibrary.Back"),
                    "MusicLibraryIcons[Back]", LeaveShowMode, BACK_ID),
                new ButtonViewType("Show Setlist", "MusicLibraryIcons[Playlists]", () => { })
            };

            foreach (var song in ShowPlaylist.ToList())
            {
                list.Add(new SongViewType(this, song));
            }

            return list;
        }

        private void SetShowNavigationScheme(bool reset = false)
        {
            if (reset)
            {
                Navigator.Instance.PopScheme();
            }

            _moreOptionsActivationTimer = MORE_OPTIONS_ACTIVATION_DELAY;
            
            // Check if we're the host (only host can start the show)
            // Use HasHostAuthority for unified host detection across in-game hosting and dedicated servers
            bool isHost = false;
            var networkingService = NetworkingServiceFactory.Instance;
            if (networkingService is LiteNetNetworkingAdapter liteNetAdapter && liteNetAdapter.IsNetworkActive)
            {
                isHost = liteNetAdapter.HasHostAuthority;
            }
            else
            {
                // Single player - always can start
                isHost = true;
            }
            
            var entries = new List<NavigationScheme.Entry>
            {
                new NavigationScheme.Entry(MenuAction.Up, "Menu.Common.Up",
                    ctx =>
                    {
                        if (IsButtonHeldByPlayer(ctx.Player, MenuAction.Orange, true))
                        {
                            GoToPreviousSection();
                        }
                        else
                        {
                            SetWrapAroundState(!ctx.IsRepeat);
                            SelectedIndex--;
                        }
                    }),
                new NavigationScheme.Entry(MenuAction.Down, "Menu.Common.Down",
                    ctx =>
                    {
                        if (IsButtonHeldByPlayer(ctx.Player, MenuAction.Orange, true))
                        {
                            GoToNextSection();
                        }
                        else
                        {
                            SetWrapAroundState(!ctx.IsRepeat);
                            SelectedIndex++;
                        }
                    }),
                new NavigationScheme.Entry(MenuAction.Green, "Menu.Common.Confirm",
                    () => CurrentSelection?.PrimaryButtonClick()),
                new NavigationScheme.Entry(MenuAction.Red, "Menu.Common.Back", LeaveShowMode),
                new NavigationScheme.Entry(MenuAction.Orange, "Menu.MusicLibrary.MoreOptions",
                    OnButtonHit, OnButtonRelease),
            };
            
            // Only add "Start Show" button for host
            if (isHost)
            {
                entries.Insert(entries.Count - 1, new NavigationScheme.Entry(MenuAction.Blue, "Menu.MusicLibrary.StartShow",
                    OnPlayShowHit));
            }

            Navigator.Instance.PushScheme(new NavigationScheme(entries, false));
        }

        private void ExitPlaylistView()
        {
            SelectedPlaylist = null;
            MenuState = MenuState.PlaylistSelect;
            SetNavigationScheme(true);
            Refresh();

            // Select playlist button
            // TODO: Fix this to select the playlist we entered from, not favorites
            SetIndexTo(i => i is ButtonViewType { ID: PLAYLIST_ID });
        }

        private void ExitPlaylistSelect()
        {
            MenuState = MenuState.Library;
            Refresh();

            SetIndexTo(i => i is ButtonViewType { ID: PLAYLIST_ID });
        }

        private void EnterShowMode()
        {
            // Save the current selected index if we're in the main library
            if (MenuState == MenuState.Library)
            {
                _mainLibraryIndex = SelectedIndex;
            }

            // Update the navigation scheme
            SetShowNavigationScheme();

            // Display the show screen
    		SelectedPlaylist = ShowPlaylist;
            MenuState = MenuState.Show;
            Refresh();

                if (ShowPlaylist.Count == 0)
                {
                    DialogManager.Instance.ShowSongPickerDialog("Pick Your Poison", this);
                }
        }

        private void LeaveShowMode()
        {
            SelectedPlaylist = null;
            // Don't clear the setlist - users may want to go back to library to add more songs
            // ShowPlaylist.Clear();

            // Pop the navigation scheme
            Navigator.Instance.PopScheme();
            // We have to reset the navigation scheme so the help bar has the correct yellow button text
            // in the case that we are leaving show mode with a playlist that has entries
            MenuState = MenuState.Library;
            SetNavigationScheme(true);

            // Back to library
            Refresh();

            // Restore the main library index if it is valid
            if (_mainLibraryIndex != -1)
            {
                SelectedIndex = _mainLibraryIndex;
            }
            else
            {
                SetIndexTo(i => i is ButtonViewType { ID: RANDOM_SONG_ID });
            }
        }

        private void StartSetlist()
        {
            if (ShowPlaylist.Count == 0)
            {
                ToastManager.ToastError("Add songs to the setlist first!");
                return;
            }
            
            if (PlayerContainer.Players.Count == 0)
            {
                ToastManager.ToastError("No players available!");
                return;
            }
            
            var networkingService = NetworkingServiceFactory.Instance;
            bool isMultiplayer = networkingService is LiteNetNetworkingAdapter liteNetAdapter && liteNetAdapter.IsNetworkActive;
            
            if (isMultiplayer)
            {
                var adapter = (LiteNetNetworkingAdapter)networkingService;
                // In multiplayer, use unified HasHostAuthority check
                if (adapter.HasHostAuthority)
                {
                    UnityEngine.Debug.Log($"[MusicLibraryMenu] Host starting show with {ShowPlaylist.Count} songs from MusicLibrary");
                    ToastManager.ToastInformation($"Starting show with {ShowPlaylist.Count} songs!");
                    StartMultiplayerShow();
                }
                else
                {
                    ToastManager.ToastWarning("Only the host can start the show");
                }
                return;
            }
            
            // Single player mode
            if (MenuState == MenuState.Library)
            {
                _mainLibraryIndex = SelectedIndex;
            }

            GlobalVariables.State.PlayingAShow = true;
            GlobalVariables.State.ShowSongs = ShowPlaylist.ToList();
            GlobalVariables.State.CurrentSong = GlobalVariables.State.ShowSongs.First();
            GlobalVariables.State.ShowIndex = 0;
            MenuManager.Instance.PushMenu(MenuManager.Menu.DifficultySelect);
        }

        private void AddToPlaylist()
        {
            var networkingService = NetworkingServiceFactory.Instance;
            bool isMultiplayer = networkingService is LiteNetNetworkingAdapter liteNetAdapter && liteNetAdapter.IsNetworkActive;
            
            if (CurrentSelection is PlaylistViewType playlist)
            {
                if (playlist.Playlist.SongHashes.Count == 0)
                {
                    ToastManager.ToastError(Localize.Key("Menu.MusicLibrary.EmptyPlaylist"));
                    return;
                }

                if (playlist.Playlist.Ephemeral)
                {
                    // No, we won't add the setlist to itself, thanks
                    ToastManager.ToastError(Localize.Key("Menu.MusicLibrary.CannotAddToSelf"));
                    return;
                }

                var i = 0;

                foreach (var song in playlist.Playlist.ToList())
                {
                    if (isMultiplayer)
                    {
                        AddSongToMultiplayerShow(song.Hash.ToString());
                    }
                    else
                    {
                        ShowPlaylist.AddSong(song);
                    }
                    i++;
                }

                if (i > 0)
                {
                    ToastManager.ToastSuccess(Localize.KeyFormat("Menu.MusicLibrary.PlaylistAddedToSet", i));
                }
                else
                {
                    ToastManager.ToastWarning(Localize.Key("Menu.MusicLibrary.NoSongsInPlaylist"));
                }

                if (i > 0 && ShowPlaylist.Count == i)
                {
                    // We need to rebuild the navigation scheme the first time we add song(s)
                    SetNavigationScheme(true);
                }

                // If we are in the playlist view, we need to refresh the view
                if (MenuState == MenuState.PlaylistSelect)
                {
                    RefreshAndReselect();
                }

                return;
            }

            if (CurrentSelection is SongViewType selection)
            {
                if (isMultiplayer)
                {
                    AddSongToMultiplayerShow(selection.SongEntry.Hash.ToString());
                }
                else
                {
                    ShowPlaylist.AddSong(selection.SongEntry);
                }
                
                if (ShowPlaylist.Count == 1)
                {
                    // We need to rebuild the navigation scheme after adding the first song
                    SetNavigationScheme(true);
                }

                ToastManager.ToastSuccess(Localize.Key("Menu.MusicLibrary.AddedToSet"));
            }
        }

        private void QuickStartShow()
        {
            // Quick start for multiplayer - starts the show immediately if there are songs
            var networkingService = NetworkingServiceFactory.Instance;
            bool isMultiplayer = networkingService is LiteNetNetworkingAdapter liteNetAdapter && liteNetAdapter.IsNetworkActive;
            
            if (!isMultiplayer)
            {
                // Fallback to regular add to playlist behavior in single player
                AddToPlaylist();
                return;
            }
            
            if (ShowPlaylist.Count == 0)
            {
                ToastManager.ToastWarning("Add songs to the setlist first!");
                return;
            }
            
            if (PlayerContainer.Players.Count == 0)
            {
                ToastManager.ToastError("No players available!");
                return;
            }
            
            // Only host can start the show
            // Use HasHostAuthority for unified host detection
            var adapter = (LiteNetNetworkingAdapter)networkingService;
            if (!adapter.HasHostAuthority)
            {
                ToastManager.ToastWarning("Only the host can start the show");
                return;
            }
            
            UnityEngine.Debug.Log($"[MusicLibraryMenu] Quick starting show with {ShowPlaylist.Count} songs");
            ToastManager.ToastSuccess($"Starting show with {ShowPlaylist.Count} songs!");
            StartMultiplayerShow();
        }

        private void OnPlayShowHit()
        {
            if (ShowPlaylist.Count > 0 && PlayerContainer.Players.Count > 0)
            {
                var networkingService = NetworkingServiceFactory.Instance;
                
                if (networkingService is LiteNetNetworkingAdapter liteNetAdapter && liteNetAdapter.IsNetworkActive)
                {
                    // In LiteNet multiplayer, use unified HasHostAuthority check
                    if (liteNetAdapter.HasHostAuthority)
                    {
                        StartMultiplayerShow();
                    }
                    else
                    {
                        ToastManager.ToastWarning("Only the host can start the show");
                    }
                    return;
                }
                
                // Single player mode
                GlobalVariables.State.PlayingAShow = true;
                GlobalVariables.State.ShowSongs = ShowPlaylist.ToList();
                GlobalVariables.State.CurrentSong = GlobalVariables.State.ShowSongs.First();
                GlobalVariables.State.ShowIndex = 0;

                // Make sure we don't come back to play a show after show has been played
                LeaveShowMode();

                MenuManager.Instance.PushMenu(MenuManager.Menu.DifficultySelect);
            }
        }

        private void MovePlaylistEntryUp()
        {
            if (CurrentSelection is SongViewType selection)
            {
                SelectedPlaylist.MoveSongUp(selection.SongEntry);
                Refresh();
            }
        }

        private void MovePlaylistEntryDown()
        {
            if (CurrentSelection is SongViewType selection)
            {
                SelectedPlaylist.MoveSongDown(selection.SongEntry);
                Refresh();
            }
        }
    }
}