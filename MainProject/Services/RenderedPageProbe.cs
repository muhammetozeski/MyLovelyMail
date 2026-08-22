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
