using UnityEngine;
using YARG.Core.Input;
using YARG.Menu.Data;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YARG.Networking;
using YARG.Networking.Abstraction;

namespace YARG.Gameplay.HUD
{
    /// <summary>
    /// Pause menu for online multiplayer gameplay.
    /// Host can restart, toggle practice, and return all players to library.
    /// Clients can leave lobby (disconnecting all players).
    /// Supports both Mirror and LiteNet networking backends.
    /// </summary>
    public class MultiplayerPause : GenericPause
    {
        [Header("UI Elements (Optional - for button visibility)")]
        [SerializeField]
        private GameObject _restartButton;
        [SerializeField]
        private GameObject _togglePracticeButton;
        [SerializeField]
        private GameObject _backToLibraryButton;
        [SerializeField]
        private GameObject _leaveLobbyButton;
        
        private bool _isHost;
        private bool _useLiteNet;

        protected override void OnEnable()
        {
            // Don't call base.OnEnable() - we'll set up our own navigation scheme
            
            Debug.Log("[MultiplayerPause] OnEnable called");
            
            // Determine which networking backend is active
            // NOTE: Check LiteNet first - if LiteNet is active, consider Mirror inactive
            _useLiteNet = NetworkingServiceFactory.Instance?.IsNetworkActive == true;
            bool isMirrorActive = !_useLiteNet && 
                                  YargNetworkManager.Instance != null && 
                                  YargNetworkManager.Instance.isNetworkActive;
            
            // Determine if local user is host
            if (_useLiteNet)
            {
                _isHost = NetworkingServiceFactory.Instance?.IsHosting == true;
            }
            else if (isMirrorActive)
            {
                _isHost = YargNetworkManager.Instance.LocalUserIsHost();
            }
            else
            {
                _isHost = false;
            }
            
            Debug.Log($"[MultiplayerPause] Networking: LiteNet={_useLiteNet}, Mirror={isMirrorActive}, IsHost={_isHost}");
            
            // Show/hide buttons based on role
            UpdateButtonVisibility();
            
            // Create navigation scheme based on role
            var entries = new System.Collections.Generic.List<NavigationScheme.Entry>
            {
                NavigationScheme.Entry.NavigateSelect,
                new NavigationScheme.Entry(MenuAction.Red, "Menu.Common.Back", Back),
                NavigationScheme.Entry.NavigateUp,
                NavigationScheme.Entry.NavigateDown,
            };
            
            // Add role-specific actions
            if (_isHost)
            {
                entries.Add(new NavigationScheme.Entry(MenuAction.Orange, "Back to Library", HostBackToLibrary));
            }
            else
            {
                entries.Add(new NavigationScheme.Entry(MenuAction.Orange, "Leave Lobby", ClientLeaveLobby));
            }
            
            Navigator.Instance.PushScheme(new NavigationScheme(entries, false));
        }

        private void OnDisable()
        {
            Navigator.Instance.PopScheme();
        }
        
        private void UpdateButtonVisibility()
        {
            Debug.Log($"[MultiplayerPause] UpdateButtonVisibility: IsHost={_isHost}");
            Debug.Log($"[MultiplayerPause] Button refs: Restart={_restartButton != null}, Practice={_togglePracticeButton != null}, BackToLibrary={_backToLibraryButton != null}, LeaveLobby={_leaveLobbyButton != null}");
            
            // Host buttons
            if (_restartButton != null)
                _restartButton.SetActive(_isHost);
            if (_togglePracticeButton != null)
                _togglePracticeButton.SetActive(_isHost);
            if (_backToLibraryButton != null)
                _backToLibraryButton.SetActive(_isHost);
                
            // Client buttons
            if (_leaveLobbyButton != null)
                _leaveLobbyButton.SetActive(!_isHost);
        }

        /// <summary>
        /// Host action: Restarts the song for all players.
        /// Called from UI button.
        /// </summary>
        public override void Restart()
        {
            if (!_isHost)
            {
                Debug.LogWarning("[MultiplayerPause] Only host can restart in multiplayer");
                return;
            }
            
            Debug.Log("[MultiplayerPause] Host restarting song for all players");
            
            // Sync all clients to restart
            if (_useLiteNet)
            {
                // Broadcast restart to all clients via LiteNet
                var networkService = NetworkingServiceFactory.Instance;
                if (networkService is LiteNetNetworkingAdapter liteNetAdapter)
                {
                    liteNetAdapter.BroadcastRestartGameplay();
                }
            }
            else if (YargNetworkManager.Instance != null)
            {
                // Send RPC to all clients to reload the gameplay scene (Mirror)
                YargNetworkManager.Instance.RestartMultiplayerGameplay();
            }
            
            // Restart for host
            PauseMenuManager.Restart();
        }
        
