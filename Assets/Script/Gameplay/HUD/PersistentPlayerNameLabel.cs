using TMPro;
using UnityEngine;
using YARG.Localization;
using YARG.Player;

namespace YARG.Gameplay.HUD
{
    /// <summary>
    /// A persistent player name label that stays visible during gameplay.
    /// Used in multiplayer to identify which track belongs to which player.
    /// For instrument tracks, this is placed under the combo/multiplier (3D TextMeshPro).
    /// For vocals, this is placed under the star power bar/multiplier (UI TextMeshProUGUI).
    /// Uses TMP_Text base class to support both 3D and UI text components.
    /// </summary>
    public class PersistentPlayerNameLabel : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("The text component for player name. Supports both TextMeshPro (3D) and TextMeshProUGUI (UI).")]
        private TMP_Text _playerNameText;

        [SerializeField]
        [Tooltip("Optional: Additional text to show (e.g., instrument name)")]
        private TMP_Text _additionalInfoText;

        private bool _isInitialized;

        /// <summary>
        /// Initializes the label with the player's name.
        /// Only shows for remote players in multiplayer, or always shows if forceShow is true.
        /// </summary>
        /// <param name="player">The YargPlayer to display</param>
        /// <param name="isRemotePlayer">Whether this is a remote player</param>
        /// <param name="forceShow">Force show even for local players</param>
        public void Initialize(YargPlayer player, bool isRemotePlayer, bool forceShow = false)
        {
            if (_playerNameText == null)
            {
                Debug.LogWarning("[PersistentPlayerNameLabel] _playerNameText is not assigned!");
                gameObject.SetActive(false);
                return;
            }

            // In multiplayer, show for remote players (or all players if forceShow)
            // forceShow can be used if a setting is added to always show names
            bool shouldShow = isRemotePlayer || forceShow;

            if (shouldShow && player?.Profile != null)
            {
                _playerNameText.text = player.Profile.Name;
                
                if (_additionalInfoText != null)
                {
                    // Optionally show instrument info
                    _additionalInfoText.text = player.Profile.CurrentInstrument.ToLocalizedName();
                }
                
                gameObject.SetActive(true);
                _isInitialized = true;
            }
            else
            {
                // Hide for local players or if no player provided
                gameObject.SetActive(false);
            }
        }

        /// <summary>
        /// Updates the player name (e.g., if profile name changes mid-game).
        /// </summary>
        public void UpdatePlayerName(string name)
        {
            if (_playerNameText != null && _isInitialized)
            {
                _playerNameText.text = name;
            }
        }

        /// <summary>
        /// Sets the color of the player name text.
        /// Useful for matching harmony colors in vocals.
        /// </summary>
        public void SetTextColor(Color color)
        {
            if (_playerNameText != null)
            {
                _playerNameText.color = color;
            }
        }

        /// <summary>
        /// Sets visibility of the label.
        /// </summary>
        public void SetVisible(bool visible)
        {
            gameObject.SetActive(visible && _isInitialized);
        }
    }
}
