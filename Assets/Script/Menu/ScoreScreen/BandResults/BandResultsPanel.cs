using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using DG.Tweening;
using YARG.Core.Engine;
using YARG.Core.Song;
using YARG.Networking.Bands;
using YARG.Player;
using YARG.Menu.Multiplayer;

namespace YARG.Menu.ScoreScreen
{
    /// <summary>
    /// Panel displaying band results comparison.
    /// Shows winner at top, band list for selection, and member score cards for selected band.
    /// </summary>
    public class BandResultsPanel : MonoBehaviour
    {
        [Header("Winner Display")]
        [SerializeField]
        private GameObject _winnerCard;
        
        [SerializeField]
        private TextMeshProUGUI _winnerBandNameText;
        
        [SerializeField]
        private TextMeshProUGUI _winnerScoreText;
        
        [SerializeField]
        private StarView _winnerStarView;
        
        [SerializeField]
        private TextMeshProUGUI _winnerPlayerCountText;
        
        [Header("Header")]
        [SerializeField]
        private TextMeshProUGUI _headerText;
        
        [Header("Your Band Position Tag")]
        [SerializeField]
        private GameObject _yourBandTag;
        
        [SerializeField]
        private TextMeshProUGUI _yourBandTagText;
        
        [Header("List Container")]
        [SerializeField]
        private RectTransform _bandListContainer;
        
        [SerializeField]
        private ScrollRect _scrollRect;
        
        [Header("Prefab")]
        [SerializeField]
        private BandResultEntry _entryPrefab;
        
        [Header("Animation Settings")]
        [SerializeField]
        private float _revealDelayBetweenEntries = 0.15f;
        
        [SerializeField]
        private float _firstPlaceShakeIntensity = 1.0f;
        
        [SerializeField]
        private float _secondPlaceShakeIntensity = 0.6f;
        
        [SerializeField]
        private float _thirdPlaceShakeIntensity = 0.3f;
        
        [Header("Selected Band Display")]
        [SerializeField]
        private TextMeshProUGUI _selectedBandNameText;
        
        private BandResultsData _resultsData;
        private readonly List<BandResultEntry> _entries = new();
        private BandResult _selectedBand;
        private BandResultEntry _selectedEntry;
        
        /// <summary>
        /// Event fired when a band is selected to show member details.
        /// </summary>
        public event Action<BandResult> OnBandSelected;
        
        /// <summary>
        /// The currently selected band.
        /// </summary>
        public BandResult SelectedBand => _selectedBand;
        
        /// <summary>
        /// Initializes the panel with band results data.
        /// </summary>
        public void Initialize(BandResultsData resultsData)
        {
            _resultsData = resultsData;
            _selectedBand = null;
            _selectedEntry = null;
            
            // Clear any existing entries
            ClearEntries();
            
            if (!resultsData.HasResults)
            {
                Debug.LogWarning("[BandResultsPanel] No band results to display");
                return;
            }
            
            // Setup winner display (1st place band)
            SetupWinnerDisplay(resultsData.BandResults[0]);
            
            // Setup your band position tag
            SetupYourBandTag(resultsData);
            
            // Create entries for each band
            foreach (var bandResult in resultsData.BandResults)
            {
                CreateEntry(bandResult);
            }
            
            // Update header
            if (_headerText != null)
            {
                _headerText.text = "BAND STANDINGS";
            }
            
            // Select local band by default and show their member cards
            SelectLocalBandByDefault();
        }
        
        private void SetupWinnerDisplay(BandResult winner)
        {
            if (_winnerCard != null)
            {
                _winnerCard.SetActive(true);
            }
            
            if (_winnerBandNameText != null)
            {
                _winnerBandNameText.text = winner.BandName;
            }
            
            if (_winnerScoreText != null)
            {
                _winnerScoreText.text = winner.TotalScore.ToString("N0");
            }
            
            if (_winnerStarView != null)
            {
                _winnerStarView.SetStars(winner.StarRating);
            }
            
            if (_winnerPlayerCountText != null)
            {
                _winnerPlayerCountText.text = $"{winner.PlayerIds.Count} player{(winner.PlayerIds.Count != 1 ? "s" : "")}";
            }
        }
        
