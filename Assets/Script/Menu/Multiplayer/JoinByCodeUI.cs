using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Cysharp.Threading.Tasks;
using YARG.Core.Logging;
using YARG.Networking.Abstraction;
using YARG.Networking.Session;
using YARG.Networking.Settings;
using YARG.Menu.Persistent;
using YARG.Menu.Navigation;

namespace YARG.Menu.Multiplayer
{
    /// <summary>
    /// UI component for joining a multiplayer session by lobby code.
    /// </summary>
    public class JoinByCodeUI : MonoBehaviour
    {
        [Header("Code Entry")]
        [SerializeField] private TMP_InputField codeInput;
        [SerializeField] private Button joinButton;
        [SerializeField] private Button cancelButton;
        [SerializeField] private Button pasteButton;

        [Header("Status")]
        [SerializeField] private TextMeshProUGUI statusText;

        [Header("Settings")]
        [SerializeField] private int codeLength = 6;

        private bool _isJoining;
        private bool _navigationSuppressed;

        private INetworkingService NetworkService => NetworkingServiceFactory.Instance;

        private void Start()
        {
            WireUpEvents();
            InitializeUI();
        }

        private void OnEnable()
        {
            SubscribeToNetworkEvents();
        }

        private void OnDisable()
        {
            UnsubscribeFromNetworkEvents();
            _isJoining = false;
            UpdateJoinButton();
            ForceRestoreNavigation();
        }

        private void OnDestroy()
        {
            UnwireEvents();
        }

        private void InitializeUI()
        {
            // Configure code input
            if (codeInput != null)
            {
                codeInput.characterLimit = codeLength;
                codeInput.contentType = TMP_InputField.ContentType.Alphanumeric;
                codeInput.onValueChanged.AddListener(OnCodeChanged);
                codeInput.onSelect.AddListener(_ => SuppressMenuNavigation());
                codeInput.onDeselect.AddListener(_ => RestoreMenuNavigation());
                codeInput.onSubmit.AddListener(_ => OnJoinClicked());
            }

            UpdateJoinButton();
        }

        private void WireUpEvents()
        {
            if (joinButton != null)
            {
                joinButton.onClick.AddListener(OnJoinClicked);
            }

            if (cancelButton != null)
            {
                cancelButton.onClick.AddListener(OnCancelClicked);
            }

            if (pasteButton != null)
            {
                pasteButton.onClick.AddListener(OnPasteClicked);
            }
        }

        private void UnwireEvents()
        {
            if (joinButton != null)
            {
                joinButton.onClick.RemoveListener(OnJoinClicked);
            }

            if (cancelButton != null)
            {
                cancelButton.onClick.RemoveListener(OnCancelClicked);
            }

            if (pasteButton != null)
            {
                pasteButton.onClick.RemoveListener(OnPasteClicked);
            }

            if (codeInput != null)
            {
                codeInput.onValueChanged.RemoveListener(OnCodeChanged);
            }
        }

        private void OnCodeChanged(string newCode)
        {
            // Auto-capitalize
            if (codeInput != null && !string.IsNullOrEmpty(newCode))
            {
                string upper = newCode.ToUpperInvariant();
                if (upper != newCode)
                {
                    codeInput.text = upper;
                    codeInput.caretPosition = upper.Length;
                    return;
                }
            }

            UpdateJoinButton();

            // Clear status when typing
            if (statusText != null)
            {
                statusText.text = string.Empty;
            }
        }

        private void UpdateJoinButton()
        {
            if (joinButton != null)
            {
                string code = codeInput?.text ?? string.Empty;
                joinButton.interactable = !_isJoining && code.Length == codeLength;
            }
        }

        private void OnPasteClicked()
        {
            string clipboard = GUIUtility.systemCopyBuffer;
            if (!string.IsNullOrEmpty(clipboard) && codeInput != null)
            {
                // Clean up - remove spaces, take first N characters
                clipboard = clipboard.Replace(" ", "").Replace("-", "").ToUpperInvariant();
                if (clipboard.Length > codeLength)
                {
                    clipboard = clipboard.Substring(0, codeLength);
                }
                codeInput.text = clipboard;
            }
        }

        private void OnJoinClicked()
        {
            string code = codeInput?.text?.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(code) || code.Length != codeLength)
            {
                SetStatus($"Please enter a {codeLength}-character code", Color.yellow);
                return;
            }

            if (_isJoining)
            {
                return;
            }

            // Check if SessionLifecycleManager is available
            if (SessionLifecycleManager.Instance == null)
            {
                SetStatus("Networking not initialized", Color.red);
                return;
            }

            _isJoining = true;
            UpdateJoinButton();
            SetStatus("Looking up code...", Color.white);

            ForceRestoreNavigation();

            // Close dialog
            gameObject.SetActive(false);

            // Show connecting message
            if (DialogManager.Instance != null && !DialogManager.Instance.IsDialogShowing)
            {
                DialogManager.Instance.ShowMessage("Connecting", $"Looking up code {code}...");
            }

            // Fire and forget the async lookup
            LookupAndJoinAsync(code).Forget();
        }
        
