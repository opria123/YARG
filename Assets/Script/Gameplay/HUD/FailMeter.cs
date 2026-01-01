using System;
using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Serialization;
using UnityEngine.UI;
using YARG.Core;
using YARG.Core.Engine;
using YARG.Core.Game;
using YARG.Core.Logging;
using YARG.Gameplay.Player;
using YARG.Helpers.Extensions;
using YARG.Player;

namespace YARG.Gameplay.HUD
{
    public class FailMeter : MonoBehaviour
    {
        [SerializeField]
        private GameObject _meterContainer;
        [FormerlySerializedAs("Slider")]
        [SerializeField]
        private Slider _bandSlider;
        [FormerlySerializedAs("FillImage")]
        [SerializeField]
        private Image _fillImage;
        [SerializeField]
        private Slider _sliderPrefab;
        [SerializeField]
        private Slider _needlePrefab;
        [SerializeField]
        private RectTransform _sliderContainer;

        private Slider[]  _playerSliders;
        private Slider[]  _needleSliders;
        private Tweener[] _playerHappinessTweeners = Array.Empty<Tweener>();
        private Tweener[] _needleHappinessTweeners = Array.Empty<Tweener>();
        private Tweener[] _xposTweeners            = Array.Empty<Tweener>();
        private Tweener   _meterRedTweener;
        private Tweener   _meterYellowTweener;
        private Tweener   _meterGreenTweener;
        private Tweener   _bandFillTweener;
        private Tweener   _meterPositionTweener;
        private float[]   _previousPlayerHappiness;
        private float     _previousBandHappiness;

        private MeterColor _previousMeterColor;

        private Vector2[] _xPosVectors;

        private bool _intendedActive;

        // TODO: Should probably make a more specific class we can reference here
        private EngineManager _engineManager;
        private GameManager _gameManager;

        private readonly List<EngineManager.EngineContainer> _players = new();
        
        // Spectate mode support
        private bool _isSpectateMode;
        private readonly List<TrackPlayer> _spectatorPlayers = new();

        // Allows some overlap
        private const float HAPPINESS_COLLISION_RANGE = 0.06f;
        private const float SPRITE_OVERLAP_OFFSET     = 28f;
        private const float SPRITE_INITIAL_OFFSET     = 42f;

        // GameManager will have to initialize us
        public void Initialize(EngineManager engineManager, GameManager gameManager)
        {
            _gameManager = gameManager;
            _engineManager = engineManager;
            _players.AddRange(engineManager.Engines);

            _playerSliders = new Slider[_players.Count];
            _needleSliders = new Slider[_players.Count];
            _xposTweeners = new Tweener[_players.Count];
            _playerHappinessTweeners = new Tweener[_players.Count];
            _needleHappinessTweeners = new Tweener[_players.Count];
            _previousPlayerHappiness = new float[_players.Count];
            _xPosVectors = new Vector2[_players.Count];

            // Cache tweens for later use
            _meterRedTweener = _fillImage.DOColor(ColorProfile.DefaultRed.ToUnityColor(), 0.25f).
                SetLoops(-1, LoopType.Yoyo).
                SetEase(Ease.InOutSine).
                SetAutoKill(false).Pause();

            _meterYellowTweener = _fillImage.DOColor(ColorProfile.DefaultYellow.ToUnityColor(), 0.25f).
                SetAutoKill(false).
                Pause();

            _meterGreenTweener = _fillImage.DOColor(ColorProfile.DefaultGreen.ToUnityColor(), 0.25f).
                SetAutoKill(false).
                Pause();

            // 0.8f is an arbitrary placeholder
            _bandFillTweener = _fillImage.DOFillAmount(0.8f, 0.125f).
                SetAutoKill(false);

            // This is set up to move the container offscreen, but may later be used to move it back on
            _meterPositionTweener = _meterContainer.transform.DOMoveY(-400f, 0.5f).
                SetAutoKill(false).
                Pause();


            // attach the slider instances to the scene and apply the correct icon
            for (int i = _players.Count - 1; i >= 0; i--)
            {
                _playerSliders[i] = Instantiate(_sliderPrefab, _sliderContainer);
                _needleSliders[i] = Instantiate(_needlePrefab, _sliderContainer);
                // y value is ignored, so it is ok that it is zero here
                var xOffset = SPRITE_INITIAL_OFFSET + (SPRITE_OVERLAP_OFFSET * i);
                _xPosVectors[i] = new Vector2(xOffset, 0);

                _xposTweeners[i] = _playerSliders[i].handleRect.DOAnchorPosX(_xPosVectors[i].x, 0.125f).SetAutoKill(false);
                _needleSliders[i].handleRect.DOAnchorPosX(SPRITE_INITIAL_OFFSET, 0.125f).SetAutoKill(false);

                var handleImage = _playerSliders[i].handleRect.GetComponentInChildren<Image>();
                var spriteName = _players[i].GetInstrumentSprite();

                var sprite = Addressables.LoadAssetAsync<Sprite>(spriteName).WaitForCompletion();
                handleImage.sprite = sprite;
                handleImage.color = _players[i].GetHarmonyColor();

                _playerSliders[i].value = 0.01f;
                _needleSliders[i].value = 0.01f;
                _playerSliders[i].gameObject.SetActive(true);
                _needleSliders[i].gameObject.SetActive(true);

                // Cached for reuse because starting a new tween generates garbage
                _playerHappinessTweeners[i] = _playerSliders[i].DOValue(_players[i].Happiness, 0.5f).SetAutoKill(false);
                _needleHappinessTweeners[i] = _needleSliders[i].DOValue(_players[i].Happiness, 0.5f).SetAutoKill(false);
                _previousPlayerHappiness[i] = _players[i].Happiness;
            }

            YargLogger.LogDebug("Initialized fail meter");
        }
        
