using UnityEngine;

namespace YARG.Networking.Abstraction
{
    /// <summary>
    /// Factory for creating and accessing the networking service singleton.
    /// Allows switching between Mirror and LiteNet implementations via feature flag.
    /// </summary>
    public static class NetworkingServiceFactory
    {
        private static INetworkingService _instance;
        private static bool _isInitialized;

        /// <summary>
        /// Get the current networking service instance.
        /// </summary>
        public static INetworkingService Instance
        {
            get
            {
                if (!_isInitialized)
                {
                    Initialize();
                }
                return _instance;
            }
        }

        /// <summary>
        /// Initialize the networking service based on settings.
        /// </summary>
        public static void Initialize()
        {
            if (_isInitialized)
            {
                return;
            }

            // For now, always use Mirror adapter (Step 3 will implement this)
            // In the future, this will check SettingsManager.Settings.UseExperimentalNetworking
            _instance = CreateMirrorAdapter();
            
            if (_instance != null)
            {
                _instance.Initialize();
                _isInitialized = true;
                Debug.Log($"[NetworkingServiceFactory] Initialized with {_instance.GetType().Name}");
            }
            else
            {
                Debug.LogError("[NetworkingServiceFactory] Failed to create networking service!");
            }
        }

        /// <summary>
        /// Explicitly set the networking service (primarily for testing).
        /// </summary>
        public static void SetInstance(INetworkingService service)
        {
            if (_isInitialized && _instance != null)
            {
                _instance.Shutdown();
            }

            _instance = service;
            _isInitialized = service != null;

            if (service != null)
            {
                service.Initialize();
            }
        }

        /// <summary>
        /// Shutdown the current networking service.
        /// </summary>
        public static void Shutdown()
        {
            if (_instance != null)
            {
                _instance.Shutdown();
                _instance = null;
            }
            _isInitialized = false;
        }

        /// <summary>
        /// Reset the factory (primarily for testing).
        /// </summary>
        public static void Reset()
        {
            Shutdown();
        }

        private static INetworkingService CreateMirrorAdapter()
        {
            // Step 3 will implement this - for now return null
            // This will look for YargNetworkManager.Instance and wrap it
            Debug.LogWarning("[NetworkingServiceFactory] Mirror adapter not yet implemented");
            return null;
        }

        private static INetworkingService CreateLiteNetAdapter()
        {
            // Step 4 will implement this
            Debug.LogWarning("[NetworkingServiceFactory] LiteNet adapter not yet implemented");
            return null;
        }
    }
}
