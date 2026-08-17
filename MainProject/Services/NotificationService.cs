using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services.Mail;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services
{
    /// <summary>One toast request handed to the platform presenter.</summary>
    public sealed class MailToast
    {
        public required string Title { get; init; }
        public required string Body { get; init; }
        /// <summary>True silences the toast sound (per-message "none" sound or a muting rule).</summary>
        public bool Mute { get; init; }
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

        /// <summary>Set by the head project (Windows: AppNotification). Null = platform without toasts.</summary>
        public static Action<MailToast>? Presenter;

        /// <summary>Called by the sync services after the rule pass with genuinely NEW messages only.</summary>
        public static void NotifyNewMessages(MailAccountData account, string folderFullName, List<MailMessageSummary> newMessages)
        {
            if (Presenter == null) return;
            if (!AccountStore.GetSettings(account.Id).NotifyOnNewMail.Value) return;
            if (IsInQuietHours(DateTime.Now.Hour)) return;

            var audible = newMessages.Where(RuleEngine.ShouldNotify).ToList();
            if (audible.Count == 0) return;

            string soundName = RuleEngine.GetNotificationSound(audible[0])
                ?? AccountStore.GetSettings(account.Id).NotificationSound.Value;
            bool mute = soundName.Equals(SilentSoundName, StringComparison.OrdinalIgnoreCase);

            var toast = audible.Count == 1
                ? new MailToast
                {
                    Title = string.IsNullOrWhiteSpace(audible[0].FromName) ? audible[0].FromAddress : audible[0].FromName,
                    Body = string.IsNullOrWhiteSpace(audible[0].Subject) ? "(no subject)" : audible[0].Subject,
                    Mute = mute,
                    AccountId = account.Id,
                    FolderFullName = folderFullName,
                    Uid = audible[0].Uid
                }
                : new MailToast
                {
                    Title = "My Lovely Mail",
                    Body = $"💌 {audible.Count} new messages for {account.EmailAddress}",
                    Mute = mute,
                    AccountId = account.Id,
                    FolderFullName = folderFullName
                };

            try
            {
                Presenter(toast);
            }
            catch (Exception ex)
            {
                Log($"Toast presenter failed: {ex.Message}", LogLevel.Warning);
            }
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
