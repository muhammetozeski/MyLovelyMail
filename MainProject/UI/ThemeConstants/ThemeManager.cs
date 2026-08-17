namespace MyLovelyMail.MainProject.Constants.ThemeConstants
{
    /// <summary>
    /// Single source of truth for the active <see cref="AppTheme"/>. Holds the running palette and
    /// notifies the UI when it swaps so every render reflects the new colors immediately.
    /// </summary>
    public static class ThemeManager
    {
        public static AppTheme Current { get; private set; } = AppThemes.LovelyBloom;

        /// <summary>
        /// Replace the active palette. Fires <see cref="Events.MainEvents.OnDataChanged"/> which the
        /// root layout subscribes to so the entire component tree re-renders against the new theme.
        /// </summary>
        public static void Apply(AppTheme theme)
        {
            if (theme == Current) return;
            Current = theme;
            Events.MainEvents.Trigger("OnThemeChanged");
        }

        /// <summary>
        /// Finds a shipped palette by name, tolerating spacing and casing differences
        /// ("LovelyBloom" matches "Lovely Bloom"). Null when nothing matches.
        /// </summary>
        public static AppTheme? ByName(string name)
        {
            static string Normalize(string s) => s.Replace(" ", string.Empty);
            return AppThemes.All.FirstOrDefault(t =>
                string.Equals(Normalize(t.Name), Normalize(name), StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Applies the palette chosen in <see cref="Stores.Settings.Theme"/>; unknown names keep the current one.</summary>
        public static void ApplyFromSettings()
        {
            var theme = ByName(Stores.Settings.Theme);
            if (theme != null) Apply(theme);
        }
    }
}
