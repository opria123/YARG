using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.UI;
using YARG.Core;
using YARG.Helpers.Extensions;
using YARG.Menu.Navigation;
using YARG.Networking.Abstraction;
using YARG.Networking.Settings;

namespace YARG.Menu.Multiplayer.Settings
{
    /// <summary>
    /// A session settings panel that uses the existing DropdownDrawer component
    /// for collapsible categories and existing setting row prefabs.
    /// This approach maximizes reuse of existing UI components.
    /// </summary>
    public class SessionSettingsPanelBuilder : MonoBehaviour
    {
        #region Serialized Fields

        [Header("Prefabs")]
        [Tooltip("The DropdownDrawer prefab from Assets/Prefabs/Menu/Common/DropdownDrawer.prefab")]
        [SerializeField] private DropdownDrawer _categoryDrawerPrefab;

        [Tooltip("The BaseSetting prefab or a variant with Label + InputField")]
        [SerializeField] private GameObject _textSettingPrefab;

        [Tooltip("The IntSetting prefab variant")]
        [SerializeField] private GameObject _intSettingPrefab;

        [Tooltip("The ToggleSetting prefab variant")]
        [SerializeField] private GameObject _toggleSettingPrefab;

        [Tooltip("The DropdownSetting prefab variant")]
        [SerializeField] private GameObject _dropdownSettingPrefab;

        [Tooltip("The SliderSetting prefab variant")]
        [SerializeField] private GameObject _sliderSettingPrefab;

        [Header("Icons")]
        [Tooltip("Sprite shown when password is visible (eye open)")]
        [SerializeField] private Sprite _visibilityVisibleSprite;
        
        [Tooltip("Sprite shown when password is hidden (eye closed)")]
        [SerializeField] private Sprite _visibilityHiddenSprite;

        [Header("Layout")]
        [SerializeField] private RectTransform _contentContainer;
        [SerializeField] private ScrollRect _scrollRect;
        [SerializeField] private NavigationGroup _navigationGroup;

        [Header("Default Expansion")]
        [SerializeField] private bool _lobbyInfoExpanded = true;
        [SerializeField] private bool _gameplayExpanded = true;
        [SerializeField] private bool _sessionExpanded = true;

        #endregion

        #region Events

        /// <summary>
        /// Fired when any setting value changes.
        /// </summary>
        public event Action<SessionSettingsData> OnSettingsChanged;

        #endregion

        #region Private State

        private SettingsPanelMode _mode = SettingsPanelMode.Create;
        private SettingsPanelConfig _config = SettingsPanelConfig.Default;
        private SessionSettingsData _currentData = new();
        private bool _suppressCallbacks;
        private bool _isBuilt;
        private bool _isBuildInProgress;
        private bool _dataInitialized; // Track if SetData has been called with real data
        private LayoutElement layoutElement;
        
        // Pending operations to apply after build completes
        private SettingsPanelMode? _pendingMode;
        private SettingsPanelConfig _pendingConfig;
        private SessionSettingsData _pendingData;

        // Category drawers (using existing DropdownDrawer component)
        private DropdownDrawer _lobbyInfoDrawer;
        private DropdownDrawer _gameplayDrawer;
        private DropdownDrawer _sessionDrawer;

        // Track created UI elements for cleanup
        private readonly List<GameObject> _createdObjects = new();

        // References to created input controls
        private TMP_InputField _lobbyNameInput;
        private ValueSlider _maxPlayersSlider;
        private TMP_Dropdown _privacyDropdown;
        private TMP_Dropdown _sessionTypeDropdown;
        private TMP_InputField _passwordInput;
        private Button _passwordRevealButton;
        private Image _passwordRevealIcon;
        private bool _isPasswordRevealed;
        private ValueSlider _bandSizeSlider;
        private Toggle _noFailToggle;
        private Toggle _sharedSongsToggle;
        private Toggle _allowModifiersToggle;
        private Toggle _presetSyncToggle;
        private Toggle _lateJoinToggle;
        private Toggle _localPlayersFirstToggle;
        private List<GameModeToggleButton> _gameModeButtons = new();

        // Row references for visibility control
        private GameObject _lobbyNameRow;
        private GameObject _maxPlayersRow;
        private GameObject _privacyRow;
        private GameObject _sessionTypeRow;
        private GameObject _passwordRow;
        private GameObject _bandSizeRow;
        private GameObject _noFailRow;
        private GameObject _sharedSongsRow;
        private GameObject _allowModifiersRow;
        private GameObject _allowedInstrumentsRow;
        private GameObject _localPlayersFirstRow;
        private GameObject _presetSyncRow;
        private GameObject _lateJoinRow;

        #endregion

        #region Public Properties

        public SettingsPanelMode Mode => _mode;
        public SettingsPanelConfig Config => _config;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            AutoFindReferences();
            EnsureLayoutElement();
            BuildIfNeeded();
        }

        private void OnEnable()
        {
            Debug.Log($"[SessionSettingsPanelBuilder] OnEnable: _isBuilt={_isBuilt}, _isBuildInProgress={_isBuildInProgress}, contentChildren={_contentContainer?.childCount ?? -1}");
            
            // CRITICAL FIX: If a build was in progress when the object was disabled,
            // the coroutine was stopped by Unity but _isBuildInProgress wasn't cleared.
            // Reset it so we can start a fresh build.
            if (_isBuildInProgress && !_isBuilt)
            {
                Debug.Log("[SessionSettingsPanelBuilder] OnEnable: Resetting stale _isBuildInProgress flag");
                _isBuildInProgress = false;
            }
            
            // Check if we need to rebuild: either not built yet, or built but content is missing/empty
            // This handles the case where the scene reloads and the build coroutine completed while disabled,
            // leaving the UI in an invalid state
            bool needsRebuild = !_isBuilt || 
                (_isBuilt && _contentContainer != null && _contentContainer.childCount == 0) ||
                (_isBuilt && _lobbyInfoDrawer == null);
            
            if (needsRebuild && !_isBuildInProgress)
            {
                Debug.Log($"[SessionSettingsPanelBuilder] OnEnable: Triggering rebuild (needsRebuild={needsRebuild})");
                // Reset build state to force a fresh build
                _isBuilt = false;
                BuildIfNeeded();
            }
            else if (_isBuilt)
            {
                // Panel was already built and content exists - just rebuild layout
                Debug.Log($"[SessionSettingsPanelBuilder] OnEnable: Panel already built with content, rebuilding layout");
                RebuildLayout();
            }
            RegisterListeners();
        }

        private void OnDisable()
        {
            Debug.Log($"[SessionSettingsPanelBuilder] OnDisable");
            UnregisterListeners();
        }

        private void OnDestroy()
        {
            ClearUI();
        }
        
        /// <summary>
        /// Auto-find references if not assigned in inspector.
        /// Also ensures the ScrollRect is properly configured.
        /// </summary>
        private void AutoFindReferences()
        {
            // Find ScrollRect if not assigned
            if (_scrollRect == null)
            {
                _scrollRect = GetComponentInChildren<ScrollRect>(true);
                if (_scrollRect != null)
                {
                    Debug.Log($"[SessionSettingsPanelBuilder] Auto-found ScrollRect: {_scrollRect.name}");
                    
                    // Ensure ScrollRect fills available space
                    var scrollRectTransform = _scrollRect.transform as RectTransform;
                    if (scrollRectTransform != null)
                    {
                        scrollRectTransform.anchorMin = new Vector2(0, 0);
                        scrollRectTransform.anchorMax = new Vector2(1, 1);
                        scrollRectTransform.offsetMin = Vector2.zero;
                        scrollRectTransform.offsetMax = Vector2.zero;
                    }
                }
            }
            
            // Find content container if not assigned
            if (_contentContainer == null && _scrollRect != null)
            {
                _contentContainer = _scrollRect.content;
                if (_contentContainer != null)
                {
                    Debug.Log($"[SessionSettingsPanelBuilder] Auto-found content container: {_contentContainer.name}");
                    
                    // Ensure content has ContentSizeFitter and VerticalLayoutGroup
                    EnsureContentHasLayoutComponents();
                }
            }
        }
        
        /// <summary>
        /// Ensures the content container has the required layout components for scrolling.
        /// </summary>
        private void EnsureContentHasLayoutComponents()
        {
            if (_contentContainer == null) return;
            
            // Ensure VerticalLayoutGroup exists
            var vlg = _contentContainer.GetComponent<VerticalLayoutGroup>();
            if (vlg == null)
            {
                vlg = _contentContainer.gameObject.AddComponent<VerticalLayoutGroup>();
                vlg.childAlignment = TextAnchor.UpperCenter;
                vlg.childControlWidth = true;
                vlg.childControlHeight = true;
                vlg.childForceExpandWidth = true;
                vlg.childForceExpandHeight = false;
                vlg.spacing = 10f;
                vlg.padding = new RectOffset(20, 20, 20, 20);
                Debug.Log("[SessionSettingsPanelBuilder] Added VerticalLayoutGroup to content container");
            }
            
            // Ensure ContentSizeFitter exists
            var csf = _contentContainer.GetComponent<ContentSizeFitter>();
            if (csf == null)
            {
                csf = _contentContainer.gameObject.AddComponent<ContentSizeFitter>();
                csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
                csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                Debug.Log("[SessionSettingsPanelBuilder] Added ContentSizeFitter to content container");
            }
        }
        
        /// <summary>
        /// Ensures the panel has a LayoutElement so it gets proper size in layout groups.
        /// Also configures the panel's RectTransform to properly fill available space.
        /// </summary>
        private void EnsureLayoutElement()
        {
            // Ensure we have a LayoutElement for layout group participation
            layoutElement = GetComponent<LayoutElement>();
            if (layoutElement == null)
            {
                layoutElement = gameObject.AddComponent<LayoutElement>();
            }
            
            // Set flexible height so this panel fills available space in VLG
            layoutElement.flexibleHeight = 1f;
            
            // Configure RectTransform to stretch and fill available space
            var rect = transform as RectTransform;
            if (rect != null)
            {
                // Set to stretch in both directions
                rect.anchorMin = new Vector2(0, 0);
                rect.anchorMax = new Vector2(1, 1);
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
            }
            
            Debug.Log($"[SessionSettingsPanelBuilder] EnsureLayoutElement: configured with flexibleHeight=1");
        }
        
        private void CalculateAndSetHeight()
        {
            var rect = transform as RectTransform;
            var parentRect = transform.parent as RectTransform;
            
            if (rect == null || parentRect == null) return;
            
            // Get parent's height
            float parentHeight = parentRect.rect.height;
            
            // Calculate height used by siblings
            float siblingHeight = 0;
            foreach (Transform sibling in transform.parent)
            {
                if (sibling == transform) continue;
                
                var siblingRect = sibling as RectTransform;
                if (siblingRect != null && sibling.gameObject.activeInHierarchy)
                {
                    // Get the sibling's preferred height from LayoutElement, or its sizeDelta
                    var siblingLE = sibling.GetComponent<LayoutElement>();
                    if (siblingLE != null && siblingLE.preferredHeight > 0)
                    {
                        siblingHeight += siblingLE.preferredHeight;
                    }
                    else
                    {
                        siblingHeight += siblingRect.rect.height;
                    }
                }
            }
            
            // Get VLG spacing
            var parentVLG = transform.parent.GetComponent<VerticalLayoutGroup>();
            float spacing = parentVLG != null ? parentVLG.spacing : 0;
            int siblingCount = transform.parent.childCount;
            float totalSpacing = spacing * (siblingCount - 1);
            
            // Calculate available height
            float availableHeight = parentHeight - siblingHeight - totalSpacing;
            
            // Ensure minimum height
            availableHeight = Mathf.Max(availableHeight, 300f);
            
            // Set the height
            rect.sizeDelta = new Vector2(rect.sizeDelta.x, availableHeight);
            
            Debug.Log($"[SessionSettingsPanelBuilder] CalculateAndSetHeight: parent={parentHeight}, siblings={siblingHeight}, spacing={totalSpacing}, available={availableHeight}");
        }

        #endregion

        #region Build

