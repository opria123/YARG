using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using YARG.Core.Input;
using YARG.Menu.Data;
using YARG.Menu.Multiplayer;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YARG.Networking.Abstraction;
using YARG.Settings;

namespace YARG.Gameplay.HUD
{
    public class FailPause : GenericPause
    {
        [SerializeField]
        private GameObject _separatorObject;

        private NavigatableButton _restartButton;
        private NavigatableButton _enableNoFailButton;
        private NavigatableButton _backToLibraryButton;
        private NavigatableButton _practiceModeButton;

        private TMP_Text _backToLibraryLabel;
        private TMP_Text _enableNoFailLabel;
        private TMP_Text _practiceModeLabel;

        private bool _buttonsCached;
        private bool _initialSeparatorState = true;
        private bool _initialRestartState = true;
        private bool _initialEnableNoFailState = true;
        private bool _initialPracticeModeState = true;

        private string _defaultBackToLibraryLabel;
        private string _defaultEnableNoFailLabel;
        private string _defaultPracticeModeLabel;

        private bool _isMultiplayer;
        private bool _isHost;

        protected override void GameplayAwake()
        {
            base.GameplayAwake();
            CacheButtons();
        }

        protected override void OnEnable()
        {
            // Reset cache if buttons weren't found (wrong component type on previous attempt)
            if (_buttonsCached && _restartButton == null && _backToLibraryButton == null)
            {
                Debug.Log("[FailPause] Resetting button cache - buttons were not found previously");
                _buttonsCached = false;
            }
            
            CacheButtons();
            UpdateMultiplayerState();
            RestoreSinglePlayerLayout();

            Debug.Log($"[FailPause] OnEnable - isMultiplayer={_isMultiplayer}, isHost={_isHost}");

            if (_isMultiplayer)
            {
                ApplyMultiplayerLayout();
            }

            HandleNavigationScheme();
        }

        private async void HandleNavigationScheme()
        {
            await UniTask.WaitForSeconds(0.5f, true);

            if (!isActiveAndEnabled || Navigator.Instance == null)
            {
                return;
            }

                var entries = new List<NavigationScheme.Entry>
                {
                    NavigationScheme.Entry.NavigateSelect,
                    NavigationScheme.Entry.NavigateUp,
                    NavigationScheme.Entry.NavigateDown,
                };

                if (!_isMultiplayer)
                {
                    entries.Insert(1, new NavigationScheme.Entry(MenuAction.Red, "Menu.Common.Back", Back));
                }

                if (_isMultiplayer)
                {
                    entries.Add(_isHost
                        ? new NavigationScheme.Entry(MenuAction.Orange, "Back to Library", HostBackToLibrary)
                        : new NavigationScheme.Entry(MenuAction.Orange, "Leave Lobby", ClientLeaveLobby));
                }

            Navigator.Instance.PushScheme(new NavigationScheme(entries, false));
        }

        public override void Restart()
        {
            UpdateMultiplayerState();

            if (_isMultiplayer && _isHost)
            {
                HostRestart();
                return;
            }

            base.Restart();
        }

        public override void BackToLibrary()
        {
            UpdateMultiplayerState();

            if (!_isMultiplayer)
            {
                base.BackToLibrary();
                return;
            }

            if (_isHost)
            {
                HostBackToLibrary();
            }
            else
            {
                ClientLeaveLobby();
            }
        }

        // TODO: Make a similar option that only makes the rest of this song no fail
        //  and then resumes the song
        public void EnableNoFail()
        {
            // It feels a bit icky reaching down into the settings like this
            SettingsManager.Settings.NoFailMode.SetValueWithoutNotify(true);
            Restart();
        }

