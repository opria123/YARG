using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using YARG.Net.Utilities.UPnP;

namespace YARG.Networking.UPnP
{
    /// <summary>
    /// Unity-friendly wrapper for UPnP port forwarding operations.
    /// Used by the Lobby system to automatically open ports on supported routers.
    /// </summary>
    public sealed class UPnPPortForwarder : IDisposable
    {
        private const string MappingDescription = "YARG Multiplayer";

        private readonly UPnPClient _client;
        private int _mappedPort;
        private PortMappingProtocol _mappedProtocol;
        private bool _hasMappedPort;
        private bool _disposed;

        /// <summary>
        /// Gets whether a UPnP-capable gateway was discovered.
        /// </summary>
        public bool IsAvailable => _client.IsAvailable;

        /// <summary>
        /// Gets the friendly name of the discovered gateway device.
        /// </summary>
        public string? GatewayName => _client.Device?.FriendlyName;

        /// <summary>
        /// Gets whether a port is currently mapped.
        /// </summary>
        public bool HasMappedPort => _hasMappedPort;

        /// <summary>
        /// Gets the currently mapped port, or 0 if none.
        /// </summary>
        public int MappedPort => _hasMappedPort ? _mappedPort : 0;

        public UPnPPortForwarder()
        {
            _client = new UPnPClient(TimeSpan.FromSeconds(3));
        }

        /// <summary>
        /// Discovers UPnP gateways on the network.
        /// </summary>
        /// <returns>True if a compatible gateway was found.</returns>
        public async UniTask<bool> DiscoverAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(UPnPPortForwarder));

            try
            {
                var result = await _client.DiscoverAsync(cancellationToken).AsUniTask();
                
                if (result)
                {
                    Debug.Log($"[UPnP] Discovered gateway: {_client.Device?.FriendlyName}");
                }
                else
                {
                    Debug.Log("[UPnP] No compatible gateway found");
                }

                return result;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UPnP] Discovery failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Opens a UDP port for YARG multiplayer.
        /// </summary>
        /// <param name="port">The port to open.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>True if the port was successfully mapped.</returns>
        public async UniTask<bool> OpenPortAsync(int port, CancellationToken cancellationToken = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(UPnPPortForwarder));

            if (!_client.IsAvailable)
            {
                Debug.LogWarning("[UPnP] No gateway available. Call DiscoverAsync first.");
                return false;
            }

            // Close existing mapping if different port
            if (_hasMappedPort && _mappedPort != port)
            {
                await ClosePortAsync(cancellationToken);
            }

            var mapping = new PortMapping(
                ExternalPort: port,
                InternalPort: port,
                Protocol: PortMappingProtocol.UDP,
                Description: MappingDescription,
                LeaseDuration: 0 // Permanent until removed
            );

            var result = await _client.AddPortMappingAsync(mapping, cancellationToken).AsUniTask();

            if (result.IsSuccess)
            {
                _mappedPort = port;
                _mappedProtocol = PortMappingProtocol.UDP;
                _hasMappedPort = true;
                Debug.Log($"[UPnP] Successfully opened UDP port {port}");
                return true;
            }
            else
            {
                Debug.LogWarning($"[UPnP] Failed to open port {port}: {result.Error}");
                return false;
            }
        }

        /// <summary>
        /// Closes the currently mapped port.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>True if the port was successfully closed.</returns>
        public async UniTask<bool> ClosePortAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(UPnPPortForwarder));

            if (!_hasMappedPort)
                return true;

            if (!_client.IsAvailable)
            {
                _hasMappedPort = false;
                return true;
            }

            var result = await _client.RemovePortMappingAsync(_mappedPort, _mappedProtocol, cancellationToken).AsUniTask();

            if (result.IsSuccess)
            {
                Debug.Log($"[UPnP] Closed port {_mappedPort}");
            }
            else
            {
                Debug.LogWarning($"[UPnP] Failed to close port {_mappedPort}: {result.Error}");
            }

            // Clear mapping state regardless of result
            _hasMappedPort = false;
            _mappedPort = 0;

            return result.IsSuccess;
        }

        /// <summary>
        /// Gets the external IP address from the gateway.
        /// </summary>
        /// <returns>The external IP, or null if unavailable.</returns>
        public async UniTask<string?> GetExternalIPAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(UPnPPortForwarder));

            if (!_client.IsAvailable)
                return null;

            return await _client.GetExternalIPAddressAsync(cancellationToken).AsUniTask();
        }

        /// <summary>
        /// Checks if a specific port is already mapped.
        /// </summary>
        public async UniTask<bool> IsPortMappedAsync(int port, CancellationToken cancellationToken = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(UPnPPortForwarder));

            if (!_client.IsAvailable)
                return false;

            var existing = await _client.GetPortMappingAsync(port, PortMappingProtocol.UDP, cancellationToken).AsUniTask();
            return existing != null;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            // Try to clean up mapping synchronously if possible
            if (_hasMappedPort && _client.IsAvailable)
            {
                try
                {
                    // Fire and forget cleanup
                    _ = _client.RemovePortMappingAsync(_mappedPort, _mappedProtocol);
                }
                catch
                {
                    // Best effort cleanup
                }
            }

            _client.Dispose();
        }
    }
}
