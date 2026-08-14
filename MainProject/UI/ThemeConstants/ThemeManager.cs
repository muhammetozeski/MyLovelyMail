namespace MyLovelyMail.MainProject.Constants.ThemeConstants
{
    /// <summary>
    /// Single source of truth for the active <see cref="AppTheme"/>. Holds the running palette and
    /// notifies the UI when it swaps so every render reflects the new colors immediately.
    /// </summary>
    public static class ThemeManager
    {
        public static AppTheme Current { get; private set; } = AppThemes.PlayfulStarlight;

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
    }
}
