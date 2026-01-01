using TMPro;
using UnityEngine;
using YARG.Core.Engine.Drums;

namespace YARG.Menu.ScoreScreen
{
    public class DrumsScoreCard : ScoreCard<DrumsStats>
    {
        [Space]
        [SerializeField]
        private TextMeshProUGUI _overhits;

        public override bool SetCardContents()
        {
            if (!base.SetCardContents())
            {
                // Stats are null (remote player), placeholder was shown, skip drums-specific stats
                return false;
            }

            _overhits.text = WrapWithColor(Stats.Overhits);
            return true;
        }
    }
}