using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using YARG.Networking.Abstraction;
using YARG.Networking.DedicatedServer;
using YARG.Networking.Settings;

namespace YARG.Networking
{
    /// <summary>
    /// Bootstrap component for dedicated (headless) servers.
    /// Automatically initializes the server when running in dedicated mode.
    /// Configuration is loaded exclusively from the JSON config file.
    /// </summary>
    public class DedicatedServerBootstrap : MonoBehaviour
    {
        /// <summary>
        /// The loaded server configuration.
        /// </summary>
        public static DedicatedServerConfig Config { get; private set; }

        /// <summary>
        /// The IP ban list for this server.
        /// </summary>
        public static IpBanList BanList { get; private set; }

        /// <summary>
        /// The web admin interface.
        /// </summary>
        public static DedicatedServerWebAdmin WebAdmin { get; private set; }

        /// <summary>
        /// Returns true if running as a dedicated server.
        /// </summary>
        public static bool IsRunning { get; private set; }

        /// <summary>
        /// The time the server was started.
        /// </summary>
        public static DateTime StartTime { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize()
        {
            Debug.Log("[DedicatedServer] Checking if should run as dedicated server...");
            
            // Check if we should run as dedicated server
            if (!ShouldRunAsDedicatedServer())
            {
                Debug.Log("[DedicatedServer] Not running as dedicated server (conditions not met)");
                return;
            }

            Debug.Log("[DedicatedServer] Starting in dedicated server mode...");

            ApplyHeadlessGraphicsSettings();

            var go = new GameObject(nameof(DedicatedServerBootstrap));
            DontDestroyOnLoad(go);
            go.AddComponent<DedicatedServerBootstrap>();
        }

        /// <summary>
        /// Editor-only: Force dedicated server mode for testing.
        /// Set this to true in the Inspector or via code before entering Play Mode.
        /// </summary>
#if UNITY_EDITOR
        public static bool EditorForceDedicatedMode = false;
#endif

        /// <summary>
        /// Determines if the game should run as a dedicated server.
        /// Checks: -dedicated flag, YARG_DEDICATED env var, or batch mode.
        /// </summary>
        private static bool ShouldRunAsDedicatedServer()
        {
#if UNITY_EDITOR
            // Editor-only: Allow forcing dedicated mode for testing
            // Use project-specific EditorPrefs key so ParrelSync clones have independent settings
            string projectPath = UnityEngine.Application.dataPath;
            int pathHash = projectPath.GetHashCode();
            string prefKey = $"YARG_ForceDedicatedServerMode_{pathHash}";
            bool editorPrefValue = UnityEditor.EditorPrefs.GetBool(prefKey, false);
            Debug.Log($"[DedicatedServer] EditorPrefs {prefKey} = {editorPrefValue}");
            if (editorPrefValue)
            {
                EditorForceDedicatedMode = true;
                Debug.Log("[DedicatedServer] Forced dedicated mode via EditorPrefs setting");
                return true;
            }
#endif

            // Check command line for -dedicated flag
            bool cmdLineFlag = CommandLineArgs.DedicatedServer;
            Debug.Log($"[DedicatedServer] CommandLineArgs.DedicatedServer = {cmdLineFlag}");
            if (cmdLineFlag)
            {
                return true;
            }

            // Check environment variable
            var env = Environment.GetEnvironmentVariable("YARG_DEDICATED") ?? string.Empty;
            Debug.Log($"[DedicatedServer] YARG_DEDICATED env var = '{env}'");
            if (!string.IsNullOrWhiteSpace(env))
            {
                var trimmed = env.Trim().ToLowerInvariant();
                if (trimmed == "1" || trimmed == "true" || trimmed == "yes")
                {
                    return true;
                }
            }

            // Check if running in batch mode (headless Unity)
            bool batchMode = Application.isBatchMode;
            Debug.Log($"[DedicatedServer] Application.isBatchMode = {batchMode}");
            if (batchMode)
            {
                return true;
            }

            return false;
        }

        private void Start()
        {
            Application.runInBackground = true;
            IsRunning = true;
            StartTime = DateTime.UtcNow;

            // Load configuration from JSON file
            Config = DedicatedServerConfig.Load();

            // Load ban list
            BanList = IpBanList.Load(Config.ResolvedBanListPath);

            Debug.Log($"[DedicatedServer] Configuration loaded:");
            Debug.Log($"  Session Name: {Config.Server.SessionName}");
            Debug.Log($"  Port: {Config.Server.Port}");
            Debug.Log($"  Max Players: {Config.Server.MaxPlayers}");
            Debug.Log($"  Privacy: {Config.Server.PrivacyMode}");
            Debug.Log($"  Band Size: {Config.Gameplay.BandSize}");
            Debug.Log($"  Admin Web Port: {Config.Admin.WebPort}");
            Debug.Log($"  Ban List: {BanList.BannedIps.Count} banned IPs");

            // Start the admin web server
            WebAdmin = new DedicatedServerWebAdmin(Config, BanList);
            WebAdmin.OnAdminAction += OnAdminAction;
            WebAdmin.Start();

            // Initialize the dedicated server manager for host role separation
            DedicatedServerManager.Initialize(Config, BanList);

            StartCoroutine(Bootstrap());
        }

        private void OnAdminAction(string action, string details)
        {
            Debug.Log($"[DedicatedServer] Admin action: {action} - {details}");
            
            // If settings changed, broadcast to all connected clients
            if (action == "SettingsChanged")
            {
                BroadcastCurrentSettings();
            }
        }
        
        /// <summary>
        /// Broadcasts the current server settings to all connected clients.
        /// </summary>
        internal static void BroadcastCurrentSettings()
        {
            if (Config == null)
            {
                Debug.LogWarning("[DedicatedServer] Cannot broadcast settings - config not loaded");
                return;
            }
            
            var networkService = NetworkingServiceFactory.Instance;
            if (networkService == null)
            {
                Debug.LogWarning("[DedicatedServer] Cannot broadcast settings - network service not available");
                return;
            }
            
            // Convert blocked game modes from config string names to int list
            var blockedModes = new List<int>();
            if (Config.Gameplay.BlockedGameModes != null)
            {
                foreach (var modeName in Config.Gameplay.BlockedGameModes)
                {
                    // Try to parse the string name to a GameMode enum
                    if (Enum.TryParse<YARG.Core.GameMode>(modeName, ignoreCase: true, out var gameMode))
                    {
                        blockedModes.Add((int)gameMode);
                    }
                    else
                    {
                        Debug.LogWarning($"[DedicatedServer] Unknown game mode in blockedGameModes: {modeName}");
                    }
                }
            }
            
            // Apply the settings locally on the server
            // This is critical for the server to actually use NoFailMode, BandSize, etc.
            var sessionPreset = Config.ToSessionPreset();
            var gameplaySettings = Gameplay.MultiplayerGameplaySettings.Instance;
            if (gameplaySettings != null)
            {
                gameplaySettings.ApplyPreset(sessionPreset);
                Debug.Log($"[DedicatedServer] Applied settings locally: NoFail={sessionPreset.NoFailMode}, BandSize={sessionPreset.bandSize}, SharedSongs={sessionPreset.SharedSongsOnly}");
            }
            else
            {
                // MultiplayerGameplaySettings might not be instantiated yet in dedicated server mode
                // Create it now if it doesn't exist
                Debug.LogWarning("[DedicatedServer] MultiplayerGameplaySettings.Instance is null - creating it now");
                var go = new GameObject("MultiplayerGameplaySettings_Server");
                UnityEngine.Object.DontDestroyOnLoad(go);
                var newSettings = go.AddComponent<Gameplay.MultiplayerGameplaySettings>();
                // Wait one frame won't work in static context, apply directly after Awake
                // Since AddComponent triggers Awake immediately, the Instance should now be set
                if (Gameplay.MultiplayerGameplaySettings.Instance != null)
                {
                    Gameplay.MultiplayerGameplaySettings.Instance.ApplyPreset(sessionPreset);
                    Debug.Log($"[DedicatedServer] Applied settings locally (after creation): NoFail={sessionPreset.NoFailMode}, BandSize={sessionPreset.bandSize}, SharedSongs={sessionPreset.SharedSongsOnly}");
                }
                else
                {
                    Debug.LogError("[DedicatedServer] Failed to create MultiplayerGameplaySettings - settings not applied!");
                }
            }
            
            // Broadcast the settings to all clients
            bool success = networkService.BroadcastSessionSettings(
                Config.Server.SessionName,
                Config.Server.MaxPlayers,
                (byte)Config.LobbyPrivacyModeEnum,
                Config.Gameplay.BandSize,
                Config.Gameplay.NoFailMode,
                Config.Gameplay.SharedSongsOnly,
                Config.Gameplay.AllowModifiers,
                Config.Gameplay.EnablePresetSync,
                Config.Gameplay.AllowLateJoin,
                blockedModes,
                localPlayersFirst: false);
            
            if (success)
            {
                Debug.Log($"[DedicatedServer] Broadcast settings to clients: BandSize={Config.Gameplay.BandSize}, NoFail={Config.Gameplay.NoFailMode}, SharedSongs={Config.Gameplay.SharedSongsOnly}");
            }
            else
            {
                Debug.LogWarning("[DedicatedServer] Failed to broadcast settings to clients");
            }
        }

        private void Update()
        {
            // Update the dedicated server manager to check for timeouts
            DedicatedServerManager.Instance?.Update();
        }

        private IEnumerator Bootstrap()
        {
            // Wait for the networking service to be available
            while (NetworkingServiceFactory.Instance == null)
            {
                yield return null;
            }

            var networkService = NetworkingServiceFactory.Instance;
            networkService.SetDedicatedServerMode(true);
            networkService.SetServerPort(Config.Server.Port);

            // Create the lobby with the configured settings
            // Dedicated servers always use SessionType.Server (no UPnP needed)
            networkService.CreateLobby(
                Config.Server.SessionName,
                Config.Server.MaxPlayers,
                Config.LobbyPrivacyModeEnum,
                SessionType.Server,
                Config.Server.Password);
            
            // Wait a frame for lobby to be fully created
            yield return null;
            
            // Enable auto-start on all ready for dedicated servers
            // This is handled centrally in the networking adapter
            if (networkService is LiteNetNetworkingAdapter liteNetAdapter)
            {
                liteNetAdapter.SetAutoStartOnAllReady(true);
                Debug.Log("[DedicatedServer] Auto-start on all ready enabled");
            }
            
            // Initialize the band system through the networking adapter
            // This ensures dedicated servers have the same band logic as in-game hosts
            if (Config.Gameplay.BandSize > 0)
            {
                bool bandsInitialized = networkService.InitializeBandsForSession(Config.Gameplay.BandSize);
                if (bandsInitialized)
                {
                    Debug.Log($"[DedicatedServer] Band system initialized with size: {Config.Gameplay.BandSize}");
                }
                else
                {
                    Debug.LogWarning("[DedicatedServer] Failed to initialize band system");
                }
            }
            
            // Apply gameplay settings from config (band size, no fail, etc.)
            BroadcastCurrentSettings();

            Debug.Log($"[DedicatedServer] Server started successfully!");
            Debug.Log($"[DedicatedServer] Listening on port {Config.Server.Port}");

            if (Config.Admin.AllowRemoteAccess)
            {
                Debug.Log($"[DedicatedServer] Admin web UI available at http://0.0.0.0:{Config.Admin.WebPort}/");
            }
            else
            {
                Debug.Log($"[DedicatedServer] Admin web UI available at http://localhost:{Config.Admin.WebPort}/");
            }
        }

        private void OnDestroy()
        {
            IsRunning = false;
            
            // Clean up dedicated server manager
            DedicatedServerManager.Shutdown();
            
            // Clean up web admin
            if (WebAdmin != null)
            {
                WebAdmin.OnAdminAction -= OnAdminAction;
                WebAdmin.Dispose();
                WebAdmin = null;
            }
        }

        private static void ApplyHeadlessGraphicsSettings()
        {
            try
            {
                if (GraphicsSettings.defaultRenderPipeline != null)
                {
                    GraphicsSettings.defaultRenderPipeline = null;
                }

                if (QualitySettings.renderPipeline != null)
                {
                    QualitySettings.renderPipeline = null;
                }

                QualitySettings.anisotropicFiltering = AnisotropicFiltering.Disable;
                QualitySettings.vSyncCount = 0;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[DedicatedServer] Failed to adjust graphics settings for headless mode: {ex.Message}");
            }
        }
    }
}
