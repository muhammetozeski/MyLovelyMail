namespace MyLovelyMail.MainProject.Constants
{
    /// <summary>
    /// The few things the screens have to ask about the operating system they run on. Pages read these
    /// instead of calling OperatingSystem themselves, so what differs between Windows and a phone is
    /// decided in one file.
    /// </summary>
    public static class PlatformFeatures
    {
        /// <summary>The operating system's name as the settings texts use it, e.g. "Follow the Android theme".</summary>
        public static string SystemName { get; } =
            OperatingSystem.IsAndroid() ? "Android"
            : OperatingSystem.IsWindows() ? "Windows"
            : "system";

        /// <summary>A resizable desktop window with a tray icon: close to tray, start minimized and start with Windows exist only there.</summary>
        public static bool HasDesktopWindow { get; } = OperatingSystem.IsWindows();
    }
}
