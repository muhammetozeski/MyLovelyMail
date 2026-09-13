using MyLovelyMail.MainProject.Services;
#if WINDOWS
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
#endif

namespace MyLovelyMail
{
    /// <summary>
    /// Windows toast presenter for <see cref="NotificationService"/> using WinAppSDK
    /// AppNotifications. Clicking a toast brings the window back; a single-message toast also
    /// opens that message in the reader. No-op on non-Windows targets.
    /// </summary>
    public static class NotificationBridge
    {
        public static void Initialize()
        {
#if WINDOWS
            try
            {
                var manager = AppNotificationManager.Default;
                manager.NotificationInvoked += HandleNotificationInvoked;
                manager.Register();
                NotificationService.Presenter = ShowToast;
                Logger.Log("Toast pipeline registered.");
            }
            catch (Exception ex)
            {
                Logger.Log($"Toast registration failed (notifications disabled): {ex.Message}", Logger.LogLevel.Warning);
            }
#endif
        }

#if WINDOWS
        static void ShowToast(MailToast toast)
        {
            var builder = new AppNotificationBuilder()
                .AddText(toast.Title)
                .AddText(toast.Body)
                .AddArgument("accountId", toast.AccountId)
                .AddArgument("folder", toast.FolderFullName)
                .AddArgument("uid", toast.Uid.ToString());

            // The toast's own audio is always muted; NotificationService plays the (possibly
            // rule-customized) sound instead, so per-rule sounds actually differ.
            builder.MuteAudio();

            AppNotificationManager.Default.Show(builder.BuildNotification());
            Logger.Log($"Toast shown: {toast.Title} — {toast.Body}");
        }

        static void HandleNotificationInvoked(object sender, AppNotificationActivatedEventArgs args)
        {
            TrayService.ShowMainWindow();

            if (args.Arguments.TryGetValue("accountId", out string? accountId)
                && args.Arguments.TryGetValue("folder", out string? folderFullName)
                && args.Arguments.TryGetValue("uid", out string? uidText)
                && uint.TryParse(uidText, out uint uid))
                NotificationService.OpenNotifiedMessage(accountId, folderFullName, uid);
        }
#endif
    }
}
