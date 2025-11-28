using UnityEngine;
using UnityEngine.UI;
using TMPro;
using YARG.Core.Logging;
using YARG.Networking;
using YARG.Networking.NewNet;
using YARG.Menu.Persistent;
using Cysharp.Threading.Tasks;
using System;

namespace YARG.Menu.Multiplayer
{
    /// <summary>
    /// Dialog for creating a new lobby.
    /// Uses LiteNet networking for all new lobbies.
    /// Mirror (legacy) support has been deprecated.
    /// </summary>
    public class CreateLobbyDialog : MonoBehaviour
    {
        [Header("UI Elements")]
        [SerializeField] private TMP_InputField lobbyNameInput;
        [SerializeField] private TMP_Dropdown maxPlayersDropdown;
        [SerializeField] private TMP_Dropdown privacyModeDropdown;
        [SerializeField] private TMP_InputField passwordInput;
        [SerializeField] private GameObject passwordPanel;
        [SerializeField] private GameObject passwordLabelPanel; // Optional: separate panel for password label
        [SerializeField] private Button createButton;
        [SerializeField] private Button cancelButton;

        private void Start()
        {
            if (createButton != null)
            {
                createButton.onClick.AddListener(OnCreateClicked);
            }

            if (cancelButton != null)
            {
                cancelButton.onClick.AddListener(OnCancelClicked);
            }

            // Set up privacy mode dropdown
            if (privacyModeDropdown != null)
            {
                privacyModeDropdown.ClearOptions();
                privacyModeDropdown.AddOptions(new System.Collections.Generic.List<string>
                {
                    "Public",
                    "Private (Password)"
                });
                privacyModeDropdown.onValueChanged.AddListener(OnPrivacyModeChanged);
            }

            // Set up max players dropdown
            if (maxPlayersDropdown != null)
            {
                maxPlayersDropdown.ClearOptions();
                var playerOptions = new System.Collections.Generic.List<string>();
                for (int i = 2; i <= 32; i++)
                {
                    playerOptions.Add(i.ToString());
                }
                maxPlayersDropdown.AddOptions(playerOptions);
                maxPlayersDropdown.value = 6; // Default to 8 players
            }

            // Set default lobby name
            if (lobbyNameInput != null)
            {
                string playerName = "Player";
                // Try to get player name from Mirror first (for backwards compatibility)
                if (YargNetworkManager.Instance != null)
                {
                    playerName = YargNetworkManager.Instance.PlayerName;
                }
                lobbyNameInput.text = $"{playerName}'s Lobby";
            }

            OnPrivacyModeChanged(0);
        }

        private void OnPrivacyModeChanged(int index)
        {
            // Show password field for private lobbies
            bool showPassword = index == 1;
            
            if (passwordPanel != null)
            {
                passwordPanel.SetActive(showPassword);
            }
            
            // Also show/hide password label panel if it exists
            if (passwordLabelPanel != null)
            {
                passwordLabelPanel.SetActive(showPassword);
            }
        }

        public async void OnCreateClicked()
        {
            string lobbyName = lobbyNameInput != null ? lobbyNameInput.text : "YARG Lobby";
            if (string.IsNullOrEmpty(lobbyName))
            {
                lobbyName = "YARG Lobby";
            }

            int maxPlayers = maxPlayersDropdown != null ? maxPlayersDropdown.value + 2 : 8;
            bool isPrivate = privacyModeDropdown != null && privacyModeDropdown.value == 1;
            string password = (isPrivate && passwordInput != null) ? passwordInput.text : "";

            // Close this dialog first
            gameObject.SetActive(false);
            
            YargLogger.LogFormatInfo("[CreateLobbyDialog] Creating LiteNet lobby: '{0}', maxPlayers={1}, hasPassword={2}", 
                lobbyName, maxPlayers, isPrivate);

            try
            {
                // Get player name
                string playerName = "Player";
                if (YargNetworkManager.Instance != null)
                {
                    playerName = YargNetworkManager.Instance.PlayerName;
                }

                // Use LiteNet for new lobbies
                var options = new ServerHostOptions
                {
                    LobbyName = lobbyName,
                    HostName = playerName,
                    MaxPlayers = maxPlayers,
                    Password = isPrivate ? password : null,
                    Port = 7777,
                    EnableNatPunchThrough = true,
                    IntroducerUri = new Uri("https://introducer.yarg.in/api/lobbies")
                };

                await ServerNetworkingService.Instance.StartAsync(options);
                YargLogger.LogInfo("[CreateLobbyDialog] LiteNet server started successfully");
                
                // Connect locally as host
                var connectionParams = new ClientConnectionParameters(
                    address: "127.0.0.1",
                    port: 7777,
                    playerName: playerName,
                    password: password
                );
                await ClientNetworkingService.Instance.ConnectAsync(connectionParams);
                YargLogger.LogInfo("[CreateLobbyDialog] Connected to local LiteNet server as host");
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex, "Failed to create LiteNet lobby");
                DialogManager.Instance.ShowMessage("Failed to Create Lobby", 
                    $"Could not create lobby: {ex.Message}\n\nPlease try again.");
            }
        }

        public void OnCancelClicked()
        {
            // Close this dialog
            gameObject.SetActive(false);
        }
    }
}