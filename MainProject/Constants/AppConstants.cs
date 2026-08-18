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
    }
}
