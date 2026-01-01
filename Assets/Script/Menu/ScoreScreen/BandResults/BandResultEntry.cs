using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using DG.Tweening;

namespace YARG.Menu.ScoreScreen
{
    /// <summary>
    /// UI entry for a band in the band results list.
    /// Clickable to drill down into individual player cards.
    /// </summary>
    public class BandResultEntry : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        [Header("Position Display")]
        [SerializeField]
        private TextMeshProUGUI _positionText;
        
        [SerializeField]
        private Image _medalIcon;
        
        [SerializeField]
        private Sprite _medalSprite;
        
        [Header("Medal Colors")]
        [SerializeField]
        private Color _firstPlaceMedalColor = new Color(1f, 0.84f, 0f, 1f); // Gold
        
        [SerializeField]
        private Color _secondPlaceMedalColor = new Color(0.75f, 0.75f, 0.8f, 1f); // Silver
        
        [SerializeField]
        private Color _thirdPlaceMedalColor = new Color(0.8f, 0.5f, 0.2f, 1f); // Bronze
        
        [Header("Band Info")]
        [SerializeField]
        private TextMeshProUGUI _bandNameText;
        
        [SerializeField]
        private TextMeshProUGUI _scoreText;
        
        [SerializeField]
        private TextMeshProUGUI _marginText;
        
        [SerializeField]
        private StarView _starView;
        
        [Header("Player Indicator")]
        [SerializeField]
        private GameObject _localBandIndicator;
        
        [SerializeField]
        private TextMeshProUGUI _playerCountText;
        
        [Header("Status")]
        [SerializeField]
        private GameObject _failedBadge;
        
        [Header("Visual")]
        [SerializeField]
        private Image _backgroundImage;
        
        [SerializeField]
        private CanvasGroup _canvasGroup;
        
        [SerializeField]
        private RectTransform _contentRect;
        
        [Header("Colors")]
        [SerializeField]
        private Color _localBandColor = new Color(0.2f, 0.4f, 0.8f, 0.3f);
        
        [SerializeField]
        private Color _normalColor = new Color(0.15f, 0.15f, 0.15f, 0.8f);
        
        [SerializeField]
        private Color _hoverColor = new Color(0.25f, 0.25f, 0.25f, 0.9f);
        
        [SerializeField]
        private Color _selectedColor = new Color(0.4f, 0.7f, 1f, 0.8f);
        
        [SerializeField]
        private Color _firstPlaceColor = new Color(1f, 0.84f, 0f, 0.15f);
        
        [SerializeField]
        private Color _secondPlaceColor = new Color(0.75f, 0.75f, 0.75f, 0.1f);
        
        [SerializeField]
        private Color _thirdPlaceColor = new Color(0.8f, 0.5f, 0.2f, 0.1f);
        
        private BandResult _bandResult;
        private bool _isHovered;
        private bool _isSelected;
        private Color _baseColor;
        
        /// <summary>
        /// Event fired when this entry is clicked.
        /// </summary>
        public event Action<BandResult> OnClicked;
        
        /// <summary>
        /// The band result data for this entry.
        /// </summary>
        public BandResult BandResult => _bandResult;
        
        /// <summary>
        /// Whether this entry is currently selected.
        /// </summary>
        public bool IsSelected => _isSelected;
        
        /// <summary>
        /// Initializes the entry with band result data.
        /// </summary>
        public void Initialize(BandResult result)
        {
            _bandResult = result;
            
            // Position display
            SetupPositionDisplay(result.Position);
            
            // Band info
            if (_bandNameText != null)
            {
                _bandNameText.text = result.BandName;
            }
            
            if (_scoreText != null)
            {
                _scoreText.text = result.TotalScore.ToString("N0");
            }
            
            // Margin to next
            if (_marginText != null)
            {
                if (result.MarginToNext > 0)
                {
                    _marginText.text = $"+{result.MarginToNext:N0}";
                    _marginText.gameObject.SetActive(true);
                }
                else
                {
                    _marginText.gameObject.SetActive(false);
                }
            }
            
            // Stars
            if (_starView != null)
            {
                _starView.SetStars(result.StarRating);
            }
            
            // Local band indicator
            if (_localBandIndicator != null)
            {
                _localBandIndicator.SetActive(result.IsLocalBand);
            }
            
            // Player count
            if (_playerCountText != null)
            {
                _playerCountText.text = $"{result.PlayerIds.Count} player{(result.PlayerIds.Count != 1 ? "s" : "")}";
            }
            
            // Failed status
            if (_failedBadge != null)
            {
                _failedBadge.SetActive(result.HasFailed);
            }
            
            // Background color based on position and local band
            SetupBackgroundColor(result);
            
            // Start hidden for reveal animation
            if (_canvasGroup != null)
            {
                _canvasGroup.alpha = 0f;
            }
            
            if (_contentRect != null)
            {
                _contentRect.anchoredPosition = new Vector2(-200f, _contentRect.anchoredPosition.y);
            }
        }
        
