using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using YARG.Networking.Settings;
using YARG.Settings;

namespace YARG.Menu.Settings
{
    /// <summary>
    /// Component for a single introducer entry in the settings list.
    /// Allows editing the URL, toggling enabled state, and removing custom introducers.
    /// </summary>
    public class IntroducerEntry : MonoBehaviour
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
        private IntroducerEndpoint _endpoint;

        private static List<IntroducerEndpoint> Introducers => 
            NetworkSettingsStore.Instance?.Settings?.introducers;

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
            var introducers = Introducers;
            if (introducers == null || _index < 0 || _index >= introducers.Count)
            {
                gameObject.SetActive(false);
                return;
            }

            _endpoint = introducers[_index];

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
                store.UpdateIntroducer(_endpoint.id, url: newUrl);
            }
        }

        private void OnEnabledChanged(bool enabled)
        {
            if (_endpoint == null)
            {
                Debug.LogWarning("[IntroducerEntry] OnEnabledChanged called but _endpoint is null");
                return;
            }

            Debug.Log($"[IntroducerEntry] OnEnabledChanged: {_endpoint.url} ({_endpoint.id}) -> enabled={enabled}");
            
            var store = NetworkSettingsStore.Instance;
            if (store != null)
            {
                store.SetIntroducerEnabled(_endpoint.id, enabled);
            }
            else
            {
                Debug.LogError("[IntroducerEntry] NetworkSettingsStore.Instance is null!");
            }
        }

        /// <summary>
        /// Removes this introducer entry.
        /// </summary>
        public void Remove()
        {
            if (_endpoint == null || _endpoint.isBuiltIn)
                return;

            var store = NetworkSettingsStore.Instance;
            if (store != null && store.RemoveIntroducer(_endpoint.id))
            {
                // Refresh the settings menu but keep scroll position
                SettingsMenu.Instance.RefreshAndKeepPosition();
            }
        }
    }
}
