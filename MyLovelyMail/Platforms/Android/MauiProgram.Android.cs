using MyLovelyMail.MainProject.Constants.ThemeConstants;

namespace MyLovelyMail
{
    public static partial class MauiProgram
    {
        /// <summary>The phone's dark theme and animation switch.</summary>
        static partial void RegisterPlatformServices()
        {
            ThemeManager.SystemDarkProbe = AndroidDisplayPreferences.PrefersDark;
            MotionPreference.SystemReducedMotionProbe = AndroidDisplayPreferences.PrefersReducedMotion;
        }
    }
}