        private void SetupYourBandTag(BandResultsData resultsData)
        {
            var localBand = resultsData.BandResults.FirstOrDefault(b => b.IsLocalBand);
            
            if (localBand == null)
            {
                if (_yourBandTag != null)
                {
                    _yourBandTag.SetActive(false);
                }
                return;
            }
            
            if (_yourBandTag != null)
            {
                _yourBandTag.SetActive(true);
            }
            
            if (_yourBandTagText != null)
            {
                string positionText = localBand.Position switch
                {
                    1 => "1ST PLACE",
                    2 => "2ND PLACE",
                    3 => "3RD PLACE",
                    _ => $"{localBand.Position}TH PLACE"
                };
                _yourBandTagText.text = positionText;
            }
        }
        
        private void SelectLocalBandByDefault()
        {
            Debug.Log($"[BandResultsPanel] SelectLocalBandByDefault: resultsData={_resultsData != null}, " +
                      $"bandCount={_resultsData?.BandResults?.Count ?? 0}, entriesCount={_entries.Count}");
            
            // Find local band and select it
            var localBandResult = _resultsData?.BandResults?.FirstOrDefault(b => b.IsLocalBand);
            
            if (localBandResult != null)
            {
                Debug.Log($"[BandResultsPanel] Found local band: {localBandResult.BandName} (BandId={localBandResult.BandId})");
                
                // Find the entry for this band
                var localEntry = _entries.FirstOrDefault(e => e.BandResult.BandId == localBandResult.BandId);
                
                if (localEntry != null)
                {
                    Debug.Log($"[BandResultsPanel] Found entry for local band, selecting...");
                    SelectBand(localEntry, localBandResult);
                }
                else
                {
                    Debug.LogWarning($"[BandResultsPanel] No entry found for local band {localBandResult.BandName}");
                }
            }
            else if (_entries.Count > 0)
            {
                // Fallback: select first band
                Debug.Log($"[BandResultsPanel] No local band found, selecting first band: {_entries[0].BandResult.BandName}");
                SelectBand(_entries[0], _entries[0].BandResult);
            }
            else
            {
                Debug.LogWarning("[BandResultsPanel] No bands to select!");
            }
        }
        
        private void SelectBand(BandResultEntry entry, BandResult result)
        {
            Debug.Log($"[BandResultsPanel] SelectBand called: {result.BandName}, memberCount={result.MemberScoreCards.Count}");
            
            // Deselect previous
            if (_selectedEntry != null)
            {
                _selectedEntry.SetSelected(false);
            }
            
            // Select new
            _selectedEntry = entry;
            _selectedBand = result;
            entry.SetSelected(true);
            
            // Update selected band name display
            if (_selectedBandNameText != null)
            {
                _selectedBandNameText.gameObject.SetActive(true);
                string positionText = result.Position switch
                {
                    1 => "1ST",
                    2 => "2ND",
                    3 => "3RD",
                    _ => $"{result.Position}TH"
                };
                _selectedBandNameText.text = $"{result.BandName} - {positionText} - {result.TotalScore:N0}";
                Debug.Log($"[BandResultsPanel] Updated selected band text: {_selectedBandNameText.text}");
            }
            else
            {
                Debug.LogWarning("[BandResultsPanel] _selectedBandNameText is null!");
            }
            
            // Fire event to update member cards
            Debug.Log($"[BandResultsPanel] Firing OnBandSelected event with {result.MemberScoreCards.Count} member cards");
            OnBandSelected?.Invoke(result);
        }
        
        private void CreateEntry(BandResult result)
        {
            if (_entryPrefab == null || _bandListContainer == null)
            {
                Debug.LogError("[BandResultsPanel] Missing prefab or container reference");
                return;
            }
            
            var entry = Instantiate(_entryPrefab, _bandListContainer);
            entry.Initialize(result);
            entry.OnClicked += HandleEntryClicked;
            
            _entries.Add(entry);
        }
        
        private void ClearEntries()
        {
            foreach (var entry in _entries)
            {
                if (entry != null)
                {
                    entry.OnClicked -= HandleEntryClicked;
                    Destroy(entry.gameObject);
                }
            }
            _entries.Clear();
        }
        
