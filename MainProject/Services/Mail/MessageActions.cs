using MailKit;
using MailKit.Net.Imap;
using System.Collections.Concurrent;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>
    /// Cached flag edits apply immediately; server moves retain the cached source until confirmed.
    /// Single-message and grouped input commands share these storage operations and server pipeline.
    /// </summary>
    public static class MessageActions
    {
        const bool IsLogEnabled = true;
        static readonly ConcurrentDictionary<MailMessageIdentity, byte> MovingMessages = [];
        public static event Action? OnMoveStateChanged;

        /// <summary>Source mail remains cached while its server move is queued; the UI hides it until the outcome is known.</summary>
        /// <param name="accountId">Owning account.</param>
        /// <param name="folderFullName">Source folder.</param>
        /// <param name="uid">Source message number.</param>
        /// <returns>Whether a move already owns this message.</returns>
        public static bool IsMoving(string accountId, string folderFullName, uint uid) =>
            MovingMessages.ContainsKey(new MailMessageIdentity(accountId, folderFullName, uid));
        public static void ToggleRead(MailAccountData account, string folderFullName, MailMessageSummary summary) =>
            SetRead(account, folderFullName, summary, !summary.Flags.HasFlag(MailFlags.Seen));

        /// <summary>Forces the read state to <paramref name="read"/> (no-op when already there) — bulk-action friendly.</summary>
        public static void SetRead(MailAccountData account, string folderFullName, MailMessageSummary summary, bool read)
        {
            SetManyRead(account, folderFullName, [summary], read);
        }

        /// <summary>Marks an unread message as read — the "opened in reader" path.</summary>
        public static void MarkRead(MailAccountData account, string folderFullName, MailMessageSummary summary) =>
            SetRead(account, folderFullName, summary, read: true);

        /// <summary>
        /// Sets a whole selection's read state in one store write and one server push, returning how many
        /// changed. Calling <see cref="SetRead"/> in a loop costs an index rewrite AND its own IMAP
        /// connection per message, so clearing a 400-unread folder meant 400 of each.
        /// Messages already in the requested state are skipped.
        /// </summary>
        /// <param name="account">Owning account.</param>
        /// <param name="folderFullName">Exact folder scope.</param>
        /// <param name="summaries">Messages to update together.</param>
        /// <param name="read">True marks read; false marks unread.</param>
        /// <returns>Number whose state changed.</returns>
        public static int SetManyRead(MailAccountData account, string folderFullName, IEnumerable<MailMessageSummary> summaries, bool read = true)
        {
            List<MailMessageSummary> changed = [.. summaries.Where(summary => summary.IsUnread == read)];
            if (MessageStore.SetSeen(account.Id, folderFullName, changed, seen: read) == 0) return 0;

            uint[] uids = [.. changed.Select(static s => s.Uid)];
            PushFlagsInBackground(account, folderFullName, uids, MessageFlags.Seen, read);
            Log($"Set read={read} on {changed.Count} message(s) in '{folderFullName}' with one push.", Run: IsLogEnabled);
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
            PushFlagsInBackground(account, folderFullName, [summary.Uid], MessageFlags.Flagged, nowFlagged);
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
        /// Applies the account's delete preference through the same grouped operation as input commands.
        /// </summary>
        public static void Delete(MailAccountData account, string folderFullName, MailMessageSummary summary)
        {
            Delete(account, folderFullName, [summary]);
        }

        /// <summary>Files a selection according to the account preference or an explicit Trash command.</summary>
        /// <param name="account">Owning account.</param>
        /// <param name="folderFullName">Source folder.</param>
        /// <param name="summaries">Messages to file or delete.</param>
        /// <param name="behavior">Null follows account settings; MoveToTrash never expunges mail.</param>
        /// <returns>Number of messages accepted for the operation.</returns>
        public static int Delete(MailAccountData account, string folderFullName, IReadOnlyList<MailMessageSummary> summaries,
            DeleteBehavior? behavior = null)
        {
            var selectedBehavior = behavior ?? AccountStore.GetSettings(account.Id).DeleteAction.Value;
            if (selectedBehavior != DeleteBehavior.DeletePermanently)
                return MoveToRole(account, folderFullName, summaries,
                    selectedBehavior == DeleteBehavior.ArchiveInstead ? FolderRole.Archive : FolderRole.Trash);
            uint[] uids = [.. summaries.Select(static summary => summary.Uid)];
            MessageStore.RemoveMessages(account.Id, folderFullName, uids);
            RunServerActionInBackground(account, folderFullName, $"Server delete failed in '{folderFullName}'", LogLevel.Error,
                async (client, folder, ct) =>
                {
                    List<UniqueId> serverUids = [.. uids.Select(static uid => new UniqueId(uid))];
                    await folder.AddFlagsAsync(serverUids, MessageFlags.Deleted, silent: true, ct);
                    // Only the explicitly requested numbers are expunged.
                    await folder.ExpungeAsync(serverUids, ct);
                });
            return uids.Length;
        }

        /// <summary>Chooses a standard destination, retaining local mail when no writable server destination exists.</summary>
        /// <param name="account">Owning account.</param>
        /// <param name="folderFullName">Source folder.</param>
        /// <param name="summaries">Messages to file.</param>
        /// <param name="role">Destination role.</param>
        /// <returns>Zero if already filed there; otherwise the number accepted.</returns>
        public static int MoveToRole(MailAccountData account, string folderFullName,
            IReadOnlyList<MailMessageSummary> summaries, FolderRole role)
        {
            var folders = MessageStore.GetFolders(account.Id);
            if (folders.Any(folder => folder.FullName == folderFullName && folder.Role == role)) return 0;
            bool local = account.Protocol != IncomingProtocol.Imap || MessageStore.IsLocalFolder(folderFullName)
                || !FolderSyncPolicy.PushesToServer(account.Id, folderFullName);
            var target = folders.FirstOrDefault(folder => folder.Role == role && folder.IsLocal == local && folder.Selectable);
            if (target == null)
            {
                // A missing server role must never turn a Trash request into permanent deletion.
                string name = role == FolderRole.Junk ? "Spam" : role.ToString();
                target = new MailFolderData { AccountId = account.Id, FullName = MessageStore.LocalFolderPrefix + name,
                    DisplayName = name, Role = role, IsLocal = true };
                MessageStore.SaveFolder(target);
            }
            int accepted;
            if (target.IsLocal)
            {
                MoveToLocalFolder(account, folderFullName, summaries, target.FullName[MessageStore.LocalFolderPrefix.Length..]);
                accepted = summaries.Count;
            }
            else
                accepted = MoveToFolder(account, folderFullName, summaries, target.FullName);
            Log($"Move requested: account={account.Id}; from='{folderFullName}'; to='{target.FullName}'; count={accepted}.", Run: IsLogEnabled);
            return accepted;
        }

        /// <summary>Bulk move into an app-local folder; the server copy (if any) stays untouched by design.</summary>
        public static void MoveToLocalFolder(MailAccountData account, string folderFullName, IReadOnlyList<MailMessageSummary> summaries, string localFolderName)
        {
            foreach (var summary in summaries)
                MessageStore.MoveCachedMessage(account.Id, folderFullName, summary.Uid, MessageStore.LocalFolderPrefix + localFolderName);
        }

        /// <summary>
        /// Moves on the server before moving the cached copy under the confirmed destination number.
        /// A failed server request leaves the source available; servers without a number mapping are re-fetched.
        /// </summary>
        /// <param name="account">Owning account.</param>
        /// <param name="folderFullName">Source server folder.</param>
        /// <param name="summaries">Messages to queue once.</param>
        /// <param name="targetFullName">Server destination path.</param>
        /// <returns>Number newly queued; duplicate in-flight requests return zero.</returns>
        public static int MoveToFolder(MailAccountData account, string folderFullName, IReadOnlyList<MailMessageSummary> summaries, string targetFullName)
        {
            if (folderFullName == targetFullName) return 0;
            if (account.Protocol != IncomingProtocol.Imap || MessageStore.IsLocalFolder(folderFullName))
                throw new InvalidOperationException("Mail in a local folder cannot be uploaded by a server-folder move.");
            uint[] uids = [.. summaries.Select(static summary => summary.Uid).Distinct()
                .Where(uid => MovingMessages.TryAdd(new MailMessageIdentity(account.Id, folderFullName, uid), 0))];
            if (uids.Length == 0) return 0;
            OnMoveStateChanged?.Invoke();
            RunServerActionInBackground(account, folderFullName, $"Server move to '{targetFullName}' failed", LogLevel.Error,
                async (client, folder, ct) =>
                {
                    var target = await client.GetFolderAsync(targetFullName, ct);
                    var moved = await folder.MoveToAsync([.. uids.Select(static uid => new UniqueId(uid))], target, ct);
                    foreach (uint uid in uids)
                    {
                        if (moved.TryGetValue(new UniqueId(uid), out var destination))
                            MessageStore.MoveCachedMessage(account.Id, folderFullName, uid, targetFullName, destination.Id);
                        else
                            MessageStore.RemoveMessages(account.Id, folderFullName, [uid]);
                    }
                    ImapSyncService.KickFolderSync(account, targetFullName);
                    Log($"Moved {summaries.Count} messages from '{folderFullName}' to '{targetFullName}'.");
                }, () =>
                {
                    foreach (uint uid in uids) MovingMessages.TryRemove(new MailMessageIdentity(account.Id, folderFullName, uid), out _);
                    OnMoveStateChanged?.Invoke();
                });
            return uids.Length;
        }

        /// <summary>Resolves complete identities and batches each account/folder once for every input surface.</summary>
        /// <param name="command">Requested mail operation.</param>
        /// <param name="identities">A snapshot of the selected or clicked messages.</param>
        /// <param name="argument">Tag name or destination folder for the commands that need one.</param>
        /// <param name="dueUtc">Snooze deadline; null uses the first existing preset.</param>
        /// <returns>Number changed or accepted for asynchronous server filing.</returns>
        public static int Execute(MailMessageCommand command, IEnumerable<MailMessageIdentity> identities,
            string? argument = null, DateTime? dueUtc = null)
        {
            int changed = 0;
            foreach (var group in identities.Distinct().GroupBy(static key => (key.AccountId, key.FolderFullName)))
            {
                var account = AccountStore.GetById(group.Key.AccountId)
                    ?? throw new InvalidOperationException("The selected account no longer exists.");
                string folder = group.Key.FolderFullName;
                List<MailMessageSummary> messages = [.. group.Select(key => MessageStore.GetSummary(account.Id, folder, key.Uid))
                    .OfType<MailMessageSummary>()];
                if (messages.Count != group.Count())
                    Log($"Mail command skipped missing messages: command={command}; account={account.Id}; folder='{folder}'; requested={group.Count()}; found={messages.Count}.", LogLevel.Warning, Run: IsLogEnabled);
                if (messages.Count == 0) continue;
                switch (command)
                {
                    case MailMessageCommand.Read:
                    case MailMessageCommand.Unread:
                        changed += SetManyRead(account, folder, messages, command == MailMessageCommand.Read);
                        break;
                    case MailMessageCommand.Trash:
                        changed += Delete(account, folder, messages, DeleteBehavior.MoveToTrash);
                        break;
                    case MailMessageCommand.Archive:
                    case MailMessageCommand.Spam:
                    case MailMessageCommand.Restore:
                        changed += MoveToRole(account, folder, messages, command switch
                        {
                            MailMessageCommand.Archive => FolderRole.Archive,
                            MailMessageCommand.Spam => FolderRole.Junk,
                            _ => FolderRole.Inbox
                        });
                        break;
                    case MailMessageCommand.AddTag:
                    case MailMessageCommand.RemoveTag:
                        string tag = argument?.Trim() ?? string.Empty;
                        if (tag.Length == 0) throw new ArgumentException("Choose a tag first.", nameof(argument));
                        if (command == MailMessageCommand.AddTag) TagStore.ColorOf(tag);
                        changed += SetTagOnMany(account, folder, messages, tag, command == MailMessageCommand.AddTag);
                        break;
                    case MailMessageCommand.Move:
                        var target = MessageStore.GetFolders(account.Id).FirstOrDefault(target => target.FullName == argument && target.Selectable)
                            ?? throw new InvalidOperationException("The destination folder no longer exists.");
                        if (target.FullName == folder) break;
                        if (target.IsLocal)
                        {
                            MoveToLocalFolder(account, folder, messages, target.FullName[MessageStore.LocalFolderPrefix.Length..]);
                            changed += messages.Count;
                        }
                        else
                            changed += MoveToFolder(account, folder, messages, target.FullName);
                        break;
                    default:
                        foreach (var message in messages)
                        {
                            switch (command)
                            {
                                case MailMessageCommand.ToggleStar: ToggleFlagged(account, folder, message); break;
                                case MailMessageCommand.ToggleImportant: ToggleImportant(account, folder, message); break;
                                case MailMessageCommand.Snooze: SnoozeService.Snooze(account, folder, message, dueUtc ?? SnoozeService.Presets[0].DueUtc()); break;
                                case MailMessageCommand.Wake when message.SnoozedUntilUtc != null: SnoozeService.Wake(account, folder, message); break;
                                case MailMessageCommand.Wake: continue;
                                default: throw new ArgumentOutOfRangeException(nameof(command));
                            }
                            changed++;
                        }
                        break;
                }
            }
            Log($"Mail command finished: command={command}; count={changed}; argument='{argument}'.", Run: IsLogEnabled);
            return changed;
        }

        static void PushFlagsInBackground(MailAccountData account, string folderFullName, uint[] uids, MessageFlags flag, bool add)
        {
            // Held from the click until the server has been told: a sync landing in between reports
            // the state from before the change, and without this it would win.
            MessageStore.MarkFlagPushPending(account.Id, folderFullName, uids);
            RunServerActionInBackground(account, folderFullName, $"Flag push failed in '{folderFullName}'", LogLevel.Warning,
                async (_, folder, ct) =>
                {
                    if (add)
                        await folder.AddFlagsAsync([.. uids.Select(static uid => new UniqueId(uid))], flag, silent: true, ct);
                    else
                        await folder.RemoveFlagsAsync([.. uids.Select(static uid => new UniqueId(uid))], flag, silent: true, ct);
                },
                () => MessageStore.ClearFlagPushPending(account.Id, folderFullName, uids));
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
                    }, account: account);
                }
                catch (Exception ex)
                {
                    Log($"{failContext}: {ex}", failLevel, Run: IsLogEnabled);
                }
                finally
                {
                    whenSettled?.Invoke();
                }
            });
        }
    }
}
