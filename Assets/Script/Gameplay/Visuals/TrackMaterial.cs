using System;
using System.Collections.Generic;
using UnityEngine;
using YARG.Core.Game;
using YARG.Helpers.Extensions;
using YARG.Settings;

namespace YARG.Gameplay.Visuals
{
    public class TrackMaterial : MonoBehaviour
    {
        // TODO: MOST OF THIS CLASS IS TEMPORARY UNTIL THE TRACK TEXTURE SETTINGS ARE IN

        private static readonly int _scrollProperty         = Shader.PropertyToID("_Scroll");
        private static readonly int _starpowerStateProperty = Shader.PropertyToID("_Starpower_State");
        private static readonly int _starpowerTimeProperty  = Shader.PropertyToID("_Starpower_Start_Time");
        private static readonly int _wavinessProperty       = Shader.PropertyToID("_Waviness");

        private static readonly int _layer1ColorProperty = Shader.PropertyToID("_Layer_1_Color");
        private static readonly int _layer2ColorProperty = Shader.PropertyToID("_Layer_2_Color");
        private static readonly int _layer3ColorProperty = Shader.PropertyToID("_Layer_3_Color");
        private static readonly int _layer4ColorProperty = Shader.PropertyToID("_Layer_4_Color");

        private static readonly int _starPowerColorProperty = Shader.PropertyToID("_Starpower_Color");
        
        private bool _isGrayedOut;
        private Preset _savedNormalPreset;
        private Preset _savedGroovePreset;

        public struct Preset
        {
            public Color Layer1;
            public Color Layer2;
            public Color Layer3;
            public Color Layer4;

            public static Preset FromHighwayPreset(HighwayPreset preset, bool groove)
            {
                if (groove)
                {
                    return new Preset
                    {
                        Layer1 = preset.BackgroundGrooveBaseColor1.ToUnityColor(),
                        Layer2 = preset.BackgroundGrooveBaseColor2.ToUnityColor(),
                        Layer3 = preset.BackgroundGrooveBaseColor3.ToUnityColor(),
                        Layer4 = preset.BackgroundGroovePatternColor.ToUnityColor()
                    };
                }

                return new Preset
                {
                    Layer1 = preset.BackgroundBaseColor1.ToUnityColor(),
                    Layer2 = preset.BackgroundBaseColor2.ToUnityColor(),
                    Layer3 = preset.BackgroundBaseColor3.ToUnityColor(),
                    Layer4 = preset.BackgroundPatternColor.ToUnityColor()
                };
            }
        }

        private Preset _normalPreset;
        private Preset _groovePreset;

        private float _grooveState;
        private float GrooveState
        {
            get => _grooveState;
            set
            {
                _grooveState = value;

                _material.SetColor(_layer1ColorProperty,
                    Color.Lerp(_normalPreset.Layer1, _groovePreset.Layer1, value));
                _material.SetColor(_layer2ColorProperty,
                    Color.Lerp(_normalPreset.Layer2, _groovePreset.Layer2, value));
                _material.SetColor(_layer3ColorProperty,
                    Color.Lerp(_normalPreset.Layer3, _groovePreset.Layer3, value));
                _material.SetColor(_layer4ColorProperty,
                    Color.Lerp(_normalPreset.Layer4, _groovePreset.Layer4, value));

                _material.SetFloat(_wavinessProperty, value);
            }
        }

        [HideInInspector]
        public bool GrooveMode;

        private bool _starpowerMode;
        [HideInInspector]
        public bool StarpowerMode
        {
            get => _starpowerMode;
            // When going from false to true, also set the starpower time
            set
            {
                if (value && !_starpowerMode)
                {
                    _material.SetFloat(_starpowerTimeProperty, Time.time);
                }
                _starpowerMode = value;
            }
        }
        private GameManager _gameManager;

        public float StarpowerState
        {
            get => _material.GetFloat(_starpowerStateProperty);
            set => _material.SetFloat(_starpowerStateProperty, value);
        }

