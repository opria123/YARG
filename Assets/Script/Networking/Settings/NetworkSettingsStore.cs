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
                    if (_settings?.lobbyServers != null)
                    {
                        Debug.Log($"[NetworkSettingsStore] Loaded {_settings.lobbyServers.Count} lobbyServers from file (before EnsureDefaults):");
                        foreach (var intro in _settings.lobbyServers)
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
            
            // Log loaded lobbyServers for debugging
            var enabledLobbyServers = _settings.EnabledLobbyServers;
            Debug.Log($"[NetworkSettingsStore] After EnsureDefaults - {_settings.lobbyServers?.Count ?? 0} lobbyServers, {enabledLobbyServers?.Count ?? 0} enabled:");
            if (_settings.lobbyServers != null)
            {
                foreach (var intro in _settings.lobbyServers)
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
        /// Adds a new lobby server and saves.
        /// </summary>
        public LobbyServerEndpoint AddLobbyServer(string displayName, string url)
        {
            var endpoint = _settings.AddLobbyServer(displayName, url);
            Save();
            return endpoint;
        }

        /// <summary>
        /// Removes a lobby server and saves.
        /// </summary>
        public bool RemoveLobbyServer(string id)
        {
            if (_settings.RemoveLobbyServer(id))
            {
                Save();
                return true;
            }
            return false;
        }

        /// <summary>
        /// Enables or disables a lobby server and saves.
        /// </summary>
        public bool SetLobbyServerEnabled(string id, bool enabled)
        {
            Debug.Log($"[NetworkSettingsStore] SetLobbyServerEnabled called: id={id}, enabled={enabled}");
            if (_settings.SetLobbyServerEnabled(id, enabled))
            {
                Debug.Log($"[NetworkSettingsStore] LobbyServer {id} enabled state changed to {enabled}, saving...");
                Save();
                return true;
            }
            Debug.LogWarning($"[NetworkSettingsStore] SetLobbyServerEnabled failed for id={id}");
            return false;
        }

        /// <summary>
        /// Updates a lobby server's display name and/or URL and saves.
        /// </summary>
        public bool UpdateLobbyServer(string id, string displayName = null, string url = null)
        {
            var endpoint = _settings.lobbyServers?.Find(i => i.id == id);
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
        /// Records a successful contact with a lobby server.
        /// </summary>
        public void RecordlobbyServersuccess(string id)
        {
            var endpoint = _settings.lobbyServers?.Find(i => i.id == id);
            if (endpoint != null)
            {
                endpoint.RecordSuccess();
                Save();
            }
        }

        /// <summary>
        /// Records a failed contact with a lobby server.
        /// </summary>
        public void RecordLobbyServerFailure(string id)
        {
            var endpoint = _settings.lobbyServers?.Find(i => i.id == id);
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
