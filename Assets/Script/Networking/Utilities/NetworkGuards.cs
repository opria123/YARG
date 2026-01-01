using System.Diagnostics.CodeAnalysis;
using YARG.Core.Logging;
using YARG.Networking.Abstraction;
using YARG.Networking.Abstraction.Handlers;
using YARG.Networking.Bands;

namespace YARG.Networking.Utilities
{
    /// <summary>
    /// Helper methods for safely accessing networking singletons with null checks.
    /// Use these to reduce boilerplate null-check patterns throughout the codebase.
    /// </summary>
    public static class NetworkGuards
    {
        /// <summary>
        /// Attempts to get the networking service instance.
        /// </summary>
        /// <param name="service">The networking service if available.</param>
        /// <returns>True if the service is available, false otherwise.</returns>
        public static bool TryGetNetworkService([NotNullWhen(true)] out INetworkingService? service)
        {
            service = NetworkingServiceFactory.Instance;
            return service != null;
        }
        
        /// <summary>
        /// Attempts to get the band manager instance.
        /// </summary>
        /// <param name="manager">The band manager if available.</param>
        /// <returns>True if the manager is available, false otherwise.</returns>
        public static bool TryGetBandManager([NotNullWhen(true)] out BandManager? manager)
        {
            manager = BandManager.Instance;
            return manager != null;
        }
        
        /// <summary>
        /// Requires the networking service to be available.
        /// Logs an error and returns null if not available.
        /// </summary>
        /// <param name="caller">Name of the calling method for logging.</param>
        /// <returns>The networking service, or null if not available.</returns>
        public static INetworkingService? RequireNetworkService(string caller = "")
        {
            var service = NetworkingServiceFactory.Instance;
            if (service == null)
            {
                NetworkLogger.Error($"[{caller}] NetworkingService not available");
            }
            return service;
        }
        
        /// <summary>
        /// Requires the band manager to be available.
        /// Logs an error and returns null if not available.
        /// </summary>
        /// <param name="caller">Name of the calling method for logging.</param>
        /// <returns>The band manager, or null if not available.</returns>
        public static BandManager? RequireBandManager(string caller = "")
        {
            var manager = BandManager.Instance;
            if (manager == null)
            {
                NetworkLogger.Error($"[{caller}] BandManager not available");
            }
            return manager;
        }
    }
}
