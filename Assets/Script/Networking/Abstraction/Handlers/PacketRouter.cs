using System;
using System.Collections.Generic;
using YARG.Net;
using YARG.Net.Packets;
using YARG.Net.Transport;

namespace YARG.Networking.Abstraction.Handlers
{
    /// <summary>
    /// Routes incoming packets to the appropriate handlers based on packet type.
    /// Centralizes the packet routing logic from the adapter.
    /// </summary>
    public sealed class PacketRouter : IDisposable
    {
        private readonly bool _isHostingProvider;
        private Func<bool> _isHosting;
        
        /// <summary>
        /// Handler delegate for packet processing.
        /// </summary>
        public delegate void PacketHandler(INetConnection connection, ReadOnlyMemory<byte> payload);
        
        private readonly Dictionary<PacketType, PacketHandler> _hostOnlyHandlers = new();
        private readonly Dictionary<PacketType, PacketHandler> _clientOnlyHandlers = new();
        private readonly Dictionary<PacketType, PacketHandler> _bothHandlers = new();
        
        /// <summary>
        /// Creates a new PacketRouter.
        /// </summary>
        /// <param name="isHosting">Function to check if we're currently hosting.</param>
        public PacketRouter(Func<bool> isHosting)
        {
            _isHosting = isHosting ?? throw new ArgumentNullException(nameof(isHosting));
        }
        
        /// <summary>
        /// Register a handler for a packet type that should only be processed by the host.
        /// </summary>
        public void RegisterHostOnly(PacketType type, PacketHandler handler)
        {
            _hostOnlyHandlers[type] = handler;
            NetworkLogger.Verbose("Registered host-only handler for {0}", type);
        }
        
        /// <summary>
        /// Register a handler for a packet type that should only be processed by clients.
        /// </summary>
        public void RegisterClientOnly(PacketType type, PacketHandler handler)
        {
            _clientOnlyHandlers[type] = handler;
            NetworkLogger.Verbose("Registered client-only handler for {0}", type);
        }
        
        /// <summary>
        /// Register a handler for a packet type that can be processed by both host and client.
        /// </summary>
        public void RegisterBoth(PacketType type, PacketHandler handler)
        {
            _bothHandlers[type] = handler;
            NetworkLogger.Verbose("Registered both-side handler for {0}", type);
        }
        
        /// <summary>
        /// Unregister a handler for a packet type.
        /// </summary>
        public void Unregister(PacketType type)
        {
            _hostOnlyHandlers.Remove(type);
            _clientOnlyHandlers.Remove(type);
            _bothHandlers.Remove(type);
        }
        
        /// <summary>
        /// Route an incoming packet to the appropriate handler.
        /// </summary>
        /// <returns>True if the packet was handled, false otherwise.</returns>
        public bool RoutePacket(INetConnection connection, ReadOnlyMemory<byte> payload, ChannelType channel)
        {
            if (payload.Length < 1) return false;
            
            var packetType = (PacketType)payload.Span[0];
            bool isHost = _isHosting();
            
            NetworkLogger.Packet(packetType.ToString(), "Routing packet, isHost={0}", isHost);
            
            // Check "both" handlers first (they apply regardless of role)
            if (_bothHandlers.TryGetValue(packetType, out var bothHandler))
            {
                bothHandler(connection, payload);
                return true;
            }
            
            // Check role-specific handlers
            if (isHost)
            {
                if (_hostOnlyHandlers.TryGetValue(packetType, out var hostHandler))
                {
                    hostHandler(connection, payload);
                    return true;
                }
            }
            else
            {
                if (_clientOnlyHandlers.TryGetValue(packetType, out var clientHandler))
                {
                    clientHandler(connection, payload);
                    return true;
                }
            }
            
            NetworkLogger.Verbose("No handler registered for packet type {0} (isHost={1})", packetType, isHost);
            return false;
        }
        
        /// <summary>
        /// Clear all registered handlers.
        /// </summary>
        public void ClearHandlers()
        {
            _hostOnlyHandlers.Clear();
            _clientOnlyHandlers.Clear();
            _bothHandlers.Clear();
        }
        
        public void Dispose()
        {
            ClearHandlers();
        }
    }
}
