using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using YARG.Core.Input;
using YARG.Core.Logging;
using YARG.Networking;
using YARG.Networking.Abstraction;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YARG.Localization;

namespace YARG.Menu.Multiplayer
{
    /// <summary>
    /// Main menu for online multiplayer.
    /// Handles navigation between lobby browser, create lobby, and direct connect.
    /// </summary>
    public class OnlineMultiplayerMenu : MonoBehaviour
    {
        [Header("UI References")]
        [SerializeField] private TextMeshProUGUI headerText;
        [SerializeField] private TextMeshProUGUI connectionStatusText;
        
        [Header("Dialogs")]
        [SerializeField] private GameObject createLobbyDialog;
        [SerializeField] private GameObject directConnectDialog;

        private void Start()
        {
            // Subscribe to network events
            var networkService = NetworkingServiceFactory.Instance;
            if (networkService != null)
            {
                networkService.OnLobbyCreated += OnLobbyCreated;
                networkService.OnLobbyJoined += OnLobbyJoined;
                networkService.OnLobbyLeft += OnLobbyLeft;
                networkService.OnNetworkError += OnNetworkError;
            }

            UpdateConnectionStatus();
        }

        private void OnEnable()
        {
            // Reset join flag when menu is reopened
            _hasJoinedLobby = false;
            
            // Set up navigation scheme (following YARG's pattern)
            Navigator.Instance.PushScheme(new NavigationScheme(new()
            {
                NavigationScheme.Entry.NavigateSelect,
                NavigationScheme.Entry.NavigateUp,
                NavigationScheme.Entry.NavigateDown,
                new NavigationScheme.Entry(MenuAction.Red, "Menu.Common.Back", OnBackClicked),
            }, true));
        }

        private void OnDisable()
        {
            Navigator.Instance?.PopScheme();
        }

        private void OnDestroy()
        {
            // Unsubscribe from events
            var networkService = NetworkingServiceFactory.Instance;
            if (networkService != null)
            {
                networkService.OnLobbyCreated -= OnLobbyCreated;
                networkService.OnLobbyJoined -= OnLobbyJoined;
                networkService.OnLobbyLeft -= OnLobbyLeft;
                networkService.OnNetworkError -= OnNetworkError;
            }
        }

        // Button callbacks (to be connected in Unity Inspector via UI button prefabs)
        public void OnFindLobbyClicked()
        {
            // Open the lobby browser menu
            YargLogger.LogInfo("[OnlineMultiplayerMenu] Find Lobby clicked");
            MenuManager.Instance.PushMenu(MenuManager.Menu.LobbyBrowser);
        }

        public void OnCreateLobbyClicked()
        {
            if (createLobbyDialog != null)
            {
                YargLogger.LogInfo("[OnlineMultiplayerMenu] Create Lobby clicked");
                createLobbyDialog.SetActive(true);
            }
            else
            {
                YargLogger.LogWarning("[OnlineMultiplayerMenu] Create Lobby Dialog not assigned in Inspector!");
            }
        }

        public void OnDirectConnectClicked()
        {
            if (directConnectDialog != null)
            {
                YargLogger.LogInfo("[OnlineMultiplayerMenu] Direct Connect clicked");
                directConnectDialog.SetActive(true);
            }
            else
            {
                YargLogger.LogWarning("[OnlineMultiplayerMenu] Direct Connect Dialog not assigned in Inspector!");
            }
        }

        public void OnBackClicked()
        {
            // Clean up any lingering network state before leaving the multiplayer menu
            // This prevents issues where probe connections or other network activity
            // leaves the system thinking we're in multiplayer mode
            CleanupNetworkState();
            
            MenuManager.Instance.PopMenu();
        }
        
        /// <summary>
        /// Cleans up any lingering network state when leaving the multiplayer menu.
        /// This is important to prevent the game from thinking we're still in multiplayer
        /// when the user goes to QuickPlay after visiting the lobby browser.
        /// </summary>
        private void CleanupNetworkState()
        {
            // Only clean up if we're NOT actually in a lobby
            var networkService = NetworkingServiceFactory.Instance;
            if (networkService != null && networkService.CurrentLobby != null)
            {
                // We're in a lobby, don't clean up
                return;
            }
            
            // Clean up Mirror state - probe connections may leave NetworkClient active
            if (YargNetworkManager.Instance != null && !YargNetworkManager.Instance.LocalUserIsHost())
            {
                // Only disconnect if we're not hosting and there's an active client
                if (Mirror.NetworkClient.active || Mirror.NetworkClient.isConnected)
                {
                    Debug.Log("[OnlineMultiplayerMenu] Cleaning up lingering Mirror client connection");
                    Mirror.NetworkClient.Disconnect();
                }
            }
            
            // Reset any multiplayer-related global state
            GlobalVariables.State.IsPractice = false;
            GlobalVariables.State.PlayingAShow = false;
            GlobalVariables.State.ShowSongs?.Clear();
            GlobalVariables.State.ShowIndex = 0;
            
            Debug.Log("[OnlineMultiplayerMenu] Network state cleaned up on back navigation");
        }

