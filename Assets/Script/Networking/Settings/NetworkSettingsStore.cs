using System;
using System.IO;
using UnityEngine;

namespace YARG.Networking.Settings
{
    /// <summary>
    /// Persists global network settings to disk.
    /// Settings are stored in network_settings.json in the persistent data path.
    /// </summary>
    public sealed class NetworkSettingsStore
    {
        private const string SettingsFileName = "network_settings.json";

        private static NetworkSettingsStore _instance;
        private static readonly object _lock = new();

        private readonly string _settingsPath;
        private NetworkGlobalSettings _settings;

        /// <summary>
        /// Gets the singleton instance of the settings store.
        /// </summary>
        public static NetworkSettingsStore Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        _instance ??= new NetworkSettingsStore();
                    }
                }
                return _instance;
            }
        }

        /// <summary>
        /// Gets the current global network settings.
        /// </summary>
        public NetworkGlobalSettings Settings => _settings;

        /// <summary>
        /// Event fired when settings are changed and saved.
        /// </summary>
        public event Action SettingsChanged;

        private NetworkSettingsStore()
        {
            _settingsPath = Path.Combine(Application.persistentDataPath, SettingsFileName);
            Load();
        }

        /// <summary>
        /// Loads settings from disk, or creates defaults if not found.
        /// </summary>
        public void Load()
        {
            _settings = null;

            if (File.Exists(_settingsPath))
            {
                try
                {
                    var json = File.ReadAllText(_settingsPath);
                    Debug.Log($"[NetworkSettingsStore] Loading from {_settingsPath}:\n{json}");
                    _settings = JsonUtility.FromJson<NetworkGlobalSettings>(json);
                    
                    // Log what was loaded BEFORE EnsureDefaults
                    if (_settings?.introducers != null)
                    {
                        Debug.Log($"[NetworkSettingsStore] Loaded {_settings.introducers.Count} introducers from file (before EnsureDefaults):");
                        foreach (var intro in _settings.introducers)
                        {
                            Debug.Log($"  - {intro.displayName} ({intro.url}) [enabled={intro.enabled}, id={intro.id}]");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[NetworkSettingsStore] Failed to load settings: {ex.Message}");
                }
            }
            else
            {
                Debug.Log($"[NetworkSettingsStore] Settings file not found at {_settingsPath}, will create defaults");
            }

            if (_settings == null)
            {
                _settings = NetworkGlobalSettings.CreateDefault();
            }

            _settings.EnsureDefaults();
            
            // Log loaded introducers for debugging
            var enabledIntroducers = _settings.EnabledIntroducers;
            Debug.Log($"[NetworkSettingsStore] After EnsureDefaults - {_settings.introducers?.Count ?? 0} introducers, {enabledIntroducers?.Count ?? 0} enabled:");
            if (_settings.introducers != null)
            {
                foreach (var intro in _settings.introducers)
                {
                    Debug.Log($"  - {intro.displayName} ({intro.url}) [enabled={intro.enabled}, builtIn={intro.isBuiltIn}]");
                }
            }
        }

        /// <summary>
        /// Saves current settings to disk.
        /// </summary>
        public void Save()
        {
            try
            {
                _settings.EnsureDefaults();
                var json = JsonUtility.ToJson(_settings, true);
                Debug.Log($"[NetworkSettingsStore] Saving to {_settingsPath}:\n{json}");
                File.WriteAllText(_settingsPath, json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NetworkSettingsStore] Failed to save settings: {ex.Message}");
            }

            SettingsChanged?.Invoke();
        }

        /// <summary>
        /// Adds a new introducer and saves.
        /// </summary>
        public IntroducerEndpoint AddIntroducer(string displayName, string url)
        {
            var endpoint = _settings.AddIntroducer(displayName, url);
            Save();
            return endpoint;
        }

        /// <summary>
        /// Removes an introducer and saves.
        /// </summary>
        public bool RemoveIntroducer(string id)
        {
            if (_settings.RemoveIntroducer(id))
            {
                Save();
                return true;
            }
            return false;
        }

        /// <summary>
        /// Enables or disables an introducer and saves.
        /// </summary>
        public bool SetIntroducerEnabled(string id, bool enabled)
        {
            Debug.Log($"[NetworkSettingsStore] SetIntroducerEnabled called: id={id}, enabled={enabled}");
            if (_settings.SetIntroducerEnabled(id, enabled))
            {
                Debug.Log($"[NetworkSettingsStore] Introducer {id} enabled state changed to {enabled}, saving...");
                Save();
                return true;
            }
            Debug.LogWarning($"[NetworkSettingsStore] SetIntroducerEnabled failed for id={id}");
            return false;
        }

        /// <summary>
        /// Updates an introducer's display name and/or URL and saves.
        /// </summary>
        public bool UpdateIntroducer(string id, string displayName = null, string url = null)
        {
            var endpoint = _settings.introducers?.Find(i => i.id == id);
            if (endpoint == null)
                return false;

            bool changed = false;

            if (displayName != null && !string.Equals(endpoint.displayName, displayName))
            {
                endpoint.displayName = displayName.Trim();
                changed = true;
            }

            if (url != null && !string.Equals(endpoint.url, url))
            {
                endpoint.url = url.Trim();
                changed = true;
            }

            if (changed)
            {
                Save();
            }

            return changed;
        }

        /// <summary>
        /// Records a successful contact with an introducer.
        /// </summary>
        public void RecordIntroducerSuccess(string id)
        {
            var endpoint = _settings.introducers?.Find(i => i.id == id);
            if (endpoint != null)
            {
                endpoint.RecordSuccess();
                Save();
            }
        }

        /// <summary>
        /// Records a failed contact with an introducer.
        /// </summary>
        public void RecordIntroducerFailure(string id)
        {
            var endpoint = _settings.introducers?.Find(i => i.id == id);
            if (endpoint != null)
            {
                endpoint.RecordFailure();
                Save();
            }
        }

        /// <summary>
        /// Updates the last used settings from a session preset and saves.
        /// </summary>
        public void UpdateLastUsedSettings(SessionPreset preset)
        {
            _settings.UpdateLastUsedFromPreset(preset);
            Save();
        }

        /// <summary>
        /// Sets the default port and saves.
        /// </summary>
        public void SetDefaultPort(int port)
        {
            if (port < 1 || port > 65535)
                return;

            _settings.defaultPort = port;
            Save();
        }

        /// <summary>
        /// Sets the global "local players first" preference and saves.
        /// </summary>
        public void SetLocalPlayersFirst(bool value)
        {
            _settings.localPlayersFirst = value;
            Save();
        }

        /// <summary>
        /// Resets all settings to defaults and saves.
        /// </summary>
        public void ResetToDefaults()
        {
            _settings = NetworkGlobalSettings.CreateDefault();
            Save();
        }

        /// <summary>
        /// Gets the settings file path (for debugging).
        /// </summary>
        public string GetSettingsPath() => _settingsPath;
    }
}
