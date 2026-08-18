using System.Text.Json;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Constants.ThemeConstants;

namespace MyLovelyMail.MainProject.Stores
{
    /// <summary>
    /// Maps tag names to their chip colors, persisted as <c>UserData/tags.json</c>. Tags stay
    /// plain strings on messages (RuleEngine and search keep working); this store only owns the
    /// visual identity. Unknown tags get the next pastel color automatically on first use.
    /// </summary>
    public static class TagStore
    {
        public const string TagsFileName = "tags.json";

        /// <summary>Chip colors handed to new tags round-robin; shared with account dots via <see cref="AppColors.IdentityPalette"/>.</summary>
        static string[] TagPalette => AppColors.IdentityPalette;

        static Dictionary<string, string> colorByName = new(StringComparer.OrdinalIgnoreCase);

        public static event Action? OnTagsChanged;

        static string TagsPath => Path.Combine(AppPaths.UserData, TagsFileName);

        public static IReadOnlyCollection<string> AllTags => colorByName.Keys;

        public static void Load()
        {
            colorByName = new(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(TagsPath)) return;
            try
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(TagsPath));
                if (loaded != null)
                    colorByName = new(loaded, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Log($"Corrupt tags file: {ex.Message}", LogLevel.Error);
            }
        }

        /// <summary>The tag's chip color, registering the tag with the next palette color when unknown.</summary>
        public static string ColorOf(string tagName)
        {
            if (colorByName.TryGetValue(tagName, out var color)) return color;
            color = TagPalette[colorByName.Count % TagPalette.Length];
            colorByName[tagName] = color;
            Persist();
            return color;
        }

        public static void Remove(string tagName)
        {
            if (colorByName.Remove(tagName))
                Persist();
        }

        static void Persist()
        {
            AtomicFile.WriteAllText(TagsPath, JsonSerializer.Serialize(colorByName, JsonDefaults.Indented));
            OnTagsChanged?.Invoke();
        }
    }
}