        /// <summary>
        /// Reinitializes the fail meter to show spectated band's players instead of local players.
        /// </summary>
        public void EnterSpectateMode(List<TrackPlayer> spectatorPlayers)
        {
            if (_isSpectateMode)
            {
                // Already in spectate mode, clean up old spectator sliders first
                CleanupSpectatorSliders();
            }
            
            _isSpectateMode = true;
            _spectatorPlayers.Clear();
            _spectatorPlayers.AddRange(spectatorPlayers);
            
            // Hide the local player sliders
            if (_playerSliders != null)
            {
                for (int i = 0; i < _playerSliders.Length; i++)
                {
                    if (_playerSliders[i] != null)
                    {
                        _playerSliders[i].gameObject.SetActive(false);
                    }
                    if (_needleSliders[i] != null)
                    {
                        _needleSliders[i].gameObject.SetActive(false);
                    }
                }
            }
            
            // Create new sliders for spectator players
            CreateSpectatorSliders();
            
            YargLogger.LogDebug($"Fail meter entered spectate mode with {spectatorPlayers.Count} players");
        }
        
        /// <summary>
        /// Exits spectate mode and restores the local player sliders.
        /// </summary>
        public void ExitSpectateMode()
        {
            if (!_isSpectateMode)
            {
                return;
            }
            
            _isSpectateMode = false;
            
            // Clean up spectator sliders
            CleanupSpectatorSliders();
            _spectatorPlayers.Clear();
            
            // Restore local player sliders
            if (_playerSliders != null)
            {
                for (int i = 0; i < _playerSliders.Length; i++)
                {
                    if (_playerSliders[i] != null)
                    {
                        _playerSliders[i].gameObject.SetActive(true);
                    }
                    if (_needleSliders[i] != null)
                    {
                        _needleSliders[i].gameObject.SetActive(true);
                    }
                }
            }
            
            YargLogger.LogDebug("Fail meter exited spectate mode");
        }
        
        // Spectate mode slider arrays
        private Slider[] _spectatorSliders;
        private Slider[] _spectatorNeedleSliders;
        private Tweener[] _spectatorHappinessTweeners = Array.Empty<Tweener>();
        private Tweener[] _spectatorNeedleHappinessTweeners = Array.Empty<Tweener>();
        private Tweener[] _spectatorXposTweeners = Array.Empty<Tweener>();
        private float[] _spectatorPreviousHappiness;
        private Vector2[] _spectatorXPosVectors;
        