        [SerializeField]
        private MeshRenderer _trackMesh;

        [SerializeField]
        private MeshRenderer[] _trackTrims;

        private Material _material;
        private readonly List<Material> _trimMaterials = new();

        private void Awake()
        {
            // Get materials
            _material = _trackMesh.material;
            foreach (var trim in _trackTrims)
            {
                _trimMaterials.Add(trim.material);
            }

            _normalPreset = new()
            {
                Layer1 = FromHex("0F0F0F", 1f),
                Layer2 = FromHex("4B4B4B", 0.15f),
                Layer3 = FromHex("FFFFFF", 0f),
                Layer4 = FromHex("575757", 1f)
            };

            _groovePreset = new()
            {
                Layer1 = FromHex("000933", 1f),
                Layer2 = FromHex("23349C", 0.15f),
                Layer3 = FromHex("FFFFFF", 0f),
                Layer4 = FromHex("2C499E", 1f)
            };
        }

        public void Initialize(HighwayPreset highwayPreset)
        {
            _material.SetColor(_starPowerColorProperty, highwayPreset.StarPowerColor.ToUnityColor() );
            _normalPreset = Preset.FromHighwayPreset(highwayPreset, false);
            _groovePreset = Preset.FromHighwayPreset(highwayPreset, true);
        }

        private void Update()
        {
            if (GrooveMode)
            {
                GrooveState = Mathf.Lerp(GrooveState, 1f, Time.deltaTime * 5f);
            }
            else
            {
                GrooveState = Mathf.Lerp(GrooveState, 0f, Time.deltaTime * 3f);
            }

            if (StarpowerMode && SettingsManager.Settings.StarPowerHighwayFx.Value is StarPowerHighwayFxMode.On)
            {
                StarpowerState = 1.0f;
            }
            else
            {
                StarpowerState = Mathf.Lerp(StarpowerState, 0f, Time.deltaTime * 4f);
            }
        }

        private static Color FromHex(string hex, float alpha)
        {
            if (ColorUtility.TryParseHtmlString("#" + hex, out var color))
            {
                color.a = alpha;
                return color;
            }

            throw new InvalidOperationException();
        }

        public void SetTrackScroll(double time, float noteSpeed)
        {
            float position = (float) time * noteSpeed / 4f;
            _material.SetFloat(_scrollProperty, position);
        }
        
        /// <summary>
        /// Sets the track to a grayed out state for disconnected players.
        /// </summary>
        public void SetGrayedOut(bool grayedOut)
        {
            if (_isGrayedOut == grayedOut)
                return;
                
            _isGrayedOut = grayedOut;
            
            if (grayedOut)
            {
                // Save current presets
                _savedNormalPreset = _normalPreset;
                _savedGroovePreset = _groovePreset;
                
                // Create grayed out preset
                var grayPreset = new Preset
                {
                    Layer1 = new Color(0.15f, 0.15f, 0.15f, 0.5f),
                    Layer2 = new Color(0.25f, 0.25f, 0.25f, 0.3f),
                    Layer3 = new Color(0.3f, 0.3f, 0.3f, 0f),
                    Layer4 = new Color(0.2f, 0.2f, 0.2f, 0.5f)
                };
                
                _normalPreset = grayPreset;
                _groovePreset = grayPreset;
                
                // Apply immediately
                _material.SetColor(_layer1ColorProperty, grayPreset.Layer1);
                _material.SetColor(_layer2ColorProperty, grayPreset.Layer2);
                _material.SetColor(_layer3ColorProperty, grayPreset.Layer3);
                _material.SetColor(_layer4ColorProperty, grayPreset.Layer4);
                
                // Disable starpower and groove effects
                StarpowerMode = false;
                GrooveMode = false;
                StarpowerState = 0f;
            }
            else
            {
                // Restore saved presets
                _normalPreset = _savedNormalPreset;
                _groovePreset = _savedGroovePreset;
            }
        }
    }
}
