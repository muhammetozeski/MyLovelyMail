using MyLovelyMail.MainProject.Constants;
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
#if DEBUG
            MainProject.Services.RenderedPageProbe.HtmlProvider = ReadRenderedHtmlAsync;
            MainProject.Services.RenderedPageProbe.ScriptRunner = RunPageScriptAsync;
#endif
        }

#if DEBUG
        /// <summary>
        /// Debug-only: hands the debug API the HTML the WebView is showing, so the UI can be
        /// audited without clicking or scrolling on the user's screen.
        /// </summary>
        Task<string> ReadRenderedHtmlAsync(string cssSelector) =>
            RunPageScriptAsync(string.IsNullOrWhiteSpace(cssSelector)
                ? "document.body.outerHTML"
                : $"Array.from(document.querySelectorAll({System.Text.Json.JsonSerializer.Serialize(cssSelector)})).map(e => e.outerHTML).join('\\n')");

        /// <summary>Debug-only: evaluates one expression inside the page and returns its value as text.</summary>
        async Task<string> RunPageScriptAsync(string script)
        {
#if WINDOWS
            // BlazorWebView itself has no script API; the WebView2 behind its handler does.
            // ExecuteScriptAsync must run on the UI thread, and the debug API calls in from an
            // HttpListener thread, hence the dispatch. It answers with a JSON-encoded string.
            return await Dispatcher.DispatchAsync(async () =>
            {
                if (blazorWebView.Handler?.PlatformView is not Microsoft.UI.Xaml.Controls.WebView2 webView)
                    return string.Empty;
                await webView.EnsureCoreWebView2Async();
                string encoded = await webView.ExecuteScriptAsync(script);
                return System.Text.Json.JsonSerializer.Deserialize<string>(encoded) ?? encoded;
            });
#else
            await Task.CompletedTask;
            return string.Empty;
#endif
        }
#endif

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
                ToolTipText = AppConstants.AppNameHumanReadable,
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
