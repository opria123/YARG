using System;
using System.Collections.Generic;
using System.Linq;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using YARG.Networking.Abstraction;
using YARG.Networking.Bands;

namespace YARG.Gameplay.HUD
{
    /// <summary>
    /// Racing-style band leaderboard during multiplayer gameplay.
    /// Shows: Top 3 | gap | 1 above | YOU | 1 below
    /// Inspired by racing game leaderboards with animated position changes.
    /// </summary>
    public class BandLeaderboardHUD : GameplayBehaviour
    {
        private const int TOP_POSITIONS_COUNT = 3;
        private const int CONTEXT_POSITIONS = 1; // How many above/below player to show
        private const float POSITION_CHANGE_ANIM_DURATION = 0.3f;
        
        [Header("Panel References")]
        [SerializeField]
        private RectTransform _leaderboardPanel;
        
        [SerializeField]
        private CanvasGroup _canvasGroup;
        
        [Header("Containers")]
        [SerializeField]
        private RectTransform _topPositionsContainer;
        
        [SerializeField]
        private RectTransform _gapIndicator;
        
        [SerializeField]
        private RectTransform _contextContainer;
        
        [Header("Entry Prefab")]
        [SerializeField]
        private BandLeaderboardEntry _entryPrefab;
        
        [Header("Colors")]
        [SerializeField]
        private Color _localBandHighlightColor = new Color(0.9f, 0.2f, 0.2f, 1f);
        
        [SerializeField]
        private Color _localBandTextColor = Color.white;
        
        [SerializeField]
        private Color _otherBandTextColor = new Color(0.85f, 0.85f, 0.85f, 1f);
        
        [SerializeField]
        private Color _positionGainColor = new Color(0.2f, 0.8f, 0.2f, 1f);
        
        [SerializeField]
        private Color _positionLossColor = new Color(0.8f, 0.2f, 0.2f, 1f);
        
        [SerializeField]
        private Color _starPowerActiveColor = new Color(1f, 0.9f, 0.2f, 1f);
        
        private BandManager _bandManager;
        private bool _isActive;
        private float _updateInterval = 0.15f;
        private float _lastUpdateTime;
        private int _localBandId = -1;
        
        // Cached band data
        private readonly Dictionary<int, BandScoreData> _bandScores = new();
        private readonly List<BandScoreData> _sortedBands = new();
        
        // UI entries (pooled for performance)
        private readonly List<BandLeaderboardEntry> _topEntries = new();
        private readonly List<BandLeaderboardEntry> _contextEntries = new();
        
        // Track previous positions for animations
        private readonly Dictionary<int, int> _previousRanks = new();
        
        protected override void GameplayAwake()
        {
            _bandManager = BandManager.Instance;
            _isActive = _bandManager != null && _bandManager.IsBandSystemActive && _bandManager.Bands.Count > 1;
            
            if (_leaderboardPanel != null)
            {
                _leaderboardPanel.gameObject.SetActive(_isActive);
            }
            
            if (!_isActive)
            {
                Debug.Log("[BandLeaderboardHUD] Band system not active or only 1 band, hiding leaderboard");
                return;
            }
            
            _localBandId = _bandManager.LocalPlayerBandId;
            
            // Create entries from prefab
            CreateEntries();
            
            // Initialize band data
            InitializeBandData();
            
            // Subscribe to score updates
            _bandManager.OnBandScoreUpdated += OnBandScoreUpdated;
            
            Debug.Log($"[BandLeaderboardHUD] Initialized racing-style leaderboard for {_bandScores.Count} bands");
        }
        
        protected override void GameplayDestroy()
        {
            if (_bandManager != null)
            {
                _bandManager.OnBandScoreUpdated -= OnBandScoreUpdated;
            }
            
            // Clean up entries
            foreach (var entry in _topEntries)
            {
                entry?.Kill();
            }
            foreach (var entry in _contextEntries)
            {
                entry?.Kill();
            }
            _topEntries.Clear();
            _contextEntries.Clear();
        }
        
        private void CreateEntries()
        {
            if (_entryPrefab == null)
            {
                Debug.LogError("[BandLeaderboardHUD] Entry prefab not assigned!");
                _isActive = false; // Disable leaderboard when prefab is missing
                if (_leaderboardPanel != null)
                {
                    _leaderboardPanel.gameObject.SetActive(false);
                }
                return;
            }
            
            // Create pooled entries for top positions
            for (int i = 0; i < TOP_POSITIONS_COUNT; i++)
            {
                var entry = Instantiate(_entryPrefab, _topPositionsContainer);
                entry.Initialize(_localBandHighlightColor, _starPowerActiveColor);
                _topEntries.Add(entry);
            }
            
            // Create pooled entries for context (above + player + below)
            int contextCount = CONTEXT_POSITIONS * 2 + 1;
            for (int i = 0; i < contextCount; i++)
            {
                var entry = Instantiate(_entryPrefab, _contextContainer);
                entry.Initialize(_localBandHighlightColor, _starPowerActiveColor);
                _contextEntries.Add(entry);
            }
        }
        
