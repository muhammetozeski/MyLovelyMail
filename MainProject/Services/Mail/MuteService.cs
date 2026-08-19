using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>
    /// Silences a conversation for the rest of its life. <see cref="MailFlags.Muted"/> already
    /// existed but only a filter rule could set it and only the notification check read it, so a
    /// recurring reply-all chain still cost a toast, a bloom and a triage decision every time.
    /// <para>
    /// The flag alone cannot carry this: a reply arrives with a new uid, and per-message state
    /// only crosses the same uid. The Message-Ids go to <see cref="MutedThreadStore"/> so an
    /// arrival that points back at the thread is muted before anyone sees it.
    /// </para>
    /// </summary>
    public static class MuteService
    {
        /// <summary>
        /// Mutes every message of the conversation the given one belongs to and remembers the
        /// thread. Marking read goes through <see cref="MessageStore.SetSeen"/> rather than a
        /// hand-rolled flag flip, because that method owns the folder badge delta.
        /// </summary>
        public static int MuteThread(MailAccountData account, string folderFullName, MailMessageSummary summary)
        {
            var members = ThreadMembers(account.Id, folderFullName, summary);

            foreach (var member in members) member.Flags |= MailFlags.Muted;
            MessageStore.UpsertSummaries(account.Id, folderFullName, members);
            // A muted thread the user still has to open to clear is not silenced, only demoted.
            MessageStore.SetSeen(account.Id, folderFullName, members, seen: true);

            MutedThreadStore.Add(members.SelectMany(IdentityOf));
            Log($"Muted {members.Count} message(s) of the conversation holding uid {summary.Uid} in '{folderFullName}'.");
            return members.Count;
        }

        /// <summary>Unmutes the conversation and forgets it, so future replies arrive normally again.</summary>
        public static int UnmuteThread(MailAccountData account, string folderFullName, MailMessageSummary summary)
        {
            var members = ThreadMembers(account.Id, folderFullName, summary);

            foreach (var member in members) member.Flags &= ~MailFlags.Muted;
            MessageStore.UpsertSummaries(account.Id, folderFullName, members);

            // Remove what the members point at as well, not just what they are: ApplyToIncoming
            // remembers each reply's own id as it arrives, and a reply that has since been moved
            // or expunged would otherwise leave its id behind and re-mute the next reply.
            MutedThreadStore.Remove(members.SelectMany(AncestryOf));
            Log($"Unmuted {members.Count} message(s) of the conversation holding uid {summary.Uid} in '{folderFullName}'.");
            return members.Count;
        }

        public static void ToggleThread(MailAccountData account, string folderFullName, MailMessageSummary summary)
        {
            if (summary.Flags.HasFlag(MailFlags.Muted))
                UnmuteThread(account, folderFullName, summary);
            else
                MuteThread(account, folderFullName, summary);
        }

        /// <summary>
        /// Mutes an arrival that belongs to a muted conversation, and records its own Message-Id so
        /// the thread stays quiet however long it runs. Called where incoming rules run, for both
        /// protocols. Returns true when the summary was muted.
        /// </summary>
        public static bool ApplyToIncoming(MailMessageSummary summary)
        {
            if (!MutedThreadStore.ContainsAny(AncestryOf(summary))) return false;

            summary.Flags |= MailFlags.Muted;
            MutedThreadStore.Add([summary.MessageId]);
            return true;
        }

        /// <summary>Mutes every arrival that belongs to a muted conversation; returns how many.</summary>
        public static int ApplyToIncoming(IEnumerable<MailMessageSummary> arrivals) =>
            arrivals.Count(ApplyToIncoming);

        /// <summary>The conversation the message belongs to, or just the message when it stands alone.</summary>
        static List<MailMessageSummary> ThreadMembers(string accountId, string folderFullName, MailMessageSummary summary) =>
            ThreadingService.BuildThreads(MessageStore.GetSummaries(accountId, folderFullName))
                .FirstOrDefault(t => t.Messages.Any(m => m.Uid == summary.Uid))
                ?.Messages ?? [summary];

        /// <summary>The ids a message IS — what a future reply will point back at.</summary>
        static IEnumerable<string> IdentityOf(MailMessageSummary summary) => [summary.MessageId];

        /// <summary>The ids a message points BACK at, plus its own — how it is recognised as part of a thread.</summary>
        static IEnumerable<string> AncestryOf(MailMessageSummary summary) =>
            [summary.InReplyTo, .. summary.ReferenceIds, summary.MessageId];
    }
}