        /// <summary>
        /// Plays the reveal animation for all band entries.
        /// </summary>
        public void PlayRevealAnimation()
        {
            StartCoroutine(RevealEntriesCoroutine());
        }
        
        private IEnumerator RevealEntriesCoroutine()
        {
            // Small initial delay
            yield return new WaitForSeconds(0.3f);
            
            for (int i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                var position = entry.BandResult.Position;
                
                // Calculate screen shake intensity based on position
                float shakeIntensity = position switch
                {
                    1 => _firstPlaceShakeIntensity,
                    2 => _secondPlaceShakeIntensity,
                    3 => _thirdPlaceShakeIntensity,
                    _ => 0f
                };
                
                // Calculate delay - faster for lower positions
                float delay = position <= 3 ? _revealDelayBetweenEntries : _revealDelayBetweenEntries * 0.5f;
                
                entry.PlayRevealAnimation(0f, shakeIntensity);
                
                yield return new WaitForSeconds(delay);
            }
        }
        
        private void HandleEntryClicked(BandResult result)
        {
            Debug.Log($"[BandResultsPanel] Band clicked: {result.BandName}");
            
            // Find the entry that was clicked
            var clickedEntry = _entries.FirstOrDefault(e => e.BandResult.BandId == result.BandId);
            
            if (clickedEntry != null)
            {
                SelectBand(clickedEntry, result);
            }
        }
        
        private void OnDestroy()
        {
            ClearEntries();
        }
        
        #region Static Helpers
        