        private void CreateSpectatorSliders()
        {
            int count = _spectatorPlayers.Count;
            _spectatorSliders = new Slider[count];
            _spectatorNeedleSliders = new Slider[count];
            _spectatorHappinessTweeners = new Tweener[count];
            _spectatorNeedleHappinessTweeners = new Tweener[count];
            _spectatorXposTweeners = new Tweener[count];
            _spectatorPreviousHappiness = new float[count];
            _spectatorXPosVectors = new Vector2[count];
            
            for (int i = count - 1; i >= 0; i--)
            {
                _spectatorSliders[i] = Instantiate(_sliderPrefab, _sliderContainer);
                _spectatorNeedleSliders[i] = Instantiate(_needlePrefab, _sliderContainer);
                
                var xOffset = SPRITE_INITIAL_OFFSET + (SPRITE_OVERLAP_OFFSET * i);
                _spectatorXPosVectors[i] = new Vector2(xOffset, 0);
                
                _spectatorXposTweeners[i] = _spectatorSliders[i].handleRect
                    .DOAnchorPosX(_spectatorXPosVectors[i].x, 0.125f).SetAutoKill(false);
                _spectatorNeedleSliders[i].handleRect.DOAnchorPosX(SPRITE_INITIAL_OFFSET, 0.125f).SetAutoKill(false);
                
                var handleImage = _spectatorSliders[i].handleRect.GetComponentInChildren<Image>();
                var player = _spectatorPlayers[i].Player;
                // Use the extension method from InstrumentIconProvider for proper sprite address
                var spriteName = player.GetInstrumentSprite();
                
                var sprite = Addressables.LoadAssetAsync<Sprite>(spriteName).WaitForCompletion();
                handleImage.sprite = sprite;
                handleImage.color = player.GetHarmonyColor();
                
                var happiness = _spectatorPlayers[i].PlayerEngineContainer?.Happiness ?? 0.5f;
                _spectatorSliders[i].value = 0.01f;
                _spectatorNeedleSliders[i].value = 0.01f;
                _spectatorSliders[i].gameObject.SetActive(true);
                _spectatorNeedleSliders[i].gameObject.SetActive(true);
                
                _spectatorHappinessTweeners[i] = _spectatorSliders[i].DOValue(happiness, 0.5f).SetAutoKill(false);
                _spectatorNeedleHappinessTweeners[i] = _spectatorNeedleSliders[i].DOValue(happiness, 0.5f).SetAutoKill(false);
                _spectatorPreviousHappiness[i] = happiness;
            }
        }
        
        private void CleanupSpectatorSliders()
        {
            if (_spectatorSliders != null)
            {
                foreach (var slider in _spectatorSliders)
                {
                    if (slider != null)
                    {
                        Destroy(slider.gameObject);
                    }
                }
            }
            
            if (_spectatorNeedleSliders != null)
            {
                foreach (var slider in _spectatorNeedleSliders)
                {
                    if (slider != null)
                    {
                        Destroy(slider.gameObject);
                    }
                }
            }
            
            // Kill tweeners
            if (_spectatorHappinessTweeners != null)
            {
                foreach (var tween in _spectatorHappinessTweeners)
                {
                    tween?.Kill();
                }
            }
            
            if (_spectatorNeedleHappinessTweeners != null)
            {
                foreach (var tween in _spectatorNeedleHappinessTweeners)
                {
                    tween?.Kill();
                }
            }
            
            if (_spectatorXposTweeners != null)
            {
                foreach (var tween in _spectatorXposTweeners)
                {
                    tween?.Kill();
                }
            }
            
            _spectatorSliders = null;
            _spectatorNeedleSliders = null;
            _spectatorHappinessTweeners = Array.Empty<Tweener>();
            _spectatorNeedleHappinessTweeners = Array.Empty<Tweener>();
            _spectatorXposTweeners = Array.Empty<Tweener>();
        }
        
        private float GetSpectatorBandHappiness()
        {
            if (_spectatorPlayers.Count == 0)
            {
                return 0.5f;
            }
            
            float total = 0f;
            foreach (var player in _spectatorPlayers)
            {
                total += GetSpectatorPlayerHappiness(player);
            }
            return total / _spectatorPlayers.Count;
        }
        
