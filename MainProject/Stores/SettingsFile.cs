using System.Text;

namespace MyLovelyMail.MainProject.Stores
{
    /// <summary>
    /// The single "key = value" text format shared by the global config and every per-account
    /// settings file. Reading and writing live here so the two stores can never drift apart.
    /// </summary>
    public static class SettingsFile
    {
        public const string CommentPrefix = "#";
        public const string KeyValueSeparator = "=";

        /// <summary>
        /// Applies every "key = value" line of <paramref name="path"/> onto the matching entry of
        /// <paramref name="settings"/>. Missing file or unknown keys are silently skipped, so old
        /// configs keep loading after settings are added or removed. Returns the number of keys applied.
        /// </summary>
        public static int Load(string path, IReadOnlyDictionary<string, ISettingSetup> settings)
        {
            if (!File.Exists(path)) return 0;

            int applied = 0;
            foreach (var line in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith(CommentPrefix)) continue;

                // Split on the FIRST '=' only, so '=' characters inside values survive.
                var parts = line.Split(KeyValueSeparator, 2);
                if (parts.Length != 2) continue;

                if (settings.TryGetValue(parts[0].Trim(), out var setting))
                {
                    setting.LoadFromStr(parts[1].Trim());
                    applied++;
                }
            }
            return applied;
        }

        /// <summary>Serializes the given settings into the shared text format and writes them atomically.</summary>
        public static void Save(string path, IEnumerable<ISettingSetup> settings, string headerTitle)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{CommentPrefix} {headerTitle}");
            sb.AppendLine($"{CommentPrefix} Generated {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            foreach (var setting in settings)
                sb.AppendLine($"{setting.Key} {KeyValueSeparator} {setting.Serialize()}");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                AtomicFile.WriteAllText(path, sb.ToString());
            }
            catch (Exception ex)
            {
                Log($"Failed to save settings to '{path}': {ex}", LogLevel.Error);
            }
        }
    }
}
