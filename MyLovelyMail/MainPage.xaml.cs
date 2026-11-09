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
            MainProject.Services.RenderedPageProbe.CdpCaller = CallCdpMethodAsync;
            MainProject.Services.RenderedPageProbe.ScreenshotProvider = CaptureWebViewScreenshotAsync;
#endif
        }

#if DEBUG
#if WINDOWS
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool IsIconic(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        const int SW_SHOWNOACTIVATE = 4;
        static readonly IntPtr HWND_BOTTOM = new(1);
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOACTIVATE = 0x0010;

        void EnsureBackgroundRenderableWindow()
        {
            if (Window?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window winuiWindow) return;
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(winuiWindow);
            if (hwnd != IntPtr.Zero && IsIconic(hwnd))
            {
                // Restore from minimized state without activating or stealing focus, and keep behind all windows.
                ShowWindow(hwnd, SW_SHOWNOACTIVATE);
                SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
        }
#endif

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
                EnsureBackgroundRenderableWindow();
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

        async Task<string> CallCdpMethodAsync(string methodName, string parametersJson)
        {
#if WINDOWS
            return await Dispatcher.DispatchAsync(async () =>
            {
                EnsureBackgroundRenderableWindow();
                if (blazorWebView.Handler?.PlatformView is not Microsoft.UI.Xaml.Controls.WebView2 webView)
                    return string.Empty;
                await webView.EnsureCoreWebView2Async();
                return await webView.CoreWebView2.CallDevToolsProtocolMethodAsync(
                    methodName,
                    string.IsNullOrWhiteSpace(parametersJson) ? "{}" : parametersJson);
            });
#else
            await Task.CompletedTask;
            return string.Empty;
#endif
        }

        async Task<byte[]?> CaptureWebViewScreenshotAsync()
        {
#if WINDOWS
            return await Dispatcher.DispatchAsync(async () =>
            {
                EnsureBackgroundRenderableWindow();
                if (blazorWebView.Handler?.PlatformView is not Microsoft.UI.Xaml.Controls.WebView2 webView)
                    return null;
                await webView.EnsureCoreWebView2Async();

                try
                {
                    string cdpJson = await webView.CoreWebView2.CallDevToolsProtocolMethodAsync(
                        "Page.captureScreenshot",
                        "{\"format\":\"png\",\"fromSurface\":true}");
                    using var doc = System.Text.Json.JsonDocument.Parse(cdpJson);
                    if (doc.RootElement.TryGetProperty("data", out var dataProp) && dataProp.GetString() is { Length: > 0 } b64)
                        return Convert.FromBase64String(b64);
                }
                catch
                {
                    // Fallback to CapturePreviewAsync below.
                }

                using var ms = new MemoryStream();
                using var ras = ms.AsRandomAccessStream();
                await webView.CoreWebView2.CapturePreviewAsync(
                    Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,
                    ras);
                return ms.Length > 0 ? ms.ToArray() : null;
            });
#else
            await Task.CompletedTask;
            return null;
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