        private void InitializeBandData()
        {
            _bandScores.Clear();
            _previousRanks.Clear();
            
            foreach (var kvp in _bandManager.Bands)
            {
                var bandInfo = kvp.Value;
                _bandScores[bandInfo.BandId] = new BandScoreData
                {
                    BandId = bandInfo.BandId,
                    BandName = bandInfo.DisplayName,
                    TotalScore = 0,
                    IsLocalBand = bandInfo.BandId == _localBandId
                };
                _previousRanks[bandInfo.BandId] = 0;
            }
        }
        
        private void Update()
        {
            if (!_isActive || GameManager == null)
                return;
            
            if (Time.time - _lastUpdateTime < _updateInterval)
                return;
            
            _lastUpdateTime = Time.time;
            
            UpdateLocalBandScore();
            UpdateRemoteBandScores();
            UpdateLeaderboardDisplay();
        }
        
        private void UpdateLocalBandScore()
        {
            if (_localBandId < 0)
                return;
            
            // When spectating, don't update local band score from GameManager.BandScore
            // because that now reflects the spectated band's players
            if (GameManager.PlayerHasFailed)
            {
                // Use the BandManager's cached score for the local band
                if (_bandManager.Bands.TryGetValue(_localBandId, out var bandInfo))
                {
                    if (_bandScores.TryGetValue(_localBandId, out var scoreData))
                    {
                        scoreData.TotalScore = bandInfo.TotalScore;
                        // Star power is definitely not active if we failed
                        scoreData.StarPowerActive = false;
                    }
                }
                return;
            }
            
            long localScore = GameManager.BandScore;
            
            if (_bandScores.TryGetValue(_localBandId, out var data))
            {
                data.TotalScore = localScore;
                data.StarPowerActive = IsAnyLocalPlayerStarPowerActive();
            }
        }
        
        private bool IsAnyLocalPlayerStarPowerActive()
        {
            var networkService = NetworkingServiceFactory.Instance;
            if (networkService == null)
                return false;
            
            var players = networkService.GetAllPlayers();
            foreach (var player in players)
            {
                if (player.IsLocalUser && player.IsStarPowerActive)
                    return true;
            }
            
            return false;
        }
        
        private void UpdateRemoteBandScores()
        {
            var networkService = NetworkingServiceFactory.Instance;
            if (networkService == null || !networkService.IsNetworkActive)
                return;
            
            if (_bandManager == null)
                return;
            
            var players = networkService.GetAllPlayers();
            var remoteBandScores = new Dictionary<int, long>();
            var bandStarPower = new Dictionary<int, bool>();
            
            foreach (var player in players)
            {
                if (player.IsLocalUser)
                    continue;
                
                int bandId = _bandManager.GetPlayerBandId(player.NetworkPlayerId);
                if (bandId < 0 || bandId == _localBandId)
                    continue;
                
                if (!remoteBandScores.ContainsKey(bandId))
                {
                    remoteBandScores[bandId] = 0;
                    bandStarPower[bandId] = false;
                }
                
                remoteBandScores[bandId] += player.CurrentScore;
                
                if (player.IsStarPowerActive)
                    bandStarPower[bandId] = true;
            }
            
            foreach (var kvp in remoteBandScores)
            {
                if (_bandScores.TryGetValue(kvp.Key, out var scoreData))
                {
                    scoreData.TotalScore = kvp.Value;
                    scoreData.StarPowerActive = bandStarPower.GetValueOrDefault(kvp.Key, false);
                }
            }
        }
        
        private void UpdateLeaderboardDisplay()
        {
            // Sort all bands by score descending
            _sortedBands.Clear();
            _sortedBands.AddRange(_bandScores.Values.OrderByDescending(b => b.TotalScore));
            
            // Assign ranks
            for (int i = 0; i < _sortedBands.Count; i++)
            {
                _sortedBands[i].CurrentRank = i + 1;
            }
            
            // Find local player's position
            int localPlayerRank = -1;
            for (int i = 0; i < _sortedBands.Count; i++)
            {
                if (_sortedBands[i].IsLocalBand)
                {
                    localPlayerRank = i;
                    break;
                }
            }
            
            // Update TOP 3 entries
            UpdateTopPositions();
            
            // Determine if we need the gap and context section
            bool localInTop3 = localPlayerRank >= 0 && localPlayerRank < TOP_POSITIONS_COUNT;
            bool showContext = !localInTop3 && localPlayerRank >= 0;
            
            // Show/hide gap indicator
            if (_gapIndicator != null)
            {
                _gapIndicator.gameObject.SetActive(showContext && localPlayerRank > TOP_POSITIONS_COUNT);
            }
            
            // Update context section (around local player)
            if (showContext)
            {
                UpdateContextPositions(localPlayerRank);
                _contextContainer.gameObject.SetActive(true);
            }
            else
            {
                _contextContainer.gameObject.SetActive(false);
            }
            
            // Check for position changes and animate
            CheckPositionChanges();
        }
        
