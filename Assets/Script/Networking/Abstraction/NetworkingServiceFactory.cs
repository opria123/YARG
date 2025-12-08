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

            // Check feature flag to determine which implementation to use
            // TEMPORARY: Force LiteNet for testing
            bool useLiteNet = true; // YARG.Settings.SettingsManager.Settings.UseExperimentalNetworking.Value;
            
            _instance = useLiteNet ? CreateLiteNetAdapter() : CreateMirrorAdapter();
            
            if (_instance != null)
            {
                _instance.Initialize();
                _isInitialized = true;
                string implName = useLiteNet ? "LiteNet (Experimental)" : "Mirror";
                Debug.Log($"[NetworkingServiceFactory] Initialized with {implName} implementation");
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
            // Wait for YargNetworkManager to be initialized
            if (YargNetworkManager.Instance == null)
            {
                Debug.LogError("[NetworkingServiceFactory] YargNetworkManager.Instance is null. Cannot create Mirror adapter.");
                return null;
            }

            Debug.Log("[NetworkingServiceFactory] Creating MirrorNetworkingAdapter");
            return new MirrorNetworkingAdapter();
        }

        private static INetworkingService CreateLiteNetAdapter()
        {
            Debug.Log("[NetworkingServiceFactory] Creating LiteNetNetworkingAdapter");
            return new LiteNetNetworkingAdapter();
        }
    }
}
