using DG.Tweening;
using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.UI;
using YARG.Core;
using YARG.Core.Logging;
using YARG.Gameplay.Player;
using YARG.Helpers.Extensions;
using YARG.Player;
using YARG.Settings;

namespace YARG.Gameplay.HUD
{
    public class PlayerNameDisplay : GameplayBehaviour
    {
        [SerializeField]
        private TextMeshProUGUI _playerName;
        [SerializeField]
        private Image _instrumentIcon;
        [SerializeField]
        private RawImage _needleIcon;

        private CanvasGroup _canvasGroup;

        public float DisplayTime = 3.0f;
        public float FadeDuration = 0.5f;

        private bool _isPersistent;
        private Coroutine _fadeoutCoroutine;

        protected override void GameplayAwake()
        {
            _canvasGroup = GetComponent<CanvasGroup>();
            _canvasGroup.alpha = 0f;
        }

        public void ShowPlayer(YargPlayer player)
        {
            if (!ShouldShowPlayer())
            {
                return;
            }

            var profile = player.Profile;
            _playerName.text = profile.Name;

            var spriteName = player.GetInstrumentSprite();
            _instrumentIcon.sprite = Addressables
                .LoadAssetAsync<Sprite>(spriteName)
                .WaitForCompletion();

            if (!_isPersistent)
            {
                _fadeoutCoroutine = StartCoroutine(FadeoutCoroutine());
            }
        }

        public void ShowPlayer(YargPlayer player, int needleId)
        {
            if (!ShouldShowPlayer())
            {
                return;
            }

            var textureNeedle = $"VocalNeedleTexture/{needleId}";
            _needleIcon.texture = Addressables.LoadAssetAsync<Texture2D>(textureNeedle).WaitForCompletion();
            _instrumentIcon.color = player.GetHarmonyColor();
            ShowPlayer(player);
        }

        /// <summary>
        /// Shows the player name persistently (doesn't fade out).
        /// Used for remote players in multiplayer to always show who is playing.
        /// Only the player name text stays visible - icons fade out normally.
        /// </summary>
        /// <param name="player">The player to display</param>
        /// <param name="needleId">The vocal needle ID for the texture</param>
        /// <param name="harmonyColor">Optional harmony color override</param>
        public void ShowPlayerPersistent(YargPlayer player, int needleId, Color? harmonyColor = null)
        {
            // Stop any running fadeout coroutine
            if (_fadeoutCoroutine != null)
            {
                StopCoroutine(_fadeoutCoroutine);
                _fadeoutCoroutine = null;
            }
            
            _isPersistent = true;
            
            var profile = player.Profile;
            _playerName.text = profile.Name;
            
            // Set player name color to match harmony
            if (harmonyColor.HasValue)
            {
                _playerName.color = harmonyColor.Value;
            }

            var textureNeedle = $"VocalNeedleTexture/{needleId}";
            _needleIcon.texture = Addressables.LoadAssetAsync<Texture2D>(textureNeedle).WaitForCompletion();
            
            var spriteName = player.GetInstrumentSprite();
            _instrumentIcon.sprite = Addressables
                .LoadAssetAsync<Sprite>(spriteName)
                .WaitForCompletion();
            
            _instrumentIcon.color = harmonyColor ?? player.GetHarmonyColor();
            
            // Show immediately, then fade out the icons but keep the name visible
            _canvasGroup.alpha = 1f;
            gameObject.SetActive(true);
            
            // Start coroutine to fade out icons only
            _fadeoutCoroutine = StartCoroutine(FadeoutIconsOnlyCoroutine());
        }

        /// <summary>
        /// Sets the color of the player name text (for matching harmony colors).
        /// </summary>
        public void SetTextColor(Color color)
        {
            if (_playerName != null)
            {
                _playerName.color = color;
            }
        }

        private bool ShouldShowPlayer()
        {
            // For persistent mode (remote players), always show
            if (_isPersistent)
            {
                return true;
            }
            return !GameManager.IsPractice && SettingsManager.Settings.ShowPlayerNameWhenStartingSong.Value;
        }

        private IEnumerator FadeoutCoroutine()
        {
            _canvasGroup.alpha = 1f;
            yield return new WaitForSeconds(DisplayTime);
            yield return _canvasGroup.DOFade(0f, FadeDuration).WaitForCompletion();

            _fadeoutCoroutine = null;
            gameObject.SetActive(false);
        }

        /// <summary>
        /// Fades out only the icons while keeping the player name visible.
        /// Also transitions the player name text color from harmony color to white.
        /// Used for persistent mode (remote players).
        /// </summary>
        private IEnumerator FadeoutIconsOnlyCoroutine()
        {
            yield return new WaitForSeconds(DisplayTime);
            
            // Fade out the icons over the duration and transition text color to white
            float elapsed = 0f;
            Color instrumentStartColor = _instrumentIcon.color;
            Color needleStartColor = _needleIcon.color;
            Color textStartColor = _playerName.color;
            Color textEndColor = Color.white;
            
            while (elapsed < FadeDuration)
            {
                elapsed += Time.deltaTime;
                float t = elapsed / FadeDuration;
                
                // Fade icon alphas to 0
                _instrumentIcon.color = instrumentStartColor.WithAlpha(Mathf.Lerp(1f, 0f, t));
                _needleIcon.color = needleStartColor.WithAlpha(Mathf.Lerp(1f, 0f, t));
                
                // Transition text color from harmony to white
                _playerName.color = Color.Lerp(textStartColor, textEndColor, t);
                
                yield return null;
            }
            
            // Ensure icons are fully faded and text is white
            _instrumentIcon.color = instrumentStartColor.WithAlpha(0f);
            _needleIcon.color = needleStartColor.WithAlpha(0f);
            _playerName.color = textEndColor;
            
            _fadeoutCoroutine = null;
        }
    }
}
