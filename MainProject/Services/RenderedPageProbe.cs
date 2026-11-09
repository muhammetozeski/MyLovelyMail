namespace MyLovelyMail.MainProject.Services
{
    /// <summary>
    /// Reads the HTML the app is actually showing. MainProject cannot touch the WebView, so the
    /// head project registers <see cref="HtmlProvider"/>, exactly like
    /// <see cref="ClipboardService.Writer"/> and <see cref="NavigationBridge.Navigator"/>.
    /// <para>
    /// This exists for the debug API: auditing the UI otherwise means clicking and scrolling on
    /// the user's screen, and screenshots cannot answer "is this element in the page" for
    /// anything below the fold.
    /// </para>
    /// </summary>
    public static class RenderedPageProbe
    {
        /// <summary>Registered by the head project (Windows: the BlazorWebView). Null = cannot read the page.</summary>
        public static Func<string, Task<string>>? HtmlProvider;

        /// <summary>
        /// Runs a line of script inside the page and returns what it evaluates to. Registered the
        /// same way as <see cref="HtmlProvider"/>.
        /// <para>
        /// This is how a control that only exists once it is opened — an expander, a menu — gets
        /// audited: the element is clicked IN THE DOM, which moves no cursor and steals no focus,
        /// unlike driving the real mouse across a screen the user is working on.
        /// </para>
        /// </summary>
        public static Func<string, Task<string>>? ScriptRunner;

        /// <summary>Calls a Chromium DevTools Protocol (CDP) method on the underlying WebView2 without bringing the window to the foreground.</summary>
        public static Func<string, string, Task<string>>? CdpCaller;

        /// <summary>Captures a PNG screenshot of the rendered WebView2 surface even when the window is in the background.</summary>
        public static Func<Task<byte[]?>>? ScreenshotProvider;

        /// <summary>Invokes a CDP command on WebView2; returns null if unavailable.</summary>
        public static async Task<string?> CallCdpAsync(string method, string parametersJson = "{}")
        {
            if (CdpCaller == null) return null;
            try
            {
                return await CdpCaller(method, parametersJson);
            }
            catch (Exception ex)
            {
                Log($"CDP call {method} failed: {ex.Message}", LogLevel.Warning);
                return null;
            }
        }

        /// <summary>Captures a PNG screenshot from the WebView2 surface without activating the window.</summary>
        public static async Task<byte[]?> CaptureScreenshotAsync()
        {
            if (ScreenshotProvider == null) return null;
            try
            {
                return await ScreenshotProvider();
            }
            catch (Exception ex)
            {
                Log($"Could not capture page screenshot: {ex.Message}", LogLevel.Warning);
                return null;
            }
        }

        /// <summary>Runs the script; null when nothing has registered a runner.</summary>
        public static async Task<string?> RunScriptAsync(string script)
        {
            if (ScriptRunner == null) return null;
            try
            {
                return await ScriptRunner(script);
            }
            catch (Exception ex)
            {
                Log($"Could not run a script in the page: {ex.Message}", LogLevel.Warning);
                return null;
            }
        }

        /// <summary>
        /// Returns the outer HTML of the elements matching <paramref name="cssSelector"/>, or the
        /// whole body when it is empty. Null when nothing has registered a provider.
        /// </summary>
        public static async Task<string?> ReadHtmlAsync(string cssSelector)
        {
            if (HtmlProvider == null) return null;
            try
            {
                return await HtmlProvider(cssSelector);
            }
            catch (Exception ex)
            {
                Log($"Could not read the rendered page: {ex.Message}", LogLevel.Warning);
                return null;
            }
        }
    }
}
