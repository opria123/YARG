using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using YARG.Core.Logging;
using YARG.Networking;
using YARG.Networking.NewNet;
using YARG.Menu.Persistent;
using YARG.Menu.Navigation;
using YARG.Localization;
using YARG.Net.Handlers.Client;
using YARG.Net.Runtime;
using YARG.Settings;

namespace YARG.Menu.Multiplayer
{
    /// <summary>
    /// Dialog for directly connecting to a lobby by IP address.
    /// </summary>
    public class DirectConnectDialog : MonoBehaviour
    {
        [Header("UI Elements")]
        [SerializeField] private TMP_InputField ipAddressInput;
        [SerializeField] private TMP_InputField passwordInput;
        [SerializeField] private Toggle hasPasswordToggle;
        [SerializeField] private GameObject passwordPanel;
        [SerializeField] private GameObject passwordLabelPanel; // Optional: separate panel for password label
        [SerializeField] private Button connectButton;
        [SerializeField] private Button cancelButton;

        private int _focusedFieldCount;
        private bool _navigationSuppressed;
        private bool _isConnecting;
        private Dictionary<string, TextMeshProUGUI> _textLookup;

        private void OnEnable()
        {
            SubscribeToNetworkEvents();
            SynchronizeButtonState();
        }

        private void Start()
        {
            if (connectButton != null)
            {
                connectButton.onClick.AddListener(OnConnectClicked);
            }

            if (cancelButton != null)
            {
                cancelButton.onClick.AddListener(OnCancelClicked);
            }

            if (hasPasswordToggle != null)
            {
                hasPasswordToggle.onValueChanged.AddListener(OnPasswordToggleChanged);
            }

            ApplyLocalization();

            // Set default IP
            if (ipAddressInput != null)
            {
                string defaultAddress = Localize.Key("Menu", "LobbyBrowser", "DirectConnectDefaultAddress");
                if (string.IsNullOrWhiteSpace(ipAddressInput.text))
                {
                    ipAddressInput.text = defaultAddress;
                }

                ipAddressInput.onSelect.AddListener(OnIpFieldSelected);
                ipAddressInput.onDeselect.AddListener(_ => RestoreMenuNavigation());
                ipAddressInput.onSubmit.AddListener(_ => OnConnectClicked());
            }

            if (passwordInput != null)
            {
                passwordInput.onSelect.AddListener(_ => SuppressMenuNavigation());
                passwordInput.onDeselect.AddListener(_ => RestoreMenuNavigation());
                passwordInput.onSubmit.AddListener(_ => OnConnectClicked());
            }

            OnPasswordToggleChanged(hasPasswordToggle != null && hasPasswordToggle.isOn);
        }

        private void OnDisable()
        {
            UnsubscribeFromNetworkEvents();
            _isConnecting = false;
            SetConnectInteractable(true);
            ForceRestoreNavigation();
        }

        private void OnPasswordToggleChanged(bool hasPassword)
        {
            if (passwordPanel != null)
            {
                passwordPanel.SetActive(hasPassword);
            }
            
            // Also show/hide password label panel if it exists
            if (passwordLabelPanel != null)
            {
                passwordLabelPanel.SetActive(hasPassword);
            }
        }

