using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.UI;
using YARG.Core;
using YARG.Helpers.Extensions;
using YARG.Networking.Abstraction;

namespace YARG.Menu.DifficultySelect
{
    /// <summary>
    /// UI component for displaying a single player's status in the multiplayer difficulty select screen
    /// </summary>
    public class MultiplayerPlayerEntry : MonoBehaviour
    {
        [Header("UI Components")]
        [SerializeField] private TextMeshProUGUI _iconsText;
        [SerializeField] private TextMeshProUGUI _playerNameText;
        [SerializeField] private Image _readyStatusIcon;
        
        [Header("Sprite Assets")]
        [SerializeField] private TMP_SpriteAsset _instrumentSpriteAsset;
        
        private NetworkPlayerData _playerData;
        private AsyncOperationHandle<Sprite> _readyIconHandle;
        private static TMP_SpriteAsset _cachedSpriteAsset;
        
        private void Awake()
        {
            // Ensure the TMP component has a sprite asset assigned
            EnsureSpriteAssetAssigned();
        }
        
        private void EnsureSpriteAssetAssigned()
        {
            if (_iconsText == null) return;
            
            // If already assigned in inspector or component, skip
            if (_iconsText.spriteAsset != null)
            {
                Debug.Log($"[MultiplayerPlayerEntry] Sprite asset already assigned: {_iconsText.spriteAsset.name}");
                return;
            }
            
            // Try to use the serialized field first
            if (_instrumentSpriteAsset != null)
            {
                _iconsText.spriteAsset = _instrumentSpriteAsset;
                Debug.Log($"[MultiplayerPlayerEntry] Assigned sprite asset from SerializeField: {_instrumentSpriteAsset.name}");
                return;
            }
            
            // Try cached asset
            if (_cachedSpriteAsset != null)
            {
                _iconsText.spriteAsset = _cachedSpriteAsset;
                Debug.Log($"[MultiplayerPlayerEntry] Assigned cached sprite asset: {_cachedSpriteAsset.name}");
                return;
            }
            
            // Try using TMP_Settings default first (most reliable)
            if (TMP_Settings.defaultSpriteAsset != null)
            {
                _iconsText.spriteAsset = TMP_Settings.defaultSpriteAsset;
                _cachedSpriteAsset = TMP_Settings.defaultSpriteAsset;
                Debug.Log($"[MultiplayerPlayerEntry] Assigned TMP default sprite asset: {TMP_Settings.defaultSpriteAsset.name}");
                return;
            }
            
            // Fallback: try to load from Resources
            var spriteAsset = Resources.Load<TMP_SpriteAsset>("Fonts & Materials/FontSprites");
            if (spriteAsset == null)
            {
                spriteAsset = Resources.Load<TMP_SpriteAsset>("FontSprites");
            }
            
            if (spriteAsset != null)
            {
                _cachedSpriteAsset = spriteAsset;
                _iconsText.spriteAsset = spriteAsset;
                Debug.Log($"[MultiplayerPlayerEntry] Loaded and assigned sprite asset from Resources: {spriteAsset.name}");
            }
            else
            {
                Debug.LogWarning("[MultiplayerPlayerEntry] Could not find any sprite asset to assign!");
            }
        }
        
        public void Initialize(NetworkPlayerData playerData)
        {
            _playerData = playerData;
            
            // Ensure sprite asset is assigned before we try to display icons
            EnsureSpriteAssetAssigned();
            
            // Debug: Check if references are assigned
            Debug.Log($"[MultiplayerPlayerEntry] Initialize called for {playerData?.PlayerName}");
            Debug.Log($"[MultiplayerPlayerEntry] References - Icons: {_iconsText != null}, Name: {_playerNameText != null}, Status: {_readyStatusIcon != null}");
            Debug.Log($"[MultiplayerPlayerEntry] SpriteAsset: {_iconsText?.spriteAsset?.name ?? "NULL"}");
            
            // Subscribe to player events
            if (_playerData != null)
            {
                _playerData.OnReadyStateChangedEvent += OnReadyStateChanged;
                _playerData.OnInstrumentChangedEvent += OnInstrumentChanged;
                _playerData.OnDifficultyChangedEvent += OnDifficultyChanged;
            }
            
            UpdateDisplay();
        }
        
