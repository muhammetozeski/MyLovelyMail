using MailKit;
using MailKit.Net.Imap;
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
        public static void ToggleRead(MailAccountData account, string folderFullName, MailMessageSummary summary) =>
            SetRead(account, folderFullName, summary, !summary.Flags.HasFlag(MailFlags.Seen));

        /// <summary>Forces the read state to <paramref name="read"/> (no-op when already there) — bulk-action friendly.</summary>
        public static void SetRead(MailAccountData account, string folderFullName, MailMessageSummary summary, bool read)
        {
            if (summary.Flags.HasFlag(MailFlags.Seen) == read) return;
            summary.Flags = read ? summary.Flags | MailFlags.Seen : summary.Flags & ~MailFlags.Seen;
            MessageStore.UpsertSummaries(account.Id, folderFullName, [summary]);
            PushFlagInBackground(account, folderFullName, summary.Uid, MessageFlags.Seen, add: read);
        }

        /// <summary>Marks an unread message as read — the "opened in reader" path.</summary>
        public static void MarkRead(MailAccountData account, string folderFullName, MailMessageSummary summary) =>
            SetRead(account, folderFullName, summary, read: true);

        public static void ToggleFlagged(MailAccountData account, string folderFullName, MailMessageSummary summary)
        {
            bool nowFlagged = !summary.Flags.HasFlag(MailFlags.Flagged);
            summary.Flags = nowFlagged ? summary.Flags | MailFlags.Flagged : summary.Flags & ~MailFlags.Flagged;
            MessageStore.UpsertSummaries(account.Id, folderFullName, [summary]);
            PushFlagInBackground(account, folderFullName, summary.Uid, MessageFlags.Flagged, nowFlagged);
        }

        /// <summary>Adds or removes a tag on the message (tags are app-local, never pushed to the server).</summary>
        public static void ToggleTag(MailAccountData account, string folderFullName, MailMessageSummary summary, string tagName)
        {
            string? existing = summary.Tags.FirstOrDefault(t => t.Equals(tagName, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                summary.Tags.Remove(existing);
            else
                summary.Tags.Add(tagName);
            MessageStore.UpsertSummaries(account.Id, folderFullName, [summary]);
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

            RunServerActionInBackground(account, folderFullName, $"Server delete failed for uid {summary.Uid}", LogLevel.Error,
                async (client, folder, ct) =>
                {
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
                });
        }

        /// <summary>
        /// Optimistic bulk move to another server folder: rows leave the local store instantly,
        /// the batched IMAP MoveToAsync follows in the background (same shape as rule moves).
        /// </summary>
        public static void MoveToFolder(MailAccountData account, string folderFullName, IReadOnlyList<MailMessageSummary> summaries, string targetFullName)
        {
            MessageStore.RemoveMessages(account.Id, folderFullName, [.. summaries.Select(static s => s.Uid)]);
            RunServerActionInBackground(account, folderFullName, $"Server move to '{targetFullName}' failed", LogLevel.Error,
                async (client, folder, ct) =>
                {
                    var target = await client.GetFolderAsync(targetFullName, ct);
                    await folder.MoveToAsync([.. summaries.Select(static s => new UniqueId(s.Uid))], target, ct);
                    Log($"Moved {summaries.Count} messages from '{folderFullName}' to '{targetFullName}'.");
                });
        }

        static void PushFlagInBackground(MailAccountData account, string folderFullName, uint uid, MessageFlags flag, bool add) =>
            RunServerActionInBackground(account, folderFullName, $"Flag push failed for uid {uid}", LogLevel.Warning,
                async (_, folder, ct) =>
                {
                    if (add)
                        await folder.AddFlagsAsync(new UniqueId(uid), flag, silent: true, ct);
                    else
                        await folder.RemoveFlagsAsync(new UniqueId(uid), flag, silent: true, ct);
                });

        /// <summary>
        /// The shared fire-and-forget server push: skips POP3 and local folders, opens the folder
        /// ReadWrite under the resilience pipeline, runs the action, logs failures with the given
        /// context. Both Delete and flag pushes go through here so the guard logic exists once.
        /// </summary>
        static void RunServerActionInBackground(MailAccountData account, string folderFullName, string failContext,
            LogLevel failLevel, Func<ImapClient, IMailFolder, CancellationToken, Task> action)
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
                        await action(client, folder, ct);
                        await client.DisconnectAsync(true, ct);
                    });
                }
                catch (Exception ex)
                {
                    Log($"{failContext}: {ex.Message}", failLevel);
                }
            });
        }
    }
}
