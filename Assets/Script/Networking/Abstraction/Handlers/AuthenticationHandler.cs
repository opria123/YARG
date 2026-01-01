using System;
using YARG.Net;
using YARG.Net.Packets;
using YARG.Net.Sessions;
using YARG.Net.Transport;

namespace YARG.Networking.Abstraction.Handlers
{
    /// <summary>
    /// Handles authentication for password-protected lobbies.
    /// </summary>
    public sealed class AuthenticationHandler : IDisposable
    {
        private readonly LobbyAuthenticator _authenticator;
        private string _lobbyPassword;
        private string _pendingPassword;
        
        /// <summary>
        /// Fired when authentication succeeds (client only).
        /// </summary>
        public event Action OnAuthenticationSucceeded;
        
        /// <summary>
        /// Fired when authentication fails.
        /// Parameter: error message
        /// </summary>
        public event Action<string> OnAuthenticationFailed;
        
        /// <summary>
        /// Fired when a client successfully authenticates (host only).
        /// </summary>
        public event Action<INetConnection> OnClientAuthenticated;

        public AuthenticationHandler(LobbyAuthenticator authenticator = null)
        {
            _authenticator = authenticator ?? new LobbyAuthenticator();
        }

        #region Configuration

        /// <summary>
        /// Set the lobby password (host only).
        /// </summary>
        public void SetLobbyPassword(string password)
        {
            _lobbyPassword = password;
            _authenticator.SetPassword(password);
            NetworkLogger.Verbose("Lobby password set (hasPassword={0})", !string.IsNullOrEmpty(password));
        }

        /// <summary>
        /// Set the password to use when joining (client only).
        /// </summary>
        public void SetJoinPassword(string password)
        {
            _pendingPassword = password;
        }

        /// <summary>
        /// Check if authentication is required for the lobby.
        /// </summary>
        public bool IsAuthenticationRequired(bool lobbyHasPassword)
        {
            return lobbyHasPassword && !string.IsNullOrEmpty(_lobbyPassword);
        }

        #endregion

        #region Client-Side

        /// <summary>
        /// Build an authentication request packet (client only).
        /// </summary>
        public byte[] BuildAuthRequest()
        {
            return AuthenticationBinaryPackets.BuildRequestPacket(_pendingPassword ?? string.Empty);
        }

        /// <summary>
        /// Send authentication request to the server.
        /// </summary>
        public void SendAuthRequest(INetConnection connection)
        {
            NetworkLogger.Client("Sending authentication request");
            
            byte[] message = BuildAuthRequest();
            connection.Send(message, ChannelType.ReliableOrdered);
            
            NetworkLogger.Client("Sent authentication request");
        }

        /// <summary>
        /// Handle authentication response from server (client only).
        /// </summary>
        public void HandleAuthResponseMessage(ReadOnlyMemory<byte> payload, Action sendPlayerIdentity)
        {
            if (!AuthenticationBinaryPackets.TryParseResponsePacket(payload.Span, out bool success, out string message))
            {
                NetworkLogger.Warn("Invalid auth response message");
                OnAuthenticationFailed?.Invoke("Invalid authentication response");
                return;
            }
            
            if (success)
            {
                NetworkLogger.Client("Authentication successful");
                OnAuthenticationSucceeded?.Invoke();
                
                // Now send player identity
                sendPlayerIdentity?.Invoke();
            }
            else
            {
                NetworkLogger.Client("Authentication failed: {0}", message);
                OnAuthenticationFailed?.Invoke(message ?? "Authentication failed");
            }
        }

        #endregion

        #region Host-Side

        /// <summary>
        /// Handle authentication request from client (host only).
        /// </summary>
        public void HandleAuthRequestMessage(INetConnection connection, ReadOnlyMemory<byte> payload)
        {
            if (!AuthenticationBinaryPackets.TryParseRequestPacket(payload.Span, out string clientPassword))
            {
                NetworkLogger.Warn("Invalid auth request from {0}", connection.EndPoint);
                SendAuthResponse(connection, false, "Invalid request format");
                return;
            }
            
            // Validate password
            bool success = _authenticator.Validate(clientPassword, out string result);
            
            NetworkLogger.Server("Auth {0} for {1} - {2}", 
                success ? "success" : "failed", connection.EndPoint, result);
            
            SendAuthResponse(connection, success, result);
            
            if (success)
            {
                OnClientAuthenticated?.Invoke(connection);
            }
        }

        private void SendAuthResponse(INetConnection connection, bool success, string message)
        {
            byte[] response = AuthenticationBinaryPackets.BuildResponsePacket(success, message);
            connection.Send(response, ChannelType.ReliableOrdered);
        }

        #endregion

        public void Clear()
        {
            _lobbyPassword = null;
            _pendingPassword = null;
            _authenticator?.Clear();
        }

        public void Dispose()
        {
            Clear();
        }
    }
}
