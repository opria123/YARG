using TMPro;
using UnityEngine;
using YARG.Core.Engine.Guitar;

namespace YARG.Menu.ScoreScreen
{
    public class GuitarScoreCard : ScoreCard<GuitarStats>
    {
        [Space]
        [SerializeField]
        private TextMeshProUGUI _overstrums;

        [SerializeField]
        private TextMeshProUGUI _hoposStrummed;

        [SerializeField]
        private TextMeshProUGUI _ghostInputs;

        public override bool SetCardContents()
        {
            if (!base.SetCardContents())
            {
                // Stats are null (remote player), placeholder was shown, skip guitar-specific stats
                return false;
            }

            _overstrums.text = WrapWithColor(Stats.Overstrums);
            _hoposStrummed.text = WrapWithColor(Stats.HoposStrummed);
            _ghostInputs.text = WrapWithColor(Stats.GhostInputs);
            return true;
        }
    }
}