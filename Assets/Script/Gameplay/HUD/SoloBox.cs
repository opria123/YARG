using System;
using System.Collections;
using Cysharp.Text;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using YARG.Core.Engine;
using YARG.Localization;

namespace YARG.Gameplay.HUD
{
    public class SoloBox : MonoBehaviour
    {
        [SerializeField]
        private Image           _soloBox;
        [SerializeField]
        private TextMeshProUGUI _soloTopText;
        [SerializeField]
        private TextMeshProUGUI _soloBottomText;
        [SerializeField]
        private TextMeshProUGUI _soloFullText;
        [SerializeField]
        private CanvasGroup     _soloBoxCanvasGroup;

        [Space]
        [SerializeField]
        private Sprite _soloSpriteNormal;
        [SerializeField]
        private Sprite _soloSpritePerfect;
        [SerializeField]
        private Sprite _soloSpriteMessy;

        [SerializeField]
        private TMP_ColorGradient _soloGradientNormal;
        [SerializeField]
        private TMP_ColorGradient _soloGradientPerfect;
        [SerializeField]
        private TMP_ColorGradient _soloGradientMessy;

        private bool _soloEnded;
        private SoloSection _solo;

        private bool _showingForPreview;
        
        // Remote player solo state (used when _solo is null)
        private bool _isRemoteSolo;
        private int _remoteNoteCount;
        private int _remoteNotesHit;
        private int _remoteLastSequence = -1;

        private int HitPercent
        {
            get
            {
                if (_isRemoteSolo)
                {
                    if (_remoteNoteCount <= 0) return 0;
                    return Mathf.FloorToInt((float) _remoteNotesHit / _remoteNoteCount * 100f);
                }
                return Mathf.FloorToInt((float) _solo.NotesHit / _solo.NoteCount * 100f);
            }
        }
        
        private int NoteCount => _isRemoteSolo ? _remoteNoteCount : (_solo?.NoteCount ?? 0);
        private int NotesHit => _isRemoteSolo ? _remoteNotesHit : (_solo?.NotesHit ?? 0);

        private Coroutine _currentCoroutine;

        public void StartSolo(SoloSection solo)
        {
            // Don't even bother if the solo has no points
            if (solo.NoteCount == 0) return;

            _solo = solo;
            _soloEnded = false;
            gameObject.SetActive(true);

            StopCurrentCoroutine();

            _currentCoroutine = StartCoroutine(ShowCoroutine());
        }

        private IEnumerator ShowCoroutine()
        {
            // Ensure the solo box image is visible (may have been hidden by ForceReset)
            _soloBox.gameObject.SetActive(true);
            
            _soloFullText.text = string.Empty;
            _soloBox.sprite = _soloSpriteNormal;

            // Set some dummy text
            _soloTopText.text = "0%";
            _soloBottomText.SetTextFormat("0/{0}", NoteCount);

            // Fade in the box
            yield return _soloBoxCanvasGroup
                .DOFade(1f, 0.25f)
                .WaitForCompletion();
        }

        private void Update()
        {
            if (_soloEnded || _showingForPreview) return;
            
            // Null check for safety (remote solos don't have _solo)
            if (!_isRemoteSolo && _solo == null) return;

            _soloTopText.SetTextFormat("{0}%", HitPercent);
            _soloBottomText.SetTextFormat("{0}/{1}", NotesHit, NoteCount);
        }

        public void EndSolo(int soloBonus, Action endCallback)
        {
            StopCurrentCoroutine();

            _currentCoroutine = StartCoroutine(HideCoroutine(soloBonus, endCallback));
        }

        public void ForceReset()
        {
            StopCurrentCoroutine();
            _soloEnded = true;
            _isRemoteSolo = false;
            _remoteLastSequence = -1;

            _soloBox.gameObject.SetActive(false);
            _currentCoroutine = null;
            _solo = null;
        }
        
        #region Remote Player Solo Support
        
