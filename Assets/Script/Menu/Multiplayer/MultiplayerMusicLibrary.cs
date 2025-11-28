using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using YARG.Core.Song;
using YARG.Multiplayer;
using YARG.Networking;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;

namespace YARG.Menu.Multiplayer
{
    /// <summary>
    /// Handles multiplayer-specific music library functionality.
    /// Shows selected song and "Start Song" button for host.
    /// </summary>
    public class MultiplayerMusicLibrary : MonoBehaviour
    {
        [Header("UI Elements")]
        [SerializeField] private GameObject multiplayerPanel;
        [SerializeField] private TextMeshProUGUI selectedSongText;
        [SerializeField] private Button startSongButton;
        [SerializeField] private TextMeshProUGUI waitingText;

        private MultiplayerShowPlaylist _showPlaylist;
        private Coroutine _waitForPlaylistRoutine;
        private bool _isHost;

        private void Start()
        {
            // Check if we're in multiplayer mode
            if (YargNetworkManager.Instance == null || !YargNetworkManager.Instance.isNetworkActive)
            {
                // Not in multiplayer, hide panel
                if (multiplayerPanel != null)
                {
                    multiplayerPanel.SetActive(false);
                }
                return;
            }

            _isHost = YargNetworkManager.Instance.LocalUserIsHost();

            // Show multiplayer panel
            if (multiplayerPanel != null)
            {
                multiplayerPanel.SetActive(true);
            }

            // Wire up start button
            if (startSongButton != null)
            {
                startSongButton.onClick.AddListener(OnStartSongClicked);
                startSongButton.gameObject.SetActive(_isHost);
            }

            // Show appropriate text
            if (waitingText != null)
            {
                waitingText.gameObject.SetActive(!_isHost);
            }

            if (selectedSongText != null)
            {
                selectedSongText.text = "Queue empty";
            }

            UpdateWaitingLabel();
            UpdateStartButtonState();

            if (!TryAttachPlaylist() && gameObject.activeInHierarchy)
            {
                _waitForPlaylistRoutine = StartCoroutine(WaitForPlaylistReference());
            }
        }

        private void OnDestroy()
        {
            DetachPlaylistEvents();

            if (_waitForPlaylistRoutine != null)
            {
                StopCoroutine(_waitForPlaylistRoutine);
                _waitForPlaylistRoutine = null;
            }
        }

        private IEnumerator WaitForPlaylistReference()
        {
            float elapsed = 0f;
            const float timeout = 2f;
            while (!TryAttachPlaylist() && elapsed < timeout)
            {
                yield return new WaitForSeconds(0.1f);
                elapsed += 0.1f;
            }
        }

        private bool TryAttachPlaylist()
        {
            var playlist = YargNetworkManager.Instance?.MultiplayerShowPlaylist;
            if (playlist == null)
            {
                return false;
            }

            AttachPlaylist(playlist);
            return true;
        }

        private void AttachPlaylist(MultiplayerShowPlaylist playlist)
        {
            if (_showPlaylist == playlist)
            {
                return;
            }

            DetachPlaylistEvents();
            _showPlaylist = playlist;
            _showPlaylist.OnQueueChanged += HandleQueueChanged;
            _showPlaylist.OnSetStarted += HandleSetStateChanged;
            _showPlaylist.OnSetEnded += HandleSetStateChanged;
            _showPlaylist.OnSongStarting += HandleSongStarting;
            UpdateUiFromPlaylist();
        }

        private void DetachPlaylistEvents()
        {
            if (_showPlaylist == null)
            {
                return;
            }

            _showPlaylist.OnQueueChanged -= HandleQueueChanged;
            _showPlaylist.OnSetStarted -= HandleSetStateChanged;
            _showPlaylist.OnSetEnded -= HandleSetStateChanged;
            _showPlaylist.OnSongStarting -= HandleSongStarting;
            _showPlaylist = null;
        }

        private void HandleQueueChanged()
        {
            UpdateUiFromPlaylist();
        }

        private void HandleSetStateChanged()
        {
            UpdateUiFromPlaylist();
        }

        private void HandleSongStarting(SongEntry _)
        {
            UpdateSelectedSongLabel();
        }

        private void OnStartSongClicked()
        {
            if (!_isHost)
            {
                Debug.LogWarning("[MultiplayerMusicLibrary] Cannot start show - local user is not host");
                return;
            }

            if (_showPlaylist == null || !_showPlaylist.HasQueue)
            {
                Debug.LogWarning("[MultiplayerMusicLibrary] Cannot start show - playlist not ready or empty");
                return;
            }

            Debug.Log("[MultiplayerMusicLibrary] Host requested start of multiplayer show via playlist UI");
            _showPlaylist.CmdStartShow();
        }

        private void UpdateUiFromPlaylist()
        {
            UpdateSelectedSongLabel();
            UpdateStartButtonState();
            UpdateWaitingLabel();
        }

        private void UpdateSelectedSongLabel()
        {
            if (selectedSongText == null)
            {
                return;
            }

            if (_showPlaylist == null || !_showPlaylist.HasQueue)
            {
                selectedSongText.text = _isHost
                    ? "Queue empty"
                    : "Waiting for host to queue songs";
                return;
            }

            var queue = _showPlaylist.CurrentQueue;
            if (queue.Count == 0)
            {
                selectedSongText.text = "Queue empty";
                return;
            }

            int index = _showPlaylist.IsPlayingSet
                ? Mathf.Clamp(_showPlaylist.CurrentSongIndex, 0, queue.Count - 1)
                : 0;

            var song = queue[index];
            string songName = song.Name.ToString();
            string artistName = song.Artist.ToString();
            string prefix = _showPlaylist.IsPlayingSet ? "Now playing" : "Next in set";
            selectedSongText.text = $"{prefix}: {songName} by {artistName}";
        }

        private void UpdateStartButtonState()
        {
            if (startSongButton == null)
            {
                return;
            }

            bool canStart = _isHost && _showPlaylist != null && _showPlaylist.HasQueue && !_showPlaylist.IsPlayingSet;
            startSongButton.gameObject.SetActive(_isHost);
            startSongButton.interactable = canStart;
        }

        private void UpdateWaitingLabel()
        {
            if (waitingText == null)
            {
                return;
            }

            if (_isHost)
            {
                waitingText.gameObject.SetActive(false);
                return;
            }

            waitingText.gameObject.SetActive(true);
            waitingText.text = _showPlaylist != null && _showPlaylist.HasQueue
                ? "Waiting for host to start the show"
                : "Waiting for songs to be queued";
        }
    }
}
