using MyLovelyMail.MainProject.DataModels.Mail;

namespace MyLovelyMail.MainProject.Storage
{
    public static partial class MessageStore
    {
        /// <summary>Prefix marking folders that exist only on this machine (created by rules/user).</summary>
        public const string LocalFolderPrefix = "Local/";

        /// <summary>
        /// A uid the target local folder is not already using for a DIFFERENT message. A local
        /// folder has no server, so its uid is purely a local key and may be reassigned; the
        /// incoming one is kept when it is free or already belongs to this same message, so a
        /// repeated move is idempotent.
        /// </summary>
        static uint FreeLocalUid(string accountId, string targetFullName, MailMessageSummary summary)
        {
            var index = GetIndex(accountId, targetFullName);
            if (!index.Summaries.TryGetValue(summary.Uid, out var occupant) || SameMessage(occupant, summary))
                return summary.Uid;

            // Derived from the message's own identity, so the same message lands on the same key
            // every time; the walk only runs on the rare hash collision.
            uint candidate = StableHash.Fnv1a(summary.MessageId.Length > 0
                ? summary.MessageId
                : $"{summary.FromAddress}|{summary.Subject}|{summary.DateUtc:O}");
            while (index.Summaries.TryGetValue(candidate, out var taken) && !SameMessage(taken, summary))
                candidate++;

            Log($"Local folder '{targetFullName}' already used uid {summary.Uid}; filed this message under {candidate} instead.");
            return candidate;
        }

        /// <summary>Same mail, whatever uid it currently carries. Falls back to sender+subject+date when Message-Id is absent.</summary>
        static bool SameMessage(MailMessageSummary first, MailMessageSummary second) =>
            first.MessageId.Length > 0 || second.MessageId.Length > 0
                ? first.MessageId.Equals(second.MessageId, StringComparison.OrdinalIgnoreCase)
                : first.FromAddress == second.FromAddress && first.Subject == second.Subject && first.DateUtc == second.DateUtc;

        /// <summary>The one spelling of the inbox path: IMAP's mandated name and POP3's single mailbox mirror it.</summary>
        public const string InboxFullName = "INBOX";

        /// <summary>
        /// Moves one message into a local-only folder: creates the folder info on first use,
        /// carries the cached .eml along when present, and removes the source entry.
        /// <para>
        /// The uid is re-keyed on the way in. IMAP numbers restart per folder, so filing INBOX
        /// uid 7 and Sent uid 7 into the same local folder used to overwrite the .eml AND the
        /// index row — and CarryOverLocalState grafted the vanishing message's tags onto the
        /// survivor first, so the wreck looked like the message that had been destroyed. Every
        /// other collision in the app re-syncs away; this one has no server copy behind it.
        /// </para>
        /// </summary>
        public static void MoveToLocalFolder(string accountId, string fromFolderFullName, uint uid, string localFolderName)
        {
            var summary = GetSummary(accountId, fromFolderFullName, uid);
            if (summary == null) return;

            string targetFullName = LocalFolderPrefix + localFolderName;
            if (!GetFolders(accountId).Any(f => f.FullName == targetFullName))
                SaveFolder(new MailFolderData
                {
                    AccountId = accountId,
                    FullName = targetFullName,
                    DisplayName = localFolderName,
                    IsLocal = true
                });

            byte[]? mimeBytes = TryLoadFullMessage(accountId, fromFolderFullName, uid);
            summary.Uid = FreeLocalUid(accountId, targetFullName, summary);
            if (mimeBytes != null)
                SaveFullMessage(accountId, targetFullName, summary.Uid, mimeBytes);

            UpsertSummaries(accountId, targetFullName, [summary]);
            RemoveMessages(accountId, fromFolderFullName, [uid]);
        }

        /// <summary>
        /// Mirrors a move the SERVER already performed: the summary, its cached .eml and every
        /// app-local field (tags, important, snooze, mute) land in the destination folder under the
        /// uid the server assigned there, and the source row goes away.
        /// <para>
        /// Only ever called with a destination uid the server reported (UIDPLUS). Inventing one
        /// would repeat the collision local folders already taught: uids restart per folder, so a
        /// guessed number can land on top of a message that is already there.
        /// </para>
        /// </summary>
        public static void MoveToServerFolder(string accountId, string fromFolderFullName, uint uid, string toFolderFullName, uint newUid)
        {
            var summary = GetSummary(accountId, fromFolderFullName, uid);
            if (summary == null) return;

            byte[]? mimeBytes = TryLoadFullMessage(accountId, fromFolderFullName, uid);
            summary.Uid = newUid;
            if (mimeBytes != null)
                SaveFullMessage(accountId, toFolderFullName, newUid, mimeBytes);

            UpsertSummaries(accountId, toFolderFullName, [summary]);
            RemoveMessages(accountId, fromFolderFullName, [uid]);
        }
    }
}
