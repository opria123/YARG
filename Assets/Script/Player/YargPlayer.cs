using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;
using YARG.Core;
using YARG.Core.Engine;
using YARG.Core.Game;
using YARG.Core.Input;
using YARG.Core.Replays;
using YARG.Core.Utility;
using YARG.Helpers;
using YARG.Input;
using YARG.Settings.Customization;
using YARG.Themes;

namespace YARG.Player
{
    public class YargPlayer : IDisposable
    {
        public event MenuInputEvent MenuInput;

        public YargProfile Profile { get; private set; }

        /// <summary>
        /// Whether or not the player is sitting out. This is not needed in <see cref="Profile"/> as
        /// players that are sitting out are not included in replays.
        /// </summary>
        public bool SittingOut;

        public bool InputsEnabled { get; private set; }
        public ProfileBindings Bindings { get; private set; }

        public EnginePreset    EnginePreset    { get; private set; }
        public ThemePreset     ThemePreset     { get; private set; }
        public ColorProfile    ColorProfile    { get; private set; }
        public CameraPreset    CameraPreset    { get; private set; }
        public HighwayPreset   HighwayPreset   { get; private set; }
        public RockMeterPreset RockMeterPreset { get; private set; }

        /// <summary>
        /// Whether or not the score is valid.
        /// </summary>
        /// <remarks>Could be invalidated due abusing pauses or no fail mode.</remarks>
        public bool IsScoreValid { get; set; } = true;
        
        /// <summary>
        /// Whether or not synced presets have been applied to this player.
        /// Used to prevent RefreshPresets() from overwriting network-synced presets.
        /// </summary>
        public bool HasSyncedPresets { get; private set; }

        public bool IsReplay { get; private set; }
        public int ReplayIndex = -1;

        /// <summary>
        /// Overrides the engine parameters in the gameplay player.
        /// This is only used when loading replays.
        /// </summary>
        public BaseEngineParameters EngineParameterOverride { get; set; }

        public bool IsMissingMicrophone => !IsReplay && Bindings != null && Profile.GameMode == GameMode.Vocals && Bindings.Microphone == null && !Profile.IsBot;
        public bool IsMissingInputDevice => !IsReplay && Bindings != null && Profile.GameMode != GameMode.Vocals && !Bindings.HasDeviceAssigned && !Profile.IsBot;

        public YargPlayer(YargProfile profile, ProfileBindings bindings)
        {
            Profile = profile;
            Bindings = bindings;
            IsReplay = false;
        }

        public YargPlayer(ReplayFrame frame, ReplayData replay)
        {
            Profile = frame.Profile;
            Bindings = null;
            EngineParameterOverride = frame.EngineParameters;
            IsReplay = true;

            EnginePreset = CustomContentManager.EnginePresets.GetPresetById(Profile.EnginePreset)
                ?? EnginePreset.Default;
            ThemePreset = CustomContentManager.ThemePresets.GetPresetById(Profile.ThemePreset)
                ?? ThemePreset.Default;
            ColorProfile = replay.GetColorProfile(Profile.ColorProfile)
                ?? CustomContentManager.ColorProfiles.GetPresetById(Profile.ColorProfile)
                ?? ColorProfile.Default;
            CameraPreset = replay.GetCameraPreset(Profile.CameraPreset)
                ?? CustomContentManager.CameraSettings.GetPresetById(Profile.CameraPreset)
                ?? CameraPreset.Default;

            HighwayPreset = CustomContentManager.HighwayPresets.GetPresetById(Profile.HighwayPreset)
                ?? HighwayPreset.Default;

            RockMeterPreset = CustomContentManager.RockMeterPresets.GetPresetById(Profile.RockMeterPreset) ??
                YARG.Core.Game.RockMeterPreset.Normal;
        }

        public void SwapToProfile(YargProfile profile, ProfileBindings bindings, bool resolveDevices)
        {
            // Force-disable inputs
            bool enabled = InputsEnabled;
            DisableInputs();

            // Swap to the new profile
            Bindings?.Dispose();
            Profile = profile;
            Bindings = bindings;

            // Resolve bindings
            if (resolveDevices)
            {
                Bindings?.ResolveDevices();
            }

            // Re-enable inputs
            if (enabled)
            {
                EnableInputs();
            }
        }

        public void RefreshPresets()
        {
            EnginePreset = CustomContentManager.EnginePresets.GetPresetById(Profile.EnginePreset)
                ?? EnginePreset.Default;
            Profile.EnginePreset = EnginePreset.Id;
            ThemePreset = CustomContentManager.ThemePresets.GetPresetById(Profile.ThemePreset)
                ?? ThemePreset.Default;
            Profile.ThemePreset = ThemePreset.Id;
            ColorProfile = CustomContentManager.ColorProfiles.GetPresetById(Profile.ColorProfile)
                ?? ColorProfile.Default;
            Profile.ColorProfile = ColorProfile.Id;
            CameraPreset = CustomContentManager.CameraSettings.GetPresetById(Profile.CameraPreset)
                ?? CameraPreset.Default;
            Profile.CameraPreset = CameraPreset.Id;
            HighwayPreset = CustomContentManager.HighwayPresets.GetPresetById(Profile.HighwayPreset)
                ?? HighwayPreset.Default;
            Profile.HighwayPreset = HighwayPreset.Id;
            RockMeterPreset = CustomContentManager.RockMeterPresets.GetPresetById(Profile.RockMeterPreset) ??
                RockMeterPreset.Normal;
            Profile.RockMeterPreset = RockMeterPreset.Id;

        }

