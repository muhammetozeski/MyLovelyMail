#if WINDOWS
using Microsoft.Win32;
#endif

namespace MyLovelyMail
{
    /// <summary>
    /// Reads the Windows app theme from the OS, NOT from MAUI's Application.RequestedTheme:
    /// the theme is chosen while MauiProgram is still building, when Application.Current is null
    /// and that property would silently answer "light" for every user.
    /// </summary>
    public static class SystemThemeProbe
    {
        public static bool PrefersDark()
        {
#if WINDOWS
            const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
            const string AppsUseLightThemeValue = "AppsUseLightTheme";
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                // 0 = dark, 1 = light; a missing value means the Windows default, which is light.
                return key?.GetValue(AppsUseLightThemeValue) is int useLight && useLight == 0;
            }
            catch (Exception ex)
            {
                Logger.Log($"Could not read the Windows app theme: {ex.Message}", Logger.LogLevel.Warning);
                return false;
            }
#else
            return false;
#endif
        }
    }
}
