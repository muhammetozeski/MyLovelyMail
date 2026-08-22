using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Constants.ThemeConstants
{
    /// <summary>
    /// The single answer to "should the UI keep moving". Shaped like
    /// <see cref="ThemeManager.SystemDarkProbe"/>: MainProject cannot read the OS, so the head
    /// project registers the probe and everything else asks <see cref="IsCalm"/>.
    /// </summary>
    public static class MotionPreference
    {
        /// <summary>Registered by the head project (Windows: the desktop animation setting). Null = platform cannot report it.</summary>
        public static Func<bool>? SystemReducedMotionProbe;

        /// <summary>Whether the OS says animations are switched off; false when nothing can answer.</summary>
        public static bool SystemPrefersReduced => SystemReducedMotionProbe?.Invoke() ?? false;

        /// <summary>
        /// True when animations should stop: the OS setting while following it, otherwise the
        /// app's own switch.
        /// </summary>
        public static bool IsCalm =>
            Settings.FollowSystemMotion.Value ? SystemPrefersReduced : Settings.ReduceMotion.Value;
    }
}
