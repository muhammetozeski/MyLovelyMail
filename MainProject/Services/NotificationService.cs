using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;
using MyLovelyMail.MainProject.Constants;

namespace MyLovelyMail.MainProject.Services
{
    /// <summary>One toast request handed to the platform presenter.</summary>
    public sealed class MailToast
    {
        public required string Title { get; init; }
        public required string Body { get; init; }
        /// <summary>True silences the toast sound (per-message "none" sound or a muting rule).</summary>
        public bool Mute { get; init; }

        /// <summary>Sound name to play with the toast (rule-chosen or the account default); ignored when muted.</summary>
        public string SoundName { get; init; } = "default";
        public string AccountId { get; init; } = string.Empty;
        public string FolderFullName { get; init; } = string.Empty;
        /// <summary>The single new message's uid; 0 for a batch toast.</summary>
        public uint Uid { get; init; }
    }

    /// <summary>
    /// Decides whether newly synced mail deserves a notification — honoring the per-account
    /// NotifyOnNewMail override, quiet hours, and rule outcomes (Muted flag, per-rule sound) —
    /// and hands the toast to the platform presenter registered by the head project.
    /// The "none" sound name silences without suppressing the visual toast.
    /// </summary>
    public static class NotificationService
    {
        public const string SilentSoundName = "none";

        /// <summary>Set by the head project (Windows: AppNotification, Android: MailNotifications). Null = platform without toasts.</summary>
        public static Action<MailToast>? Presenter;

        /// <summary>Called by the sync services after the rule pass with genuinely NEW messages only.</summary>
        public static void NotifyNewMessages(MailAccountData account, string folderFullName, List<MailMessageSummary> newMessages)
        {
            if (Presenter == null) { Log("Notification skipped: no platform presenter registered."); return; }
            if (!AccountStore.GetSettings(account.Id).NotifyOnNewMail.Value) { Log($"Notification skipped: NotifyOnNewMail off for {account.EmailAddress}."); return; }
            if (IsInQuietHours(DateTime.Now.Hour)) { Log("Notification skipped: quiet hours."); return; }

            var audible = newMessages.Where(RuleEngine.ShouldNotify).ToList();
            if (audible.Count == 0) { Log("Notification skipped: all new messages muted by rules."); return; }

            string soundName = RuleEngine.GetNotificationSound(audible[0])
                ?? AccountStore.GetSettings(account.Id).NotificationSound.Value;
            bool mute = soundName.Equals(SilentSoundName, StringComparison.OrdinalIgnoreCase);

            // single != null: sender/subject toast opening that message; null: batch count toast (Uid 0).
            var single = audible.Count == 1 ? audible[0] : null;
            var toast = new MailToast
            {
                Title = single == null ? AppConstants.AppNameHumanReadable
                    : string.IsNullOrWhiteSpace(single.FromName) ? single.FromAddress : single.FromName,
                Body = single == null ? $"💌 {audible.Count} new messages for {account.EmailAddress}"
                    : string.IsNullOrWhiteSpace(single.Subject) ? "(no subject)" : single.Subject,
                Mute = mute,
                SoundName = soundName,
                AccountId = account.Id,
                FolderFullName = folderFullName,
                Uid = single?.Uid ?? 0
            };

            Present(toast);
        }

        /// <summary>
        /// Shows one toast through the platform presenter and plays its sound unless it is muted. Whether
        /// to notify at all is decided before this; the debug API calls it directly to show a probe.
        /// </summary>
        /// <param name="toast">The finished notification.</param>
        internal static void Present(MailToast toast)
        {
            try
            {
                Presenter?.Invoke(toast);
            }
            catch (Exception ex)
            {
                Log($"Toast presenter failed: {ex.Message}", LogLevel.Warning);
            }

            // Every platform's notification stays silent itself, so the sound heard is the one a rule chose.
            if (!toast.Mute)
                SoundService.Play(toast.SoundName);
        }

        /// <summary>
        /// What tapping a single-message notification does on every platform: selects its account and
        /// folder and opens the message in the reader. A batch notification carries uid 0 and only
        /// brings the app forward, which the platform has done before calling this.
        /// </summary>
        /// <param name="accountId">The account the notification was about.</param>
        /// <param name="folderFullName">The folder the message arrived in.</param>
        /// <param name="uid">The message's uid; 0 for a batch notification.</param>
        public static void OpenNotifiedMessage(string accountId, string folderFullName, uint uid)
        {
            if (uid == 0) return;
            var account = AccountStore.GetById(accountId);
            if (account == null) return;

            var summary = MessageStore.GetSummary(accountId, folderFullName, uid);
            var folder = MessageStore.GetFolders(accountId).FirstOrDefault(f => f.FullName == folderFullName);

            MailUiState.SelectAccount(account);
            if (folder != null) MailUiState.SelectFolder(folder);
            if (summary != null) MailUiState.OpenMessageInReader(summary);
        }

        /// <summary>Start==End disables the window; a wrapping window (22→7) spans midnight.</summary>
        internal static bool IsInQuietHours(int hour)
        {
            int start = Settings.QuietHoursStart.Value;
            int end = Settings.QuietHoursEnd.Value;
            if (start == end) return false;
            return start < end ? hour >= start && hour < end : hour >= start || hour < end;
        }
    }
}