        /// <summary>
        /// Gets happiness for a spectator track player.
        /// For spectator tracks, EngineContainer is null, so we get happiness from NetworkPlayerData.
        /// </summary>
        private float GetSpectatorPlayerHappiness(TrackPlayer player)
        {
            // Try NetworkPlayerData first (for spectator tracks that don't have an EngineContainer)
            var networkData = player.NetworkPlayerData;
            if (networkData != null)
            {
                // Debug: Periodically log spectator player happiness to diagnose sync issues
                if (_spectatorDebugLogTimer <= 0f)
                {
                    YargLogger.LogDebug($"[FailMeter] Spectator '{networkData.PlayerName}' happiness={networkData.Happiness:F2}, hasFailed={networkData.HasFailed}");
                }
                return networkData.Happiness;
            }
            
            // Fallback to EngineContainer (shouldn't happen for spectator tracks)
            return player.PlayerEngineContainer?.Happiness ?? 0.5f;
        }
        
        // Debug timer for spectate mode logging
        private float _spectatorDebugLogTimer;

        // Update is called once per frame
        private void Update()
        {
            // Don't crash the whole game if we didn't get initialized and still manage to somehow become active
            if (_engineManager == null)
            {
                return;
            }

            // No need for any of this if we're paused anyway
            if (_gameManager.Paused)
            {
                return;
            }

            if (_isSpectateMode)
            {
                UpdateSpectateMode();
            }
            else
            {
                UpdateNormalMode();
            }
        }
        
        private void UpdateNormalMode()
        {
            if (_previousBandHappiness != _engineManager.Happiness)
            {
                UpdateMeterFill(_engineManager.Happiness);
            }

            for (var i = _players.Count - 1; i >= 0; i--)
            {
                int overlap = 0;
                // Check if we will overlap another icon
                for (var j = i; j >= 0; j--)
                {
                    if (j == i)
                    {
                        // Ignore self
                        continue;
                    }

                    if (Math.Abs(_players[i].Happiness - _players[j].Happiness) < HAPPINESS_COLLISION_RANGE)
                    {
                        overlap++;
                    }
                }

                // The extra SPRITE_INITIAL_OFFSET is to get the whole group a bit farther from the meter itself
                var xOffset =  SPRITE_INITIAL_OFFSET + (SPRITE_OVERLAP_OFFSET * overlap);
                _xPosVectors[i].x = xOffset;

                _xposTweeners[i].ChangeEndValue(_xPosVectors[i], 0.125f, true).Play();

                // This we can not do if the current player's happiness hasn't changed
                if (_previousPlayerHappiness[i] != _players[i].Happiness)
                {
                    _playerHappinessTweeners[i].ChangeValues(_playerSliders[i].value, _players[i].Happiness, 0.1f);
                    _needleHappinessTweeners[i].ChangeValues(_needleSliders[i].value, _players[i].Happiness, 0.1f);

                    // Not sure if strictly necessary, but it seems like good practice to not try to play a playing tween
                    if (_playerHappinessTweeners[i].IsComplete())
                    {
                        _playerHappinessTweeners[i].Play();
                    }
                    else
                    {
                        _playerHappinessTweeners[i].Restart();
                    }

                    if (_needleHappinessTweeners[i].IsComplete())
                    {
                        _needleHappinessTweeners[i].Play();
                    }
                    else
                    {
                        _needleHappinessTweeners[i].Restart();
                    }
                }

                _previousPlayerHappiness[i] = _players[i].Happiness;
            }
        }
        