        /// <summary>
        /// Updates the solo display for a remote player based on network state.
        /// Call this every frame when displaying a remote player's track.
        /// </summary>
        /// <param name="soloActive">Whether the solo is currently active</param>
        /// <param name="soloSequence">Sequence number to detect solo start/end transitions</param>
        /// <param name="noteCount">Total notes in the solo</param>
        /// <param name="notesHit">Notes hit so far in the solo</param>
        /// <param name="lastBonus">The bonus from the last completed solo (used when solo ends)</param>
        public void UpdateRemoteSolo(bool soloActive, int soloSequence, int noteCount, int notesHit, int lastBonus)
        {
            // Detect solo start (sequence changed and now active)
            if (soloActive && soloSequence != _remoteLastSequence && noteCount > 0)
            {
                StartRemoteSolo(noteCount);
                _remoteLastSequence = soloSequence;
            }
            // Detect solo end (was active, now not active, and sequence matches)
            else if (!soloActive && _isRemoteSolo && !_soloEnded)
            {
                EndRemoteSolo(lastBonus);
            }
            // Update ongoing solo
            else if (soloActive && _isRemoteSolo && !_soloEnded)
            {
                _remoteNotesHit = Mathf.Clamp(notesHit, 0, _remoteNoteCount);
            }
        }
        
        /// <summary>
        /// Starts displaying a solo for a remote player.
        /// </summary>
        private void StartRemoteSolo(int noteCount)
        {
            if (noteCount <= 0) return;
            
            _isRemoteSolo = true;
            _remoteNoteCount = noteCount;
            _remoteNotesHit = 0;
            _soloEnded = false;
            _solo = null; // Clear any local solo reference
            
            gameObject.SetActive(true);
            StopCurrentCoroutine();
            _currentCoroutine = StartCoroutine(ShowCoroutine());
        }
        
        /// <summary>
        /// Ends the solo display for a remote player.
        /// </summary>
        private void EndRemoteSolo(int soloBonus)
        {
            if (!_isRemoteSolo) return;
            
            StopCurrentCoroutine();
            _currentCoroutine = StartCoroutine(HideCoroutine(soloBonus, () =>
            {
                _isRemoteSolo = false;
            }));
        }
        
        #endregion

        private IEnumerator HideCoroutine(int soloBonus, Action endCallback)
        {
            _soloEnded = true;

            // Hide the top and bottom text
            _soloTopText.text = string.Empty;
            _soloBottomText.text = string.Empty;

            // Get the correct gradient and color
            var (sprite, gradient) = HitPercent switch
            {
                >= 100 => (_soloSpritePerfect, _soloGradientPerfect),
                >= 60  => (_soloSpriteNormal, _soloGradientNormal),
                _      => (_soloSpriteMessy, _soloGradientMessy),
            };
            _soloBox.sprite = sprite;
            _soloFullText.colorGradientPreset = gradient;

            // Display final hit percentage
            _soloFullText.SetTextFormat("{0}%", HitPercent);

            yield return new WaitForSeconds(1f);

            // Show performance text
            string performanceKey = HitPercent switch
            {
                > 100 => "How",
                  100 => "Perfect",
                >= 95 => "Awesome",
                >= 90 => "Great",
                >= 80 => "Good",
                >= 70 => "Solid",
                   69 => "Nice",
                >= 60 => "Okay",
                >= 0  => "Messy",
                <  0  => "How",
            };

            _soloFullText.text = Localize.Key("Gameplay.Solo.Performance", performanceKey);

            yield return new WaitForSeconds(1f);

            // Show point bonus
            _soloFullText.text = Localize.KeyFormat("Gameplay.Solo.PointsResult", soloBonus);

            yield return new WaitForSeconds(1f);

            // Fade out the box
            yield return _soloBoxCanvasGroup
                .DOFade(0f, 0.25f)
                .WaitForCompletion();

            _soloBox.gameObject.SetActive(false);
            _currentCoroutine = null;
            _solo = null;

            endCallback?.Invoke();
        }

        private void StopCurrentCoroutine()
        {
            if (_currentCoroutine != null)
            {
                StopCoroutine(_currentCoroutine);
                _currentCoroutine = null;
            }
        }

        public void PreviewForEditMode(bool on)
        {
            if (on && !_soloBox.gameObject.activeSelf)
            {
                _soloBox.gameObject.SetActive(true);

                // Set preview solo box properties
                _soloFullText.text = string.Empty;
                _soloBox.sprite = _soloSpriteNormal;
                _soloTopText.text = "50%";
                _soloBottomText.text = "50/100";
                _soloBoxCanvasGroup.alpha = 1f;

                _showingForPreview = true;
            }
            else if (!on && _showingForPreview)
            {
                _soloBox.gameObject.SetActive(false);
                _showingForPreview = false;
            }
        }
    }
}