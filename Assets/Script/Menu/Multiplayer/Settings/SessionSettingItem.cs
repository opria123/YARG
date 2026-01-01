using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace YARG.Menu.Multiplayer.Settings
{
    /// <summary>
    /// Types of session settings for UI display.
    /// </summary>
    public enum SessionSettingType
    {
        Text,
        Integer,
        Toggle,
        Dropdown
    }

    /// <summary>
    /// A single setting item in the session settings panel.
    /// This wraps around the existing setting visual prefabs.
    /// </summary>
    public class SessionSettingItem : MonoBehaviour
    {
        [Header("Common")]
        [SerializeField] private TextMeshProUGUI _label;
        [SerializeField] private GameObject _evenBackground;
        [SerializeField] private GameObject _selectedBackground;

        [Header("Input Controls - Only one should be active")]
        [SerializeField] private TMP_InputField _inputField;
        [SerializeField] private Toggle _toggle;
        [SerializeField] private TMP_Dropdown _dropdown;

        private string _settingKey;
        private SessionSettingType _settingType;
        private bool _suppressCallbacks;

        /// <summary>
        /// Gets the unique key for this setting.
        /// </summary>
        public string SettingKey => _settingKey;

        /// <summary>
        /// Gets the setting type.
        /// </summary>
        public SessionSettingType SettingType => _settingType;

        /// <summary>
        /// Event fired when the value changes.
        /// </summary>
        public event Action<SessionSettingItem, object> OnValueChanged;

        private void Awake()
        {
            // Register listeners
            if (_inputField != null)
            {
                _inputField.onEndEdit.AddListener(OnInputFieldChanged);
            }

            if (_toggle != null)
            {
                _toggle.onValueChanged.AddListener(OnToggleChanged);
            }

            if (_dropdown != null)
            {
                _dropdown.onValueChanged.AddListener(OnDropdownChanged);
            }
        }

        private void OnDestroy()
        {
            // Unregister listeners
            if (_inputField != null)
            {
                _inputField.onEndEdit.RemoveListener(OnInputFieldChanged);
            }

            if (_toggle != null)
            {
                _toggle.onValueChanged.RemoveListener(OnToggleChanged);
            }

            if (_dropdown != null)
            {
                _dropdown.onValueChanged.RemoveListener(OnDropdownChanged);
            }
        }

        /// <summary>
        /// Initializes the setting item.
        /// </summary>
        public void Initialize(string key, string labelText, SessionSettingType type)
        {
            _settingKey = key;
            _settingType = type;

            if (_label != null)
            {
                _label.text = labelText;
            }

            // Show/hide appropriate input based on type
            UpdateInputVisibility();
        }

        private void UpdateInputVisibility()
        {
            // Hide all inputs by default
            if (_inputField != null)
            {
                _inputField.gameObject.SetActive(
                    _settingType == SessionSettingType.Text ||
                    _settingType == SessionSettingType.Integer);
            }

            if (_toggle != null)
            {
                _toggle.gameObject.SetActive(_settingType == SessionSettingType.Toggle);
            }

            if (_dropdown != null)
            {
                _dropdown.gameObject.SetActive(_settingType == SessionSettingType.Dropdown);
            }
        }

        /// <summary>
        /// Sets the even/odd background state.
        /// </summary>
        public void SetEvenBackground(bool isEven)
        {
            if (_evenBackground != null)
            {
                _evenBackground.SetActive(isEven);
            }
        }

        /// <summary>
        /// Sets the selected visual state.
        /// </summary>
        public void SetSelected(bool selected)
        {
            if (_selectedBackground != null)
            {
                _selectedBackground.SetActive(selected);
            }
        }

        /// <summary>
        /// Sets whether the setting is interactable.
        /// </summary>
        public void SetInteractable(bool interactable)
        {
            if (_inputField != null)
            {
                _inputField.interactable = interactable;
            }

            if (_toggle != null)
            {
                _toggle.interactable = interactable;
            }

            if (_dropdown != null)
            {
                _dropdown.interactable = interactable;
            }
        }

        #region Value Getters/Setters

        /// <summary>
        /// Gets the current value.
        /// </summary>
        public object GetValue()
        {
            return _settingType switch
            {
                SessionSettingType.Text => _inputField?.text ?? string.Empty,
                SessionSettingType.Integer => int.TryParse(_inputField?.text, out int val) ? val : 0,
                SessionSettingType.Toggle => _toggle?.isOn ?? false,
                SessionSettingType.Dropdown => _dropdown?.value ?? 0,
                _ => null
            };
        }

        /// <summary>
        /// Sets the current value without triggering callbacks.
        /// </summary>
        public void SetValueWithoutNotify(object value)
        {
            _suppressCallbacks = true;
            try
            {
                SetValueInternal(value);
            }
            finally
            {
                _suppressCallbacks = false;
            }
        }

        /// <summary>
        /// Sets the current value (may trigger callbacks).
        /// </summary>
        public void SetValue(object value)
        {
            SetValueInternal(value);
        }

        private void SetValueInternal(object value)
        {
            switch (_settingType)
            {
                case SessionSettingType.Text:
                    if (_inputField != null)
                    {
                        _inputField.SetTextWithoutNotify(value?.ToString() ?? string.Empty);
                    }
                    break;

                case SessionSettingType.Integer:
                    if (_inputField != null)
                    {
                        int intVal = Convert.ToInt32(value);
                        _inputField.SetTextWithoutNotify(intVal.ToString());
                    }
                    break;

                case SessionSettingType.Toggle:
                    if (_toggle != null)
                    {
                        bool boolVal = Convert.ToBoolean(value);
                        _toggle.SetIsOnWithoutNotify(boolVal);
                    }
                    break;

                case SessionSettingType.Dropdown:
                    if (_dropdown != null)
                    {
                        int dropdownVal = Convert.ToInt32(value);
                        dropdownVal = Mathf.Clamp(dropdownVal, 0, _dropdown.options.Count - 1);
                        _dropdown.SetValueWithoutNotify(dropdownVal);
                        _dropdown.RefreshShownValue();
                    }
                    break;
            }
        }

        /// <summary>
        /// Gets the string value.
        /// </summary>
        public string GetStringValue()
        {
            return _inputField?.text ?? string.Empty;
        }

        /// <summary>
        /// Gets the integer value.
        /// </summary>
        public int GetIntValue()
        {
            if (_settingType == SessionSettingType.Dropdown)
            {
                return _dropdown?.value ?? 0;
            }

            if (int.TryParse(_inputField?.text, out int val))
            {
                return val;
            }
            return 0;
        }

        /// <summary>
        /// Gets the boolean value.
        /// </summary>
        public bool GetBoolValue()
        {
            return _toggle?.isOn ?? false;
        }

        #endregion

        #region Dropdown Options

        /// <summary>
        /// Sets the options for a dropdown setting.
        /// </summary>
        public void SetDropdownOptions(params string[] options)
        {
            if (_dropdown == null) return;

            _dropdown.ClearOptions();
            _dropdown.AddOptions(new System.Collections.Generic.List<string>(options));
        }

        /// <summary>
        /// Sets the options for a dropdown setting.
        /// </summary>
        public void SetDropdownOptions(System.Collections.Generic.List<string> options)
        {
            if (_dropdown == null) return;

            _dropdown.ClearOptions();
            _dropdown.AddOptions(options);
        }

        #endregion

        #region Input Configuration

        /// <summary>
        /// Configures the input field for text input.
        /// </summary>
        public void ConfigureTextInput(string placeholder = "", int characterLimit = 0)
        {
            if (_inputField == null) return;

            _inputField.contentType = TMP_InputField.ContentType.Standard;
            _inputField.characterLimit = characterLimit;

            if (_inputField.placeholder is TextMeshProUGUI placeholderText)
            {
                placeholderText.text = placeholder;
            }
        }

        /// <summary>
        /// Configures the input field for integer input.
        /// </summary>
        public void ConfigureIntegerInput(int min = int.MinValue, int max = int.MaxValue, string placeholder = "")
        {
            if (_inputField == null) return;

            _inputField.contentType = TMP_InputField.ContentType.IntegerNumber;

            if (_inputField.placeholder is TextMeshProUGUI placeholderText)
            {
                placeholderText.text = placeholder;
            }

            // We can validate min/max on value change if needed
        }

        /// <summary>
        /// Configures the input field for password input.
        /// </summary>
        public void ConfigurePasswordInput(string placeholder = "")
        {
            if (_inputField == null) return;

            _inputField.contentType = TMP_InputField.ContentType.Password;

            if (_inputField.placeholder is TextMeshProUGUI placeholderText)
            {
                placeholderText.text = placeholder;
            }
        }

        #endregion

        #region Event Handlers

        private void OnInputFieldChanged(string value)
        {
            if (_suppressCallbacks) return;

            object convertedValue = _settingType == SessionSettingType.Integer
                ? (int.TryParse(value, out int intVal) ? intVal : 0)
                : value;

            OnValueChanged?.Invoke(this, convertedValue);
        }

        private void OnToggleChanged(bool value)
        {
            if (_suppressCallbacks) return;
            OnValueChanged?.Invoke(this, value);
        }

        private void OnDropdownChanged(int index)
        {
            if (_suppressCallbacks) return;
            OnValueChanged?.Invoke(this, index);
        }

        #endregion

        #region Focus

        /// <summary>
        /// Focuses this setting's input.
        /// </summary>
        public void Focus()
        {
            if (_inputField != null && _inputField.gameObject.activeSelf)
            {
                _inputField.Select();
                _inputField.ActivateInputField();
            }
            else if (_toggle != null && _toggle.gameObject.activeSelf)
            {
                _toggle.Select();
            }
            else if (_dropdown != null && _dropdown.gameObject.activeSelf)
            {
                _dropdown.Select();
            }
        }

        #endregion
    }
}
