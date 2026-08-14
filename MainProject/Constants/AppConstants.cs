namespace MyLovelyMail.MainProject.Constants
{
    public static class AppConstants
    {
        /// <summary> Geliştirici modunu ve test araçlarını açıp kapatır. CANLIYA ÇIKARKEN FALSE YAPILMALI. </summary>
        public const bool TestBuild = false;

        public const string AppName = "MyLovelyMail";
        public const string AppNameHumanReadable = "My Lovely Mail";

        public const string GuestDisplayName = "Guest";

        public static class Avatar
        {
            public const string Guest = "👤";
            public const string DefaultUser = "🙂";
        }

        public const string LoginMenuTitle = AppNameHumanReadable;
        public const string LoginMenuSubtitle = AppTagline;
        public const string AppTagline = "Welcome to " + AppNameHumanReadable;
        public const string ShareTextHeader = "Check out " + AppNameHumanReadable + "! Join me! 🚀";

        public static class WebViewErrors
        {
            public const string Title = "WebView Engine Error";
            public const string Description = "A valid WebView engine was not found or is outdated. Please update or install a compatible browser to continue.";
            public const string UpdateAndroidWebView = "Install or Update Android System WebView to the latest version";
            public const string ChromeFallbackText = "If the issue persists, try installing Google Chrome:";
            public const string UpdateChrome = "Install or Update Google Chrome to the latest version";
        }

        public static class Urls
        {
            public const string AppStore = "https://apps.apple.com/app/idYOUR_APP_STORE_ID";
            public const string PlayStore = "https://play.google.com/store/apps/details?id=com.company.app";
            public const string PlayStoreWebView = "https://play.google.com/store/apps/details?id=com.google.android.webview";
            public const string PlayStoreChrome = "https://play.google.com/store/apps/details?id=com.android.chrome";

            public const string DefaultContentImage = "https://images.unsplash.com/photo-1518709268805-4e9042af9f23?q=80&w=1080&auto=format&fit=crop";
        }

        public static class Legal
        {
            public const string ContactEmail = "support@example.com";
        }

        public static class FilePaths
        {
            public const string DummyJson = "dummy.json";
            public const string LogoImage = "logo/logo.png";
        }
    }
}
