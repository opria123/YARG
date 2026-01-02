using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using YARG.Multiplayer;
using YARG.Networking.Settings;
using YARG.Networking.Tracks;

namespace YARG.Networking.Gameplay
{
    /// <summary>
    /// Manages gameplay settings for multiplayer sessions.
    /// Applies session preset settings like no-fail mode, allowed instruments, etc.
    /// </summary>
    public sealed class MultiplayerGameplaySettings : MonoBehaviour
    {
        public static MultiplayerGameplaySettings Instance { get; private set; }

        private SessionPreset? _activePreset;
        private HashSet<int> _allowedInstruments = new();
        private bool _settingsApplied;

        /// <summary>
        /// Gets the active session preset.
        /// </summary>
        public SessionPreset? ActivePreset => _activePreset;

        /// <summary>
        /// Gets whether no-fail mode is enforced.
        /// </summary>
        public bool NoFailMode => _activePreset?.NoFailMode ?? false;

        /// <summary>
        /// Gets whether only shared songs can be played.
        /// </summary>
        public bool SharedSongsOnly => _activePreset?.SharedSongsOnly ?? false;

        /// <summary>
        /// Gets whether any modifier settings are enforced.
        /// </summary>
        public bool HasEnforcedModifiers => _activePreset?.AllowModifiers == false;

        /// <summary>
        /// Gets whether modifiers are allowed.
        /// </summary>
        public bool AllowModifiers => _activePreset?.AllowModifiers ?? true;

        /// <summary>
        /// Gets whether local players should be displayed first in track order.
        /// </summary>
        public bool LocalPlayersFirst => _activePreset?.LocalPlayersFirst ?? false;

        /// <summary>
        /// Gets whether preset syncing is enabled (sync camera/highway/color presets for remote players).
        /// </summary>
        public bool EnablePresetSync => _activePreset?.enablePresetSync ?? true;

        /// <summary>
        /// Gets whether score submission is blocked (due to enforced settings).
        /// </summary>
        public bool BlockScoreSubmission => _activePreset?.NoFailMode == true;

        /// <summary>
        /// Fired when gameplay settings are applied.
        /// </summary>
        public event Action? OnSettingsApplied;

        /// <summary>
        /// Fired when an instrument selection is rejected.
        /// </summary>
        public event Action<int, string>? OnInstrumentRejected;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        /// <summary>
        /// Applies session preset settings for gameplay.
        /// </summary>
        public void ApplyPreset(SessionPreset preset)
        {
            _activePreset = preset;
            _allowedInstruments.Clear();

            if (preset.AllowedInstruments != null && preset.AllowedInstruments.Length > 0)
            {
                _allowedInstruments = new HashSet<int>(preset.AllowedInstruments);
            }

            // Update the song filter setting based on SharedSongsOnly
            // When SharedSongsOnly is ON: require songs to have parts for all profiles
            // When SharedSongsOnly is OFF: only filter to songs all users have
            MultiplayerSongFilter.RequirePartsForAllProfiles = preset.SharedSongsOnly;
            
            // Update TrackOrderManager with LocalPlayersFirst setting
            var trackOrderManager = TrackOrderManager.Instance;
            if (trackOrderManager != null)
            {
                trackOrderManager.LocalPlayersFirst = preset.LocalPlayersFirst;
                Debug.Log($"[GameplaySettings] Set TrackOrderManager.LocalPlayersFirst={preset.LocalPlayersFirst}");
            }

            _settingsApplied = true;
            Debug.Log($"[GameplaySettings] Applied preset: NoFail={preset.NoFailMode}, " +
                $"SharedSongsOnly={preset.SharedSongsOnly}, AllowMods={preset.AllowModifiers}, LocalPlayersFirst={preset.LocalPlayersFirst}");

            OnSettingsApplied?.Invoke();
        }

        /// <summary>
        /// Clears the active preset.
        /// </summary>
        public void ClearPreset()
        {
            _activePreset = null;
            _allowedInstruments.Clear();
            _settingsApplied = false;
            
            // Reset the song filter to default (require parts)
            MultiplayerSongFilter.RequirePartsForAllProfiles = true;
        }

        /// <summary>
        /// Checks if an instrument is allowed for this session.
        /// </summary>
        /// <param name="instrumentId">The instrument ID to check.</param>
        /// <returns>True if allowed, false if restricted.</returns>
        public bool IsInstrumentAllowed(int instrumentId)
        {
            // If no restrictions, allow all
            if (_allowedInstruments.Count == 0)
                return true;

            return _allowedInstruments.Contains(instrumentId);
        }

        /// <summary>
        /// Validates and potentially rejects an instrument selection.
        /// </summary>
        /// <param name="instrumentId">The instrument ID to validate.</param>
        /// <returns>True if valid, false if rejected.</returns>
        public bool ValidateInstrumentSelection(int instrumentId)
        {
            if (IsInstrumentAllowed(instrumentId))
                return true;

            OnInstrumentRejected?.Invoke(instrumentId, "This instrument is not allowed in this session.");
            return false;
        }

        /// <summary>
        /// Gets all allowed instrument IDs, or empty if all are allowed.
        /// </summary>
        public IReadOnlyCollection<int> GetAllowedInstruments()
        {
            return _allowedInstruments;
        }

        /// <summary>
        /// Applies gameplay modifiers based on session settings.
        /// Call this when setting up gameplay to enforce restrictions.
        /// </summary>
        /// <param name="currentNoFail">Current no-fail setting from player.</param>
        /// <returns>The effective no-fail setting to use.</returns>
        public bool GetEffectiveNoFailMode(bool currentNoFail)
        {
            // If session enforces no-fail, override player setting
            if (_activePreset?.NoFailMode == true)
                return true;

            return currentNoFail;
        }

        /// <summary>
        /// Checks if a modifier can be applied based on session settings.
        /// </summary>
        /// <param name="modifierName">Name of the modifier.</param>
        /// <returns>True if the modifier can be applied.</returns>
        public bool CanApplyModifier(string modifierName)
        {
            if (_activePreset?.AllowModifiers == false)
            {
                Debug.LogWarning($"[GameplaySettings] Modifier '{modifierName}' blocked - modifiers not allowed in this session");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Gets a message describing the active restrictions for display.
        /// </summary>
        public string GetRestrictionsMessage()
        {
            if (_activePreset == null)
                return string.Empty;

            var restrictions = new List<string>();

            if (_activePreset.NoFailMode)
                restrictions.Add("No-Fail Mode (scores not submitted)");

            if (_activePreset.SharedSongsOnly)
                restrictions.Add("Songs Support All Instruments");

            if (!_activePreset.AllowModifiers)
                restrictions.Add("Modifiers Disabled");

            if (_allowedInstruments.Count > 0)
                restrictions.Add($"Instrument Restrictions ({_allowedInstruments.Count} allowed)");

            if (restrictions.Count == 0)
                return string.Empty;

            return "Session Restrictions:\n• " + string.Join("\n• ", restrictions);
        }
    }
}
