using MailKit;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>
    /// User actions on a single message. Every action applies to the local store FIRST (instant
    /// UI), then pushes to the server best-effort in the background — a failed push logs and the
    /// next sync reconciles. POP3 and local folders skip the server step by design.
    /// </summary>
    public static class MessageActions
    {
        public static void ToggleRead(MailAccountData account, string folderFullName, MailMessageSummary summary)
        {
            bool nowSeen = !summary.Flags.HasFlag(MailFlags.Seen);
            summary.Flags = nowSeen ? summary.Flags | MailFlags.Seen : summary.Flags & ~MailFlags.Seen;
            MessageStore.UpsertSummaries(account.Id, folderFullName, [summary]);
            PushFlagInBackground(account, folderFullName, summary.Uid, MessageFlags.Seen, nowSeen);
        }

        /// <summary>Marks an unread message as read (no-op when already read) — the "opened in reader" path.</summary>
        public static void MarkRead(MailAccountData account, string folderFullName, MailMessageSummary summary)
        {
            if (!summary.IsUnread) return;
            summary.Flags |= MailFlags.Seen;
            MessageStore.UpsertSummaries(account.Id, folderFullName, [summary]);
            PushFlagInBackground(account, folderFullName, summary.Uid, MessageFlags.Seen, add: true);
        }

        public static void ToggleFlagged(MailAccountData account, string folderFullName, MailMessageSummary summary)
        {
            bool nowFlagged = !summary.Flags.HasFlag(MailFlags.Flagged);
            summary.Flags = nowFlagged ? summary.Flags | MailFlags.Flagged : summary.Flags & ~MailFlags.Flagged;
            MessageStore.UpsertSummaries(account.Id, folderFullName, [summary]);
            PushFlagInBackground(account, folderFullName, summary.Uid, MessageFlags.Flagged, nowFlagged);
        }

        /// <summary>Important is the app's own marker — it lives only in the local store.</summary>
        public static void ToggleImportant(MailAccountData account, string folderFullName, MailMessageSummary summary)
        {
            summary.Flags ^= MailFlags.Important;
            MessageStore.UpsertSummaries(account.Id, folderFullName, [summary]);
        }

        /// <summary>
        /// Deletes per <see cref="Settings.DeleteAction"/>: moves to the Trash/Archive folder on
        /// IMAP when one is known, otherwise (POP3, local folders, permanent mode) removes locally
        /// — POP3 servers keep their copy, matching classic POP3 client behavior.
        /// </summary>
        public static void Delete(MailAccountData account, string folderFullName, MailMessageSummary summary)
        {
            MessageStore.RemoveMessages(account.Id, folderFullName, [summary.Uid]);
            if (account.Protocol != IncomingProtocol.Imap || folderFullName.StartsWith(MessageStore.LocalFolderPrefix))
                return;

            var behavior = Settings.DeleteAction.Value;
            var targetRole = behavior == DeleteBehavior.ArchiveInstead ? FolderRole.Archive : FolderRole.Trash;
            string? targetFolder = behavior == DeleteBehavior.DeletePermanently
                ? null
                : MessageStore.GetFolders(account.Id).FirstOrDefault(f => f.Role == targetRole)?.FullName;

            _ = Task.Run(async () =>
            {
                try
                {
                    await ResiliencePolicy.RunNetwork(async ct =>
                    {
                        using var client = await MailConnections.OpenImapAsync(account, ct);
                        var folder = await client.GetFolderAsync(folderFullName, ct);
                        await folder.OpenAsync(FolderAccess.ReadWrite, ct);
                        var uid = new UniqueId(summary.Uid);

                        if (targetFolder != null)
                        {
                            var target = await client.GetFolderAsync(targetFolder, ct);
                            await folder.MoveToAsync(uid, target, ct);
                        }
                        else
                        {
                            await folder.AddFlagsAsync(uid, MessageFlags.Deleted, silent: true, ct);
                            await folder.ExpungeAsync(ct);
                        }
                        await client.DisconnectAsync(true, ct);
                    });
                }
                catch (Exception ex)
                {
                    Log($"Server delete failed for uid {summary.Uid}: {ex.Message}", LogLevel.Error);
                }
            });
        }

        static void PushFlagInBackground(MailAccountData account, string folderFullName, uint uid, MessageFlags flag, bool add)
        {
            if (account.Protocol != IncomingProtocol.Imap || folderFullName.StartsWith(MessageStore.LocalFolderPrefix))
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await ResiliencePolicy.RunNetwork(async ct =>
                    {
                        using var client = await MailConnections.OpenImapAsync(account, ct);
                        var folder = await client.GetFolderAsync(folderFullName, ct);
                        await folder.OpenAsync(FolderAccess.ReadWrite, ct);
                        if (add)
                            await folder.AddFlagsAsync(new UniqueId(uid), flag, silent: true, ct);
                        else
                            await folder.RemoveFlagsAsync(new UniqueId(uid), flag, silent: true, ct);
                        await client.DisconnectAsync(true, ct);
                    });
                }
                catch (Exception ex)
                {
                    Log($"Flag push failed for uid {uid}: {ex.Message}", LogLevel.Warning);
                }
            });
        }
    }
}