        private void UpdateTopPositions()
        {
            for (int i = 0; i < _topEntries.Count; i++)
            {
                var entry = _topEntries[i];
                
                if (i < _sortedBands.Count)
                {
                    var band = _sortedBands[i];
                    entry.gameObject.SetActive(true);
                    entry.SetBandId(band.BandId);
                    entry.SetData(
                        band.CurrentRank,
                        band.BandName,
                        band.TotalScore,
                        band.IsLocalBand,
                        band.StarPowerActive,
                        band.IsLocalBand ? _localBandTextColor : _otherBandTextColor,
                        _starPowerActiveColor
                    );
                }
                else
                {
                    entry.gameObject.SetActive(false);
                }
            }
        }
        
        private void UpdateContextPositions(int localPlayerRank)
        {
            // Context entries: [1 above] [LOCAL] [1 below]
            int startIndex = Math.Max(TOP_POSITIONS_COUNT, localPlayerRank - CONTEXT_POSITIONS);
            int endIndex = Math.Min(_sortedBands.Count - 1, localPlayerRank + CONTEXT_POSITIONS);
            
            int entryIndex = 0;
            
            for (int i = startIndex; i <= endIndex && entryIndex < _contextEntries.Count; i++)
            {
                var entry = _contextEntries[entryIndex];
                var band = _sortedBands[i];
                
                entry.gameObject.SetActive(true);
                entry.SetBandId(band.BandId);
                entry.SetData(
                    band.CurrentRank,
                    band.BandName,
                    band.TotalScore,
                    band.IsLocalBand,
                    band.StarPowerActive,
                    band.IsLocalBand ? _localBandTextColor : _otherBandTextColor,
                    _starPowerActiveColor
                );
                
                // Show score difference for non-local bands
                if (!band.IsLocalBand && localPlayerRank >= 0 && localPlayerRank < _sortedBands.Count)
                {
                    long localScore = _sortedBands[localPlayerRank].TotalScore;
                    long diff = band.TotalScore - localScore;
                    bool isAhead = diff > 0;
                    entry.ShowScoreDifference(diff, isAhead ? _positionLossColor : _positionGainColor);
                }
                else
                {
                    entry.HideScoreDifference();
                }
                
                entryIndex++;
            }
            
            // Hide unused entries
            for (; entryIndex < _contextEntries.Count; entryIndex++)
            {
                _contextEntries[entryIndex].gameObject.SetActive(false);
            }
        }
        
        private void CheckPositionChanges()
        {
            foreach (var band in _sortedBands)
            {
                int prevRank = _previousRanks.GetValueOrDefault(band.BandId, band.CurrentRank);
                int currentRank = band.CurrentRank;
                
                if (prevRank != currentRank && prevRank > 0)
                {
                    // Position changed - find the entry and animate
                    var entry = FindEntryForBand(band.BandId);
                    if (entry != null)
                    {
                        bool gainedPosition = currentRank < prevRank;
                        entry.AnimatePositionChange(
                            gainedPosition,
                            gainedPosition ? _positionGainColor : _positionLossColor,
                            POSITION_CHANGE_ANIM_DURATION
                        );
                    }
                }
                
                _previousRanks[band.BandId] = currentRank;
            }
        }
        
        private BandLeaderboardEntry FindEntryForBand(int bandId)
        {
            // Check top entries
            foreach (var entry in _topEntries)
            {
                if (entry.CurrentBandId == bandId)
                    return entry;
            }
            
            // Check context entries
            foreach (var entry in _contextEntries)
            {
                if (entry.CurrentBandId == bandId)
                    return entry;
            }
            
            return null;
        }
        
        private void OnBandScoreUpdated(int bandId, long totalScore)
        {
            if (_bandScores.TryGetValue(bandId, out var scoreData))
            {
                scoreData.TotalScore = totalScore;
            }
        }
        
        public void UpdateBandScoreFromNetwork(int bandId, long score, bool starPowerActive)
        {
            if (_bandScores.TryGetValue(bandId, out var scoreData))
            {
                scoreData.TotalScore = score;
                scoreData.StarPowerActive = starPowerActive;
            }
        }
        
        private class BandScoreData
        {
            public int BandId;
            public string BandName;
            public long TotalScore;
            public bool IsLocalBand;
            public bool StarPowerActive;
            public int CurrentRank;
        }
    }
}
