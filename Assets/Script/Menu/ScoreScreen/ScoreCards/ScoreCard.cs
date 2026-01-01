using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.UI;
using YARG.Helpers.Extensions;
using YARG.Core;
using YARG.Core.Engine;
using YARG.Core.Extensions;
using YARG.Core.Game;
using YARG.Localization;
using YARG.Player;

namespace YARG.Menu.ScoreScreen
{
    public abstract class ScoreCard<T> : MonoBehaviour, IScoreCard<T> where T : BaseStats
    {
        [SerializeField]
        private ModifierIcon _modifierIconPrefab;

        [Space]
        [SerializeField]
        private TextMeshProUGUI _accuracyPercent;

        [Space]
        [SerializeField]
        private TextMeshProUGUI _playerName;
        [SerializeField]
        private TextMeshProUGUI _instrument;
        [SerializeField]
        private TextMeshProUGUI _difficulty;

        [Space]
        [SerializeField]
        private TextMeshProUGUI _score;
        [SerializeField]
        private StarView _starView;
        [SerializeField]
        private Transform _modifierIconContainer;

        [Space]
        [SerializeField]
        private Image _instrumentIcon;

        [Space]
        [SerializeField]
        private GameObject _tagGameObject;
        [SerializeField]
        private TextMeshProUGUI _tagText;

        [Space]
        [SerializeField]
        private ScrollRect _statsRect;

        [Space]
        [SerializeField]
        private TextMeshProUGUI _notesHit;
        [SerializeField]
        private TextMeshProUGUI _maxStreak;
        [SerializeField]
        private TextMeshProUGUI _notesMissed;
        [SerializeField]
        private TextMeshProUGUI _starpowerPhrases;
        [SerializeField]
        private TextMeshProUGUI _bandBonusScore;
        [SerializeField]
        private TextMeshProUGUI _averageOffset;

        private ScoreCardColorizer _colorizer;

        protected bool IsHighScore;
        protected T Stats;

        public YargPlayer Player { get; private set; }

        private void Awake()
        {
            _colorizer = GetComponent<ScoreCardColorizer>();
        }

        public void Initialize(bool isHighScore, YargPlayer player, T stats)
        {
            IsHighScore = isHighScore;
            Player = player;
            Stats = stats;
        }

        /// <summary>
        /// Sets the card contents. Returns true if stats were available, false if placeholder was shown.
        /// Derived classes should check the return value and skip their own stat display if false.
        /// </summary>
        public virtual bool SetCardContents()
        {
            _playerName.text = Player.Profile.Name;

            _instrument.text = Player.Profile.CurrentInstrument.ToLocalizedName();
            _difficulty.text = Player.Profile.CurrentDifficulty.ToDisplayName();

            // Handle null stats (remote players in band mode don't have local stats)
            if (Stats == null)
            {
                SetRemotePlayerCardContents();
                return false;
            }

            // Set percent
            _accuracyPercent.text = $"{Mathf.FloorToInt(Stats.Percent * 100f)}%";


            // Set background and foreground colors
            if (Player.Profile.IsBot)
            {
                _colorizer.SetCardColor(ScoreCardColorizer.ScoreCardColor.Gray);
                ShowTag("Bot");
            }
            else if (Player.IsReplay)
            {
                if (Stats.IsFullCombo)
                {
                    _colorizer.SetCardColor(ScoreCardColorizer.ScoreCardColor.Gold);
                }
                else
                {
                    _colorizer.SetCardColor(ScoreCardColorizer.ScoreCardColor.Blue);
                }

                ShowTag("Replay");
            }
            else if (Stats.IsFullCombo)
            {
                _colorizer.SetCardColor(ScoreCardColorizer.ScoreCardColor.Gold);
                ShowTag("Full Combo");
            }
            else if (IsHighScore)
            {
                _colorizer.SetCardColor(ScoreCardColorizer.ScoreCardColor.Blue);
                ShowTag("High Score");
            }
            else
            {
                _colorizer.SetCardColor(ScoreCardColorizer.ScoreCardColor.Blue);
                HideTag();
            }

            _score.text = Stats.TotalScore.ToString("N0");
            _starView.SetStars((int) Stats.Stars);

            _notesHit.text = $"{WrapWithColor(Stats.NotesHit)} / {Stats.TotalNotes}";
            _maxStreak.text = WrapWithColor(Stats.MaxCombo);
            _notesMissed.text = WrapWithColor(Stats.NotesMissed);
            _starpowerPhrases.text = $"{WrapWithColor(Stats.StarPowerPhrasesHit)} / {Stats.TotalStarPowerPhrases}";
            _bandBonusScore.text = WrapWithColor(Stats.BandBonusScore.ToString("N0"));
            _averageOffset.text = WrapWithColor(Mathf.RoundToInt((float)(Stats.GetAverageOffset() * 1000.0)).ToString() + " ms");

            // Set background icon
            _instrumentIcon.sprite = Addressables
                .LoadAssetAsync<Sprite>($"InstrumentIcons[{Player.Profile.CurrentInstrument.ToResourceName()}]")
                .WaitForCompletion();

            // Set engine preset icons
            ModifierIcon.SpawnEnginePresetIcons(_modifierIconPrefab, _modifierIconContainer,
                Player.EnginePreset, Player.Profile.GameMode);

            // Set modifier icons
            foreach (var modifier in EnumExtensions<Modifier>.Values)
            {
                if (modifier == Modifier.None) continue;

                if (!Player.Profile.IsModifierActive(modifier)) continue;

                var icon = Instantiate(_modifierIconPrefab, _modifierIconContainer);
                icon.InitializeForModifier(modifier);
            }
            
            return true;
        }

        /// <summary>
        /// Sets card contents for remote players who don't have local stats.
        /// Shows basic player info with placeholder values for stats.
        /// </summary>
        protected virtual void SetRemotePlayerCardContents()
        {
            // Set a gray color for players without stats
            _colorizer.SetCardColor(ScoreCardColorizer.ScoreCardColor.Gray);
            HideTag(); // No tag needed

            // Show placeholder text for stats
            _accuracyPercent.text = "---";
            _score.text = "---";
            _starView.SetStars(0);

            // Use gray for placeholder values
            string placeholder = "<color=#888888>---</color>";
            _notesHit.text = placeholder;
            _maxStreak.text = placeholder;
            _notesMissed.text = placeholder;
            _starpowerPhrases.text = placeholder;
            _bandBonusScore.text = placeholder;
            _averageOffset.text = placeholder;

            // Set background icon
            _instrumentIcon.sprite = Addressables
                .LoadAssetAsync<Sprite>($"InstrumentIcons[{Player.Profile.CurrentInstrument.ToResourceName()}]")
                .WaitForCompletion();
        }

        private void ShowTag(string tagText)
        {
            _tagGameObject.SetActive(true);
            _tagText.text = tagText;
        }

        private void HideTag()
        {
            _tagGameObject.SetActive(false);
        }

        protected string WrapWithColor(object s)
        {
            return
                $"<font-weight=700><color=#{ColorUtility.ToHtmlStringRGB(_colorizer.CurrentColor)}>" +
                $"{s}</color></font-weight>";
        }

        public void ScrollStats(float delta)
        {
            _statsRect.MoveVerticalInUnits(delta);
        }
    }

    public interface IScoreCard<out T> where T : BaseStats
    {
        YargPlayer Player { get; }
        void ScrollStats(float delta);
        bool SetCardContents();
    }
}