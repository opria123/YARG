using UnityEngine;

namespace YARG.Networking.Abstraction
{
    /// <summary>
    /// Factory for creating and accessing the networking service singleton.
    /// Uses LiteNetLib-based implementation (YARG.Net).
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
        /// Initialize the networking service.
        /// </summary>
        public static void Initialize()
        {
            if (_isInitialized)
            {
                return;
            }

            _instance = new LiteNetNetworkingAdapter();
            
            if (_instance != null)
            {
                _instance.Initialize();
                _isInitialized = true;
                Debug.Log("[NetworkingServiceFactory] Initialized LiteNet networking");
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
    }
}
