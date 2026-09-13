using Android.App;
using Android.Content.Res;
using AndroidSettings = Android.Provider.Settings;

namespace MyLovelyMail.MainProject.Constants.ThemeConstants
{
    /// <summary>The phone's dark theme and animation switch, which the "follow the system" settings read on Android.</summary>
    public static class AndroidDisplayPreferences
    {
        /// <summary>The Android <see cref="ThemeManager.SystemDarkProbe"/>.</summary>
        /// <returns>True while the phone's dark theme is on.</returns>
        public static bool PrefersDark() =>
            (Application.Context.Resources?.Configuration?.UiMode & UiMode.NightMask) == UiMode.NightYes;

        /// <summary>
        /// The Android <see cref="MotionPreference.SystemReducedMotionProbe"/>. The accessibility switch
        /// "Remove animations" works by setting the animator duration scale to 0, the same value the
        /// developer option writes.
        /// </summary>
        /// <returns>True when animations are switched off on the phone.</returns>
        public static bool PrefersReducedMotion() =>
            AndroidSettings.Global.GetFloat(Application.Context.ContentResolver, AndroidSettings.Global.AnimatorDurationScale, 1f) == 0f;
    }
}
