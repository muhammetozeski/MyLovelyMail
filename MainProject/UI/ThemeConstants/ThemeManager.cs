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

        /// <summary>
        /// Applies the palette the settings ask for: the Windows app theme picks a matching
        /// light/dark palette when FollowSystemTheme is on, otherwise the named Theme is used.
        /// Unknown names keep the current palette.
        /// </summary>
        public static void ApplyFromSettings()
        {
            if (Stores.Settings.FollowSystemTheme.Value)
            {
                Apply(AppThemes.All.FirstOrDefault(t => t.IsDark == SystemPrefersDark) ?? Current);
                return;
            }

            var theme = ByName(Stores.Settings.Theme);
            if (theme != null) Apply(theme);
        }

        /// <summary>Set by the head project (Windows: Application.RequestedTheme); false on platforms that cannot report it.</summary>
        public static Func<bool>? SystemDarkProbe;

        static bool SystemPrefersDark => SystemDarkProbe?.Invoke() ?? Current.IsDark;
    }
}
