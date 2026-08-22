using MyLovelyMail.MainProject.Storage;

namespace MyLovelyMail.MainProject.Stores
{
    /// <summary>
    /// Registers, loads and saves all <see cref="Settings"/> fields against the single global
    /// config file in <see cref="AppPaths.UserData"/>. Per-account settings are handled by
    /// <see cref="AccountSettings"/>, which reuses the same registration and file format.
    /// </summary>
    public static class SettingsManager
    {
        public const string ConfigFileName = "config.txt";

        public static string ConfigPath => Path.Combine(AppPaths.UserData, ConfigFileName);

        static readonly Dictionary<string, ISettingSetup> iSettingSetups = [];
        static readonly Dictionary<string, ISetting> iSettings = [];

        public static ISetting[] GetAllSettings() => [.. iSettings.Values];

        static SettingsManager() =>
            SettingRegistration.RegisterFields(typeof(Settings), null, iSettingSetups, iSettings);

        /// <summary>Loads settings from the config file, creating it with defaults if missing.</summary>
        public static void LoadSettings()
        {
            if (!File.Exists(ConfigPath))
            {
                SaveSettings();
                return;
            }
            SettingsFile.Load(ConfigPath, iSettingSetups);
        }

        /// <summary>Serializes all settings to the single config file.</summary>
        public static void SaveSettings() =>
            SettingsFile.Save(ConfigPath, iSettingSetups.Values, "MyLovelyMail Configuration");

        /// <summary>Reset every registered setting to its shipped default and persist.</summary>
        public static void ResetAllToDefaults()
        {
            foreach (var s in iSettings.Values) s.ResetToDefault();
            SaveSettings();
        }

        /// <summary>Write a portable copy of the config (for the migration/export bundle).</summary>
        public static void ExportSettings(string path) =>
            SettingsFile.Save(path, iSettingSetups.Values, "MyLovelyMail settings export");

        /// <summary>Apply a previously-exported config file onto the live settings, then persist. Returns keys applied.</summary>
        public static int ImportSettings(string path)
        {
            int applied = SettingsFile.Load(path, iSettingSetups);
            if (applied > 0) SaveSettings();
            return applied;
        }
    }
}
