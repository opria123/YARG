using UnityEngine;
using YARG.Networking.Settings;
using YARG.Settings;

namespace YARG.Menu.Settings
{
    /// <summary>
    /// Header component for the Introducer settings tab.
    /// Provides "Add New" functionality.
    /// </summary>
    public class IntroducerHeader : MonoBehaviour
    {
        /// <summary>
        /// Called when the "Add New Introducer" button is clicked.
        /// </summary>
        public void AddNewIntroducer()
        {
            var store = NetworkSettingsStore.Instance;
            if (store == null)
            {
                Debug.LogError("[IntroducerHeader] NetworkSettingsStore not available");
                return;
            }

            // Add a new empty introducer entry
            store.AddIntroducer("New Introducer", string.Empty);

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
                Debug.LogError("[IntroducerHeader] NetworkSettingsStore not available");
                return;
            }

            store.ResetToDefaults();

            // Refresh the settings menu
            SettingsMenu.Instance.RefreshAndKeepPosition();
        }
    }
}
