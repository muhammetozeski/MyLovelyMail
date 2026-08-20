namespace MyLovelyMail.MainProject.Constants
{
    public static class AppConstants
    {
        /// <summary> Geliştirici modunu ve test araçlarını açıp kapatır. CANLIYA ÇIKARKEN FALSE YAPILMALI. </summary>
        public const bool TestBuild = false;

        public const string AppName = "MyLovelyMail";

        /// <summary>The one source of the visible app name: window title, tray tooltip, single-instance lookup.</summary>
        public const string AppNameHumanReadable = "My Lovely Mail";

        /// <summary>Launcher sitting at the deploy root; it applies pending updates and starts AppData's exe.</summary>
        public const string LauncherFileName = AppName + ".exe";

        /// <summary>
        /// Shown at the foot of Settings. A hand-typed literal for now, so it is only as true as
        /// the last person to edit it: it does not come from the csproj, the assembly or the
        /// release tag, and nothing fails when they disagree. Bump it with the release.
        /// </summary>
        public const string AppVersion = "v1.34.0";
    }
}
