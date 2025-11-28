using System;

namespace YARG.Settings.Types
{
    /// <summary>
    /// A simple text/string setting with no validation.
    /// </summary>
    public class TextSetting : AbstractSetting<string>
    {
        public override string AddressableName => "Setting/Text";

        public TextSetting(string value, Action<string>? onChange = null) : base(onChange)
        {
            _value = value ?? string.Empty;
        }

        protected override void SetValue(string value)
        {
            _value = value ?? string.Empty;
        }

        public override bool ValueEquals(string value)
        {
            return string.Equals(value, Value, StringComparison.Ordinal);
        }
    }
}
