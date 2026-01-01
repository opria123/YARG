using TMPro;
using UnityEngine;
using YARG.Core.Engine.Keys;

namespace YARG.Menu.ScoreScreen
{
    public class ProKeysScoreCard : ScoreCard<KeysStats>
    {
        [Space]
        [SerializeField]
        private TextMeshProUGUI _overhits;

        public override bool SetCardContents()
        {
            if (!base.SetCardContents())
            {
                // Stats are null (remote player), placeholder was shown, skip keys-specific stats
                return false;
            }

            _overhits.text = WrapWithColor(Stats.Overhits);
            return true;
        }
    }
}