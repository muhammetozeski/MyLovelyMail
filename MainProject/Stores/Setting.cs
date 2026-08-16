using System.Text.RegularExpressions;

namespace MyLovelyMail.MainProject.Stores
{
    /// <summary>Read-only view of a setting (value as object, plus its keys).</summary>
    public interface ISetting
    {
        string Key { get; }
        string KeyHumanReadable { get; }
        object Value { get; }
        object DefaultValue { get; }
        bool IsDefault { get; }
        void ResetToDefault();
    }

    /// <summary>Setup contract used exclusively by the settings stores (key assignment + persistence).</summary>
    public interface ISettingSetup
    {
        string Key { get; }
        void InitializeKey(string key);
        void LoadFromStr(string value);
        string Serialize();
    }

    /// <summary>
    /// A strongly-typed configuration setting. The key is assigned by the owning store from the
    /// field name via reflection, so a setting is declared as a single field with a default value.
    /// </summary>
    public class Setting<T>(T defaultValue) : ISetting, ISettingSetup
    {
        string _key = string.Empty;

        public string Key
        {
            get => _key;
            private set
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    _key = value;
                    KeyHumanReadable = SplitCamelCase(value);
                }
            }
        }

        public string KeyHumanReadable { get; private set; } = string.Empty;

        public T Value = defaultValue;
        public readonly T DefaultValue = defaultValue;

        /// <summary>Raised after <see cref="Value"/> is replaced through <see cref="Set"/> (UI refresh hook).</summary>
        public event Action<T>? OnChanged;

        object ISetting.Value => Value!;
        object ISetting.DefaultValue => DefaultValue!;
        bool ISetting.IsDefault => EqualityComparer<T>.Default.Equals(Value, DefaultValue);
        void ISetting.ResetToDefault() => Set(DefaultValue);

        /// <summary>Replaces the value and raises <see cref="OnChanged"/> when it actually differs.</summary>
        public void Set(T newValue)
        {
            if (EqualityComparer<T>.Default.Equals(Value, newValue)) return;
            Value = newValue;
            OnChanged?.Invoke(newValue);
        }

        void ISettingSetup.InitializeKey(string key)
        {
            if (!string.IsNullOrEmpty(Key))
                throw new InvalidOperationException($"Key already initialized to '{Key}'. Cannot re-assign to '{key}'.");
            Key = key;
        }

        void ISettingSetup.LoadFromStr(string value) => Value = ParseOrDefault(value, DefaultValue, Key);

        string ISettingSetup.Serialize() => SerializeValue(Value);

        public static implicit operator T(Setting<T> setting) => setting.Value;

        internal static string SerializeValue(T? value) => value switch
        {
            bool b => b.ToString(),
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => value?.ToString() ?? string.Empty
        };

        internal static T ParseOrDefault(string text, T fallback, string keyForLog)
        {
            try
            {
                if (typeof(T).IsEnum)
                    return (T)Enum.Parse(typeof(T), text, ignoreCase: true);
                return (T)Convert.ChangeType(text, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                Log($"Config: invalid {typeof(T).Name} for '{keyForLog}': '{text}'. Using default.", LogLevel.Warning);
                return fallback;
            }
        }

        internal static string SplitCamelCase(string value) =>
            Regex.Replace(value, "(?<=[a-z0-9])(?=[A-Z])", " ");
    }

    /// <summary>
    /// A per-scope (e.g. per-account) setting that inherits its value from a parent
    /// <see cref="Setting{T}"/> until explicitly overridden. While not overridden, reads always
    /// flow to the parent LIVE, so changing the global value changes every non-overridden child;
    /// once overridden, the child keeps its own value and ignores the parent until
    /// <see cref="ClearOverride"/>.
    /// </summary>
    public class InheritedSetting<T>(Setting<T> parent) : ISetting, ISettingSetup
    {
        public readonly Setting<T> Parent = parent;

        string _key = string.Empty;
        T _overrideValue = parent.DefaultValue;

        public bool IsOverridden { get; private set; }

        public string Key
        {
            get => _key;
            private set
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    _key = value;
                    KeyHumanReadable = Setting<T>.SplitCamelCase(value);
                }
            }
        }

        public string KeyHumanReadable { get; private set; } = string.Empty;

        public T Value
        {
            get => IsOverridden ? _overrideValue : Parent.Value;
            set
            {
                _overrideValue = value;
                IsOverridden = true;
            }
        }

        /// <summary>Drops the override so the setting follows the parent again.</summary>
        public void ClearOverride()
        {
            IsOverridden = false;
            _overrideValue = Parent.DefaultValue;
        }

        object ISetting.Value => Value!;
        object ISetting.DefaultValue => Parent.DefaultValue!;
        bool ISetting.IsDefault => !IsOverridden;
        void ISetting.ResetToDefault() => ClearOverride();

        void ISettingSetup.InitializeKey(string key)
        {
            if (!string.IsNullOrEmpty(Key))
                throw new InvalidOperationException($"Key already initialized to '{Key}'. Cannot re-assign to '{key}'.");
            Key = key;
        }

        void ISettingSetup.LoadFromStr(string value) =>
            Value = Setting<T>.ParseOrDefault(value, Parent.DefaultValue, Key);

        string ISettingSetup.Serialize() => Setting<T>.SerializeValue(Value);

        public static implicit operator T(InheritedSetting<T> setting) => setting.Value;
    }
}
