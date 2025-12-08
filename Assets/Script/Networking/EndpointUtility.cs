using System;
using NetEndpointUtility = YARG.Net.Utilities.EndpointUtility;

namespace YARG.Networking
{
    /// <summary>
    /// Unity-side wrapper around YARG.Net.Utilities.EndpointUtility with access to 
    /// Unity-specific defaults like NetworkTransportDefaults.
    /// </summary>
    public static class EndpointUtility
    {
        /// <summary>
        /// Attempts to parse an endpoint string into address and port components.
        /// Uses NetworkTransportDefaults.DefaultUdpPort as the fallback.
        /// </summary>
        /// <param name="input">Player provided endpoint (hostname, IPv4/6, optional port).</param>
        /// <param name="address">Resulting normalized address without brackets.</param>
        /// <param name="port">Resulting port number.</param>
        /// <param name="error">Validation error message when parsing fails.</param>
        public static bool TryParseEndpoint(string input, out string address, out int port, out string error)
        {
            return NetEndpointUtility.TryParseEndpoint(input, NetworkTransportDefaults.DefaultUdpPort, out address, out port, out error);
        }

        /// <summary>
        /// Attempts to parse an endpoint string into address and port components.
        /// </summary>
        /// <param name="input">Player provided endpoint (hostname, IPv4/6, optional port).</param>
        /// <param name="fallbackPort">Port used when the endpoint omits an explicit port.</param>
        /// <param name="address">Resulting normalized address without brackets.</param>
        /// <param name="port">Resulting port number.</param>
        /// <param name="error">Validation error message when parsing fails.</param>
        public static bool TryParseEndpoint(string input, int fallbackPort, out string address, out int port, out string error)
        {
            return NetEndpointUtility.TryParseEndpoint(input, fallbackPort, out address, out port, out error);
        }

        /// <summary>
        /// Formats an address/port pair into a user-facing endpoint string.
        /// </summary>
        public static string FormatEndpoint(string address, int port)
        {
            return NetEndpointUtility.FormatEndpoint(address, port);
        }

        /// <summary>
        /// Returns just the address portion from an endpoint string.
        /// </summary>
        public static string GetAddress(string endpoint)
        {
            return NetEndpointUtility.GetAddress(endpoint, NetworkTransportDefaults.DefaultUdpPort);
        }

        /// <summary>
        /// Returns just the port portion from an endpoint string.
        /// </summary>
        public static int GetPort(string endpoint)
        {
            return NetEndpointUtility.GetPort(endpoint, NetworkTransportDefaults.DefaultUdpPort);
        }
    }
}
