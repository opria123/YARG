using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using YARG.Networking.Settings;
using YARG.Settings;

namespace YARG.Menu.Settings
{
    /// <summary>
    /// Component for a single lobby server entry in the settings list.
    /// Allows editing the URL, toggling enabled state, and removing custom lobbyServers.
    /// </summary>
    public class LobbyServerEntry : MonoBehaviour
    {
        [SerializeField]
        private TMP_InputField _urlInput;

        [SerializeField]
        private Toggle _enabledToggle;

        [SerializeField]
        private Button _removeButton;

        [SerializeField]
        private GameObject _builtInIndicator;

        private int _index;
        private LobbyServerEndpoint _endpoint;

        private static List<LobbyServerEndpoint> LobbyServers => 
            NetworkSettingsStore.Instance?.Settings?.lobbyServers;

        public void SetIndex(int index)
        {
            _index = index;
            RefreshFromData();
        }

        private void Start()
        {
            // Wire up events
            if (_urlInput != null)
            {
                _urlInput.onEndEdit.AddListener(OnUrlChanged);
            }

            if (_enabledToggle != null)
            {
                _enabledToggle.onValueChanged.AddListener(OnEnabledChanged);
            }

            if (_removeButton != null)
            {
                _removeButton.onClick.AddListener(Remove);
            }
        }

        private void OnDestroy()
        {
            if (_urlInput != null)
            {
                _urlInput.onEndEdit.RemoveListener(OnUrlChanged);
            }

            if (_enabledToggle != null)
            {
                _enabledToggle.onValueChanged.RemoveListener(OnEnabledChanged);
            }

            if (_removeButton != null)
            {
                _removeButton.onClick.RemoveListener(Remove);
            }
        }

        private void RefreshFromData()
        {
            var servers = LobbyServers;
            if (servers == null || _index < 0 || _index >= servers.Count)
            {
                gameObject.SetActive(false);
                return;
            }

            _endpoint = servers[_index];

            // Update URL input
            if (_urlInput != null)
            {
                _urlInput.text = _endpoint.url ?? string.Empty;
                _urlInput.interactable = !_endpoint.isBuiltIn; // Can't edit built-in URLs
            }

            // Update enabled toggle
            if (_enabledToggle != null)
            {
                _enabledToggle.SetIsOnWithoutNotify(_endpoint.enabled);
            }

            // Show/hide remove button (can't remove built-in)
            if (_removeButton != null)
            {
                _removeButton.gameObject.SetActive(!_endpoint.isBuiltIn);
            }

            // Show built-in indicator (small icon to show it's official)
            if (_builtInIndicator != null)
            {
                _builtInIndicator.SetActive(_endpoint.isBuiltIn);
            }
        }

        private void OnUrlChanged(string newUrl)
        {
            if (_endpoint == null || _endpoint.isBuiltIn)
                return;

            var store = NetworkSettingsStore.Instance;
            if (store != null)
            {
                store.UpdateLobbyServer(_endpoint.id, url: newUrl);
            }
        }

        private void OnEnabledChanged(bool enabled)
        {
            if (_endpoint == null)
            {
                Debug.LogWarning("[LobbyServerEntry] OnEnabledChanged called but _endpoint is null");
                return;
            }

            Debug.Log($"[LobbyServerEntry] OnEnabledChanged: {_endpoint.url} ({_endpoint.id}) -> enabled={enabled}");
            
            var store = NetworkSettingsStore.Instance;
            if (store != null)
            {
                store.SetLobbyServerEnabled(_endpoint.id, enabled);
            }
            else
            {
                Debug.LogError("[LobbyServerEntry] NetworkSettingsStore.Instance is null!");
            }
        }

        /// <summary>
        /// Removes this lobby server entry.
        /// </summary>
        public void Remove()
        {
            if (_endpoint == null || _endpoint.isBuiltIn)
                return;

            var store = NetworkSettingsStore.Instance;
            if (store != null && store.RemoveLobbyServer(_endpoint.id))
            {
                // Refresh the settings menu but keep scroll position
                SettingsMenu.Instance.RefreshAndKeepPosition();
            }
        }
    }
}