        private void CacheButtons()
        {
            if (_buttonsCached)
            {
                Debug.Log("[FailPause] CacheButtons - already cached");
                return;
            }

            _buttonsCached = true;
            Debug.Log("[FailPause] CacheButtons - caching buttons...");

            if (_separatorObject != null)
            {
                _initialSeparatorState = _separatorObject.activeSelf;
            }

            foreach (var button in GetComponentsInChildren<NavigatableButton>(true))
            {
                Debug.Log($"[FailPause] Found button: '{button.gameObject.name}'");
                switch (button.gameObject.name)
                {
                    case "Restart":
                        _restartButton = button;
                        _initialRestartState = button.gameObject.activeSelf;
                        Debug.Log("[FailPause] Cached Restart button");
                        break;
                    case "Enable No Fail":
                        _enableNoFailButton = button;
                        _initialEnableNoFailState = button.gameObject.activeSelf;
                        _enableNoFailLabel = button.GetComponentInChildren<TMP_Text>(true);
                        if (_enableNoFailLabel != null)
                        {
                            _defaultEnableNoFailLabel = _enableNoFailLabel.text;
                        }
                        Debug.Log("[FailPause] Cached Enable No Fail button");
                        break;
                    case "Back to Library":
                        _backToLibraryButton = button;
                        _backToLibraryLabel = button.GetComponentInChildren<TMP_Text>(true);
                        if (_backToLibraryLabel != null)
                        {
                            _defaultBackToLibraryLabel = _backToLibraryLabel.text;
                        }
                        Debug.Log("[FailPause] Cached Back to Library button");
                        break;
                    case "Practice Mode":
                        _practiceModeButton = button;
                        _initialPracticeModeState = button.gameObject.activeSelf;
                        _practiceModeLabel = button.GetComponentInChildren<TMP_Text>(true);
                        if (_practiceModeLabel != null)
                        {
                            _defaultPracticeModeLabel = _practiceModeLabel.text;
                        }
                        Debug.Log("[FailPause] Cached Practice Mode button");
                        break;
                }
            }

            if (_backToLibraryButton != null && string.IsNullOrEmpty(_defaultBackToLibraryLabel))
            {
                _backToLibraryLabel = _backToLibraryButton.GetComponentInChildren<TMP_Text>(true);
                if (_backToLibraryLabel != null)
                {
                    _defaultBackToLibraryLabel = _backToLibraryLabel.text;
                }
            }
            
            Debug.Log($"[FailPause] CacheButtons complete - restart={_restartButton != null}, enableNoFail={_enableNoFailButton != null}, backToLib={_backToLibraryButton != null}, practice={_practiceModeButton != null}");
        }

        private void UpdateMultiplayerState()
        {
            // Check for multiplayer using multiple methods for reliability
            var networkService = NetworkingServiceFactory.Instance;
            bool networkActive = networkService != null && networkService.IsNetworkActive;
            
            // Also check if we have the multiplayer sync component (more reliable during gameplay)
            var multiplayerSync = GameManager != null ? GameManager.GetComponent<MultiplayerGameplaySync>() : null;
            
            _isMultiplayer = networkActive || multiplayerSync != null;
            _isHost = _isMultiplayer && networkService != null && networkService.IsHosting;
            
            Debug.Log($"[FailPause] UpdateMultiplayerState - networkActive={networkActive}, hasSync={multiplayerSync != null}, _isMultiplayer={_isMultiplayer}, _isHost={_isHost}");
        }

        private void RestoreSinglePlayerLayout()
        {
            if (_separatorObject != null)
            {
                _separatorObject.SetActive(_initialSeparatorState);
            }

            if (_restartButton != null)
            {
                _restartButton.gameObject.SetActive(_initialRestartState);
            }

            if (_enableNoFailButton != null)
            {
                _enableNoFailButton.gameObject.SetActive(_initialEnableNoFailState);
            }

            if (_practiceModeButton != null)
            {
                _practiceModeButton.gameObject.SetActive(_initialPracticeModeState);
            }

            if (_backToLibraryButton != null)
            {
                _backToLibraryButton.gameObject.SetActive(true);
            }

            if (_backToLibraryLabel != null && !string.IsNullOrEmpty(_defaultBackToLibraryLabel))
            {
                _backToLibraryLabel.text = _defaultBackToLibraryLabel;
            }

            if (_enableNoFailLabel != null && !string.IsNullOrEmpty(_defaultEnableNoFailLabel))
            {
                _enableNoFailLabel.text = _defaultEnableNoFailLabel;
            }

            if (_practiceModeLabel != null && !string.IsNullOrEmpty(_defaultPracticeModeLabel))
            {
                _practiceModeLabel.text = _defaultPracticeModeLabel;
            }
        }

