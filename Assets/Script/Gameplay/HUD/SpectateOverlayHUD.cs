using System.Collections;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using YARG.Networking.Bands;

namespace YARG.Gameplay.HUD
{
    /// <summary>
    /// Displays an overlay when the local player's band has failed but other bands are still playing.
    /// Shows "You Failed" message with a gray vignette effect.
    /// </summary>
    public class SpectateOverlayHUD : GameplayBehaviour
    {
        [Header("Overlay")]
        [SerializeField]
        private Image _vignetteOverlay;
        
        [SerializeField]
        private CanvasGroup _overlayCanvasGroup;
        
        [Header("Failed Message")]
        [SerializeField]
        private TextMeshProUGUI _failedText;
        
        [SerializeField]
        private TextMeshProUGUI _spectatingText;
        
        [Header("Animation")]
        [SerializeField]
        private float _fadeInDuration = 0.5f;
        
        [SerializeField]
        private float _vignetteAlpha = 0.5f;
        
        [Header("Colors")]
        [SerializeField]
        private Color _vignetteColor = new Color(0, 0, 0, 0.3f); // Lighter vignette for better visibility
        
        [SerializeField]
        private Color _failedTextColor = new Color(0.9f, 0.2f, 0.2f, 1f);
        
        [SerializeField]
        private Color _spectatingTextColor = new Color(0.8f, 0.8f, 0.8f, 1f);
        
        private bool _isActive;
        private Tween _fadeInTween;
        
        protected override void GameplayAwake()
        {
            // Create the overlay UI if not assigned
            if (_vignetteOverlay == null)
            {
                CreateOverlayUI();
            }
            
            // Initially hide everything
            if (_overlayCanvasGroup != null)
            {
                _overlayCanvasGroup.alpha = 0;
                _overlayCanvasGroup.gameObject.SetActive(false);
            }
            
            // Subscribe to spectate event
            if (GameManager != null)
            {
                GameManager.OnSpectateStarted += OnSpectateStarted;
            }
        }
        
        protected override void GameplayDestroy()
        {
            _isActive = false;
            StopAllCoroutines();
            
            _fadeInTween?.Kill();
            
            // Kill DOTween on vignette to prevent errors after destroy
            if (_vignetteOverlay != null)
            {
                _vignetteOverlay.DOKill();
            }
            if (_overlayCanvasGroup != null)
            {
                _overlayCanvasGroup.DOKill();
            }
            
            if (GameManager != null)
            {
                GameManager.OnSpectateStarted -= OnSpectateStarted;
            }
        }
        
        private void CreateOverlayUI()
        {
            // Create main container
            var containerObj = new GameObject("SpectateOverlay");
            containerObj.transform.SetParent(transform, false);
            
            var containerRect = containerObj.AddComponent<RectTransform>();
            containerRect.anchorMin = Vector2.zero;
            containerRect.anchorMax = Vector2.one;
            containerRect.offsetMin = Vector2.zero;
            containerRect.offsetMax = Vector2.zero;
            
            _overlayCanvasGroup = containerObj.AddComponent<CanvasGroup>();
            _overlayCanvasGroup.alpha = 0;
            _overlayCanvasGroup.blocksRaycasts = false;
            _overlayCanvasGroup.interactable = false;
            
            // Create vignette overlay (darkens edges of screen)
            var vignetteObj = new GameObject("Vignette");
            vignetteObj.transform.SetParent(containerObj.transform, false);
            
            var vignetteRect = vignetteObj.AddComponent<RectTransform>();
            vignetteRect.anchorMin = Vector2.zero;
            vignetteRect.anchorMax = Vector2.one;
            vignetteRect.offsetMin = Vector2.zero;
            vignetteRect.offsetMax = Vector2.zero;
            
            _vignetteOverlay = vignetteObj.AddComponent<Image>();
            _vignetteOverlay.color = _vignetteColor;
            
            // Try to load a vignette sprite, fallback to solid color
            // A proper vignette would be transparent in center, dark at edges
            _vignetteOverlay.raycastTarget = false;
            
            // Create "YOU FAILED" text - positioned at bottom of screen
            var failedObj = new GameObject("FailedText");
            failedObj.transform.SetParent(containerObj.transform, false);
            
            var failedRect = failedObj.AddComponent<RectTransform>();
            // Anchor to bottom center
            failedRect.anchorMin = new Vector2(0.5f, 0f);
            failedRect.anchorMax = new Vector2(0.5f, 0f);
            failedRect.pivot = new Vector2(0.5f, 0f);
            failedRect.anchoredPosition = new Vector2(0, 120); // 120 pixels from bottom
            failedRect.sizeDelta = new Vector2(600, 100);
            
            _failedText = failedObj.AddComponent<TextMeshProUGUI>();
            _failedText.text = "YOU FAILED";
            _failedText.fontSize = 56; // Slightly smaller since it's at the bottom
            _failedText.fontStyle = FontStyles.Bold;
            _failedText.alignment = TextAlignmentOptions.Center;
            _failedText.color = _failedTextColor;
            _failedText.enableWordWrapping = false;
            
            // Add outline for visibility
            _failedText.outlineWidth = 0.2f;
            _failedText.outlineColor = new Color32(0, 0, 0, 200);
            
            // Create "Spectating..." text - below the failed text
            var spectatingObj = new GameObject("SpectatingText");
            spectatingObj.transform.SetParent(containerObj.transform, false);
            
            var spectatingRect = spectatingObj.AddComponent<RectTransform>();
            // Anchor to bottom center
            spectatingRect.anchorMin = new Vector2(0.5f, 0f);
            spectatingRect.anchorMax = new Vector2(0.5f, 0f);
            spectatingRect.pivot = new Vector2(0.5f, 0f);
            spectatingRect.anchoredPosition = new Vector2(0, 60); // Below the failed text
            spectatingRect.sizeDelta = new Vector2(800, 50);
            
            _spectatingText = spectatingObj.AddComponent<TextMeshProUGUI>();
            _spectatingText.text = "Spectating other bands...";
            _spectatingText.fontSize = 28;
            _spectatingText.fontStyle = FontStyles.Italic;
            _spectatingText.alignment = TextAlignmentOptions.Center;
            _spectatingText.color = _spectatingTextColor;
            _spectatingText.enableWordWrapping = false;
            
            containerObj.SetActive(false);
        }
        
