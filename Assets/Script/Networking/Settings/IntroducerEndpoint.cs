using System;

namespace YARG.Networking.Settings
{
    /// <summary>
    /// Represents a single introducer service endpoint that can be used
    /// to advertise sessions and lookup lobby codes.
    /// </summary>
    [Serializable]
    public sealed class IntroducerEndpoint
    {
        /// <summary>
        /// Unique identifier for this endpoint configuration.
        /// </summary>
        public string id = string.Empty;

        /// <summary>
        /// Human-readable name for display (e.g., "YARG Official", "My Community Server").
        /// </summary>
        public string displayName = string.Empty;

        /// <summary>
        /// Base URL of the introducer service (e.g., "https://lobby.yarg.in").
        /// </summary>
        public string url = string.Empty;

        /// <summary>
        /// Whether this endpoint is enabled for advertising/discovery.
        /// </summary>
        public bool enabled = true;

        /// <summary>
        /// Whether this is a built-in endpoint that cannot be removed (only disabled).
        /// </summary>
        public bool isBuiltIn;

        /// <summary>
        /// When this endpoint was added (Unix timestamp).
        /// </summary>
        public long createdAt;

        /// <summary>
        /// Last time this endpoint was successfully contacted (Unix timestamp).
        /// </summary>
        public long lastSuccessAt;

        /// <summary>
        /// Number of consecutive failures when contacting this endpoint.
        /// </summary>
        public int consecutiveFailures;

        /// <summary>
        /// Returns a normalized URI for this endpoint, or null if invalid.
        /// </summary>
        public Uri GetUri()
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;

            string normalized = url.Trim();
            if (!normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "https://" + normalized;
            }

            if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
                return uri;

            return null;
        }

        /// <summary>
        /// Returns the lobbies API endpoint URI.
        /// </summary>
        public Uri GetLobbiesUri()
        {
            var baseUri = GetUri();
            if (baseUri == null)
                return null;

            return new Uri(baseUri, "/api/lobbies");
        }

        /// <summary>
        /// Returns the lobby code lookup URI for a specific code.
        /// </summary>
        public Uri GetLobbyCodeUri(string code)
        {
            var baseUri = GetUri();
            if (baseUri == null || string.IsNullOrWhiteSpace(code))
                return null;

            return new Uri(baseUri, $"/api/lobbies/code/{code.Trim().ToUpperInvariant()}");
        }

        /// <summary>
        /// Returns the lobby code generation URI.
        /// </summary>
        public Uri GetLobbyCodeGenerationUri()
        {
            var baseUri = GetUri();
            if (baseUri == null)
                return null;

            return new Uri(baseUri, "/api/lobbies/code");
        }

        public IntroducerEndpoint Clone()
        {
            return new IntroducerEndpoint
            {
                id = id,
                displayName = displayName,
                url = url,
                enabled = enabled,
                isBuiltIn = isBuiltIn,
                createdAt = createdAt,
                lastSuccessAt = lastSuccessAt,
                consecutiveFailures = consecutiveFailures
            };
        }

        public void EnsureIdentifiers()
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                id = Guid.NewGuid().ToString("N");
            }

            if (createdAt <= 0)
            {
                createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }
        }

        /// <summary>
        /// Records a successful contact with this endpoint.
        /// </summary>
        public void RecordSuccess()
        {
            lastSuccessAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            consecutiveFailures = 0;
        }

        /// <summary>
        /// Records a failed contact attempt with this endpoint.
        /// </summary>
        public void RecordFailure()
        {
            consecutiveFailures++;
        }

        /// <summary>
        /// Creates the default YARG official introducer endpoint.
        /// </summary>
        public static IntroducerEndpoint CreateYargOfficial()
        {
            return new IntroducerEndpoint
            {
                id = "yarg-official",
                displayName = "YARG Official",
                url = "https://lobby.yarg.in",
                enabled = true,
                isBuiltIn = true,
                createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
        }

        /// <summary>
        /// Creates a local development introducer endpoint for testing.
        /// Run: dotnet run in YARG.Networking/src/YARG.Introducer folder.
        /// </summary>
        public static IntroducerEndpoint CreateLocalDev()
        {
            return new IntroducerEndpoint
            {
                id = "local-dev",
                displayName = "Local Dev (localhost:5000)",
                url = "http://localhost:5000",
                enabled = true,
                isBuiltIn = false,
                createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
        }
    }
}
