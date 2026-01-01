using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using YARG.Helpers.Extensions;
using YARG.Menu.Navigation;

namespace YARG.Menu.Multiplayer.Settings
{
    /// <summary>
    /// A collapsible category drawer for session settings.
    /// Uses the same visual style as the settings menu.
    /// </summary>
    public class SessionSettingsCategoryDrawer : MonoBehaviour
    {
        [Header("Header")]
        [SerializeField] private Button _headerButton;
        [SerializeField] private TextMeshProUGUI _categoryLabel;
        [SerializeField] private RectTransform _arrowIcon;
        [SerializeField] private GameObject _headerBackground;

        [Header("Content")]
        [SerializeField] private GameObject _contentContainer;
        [SerializeField] private VerticalLayoutGroup _contentLayout;

        [Header("Settings")]
        [SerializeField] private bool _startExpanded = true;

        private bool _isExpanded;
        private readonly List<SessionSettingItem> _settingItems = new();
        private NavigationGroup _navGroup;

        /// <summary>
        /// Gets or sets whether the category is expanded.
        /// </summary>
        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetExpanded(value, rebuild: true);
        }

        /// <summary>
        /// Gets the setting items in this category.
        /// </summary>
        public IReadOnlyList<SessionSettingItem> SettingItems => _settingItems;

        /// <summary>
        /// Event fired when expansion state changes.
        /// </summary>
        public event Action<bool> OnExpansionChanged;

        private void Awake()
        {
            if (_headerButton != null)
            {
                _headerButton.onClick.AddListener(ToggleExpanded);
            }
        }

        private void Start()
        {
            SetExpanded(_startExpanded, rebuild: false);
        }

        private void OnDestroy()
        {
            if (_headerButton != null)
            {
                _headerButton.onClick.RemoveListener(ToggleExpanded);
            }
        }

        /// <summary>
        /// Initializes the category with a label and optional navigation group.
        /// </summary>
        public void Initialize(string categoryName, NavigationGroup navGroup = null)
        {
            if (_categoryLabel != null)
            {
                _categoryLabel.text = categoryName;
            }

            _navGroup = navGroup;
        }

        /// <summary>
        /// Adds a setting item to this category.
        /// </summary>
        public void AddSettingItem(SessionSettingItem item)
        {
            if (item == null) return;

            item.transform.SetParent(_contentContainer.transform, false);
            _settingItems.Add(item);

            // Assign alternating background
            item.SetEvenBackground(_settingItems.Count % 2 == 0);

            // Add to navigation group if available
            if (_navGroup != null)
            {
                var navigatable = item.GetComponentInChildren<NavigatableBehaviour>();
                if (navigatable != null)
                {
                    _navGroup.AddNavigatable(navigatable);
                }
            }
        }

        /// <summary>
        /// Clears all setting items from this category.
        /// </summary>
        public void ClearSettingItems()
        {
            foreach (var item in _settingItems)
            {
                if (item != null && item.gameObject != null)
                {
                    // Remove from nav group if available
                    if (_navGroup != null)
                    {
                        var navigatable = item.GetComponentInChildren<NavigatableBehaviour>();
                        if (navigatable != null)
                        {
                            _navGroup.RemoveNavigatable(navigatable);
                        }
                    }

                    Destroy(item.gameObject);
                }
            }

            _settingItems.Clear();
        }

        /// <summary>
        /// Toggles the expanded state.
        /// </summary>
        public void ToggleExpanded()
        {
            SetExpanded(!_isExpanded, rebuild: true);
        }

        /// <summary>
        /// Sets the expanded state.
        /// </summary>
        public void SetExpanded(bool expanded, bool rebuild = true)
        {
            _isExpanded = expanded;

            // Update content visibility
            if (_contentContainer != null)
            {
                _contentContainer.SetActive(_isExpanded);
            }

            // Update arrow rotation
            if (_arrowIcon != null)
            {
                _arrowIcon.localEulerAngles = _isExpanded
                    ? new Vector3(0, 0, 0)    // Points down when expanded
                    : new Vector3(0, 0, 90);  // Points right when collapsed
            }

            // Rebuild layout if needed
            if (rebuild)
            {
                RebuildLayout();
            }

            OnExpansionChanged?.Invoke(_isExpanded);
        }

        /// <summary>
        /// Rebuilds the layout to account for size changes.
        /// </summary>
        public void RebuildLayout()
        {
            if (transform is RectTransform rect)
            {
                rect.ForceUpdateRectTransforms();
                LayoutRebuilder.MarkLayoutForRebuild(rect);
            }

            // Also rebuild parent if available
            var parentRect = transform.parent as RectTransform;
            if (parentRect != null)
            {
                LayoutRebuilder.MarkLayoutForRebuild(parentRect);
            }
        }

        /// <summary>
        /// Gets a setting item by its key.
        /// </summary>
        public SessionSettingItem GetSettingItem(string key)
        {
            foreach (var item in _settingItems)
            {
                if (item.SettingKey == key)
                    return item;
            }
            return null;
        }

        /// <summary>
        /// Enables or disables all setting items in this category.
        /// </summary>
        public void SetAllInteractable(bool interactable)
        {
            foreach (var item in _settingItems)
            {
                item.SetInteractable(interactable);
            }
        }
    }
}