        private void ApplyMultiplayerLayout()
        {
            Debug.Log($"[FailPause] ApplyMultiplayerLayout - isHost={_isHost}, restartBtn={_restartButton != null}, enableNoFailBtn={_enableNoFailButton != null}, backToLibBtn={_backToLibraryButton != null}, practiceModeBtn={_practiceModeButton != null}");
            
            // Hide separator and single-player-only options in multiplayer
            if (_separatorObject != null)
            {
                _separatorObject.SetActive(false);
            }

            if (_enableNoFailButton != null)
            {
                Debug.Log("[FailPause] Hiding Enable No Fail button");
                _enableNoFailButton.gameObject.SetActive(false);
            }

            if (_practiceModeButton != null)
            {
                Debug.Log("[FailPause] Hiding Practice Mode button");
                _practiceModeButton.gameObject.SetActive(false);
            }

            if (_isHost)
            {
                // Host sees: Restart + Back to Library
                // Both actions sync to all clients
                if (_restartButton != null)
                {
                    Debug.Log("[FailPause] Host: Showing Restart button");
                    _restartButton.gameObject.SetActive(true);
                }

                if (_backToLibraryButton != null)
                {
                    Debug.Log("[FailPause] Host: Showing Back to Library button");
                    _backToLibraryButton.gameObject.SetActive(true);
                }

                if (_backToLibraryLabel != null && !string.IsNullOrEmpty(_defaultBackToLibraryLabel))
                {
                    _backToLibraryLabel.text = _defaultBackToLibraryLabel;
                }
            }
            else
            {
                // Client sees ONLY: Leave Lobby
                // Hide everything except the back to library button (repurposed as Leave Lobby)
                if (_restartButton != null)
                {
                    Debug.Log("[FailPause] Client: Hiding Restart button");
                    _restartButton.gameObject.SetActive(false);
                }

                if (_backToLibraryButton != null)
                {
                    Debug.Log("[FailPause] Client: Showing Leave Lobby button");
                    _backToLibraryButton.gameObject.SetActive(true);
                }

                if (_backToLibraryLabel != null)
                {
                    _backToLibraryLabel.text = "Leave Lobby";
                }
            }
        }

        private void HostRestart()
        {
            if (!_isHost)
            {
                return;
            }

            var networkService = NetworkingServiceFactory.Instance as LiteNetNetworkingAdapter;
            if (networkService != null)
            {
                // Use unified RestartGameplay - works for both in-game hosting and dedicated servers
                networkService.RestartGameplay();
            }

            PauseMenuManager.Restart();
        }

        private void HostBackToLibrary()
        {
            if (!_isHost)
            {
                return;
            }

            var networkService = NetworkingServiceFactory.Instance as LiteNetNetworkingAdapter;
            if (networkService != null)
            {
                // Use unified host action methods - works for both in-game hosting and dedicated servers
                networkService.QuitToLibrary();
                networkService.NavigateToMusicLibrary();
            }
            
            // Set up proper menu navigation stack so back button works correctly
            // Stack will be: MainMenu > OnlineMultiplayer > LobbyRoom > MusicLibrary
            Networking.Abstraction.MenuNavigationHelper.SetMenuNavigationAfterSceneLoad(
                Menu.MenuManager.Menu.OnlineMultiplayer,
                Menu.MenuManager.Menu.LobbyRoom,
                Menu.MenuManager.Menu.MusicLibrary);

            PauseMenuManager.Quit();
        }

        private void ClientLeaveLobby()
        {
            if (!_isMultiplayer || _isHost)
            {
                return;
            }

            var dialogManager = DialogManager.Instance;
            if (dialogManager == null)
            {
                ExecuteClientLeaveLobby();
                return;
            }

            var dialog = dialogManager.ShowMessage(
                "Leave Lobby?",
                "Are you sure you want to leave the lobby?");

            dialog.ClearButtons();
            dialog.AddDialogButton("Cancel", MenuData.Colors.BrightButton, () => dialogManager.ClearDialog());
            dialog.AddDialogButton("Leave Lobby", MenuData.Colors.CancelButton, () =>
            {
                dialogManager.ClearDialog();
                ExecuteClientLeaveLobby();
            });
        }

        private void ExecuteClientLeaveLobby()
        {
            var networkService = NetworkingServiceFactory.Instance;
            if (networkService != null)
            {
                networkService.LeaveLobby();
            }
            
            // Navigate back to the main menu after leaving the lobby
            PauseMenuManager.Quit();
        }
    }
}