        public void OnConnectClicked()
        {
            if (_isConnecting)
            {
                return;
            }

            string endpoint = ipAddressInput != null ? ipAddressInput.text : string.Empty;

            string password = string.Empty;
            if (hasPasswordToggle != null && hasPasswordToggle.isOn && passwordInput != null)
            {
                password = passwordInput.text;
            }

            int defaultPort = GetDefaultPort();
            if (!EndpointUtility.TryParseEndpoint(endpoint, defaultPort, out var host, out var parsedPort, out var errorMessage))
            {
                if (!string.IsNullOrEmpty(errorMessage) && DialogManager.Instance != null)
                {
                    DialogManager.Instance.ShowMessage(Localize.Key("Menu", "LobbyBrowser", "InvalidEndpointTitle"), errorMessage);
                }
                return;
            }

            string normalizedEndpoint = EndpointUtility.FormatEndpoint(host, parsedPort);

            ForceRestoreNavigation();

            if (!ClientNetworkingMenuUtility.IsServiceAvailable)
            {
                ClientNetworkingMenuUtility.ShowConnectionError(Localize.Key("Menu", "LobbyBrowser", "StatusNotConnected"));
                return;
            }

            _isConnecting = true;
            SetConnectInteractable(false);

            // Close this dialog first
            gameObject.SetActive(false);

            ClientNetworkingMenuUtility.ShowConnectingDialog(normalizedEndpoint);

            YargLogger.LogFormatInfo("[DirectConnectDialog] Attempting LiteNetLib direct connect to {0}", normalizedEndpoint);
            BeginNewNetworkingConnect(host, parsedPort, password);
        }

        public void OnCancelClicked()
        {
            _isConnecting = false;
            SetConnectInteractable(true);
            ForceRestoreNavigation();
            gameObject.SetActive(false);
        }

        private void OnIpFieldSelected(string _)
        {
            SuppressMenuNavigation();
            if (ipAddressInput != null)
            {
                SelectAll(ipAddressInput);
            }
        }

        private static void SelectAll(TMP_InputField field)
        {
            if (field == null)
            {
                return;
            }

            int length = field.text != null ? field.text.Length : 0;
            field.selectionAnchorPosition = 0;
            field.selectionFocusPosition = length;
            field.caretPosition = length;
        }


        private int GetDefaultPort()
        {
            return SettingsManager.Settings?.NetworkPort?.Value ?? NetworkTransportDefaults.DefaultUdpPort;
        }

        private void SuppressMenuNavigation()
        {
            _focusedFieldCount++;
            if (!_navigationSuppressed && _focusedFieldCount > 0)
            {
                Navigator.Instance?.PushScheme(NavigationScheme.Empty);
                _navigationSuppressed = true;
            }
        }

        private void RestoreMenuNavigation()
        {
            if (_focusedFieldCount > 0)
            {
                _focusedFieldCount--;
            }

            if (_navigationSuppressed && _focusedFieldCount <= 0)
            {
                Navigator.Instance?.PopScheme();
                _navigationSuppressed = false;
                _focusedFieldCount = 0;
            }
        }

        private void ForceRestoreNavigation()
        {
            _focusedFieldCount = 0;
            if (_navigationSuppressed)
            {
                Navigator.Instance?.PopScheme();
                _navigationSuppressed = false;
            }
        }

        private void SubscribeToNetworkEvents()
        {
            if (!ClientNetworkingMenuUtility.IsServiceAvailable)
            {
                return;
            }

            var service = ClientNetworkingService.Instance;
            service.Connected += HandleNewNetworkingConnected;
            service.Disconnected += HandleNewNetworkingDisconnected;
            service.HandshakeCompleted += HandleNewNetworkingHandshakeCompleted;
        }

        private void UnsubscribeFromNetworkEvents()
        {
            if (!ClientNetworkingService.HasInstance)
            {
                return;
            }

            var service = ClientNetworkingService.Instance;
            service.Connected -= HandleNewNetworkingConnected;
            service.Disconnected -= HandleNewNetworkingDisconnected;
            service.HandshakeCompleted -= HandleNewNetworkingHandshakeCompleted;
        }

        private void HandleNewNetworkingConnected(object sender, ClientConnectedEventArgs e)
        {
            // Intentionally left blank; success is handled once handshake completes.
        }

        private void HandleNewNetworkingDisconnected(object sender, ClientDisconnectedEventArgs e)
        {
            HandleConnectionComplete();
        }

        private void HandleNewNetworkingHandshakeCompleted(object sender, ClientHandshakeCompletedEventArgs e)
        {
            if (!e.Accepted)
            {
                string reason = !string.IsNullOrWhiteSpace(e.Reason)
                    ? e.Reason
                    : Localize.Key("Menu", "LobbyBrowser", "StatusNotConnected");

                ClientNetworkingMenuUtility.ShowConnectionError(reason);
                _ = ClientNetworkingService.Instance.DisconnectAsync(reason);
            }

            HandleConnectionComplete();
        }

