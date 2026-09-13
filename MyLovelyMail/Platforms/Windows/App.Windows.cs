namespace MyLovelyMail
{
    public partial class App
    {
        /// <summary>Close-to-tray, start minimized, the autostart entry and remembered window bounds.</summary>
        partial void OnWindowCreated(Window window) => TrayService.AttachWindow(window);
    }
}
