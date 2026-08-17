namespace MyLovelyMail
{
    public partial class MainPage : ContentPage
    {
        public MainPage()
        {
            InitializeComponent();
#if WINDOWS
            BuildTrayIcon();
#endif
        }

#if WINDOWS
        H.NotifyIcon.TaskbarIcon? trayIcon;

        /// <summary>
        /// Builds the tray icon fully in code (no XAML) so the Android/iOS targets, which do not
        /// reference H.NotifyIcon, still compile this page. The icon file is a Raw MAUI asset
        /// copied next to the executable.
        /// </summary>
        void BuildTrayIcon()
        {
            var menu = new MenuFlyout();

            // Proven by log evidence: H.NotifyIcon never delivers MenuFlyoutItem.Clicked for the
            // tray flyout, but it does execute Command. Keep both wired — whichever fires, wins.
            var openItem = new MenuFlyoutItem { Text = "💌 Open My Lovely Mail", Command = new Command(static () => TrayService.ShowMainWindow()) };
            openItem.Clicked += static (_, _) => TrayService.ShowMainWindow();
            var syncItem = new MenuFlyoutItem { Text = "🔄 Sync now", Command = new Command(static () => TrayService.SyncNow()) };
            syncItem.Clicked += static (_, _) => TrayService.SyncNow();
            var exitItem = new MenuFlyoutItem { Text = "❌ Exit", Command = new Command(static () => TrayService.ExitApplication()) };
            exitItem.Clicked += static (_, _) => TrayService.ExitApplication();

            menu.Add(openItem);
            menu.Add(syncItem);
            menu.Add(exitItem);

            trayIcon = new H.NotifyIcon.TaskbarIcon
            {
                ToolTipText = "My Lovely Mail",
                LeftClickCommand = new Command(static () => TrayService.ShowMainWindow()),
                NoLeftClickDelay = true
            };

            string iconPath = Path.Combine(AppContext.BaseDirectory, "trayicon.ico");
            if (File.Exists(iconPath))
                trayIcon.IconSource = ImageSource.FromFile(iconPath);

            FlyoutBase.SetContextFlyout(trayIcon, menu);
            RootLayout.Add(trayIcon);

            Loaded += (_, _) => trayIcon.ForceCreate();
        }
#endif
    }
}