        private void HandleConnectionComplete()
        {
            _isConnecting = false;
            SetConnectInteractable(true);
        }

        private void SynchronizeButtonState()
        {
            bool liteNetLibConnected = ClientNetworkingService.HasInstance && ClientNetworkingService.Instance.IsConnected;

            _isConnecting = liteNetLibConnected;
            SetConnectInteractable(!liteNetLibConnected);
        }

        private void SetConnectInteractable(bool interactable)
        {
            if (connectButton != null)
            {
                connectButton.interactable = interactable;
            }
        }

        private void BeginNewNetworkingConnect(string host, int port, string password)
        {
            var parameters = ClientNetworkingMenuUtility.BuildParameters(host, port, password);
            _ = ConnectWithNewNetworkingAsync(parameters);
        }

        private async Task ConnectWithNewNetworkingAsync(ClientConnectionParameters parameters)
        {
            try
            {
                var service = ClientNetworkingService.Instance;
                service.Initialize();
                await service.ConnectAsync(parameters);
            }
            catch (Exception ex)
            {
                YargLogger.LogError($"[DirectConnectDialog] LiteNetLib connection failed: {ex.Message}");
                ClientNetworkingMenuUtility.ShowConnectionError(ex.Message);
                HandleConnectionComplete();
            }
        }

        private void ApplyLocalization()
        {
            SetText("Title", Localize.Key("Menu", "LobbyBrowser", "DirectConnectTitle"));
            SetText("IPAddressLabel", Localize.Key("Menu", "LobbyBrowser", "IpAddressLabel"));
            SetText("PasswordLabel", Localize.Key("Menu", "LobbyBrowser", "PasswordLabel"));

            SetPlaceholder(ipAddressInput, Localize.Key("Menu", "LobbyBrowser", "DirectConnectPlaceholder"));
            SetPlaceholder(passwordInput, Localize.Key("Menu", "LobbyBrowser", "PasswordPlaceholder"));

            if (connectButton != null)
            {
                var text = connectButton.GetComponentInChildren<TextMeshProUGUI>(true);
                if (text != null)
                {
                    text.text = Localize.Key("Menu", "LobbyBrowser", "ConnectButton");
                }
            }

            if (cancelButton != null)
            {
                var text = cancelButton.GetComponentInChildren<TextMeshProUGUI>(true);
                if (text != null)
                {
                    text.text = Localize.Key("Menu", "Common", "Cancel");
                }
            }
        }

        private void SetText(string objectName, string value)
        {
            if (string.IsNullOrEmpty(objectName) || string.IsNullOrEmpty(value))
            {
                return;
            }

            var textComponent = FindText(objectName);
            if (textComponent != null)
            {
                textComponent.text = value;
            }
        }

        private void SetPlaceholder(TMP_InputField input, string value)
        {
            if (input == null || string.IsNullOrEmpty(value))
            {
                return;
            }

            if (input.placeholder is TextMeshProUGUI placeholder)
            {
                placeholder.text = value;
            }
        }

        private TextMeshProUGUI FindText(string objectName)
        {
            if (string.IsNullOrEmpty(objectName))
            {
                return null;
            }

            _textLookup ??= BuildTextLookup();
            return _textLookup != null && _textLookup.TryGetValue(objectName, out var text)
                ? text
                : null;
        }

        private Dictionary<string, TextMeshProUGUI> BuildTextLookup()
        {
            var lookup = new Dictionary<string, TextMeshProUGUI>(StringComparer.OrdinalIgnoreCase);
            foreach (var text in GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                if (text != null && !string.IsNullOrEmpty(text.gameObject.name))
                {
                    lookup[text.gameObject.name] = text;
                }
            }

            return lookup;
        }
    }
}