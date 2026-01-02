using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;
using YARG.Localization;
using YARG.Menu.Navigation;
using YARG.Menu.Settings;
using YARG.Menu.Settings.Visuals;
using YARG.Networking.Settings;

namespace YARG.Settings.Metadata
{
    public class MetadataTab : Tab, IEnumerable<AbstractMetadata>
    {
        // Prefabs needed for this tab type
        private static readonly GameObject _headerPrefab = Addressables
            .LoadAssetAsync<GameObject>("SettingTab/Header")
            .WaitForCompletion();
        private static readonly GameObject _buttonPrefab = Addressables
            .LoadAssetAsync<GameObject>("SettingTab/Button")
            .WaitForCompletion();
        private static readonly GameObject _textPrefab = Addressables
            .LoadAssetAsync<GameObject>("SettingTab/Text")
            .WaitForCompletion();
        
        // lobby server prefabs - loaded lazily since they may not exist yet
        private static GameObject _lobbyServerHeaderPrefab;
        private static GameObject _lobbyServerEntryPrefab;
        private static bool _lobbyServerPrefabsLoaded;

        private static void EnsureLobbyServerPrefabsLoaded()
        {
            if (_lobbyServerPrefabsLoaded) return;
            _lobbyServerPrefabsLoaded = true;
            
            try
            {
                var headerOp = Addressables.LoadAssetAsync<GameObject>("SettingTab/LobbyServerHeader");
                _lobbyServerHeaderPrefab = headerOp.WaitForCompletion();
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[MetadataTab] LobbyServerHeader prefab not found: {e.Message}");
            }
            
            try
            {
                var entryOp = Addressables.LoadAssetAsync<GameObject>("SettingTab/LobbyServerEntry");
                _lobbyServerEntryPrefab = entryOp.WaitForCompletion();
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[MetadataTab] LobbyServerEntry prefab not found: {e.Message}");
            }
        }

        private Dictionary<string, BaseSettingVisual> _settingVisuals = new();
        private readonly List<AbstractMetadata> _settings = new();

        public IReadOnlyList<AbstractMetadata> Settings => _settings;

        public MetadataTab(string name, string icon = "Generic", IPreviewBuilder previewBuilder = null)
            : base(name, icon, previewBuilder)
        {
        }

        public override void BuildSettingTab(Transform container, NavigationGroup navGroup)
        {
            _settingVisuals.Clear();
            var settingIndex = 0;

            // Once we've found the tab, add the settings
            foreach (var settingMetadata in _settings)
            {
                switch (settingMetadata)
                {
                    case HeaderMetadata header:
                    {
                        // Spawn in the header
                        var go = Object.Instantiate(_headerPrefab, container);

                        // Set header text
                        go.GetComponentInChildren<TextMeshProUGUI>().text =
                            Localize.Key("Settings.Header", header.HeaderName);

                        settingIndex = 0;
                        break;
                    }
                    case ButtonRowMetadata buttonRow:
                    {
                        // Spawn the button
                        var go = Object.Instantiate(_buttonPrefab, container);

                        var buttonGroup = go.GetComponent<SettingsButton>();
                        buttonGroup.SetInfo(buttonRow.Buttons);
                        navGroup.AddNavigatable(buttonGroup);

                        break;
                    }
                    case TextMetadata text:
                    {
                        // Spawn in the text
                        var go = Object.Instantiate(_textPrefab, container);

                        // Set text
                        go.GetComponentInChildren<TextMeshProUGUI>().text =
                            Localize.Key("Settings.Text", text.TextName);

                        break;
                    }
                    case LobbyServerListMetadata:
                    {
                        EnsureLobbyServerPrefabsLoaded();
                        
                        // Spawn the lobby server header with "Add New" button
                        if (_lobbyServerHeaderPrefab != null)
                        {
                            Object.Instantiate(_lobbyServerHeaderPrefab, container);
                        }

                        // Create entries for each lobby server
                        if (_lobbyServerEntryPrefab != null)
                        {
                            var lobbyServers = NetworkSettingsStore.Instance?.Settings?.lobbyServers;
                            if (lobbyServers != null)
                            {
                                for (int i = 0; i < lobbyServers.Count; i++)
                                {
                                    var go = Object.Instantiate(_lobbyServerEntryPrefab, container);
                                    go.GetComponent<LobbyServerEntry>().SetIndex(i);
                                }
                            }
                        }

                        break;
                    }
                    case FieldMetadata field:
                    {
                        var setting = SettingsManager.GetSettingByName(field.FieldName);

                        var visual = SpawnSettingVisual(setting, container);
                        visual.AssignSetting(field.FieldName, field.HasDescription);
                        visual.AssignIndex(settingIndex);

                        _settingVisuals.Add(field.FieldName, visual);
                        navGroup.AddNavigatable(visual.gameObject);

                        settingIndex++;
                        break;
                    }
                }
            }
        }

        // For collection initializer support
        public void Add(AbstractMetadata setting) => _settings.Add(setting);
        private List<AbstractMetadata>.Enumerator GetEnumerator() => _settings.GetEnumerator();
        IEnumerator<AbstractMetadata> IEnumerable<AbstractMetadata>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