        private void OnDestroy()
        {
            // Unsubscribe from events
            if (_playerData != null)
            {
                _playerData.OnReadyStateChangedEvent -= OnReadyStateChanged;
                _playerData.OnInstrumentChangedEvent -= OnInstrumentChanged;
                _playerData.OnDifficultyChangedEvent -= OnDifficultyChanged;
            }
            
            // Release addressable handles
            if (_readyIconHandle.IsValid())
            {
                Addressables.Release(_readyIconHandle);
            }
        }
        
        private void OnReadyStateChanged(bool isReady)
        {
            Debug.Log($"[MultiplayerPlayerEntry] OnReadyStateChanged called - Player: {_playerData?.PlayerName}, IsReady: {isReady}");
            UpdateReadyStatus();
        }
        
        private void OnInstrumentChanged(int newInstrument, int newDifficulty)
        {
            Debug.Log($"[MultiplayerPlayerEntry] OnInstrumentChanged called - Player: {_playerData?.PlayerName}, Instrument: {newInstrument}, Difficulty: {newDifficulty}");
            UpdatePlayerNameWithIcons();
        }
        
        private void OnDifficultyChanged(int newInstrument, int newDifficulty)
        {
            Debug.Log($"[MultiplayerPlayerEntry] OnDifficultyChanged called - Player: {_playerData?.PlayerName}, Instrument: {newInstrument}, Difficulty: {newDifficulty}");
            UpdatePlayerNameWithIcons();
        }
        
        private void UpdateDisplay()
        {
            if (_playerData == null) return;
            
            UpdatePlayerNameWithIcons();
            UpdateReadyStatus();
        }
        
        private void UpdatePlayerNameWithIcons()
        {
            if (_playerNameText == null || _playerData == null)
            {
                Debug.LogWarning($"[MultiplayerPlayerEntry] Cannot update name - _playerNameText: {_playerNameText != null}, _playerData: {_playerData != null}");
                return;
            }
            
            // Get instrument sprite name (may be null if not selected yet)
            Instrument instrument = (Instrument)_playerData.Instrument;
            string instrumentSprite = instrument.ToResourceName();
            
            Debug.Log($"[MultiplayerPlayerEntry] Raw values - InstrumentInt: {_playerData.Instrument}, DifficultyInt: {_playerData.Difficulty}, InstrumentEnum: {instrument}, InstrumentSprite: {instrumentSprite ?? "NULL"}");
            
            // Get difficulty sprite name - map to proper sprite names
            Difficulty difficulty = (Difficulty)_playerData.Difficulty;
            string difficultyName = difficulty switch
            {
                Difficulty.Beginner   => "Easy",    // Beginner uses Easy sprite
                Difficulty.Easy       => "Easy",
                Difficulty.Medium     => "Medium",
                Difficulty.Hard       => "Hard",
                Difficulty.Expert     => "Expert",
                Difficulty.ExpertPlus => "ExpertPlus",
                _ => "Easy"
            };
            
            Debug.Log($"[MultiplayerPlayerEntry] DifficultyEnum: {difficulty}, DifficultySprite: {difficultyName}");
            
            // Update combined icons (instrument + difficulty)
            // Only show icons if instrument is selected (non-null sprite name)
            if (_iconsText != null)
            {
                // Ensure sprite asset is assigned before setting text
                EnsureSpriteAssetAssigned();
                
                if (!string.IsNullOrEmpty(instrumentSprite))
                {
                    string spriteMarkup = $"<sprite name=\"{instrumentSprite}\"><sprite name=\"{difficultyName}\">";
                    _iconsText.text = spriteMarkup;
                    _iconsText.gameObject.SetActive(true);
                    
                    // Force mesh update to ensure sprites render
                    _iconsText.ForceMeshUpdate();
                    
                    Debug.Log($"[MultiplayerPlayerEntry] Set icons text to: {spriteMarkup}, SpriteAsset: {_iconsText.spriteAsset?.name ?? "NULL"}");
                    
                    // Log sprite asset details for debugging
                    if (_iconsText.spriteAsset != null)
                    {
                        Debug.Log($"[MultiplayerPlayerEntry] SpriteAsset has {_iconsText.spriteAsset.spriteCharacterTable?.Count ?? 0} sprites, fallbacks: {_iconsText.spriteAsset.fallbackSpriteAssets?.Count ?? 0}");
                    }
                }
                else
                {
                    // No instrument selected yet - hide icons or show placeholder
                    _iconsText.text = "";
                    _iconsText.gameObject.SetActive(false);
                    Debug.Log($"[MultiplayerPlayerEntry] Icons hidden - no valid instrument sprite");
                }
            }
            else
            {
                Debug.LogWarning($"[MultiplayerPlayerEntry] _iconsText is null!");
            }
            
            // Update player name with ellipsis overflow
            string playerName = _playerData.PlayerName;
            _playerNameText.text = playerName;
            _playerNameText.enableWordWrapping = false;
            _playerNameText.overflowMode = TextOverflowModes.Ellipsis;
            _playerNameText.horizontalAlignment = HorizontalAlignmentOptions.Left;
            
            Debug.Log($"[MultiplayerPlayerEntry] Updated - Player: {playerName}, Instrument: {instrumentSprite ?? "(none)"}, Difficulty: {difficultyName}");
        }
        
