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
            if (MessageStore.SetSeen(account.Id, folderFullName, [summary], read) == 0) return;
            PushFlagInBackground(account, folderFullName, summary.Uid, MessageFlags.Seen, add: read);
        }

        /// <summary>Marks an unread message as read — the "opened in reader" path.</summary>
        public static void MarkRead(MailAccountData account, string folderFullName, MailMessageSummary summary) =>
            SetRead(account, folderFullName, summary, read: true);

        /// <summary>
        /// Marks a whole set read in one store write and one server push, and returns how many
        /// changed. Calling <see cref="SetRead"/> in a loop costs an index rewrite AND its own IMAP
        /// connection per message, so clearing a 400-unread folder meant 400 of each.
        /// Already-read messages are skipped, so snoozed rows (SnoozeService marks them Seen)
        /// cannot be woken by accident.
        /// </summary>
        public static int SetManyRead(MailAccountData account, string folderFullName, IEnumerable<MailMessageSummary> summaries)
        {
            List<MailMessageSummary> changed = [.. summaries.Where(static s => s.IsUnread)];
            if (MessageStore.SetSeen(account.Id, folderFullName, changed, seen: true) == 0) return 0;

            uint[] uids = [.. changed.Select(static s => s.Uid)];
            MessageStore.MarkFlagPushPending(account.Id, folderFullName, uids);
            RunServerActionInBackground(account, folderFullName, $"Batched read push failed in '{folderFullName}'", LogLevel.Warning,
                async (_, folder, ct) =>
                {
                    await folder.AddFlagsAsync([.. uids.Select(static uid => new UniqueId(uid))], MessageFlags.Seen, silent: true, ct);
                    Log($"Marked {changed.Count} messages read in '{folderFullName}' with one push.");
                },
                () => MessageStore.ClearFlagPushPending(account.Id, folderFullName, uids));
            return changed.Count;
        }

        /// <summary>Marks everything cached in the folder read.</summary>
        public static int SetFolderRead(MailAccountData account, string folderFullName) =>
            SetManyRead(account, folderFullName, MessageStore.GetSummaries(account.Id, folderFullName));

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

        /// <summary>Mutes or unmutes the whole conversation — app-local, never pushed to a server.</summary>
        public static void ToggleMuted(MailAccountData account, string folderFullName, MailMessageSummary summary) =>
            MuteService.ToggleThread(account, folderFullName, summary);

        /// <summary>
        /// Adds or removes a tag across a whole selection with ONE index write instead of one per
        /// message. Tagging was the only local triage action with no bulk path, so filing forty
        /// receipts meant opening forty messages and using the reader picker forty times.
        /// Tags never leave the machine, so there is no server push here.
        /// </summary>
        public static int SetTagOnMany(MailAccountData account, string folderFullName,
            IEnumerable<MailMessageSummary> summaries, string tagName, bool add)
        {
            if (tagName.Length == 0) return 0;

            List<MailMessageSummary> changed = [];
            foreach (var summary in summaries)
            {
                string? existing = summary.Tags.FirstOrDefault(t => t.Equals(tagName, StringComparison.OrdinalIgnoreCase));
                if (add == (existing != null)) continue;

                if (add) summary.Tags.Add(tagName); else summary.Tags.Remove(existing!);
                changed.Add(summary);
            }
            if (changed.Count == 0) return 0;

            // These are the STORED instances, so the store's reference check skips the local-state
            // carry-over — which is what lets removing the last tag stick instead of being put back.
            MessageStore.UpsertSummaries(account.Id, folderFullName, changed);
            Log($"{(add ? "Tagged" : "Untagged")} {changed.Count} message(s) '{tagName}' in '{folderFullName}'.");
            return changed.Count;
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
                        // Targeted expunge: the parameterless ExpungeAsync would purge EVERY
                        // \Deleted message in the folder, including ones another client soft-deleted.
                        await folder.ExpungeAsync([uid], ct);
                    }
                });
        }

        /// <summary>Bulk move into an app-local folder; the server copy (if any) stays untouched by design.</summary>
        public static void MoveToLocalFolder(MailAccountData account, string folderFullName, IReadOnlyList<MailMessageSummary> summaries, string localFolderName)
        {
            foreach (var summary in summaries)
                MessageStore.MoveToLocalFolder(account.Id, folderFullName, summary.Uid, localFolderName);
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

        static void PushFlagInBackground(MailAccountData account, string folderFullName, uint uid, MessageFlags flag, bool add)
        {
            // Held from the click until the server has been told: a sync landing in between reports
            // the state from before the change, and without this it would win.
            MessageStore.MarkFlagPushPending(account.Id, folderFullName, [uid]);
            RunServerActionInBackground(account, folderFullName, $"Flag push failed for uid {uid}", LogLevel.Warning,
                async (_, folder, ct) =>
                {
                    if (add)
                        await folder.AddFlagsAsync(new UniqueId(uid), flag, silent: true, ct);
                    else
                        await folder.RemoveFlagsAsync(new UniqueId(uid), flag, silent: true, ct);
                },
                () => MessageStore.ClearFlagPushPending(account.Id, folderFullName, [uid]));
        }

        /// <summary>
        /// The shared fire-and-forget server push: skips POP3 and local folders, opens the folder
        /// ReadWrite under the resilience pipeline, runs the action, logs failures with the given
        /// context. Both Delete and flag pushes go through here so the guard logic exists once.
        /// </summary>
        /// <param name="whenSettled">Runs after the push finished, succeeded or not, and after the
        /// policy gate refused it — anything held for the duration of the push is released here.</param>
        static void RunServerActionInBackground(MailAccountData account, string folderFullName, string failContext,
            LogLevel failLevel, Func<ImapClient, IMailFolder, CancellationToken, Task> action, Action? whenSettled = null)
        {
            if (account.Protocol != IncomingProtocol.Imap || folderFullName.StartsWith(MessageStore.LocalFolderPrefix))
            {
                whenSettled?.Invoke();
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    // The one gate every local-to-server write passes through. Read inside the task
                    // because it walks the folder list, and this is called straight off a click.
                    if (!FolderSyncPolicy.PushesToServer(account.Id, folderFullName))
                    {
                        Log($"'{folderFullName}' does not push to the server ({FolderSyncPolicy.For(account.Id, folderFullName)}); the change stays local.");
                        return;
                    }

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
                finally
                {
                    whenSettled?.Invoke();
                }
            });
        }
    }
}
