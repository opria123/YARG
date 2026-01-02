using UnityEngine;
using YARG.Networking.Settings;
using YARG.Settings;

namespace YARG.Menu.Settings
{
    /// <summary>
    /// Header component for the Lobby Server settings tab.
    /// Provides "Add New" functionality.
    /// </summary>
    public class LobbyServerHeader : MonoBehaviour
    {
        /// <summary>
        /// Called when the "Add New Lobby Server" button is clicked.
        /// </summary>
        public void AddNewLobbyServer()
        {
            var store = NetworkSettingsStore.Instance;
            if (store == null)
            {
                Debug.LogError("[LobbyServerHeader] NetworkSettingsStore not available");
                return;
            }

            // Add a new empty lobby server entry
            store.AddLobbyServer("New Lobby Server", string.Empty);

            // Refresh the settings menu to show the new entry
            SettingsMenu.Instance.RefreshAndKeepPosition();
        }

        /// <summary>
        /// Called when the "Reset to Defaults" button is clicked.
        /// </summary>
        public void ResetToDefaults()
        {
            var store = NetworkSettingsStore.Instance;
            if (store == null)
            {
                Debug.LogError("[LobbyServerHeader] NetworkSettingsStore not available");
                return;
            }

            store.ResetToDefaults();

            // Refresh the settings menu
            SettingsMenu.Instance.RefreshAndKeepPosition();
        }
    }
}
