using System;
using System.Collections.Generic;
using YARG.Networking.Bands;

namespace YARG.Menu.ScoreScreen
{
    /// <summary>
    /// Data structure for end-of-song band results.
    /// </summary>
    public class BandResultsData
    {
        /// <summary>
        /// Results for each band, sorted by score (highest first).
        /// </summary>
        public List<BandResult> BandResults { get; } = new();
        
        /// <summary>
        /// The local player's band ID.
        /// </summary>
        public int LocalBandId { get; set; } = -1;
        
        /// <summary>
        /// Whether band results are available.
        /// </summary>
        public bool HasResults => BandResults.Count > 0;
    }
    
    /// <summary>
    /// Result data for a single band.
    /// </summary>
    public class BandResult
    {
        /// <summary>
        /// Band identifier.
        /// </summary>
        public int BandId { get; set; }
        
        /// <summary>
        /// Display name of the band (e.g., "Electric Gorillas").
        /// </summary>
        public string BandName { get; set; }
        
        /// <summary>
        /// Final position (1 = 1st place, 2 = 2nd place, etc.)
        /// </summary>
        public int Position { get; set; }
        
        /// <summary>
        /// Total band score.
        /// </summary>
        public long TotalScore { get; set; }
        
        /// <summary>
        /// Star rating for the band (0-6, calculated from band score).
        /// </summary>
        public int StarRating { get; set; }
        
        /// <summary>
        /// Whether this band failed during gameplay.
        /// </summary>
        public bool HasFailed { get; set; }
        
        /// <summary>
        /// Score margin to the next band below (0 if last place).
        /// </summary>
        public long MarginToNext { get; set; }
        
        /// <summary>
        /// Whether this is the local player's band.
        /// </summary>
        public bool IsLocalBand { get; set; }
        
        /// <summary>
        /// Player score cards for members of this band.
        /// Populated when drilling down into the band.
        /// </summary>
        public List<PlayerScoreCard> MemberScoreCards { get; } = new();
        
        /// <summary>
        /// Player IDs in this band.
        /// </summary>
        public List<Guid> PlayerIds { get; } = new();
    }
}
