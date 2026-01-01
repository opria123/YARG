using UnityEditor;
using UnityEngine;
using YARG.Networking;

namespace YARG.Editor
{
    /// <summary>
    /// Editor window to easily toggle dedicated server mode for testing.
    /// </summary>
    public class DedicatedServerTestWindow : EditorWindow
    {
        private const string PREF_KEY_BASE = "YARG_ForceDedicatedServerMode";
        
        /// <summary>
        /// Gets a unique EditorPrefs key for this project instance.
        /// This allows ParrelSync clones to have independent settings.
        /// </summary>
        private static string GetProjectSpecificKey()
        {
            // Use the project path hash to create a unique key per project/clone
            string projectPath = Application.dataPath;
            int pathHash = projectPath.GetHashCode();
            return $"{PREF_KEY_BASE}_{pathHash}";
        }
        
        /// <summary>
        /// Gets the persisted dedicated server mode setting for this project instance.
        /// This is called from DedicatedServerBootstrap during initialization.
        /// </summary>
        public static bool GetPersistedDedicatedMode()
        {
            return EditorPrefs.GetBool(GetProjectSpecificKey(), false);
        }
        
        /// <summary>
        /// Sets the persisted dedicated server mode setting for this project instance.
        /// </summary>
        public static void SetPersistedDedicatedMode(bool value)
        {
            EditorPrefs.SetBool(GetProjectSpecificKey(), value);
            DedicatedServerBootstrap.EditorForceDedicatedMode = value;
        }
        
        [MenuItem("YARG/Dedicated Server Test")]
        public static void ShowWindow()
        {
            GetWindow<DedicatedServerTestWindow>("Dedicated Server Test");
        }

        private void OnGUI()
        {
            GUILayout.Label("Dedicated Server Testing", EditorStyles.boldLabel);
            GUILayout.Space(10);
            
            // Show which instance this is
            string projectPath = Application.dataPath;
            bool isClone = projectPath.Contains("_clone_");
            string instanceName = isClone ? System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(projectPath)) : "Main Project";
            EditorGUILayout.LabelField("Instance:", instanceName, EditorStyles.boldLabel);
            GUILayout.Space(5);

            EditorGUILayout.HelpBox(
                "Enable this to run as a dedicated server when you enter Play Mode.\n\n" +
                "For testing with ParrelSync:\n" +
                "1. Enable this in ONE Unity Editor instance (the server)\n" +
                "2. Leave it disabled in other instances (clients)\n" +
                "3. Enter Play Mode in the server first\n" +
                "4. Then connect from client instances\n\n" +
                "Note: This setting is per-instance, so each clone has its own toggle.",
                MessageType.Info);

            GUILayout.Space(10);

            // Use persisted value from EditorPrefs (project-specific key)
            bool currentValue = GetPersistedDedicatedMode();
            bool newValue = EditorGUILayout.Toggle("Force Dedicated Server Mode", currentValue);

            if (newValue != currentValue)
            {
                SetPersistedDedicatedMode(newValue);
                Debug.Log($"[DedicatedServerTest] [{instanceName}] Dedicated server mode: {(newValue ? "ENABLED" : "DISABLED")}");
            }

            GUILayout.Space(10);

            if (DedicatedServerBootstrap.EditorForceDedicatedMode)
            {
                EditorGUILayout.HelpBox(
                    "⚠️ DEDICATED SERVER MODE ENABLED\n\n" +
                    "When you enter Play Mode, this instance will run as a dedicated server.\n" +
                    "Make sure you have a config file at:\n" +
                    Application.persistentDataPath + "/dedicated_server.json",
                    MessageType.Warning);
            }

            GUILayout.Space(10);

            if (GUILayout.Button("Open Config Folder"))
            {
                EditorUtility.RevealInFinder(Application.persistentDataPath);
            }

            if (GUILayout.Button("Create Default Config"))
            {
                CreateDefaultConfig();
            }
        }

        private void CreateDefaultConfig()
        {
            string configPath = System.IO.Path.Combine(Application.persistentDataPath, "dedicated_server.json");
            
            if (System.IO.File.Exists(configPath))
            {
                if (!EditorUtility.DisplayDialog("Config Exists", 
                    "A config file already exists. Overwrite?", "Yes", "No"))
                {
                    return;
                }
            }

            string defaultConfig = @"{
    ""sessionName"": ""YARG Test Server"",
    ""port"": 9050,
    ""maxPlayers"": 8,
    ""privacy"": ""public"",
    ""bandSizeLimit"": 0,
    ""blockedGameModes"": [],
    ""adminWebPort"": 8080
}";
            System.IO.File.WriteAllText(configPath, defaultConfig);
            Debug.Log($"[DedicatedServerTest] Created config at: {configPath}");
            EditorUtility.RevealInFinder(configPath);
        }
    }
}
