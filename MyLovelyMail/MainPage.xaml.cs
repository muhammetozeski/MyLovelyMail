#if WINDOWS
using H.NotifyIcon;
#endif

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
        TaskbarIcon? trayIcon;

        /// <summary>
        /// Builds the tray icon fully in code (no XAML) so the Android/iOS targets, which do not
        /// reference H.NotifyIcon, still compile this page. The icon file is a Raw MAUI asset
        /// copied next to the executable.
        /// </summary>
        void BuildTrayIcon()
        {
            var menu = new MenuFlyout();
            menu.Add(CreateTrayMenuItem("💌 Open My Lovely Mail", TrayService.ShowMainWindow));
            menu.Add(CreateTrayMenuItem("🔄 Sync now", TrayService.SyncNow));
            menu.Add(CreateTrayMenuItem("❌ Exit", TrayService.ExitApplication));

            trayIcon = new TaskbarIcon
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

        /// <summary>
        /// Returns a flyout item with <paramref name="clickAction"/> wired to both Clicked and Command.
        /// Proven by log evidence: H.NotifyIcon never delivers MenuFlyoutItem.Clicked for the tray
        /// flyout, but it does execute Command. Keep both wired — whichever fires, wins.
        /// </summary>
        static MenuFlyoutItem CreateTrayMenuItem(string text, Action clickAction)
        {
            var item = new MenuFlyoutItem { Text = text, Command = new Command(clickAction) };
            item.Clicked += (_, _) => clickAction();
            return item;
        }
#endif
    }
}