        /// <summary>
        /// Creates band results data from the current game state.
        /// </summary>
        public static BandResultsData CreateFromGameState(
            BandManager bandManager,
            ScoreScreenStats scoreScreenStats,
            SongEntry songEntry)
        {
            var resultsData = new BandResultsData();
            
            if (bandManager == null || !bandManager.IsBandSystemActive)
            {
                return resultsData;
            }
            
            resultsData.LocalBandId = bandManager.LocalPlayerBandId;
            
            // Get all bands and their scores
            var bandScores = new List<(BandInfo band, long score)>();
            
            foreach (var band in bandManager.Bands.Values)
            {
                bandScores.Add((band, band.TotalScore));
                Debug.Log($"[BandResultsPanel] Band '{band.DisplayName}' (ID={band.BandId}) - TotalScore={band.TotalScore}, HasFailed={band.HasFailed}");
            }
            
            // Sort by score descending
            bandScores.Sort((a, b) => b.score.CompareTo(a.score));
            
            Debug.Log($"[BandResultsPanel] Band order after sorting by score (descending):");
            for (int idx = 0; idx < bandScores.Count; idx++)
            {
                Debug.Log($"[BandResultsPanel]   Position {idx + 1}: '{bandScores[idx].band.DisplayName}' - Score={bandScores[idx].score}");
            }
            
            // Create band results
            for (int i = 0; i < bandScores.Count; i++)
            {
                var (band, score) = bandScores[i];
                var position = i + 1;
                
                // Calculate margin to next
                long marginToNext = 0;
                if (i < bandScores.Count - 1)
                {
                    marginToNext = score - bandScores[i + 1].score;
                }
                
                // Star rating will be calculated after collecting member cards
                var bandResult = new BandResult
                {
                    BandId = band.BandId,
                    BandName = band.DisplayName,
                    Position = position,
                    TotalScore = score,
                    StarRating = 0, // Will be set after collecting members
                    HasFailed = band.HasFailed,
                    MarginToNext = marginToNext,
                    IsLocalBand = band.BandId == resultsData.LocalBandId
                };
                
                // Copy player IDs
                bandResult.PlayerIds.AddRange(band.PlayerIds);
                
                Debug.Log($"[BandResultsPanel] Band '{band.DisplayName}' has {band.PlayerIds.Count} player IDs: [{string.Join(", ", band.PlayerIds)}]");
                Debug.Log($"[BandResultsPanel] ScoreScreenStats has {scoreScreenStats.PlayerScores.Length} player scores");
                
                // Find player score cards for this band
                // Note: band.PlayerIds contains NetworkPlayerIds, not ProfileIds
                // We need to map each player in ScoreScreenStats to their NetworkPlayerId
                // 
                // In band mode, ScoreScreenStats only contains players from the LOCAL band.
                // For remote band players, we create placeholder entries using NetworkPlayerData.
                foreach (var playerId in band.PlayerIds)
                {
                    Debug.Log($"[BandResultsPanel] Looking for NetworkPlayerId: {playerId}");
                    
                    PlayerScoreCard? matchedCard = null;
                    
                    // Try to match by NetworkPlayerId (requires lookup via MultiplayerPlayerManager)
                    foreach (var ps in scoreScreenStats.PlayerScores)
                    {
                        if (ps.Player == null) continue;
                        
                        // Get this player's NetworkPlayerId from the multiplayer manager
                        if (MultiplayerPlayerManager.TryGetNetworkPlayer(ps.Player, out var networkData))
                        {
                            Debug.Log($"[BandResultsPanel]   - Player '{ps.Player.Profile?.Name}' has NetworkPlayerId: {networkData.NetworkPlayerId}");
                            if (networkData.NetworkPlayerId == playerId)
                            {
                                Debug.Log($"[BandResultsPanel]   -> Found match by NetworkPlayerId!");
                                matchedCard = ps;
                                break;
                            }
                        }
                        else
                        {
                            Debug.Log($"[BandResultsPanel]   - Player '{ps.Player.Profile?.Name}' has no network data mapping");
                        }
                    }
                    
                    if (matchedCard.HasValue && matchedCard.Value.Player != null)
                    {
                        Debug.Log($"[BandResultsPanel]   -> Adding member: {matchedCard.Value.Player.Profile.Name}");
                        bandResult.MemberScoreCards.Add(matchedCard.Value);
                    }
                    else
                    {
                        // For remote band players, create a placeholder card using NetworkPlayerData
                        // This handles the case where the player is from another band and wasn't simulated locally
                        Debug.Log($"[BandResultsPanel]   -> No local match found for NetworkPlayerId {playerId}, checking for remote player data...");
                        
                        if (MultiplayerPlayerManager.TryGetNetworkPlayerData(playerId, out var remotePlayerData))
                        {
                            Debug.Log($"[BandResultsPanel]   -> Found remote player data: {remotePlayerData.PlayerName}");
                            
                            // Create a placeholder YargPlayer from the network data
                            var placeholderPlayer = MultiplayerPlayerManager.CreatePlaceholderPlayer(remotePlayerData);
                            if (placeholderPlayer != null)
                            {
                                // Create stats from the network data so we can show actual scores
                                var remoteStats = MultiplayerPlayerManager.CreateStatsFromNetworkData(remotePlayerData);
                                
                                var placeholderCard = new PlayerScoreCard
                                {
                                    IsHighScore = false, // Unknown for remote players
                                    Player = placeholderPlayer,
                                    Stats = remoteStats // Use stats from network data
                                };
                                
                                Debug.Log($"[BandResultsPanel]   -> Adding remote member: {placeholderPlayer.Profile.Name} " +
                                          $"(Score={remoteStats?.CommittedScore ?? 0}, NotesHit={remoteStats?.NotesHit ?? 0})");
                                bandResult.MemberScoreCards.Add(placeholderCard);
                            }
                        }
                        else
                        {
                            Debug.LogWarning($"[BandResultsPanel]   -> No match found for NetworkPlayerId {playerId}");
                        }
                    }
                }
                
                Debug.Log($"[BandResultsPanel] Band '{band.DisplayName}' populated with {bandResult.MemberScoreCards.Count} member score cards");
                
                // Calculate star rating from actual member stats (average of member stars)
                // This matches how BandStars is calculated in GameManager
                if (bandResult.MemberScoreCards.Count > 0)
                {
                    float totalStars = 0f;
                    foreach (var memberCard in bandResult.MemberScoreCards)
                    {
                        if (memberCard.Stats != null)
                        {
                            totalStars += memberCard.Stats.Stars;
                        }
                    }
                    bandResult.StarRating = (int)(totalStars / bandResult.MemberScoreCards.Count);
                    Debug.Log($"[BandResultsPanel] Band '{band.DisplayName}' star rating: {bandResult.StarRating} (from {totalStars} total / {bandResult.MemberScoreCards.Count} members)");
                }
                
                resultsData.BandResults.Add(bandResult);
            }
            
            return resultsData;
        }
        
        #endregion
    }
}