        private async UniTaskVoid LookupAndJoinAsync(string code)
        {
            try
            {
                YargLogger.LogFormatInfo("[JoinByCodeUI] Looking up lobby code: {0}", code);
                
                var result = await SessionLifecycleManager.Instance.LookupLobbyCodeAsync(code);
                
                if (result == null || !result.IsSuccess || result.Lobby == null)
                {
                    string errorMsg = result?.Error ?? "Invalid or expired code";
                    YargLogger.LogFormatWarning("[JoinByCodeUI] Code lookup failed: {0}", errorMsg);
                    
                    if (DialogManager.Instance != null)
                    {
                        DialogManager.Instance.ShowMessage("Join Failed", errorMsg);
                    }
                    _isJoining = false;
                    return;
                }
                
                var lobby = result.Lobby;
                YargLogger.LogInfo($"[JoinByCodeUI] Found lobby: {lobby.LobbyName} at {lobby.Address}:{lobby.Port} (hasPassword={lobby.HasPassword})");
                
                string endpoint = $"{lobby.Address}:{lobby.Port}";
                
                // If lobby requires a password, prompt the user
                if (lobby.HasPassword)
                {
                    YargLogger.LogInfo("[JoinByCodeUI] Lobby requires password, showing dialog");
                    ShowPasswordDialogForCodeJoin(lobby.LobbyName, endpoint);
                    return;
                }
                
                // Update dialog to show we're connecting
                if (DialogManager.Instance != null && DialogManager.Instance.IsDialogShowing)
                {
                    DialogManager.Instance.ClearDialog();
                    DialogManager.Instance.ShowMessage("Connecting", $"Connecting to {lobby.LobbyName}...");
                }
                
                // Join via the networking service
                if (NetworkService != null)
                {
                    NetworkService.JoinLobby(endpoint, string.Empty);
                }
                else
                {
                    YargLogger.LogError("[JoinByCodeUI] NetworkService not available");
                    if (DialogManager.Instance != null)
                    {
                        DialogManager.Instance.ShowMessage("Join Failed", "Networking service not available");
                    }
                }
            }
            catch (Exception ex)
            {
                YargLogger.LogFormatError("[JoinByCodeUI] Error joining by code: {0}", ex.Message);
                if (DialogManager.Instance != null)
                {
                    DialogManager.Instance.ShowMessage("Join Failed", $"Error: {ex.Message}");
                }
            }
            finally
            {
                _isJoining = false;
            }
        }
        
        /// <summary>
        /// Shows a password dialog for joining a lobby via code.
        /// </summary>
        private void ShowPasswordDialogForCodeJoin(string lobbyName, string endpoint)
        {
            _isJoining = false; // Allow UI interaction while dialog is open
            
            if (DialogManager.Instance == null)
            {
                YargLogger.LogWarning("[JoinByCodeUI] Password dialog requested but DialogManager is unavailable.");
                return;
            }

            var dialog = DialogManager.Instance.ShowRenameDialog("Password Required", value =>
            {
                var submitted = (value ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(submitted))
                    return;

                YargLogger.LogInfo($"[JoinByCodeUI] Joining {lobbyName} at {endpoint} with password");
                
                if (NetworkService != null)
                {
                    NetworkService.JoinLobby(endpoint, submitted);
                }
                else
                {
                    YargLogger.LogError("[JoinByCodeUI] NetworkService not available");
                    if (DialogManager.Instance != null)
                    {
                        DialogManager.Instance.ShowMessage("Join Failed", "Networking service not available");
                    }
                }
            });

            dialog.AllowEmpty = false;
            dialog.SetInitialText(string.Empty, false);

            var inputField = dialog.GetComponentInChildren<TMP_InputField>(true);
            if (inputField != null)
            {
                inputField.contentType = TMP_InputField.ContentType.Password;
                inputField.lineType = TMP_InputField.LineType.SingleLine;
                inputField.text = string.Empty;
                if (inputField.placeholder is TMP_Text placeholderText)
                    placeholderText.text = "Enter lobby password";
                inputField.Select();
                inputField.ActivateInputField();
            }
        }

        private void OnCancelClicked()
        {
            _isJoining = false;
            UpdateJoinButton();
            ForceRestoreNavigation();
            gameObject.SetActive(false);
        }

        private void SetStatus(string message, Color color)
        {
            if (statusText != null)
            {
                statusText.text = message;
                statusText.color = color;
            }
        }

        private void SuppressMenuNavigation()
        {
            if (!_navigationSuppressed)
            {
                Navigator.Instance?.PushScheme(NavigationScheme.Empty);
                _navigationSuppressed = true;
            }
        }

        private void RestoreMenuNavigation()
        {
            if (_navigationSuppressed)
            {
                Navigator.Instance?.PopScheme();
                _navigationSuppressed = false;
            }
        }

        private void ForceRestoreNavigation()
        {
            if (_navigationSuppressed)
            {
                Navigator.Instance?.PopScheme();
                _navigationSuppressed = false;
            }
        }

        private void SubscribeToNetworkEvents()
        {
            if (NetworkService == null) return;

            NetworkService.OnLobbyJoined += HandleLobbyJoined;
            NetworkService.OnNetworkError += HandleNetworkError;
        }

        private void UnsubscribeFromNetworkEvents()
        {
            if (NetworkService == null) return;

            NetworkService.OnLobbyJoined -= HandleLobbyJoined;
            NetworkService.OnNetworkError -= HandleNetworkError;
        }

        private void HandleLobbyJoined(LobbyInfo _)
        {
            _isJoining = false;
            UpdateJoinButton();
        }

        private void HandleNetworkError(string error)
        {
            _isJoining = false;
            UpdateJoinButton();
            SetStatus($"Error: {error}", Color.red);
        }

        /// <summary>
        /// Set the code from external source (e.g., deep link).
        /// </summary>
        public void SetCode(string code)
        {
            if (codeInput != null && !string.IsNullOrEmpty(code))
            {
                codeInput.text = code.ToUpperInvariant();
            }
        }
    }
}
