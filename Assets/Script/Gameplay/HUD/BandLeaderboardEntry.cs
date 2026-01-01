using System;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace YARG.Gameplay.HUD
{
    /// <summary>
    /// Single entry in the racing-style band leaderboard.
    /// Displays: [Position] [BandName] [Score] with optional score difference.
    /// Background color changes to indicate: normal, local band highlight, star power active.
    /// </summary>
    public class BandLeaderboardEntry : MonoBehaviour
    {
        [Header("UI References")]
        [SerializeField]
        private Image _backgroundImage;
        
        [SerializeField]
        private TextMeshProUGUI _positionText;
        
        [SerializeField]
        private TextMeshProUGUI _bandNameText;
        
        [SerializeField]
        private TextMeshProUGUI _scoreText;
        
        [SerializeField]
        private TextMeshProUGUI _diffText;
        
        private RectTransform _rectTransform;
        private int _currentBandId = -1;
        private Color _normalBgColor;
        private Color _highlightColor;
        private Color _starPowerColor;
        private Tween _positionChangeTween;
        private Tween _highlightPulseTween;
        
        public int CurrentBandId => _currentBandId;
        
        private void Awake()
        {
            _rectTransform = GetComponent<RectTransform>();
            if (_backgroundImage != null)
            {
                _normalBgColor = _backgroundImage.color;
            }
        }
        
        public void Initialize(Color highlightColor, Color starPowerColor)
        {
            _highlightColor = highlightColor;
            _starPowerColor = starPowerColor;
            
            if (_diffText != null)
            {
                _diffText.gameObject.SetActive(false);
            }
        }
        
        public void SetData(int position, string bandName, long score, bool isLocal, bool starPowerActive, Color textColor, Color starPowerColor)
        {
            // Position
            if (_positionText != null)
            {
                _positionText.text = position.ToString();
                _positionText.color = textColor;
            }
            
            // Band name
            if (_bandNameText != null)
            {
                _bandNameText.text = bandName;
                _bandNameText.color = textColor;
            }
            
            // Score
            if (_scoreText != null)
            {
                _scoreText.text = score.ToString("N0");
                _scoreText.color = textColor;
            }
            
            // Set background color based on state:
            // Priority: Star Power > Local Highlight > Normal
            if (_backgroundImage != null)
            {
                if (starPowerActive)
                {
                    // Star power active - use star power color with good visibility
                    _backgroundImage.color = new Color(
                        _starPowerColor.r * 0.5f,
                        _starPowerColor.g * 0.5f,
                        _starPowerColor.b * 0.1f,
                        0.9f
                    );
                }
                else if (isLocal)
                {
                    // Local band highlight - use highlight color
                    _backgroundImage.color = new Color(
                        _highlightColor.r * 0.3f, 
                        _highlightColor.g * 0.3f, 
                        _highlightColor.b * 0.3f, 
                        0.8f
                    );
                }
                else
                {
                    // Normal background
                    _backgroundImage.color = _normalBgColor;
                }
            }
        }
        
        public void SetBandId(int bandId)
        {
            _currentBandId = bandId;
        }
        
        public void ShowScoreDifference(long diff, Color color)
        {
            if (_diffText == null) return;
            
            _diffText.gameObject.SetActive(true);
            
            string prefix = diff > 0 ? "+" : "";
            if (Math.Abs(diff) >= 1000000)
            {
                _diffText.text = $"{prefix}{diff / 1000000f:F1}M";
            }
            else if (Math.Abs(diff) >= 1000)
            {
                _diffText.text = $"{prefix}{diff / 1000f:F0}K";
            }
            else
            {
                _diffText.text = $"{prefix}{diff}";
            }
            
            _diffText.color = color;
        }
        
        public void HideScoreDifference()
        {
            if (_diffText != null)
            {
                _diffText.gameObject.SetActive(false);
            }
        }
        
        public void AnimatePositionChange(bool gainedPosition, Color flashColor, float duration)
        {
            if (_backgroundImage == null || _rectTransform == null) return;
            
            // Kill any existing tween
            _positionChangeTween?.Kill();
            
            // Flash the background color
            var originalColor = _backgroundImage.color;
            
            _positionChangeTween = DOTween.Sequence()
                .Append(_backgroundImage.DOColor(flashColor, duration * 0.3f))
                .Append(_backgroundImage.DOColor(originalColor, duration * 0.7f))
                .SetEase(Ease.OutQuad);
            
            // Scale pop for gained positions
            if (gainedPosition)
            {
                _rectTransform.DOPunchScale(new Vector3(0.05f, 0.1f, 0), duration, 1, 0.5f);
            }
        }
        
        public void Kill()
        {
            _positionChangeTween?.Kill();
            _highlightPulseTween?.Kill();
            
            if (gameObject != null)
            {
                Destroy(gameObject);
            }
        }
    }
}