        /// <summary>
        /// Host action: Toggles practice mode and restarts for all players.
        /// Called from UI button.
        /// </summary>
        public void HostTogglePractice()
        {
            if (!_isHost)
            {
                Debug.LogWarning("[MultiplayerPause] Only host can toggle practice in multiplayer");
                return;
            }
            
            Debug.Log("[MultiplayerPause] Host toggling practice mode for all players");
            
            // Toggle practice state
            GlobalVariables.State.IsPractice = !GlobalVariables.State.IsPractice;
            
            // Sync practice state to all clients
            if (_useLiteNet)
            {
                // For LiteNet, we'd need to implement practice mode sync
                // For now, just toggle locally (TODO: implement network sync)
                Debug.Log("[MultiplayerPause] LiteNet practice toggle - currently local only");
            }
            else if (YargNetworkManager.Instance != null)
            {
                YargNetworkManager.Instance.SyncPracticeMode(GlobalVariables.State.IsPractice);
                YargNetworkManager.Instance.RestartMultiplayerGameplay();
            }
            
            // Restart for host
            PauseMenuManager.Restart();
        }
        
        /// <summary>
        /// Host action: Brings all players back to music library.
        /// Called from UI button or navigation.
        /// </summary>
        public void HostBackToLibrary()
        {
            if (!_isHost)
            {
                Debug.LogWarning("[MultiplayerPause] Only host can return to library in multiplayer");
                return;
            }
            
            Debug.Log("[MultiplayerPause] Host returning all players to music library");
            
            if (_useLiteNet)
            {
                // For LiteNet, broadcast quit to library to all clients
                var networkService = NetworkingServiceFactory.Instance;
                if (networkService is LiteNetNetworkingAdapter liteNetAdapter)
                {
                    // Use BroadcastQuitToLibrary which is specific to quitting gameplay
                    // This will trigger OnQuitToLibraryRequested on clients
                    liteNetAdapter.BroadcastQuitToLibrary();
                    
                    // Also navigate to music library (this clears setlist and updates state)
                    liteNetAdapter.BroadcastNavigateToMusicLibrary();
                }
                
                // Quit song for host - this will load Menu scene
                PauseMenuManager.Quit();
            }
            else
            {
                // Mirror path
                // Set the navigation target for everyone (host and clients)
                YargNetworkManager.SetMenuNavigationAfterSceneLoad(
                    Menu.MenuManager.Menu.OnlineMultiplayer,
                    Menu.MenuManager.Menu.LobbyRoom,
                    Menu.MenuManager.Menu.MusicLibrary);
                
                // Tell all clients to quit and return to Menu scene
                if (YargNetworkManager.Instance != null)
                {
                    YargNetworkManager.Instance.QuitMultiplayerGameplay();
                }
                
                // Quit song for host - this will load Menu scene
                PauseMenuManager.Quit();
            }
        }
        
        /// <summary>
        /// Client action: Leaves the lobby and disconnects from gameplay.
        /// Only affects this client - other players continue playing.
        /// Called from UI button.
        /// </summary>
        public void ClientLeaveLobby()
        {
            Debug.Log("[MultiplayerPause] ClientLeaveLobby called!");
            
            if (_isHost)
            {
                Debug.LogWarning("[MultiplayerPause] Host should use HostBackToLibrary instead");
                return;
            }
            
            Debug.Log("[MultiplayerPause] Showing leave lobby dialog...");
            
            // Show confirmation dialog
            ShowLeaveLobbyDialog();
        }
        
        private void ShowLeaveLobbyDialog()
        {
            if (DialogManager.Instance == null)
            {
                Debug.LogWarning("[MultiplayerPause] DialogManager.Instance is null");
                // If no dialog manager, just leave directly
                ExecuteClientLeaveLobby();
                return;
            }
            
            var dialog = DialogManager.Instance.ShowMessage(
                "Leave Lobby?",
                "Are you sure you want to leave? Your track will be removed from the game and you will be disconnected.");
            
            dialog.ClearButtons();
            dialog.AddDialogButton("Cancel", MenuData.Colors.BrightButton, () => DialogManager.Instance.ClearDialog());
            dialog.AddDialogButton("Leave", MenuData.Colors.CancelButton, () =>
            {
                DialogManager.Instance.ClearDialog();
                ExecuteClientLeaveLobby();
            });
        }
        
        private void ExecuteClientLeaveLobby()
        {
            Debug.Log("[MultiplayerPause] Client leaving lobby - disconnecting from gameplay");
            
            if (_useLiteNet)
            {
                // For LiteNet, leave via the networking service
                // This will trigger OnPlayerLeft on the host, which broadcasts to other clients
                var networkService = NetworkingServiceFactory.Instance;
                if (networkService != null)
                {
                    networkService.LeaveLobby();
                }
                
                // Return to menu (main menu, not lobby)
                GlobalVariables.Instance.LoadScene(SceneIndex.Menu);
            }
            else
            {
                // Mirror path
                // The disconnect will be handled by the host which will broadcast to other clients
                if (YargNetworkManager.Instance != null)
                {
                    YargNetworkManager.Instance.LeaveLobby();
                }
                else
                {
                    Debug.LogError("[MultiplayerPause] YargNetworkManager.Instance is null!");
                }
            }
        }
    }
}
