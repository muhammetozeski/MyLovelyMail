using MyLovelyMail.MainProject.Services;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;
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

            // The toast's own audio is always muted; our SoundBridge plays the (possibly
            // rule-customized) sound instead, so per-rule sounds actually differ.
            builder.MuteAudio();

            AppNotificationManager.Default.Show(builder.BuildNotification());
            Logger.Log($"Toast shown: {toast.Title} — {toast.Body}");

            if (!toast.Mute)
                SoundService.Play(toast.SoundName);
        }

        static void HandleNotificationInvoked(object sender, AppNotificationActivatedEventArgs args)
        {
            TrayService.ShowMainWindow();

            if (!args.Arguments.TryGetValue("accountId", out string? accountId)
                || !args.Arguments.TryGetValue("folder", out string? folderFullName)
                || !args.Arguments.TryGetValue("uid", out string? uidText)
                || !uint.TryParse(uidText, out uint uid) || uid == 0)
                return;

            var account = AccountStore.GetById(accountId);
            if (account == null) return;

            var summary = MessageStore.GetSummary(accountId, folderFullName, uid);
            var folder = MessageStore.GetFolders(accountId).FirstOrDefault(f => f.FullName == folderFullName);

            MailUiState.SelectAccount(account);
            if (folder != null) MailUiState.SelectFolder(folder);
            if (summary != null) MailUiState.OpenMessageInReader(summary);
        }
#endif
    }
}
