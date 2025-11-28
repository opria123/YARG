using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using YARG.Core.Input;
using YARG.Core.Logging;
using YARG.Networking;
using YARG.Networking.NewNet;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YARG.Localization;
using YARG.Net.Handlers.Client;
using YARG.Net.Packets;
using YARG.Net.Runtime;
using YARG.Net.Sessions;

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

        private bool _liteNetEventsHooked;
        private ClientSessionContext? _liteNetSessionContext;
        private bool _liteNetStatusActive;

        private void Start()
        {
            // Subscribe to network events
            if (YargNetworkManager.Instance != null)
            {
                YargNetworkManager.Instance.OnLobbyCreated += OnLobbyCreated;
                YargNetworkManager.Instance.OnLobbyJoined += OnLobbyJoined;
                YargNetworkManager.Instance.OnLobbyLeft += OnLobbyLeft;
                YargNetworkManager.Instance.OnNetworkError += OnNetworkError;
            }

            SubscribeToLiteNetEvents();
            UpdateConnectionStatus();
        }

        private void OnEnable()
        {
            // Reset join flag when menu is reopened
            _hasJoinedLobby = false;
            _liteNetLobbyOpened = false;
            
            // Set up navigation scheme (following YARG's pattern)
            Navigator.Instance.PushScheme(new NavigationScheme(new()
            {
                NavigationScheme.Entry.NavigateSelect,
                NavigationScheme.Entry.NavigateUp,
                NavigationScheme.Entry.NavigateDown,
                new NavigationScheme.Entry(MenuAction.Red, "Menu.Common.Back", OnBackClicked),
            }, true));

            UpdateConnectionStatus();
        }

        private void OnDisable()
        {
            Navigator.Instance?.PopScheme();
        }

        private void OnDestroy()
        {
            // Unsubscribe from events
            if (YargNetworkManager.Instance != null)
            {
                YargNetworkManager.Instance.OnLobbyCreated -= OnLobbyCreated;
                YargNetworkManager.Instance.OnLobbyJoined -= OnLobbyJoined;
                YargNetworkManager.Instance.OnLobbyLeft -= OnLobbyLeft;
                YargNetworkManager.Instance.OnNetworkError -= OnNetworkError;
            }

            UnsubscribeFromLiteNetEvents();
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
            MenuManager.Instance.PopMenu();
        }

        private void OnLobbyCreated(YargNetworkManager.LobbyInfo lobby)
        {
            YargLogger.LogInfo($"[OnlineMultiplayerMenu] Lobby created: {lobby.lobbyName}");
            UpdateConnectionStatus();
            
            // Don't show dialog or navigate here - the host's own OnLobbyJoined will handle it
            // This prevents double-navigation and dialog spam
        }

        private bool _hasJoinedLobby = false;
        private bool _liteNetLobbyOpened;

        private void OnLobbyJoined(YargNetworkManager.LobbyInfo lobby)
        {
            // Prevent multiple calls (Mirror can trigger this multiple times)
            if (_hasJoinedLobby)
            {
                YargLogger.LogInfo("[OnlineMultiplayerMenu] Already joined lobby, ignoring duplicate call");
                return;
            }
            
            _hasJoinedLobby = true;
            YargLogger.LogInfo($"[OnlineMultiplayerMenu] Joined lobby: {lobby.lobbyName}");
            UpdateConnectionStatus();
            
            // Dismiss any connecting dialogs
            if (DialogManager.Instance != null && DialogManager.Instance.IsDialogShowing)
            {
                DialogManager.Instance.ClearDialog();
            }
            
            // Navigate to lobby room (waiting room before song selection)
            // The LobbyRoomMenu will show all the lobby info, no need for a dialog here
            MenuManager.Instance.PushMenu(MenuManager.Menu.LobbyRoom);
        }

        private void OnLobbyLeft()
        {
            YargLogger.LogInfo("[OnlineMultiplayerMenu] Left lobby");
            _hasJoinedLobby = false;
            _liteNetLobbyOpened = false;
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

            if (!_liteNetEventsHooked)
            {
                SubscribeToLiteNetEvents();
            }

            if (TryUpdateLiteNetStatus())
            {
                return;
            }

            _liteNetStatusActive = false;

            var networkManager = YargNetworkManager.Instance;

            if (!_liteNetStatusActive && networkManager != null && networkManager.LocalUserIsHost())
            {
                SetStatus("StatusHosting", Color.green);
            }
            else if (!_liteNetStatusActive && networkManager != null && networkManager.CurrentLobby != null)
            {
                SetStatus("StatusConnected", Color.green);
            }
            else
            {
                SetStatus("StatusNotConnected", Color.white);
            }
        }

        private bool TryUpdateLiteNetStatus()
        {
            if (!ClientNetworkingService.HasInstance)
            {
                return false;
            }

            var service = ClientNetworkingService.Instance;

            if (!service.IsInitialized)
            {
                return false;
            }

            if (service.IsConnected)
            {
                if (service.LobbyHandler != null && service.LobbyHandler.TryGetSnapshot(out var snapshot) && snapshot != null)
                {
                    var sessionId = service.SessionContext?.SessionId;
                    bool isHost = sessionId.HasValue && snapshot.Players.Any(p => p.PlayerId == sessionId.Value && p.Role == LobbyRole.Host);
                    SetStatus(isHost ? "StatusHosting" : "StatusConnected", Color.green);
                    _liteNetStatusActive = true;
                    return true;
                }

                if (service.SessionContext != null && service.SessionContext.HasSession)
                {
                    SetStatus("StatusConnected", Color.green);
                    _liteNetStatusActive = true;
                    return true;
                }

                SetStatus("StatusConnected", Color.yellow);
                _liteNetStatusActive = true;
                return true;
            }

            return false;
        }

        private void SetStatus(string localizationKeySuffix, Color color)
        {
            if (connectionStatusText == null)
            {
                return;
            }

            connectionStatusText.text = Localize.Key("Menu", "LobbyBrowser", localizationKeySuffix);
            connectionStatusText.color = color;
        }

        private void SubscribeToLiteNetEvents()
        {
            if (_liteNetEventsHooked)
            {
                return;
            }

            if (!ClientNetworkingService.HasInstance)
            {
                return;
            }

            var service = ClientNetworkingService.Instance;
            service.Initialize();
            service.Connected += HandleLiteNetConnected;
            service.Disconnected += HandleLiteNetDisconnected;
            service.HandshakeCompleted += HandleLiteNetHandshakeCompleted;
            service.LobbyStateChanged += HandleLiteNetLobbyStateChanged;

            if (service.SessionContext != null)
            {
                _liteNetSessionContext = service.SessionContext;
                _liteNetSessionContext.SessionChanged += HandleLiteNetSessionChanged;
            }

            _liteNetEventsHooked = true;
        }

        private void UnsubscribeFromLiteNetEvents()
        {
            if (!_liteNetEventsHooked)
            {
                return;
            }

            if (ClientNetworkingService.HasInstance)
            {
                var service = ClientNetworkingService.Instance;
                service.Connected -= HandleLiteNetConnected;
                service.Disconnected -= HandleLiteNetDisconnected;
                service.HandshakeCompleted -= HandleLiteNetHandshakeCompleted;
                service.LobbyStateChanged -= HandleLiteNetLobbyStateChanged;
            }

            if (_liteNetSessionContext != null)
            {
                _liteNetSessionContext.SessionChanged -= HandleLiteNetSessionChanged;
                _liteNetSessionContext = null;
            }

            _liteNetEventsHooked = false;
        }

        private void HandleLiteNetConnected(object sender, ClientConnectedEventArgs e)
        {
            _liteNetLobbyOpened = false;
            UpdateConnectionStatus();
        }

        private void HandleLiteNetDisconnected(object sender, ClientDisconnectedEventArgs e)
        {
            _liteNetLobbyOpened = false;
            _hasJoinedLobby = false;
            UpdateConnectionStatus();
        }

        private void HandleLiteNetHandshakeCompleted(object sender, ClientHandshakeCompletedEventArgs e)
        {
            if (!e.Accepted)
            {
                string reason = string.IsNullOrWhiteSpace(e.Reason)
                    ? Localize.Key("Menu", "LobbyBrowser", "StatusNotConnected")
                    : e.Reason;

                if (DialogManager.Instance == null || !DialogManager.Instance.IsDialogShowing)
                {
                    ClientNetworkingMenuUtility.ShowConnectionError(reason);
                }
                _liteNetLobbyOpened = false;
                _hasJoinedLobby = false;
                _ = ClientNetworkingService.Instance.DisconnectAsync(reason);
            }
            else
            {
                EnsureLiteNetLobbyOpened();
            }

            UpdateConnectionStatus();
        }

        private void HandleLiteNetLobbyStateChanged(object sender, ClientLobbyStateChangedEventArgs e)
        {
            EnsureLiteNetLobbyOpened();
            UpdateConnectionStatus();
        }

        private void HandleLiteNetSessionChanged(object sender, ClientSessionChangedEventArgs e)
        {
            if (e.HasSession)
            {
                EnsureLiteNetLobbyOpened();
            }

            UpdateConnectionStatus();
        }

        private void EnsureLiteNetLobbyOpened()
        {
            if (_liteNetLobbyOpened)
            {
                return;
            }

            _liteNetLobbyOpened = true;
            _hasJoinedLobby = true;

            if (DialogManager.Instance != null && DialogManager.Instance.IsDialogShowing)
            {
                DialogManager.Instance.ClearDialog();
            }

            if (MenuManager.Instance != null && MenuManager.Instance.CurrentMenu != MenuManager.Menu.LobbyRoom)
            {
                MenuManager.Instance.PushMenu(MenuManager.Menu.LobbyRoom);
            }
        }
    }
}