        private void BuildIfNeeded()
        {
            if (_isBuilt) return;
            if (_isBuildInProgress) return; // Prevent multiple coroutines
            
            // Can't start coroutines on inactive game objects - defer to OnEnable
            if (!gameObject.activeInHierarchy)
            {
                return;
            }
            
            _isBuildInProgress = true;
            // Delay build to next frame so layout has time to calculate sizes
            StartCoroutine(BuildNextFrame());
        }
        
        private System.Collections.IEnumerator BuildNextFrame()
        {
            // Wait for end of frame so layout has time to calculate sizes
            yield return null;
            
            // Check if we were disabled while waiting - if so, abort and let OnEnable handle it
            if (!gameObject.activeInHierarchy)
            {
                Debug.Log($"[SessionSettingsPanelBuilder] BuildNextFrame: Panel disabled during wait, aborting build");
                _isBuildInProgress = false;
                yield break;
            }
            
            // Force layout update on parent hierarchy
            Canvas.ForceUpdateCanvases();
            
            // Log the panel size for debugging
            var rect = transform as RectTransform;
            Debug.Log($"[SessionSettingsPanelBuilder] Panel size after layout: {rect?.rect.size}");
            
            if (!_isBuilt)
            {
                BuildUI();
                _isBuilt = true;
                
                // Register listeners AFTER controls are created
                // OnEnable runs before BuildUI completes (due to coroutine), so we must register here
                RegisterListeners();
                
                // Apply any pending operations that were queued during build
                ApplyPendingOperations();
            }
            
            _isBuildInProgress = false;
        }
        
        /// <summary>
        /// Applies any pending Configure/SetData operations that were queued while build was in progress.
        /// </summary>
        private void ApplyPendingOperations()
        {
            // Suppress all callbacks during pending operations to prevent spurious events
            // This is critical because visibility/interactability changes might trigger
            // callbacks before the data is properly applied to the UI
            _suppressCallbacks = true;
            try
            {
                if (_pendingMode.HasValue)
                {
                    _mode = _pendingMode.Value;
                    _config = _pendingConfig ?? ((_pendingMode.Value == SettingsPanelMode.Create)
                        ? SettingsPanelConfig.ForLobbyBrowser
                        : SettingsPanelConfig.ForLobbyRoom);
                    _pendingMode = null;
                    _pendingConfig = null;
                    
                    ApplyVisibility();
                    ApplyInteractability();
                }
                
                if (_pendingData != null)
                {
                    _currentData = _pendingData.Clone();
                    _pendingData = null;
                    _dataInitialized = true; // Mark that real data has been set
                    ApplyDataToUI();
                }
            }
            finally
            {
                _suppressCallbacks = false;
            }
        }

        /// <summary>
        /// Builds the settings UI dynamically.
        /// </summary>
        public void BuildUI()
        {
            ClearUI();

            if (_contentContainer == null)
            {
                Debug.LogError("[SessionSettingsPanelBuilder] Content container is not assigned!");
                return;
            }
            
            // Force layout rebuild before checking size
            Canvas.ForceUpdateCanvases();
            if (transform is RectTransform rootRect)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(rootRect);
            }

            Debug.Log($"[SessionSettingsPanelBuilder] Building UI. Container: {_contentContainer.name}, Size: {_contentContainer.rect.size}, Panel size: {(transform as RectTransform)?.rect.size}, ScrollView size: {(_scrollRect?.transform as RectTransform)?.rect.size}");

            // Create category drawers
            _lobbyInfoDrawer = CreateCategoryDrawer("Lobby Info");
            _gameplayDrawer = CreateCategoryDrawer("Gameplay Settings");
            _sessionDrawer = CreateCategoryDrawer("Session Settings");

            // IMPORTANT: Expand drawers BEFORE adding content so Content is active
            // This ensures items are added to an active GameObject
            _lobbyInfoDrawer?.SetDrawerWithoutRebuild(true);
            _gameplayDrawer?.SetDrawerWithoutRebuild(true);
            _sessionDrawer?.SetDrawerWithoutRebuild(true);

            // Build settings in each category
            BuildLobbyInfoSettings();
            BuildGameplaySettings();
            BuildSessionSettings();

            // Set final expansion states (user preference)
            _lobbyInfoDrawer?.SetDrawerWithoutRebuild(_lobbyInfoExpanded);
            _gameplayDrawer?.SetDrawerWithoutRebuild(_gameplayExpanded);
            _sessionDrawer?.SetDrawerWithoutRebuild(_sessionExpanded);

            // Force layout rebuild
            RebuildLayout();
            