        private int _currentSpectatingBandId = -1;
        
        private void OnSpectateStarted()
        {
            if (_isActive)
                return;
            
            _isActive = true;
            
            Debug.Log("[SpectateOverlayHUD] Spectate mode activated - starting spectate setup");
            
            // Use coroutine to setup the overlay (tracks are already created by GameManager)
            StartCoroutine(SetupSpectateCoroutine());
        }
        
        private IEnumerator SetupSpectateCoroutine()
        {
            // Get the band we're spectating (tracks already created and revealed by GameManager)
            var bandManager = BandManager.Instance;
            if (bandManager != null)
            {
                int spectateBandId = bandManager.GetNextAliveBandToSpectate();
                if (spectateBandId >= 0 && bandManager.Bands.TryGetValue(spectateBandId, out var bandInfo))
                {
                    _currentSpectatingBandId = spectateBandId;
                    _spectatingText.text = $"Watching: {bandInfo.DisplayName} - Score: {bandInfo.TotalScore:N0}";
                    
                    // Start updating the score periodically
                    StartCoroutine(UpdateSpectatingScoreCoroutine(spectateBandId));
                }
                else
                {
                    _spectatingText.text = "Waiting for other bands...";
                }
            }
            
            // Wait one frame to ensure everything is settled
            yield return null;
            
            Debug.Log("[SpectateOverlayHUD] Showing overlay");
            
            // Show and animate the overlay
            ShowOverlay();
        }
        
        private System.Collections.IEnumerator UpdateSpectatingScoreCoroutine(int bandId)
        {
            var bandManager = BandManager.Instance;
            while (_isActive && bandManager != null)
            {
                if (bandManager.Bands.TryGetValue(bandId, out var bandInfo))
                {
                    if (!bandInfo.HasFailed)
                    {
                        _spectatingText.text = $"Watching: {bandInfo.DisplayName} - Score: {bandInfo.TotalScore:N0}";
                    }
                    else
                    {
                        // Band we were watching failed, find another
                        int newBandId = bandManager.GetNextAliveBandToSpectate();
                        if (newBandId >= 0 && bandManager.Bands.TryGetValue(newBandId, out var newBandInfo))
                        {
                            // Switch to new band - remove old tracks and create new ones
                            SwitchSpectatedBand(bandId, newBandId);
                            bandId = newBandId;
                            _spectatingText.text = $"Watching: {newBandInfo.DisplayName} - Score: {newBandInfo.TotalScore:N0}";
                        }
                        else
                        {
                            _spectatingText.text = "All bands have failed!";
                            yield break;
                        }
                    }
                }
                yield return new WaitForSeconds(0.1f); // Update every 100ms
            }
        }
        
        private void SwitchSpectatedBand(int oldBandId, int newBandId)
        {
            if (GameManager == null) return;
            
            Debug.Log($"[SpectateOverlayHUD] Switching spectate from band {oldBandId} to band {newBandId}");
            
            // Remove old spectator tracks (don't restore local tracks - we're still spectating)
            GameManager.RemoveSpectatorTracks(restoreLocalTracks: false);
            
            // Create new spectator tracks (this will keep local tracks hidden)
            _currentSpectatingBandId = newBandId;
            GameManager.CreateSpectatorTracksForBand(newBandId);
            
            // Immediately reveal since we're already in spectate mode
            // (the initial transition was already handled)
            GameManager.RevealSpectatorTracks();
        }
        
        private void ShowOverlay()
        {
            _fadeInTween?.Kill();
            
            if (_overlayCanvasGroup == null)
                return;
            
            _overlayCanvasGroup.gameObject.SetActive(true);
            _overlayCanvasGroup.alpha = 0;
            
            // Fade in
            _fadeInTween = DOTween.Sequence()
                .Append(_overlayCanvasGroup.DOFade(1f, _fadeInDuration))
                .Join(_failedText.transform.DOScale(Vector3.one, _fadeInDuration).From(Vector3.one * 1.5f).SetEase(Ease.OutBack))
                .SetEase(Ease.OutQuad);
            
            // Pulse the vignette gently
            if (_vignetteOverlay != null)
            {
                var targetAlpha = _vignetteColor.a;
                _vignetteOverlay.DOFade(targetAlpha * 0.7f, 2f)
                    .SetLoops(-1, LoopType.Yoyo)
                    .SetEase(Ease.InOutSine);
            }
        }
        
        /// <summary>
        /// Hides the spectate overlay (called when transitioning to results).
        /// </summary>
        public void HideOverlay()
        {
            _isActive = false;
            StopAllCoroutines();
            
            _fadeInTween?.Kill();
            
            // Kill vignette animation
            if (_vignetteOverlay != null)
            {
                _vignetteOverlay.DOKill();
            }
            
            if (_overlayCanvasGroup != null)
            {
                _overlayCanvasGroup.DOKill();
                _overlayCanvasGroup.DOFade(0f, 0.3f)
                    .OnComplete(() => 
                    {
                        if (_overlayCanvasGroup != null && _overlayCanvasGroup.gameObject != null)
                            _overlayCanvasGroup.gameObject.SetActive(false);
                    });
            }
        }
    }
}
