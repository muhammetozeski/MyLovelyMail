namespace MyLovelyMail.MainProject.Services
{
    /// <summary>
    /// Lets non-component code ask for a route change. Blazor's NavigationManager is injected per
    /// circuit, so a static caller cannot reach it; MainLayout registers <see cref="Navigator"/>
    /// once and everything else goes through here.
    /// <para>
    /// Shaped like <see cref="ClipboardService.Writer"/>: null means nothing has registered yet,
    /// and navigating is then a no-op rather than a crash.
    /// </para>
    /// </summary>
    public static class NavigationBridge
    {
        /// <summary>Registered by MainLayout. Null before the first render.</summary>
        public static Action<string>? Navigator;

        /// <summary>Navigates to a route, returning whether anything could act on it.</summary>
        public static bool TryNavigate(string route)
        {
            if (Navigator == null)
            {
                Log($"Navigation to '{route}' ignored: no layout has registered yet.", LogLevel.Warning);
                return false;
            }
            Navigator(route);
            return true;
        }
    }
}