            Debug.Log($"[SessionSettingsPanelBuilder] Build complete. Container child count: {_contentContainer.childCount}");
        }

        private DropdownDrawer CreateCategoryDrawer(string categoryName)
        {
            if (_categoryDrawerPrefab == null)
            {
                Debug.LogWarning($"[SessionSettingsPanelBuilder] No category drawer prefab assigned, creating simple header for: {categoryName}");
                return null;
            }

            var drawer = Instantiate(_categoryDrawerPrefab, _contentContainer);
            _createdObjects.Add(drawer.gameObject);

            // Set the label text
            var label = drawer.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null)
            {
                label.text = categoryName;
            }

            return drawer;
        }

        private void BuildLobbyInfoSettings()
        {
            var parent = _lobbyInfoDrawer != null
                ? GetDrawerContentTransform(_lobbyInfoDrawer)
                : _contentContainer;

            Debug.Log($"[SessionSettingsPanelBuilder] BuildLobbyInfoSettings: parent={parent?.name}, active={parent?.gameObject.activeInHierarchy}");

            // Lobby Name (Text)
            _lobbyNameRow = CreateTextSetting(parent, "Lobby Name", "Enter lobby name...", out _lobbyNameInput);
            Debug.Log($"[SessionSettingsPanelBuilder] Created Lobby Name row: {_lobbyNameRow?.name}");

            // Max Players (Slider 2-64)
            _maxPlayersRow = CreateSliderSetting(parent, "Max Players", 2, 64, true, out _maxPlayersSlider, disableTextInput: true);
            Debug.Log($"[SessionSettingsPanelBuilder] Created Max Players row: {_maxPlayersRow?.name}, slider={_maxPlayersSlider != null}");

            // Privacy (Dropdown)
            _privacyRow = CreateDropdownSetting(parent, "Privacy", CreatePrivacyOptions(), out _privacyDropdown);
            Debug.Log($"[SessionSettingsPanelBuilder] Created Privacy row: {_privacyRow?.name}, dropdown={_privacyDropdown != null}");

            // Session Type (Dropdown) - Automatic (UPnP/Lobby) vs Manual (Server/Port Forward)
            _sessionTypeRow = CreateDropdownSetting(parent, "Connection Mode", CreateSessionTypeOptions(), out _sessionTypeDropdown);
            Debug.Log($"[SessionSettingsPanelBuilder] Created Session Type row: {_sessionTypeRow?.name}, dropdown={_sessionTypeDropdown != null}");

            // Password (Text with reveal toggle)
            _passwordRow = CreatePasswordSetting(parent, "Password", "Enter password...", out _passwordInput, out _passwordRevealButton, out _passwordRevealIcon);
            Debug.Log($"[SessionSettingsPanelBuilder] Created Password row: {_passwordRow?.name}");
            
            Debug.Log($"[SessionSettingsPanelBuilder] BuildLobbyInfoSettings complete. Parent child count: {parent?.childCount}");
        }

        private void BuildGameplaySettings()
        {
            var parent = _gameplayDrawer != null
                ? GetDrawerContentTransform(_gameplayDrawer)
                : _contentContainer;

            Debug.Log($"[SessionSettingsPanelBuilder] BuildGameplaySettings: parent={parent?.name}, active={parent?.gameObject.activeInHierarchy}");

            // Band Size (Slider 0-8, where 0 = unlimited)
            _bandSizeRow = CreateSliderSetting(parent, "Band Size", 0, 8, true, out _bandSizeSlider, disableTextInput: true);
            Debug.Log($"[SessionSettingsPanelBuilder] Created Band Size row: {_bandSizeRow?.name}, slider={_bandSizeSlider != null}");

            // No Fail Mode (Toggle)
            _noFailRow = CreateToggleSetting(parent, "No Fail Mode", out _noFailToggle);
            Debug.Log($"[SessionSettingsPanelBuilder] Created No Fail row: {_noFailRow?.name}, toggle={_noFailToggle != null}");

            // Allow Modifiers (Toggle)
            _allowModifiersRow = CreateToggleSetting(parent, "Allow Modifiers", out _allowModifiersToggle);
            Debug.Log($"[SessionSettingsPanelBuilder] Created Allow Modifiers row: {_allowModifiersRow?.name}, toggle={_allowModifiersToggle != null}");
            
            // Allowed Instruments (Icon Buttons)
            _allowedInstrumentsRow = CreateInstrumentPickerSetting(parent, "Allowed Instruments");
            Debug.Log($"[SessionSettingsPanelBuilder] Created Allowed Instruments row: {_allowedInstrumentsRow?.name}");

            // Songs Support All Instruments (Toggle) - placed after allowed instruments
            _sharedSongsRow = CreateToggleSetting(parent, "Songs Support All Instruments", out _sharedSongsToggle);
            Debug.Log($"[SessionSettingsPanelBuilder] Created Songs Support All Instruments row: {_sharedSongsRow?.name}, toggle={_sharedSongsToggle != null}");
            
            // Local Players First (Toggle)
            _localPlayersFirstRow = CreateToggleSetting(parent, "Align Local Tracks Left", out _localPlayersFirstToggle);
            Debug.Log($"[SessionSettingsPanelBuilder] Created Align Local Tracks Left row: {_localPlayersFirstRow?.name}, toggle={_localPlayersFirstToggle != null}");
            
            Debug.Log($"[SessionSettingsPanelBuilder] BuildGameplaySettings complete. Parent child count: {parent?.childCount}");
        }

        private void BuildSessionSettings()
        {
            var parent = _sessionDrawer != null
                ? GetDrawerContentTransform(_sessionDrawer)
                : _contentContainer;

            Debug.Log($"[SessionSettingsPanelBuilder] BuildSessionSettings: parent={parent?.name}, active={parent?.gameObject.activeInHierarchy}");

            // Enable Preset Sync (Toggle)
            _presetSyncRow = CreateToggleSetting(parent, "Enable Preset Sync", out _presetSyncToggle);
            Debug.Log($"[SessionSettingsPanelBuilder] Created Preset Sync row: {_presetSyncRow?.name}, toggle={_presetSyncToggle != null}");

            // Allow Late Join (Toggle)
            _lateJoinRow = CreateToggleSetting(parent, "Allow Late Join", out _lateJoinToggle);
            Debug.Log($"[SessionSettingsPanelBuilder] Created Late Join row: {_lateJoinRow?.name}, toggle={_lateJoinToggle != null}");
            
            Debug.Log($"[SessionSettingsPanelBuilder] BuildSessionSettings complete. Parent child count: {parent?.childCount}");
        }

        private Transform GetDrawerContentTransform(DropdownDrawer drawer)
        {
            // The DropdownDrawer has a Content child (the _foldout) that holds the items
            // Try common names
            var content = drawer.transform.Find("Content");
            if (content == null)
                content = drawer.transform.Find("Foldout");
            
            if (content == null)
            {
                // Fall back to finding by component structure - look for a child with VerticalLayoutGroup
                foreach (Transform child in drawer.transform)
                {
                    if (child.GetComponent<UnityEngine.UI.VerticalLayoutGroup>() != null)
                    {
                        content = child;
                        break;
                    }
                }
            }
            
            Debug.Log($"[SessionSettingsPanelBuilder] GetDrawerContentTransform for {drawer.name}: found {(content != null ? content.name : "null, using drawer itself")}");
            return content != null ? content : drawer.transform;
        }

        #endregion

        #region Setting Creation Helpers

        /// <summary>
        /// Removes BaseSettingVisual-derived components from instantiated prefabs.
        /// These components have navigation schemes that can interfere with LobbyRoomMenu's navigation.
        /// IMPORTANT: We disable components immediately because Destroy() is deferred until end of frame.
        /// </summary>
        private void RemoveSettingVisualComponents(GameObject row)
        {
            if (row == null) return;
            
            // Remove any BaseSettingVisual-derived components (SliderSettingVisual, ToggleSettingVisual, etc.)
            // These have GetNavigationScheme() that could interfere with our lobby navigation
            var settingVisuals = row.GetComponentsInChildren<YARG.Menu.Settings.Visuals.BaseSettingVisual>(true);
            foreach (var visual in settingVisuals)
            {
                Debug.Log($"[SessionSettingsPanelBuilder] Removing BaseSettingVisual component: {visual.GetType().Name} on {visual.gameObject.name}");
                // Disable immediately - Destroy() is deferred until end of frame and component can still respond to events
                visual.enabled = false;
                DestroyImmediate(visual);
            }
            
            // Also remove BaseSettingNavigatable if present (shouldn't be, but safety check)
            var navigatables = row.GetComponentsInChildren<YARG.Menu.Settings.BaseSettingNavigatable>(true);
            foreach (var nav in navigatables)
            {
                Debug.Log($"[SessionSettingsPanelBuilder] Removing BaseSettingNavigatable on {nav.gameObject.name}");
                nav.enabled = false;
                DestroyImmediate(nav);
            }
            
            // Also remove any NavigatableBehaviour components that might push navigation schemes
            var navigatableBehaviours = row.GetComponentsInChildren<YARG.Menu.Navigation.NavigatableBehaviour>(true);
            foreach (var navBehaviour in navigatableBehaviours)
            {
                Debug.Log($"[SessionSettingsPanelBuilder] Removing NavigatableBehaviour: {navBehaviour.GetType().Name} on {navBehaviour.gameObject.name}");
                navBehaviour.enabled = false;
                DestroyImmediate(navBehaviour);
            }
            
            // Remove TextFieldNavigationDisabler which pushes empty nav scheme when input fields get focus
            var textFieldDisablers = row.GetComponentsInChildren<YARG.Menu.Navigation.TextFieldNavigationDisabler>(true);
            foreach (var disabler in textFieldDisablers)
            {
                Debug.Log($"[SessionSettingsPanelBuilder] Removing TextFieldNavigationDisabler on {disabler.gameObject.name}");
                disabler.enabled = false;
                DestroyImmediate(disabler);
            }
        }

        /// <summary>
        /// Ensures a setting row has a LayoutElement with proper preferredHeight.
        /// For prefab-based settings, adjusts the internal layout to work in narrow containers.
        /// </summary>
        private void EnsureRowLayoutElement(GameObject row, float preferredHeight = 90f)
        {
            if (row == null) return;
            
            var le = row.GetComponent<LayoutElement>();
            if (le == null)
            {
                le = row.AddComponent<LayoutElement>();
            }
            
            // Set preferred height so VLG knows how tall this row should be
            le.preferredHeight = preferredHeight;
            le.minHeight = preferredHeight;
            le.flexibleWidth = 1;
            
            // Configure row to stretch horizontally within VLG
            var rect = row.GetComponent<RectTransform>();
            if (rect != null)
            {
                rect.anchorMin = new Vector2(0, 0);
                rect.anchorMax = new Vector2(1, 0);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(0, preferredHeight);
            }
            
            // Adjust internal label and container for narrow sidebar layout
            AdjustPrefabLayoutForNarrowContainer(row);
            
            Debug.Log($"[SessionSettingsPanelBuilder] EnsureRowLayoutElement: {row.name} preferredHeight={preferredHeight}");
        }
        
        /// <summary>
        /// Adjusts internal label and container positioning in prefab-based settings
        /// to fit within a narrow container. Uses fixed pixel layout for consistency.
        /// </summary>
        private void AdjustPrefabLayoutForNarrowContainer(GameObject row)
        {
            if (row == null) return;
            
            // Fixed pixel layout constants
            const float LABEL_WIDTH = 160f;
            const float LABEL_LEFT_MARGIN = 10f;
            const float CONTAINER_START = 175f;  // LABEL_LEFT_MARGIN + LABEL_WIDTH + 5px gap
            const float RIGHT_PADDING = 10f;
            
            // Find the Label child - direct child of the row
            Transform labelTransform = row.transform.Find("Label");
            if (labelTransform != null)
            {
                var labelRect = labelTransform.GetComponent<RectTransform>();
                if (labelRect != null)
                {
                    // Fixed width label anchored to left
                    labelRect.anchorMin = new Vector2(0, 0);
                    labelRect.anchorMax = new Vector2(0, 1);
                    labelRect.pivot = new Vector2(0, 0.5f);
                    labelRect.anchoredPosition = new Vector2(LABEL_LEFT_MARGIN, 0);
                    labelRect.sizeDelta = new Vector2(LABEL_WIDTH, 0);
                }
                
                // Configure label text
                var labelText = labelTransform.GetComponent<TextMeshProUGUI>();
                if (labelText != null)
                {
                    labelText.enableAutoSizing = false;
                    labelText.fontSize = 14;
                    labelText.margin = Vector4.zero;
                    labelText.alignment = TextAlignmentOptions.MidlineLeft;
                    labelText.overflowMode = TextOverflowModes.Ellipsis;
                }
            }
            
            // Find container children (everything except Label, Background, BG, Even)
            for (int i = 0; i < row.transform.childCount; i++)
            {
                Transform child = row.transform.GetChild(i);
                
                if (child.name == "Label" || child.name.Contains("Background") || child.name.Contains("BG") || child.name.Contains("Even"))
                    continue;
                
                var containerRect = child.GetComponent<RectTransform>();
                if (containerRect == null) continue;
                
                // Check if this child contains any actual controls
                bool hasControls = child.GetComponentInChildren<Slider>(true) != null ||
                                   child.GetComponentInChildren<Toggle>(true) != null ||
                                   child.GetComponentInChildren<TMP_Dropdown>(true) != null ||
                                   child.GetComponentInChildren<TMP_InputField>(true) != null;
                
                if (hasControls || child.childCount > 0)
                {
                    // Container stretches from CONTAINER_START to right edge
                    containerRect.anchorMin = new Vector2(0, 0);
                    containerRect.anchorMax = new Vector2(1, 1);
                    containerRect.pivot = new Vector2(0.5f, 0.5f);
                    containerRect.offsetMin = new Vector2(CONTAINER_START, 5);
                    containerRect.offsetMax = new Vector2(-RIGHT_PADDING, -5);
                    
                    // Adjust internal controls
                    AdjustInternalControlLayout(child);
                }
            }
        }
        
        /// <summary>
        /// Adjusts internal control layout within the container.
        /// Preserves prefab structure - just ensures controls fill their container.
        /// </summary>
        private void AdjustInternalControlLayout(Transform container)
        {
            // Process direct children - make them fill the container
            foreach (Transform control in container)
            {
                var controlRect = control.GetComponent<RectTransform>();
                if (controlRect == null) continue;
                
                // Check if this is a toggle - right-align it
                var toggleComp = control.GetComponent<Toggle>();
                if (toggleComp != null || control.name.Contains("Toggle"))
                {
                    // Toggle right-aligned within container
                    controlRect.anchorMin = new Vector2(1, 0.5f);
                    controlRect.anchorMax = new Vector2(1, 0.5f);
                    controlRect.pivot = new Vector2(1, 0.5f);
                    controlRect.anchoredPosition = Vector2.zero;
                    controlRect.sizeDelta = new Vector2(80, 40);
                    continue;
                }
                
                // Check if this is a slider/ValueSlider - add small left padding to align with input borders
                var valueSlider = control.GetComponent<ValueSlider>();
                var sliderComp = control.GetComponent<Slider>();
                if (valueSlider != null || sliderComp != null || control.name.Contains("Slider") || control.name.Contains("ValueSlider"))
                {
                    controlRect.anchorMin = Vector2.zero;
                    controlRect.anchorMax = Vector2.one;
                    controlRect.pivot = new Vector2(0.5f, 0.5f);
                    controlRect.anchoredPosition = Vector2.zero;
                    controlRect.sizeDelta = Vector2.zero;
                    controlRect.offsetMin = new Vector2(5, 0);  // Small left padding to align with input field borders
                    controlRect.offsetMax = Vector2.zero;
                    continue;
                }
                
                // For dropdowns and other controls - stretch to fill
                controlRect.anchorMin = Vector2.zero;
                controlRect.anchorMax = Vector2.one;
                controlRect.pivot = new Vector2(0.5f, 0.5f);
                controlRect.anchoredPosition = Vector2.zero;
                controlRect.sizeDelta = Vector2.zero;
                controlRect.offsetMin = Vector2.zero;
                controlRect.offsetMax = Vector2.zero;
            }
        }

        /// <summary>
        /// Disables automatic navigation on selectables to prevent sticky highlighting.
        /// Also disables transitions to prevent visual highlight states.
        /// </summary>
        private void DisableSelectableNavigation(GameObject row)
        {
            if (row == null) return;
            
            var selectables = row.GetComponentsInChildren<Selectable>(true);
            foreach (var selectable in selectables)
            {
                // Disable navigation
                var nav = selectable.navigation;
                nav.mode = UnityEngine.UI.Navigation.Mode.None;
                selectable.navigation = nav;
                
                // Disable color transitions to prevent sticky highlighting
                // Note: We keep the transition type but set all colors to normal
                var colors = selectable.colors;
                colors.highlightedColor = colors.normalColor;
                colors.selectedColor = colors.normalColor;
                colors.pressedColor = colors.normalColor;
                selectable.colors = colors;
            }
        }

        /// <summary>
        /// Ensures the slider track and fill area are properly sized and visible.
        /// Fixes issue where only the handle circle is visible but not the track bar.
        /// </summary>
        private void EnsureSliderTrackVisible(Slider slider)
        {
            if (slider == null) return;
            
            // Find and ensure the background (track) is visible
            var background = slider.transform.Find("Background");
            if (background != null)
            {
                var bgImage = background.GetComponent<Image>();
                if (bgImage != null)
                {
                    bgImage.enabled = true;
                    // Ensure it has visible color if transparent
                    if (bgImage.color.a < 0.1f)
                    {
                        bgImage.color = new Color(0.0f, 0.12f, 0.29f, 1f); // Dark blue background
                    }
                }
            }
            
            // Find and ensure the fill is visible
            var fill = slider.transform.Find("Fill");
            if (fill != null)
            {
                var fillImage = fill.GetComponent<Image>();
                if (fillImage != null)
                {
                    fillImage.enabled = true;
                    // Ensure it has visible color if transparent
                    if (fillImage.color.a < 0.1f)
                    {
                        fillImage.color = new Color(0.12f, 0.81f, 0.97f, 1f); // YARG cyan
                    }
                }
            }
            
            // Ensure handle is visible
            var handle = slider.transform.Find("Handle");
            if (handle != null)
            {
                var handleImage = handle.GetComponent<Image>();
                if (handleImage != null)
                {
                    handleImage.enabled = true;
                }
            }
            
            Debug.Log($"[SessionSettingsPanelBuilder] EnsureSliderTrackVisible: configured slider {slider.gameObject.name}");
        }

        /// <summary>
        /// Reconfigures a ValueSlider's internal layout for narrow containers.
        /// Preserves the prefab's internal structure but adjusts proportions.
        /// </summary>
        private void ConfigureValueSliderForNarrowContainer(ValueSlider valueSlider)
        {
            if (valueSlider == null) return;
            
            // The ValueSlider contains a Slider and InputField - don't modify their internal structure
            // Just ensure the ValueSlider container itself fills its parent properly
            var vsRect = valueSlider.GetComponent<RectTransform>();
            if (vsRect != null)
            {
                vsRect.anchorMin = Vector2.zero;
                vsRect.anchorMax = Vector2.one;
                vsRect.offsetMin = Vector2.zero;
                vsRect.offsetMax = Vector2.zero;
                vsRect.pivot = new Vector2(0.5f, 0.5f);
                vsRect.anchoredPosition = Vector2.zero;
                vsRect.sizeDelta = Vector2.zero;
            }
            
            // Ensure slider track is visible
            var slider = valueSlider.GetComponentInChildren<Slider>(true);
            if (slider != null)
            {
                EnsureSliderTrackVisible(slider);
            }
            
            Debug.Log($"[SessionSettingsPanelBuilder] ConfigureValueSliderForNarrowContainer: adjusted {valueSlider.gameObject.name}");
        }

        private GameObject CreateTextSetting(Transform parent, string label, string placeholder, out TMP_InputField inputField)
        {
            inputField = null;

            if (_textSettingPrefab == null)
            {
                Debug.LogWarning($"[SessionSettingsPanelBuilder] No text setting prefab for: {label}");
                return null;
            }

            // Layout constants (must match AdjustPrefabLayoutForNarrowContainer)
            const float LABEL_WIDTH = 160f;
            const float LABEL_LEFT_MARGIN = 10f;
            const float CONTAINER_START = 175f;
            const float RIGHT_PADDING = 10f;

            // Create a row container
            var row = new GameObject($"TextSetting_{label}");
            row.transform.SetParent(parent, false);
            _createdObjects.Add(row);
            
            var rowRect = row.AddComponent<RectTransform>();
            rowRect.anchorMin = new Vector2(0, 0);
            rowRect.anchorMax = new Vector2(1, 0);
            rowRect.pivot = new Vector2(0.5f, 0.5f);
            rowRect.sizeDelta = new Vector2(0, 80);
            
            // Add LayoutElement for VLG to size this row
            var rowLayout = row.AddComponent<LayoutElement>();
            rowLayout.preferredHeight = 80;
            rowLayout.minHeight = 80;
            rowLayout.flexibleWidth = 1;
            
            // Create label with fixed pixel positioning
            var labelObj = new GameObject("Label");
            labelObj.transform.SetParent(row.transform, false);
            
            var labelRect = labelObj.AddComponent<RectTransform>();
            labelRect.anchorMin = new Vector2(0, 0);
            labelRect.anchorMax = new Vector2(0, 1);
            labelRect.pivot = new Vector2(0, 0.5f);
            labelRect.anchoredPosition = new Vector2(LABEL_LEFT_MARGIN, 0);
            labelRect.sizeDelta = new Vector2(LABEL_WIDTH, 0);
            
            var labelText = labelObj.AddComponent<TextMeshProUGUI>();
            labelText.text = label.ToUpper();
            labelText.fontSize = 14;
            labelText.enableAutoSizing = false;
            labelText.fontStyle = FontStyles.UpperCase;
            labelText.color = Color.white;
            labelText.alignment = TextAlignmentOptions.MidlineLeft;
            labelText.margin = Vector4.zero;
            labelText.overflowMode = TextOverflowModes.Ellipsis;
            
            // Instantiate the input field with fixed pixel positioning
            var inputFieldInstance = Instantiate(_textSettingPrefab, row.transform);
            inputFieldInstance.name = "Field";
            
            var inputRect = inputFieldInstance.GetComponent<RectTransform>();
            if (inputRect != null)
            {
                inputRect.anchorMin = new Vector2(0, 0);
                inputRect.anchorMax = new Vector2(1, 1);
                inputRect.pivot = new Vector2(0.5f, 0.5f);
                inputRect.offsetMin = new Vector2(CONTAINER_START, 5);
                inputRect.offsetMax = new Vector2(-RIGHT_PADDING, -5);
            }
            
            // Get input field and configure
            inputField = inputFieldInstance.GetComponentInChildren<TMP_InputField>(true);
            if (inputField == null)
            {
                Debug.LogWarning($"[SessionSettingsPanelBuilder] No TMP_InputField found in text setting prefab for: {label}.");
            }
            else if (inputField.placeholder is TextMeshProUGUI ph)
            {
                ph.text = placeholder;
            }
            
            Debug.Log($"[SessionSettingsPanelBuilder] Created text setting: {label}, inputField={inputField != null}");
            return row;
        }

        private GameObject CreatePasswordSetting(Transform parent, string label, string placeholder, out TMP_InputField inputField, out Button revealButton, out Image revealIcon)
        {
            inputField = null;
            revealButton = null;
            revealIcon = null;

            if (_textSettingPrefab == null)
            {
                Debug.LogWarning($"[SessionSettingsPanelBuilder] No text setting prefab for password: {label}");
                return null;
            }

            // Layout constants (must match AdjustPrefabLayoutForNarrowContainer)
            const float LABEL_WIDTH = 160f;
            const float LABEL_LEFT_MARGIN = 10f;
            const float CONTAINER_START = 175f;
            const float RIGHT_PADDING = 10f;

            // Create a row container
            var row = new GameObject($"PasswordSetting_{label}");
            row.transform.SetParent(parent, false);
            _createdObjects.Add(row);
            
            var rowRect = row.AddComponent<RectTransform>();
            rowRect.anchorMin = new Vector2(0, 0);
            rowRect.anchorMax = new Vector2(1, 0);
            rowRect.pivot = new Vector2(0.5f, 0.5f);
            rowRect.sizeDelta = new Vector2(0, 80);
            
            // Add LayoutElement for VLG to size this row
            var rowLayout = row.AddComponent<LayoutElement>();
            rowLayout.preferredHeight = 80;
            rowLayout.minHeight = 80;
            rowLayout.flexibleWidth = 1;
            
            // Create label with fixed pixel positioning
            var labelObj = new GameObject("Label");
            labelObj.transform.SetParent(row.transform, false);
            
            var labelRect = labelObj.AddComponent<RectTransform>();
            labelRect.anchorMin = new Vector2(0, 0);
            labelRect.anchorMax = new Vector2(0, 1);
            labelRect.pivot = new Vector2(0, 0.5f);
            labelRect.anchoredPosition = new Vector2(LABEL_LEFT_MARGIN, 0);
            labelRect.sizeDelta = new Vector2(LABEL_WIDTH, 0);
            
            var labelText = labelObj.AddComponent<TextMeshProUGUI>();
            labelText.text = label.ToUpper();
            labelText.fontSize = 14;
            labelText.enableAutoSizing = false;
            labelText.fontStyle = FontStyles.UpperCase;
            labelText.color = Color.white;
            labelText.alignment = TextAlignmentOptions.MidlineLeft;
            labelText.margin = Vector4.zero;
            labelText.overflowMode = TextOverflowModes.Ellipsis;
            
            // Instantiate the input field with fixed pixel positioning
            var inputFieldInstance = Instantiate(_textSettingPrefab, row.transform);
            inputFieldInstance.name = "Field";
            
            var inputRect = inputFieldInstance.GetComponent<RectTransform>();
            if (inputRect != null)
            {
                inputRect.anchorMin = new Vector2(0, 0);
                inputRect.anchorMax = new Vector2(1, 1);
                inputRect.pivot = new Vector2(0.5f, 0.5f);
                inputRect.offsetMin = new Vector2(CONTAINER_START, 5);
                inputRect.offsetMax = new Vector2(-RIGHT_PADDING, -5);
            }
            
            // Get input field and configure
            inputField = inputFieldInstance.GetComponentInChildren<TMP_InputField>(true);
            if (inputField == null)
            {
                Debug.LogWarning($"[SessionSettingsPanelBuilder] No TMP_InputField found in text setting prefab for password: {label}.");
            }
            else
            {
                if (inputField.placeholder is TextMeshProUGUI ph)
                {
                    ph.text = placeholder;
                }
                
                // Set as password field
                inputField.contentType = TMP_InputField.ContentType.Password;
                
                // Create reveal button next to input field
                revealButton = CreatePasswordRevealButton(inputField, inputFieldInstance, out revealIcon);
            }
            
            Debug.Log($"[SessionSettingsPanelBuilder] Created password setting: {label}, inputField={inputField != null}, revealButton={revealButton != null}");
            return row;
        }

        private Button CreatePasswordRevealButton(TMP_InputField inputField, GameObject row, out Image iconImage)
        {
            iconImage = null;
            
            if (inputField == null)
                return null;
            
            // Find or create a container for the reveal button
            var inputParent = inputField.transform.parent;
            
            // Create the reveal button
            var buttonObj = new GameObject("PasswordRevealButton");
            buttonObj.transform.SetParent(inputParent, false);
            
            // Add RectTransform
            var rectTransform = buttonObj.AddComponent<RectTransform>();
            rectTransform.anchorMin = new Vector2(1, 0.5f);
            rectTransform.anchorMax = new Vector2(1, 0.5f);
            rectTransform.pivot = new Vector2(1, 0.5f);
            rectTransform.anchoredPosition = new Vector2(-5, 0);
            rectTransform.sizeDelta = new Vector2(30, 30);
            
            // Add Image component for button background (transparent for hitbox)
            var bgImage = buttonObj.AddComponent<Image>();
            bgImage.color = new Color(0, 0, 0, 0); // Transparent background, just for raycast
            bgImage.raycastTarget = true;
            
            // Add Button component
            var button = buttonObj.AddComponent<Button>();
            button.targetGraphic = bgImage;
            
            // Disable navigation
            var nav = button.navigation;
            nav.mode = UnityEngine.UI.Navigation.Mode.None;
            button.navigation = nav;
            
            // Create icon child with Image component for the sprite
            var iconObj = new GameObject("Icon");
            iconObj.transform.SetParent(buttonObj.transform, false);
            
            var iconRect = iconObj.AddComponent<RectTransform>();
            iconRect.anchorMin = Vector2.zero;
            iconRect.anchorMax = Vector2.one;
            iconRect.offsetMin = new Vector2(4, 4);
            iconRect.offsetMax = new Vector2(-4, -4);
            
            iconImage = iconObj.AddComponent<Image>();
            iconImage.preserveAspect = true;
            iconImage.color = Color.white;
            
            // Set initial sprite (hidden state)
            if (_visibilityHiddenSprite != null)
            {
                iconImage.sprite = _visibilityHiddenSprite;
            }
            else
            {
                // Fallback: load from resources path
                var hiddenSprite = Resources.Load<Sprite>("Art/Menu/Common/Icons/invisible");
                if (hiddenSprite != null)
                    iconImage.sprite = hiddenSprite;
            }
            
            // Set up click handler
            var inputRef = inputField;
            var iconRef = iconImage;
            button.onClick.AddListener(() => TogglePasswordReveal(inputRef, iconRef));
            
            // Track in created objects for cleanup
            _createdObjects.Add(buttonObj);
            
            return button;
        }

        private void TogglePasswordReveal(TMP_InputField inputField, Image iconImage)
        {
            if (inputField == null)
                return;
            
            _isPasswordRevealed = !_isPasswordRevealed;
            
            // Store current text before changing content type
            string currentText = inputField.text;
            
            // Toggle content type
            inputField.contentType = _isPasswordRevealed 
                ? TMP_InputField.ContentType.Standard 
                : TMP_InputField.ContentType.Password;
            
            // Force refresh
            inputField.ForceLabelUpdate();
            inputField.text = currentText;
            
            // Update icon sprite
            if (iconImage != null)
            {
                if (_isPasswordRevealed)
                {
                    iconImage.sprite = _visibilityVisibleSprite;
                }
                else
                {
                    iconImage.sprite = _visibilityHiddenSprite;
                }
            }
            
            Debug.Log($"[SessionSettingsPanelBuilder] Password reveal toggled: {_isPasswordRevealed}");
        }

        private GameObject CreateIntSetting(Transform parent, string label, int min, int max, out TMP_InputField inputField)
        {
            inputField = null;

            var prefab = _intSettingPrefab ?? _textSettingPrefab;
            if (prefab == null)
            {
                Debug.LogWarning($"[SessionSettingsPanelBuilder] No int setting prefab for: {label}");
                return null;
            }

            // Instantiate as INACTIVE to prevent navigation components from registering
            var row = Instantiate(prefab, parent);
            row.SetActive(false);
            _createdObjects.Add(row);
            row.name = $"IntSetting_{label}";
            
            // Set label FIRST (before removing BaseSettingVisual which holds the _settingLabel reference)
            SetSettingLabel(row, label);
            
            // Remove setting visual components that have navigation schemes
            RemoveSettingVisualComponents(row);
            
            // Ensure proper layout
            EnsureRowLayoutElement(row);
            
            // Disable navigation to prevent sticky highlighting
            DisableSelectableNavigation(row);

            // Get and configure input field
            inputField = row.GetComponentInChildren<TMP_InputField>(true);
            if (inputField != null)
            {
                inputField.contentType = TMP_InputField.ContentType.IntegerNumber;
                if (inputField.placeholder is TextMeshProUGUI ph)
                {
                    ph.text = $"{min}-{max}";
                }
            }

            // Activate now that navigation components are removed
            row.SetActive(true);
            
            Debug.Log($"[SessionSettingsPanelBuilder] Created int setting: {label}, inputField={inputField != null}");
            return row;
        }

        private GameObject CreateToggleSetting(Transform parent, string label, out Toggle toggle)
        {
            toggle = null;

            if (_toggleSettingPrefab == null)
            {
                Debug.LogWarning($"[SessionSettingsPanelBuilder] No toggle setting prefab for: {label}");
                return null;
            }

            // Instantiate as INACTIVE to prevent navigation components from registering
            var row = Instantiate(_toggleSettingPrefab, parent);
            row.SetActive(false);
            _createdObjects.Add(row);
            
            // Set label FIRST (before removing BaseSettingVisual which holds the _settingLabel reference)
            SetSettingLabel(row, label);
            
            // Remove setting visual components that have navigation schemes
            RemoveSettingVisualComponents(row);
            
            // Ensure proper layout
            EnsureRowLayoutElement(row);
            
            // Disable navigation to prevent sticky highlighting
            DisableSelectableNavigation(row);

            // Get toggle (use includeInactive since row is inactive)
            toggle = row.GetComponentInChildren<Toggle>(true);
            
            // Expand toggle hitbox by making the entire row clickable
            if (toggle != null)
            {
                // Clear any existing persistent listeners that might cause issues
                toggle.onValueChanged = new Toggle.ToggleEvent();
                
                // Ensure the row has a graphic for raycast target (for clicking anywhere on row)
                var rowImage = row.GetComponent<UnityEngine.UI.Image>();
                if (rowImage == null)
                {
                    rowImage = row.AddComponent<UnityEngine.UI.Image>();
                    rowImage.color = new Color(0, 0, 0, 0); // Transparent but raycastable
                }
                rowImage.raycastTarget = true;
                
                // Add a ClickHandler to the row that toggles the toggle
                var clickHandler = row.GetComponent<UnityEngine.EventSystems.IPointerClickHandler>();
                if (clickHandler == null)
                {
                    var buttonComponent = row.AddComponent<UnityEngine.UI.Button>();
                    buttonComponent.transition = UnityEngine.UI.Selectable.Transition.None;
                    var toggleRef = toggle; // Capture for closure
                    buttonComponent.onClick.AddListener(() => {
                        toggleRef.isOn = !toggleRef.isOn;
                    });
                }
            }

            // Activate now that navigation components are removed
            row.SetActive(true);
            
            return row;
        }

        private GameObject CreateDropdownSetting(Transform parent, string label, List<string> options, out TMP_Dropdown dropdown)
        {
            dropdown = null;

            if (_dropdownSettingPrefab == null)
            {
                Debug.LogWarning($"[SessionSettingsPanelBuilder] No dropdown setting prefab for: {label}");
                return null;
            }

            // Instantiate as INACTIVE to prevent navigation components from registering
            var row = Instantiate(_dropdownSettingPrefab, parent);
            row.SetActive(false);
            _createdObjects.Add(row);
            
            // Set label FIRST (before removing BaseSettingVisual which holds the _settingLabel reference)
            SetSettingLabel(row, label);
            
            // Remove setting visual components that have navigation schemes
            RemoveSettingVisualComponents(row);
            
            // Ensure proper layout
            EnsureRowLayoutElement(row);
            
            // Disable navigation to prevent sticky highlighting
            DisableSelectableNavigation(row);

            // Get and configure dropdown (use includeInactive since row is inactive)
            dropdown = row.GetComponentInChildren<TMP_Dropdown>(true);
            if (dropdown != null)
            {
                // Clear persistent listeners that might cause issues
                dropdown.onValueChanged = new TMP_Dropdown.DropdownEvent();
                
                dropdown.ClearOptions();
                dropdown.AddOptions(options);
            }

            // Activate now that navigation components are removed
            row.SetActive(true);
            
            return row;
        }

        private GameObject CreateSliderSetting(Transform parent, string label, float min, float max, bool wholeNumbers, out ValueSlider valueSlider, bool disableTextInput = false)
        {
            valueSlider = null;

            if (_sliderSettingPrefab == null)
            {
                Debug.LogWarning($"[SessionSettingsPanelBuilder] No slider setting prefab for: {label}");
                return null;
            }

            // Instantiate as INACTIVE to prevent navigation components from registering
            var row = Instantiate(_sliderSettingPrefab, parent);
            row.SetActive(false);
            _createdObjects.Add(row);
            
            // Set label FIRST (before removing BaseSettingVisual which holds the _settingLabel reference)
            SetSettingLabel(row, label);
            
            // Remove setting visual components that have navigation schemes
            // Must be after SetSettingLabel since it uses BaseSettingVisual to find the label
            RemoveSettingVisualComponents(row);
            
            // Ensure proper layout
            EnsureRowLayoutElement(row);
            
            // Disable UI navigation to prevent sticky highlights
            DisableSelectableNavigation(row);

            // Get and configure ValueSlider (use includeInactive since row is inactive)
            valueSlider = row.GetComponentInChildren<ValueSlider>(true);
            if (valueSlider != null)
            {
                // CRITICAL: Remove all listeners from ValueChanged BEFORE setting min/max
                // The prefab has a listener to SliderSettingVisual.OnValueChange which throws
                // NullReferenceException when used standalone (not connected to YARG settings system)
                valueSlider.ValueChanged.RemoveAllListeners();
                
                // Also remove persistent listeners by setting to a new event
                // RemoveAllListeners() only clears runtime listeners, not serialized ones
                valueSlider.ValueChanged = new UnityEngine.Events.UnityEvent<float>();
                
                // Also clear the underlying Slider's onValueChanged to prevent callbacks during min/max setting
                var slider = valueSlider.GetComponentInChildren<Slider>(true);
                if (slider != null)
                {
                    slider.onValueChanged.RemoveAllListeners();
                    slider.onValueChanged = new UnityEngine.UI.Slider.SliderEvent();
                    
                    // FIX: Ensure the slider track (fill area) is visible
                    // Some prefabs have the track too narrow or positioned incorrectly
                    EnsureSliderTrackVisible(slider);
                }
                
                // FIX: Reconfigure ValueSlider layout for narrow containers (sidebar)
                ConfigureValueSliderForNarrowContainer(valueSlider);
                
                valueSlider.MinimumValue = min;
                valueSlider.MaximumValue = max;
                
                // Configure the underlying slider
                if (slider != null)
                {
                    slider.wholeNumbers = wholeNumbers;
                    
                    // Re-add listener to trigger ValueSlider's OnSliderChange
                    // This properly updates the value AND fires the ValueChanged event
                    // Capture valueSlider in local variable to use in lambda
                    var sliderRef = valueSlider;
                    slider.onValueChanged.AddListener((val) => {
                        sliderRef.OnSliderChange(val);
                    });
                }
                
                // Set format string for whole numbers via reflection
                if (wholeNumbers)
                {
                    var formatField = typeof(ValueSlider).GetField("_formatString", 
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (formatField != null)
                    {
                        formatField.SetValue(valueSlider, "N0");
                    }
                }
                
                // Set initial value to min (will be overwritten by ApplyDataToUI)
                valueSlider.SetValueWithoutNotify(min);
                
                // Disable text input if requested (read-only slider display)
                if (disableTextInput)
                {
                    var inputField = valueSlider.GetComponentInChildren<TMP_InputField>(true);
                    if (inputField != null)
                    {
                        inputField.interactable = false;
                    }
                }
            }

            // Activate now that navigation components are removed
            row.SetActive(true);
            
            Debug.Log($"[SessionSettingsPanelBuilder] Created slider setting: {label}, min={min}, max={max}, valueSlider={valueSlider != null}");
            return row;
        }

        /// <summary>
        /// Creates the instrument picker setting with clickable instrument icons.
        /// Icons are displayed in two rows within the control area.
        /// </summary>
        private GameObject CreateInstrumentPickerSetting(Transform parent, string label)
        {
            // Layout constants
            const float LABEL_WIDTH = 160f;
            const float LABEL_LEFT_MARGIN = 10f;
            const float CONTAINER_START = 175f;
            const float RIGHT_PADDING = 10f;
            const float ICON_SIZE = 40f;
            const float ICON_SPACING = 6f;
            const float ROW_SPACING = 4f;
            const float ROW_HEIGHT = 100f;  // Taller to fit two rows

            // Create a row container
            var row = new GameObject($"InstrumentPickerSetting_{label}");
            row.transform.SetParent(parent, false);
            _createdObjects.Add(row);
            
            var rowRect = row.AddComponent<RectTransform>();
            rowRect.anchorMin = new Vector2(0, 0);
            rowRect.anchorMax = new Vector2(1, 0);
            rowRect.pivot = new Vector2(0.5f, 0.5f);
            rowRect.sizeDelta = new Vector2(0, ROW_HEIGHT);
            
            // Add LayoutElement for VLG to size this row
            var rowLayout = row.AddComponent<LayoutElement>();
            rowLayout.preferredHeight = ROW_HEIGHT;
            rowLayout.minHeight = ROW_HEIGHT;
            rowLayout.flexibleWidth = 1;
            
            // Create label with fixed pixel positioning
            var labelObj = new GameObject("Label");
            labelObj.transform.SetParent(row.transform, false);
            
            var labelRect = labelObj.AddComponent<RectTransform>();
            labelRect.anchorMin = new Vector2(0, 0);
            labelRect.anchorMax = new Vector2(0, 1);
            labelRect.pivot = new Vector2(0, 0.5f);
            labelRect.anchoredPosition = new Vector2(LABEL_LEFT_MARGIN, 0);
            labelRect.sizeDelta = new Vector2(LABEL_WIDTH, 0);
            
            var labelText = labelObj.AddComponent<TextMeshProUGUI>();
            labelText.text = label.ToUpper();
            labelText.fontSize = 14;
            labelText.enableAutoSizing = false;
            labelText.fontStyle = FontStyles.UpperCase;
            labelText.color = Color.white;
            labelText.alignment = TextAlignmentOptions.MidlineLeft;
            labelText.margin = Vector4.zero;
            labelText.overflowMode = TextOverflowModes.Ellipsis;
            
            // Create outer container for vertical layout (two rows)
            var outerContainer = new GameObject("OuterContainer");
            outerContainer.transform.SetParent(row.transform, false);
            
            var outerRect = outerContainer.AddComponent<RectTransform>();
            outerRect.anchorMin = new Vector2(0, 0);
            outerRect.anchorMax = new Vector2(1, 1);
            outerRect.pivot = new Vector2(0.5f, 0.5f);
            outerRect.offsetMin = new Vector2(CONTAINER_START, 5);
            outerRect.offsetMax = new Vector2(-RIGHT_PADDING, -5);
            
            // Add VerticalLayoutGroup for two rows
            var vlg = outerContainer.AddComponent<VerticalLayoutGroup>();
            vlg.childAlignment = TextAnchor.MiddleRight;  // Right-align rows
            vlg.childControlWidth = true;
            vlg.childControlHeight = false;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.spacing = ROW_SPACING;
            vlg.padding = new RectOffset(0, 0, 0, 0);
            
            // Clear existing game mode buttons
            _gameModeButtons.Clear();
            
            // Get all game modes (simplified categories matching profile settings)
            var gameModes = SessionSettingsData.GetAllGameModes();
            
            // Create single row for 4 game mode icons
            var iconRow = CreateInstrumentRow(outerContainer.transform, ICON_SIZE, ICON_SPACING);
            
            // Create buttons for each game mode
            foreach (var gameMode in gameModes)
            {
                var button = CreateGameModeToggleButton(iconRow, gameMode, ICON_SIZE);
                if (button != null)
                {
                    _gameModeButtons.Add(button);
                }
            }
            
            Debug.Log($"[SessionSettingsPanelBuilder] Created game mode picker with {_gameModeButtons.Count} buttons");
            return row;
        }
        
        /// <summary>
        /// Creates a horizontal row container for instrument icons.
        /// </summary>
        private Transform CreateInstrumentRow(Transform parent, float iconSize, float spacing)
        {
            var rowObj = new GameObject("IconRow");
            rowObj.transform.SetParent(parent, false);
            _createdObjects.Add(rowObj);
            
            var rowRect = rowObj.AddComponent<RectTransform>();
            rowRect.sizeDelta = new Vector2(0, iconSize);
            
            var hlg = rowObj.AddComponent<HorizontalLayoutGroup>();
            hlg.childAlignment = TextAnchor.MiddleRight;  // Right-align icons within row
            hlg.childControlWidth = false;
            hlg.childControlHeight = false;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = false;
            hlg.spacing = spacing;
            
            var layoutElement = rowObj.AddComponent<LayoutElement>();
            layoutElement.preferredHeight = iconSize;
            layoutElement.flexibleWidth = 1;
            
            return rowObj.transform;
        }

        /// <summary>
        /// Creates a single game mode toggle button.
        /// </summary>
        private GameModeToggleButton CreateGameModeToggleButton(Transform parent, GameMode gameMode, float size)
        {
            var buttonObj = new GameObject($"GameMode_{gameMode}");
            buttonObj.transform.SetParent(parent, false);
            _createdObjects.Add(buttonObj);
            
            var rectTransform = buttonObj.AddComponent<RectTransform>();
            rectTransform.sizeDelta = new Vector2(size, size);
            
            // Add Image component for the game mode icon
            var iconImage = buttonObj.AddComponent<Image>();
            iconImage.preserveAspect = true;
            iconImage.raycastTarget = true;
            
            // Load the game mode icon from Addressables
            string resourceName = GetGameModeResourceName(gameMode);
            string address = $"InstrumentIcons[{resourceName}]";
            
            try
            {
                var sprite = Addressables.LoadAssetAsync<Sprite>(address).WaitForCompletion();
                if (sprite != null)
                {
                    iconImage.sprite = sprite;
                }
                else
                {
                    Debug.LogWarning($"[SessionSettingsPanelBuilder] No sprite found for game mode: {address}");
                    iconImage.color = GetGameModeFallbackColor(gameMode);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SessionSettingsPanelBuilder] Failed to load game mode icon '{address}': {ex.Message}");
                iconImage.color = GetGameModeFallbackColor(gameMode);
            }
            
            // Add Button component
            var button = buttonObj.AddComponent<Button>();
            button.targetGraphic = iconImage;
            
            // Disable navigation
            var nav = button.navigation;
            nav.mode = UnityEngine.UI.Navigation.Mode.None;
            button.navigation = nav;
            
            // Create the toggle button data structure
            var toggleButton = new GameModeToggleButton
            {
                GameMode = gameMode,
                Button = button,
                IconImage = iconImage,
                IsEnabled = true
            };
            
            // Wire up click handler
            button.onClick.AddListener(() => OnGameModeToggleClicked(toggleButton));
            
            return toggleButton;
        }

        /// <summary>
        /// Gets the addressable resource name for a game mode icon.
        /// </summary>
        private string GetGameModeResourceName(GameMode gameMode)
        {
            return gameMode switch
            {
                GameMode.FiveFretGuitar => "guitar",
                GameMode.SixFretGuitar => "guitar",
                GameMode.FourLaneDrums => "drums",
                GameMode.FiveLaneDrums => "drums",
                GameMode.EliteDrums => "drums",
                GameMode.ProGuitar => "realGuitar",
                GameMode.ProKeys => "keys",
                GameMode.Vocals => "vocals",
                _ => "guitar"
            };
        }

        /// <summary>
        /// Gets a fallback color for a game mode when icon loading fails.
        /// </summary>
        private Color GetGameModeFallbackColor(GameMode gameMode)
        {
            return gameMode switch
            {
                GameMode.FiveFretGuitar or GameMode.SixFretGuitar => new Color(0.2f, 0.6f, 1f), // Blue
                GameMode.FourLaneDrums or GameMode.FiveLaneDrums or GameMode.EliteDrums => new Color(0.2f, 0.8f, 0.2f), // Green
                GameMode.Vocals => new Color(1f, 0.8f, 0.2f), // Yellow
                GameMode.ProKeys => new Color(0.8f, 0.2f, 0.8f), // Purple
                GameMode.ProGuitar => new Color(0.2f, 0.4f, 0.8f), // Dark Blue
                _ => Color.gray
            };
        }

        /// <summary>
        /// Handles clicking a game mode toggle button.
        /// </summary>
        private void OnGameModeToggleClicked(GameModeToggleButton toggleButton)
        {
            if (_suppressCallbacks) return;
            
            // Don't allow changes in view-only mode (client)
            if (_mode == SettingsPanelMode.ViewOnly) return;
            
            // Toggle the game mode in the data
            _currentData.ToggleGameMode(toggleButton.GameMode);
            
            // Update all button visuals based on new state
            UpdateGameModeButtonVisuals();
            
            // Notify settings changed
            NotifySettingsChanged();
        }

        /// <summary>
        /// Updates all game mode button visuals based on current data state.
        /// </summary>
        private void UpdateGameModeButtonVisuals()
        {
            Debug.Log($"[SessionSettingsPanelBuilder] UpdateGameModeButtonVisuals: AllowedGameModes.Count={_currentData.AllowedGameModes?.Count ?? 0}, Items=[{string.Join(", ", _currentData.AllowedGameModes ?? new List<GameMode>())}]");
            
            foreach (var btn in _gameModeButtons)
            {
                bool isAllowed = _currentData.IsGameModeAllowed(btn.GameMode);
                btn.IsEnabled = isAllowed;
                
                Debug.Log($"[SessionSettingsPanelBuilder] GameMode {btn.GameMode}: isAllowed={isAllowed}");
                
                // Visual feedback: enabled = full color, disabled = greyed out
                if (btn.IconImage != null)
                {
                    btn.IconImage.color = isAllowed 
                        ? Color.white 
                        : new Color(0.3f, 0.3f, 0.3f, 0.5f);
                }
            }
        }

        private void SetSettingLabel(GameObject row, string label)
        {
            if (row == null) return;
            
            // First, try to get the label via the BaseSettingVisual's _settingLabel field (preferred for prefabs)
            var settingVisual = row.GetComponent<YARG.Menu.Settings.Visuals.BaseSettingVisual>();
            if (settingVisual != null)
            {
                var labelField = typeof(YARG.Menu.Settings.Visuals.BaseSettingVisual).GetField("_settingLabel", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (labelField != null)
                {
                    var labelText = labelField.GetValue(settingVisual) as TextMeshProUGUI;
                    if (labelText != null)
                    {
                        Debug.Log($"[SessionSettingsPanelBuilder] Setting label via _settingLabel field to '{label}'");
                        labelText.text = label;
                        return;
                    }
                }
            }
            
            // Fallback: Find the label text - look for specific patterns used in setting prefabs
            var allTexts = row.GetComponentsInChildren<TextMeshProUGUI>(true);
            TextMeshProUGUI foundLabel = null;
            
            // Debug: log all found texts
            Debug.Log($"[SessionSettingsPanelBuilder] SetSettingLabel for '{label}': Found {allTexts.Length} TextMeshProUGUI components (fallback mode)");
            
            // Priority 1: Look for text with "Setting Label" placeholder text
            foreach (var text in allTexts)
            {
                if (text.text == "Setting Label")
                {
                    foundLabel = text;
                    break;
                }
            }
            
            // Priority 2: Look for object named "Label" that doesn't contain numeric text
            if (foundLabel == null)
            {
                foreach (var text in allTexts)
                {
                    if (text.gameObject.name == "Label")
                    {
                        // Skip if it contains numeric text (value display)
                        string cleanText = text.text?.Trim().Replace("\u200b", "") ?? "";
                        if (!string.IsNullOrEmpty(cleanText) && float.TryParse(cleanText, out _))
                            continue;
                        
                        foundLabel = text;
                        break;
                    }
                }
            }
            
            // Priority 3: Fallback to first non-numeric text
            if (foundLabel == null && allTexts.Length > 0)
            {
                foreach (var text in allTexts)
                {
                    var name = text.gameObject.name.ToLower();
                    if (name.Contains("value") || name.Contains("count") || name.Contains("placeholder"))
                        continue;
                        
                    string cleanText = text.text?.Trim().Replace("\u200b", "") ?? "";
                    if (!string.IsNullOrEmpty(cleanText) && float.TryParse(cleanText, out _))
                        continue;
                            
                    foundLabel = text;
                    break;
                }
            }

            if (foundLabel != null)
            {
                Debug.Log($"[SessionSettingsPanelBuilder] Setting label text on '{foundLabel.gameObject.name}' to '{label}'");
                foundLabel.text = label;
            }
            else
            {
                Debug.LogWarning($"[SessionSettingsPanelBuilder] Could not find label text for '{label}'");
            }
        }

        private List<string> CreatePrivacyOptions()
        {
            return new List<string>
            {
                "Public",
                "Private",
                "Unlisted"
            };
        }

        private List<string> CreateSessionTypeOptions()
        {
            // Options ordered to match SessionType enum: Server=0, Lobby=1
            // But we want Lobby as default and first in UI
            return new List<string>
            {
                "Lobby",  // SessionType.Lobby = 1
                "Server"  // SessionType.Server = 0
            };
        }
        
        /// <summary>
        /// Converts UI dropdown index to SessionType enum value.
        /// UI Index 0 = Automatic = SessionType.Lobby (1)
        /// UI Index 1 = Manual = SessionType.Server (0)
        /// </summary>
        private SessionType DropdownIndexToSessionType(int index)
        {
            return index == 0 ? SessionType.Lobby : SessionType.Server;
        }
        
        /// <summary>
        /// Converts SessionType enum value to UI dropdown index.
        /// SessionType.Lobby (1) = UI Index 0 (Automatic)
        /// SessionType.Server (0) = UI Index 1 (Manual)
        /// </summary>
        private int SessionTypeToDropdownIndex(SessionType type)
        {
            return type == SessionType.Lobby ? 0 : 1;
        }

        #endregion

        #region Public API

        /// <summary>
        /// Configures the panel with a mode and configuration.
        /// </summary>
        public void Configure(SettingsPanelMode mode, SettingsPanelConfig config = null)
        {
            // If build is in progress, queue this operation
            if (_isBuildInProgress)
            {
                _pendingMode = mode;
                _pendingConfig = config;
                return;
            }
            
            BuildIfNeeded();

            // If we just triggered a build OR build was deferred (inactive), queue this operation
            if (_isBuildInProgress || !_isBuilt)
            {
                _pendingMode = mode;
                _pendingConfig = config;
                return;
            }

            _mode = mode;
            _config = config ?? (mode == SettingsPanelMode.Create
                ? SettingsPanelConfig.ForLobbyBrowser
                : SettingsPanelConfig.ForLobbyRoom);

            // Suppress callbacks during visibility/interactability changes
            // to prevent spurious events before data is properly applied
            _suppressCallbacks = true;
            try
            {
                ApplyVisibility();
                ApplyInteractability();
            }
            finally
            {
                _suppressCallbacks = false;
            }
            
            // Force layout rebuild to ensure correct display after re-enabling
            RebuildLayout();
        }

        /// <summary>
        /// Sets the settings data to display.
        /// </summary>
        public void SetData(SessionSettingsData data)
        {
            // If build is in progress, queue this operation
            if (_isBuildInProgress)
            {
                _pendingData = data?.Clone() ?? new SessionSettingsData();
                return;
            }
            
            BuildIfNeeded();

            // If we just triggered a build OR build was deferred (inactive), queue this operation
            if (_isBuildInProgress || !_isBuilt)
            {
                _pendingData = data?.Clone() ?? new SessionSettingsData();
                return;
            }

            _currentData = data?.Clone() ?? new SessionSettingsData();
            _dataInitialized = true; // Mark that real data has been set
            ApplyDataToUI();
        }

        /// <summary>
        /// Gets the current settings data from the UI.
        /// </summary>
        public SessionSettingsData GetData()
        {
            Debug.Log($"[SessionSettingsPanelBuilder] GetData: BEFORE ReadDataFromUI - _currentData.SessionType={_currentData.SessionType}, AllowLateJoin={_currentData.AllowLateJoin}");
            ReadDataFromUI();
            Debug.Log($"[SessionSettingsPanelBuilder] GetData: AFTER ReadDataFromUI - _currentData.SessionType={_currentData.SessionType}, AllowLateJoin={_currentData.AllowLateJoin}, _lateJoinToggle.isOn={_lateJoinToggle?.isOn}");
            return _currentData.Clone();
        }

        /// <summary>
        /// Validates the current settings.
        /// </summary>
        public string Validate()
        {
            ReadDataFromUI();

            if (_config.ShowLobbyName && string.IsNullOrWhiteSpace(_currentData.LobbyName))
            {
                return "Lobby name cannot be empty.";
            }

            if (_currentData.MaxPlayers < 2 || _currentData.MaxPlayers > 32)
            {
                return "Max players must be between 2 and 32.";
            }

            if (_currentData.PrivacyMode == LobbyPrivacyMode.Private && string.IsNullOrWhiteSpace(_currentData.Password))
            {
                return "Password is required for private lobbies.";
            }

            if (_currentData.BandSize < 0 || _currentData.BandSize > 8)
            {
                return "Band size must be between 0 and 8.";
            }

            return null;
        }

        /// <summary>
        /// Focuses the first editable input field.
        /// </summary>
        public void FocusFirstField()
        {
            if (_lobbyNameInput != null && _lobbyNameInput.interactable && _lobbyNameRow != null && _lobbyNameRow.activeSelf)
            {
                _lobbyNameInput.Select();
                _lobbyNameInput.ActivateInputField();
            }
        }

        /// <summary>
        /// Scrolls to the top of the settings list.
        /// </summary>
        public void ScrollToTop()
        {
            if (_scrollRect != null)
            {
                _scrollRect.verticalNormalizedPosition = 1f;
            }
        }

        /// <summary>
        /// Expands or collapses all categories.
        /// </summary>
        public void SetAllCategoriesExpanded(bool expanded)
        {
            _lobbyInfoDrawer?.SetDrawerWithoutRebuild(expanded);
            _gameplayDrawer?.SetDrawerWithoutRebuild(expanded);
            _sessionDrawer?.SetDrawerWithoutRebuild(expanded);
            RebuildLayout();
        }

        /// <summary>
        /// Clears all dynamically created UI elements.
        /// </summary>
        public void ClearUI()
        {
            UnregisterListeners();
            _dataInitialized = false; // Reset data initialization flag

            foreach (var obj in _createdObjects)
            {
                if (obj != null)
                {
                    Destroy(obj);
                }
            }
            _createdObjects.Clear();

            // Clear references
            _lobbyInfoDrawer = null;
            _gameplayDrawer = null;
            _sessionDrawer = null;
            _lobbyNameInput = null;
            _maxPlayersSlider = null;
            _privacyDropdown = null;
            _passwordInput = null;
            _bandSizeSlider = null;
            _noFailToggle = null;
            _sharedSongsToggle = null;
            _allowModifiersToggle = null;
            _presetSyncToggle = null;
            _lateJoinToggle = null;
            _localPlayersFirstToggle = null;
            _gameModeButtons.Clear();

            _isBuilt = false;
        }

        #endregion

        #region UI State

        private void ApplyVisibility()
        {
            // Category visibility
            SetDrawerVisible(_lobbyInfoDrawer,
                _config.ShowLobbyName || _config.ShowMaxPlayers || _config.ShowPrivacy || _config.ShowSessionType);

            SetDrawerVisible(_gameplayDrawer,
                _config.ShowBandSize || _config.ShowNoFailMode ||
                _config.ShowSharedSongsOnly || _config.ShowAllowModifiers);

            SetDrawerVisible(_sessionDrawer,
                _config.ShowEnablePresetSync || _config.ShowAllowLateJoin);

            // Ensure drawers are expanded (their content is visible)
            // This fixes issues when returning from another menu where drawer state might be stale
            if (_lobbyInfoDrawer != null && _lobbyInfoDrawer.gameObject.activeSelf)
                _lobbyInfoDrawer.SetDrawerWithoutRebuild(_lobbyInfoExpanded);
            if (_gameplayDrawer != null && _gameplayDrawer.gameObject.activeSelf)
                _gameplayDrawer.SetDrawerWithoutRebuild(_gameplayExpanded);
            if (_sessionDrawer != null && _sessionDrawer.gameObject.activeSelf)
                _sessionDrawer.SetDrawerWithoutRebuild(_sessionExpanded);

            // Individual setting visibility
            SetRowVisible(_lobbyNameRow, _config.ShowLobbyName);
            SetRowVisible(_maxPlayersRow, _config.ShowMaxPlayers);
            SetRowVisible(_privacyRow, _config.ShowPrivacy);
            SetRowVisible(_sessionTypeRow, _config.ShowSessionType);
            SetRowVisible(_bandSizeRow, _config.ShowBandSize);
            SetRowVisible(_noFailRow, _config.ShowNoFailMode);
            SetRowVisible(_sharedSongsRow, _config.ShowSharedSongsOnly);
            SetRowVisible(_allowModifiersRow, _config.ShowAllowModifiers);
            SetRowVisible(_allowedInstrumentsRow, _config.ShowAllowedInstruments);
            SetRowVisible(_localPlayersFirstRow, _config.ShowLocalPlayersFirst);
            SetRowVisible(_presetSyncRow, _config.ShowEnablePresetSync);
            SetRowVisible(_lateJoinRow, _config.ShowAllowLateJoin);

            // Password visibility depends on privacy mode
            UpdatePasswordVisibility();
        }

        private void ApplyInteractability()
        {
            bool isViewOnly = _mode == SettingsPanelMode.ViewOnly;
            bool isHostEdit = _mode == SettingsPanelMode.HostEdit;

            // Lobby info - locked in active sessions (read-only for everyone)
            SetInteractable(_lobbyNameInput, !isViewOnly && !(isHostEdit && _config.LockLobbyNameInSession));
            SetInteractable(_maxPlayersSlider, !isViewOnly && !(isHostEdit && _config.LockMaxPlayersInSession));
            SetInteractable(_privacyDropdown, !isViewOnly && !(isHostEdit && _config.LockPrivacyInSession));
            SetInteractable(_passwordInput, !isViewOnly && !(isHostEdit && _config.LockPasswordInSession));
            
            // Gameplay settings - editable by host
            SetInteractable(_bandSizeSlider, !isViewOnly && !(isHostEdit && _config.LockBandSizeInSession));
            SetInteractable(_noFailToggle, !isViewOnly);
            SetInteractable(_sharedSongsToggle, !isViewOnly);
            SetInteractable(_allowModifiersToggle, !isViewOnly);
            SetInteractable(_localPlayersFirstToggle, !isViewOnly);
            SetInstrumentPickerInteractable(!isViewOnly);
            
            // Session settings
            SetInteractable(_presetSyncToggle, !isViewOnly);
            SetInteractable(_lateJoinToggle, !isViewOnly);
        }
        
        private void SetInstrumentPickerInteractable(bool interactable)
        {
            foreach (var btn in _gameModeButtons)
            {
                if (btn.Button != null)
                {
                    btn.Button.interactable = interactable;
                    
                    // Dim the icons in view-only mode
                    if (!interactable && btn.IconImage != null)
                    {
                        var currentColor = btn.IconImage.color;
                        btn.IconImage.color = new Color(currentColor.r, currentColor.g, currentColor.b, 0.5f);
                    }
                }
            }
        }

        private void ApplyDataToUI()
        {
            _suppressCallbacks = true;

            try
            {
                if (_lobbyNameInput != null)
                    _lobbyNameInput.SetTextWithoutNotify(_currentData.LobbyName ?? string.Empty);

                if (_maxPlayersSlider != null)
                    _maxPlayersSlider.SetValueWithoutNotify(_currentData.MaxPlayers);

                if (_privacyDropdown != null)
                {
                    int index = Mathf.Clamp((int)_currentData.PrivacyMode, 0, 2);
                    _privacyDropdown.SetValueWithoutNotify(index);
                    _privacyDropdown.RefreshShownValue();
                }

                if (_sessionTypeDropdown != null)
                {
                    int index = SessionTypeToDropdownIndex(_currentData.SessionType);
                    _sessionTypeDropdown.SetValueWithoutNotify(index);
                    _sessionTypeDropdown.RefreshShownValue();
                }

                if (_passwordInput != null)
                    _passwordInput.SetTextWithoutNotify(_currentData.Password ?? string.Empty);

                if (_bandSizeSlider != null)
                    _bandSizeSlider.SetValueWithoutNotify(_currentData.BandSize);

                if (_noFailToggle != null)
                    _noFailToggle.SetIsOnWithoutNotify(_currentData.NoFailMode);

                if (_sharedSongsToggle != null)
                    _sharedSongsToggle.SetIsOnWithoutNotify(_currentData.SharedSongsOnly);

                if (_allowModifiersToggle != null)
                    _allowModifiersToggle.SetIsOnWithoutNotify(_currentData.AllowModifiers);

                if (_presetSyncToggle != null)
                    _presetSyncToggle.SetIsOnWithoutNotify(_currentData.EnablePresetSync);

                if (_lateJoinToggle != null)
                    _lateJoinToggle.SetIsOnWithoutNotify(_currentData.AllowLateJoin);

                if (_localPlayersFirstToggle != null)
                    _localPlayersFirstToggle.SetIsOnWithoutNotify(_currentData.LocalPlayersFirst);

                // Update game mode button visuals based on data
                UpdateGameModeButtonVisuals();

                UpdatePasswordVisibility();
            }
            finally
            {
                _suppressCallbacks = false;
            }
        }

        private void ReadDataFromUI()
        {
            if (_lobbyNameInput != null)
                _currentData.LobbyName = _lobbyNameInput.text?.Trim() ?? string.Empty;

            if (_maxPlayersSlider != null)
                _currentData.MaxPlayers = Mathf.RoundToInt(_maxPlayersSlider.Value);

            if (_privacyDropdown != null)
                _currentData.PrivacyMode = (LobbyPrivacyMode)Mathf.Clamp(_privacyDropdown.value, 0, 2);

            if (_sessionTypeDropdown != null)
            {
                Debug.Log($"[SessionSettingsPanelBuilder] ReadDataFromUI: _sessionTypeDropdown.value={_sessionTypeDropdown.value}");
                _currentData.SessionType = DropdownIndexToSessionType(_sessionTypeDropdown.value);
                Debug.Log($"[SessionSettingsPanelBuilder] ReadDataFromUI: converted SessionType={_currentData.SessionType}");
            }

            if (_passwordInput != null)
                _currentData.Password = _passwordInput.text ?? string.Empty;

            if (_bandSizeSlider != null)
                _currentData.BandSize = Mathf.RoundToInt(_bandSizeSlider.Value);

            if (_noFailToggle != null)
                _currentData.NoFailMode = _noFailToggle.isOn;

            if (_sharedSongsToggle != null)
                _currentData.SharedSongsOnly = _sharedSongsToggle.isOn;

            if (_allowModifiersToggle != null)
                _currentData.AllowModifiers = _allowModifiersToggle.isOn;

            if (_presetSyncToggle != null)
                _currentData.EnablePresetSync = _presetSyncToggle.isOn;

            if (_lateJoinToggle != null)
                _currentData.AllowLateJoin = _lateJoinToggle.isOn;

            if (_localPlayersFirstToggle != null)
                _currentData.LocalPlayersFirst = _localPlayersFirstToggle.isOn;

            // AllowedInstruments is already updated via toggle callbacks, no need to read from UI
        }

        private void UpdatePasswordVisibility()
        {
            // Hide password field entirely for clients in view-only mode
            if (_mode == SettingsPanelMode.ViewOnly)
            {
                SetRowVisible(_passwordRow, false);
                return;
            }
            
            // Password visibility rules:
            // - Private: always show password (required)
            // - Unlisted + Server: show password (optional, for direct connect)
            // - Unlisted + Lobby: hide password (lobby code is the secret)
            // - Public: hide password (not allowed)
            bool showPassword = _config.ShowPassword && (
                _currentData.PrivacyMode == LobbyPrivacyMode.Private ||
                (_currentData.PrivacyMode == LobbyPrivacyMode.Unlisted && 
                 _currentData.SessionType == SessionType.Server));
            SetRowVisible(_passwordRow, showPassword);
        }

        #endregion

        #region Event Handlers

        private void RegisterListeners()
        {
            int registered = 0;
            
            if (_lobbyNameInput != null)
            {
                _lobbyNameInput.onEndEdit.AddListener(OnLobbyNameChanged);
                registered++;
            }

            if (_maxPlayersSlider != null)
            {
                _maxPlayersSlider.ValueChanged.AddListener(OnMaxPlayersChanged);
                registered++;
            }

            if (_privacyDropdown != null)
            {
                _privacyDropdown.onValueChanged.AddListener(OnPrivacyChanged);
                registered++;
            }

            if (_sessionTypeDropdown != null)
            {
                _sessionTypeDropdown.onValueChanged.AddListener(OnSessionTypeChanged);
                registered++;
            }

            if (_passwordInput != null)
            {
                _passwordInput.onEndEdit.AddListener(OnPasswordChanged);
                registered++;
            }

            if (_bandSizeSlider != null)
            {
                _bandSizeSlider.ValueChanged.AddListener(OnBandSizeChanged);
                registered++;
            }

            if (_noFailToggle != null)
            {
                _noFailToggle.onValueChanged.AddListener(OnNoFailChanged);
                registered++;
            }

            if (_sharedSongsToggle != null)
            {
                _sharedSongsToggle.onValueChanged.AddListener(OnSharedSongsChanged);
                registered++;
            }

            if (_allowModifiersToggle != null)
            {
                _allowModifiersToggle.onValueChanged.AddListener(OnAllowModifiersChanged);
                registered++;
            }

            if (_presetSyncToggle != null)
            {
                _presetSyncToggle.onValueChanged.AddListener(OnPresetSyncChanged);
                registered++;
            }

            if (_lateJoinToggle != null)
            {
                _lateJoinToggle.onValueChanged.AddListener(OnLateJoinChanged);
                registered++;
            }

            if (_localPlayersFirstToggle != null)
            {
                _localPlayersFirstToggle.onValueChanged.AddListener(OnLocalPlayersFirstChanged);
                registered++;
            }
            
            Debug.Log($"[SessionSettingsPanelBuilder] RegisterListeners: Registered {registered} change listeners");
        }

        private void UnregisterListeners()
        {
            if (_lobbyNameInput != null)
                _lobbyNameInput.onEndEdit.RemoveListener(OnLobbyNameChanged);

            if (_maxPlayersSlider != null)
                _maxPlayersSlider.ValueChanged.RemoveListener(OnMaxPlayersChanged);

            if (_privacyDropdown != null)
                _privacyDropdown.onValueChanged.RemoveListener(OnPrivacyChanged);

            if (_sessionTypeDropdown != null)
                _sessionTypeDropdown.onValueChanged.RemoveListener(OnSessionTypeChanged);

            if (_passwordInput != null)
                _passwordInput.onEndEdit.RemoveListener(OnPasswordChanged);

            if (_bandSizeSlider != null)
                _bandSizeSlider.ValueChanged.RemoveListener(OnBandSizeChanged);

            if (_noFailToggle != null)
                _noFailToggle.onValueChanged.RemoveListener(OnNoFailChanged);

            if (_sharedSongsToggle != null)
                _sharedSongsToggle.onValueChanged.RemoveListener(OnSharedSongsChanged);

            if (_allowModifiersToggle != null)
                _allowModifiersToggle.onValueChanged.RemoveListener(OnAllowModifiersChanged);

            if (_presetSyncToggle != null)
                _presetSyncToggle.onValueChanged.RemoveListener(OnPresetSyncChanged);

            if (_lateJoinToggle != null)
                _lateJoinToggle.onValueChanged.RemoveListener(OnLateJoinChanged);

            if (_localPlayersFirstToggle != null)
                _localPlayersFirstToggle.onValueChanged.RemoveListener(OnLocalPlayersFirstChanged);
        }

        private void OnLobbyNameChanged(string value)
        {
            if (_suppressCallbacks || _mode == SettingsPanelMode.ViewOnly) return;
            _currentData.LobbyName = value?.Trim() ?? string.Empty;
            NotifySettingsChanged();
        }

        private void OnMaxPlayersChanged(float value)
        {
            Debug.Log($"[SessionSettingsPanelBuilder] OnMaxPlayersChanged: {value}, _suppressCallbacks={_suppressCallbacks}");
            if (_suppressCallbacks || _mode == SettingsPanelMode.ViewOnly) return;
            _currentData.MaxPlayers = Mathf.RoundToInt(value);
            NotifySettingsChanged();
        }

        private void OnPrivacyChanged(int index)
        {
            if (_suppressCallbacks || _mode == SettingsPanelMode.ViewOnly) return;
            _currentData.PrivacyMode = (LobbyPrivacyMode)Mathf.Clamp(index, 0, 2);
            UpdatePasswordVisibility();
            NotifySettingsChanged();
        }

        private void OnSessionTypeChanged(int index)
        {
            Debug.Log($"[SessionSettingsPanelBuilder] OnSessionTypeChanged: index={index}, _suppressCallbacks={_suppressCallbacks}, _mode={_mode}");
            if (_suppressCallbacks || _mode == SettingsPanelMode.ViewOnly) return;
            _currentData.SessionType = DropdownIndexToSessionType(index);
            Debug.Log($"[SessionSettingsPanelBuilder] OnSessionTypeChanged: new SessionType={_currentData.SessionType}");
            // Session type affects password visibility when privacy is Unlisted
            UpdatePasswordVisibility();
            NotifySettingsChanged();
        }

        private void OnPasswordChanged(string value)
        {
            if (_suppressCallbacks || _mode == SettingsPanelMode.ViewOnly) return;
            _currentData.Password = value ?? string.Empty;
            NotifySettingsChanged();
        }

        private void OnBandSizeChanged(float value)
        {
            if (_suppressCallbacks || _mode == SettingsPanelMode.ViewOnly) return;
            _currentData.BandSize = Mathf.RoundToInt(value);
            NotifySettingsChanged();
        }

        private void OnNoFailChanged(bool value)
        {
            Debug.Log($"[SessionSettingsPanelBuilder] OnNoFailChanged: {value}, _suppressCallbacks={_suppressCallbacks}");
            if (_suppressCallbacks || _mode == SettingsPanelMode.ViewOnly) return;
            _currentData.NoFailMode = value;
            NotifySettingsChanged();
        }

        private void OnSharedSongsChanged(bool value)
        {
            if (_suppressCallbacks || _mode == SettingsPanelMode.ViewOnly) return;
            _currentData.SharedSongsOnly = value;
            NotifySettingsChanged();
        }

        private void OnAllowModifiersChanged(bool value)
        {
            if (_suppressCallbacks || _mode == SettingsPanelMode.ViewOnly) return;
            _currentData.AllowModifiers = value;
            NotifySettingsChanged();
        }

        private void OnPresetSyncChanged(bool value)
        {
            if (_suppressCallbacks || _mode == SettingsPanelMode.ViewOnly) return;
            _currentData.EnablePresetSync = value;
            NotifySettingsChanged();
        }

        private void OnLateJoinChanged(bool value)
        {
            Debug.Log($"[SessionSettingsPanelBuilder] OnLateJoinChanged: value={value}, _suppressCallbacks={_suppressCallbacks}, _mode={_mode}");
            if (_suppressCallbacks || _mode == SettingsPanelMode.ViewOnly) return;
            _currentData.AllowLateJoin = value;
            NotifySettingsChanged();
        }

        private void OnLocalPlayersFirstChanged(bool value)
        {
            if (_suppressCallbacks || _mode == SettingsPanelMode.ViewOnly) return;
            _currentData.LocalPlayersFirst = value;
            NotifySettingsChanged();
        }

        private void NotifySettingsChanged()
        {
            // Don't notify until SetData has been called with real data
            // This prevents spurious events during panel initialization where
            // _currentData still has default values (e.g., SessionType=Lobby)
            if (!_dataInitialized)
            {
                Debug.Log($"[SessionSettingsPanelBuilder] NotifySettingsChanged SKIPPED - data not yet initialized. AllowLateJoin={_currentData.AllowLateJoin}, SessionType={_currentData.SessionType}");
                return;
            }
            
            Debug.Log($"[SessionSettingsPanelBuilder] NotifySettingsChanged called. HasListeners={OnSettingsChanged != null}, AllowLateJoin={_currentData.AllowLateJoin}, SessionType={_currentData.SessionType}");
            // Log stack trace to identify the source of the callback
            Debug.Log($"[SessionSettingsPanelBuilder] NotifySettingsChanged stack trace:\n{System.Environment.StackTrace}");
            OnSettingsChanged?.Invoke(_currentData.Clone());
        }

        #endregion

        #region Helpers

        private void SetDrawerVisible(DropdownDrawer drawer, bool visible)
        {
            if (drawer != null)
            {
                drawer.gameObject.SetActive(visible);
            }
        }

        private void SetRowVisible(GameObject row, bool visible)
        {
            if (row != null)
            {
                row.SetActive(visible);
            }
        }

        private void SetInteractable(Toggle toggle, bool interactable)
        {
            if (toggle != null)
            {
                toggle.interactable = interactable;
                
                // Also disable any Button in the parent hierarchy that was added to expand the click area
                // This Button directly sets toggle.isOn and bypasses interactability
                var rowButton = toggle.GetComponentInParent<Button>();
                if (rowButton != null)
                {
                    rowButton.interactable = interactable;
                }
                
                // Apply visual dimming for read-only state on the row
                var row = toggle.transform.parent?.gameObject ?? toggle.gameObject;
                var canvasGroup = row.GetComponent<CanvasGroup>();
                if (canvasGroup == null)
                {
                    canvasGroup = row.AddComponent<CanvasGroup>();
                }
                canvasGroup.alpha = interactable ? 1f : 0.5f;
            }
        }

        private void SetInteractable(Selectable selectable, bool interactable)
        {
            if (selectable != null)
            {
                selectable.interactable = interactable;
                
                // Apply visual dimming for read-only state
                var canvasGroup = selectable.GetComponent<CanvasGroup>();
                if (canvasGroup == null)
                {
                    canvasGroup = selectable.gameObject.AddComponent<CanvasGroup>();
                }
                canvasGroup.alpha = interactable ? 1f : 0.5f;
            }
        }

        private void SetInteractable(TMP_InputField input, bool interactable)
        {
            if (input != null)
            {
                input.interactable = interactable;
                
                // Apply visual dimming for read-only state
                var canvasGroup = input.GetComponent<CanvasGroup>();
                if (canvasGroup == null)
                {
                    canvasGroup = input.gameObject.AddComponent<CanvasGroup>();
                }
                canvasGroup.alpha = interactable ? 1f : 0.5f;
            }
        }

        private void SetInteractable(TMP_Dropdown dropdown, bool interactable)
        {
            if (dropdown != null)
            {
                dropdown.interactable = interactable;
                
                // Apply visual dimming for read-only state
                var canvasGroup = dropdown.GetComponent<CanvasGroup>();
                if (canvasGroup == null)
                {
                    canvasGroup = dropdown.gameObject.AddComponent<CanvasGroup>();
                }
                canvasGroup.alpha = interactable ? 1f : 0.5f;
            }
        }

        private void SetInteractable(ValueSlider valueSlider, bool interactable)
        {
            if (valueSlider != null)
            {
                // Set interactable on the underlying slider
                var slider = valueSlider.GetComponentInChildren<Slider>();
                if (slider != null)
                {
                    slider.interactable = interactable;
                }
                
                // Also set the input field interactability
                var inputField = valueSlider.GetComponentInChildren<TMP_InputField>();
                if (inputField != null)
                {
                    inputField.interactable = interactable;
                }
                
                // Apply visual dimming for read-only state on the whole ValueSlider
                var canvasGroup = valueSlider.GetComponent<CanvasGroup>();
                if (canvasGroup == null)
                {
                    canvasGroup = valueSlider.gameObject.AddComponent<CanvasGroup>();
                }
                canvasGroup.alpha = interactable ? 1f : 0.5f;
            }
        }

        private void RebuildLayout()
        {
            if (_contentContainer != null)
            {
                _contentContainer.ForceUpdateRectTransforms();
                LayoutRebuilder.ForceRebuildLayoutImmediate(_contentContainer);
            }
        }

        #endregion
    }

    /// <summary>
    /// Helper class to track game mode toggle button state.
    /// </summary>
    public class GameModeToggleButton
    {
        public GameMode GameMode { get; set; }
        public Button Button { get; set; }
        public Image IconImage { get; set; }
        public bool IsEnabled { get; set; }
    }
}
