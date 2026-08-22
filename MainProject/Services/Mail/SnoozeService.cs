using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>
    /// Hides a message until its time comes, then brings it back as new mail. The state is a field
    /// on the summary, so it survives restarts through index.jsonl and behaves the same on IMAP,
    /// POP3 and local folders without a single server round-trip.
    /// </summary>
    public static class SnoozeService
    {
        /// <summary>The offers in the snooze menu; "tomorrow" and "next week" resolve against the local clock.</summary>
        public static readonly (string Label, Func<DateTime> DueUtc)[] Presets =
        [
            ("⏰ In 3 hours", static () => DateTime.UtcNow.AddHours(3)),
            ("🌅 Tomorrow 08:00", static () => NextLocalTime(DateTime.Today.AddDays(1))),
            ("📆 Next Monday 08:00", static () => NextLocalTime(NextMonday()))
        ];

        const int WakeHourLocal = 8;

        static DateTime NextLocalTime(DateTime localDay) => localDay.AddHours(WakeHourLocal).ToUniversalTime();

        static DateTime NextMonday()
        {
            int daysAhead = ((int)DayOfWeek.Monday - (int)DateTime.Today.DayOfWeek + 7) % 7;
            return DateTime.Today.AddDays(daysAhead == 0 ? 7 : daysAhead);
        }

        /// <summary>
        /// Hides the message until <paramref name="untilUtc"/>. Marking it read is REQUIRED, not
        /// cosmetic: the folder's unread count is recomputed over the whole index, so a hidden but
        /// unread message would leave a badge pointing at mail the user cannot see.
        /// </summary>
        public static void Snooze(MailAccountData account, string folderFullName, MailMessageSummary summary, DateTime untilUtc)
        {
            MessageActions.SetRead(account, folderFullName, summary, read: true);
            summary.SnoozedUntilUtc = untilUtc;
            MessageStore.UpsertSummaries(account.Id, folderFullName, [summary]);
            Log($"Snoozed uid {summary.Uid} in '{folderFullName}' until {untilUtc:u}.");
        }

        /// <summary>Brings the message back and marks it unread, so it reads as a fresh arrival.</summary>
        public static void Wake(MailAccountData account, string folderFullName, MailMessageSummary summary)
        {
            summary.SnoozedUntilUtc = null;
            MessageStore.UpsertSummaries(account.Id, folderFullName, [summary]);
            MessageActions.SetRead(account, folderFullName, summary, read: false);
            Log($"Woke uid {summary.Uid} in '{folderFullName}'.");
        }

        /// <summary>Wakes everything whose time has passed. Called from the periodic sync pass — no separate timer.</summary>
        public static void WakeDue()
        {
            var nowUtc = DateTime.UtcNow;
            foreach (var account in AccountStore.Accounts)
            {
                foreach (var folder in MessageStore.GetFolders(account.Id))
                {
                    foreach (var summary in MessageStore.GetSummaries(account.Id, folder.FullName))
                    {
                        if (summary.SnoozedUntilUtc is { } due && due <= nowUtc)
                            Wake(account, folder.FullName, summary);
                    }
                }
            }
        }

        /// <summary>Every snoozed message of the account, soonest first (drives the debug view and any future overview).</summary>
        public static List<(string FolderFullName, MailMessageSummary Summary)> Snoozed(string accountId) =>
            [.. MessageStore.GetFolders(accountId)
                .SelectMany(folder => MessageStore.GetSummaries(accountId, folder.FullName)
                    .Where(static s => s.SnoozedUntilUtc != null)
                    .Select(s => (folder.FullName, s)))
                .OrderBy(entry => entry.Item2.SnoozedUntilUtc)];
    }
}
