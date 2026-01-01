using UnityEngine;
using YARG.Core.Input;
using YARG.Menu.Data;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YARG.Networking.Abstraction;

namespace YARG.Gameplay.HUD
{
    /// <summary>
    /// Pause menu for online multiplayer gameplay.
    /// Host can restart, toggle practice, and return all players to library.
    /// Clients can leave lobby (disconnecting all players).
    /// Uses LiteNet networking backend.
    /// </summary>
    public class MultiplayerPause : GenericPause
    {
        [Header("UI Elements (Optional - for button visibility)")]
        [SerializeField]
        private GameObject _restartButton;
        [SerializeField]
        private GameObject _backToLibraryButton;
        [SerializeField]
        private GameObject _leaveLobbyButton;
        
        private bool _isHost;

        protected override void OnEnable()
        {
            // Don't call base.OnEnable() - we'll set up our own navigation scheme
            
            Debug.Log("[MultiplayerPause] OnEnable called");
            
            // Check if LiteNet is active
            bool isNetworkActive = NetworkingServiceFactory.Instance?.IsNetworkActive == true;
            
            // Determine if local user is host
            _isHost = NetworkingServiceFactory.Instance?.IsHosting == true;
            
            Debug.Log($"[MultiplayerPause] Networking: Active={isNetworkActive}, IsHost={_isHost}");
            
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
            Debug.Log($"[MultiplayerPause] Button refs: Restart={_restartButton != null}, BackToLibrary={_backToLibraryButton != null}, LeaveLobby={_leaveLobbyButton != null}");
            
            // Host buttons
            if (_restartButton != null)
                _restartButton.SetActive(_isHost);
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
            
            // Use unified RestartGameplay - works for both in-game hosting and dedicated servers
            var networkService = NetworkingServiceFactory.Instance;
            if (networkService is LiteNetNetworkingAdapter liteNetAdapter)
            {
                liteNetAdapter.RestartGameplay();
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
            
            // For LiteNet, use the unified host action methods
            var networkService = NetworkingServiceFactory.Instance;
            if (networkService is LiteNetNetworkingAdapter liteNetAdapter)
            {
                // Use unified QuitToLibrary which broadcasts to all clients
                liteNetAdapter.QuitToLibrary();
                
                // Also navigate to music library (this clears setlist and updates state)
                liteNetAdapter.NavigateToMusicLibrary();
            }
            
            // Set navigation to full multiplayer stack so back button works correctly
            // Stack will be: MainMenu > OnlineMultiplayer > LobbyRoom > MusicLibrary
            Networking.Abstraction.MenuNavigationHelper.SetMenuNavigationAfterSceneLoad(
                Menu.MenuManager.Menu.OnlineMultiplayer,
                Menu.MenuManager.Menu.LobbyRoom,
                Menu.MenuManager.Menu.MusicLibrary);
            
            // Quit song for host - this will load Menu scene
            PauseMenuManager.Quit();
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
            
            // For LiteNet, leave via the networking service
            // This will trigger OnPlayerLeft on the host, which broadcasts to other clients
            var networkService = NetworkingServiceFactory.Instance;
            if (networkService != null)
            {
                networkService.LeaveLobby();
            }
            
            // Set navigation to LobbyBrowser after loading Menu scene
            // This ensures client goes to the lobby browser where they can join another game
            Networking.Abstraction.MenuNavigationHelper.SetMenuNavigationAfterSceneLoad(
                Menu.MenuManager.Menu.OnlineMultiplayer,
                Menu.MenuManager.Menu.LobbyBrowser);
            
            // Return to menu
            GlobalVariables.Instance.LoadScene(SceneIndex.Menu);
        }
    }
}
