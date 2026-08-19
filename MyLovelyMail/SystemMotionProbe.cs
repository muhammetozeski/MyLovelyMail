#if WINDOWS
using Microsoft.Win32;
#endif

namespace MyLovelyMail
{
    /// <summary>
    /// Reads "Show animations in Windows" from the OS, the same setting the Settings app writes
    /// when accessibility asks for less motion.
    /// </summary>
    public static class SystemMotionProbe
    {
        public static bool PrefersReducedMotion()
        {
#if WINDOWS
            const string WindowMetricsKey = @"Control Panel\Desktop\WindowMetrics";
            const string MinAnimateValue = "MinAnimate";
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(WindowMetricsKey);
                // REG_SZ, not REG_DWORD: it reads back as the string "1" or "0". Copying the int
                // pattern from SystemThemeProbe would silently answer "animations on" for everyone.
                // "0" = animations off; anything else, including a missing value, means on.
                return key?.GetValue(MinAnimateValue) as string == "0";
            }
            catch (Exception ex)
            {
                Logger.Log($"Could not read the Windows animation setting: {ex.Message}", Logger.LogLevel.Warning);
                return false;
            }
#else
            return false;
#endif
        }
    }
}