        private void UpdateSpectateMode()
        {
            // Debug: Periodically log spectator happiness (every 2 seconds)
            _spectatorDebugLogTimer -= Time.deltaTime;
            if (_spectatorDebugLogTimer <= 0f)
            {
                _spectatorDebugLogTimer = 2f;
            }
            
            var bandHappiness = GetSpectatorBandHappiness();
            if (_previousBandHappiness != bandHappiness)
            {
                UpdateMeterFill(bandHappiness);
            }
            
            if (_spectatorSliders == null || _spectatorPlayers.Count == 0)
            {
                return;
            }

            for (var i = _spectatorPlayers.Count - 1; i >= 0; i--)
            {
                var happiness = GetSpectatorPlayerHappiness(_spectatorPlayers[i]);
                
                int overlap = 0;
                // Check if we will overlap another icon
                for (var j = i; j >= 0; j--)
                {
                    if (j == i)
                    {
                        continue;
                    }

                    var otherHappiness = GetSpectatorPlayerHappiness(_spectatorPlayers[j]);
                    if (Math.Abs(happiness - otherHappiness) < HAPPINESS_COLLISION_RANGE)
                    {
                        overlap++;
                    }
                }

                var xOffset = SPRITE_INITIAL_OFFSET + (SPRITE_OVERLAP_OFFSET * overlap);
                _spectatorXPosVectors[i].x = xOffset;

                _spectatorXposTweeners[i].ChangeEndValue(_spectatorXPosVectors[i], 0.125f, true).Play();

                if (_spectatorPreviousHappiness[i] != happiness)
                {
                    _spectatorHappinessTweeners[i].ChangeValues(_spectatorSliders[i].value, happiness, 0.1f);
                    _spectatorNeedleHappinessTweeners[i].ChangeValues(_spectatorNeedleSliders[i].value, happiness, 0.1f);

                    if (_spectatorHappinessTweeners[i].IsComplete())
                    {
                        _spectatorHappinessTweeners[i].Play();
                    }
                    else
                    {
                        _spectatorHappinessTweeners[i].Restart();
                    }

                    if (_spectatorNeedleHappinessTweeners[i].IsComplete())
                    {
                        _spectatorNeedleHappinessTweeners[i].Play();
                    }
                    else
                    {
                        _spectatorNeedleHappinessTweeners[i].Restart();
                    }
                }

                _spectatorPreviousHappiness[i] = happiness;
            }
        }

        private void UpdateMeterFill(float happiness)
        {
            var currentColor = GetMeterColor(happiness);
            if (currentColor != _previousMeterColor)
            {
                ApplyColor(currentColor);
                _previousMeterColor = currentColor;
            }

            _bandFillTweener.ChangeValues(_fillImage.fillAmount, happiness).Play();

            _previousBandHappiness = happiness;
        }

        private void ApplyColor(MeterColor color)
        {
            if (_meterRedTweener.active)
            {
                _meterRedTweener.Pause();
            }

            if (_meterYellowTweener.active)
            {
                _meterYellowTweener.Pause();
            }

            if (_meterGreenTweener.active)
            {
                _meterGreenTweener.Pause();
            }

            switch (color)
            {
                case MeterColor.Red:
                    _fillImage.color = Color.black;
                    _meterRedTweener.Restart();
                    break;
                case MeterColor.Yellow:
                    _meterYellowTweener.Restart();
                    break;
                case MeterColor.Green:
                    _meterGreenTweener.Restart();
                    break;
            }
        }

        public void SetActive(bool active)
        {
            if (active)
            {
                // Move onscreen
                _meterPositionTweener.PlayBackwards();
            }

            if (!active)
            {
                // Move offscreen
                _meterPositionTweener.PlayForward();
            }
        }

        private static MeterColor GetMeterColor(float happiness)
        {
            return happiness switch
            {
                < 0.333f => MeterColor.Red,
                < 0.666f => MeterColor.Yellow,
                _        => MeterColor.Green
            };
        }

        private void OnDisable()
        {
            // Make sure the tweens are dead
            _meterRedTweener?.Kill();
            _meterYellowTweener?.Kill();
            _meterGreenTweener?.Kill();
            _bandFillTweener?.Kill();
            _meterPositionTweener?.Kill();
            foreach (var tween in _playerHappinessTweeners)
            {
                tween.Kill();
            }

            foreach (var tween in _needleHappinessTweeners)
            {
                tween.Kill();
            }

            foreach (var tween in _xposTweeners)
            {
                tween.Kill();
            }
            
            // Clean up spectate mode tweeners and sliders
            CleanupSpectatorSliders();
        }

        private enum MeterColor
        {
            Red,
            Yellow,
            Green
        }
    }
}