        private void OnLobbyCreated(LobbyInfo lobby)
        {
            YargLogger.LogInfo($"[OnlineMultiplayerMenu] Lobby created: {lobby.LobbyName}");
            UpdateConnectionStatus();
            
            // Don't show dialog or navigate here - the host's own OnLobbyJoined will handle it
            // This prevents double-navigation and dialog spam
        }

        private bool _hasJoinedLobby = false;

        private void OnLobbyJoined(LobbyInfo lobby)
        {
            Debug.Log($"[OnlineMultiplayerMenu] OnLobbyJoined called with lobby: {lobby?.LobbyName ?? "null"}");
            
            // Prevent multiple calls (Mirror can trigger this multiple times)
            if (_hasJoinedLobby)
            {
                YargLogger.LogInfo("[OnlineMultiplayerMenu] Already joined lobby, ignoring duplicate call");
                return;
            }
            
            _hasJoinedLobby = true;
            YargLogger.LogInfo($"[OnlineMultiplayerMenu] Joined lobby: {lobby.LobbyName}");
            UpdateConnectionStatus();
            
            // Dismiss any connecting dialogs
            if (DialogManager.Instance != null && DialogManager.Instance.IsDialogShowing)
            {
                YargLogger.LogInfo("[OnlineMultiplayerMenu] Clearing dialog before navigation");
                DialogManager.Instance.ClearDialog();
            }
            
            // Navigate to lobby room (waiting room before song selection)
            // The LobbyRoomMenu will show all the lobby info, no need for a dialog here
            try
            {
                Debug.Log("[OnlineMultiplayerMenu] About to push LobbyRoom menu...");
                if (MenuManager.Instance != null)
                {
                    Debug.Log($"[OnlineMultiplayerMenu] MenuManager.Instance is valid, pushing LobbyRoom");
                    MenuManager.Instance.PushMenu(MenuManager.Menu.LobbyRoom);
                    Debug.Log("[OnlineMultiplayerMenu] LobbyRoom menu pushed successfully");
                }
                else
                {
                    Debug.LogError("[OnlineMultiplayerMenu] MenuManager.Instance is null!");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[OnlineMultiplayerMenu] Exception while pushing LobbyRoom menu: {ex}");
            }
        }

        private void OnLobbyLeft()
        {
            YargLogger.LogInfo("[OnlineMultiplayerMenu] Left lobby");
            _hasJoinedLobby = false;
            UpdateConnectionStatus();
        }

        private void OnNetworkError(string error)
        {
            YargLogger.LogError($"[OnlineMultiplayerMenu] Network error: {error}");
            
            // Show error dialog using YARG's DialogManager
            if (DialogManager.Instance != null)
            {
                if (DialogManager.Instance.IsDialogShowing)
                {
                    DialogManager.Instance.ClearDialog();
                }

                DialogManager.Instance.ShowMessage(Localize.Key("Menu", "LobbyBrowser", "ConnectionErrorTitle"), error);
            }

            if (connectionStatusText != null)
            {
                connectionStatusText.text = Localize.KeyFormat(("Menu", "LobbyBrowser", "StatusError"), error);
                connectionStatusText.color = Color.red;
            }
        }

        private void UpdateConnectionStatus()
        {
            if (connectionStatusText == null) return;

            var networkService = NetworkingServiceFactory.Instance;

            if (networkService != null && networkService.IsHosting)
            {
                connectionStatusText.text = Localize.Key("Menu", "LobbyBrowser", "StatusHosting");
                connectionStatusText.color = Color.green;
            }
            else if (networkService != null && networkService.CurrentLobby != null)
            {
                // Check if we're connected by seeing if we have a current lobby
                connectionStatusText.text = Localize.Key("Menu", "LobbyBrowser", "StatusConnected");
                connectionStatusText.color = Color.green;
            }
            else
            {
                connectionStatusText.text = Localize.Key("Menu", "LobbyBrowser", "StatusNotConnected");
                connectionStatusText.color = Color.white;
            }
        }
    }
}