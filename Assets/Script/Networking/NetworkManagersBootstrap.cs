using UnityEngine;
using YARG.Networking.Abstraction;
using YARG.Networking.Bands;
using YARG.Networking.Tracks;
using YARG.Networking.Session;
using YARG.Networking.Gameplay;
using YARG.Networking.Settings;

namespace YARG.Networking
{
    /// <summary>
    /// Bootstrap class responsible for initializing all networking managers.
    /// Call Initialize() during application startup (typically from GlobalVariables).
    /// </summary>
    public static class NetworkManagersBootstrap
    {
        private static bool _isInitialized;
        private static GameObject _managersRoot;

        /// <summary>
        /// Whether the networking subsystem has been initialized.
        /// </summary>
        public static bool IsInitialized => _isInitialized;

        /// <summary>
        /// Initialize all networking subsystems.
        /// Safe to call multiple times - will only initialize once.
        /// </summary>
        public static void Initialize()
        {
            if (_isInitialized)
            {
                Debug.Log("[NetworkManagersBootstrap] Already initialized, skipping");
                return;
            }

            Debug.Log("[NetworkManagersBootstrap] Initializing networking subsystems...");

            // 1. Initialize the settings store first (non-MonoBehaviour singleton)
            EnsureSettingsLoaded();

            // 2. Create a root GameObject for all network managers
            _managersRoot = new GameObject("Network Managers");
            Object.DontDestroyOnLoad(_managersRoot);

            // 3. Initialize core networking service
            NetworkingServiceFactory.Initialize();

            // 4. Create MonoBehaviour-based managers as children
            CreateManagerComponents();

            _isInitialized = true;
            Debug.Log("[NetworkManagersBootstrap] Networking subsystems initialized");
        }

        /// <summary>
        /// Shutdown all networking subsystems.
        /// </summary>
        public static void Shutdown()
        {
            if (!_isInitialized)
            {
                return;
            }

            Debug.Log("[NetworkManagersBootstrap] Shutting down networking subsystems...");

            // Shutdown the networking service
            NetworkingServiceFactory.Shutdown();

            // Destroy managers root (will destroy all child managers)
            if (_managersRoot != null)
            {
                Object.Destroy(_managersRoot);
                _managersRoot = null;
            }

            // Save settings
            NetworkSettingsStore.Instance.Save();

            _isInitialized = false;
            Debug.Log("[NetworkManagersBootstrap] Networking subsystems shut down");
        }

        private static void EnsureSettingsLoaded()
        {
            // Access the instance to trigger lazy initialization and load
            // The constructor automatically loads settings or creates defaults
            var store = NetworkSettingsStore.Instance;
            if (store == null)
            {
                Debug.LogError("[NetworkManagersBootstrap] Failed to initialize NetworkSettingsStore");
                return;
            }

            // Settings are automatically loaded/created by the store constructor
            if (store.Settings == null)
            {
                Debug.LogError("[NetworkManagersBootstrap] NetworkSettingsStore has null settings - this shouldn't happen");
            }
            else
            {
                Debug.Log("[NetworkManagersBootstrap] Network settings loaded");
            }
        }

        private static void CreateManagerComponents()
        {
            // Create BandManager
            var bandManagerGo = new GameObject("BandManager");
            bandManagerGo.transform.SetParent(_managersRoot.transform);
            bandManagerGo.AddComponent<BandManager>();
            Debug.Log("[NetworkManagersBootstrap] Created BandManager");

            // Create TrackOrderManager
            var trackOrderGo = new GameObject("TrackOrderManager");
            trackOrderGo.transform.SetParent(_managersRoot.transform);
            trackOrderGo.AddComponent<TrackOrderManager>();
            Debug.Log("[NetworkManagersBootstrap] Created TrackOrderManager");

            // Create SessionLifecycleManager
            var sessionGo = new GameObject("SessionLifecycleManager");
            sessionGo.transform.SetParent(_managersRoot.transform);
            sessionGo.AddComponent<SessionLifecycleManager>();
            Debug.Log("[NetworkManagersBootstrap] Created SessionLifecycleManager");

            // Create MultiplayerGameplaySettings
            var gameplayGo = new GameObject("MultiplayerGameplaySettings");
            gameplayGo.transform.SetParent(_managersRoot.transform);
            gameplayGo.AddComponent<MultiplayerGameplaySettings>();
            Debug.Log("[NetworkManagersBootstrap] Created MultiplayerGameplaySettings");
        }

        /// <summary>
        /// Get the root GameObject containing all network managers.
        /// </summary>
        public static GameObject GetManagersRoot()
        {
            return _managersRoot;
        }

        /// <summary>
        /// Check if a specific manager type is available.
        /// </summary>
        public static T GetManager<T>() where T : MonoBehaviour
        {
            if (_managersRoot == null)
            {
                return null;
            }

            return _managersRoot.GetComponentInChildren<T>();
        }
    }
}