        /// <summary>
        /// Applies synced preset data from network for remote players when EnablePresetSync is true.
        /// First tries to find the preset locally by ID, then falls back to deserializing from JSON.
        /// </summary>
        /// <param name="cameraPresetId">The camera preset GUID.</param>
        /// <param name="cameraPresetJson">Serialized camera preset JSON as fallback.</param>
        /// <param name="highwayPresetId">The highway preset GUID.</param>
        /// <param name="highwayPresetJson">Serialized highway preset JSON as fallback.</param>
        /// <param name="colorProfileId">The color profile GUID.</param>
        /// <param name="colorProfileJson">Serialized color profile JSON as fallback.</param>
        /// <param name="themePresetId">The theme preset GUID.</param>
        /// <param name="themePresetJson">Serialized theme preset JSON as fallback.</param>
        public void ApplySyncedPresets(
            Guid cameraPresetId, string cameraPresetJson,
            Guid highwayPresetId, string highwayPresetJson,
            Guid colorProfileId, string colorProfileJson,
            Guid themePresetId, string themePresetJson)
        {
            Debug.Log($"[YargPlayer] ApplySyncedPresets for {Profile.Name}: Camera={cameraPresetId}, Highway={highwayPresetId}, Color={colorProfileId}, Theme={themePresetId}");
            
            // Apply camera preset
            CameraPreset = TryGetOrDeserializePreset(
                cameraPresetId,
                cameraPresetJson,
                CustomContentManager.CameraSettings.GetPresetById,
                CameraPreset.Default);
            Profile.CameraPreset = CameraPreset.Id;
            
            // Apply highway preset
            HighwayPreset = TryGetOrDeserializePreset(
                highwayPresetId,
                highwayPresetJson,
                CustomContentManager.HighwayPresets.GetPresetById,
                HighwayPreset.Default);
            Profile.HighwayPreset = HighwayPreset.Id;
            
            // Apply color profile
            ColorProfile = TryGetOrDeserializePreset(
                colorProfileId,
                colorProfileJson,
                CustomContentManager.ColorProfiles.GetPresetById,
                ColorProfile.Default);
            Profile.ColorProfile = ColorProfile.Id;
            
            // Apply theme preset
            ThemePreset = TryGetOrDeserializePreset(
                themePresetId,
                themePresetJson,
                CustomContentManager.ThemePresets.GetPresetById,
                ThemePreset.Default);
            Profile.ThemePreset = ThemePreset.Id;
            
            // Load remaining presets from local defaults (engine, rock meter)
            EnginePreset = CustomContentManager.EnginePresets.GetPresetById(Profile.EnginePreset)
                ?? EnginePreset.Default;
            Profile.EnginePreset = EnginePreset.Id;
            
            RockMeterPreset = CustomContentManager.RockMeterPresets.GetPresetById(Profile.RockMeterPreset) ??
                RockMeterPreset.Normal;
            Profile.RockMeterPreset = RockMeterPreset.Id;
            
            // Mark that synced presets have been applied - prevents RefreshPresets from overwriting them
            HasSyncedPresets = true;
            
            Debug.Log($"[YargPlayer] Applied synced presets: Camera={CameraPreset.Name}, Highway={HighwayPreset.Name}, Color={ColorProfile.Name}, Theme={ThemePreset.Name}");
        }
        
        // JSON settings for preset serialization/deserialization - must match CustomContent settings
        private static readonly JsonSerializerSettings PresetJsonSettings = new()
        {
            Formatting = Formatting.Indented,
            Converters = new List<JsonConverter>
            {
                new JsonColorConverter(),
                new JsonVector2Converter()
            }
        };
        
        /// <summary>
        /// Tries to get a preset by ID from CustomContentManager, or deserializes from JSON as fallback.
        /// </summary>
        private T TryGetOrDeserializePreset<T>(
            Guid presetId,
            string presetJson,
            Func<Guid, T?> getById,
            T defaultPreset) where T : class
        {
            // Try to find locally by ID first
            if (presetId != Guid.Empty)
            {
                var localPreset = getById(presetId);
                if (localPreset != null)
                {
                    Debug.Log($"[YargPlayer] Found preset {presetId} locally");
                    return localPreset;
                }
            }
            
            // Try to deserialize from JSON using the same settings as CustomContent
            if (!string.IsNullOrEmpty(presetJson))
            {
                try
                {
                    var deserialized = JsonConvert.DeserializeObject<T>(presetJson, PresetJsonSettings);
                    if (deserialized != null)
                    {
                        Debug.Log($"[YargPlayer] Deserialized preset from JSON (length={presetJson.Length})");
                        return deserialized;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[YargPlayer] Failed to deserialize preset JSON: {ex.Message}\nJSON: {presetJson.Substring(0, Math.Min(200, presetJson.Length))}...");
                }
            }
            
            // Fallback to default
            Debug.Log($"[YargPlayer] Using default preset (no JSON or deserialize failed)");
            return defaultPreset;
        }

        public void EnableInputs()
        {
            if (InputsEnabled || Bindings == null)
            {
                return;
            }

            Bindings.EnableInputs();
            Bindings.MenuInputProcessed += OnMenuInput;
            InputManager.RegisterPlayer(this);

            InputsEnabled = true;
        }

        public void DisableInputs()
        {
            if (!InputsEnabled || Bindings == null)
            {
                return;
            }

            Bindings.DisableInputs();
            Bindings.MenuInputProcessed -= OnMenuInput;
            InputManager.UnregisterPlayer(this);

            InputsEnabled = false;
        }

        private void OnMenuInput(ref GameInput input)
        {
            MenuInput?.Invoke(this, ref input);
        }

        public void Dispose()
        {
            DisableInputs();
            Bindings?.Dispose();
        }
    }
}