        private void UpdateReadyStatus()
        {
            if (_readyStatusIcon == null || _playerData == null)
            {
                Debug.LogWarning($"[MultiplayerPlayerEntry] UpdateReadyStatus - _readyStatusIcon: {_readyStatusIcon != null}, _playerData: {_playerData != null}");
                return;
            }
            
            Debug.Log($"[MultiplayerPlayerEntry] UpdateReadyStatus - Player: {_playerData.PlayerName}, IsReady: {_playerData.IsReady}");
            
            // Release previous handle if valid
            if (_readyIconHandle.IsValid())
            {
                Addressables.Release(_readyIconHandle);
            }
            
            // Load appropriate icon and set color based on ready state
            string iconPath;
            Color iconColor;
            
            if (_playerData.IsReady)
            {
                // Ready: Green checkmark
                iconPath = "AssortedIcons[AssortedIcons_0]"; // Checkmark icon
                iconColor = new Color(0.2f, 0.8f, 0.2f, 1f); // Bright green
            }
            else
            {
                // Not ready: Red X
                iconPath = "CloseIcon"; // X icon
                iconColor = new Color(0.9f, 0.2f, 0.2f, 1f); // Bright red
            }
            
            Debug.Log($"[MultiplayerPlayerEntry] Loading ready icon from: {iconPath}");
            
            _readyIconHandle = Addressables.LoadAssetAsync<Sprite>(iconPath);
            _readyIconHandle.Completed += handle =>
            {
                if (handle.Status == UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded)
                {
                    if (_readyStatusIcon != null)
                    {
                        _readyStatusIcon.sprite = handle.Result;
                        _readyStatusIcon.color = iconColor;
                        _readyStatusIcon.enabled = true;
                        Debug.Log($"[MultiplayerPlayerEntry] Successfully loaded ready icon: {iconPath} with color {iconColor}");
                    }
                }
                else
                {
                    Debug.LogWarning($"[MultiplayerPlayerEntry] Failed to load ready icon: {iconPath}, Status: {handle.Status}");
                    if (_readyStatusIcon != null)
                    {
                        _readyStatusIcon.enabled = false;
                    }
                }
            };
        }
        
        /// <summary>
        /// Manual update method for force-refreshing the display
        /// </summary>
        public void RefreshDisplay()
        {
            UpdateDisplay();
        }

        /// <summary>
        /// Set player name and instrument icon manually (for sidebar)
        /// </summary>
        public void SetPlayer(string name, string instrument)
        {
            if (_playerNameText != null)
                _playerNameText.text = name;
            if (_iconsText != null)
                _iconsText.text = instrument;
        }
    }
}