        private void SetupPositionDisplay(int position)
        {
            bool showMedal = position <= 3;
            
            if (_medalIcon != null)
            {
                _medalIcon.gameObject.SetActive(showMedal);
                
                if (showMedal)
                {
                    // Set the sprite and tint with position color
                    if (_medalSprite != null)
                    {
                        _medalIcon.sprite = _medalSprite;
                    }
                    
                    _medalIcon.color = position switch
                    {
                        1 => _firstPlaceMedalColor,
                        2 => _secondPlaceMedalColor,
                        3 => _thirdPlaceMedalColor,
                        _ => Color.white
                    };
                }
            }
            
            if (_positionText != null)
            {
                if (showMedal)
                {
                    // Show ordinal for top 3
                    _positionText.text = position switch
                    {
                        1 => "1ST",
                        2 => "2ND",
                        3 => "3RD",
                        _ => $"{position}TH"
                    };
                }
                else
                {
                    // Just show number for 4th+
                    _positionText.text = $"{position}{GetOrdinalSuffix(position)}";
                }
            }
        }
        
        private static string GetOrdinalSuffix(int number)
        {
            if (number % 100 >= 11 && number % 100 <= 13)
                return "TH";
            
            return (number % 10) switch
            {
                1 => "ST",
                2 => "ND",
                3 => "RD",
                _ => "TH"
            };
        }
        
        private void SetupBackgroundColor(BandResult result)
        {
            if (_backgroundImage == null)
                return;
            
            // Determine base color
            if (result.IsLocalBand)
            {
                _baseColor = _localBandColor;
            }
            else
            {
                _baseColor = result.Position switch
                {
                    1 => _firstPlaceColor,
                    2 => _secondPlaceColor,
                    3 => _thirdPlaceColor,
                    _ => _normalColor
                };
            }
            
            _backgroundImage.color = _baseColor;
        }
        
        /// <summary>
        /// Plays the reveal animation for this entry.
        /// </summary>
        /// <param name="delay">Delay before starting the animation.</param>
        /// <param name="screenShakeIntensity">Screen shake intensity (0-1, 0 = no shake).</param>
        /// <param name="onComplete">Callback when animation completes.</param>
        public void PlayRevealAnimation(float delay, float screenShakeIntensity, Action onComplete = null)
        {
            var sequence = DOTween.Sequence();
            sequence.SetDelay(delay);
            
            // Slide in from left
            if (_contentRect != null)
            {
                sequence.Append(_contentRect.DOAnchorPosX(0f, 0.3f).SetEase(Ease.OutBack));
            }
            
            // Fade in
            if (_canvasGroup != null)
            {
                sequence.Join(_canvasGroup.DOFade(1f, 0.2f));
            }
            
            // Screen shake for top positions
            if (screenShakeIntensity > 0f)
            {
                sequence.AppendCallback(() =>
                {
                    // Trigger screen shake via Camera or UI shake system
                    TriggerScreenShake(screenShakeIntensity);
                });
            }
            
            // Scale punch for emphasis
            if (_bandResult.Position <= 3)
            {
                sequence.Append(transform.DOPunchScale(Vector3.one * 0.05f, 0.2f, 5, 0.5f));
            }
            
            if (onComplete != null)
            {
                sequence.OnComplete(() => onComplete());
            }
        }
        
        private void TriggerScreenShake(float intensity)
        {
            // Find the parent canvas or panel and shake it
            var parentRect = transform.parent?.GetComponent<RectTransform>();
            if (parentRect != null)
            {
                parentRect.DOShakePosition(0.15f, intensity * 10f, 20, 90f, false, true, ShakeRandomnessMode.Harmonic);
            }
        }
        
        public void OnPointerClick(PointerEventData eventData)
        {
            OnClicked?.Invoke(_bandResult);
            
            // Click feedback
            transform.DOPunchScale(Vector3.one * -0.02f, 0.1f, 5, 0.5f);
        }
        
        public void OnPointerEnter(PointerEventData eventData)
        {
            _isHovered = true;
            
            if (_backgroundImage != null)
            {
                _backgroundImage.DOColor(_hoverColor + _baseColor, 0.15f);
            }
            
            transform.DOScale(1.02f, 0.1f);
        }
        
        public void OnPointerExit(PointerEventData eventData)
        {
            _isHovered = false;
            
            if (_backgroundImage != null)
            {
                var targetColor = _isSelected ? _selectedColor : _baseColor;
                _backgroundImage.DOColor(targetColor, 0.15f);
            }
            
            if (!_isSelected)
            {
                transform.DOScale(1f, 0.1f);
            }
        }
        
        /// <summary>
        /// Sets the selected state of this entry.
        /// </summary>
        public void SetSelected(bool selected)
        {
            _isSelected = selected;
            
            if (_backgroundImage != null)
            {
                var targetColor = selected ? _selectedColor : _baseColor;
                _backgroundImage.DOColor(targetColor, 0.15f);
            }
            
            // More noticeable scale change for selected entry
            // Kill any existing scale tween first to avoid conflicts
            DOTween.Kill(transform, complete: false);
            
            if (selected)
            {
                // Scale up and add a subtle pulse effect when selected
                transform.DOScale(1.04f, 0.15f).SetEase(Ease.OutBack);
            }
            else
            {
                transform.DOScale(1f, 0.1f);
            }
        }
        
        private void OnDestroy()
        {
            // Kill any running tweens
            DOTween.Kill(transform);
            DOTween.Kill(_backgroundImage);
            DOTween.Kill(_canvasGroup);
            DOTween.Kill(_contentRect);
        }
    }
}
