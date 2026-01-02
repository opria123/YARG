namespace YARG.Networking.Settings
{
    /// <summary>
    /// Defines the type of network session being hosted.
    /// </summary>
    public enum SessionType
    {
        /// <summary>
        /// A server requires manual port forwarding but works without any external services.
        /// Players connect via direct IP:Port, LAN discovery, or lobby server browser.
        /// </summary>
        Server = 0,

        /// <summary>
        /// A lobby uses UPnP for automatic port mapping and registers with lobby servers
        /// to get a shareable lobby code. Hard fails if UPnP is unavailable.
        /// </summary>
        Lobby = 1
    }

    /// <summary>
    /// Defines the privacy/visibility mode for a session.
    /// </summary>
    public enum SessionPrivacyMode
    {
        /// <summary>
        /// Visible in LAN discovery and lobby server browsers. No password required.
        /// </summary>
        Public = 0,

        /// <summary>
        /// Visible in LAN discovery and lobby server browsers, but password required to join.
        /// </summary>
        Private = 1,

        /// <summary>
        /// Not visible in browsers. Only joinable via lobby code (Lobby mode) or direct connect (Server mode).
        /// </summary>
        Unlisted = 2
    }
}
