using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>
    /// IMAP synchronization: mirrors the account's folder tree into <see cref="MessageStore"/>,
    /// fetches message summaries incrementally by UID, and downloads full MIME bodies on demand.
    /// All server calls run through <see cref="ResiliencePolicy.RunNetwork"/>.
    /// </summary>
    public static partial class ImapSyncService
    {
        /// <summary>Newest messages fetched for a folder that has never been synced (older mail loads when scrolled later).</summary>
        const int InitialFetchCount = 300;

        /// <summary>First-fill fetches land in slices this big, each painted into the UI on arrival.</summary>
        const int FirstFillSliceSize = 50;

        const MessageSummaryItems SummaryItems =
            MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags |
            MessageSummaryItems.Size | MessageSummaryItems.BodyStructure | MessageSummaryItems.PreviewText |
            MessageSummaryItems.References;


        /// <summary>Refreshes the folder list, the Inbox, and a couple of the stalest other folders.</summary>
        public static async Task SyncAccountAsync(MailAccountData account, CancellationToken cancellationToken = default)
        {
            using var syncScope = SyncScheduler.EnterSyncScope();
            Log($"IMAP sync started: {account.EmailAddress}");
            await ResiliencePolicy.RunNetwork(async ct =>
            {
                using var client = await MailConnections.OpenImapAsync(account, ct);
                await SyncFolderListAsync(account, client, ct);
                await SyncOpenedFolderAsync(account, client, client.Inbox, ct);
                await RefreshStalestFoldersAsync(account, client, ct);
                await client.DisconnectAsync(true, ct);
            }, cancellationToken, account);
            Log($"IMAP sync finished: {account.EmailAddress}");
        }

        /// <summary>
        /// Fetches messages for the stalest few server folders, riding the connection this pass
        /// already opened. Without it a folder is only ever filled by opening it, so all-folders
        /// search — which reads the cache alone — cannot find anything that arrived in a folder
        /// the user has not clicked, while the folder-list pass keeps its unread badge perfectly
        /// current. A badge saying 4 unread over a message list from three weeks ago is the
        /// worst form of that mismatch.
        /// </summary>
        static async Task RefreshStalestFoldersAsync(MailAccountData account, ImapClient client, CancellationToken cancellationToken)
        {
            int count = AccountStore.GetSettings(account.Id).BackgroundFolderRefreshCount.Value;
            if (count <= 0) return;

            var stalest = MessageStore.GetFolders(account.Id)
                .Where(f => !f.IsLocal && f.Selectable && !f.FullName.Equals(MessageStore.InboxFullName, StringComparison.OrdinalIgnoreCase))
                // A server count that disagrees with the cache is free evidence something changed,
                // so those folders go first; the rest fall back to plain age.
                .OrderByDescending(f => f.TotalCount != MessageStore.CountSummaries(account.Id, f.FullName))
                .ThenBy(f => f.LastSyncedUtc ?? DateTime.MinValue)
                .Take(count)
                .ToList();

            foreach (var folder in stalest)
            {
                var serverFolder = await client.GetFolderAsync(folder.FullName, cancellationToken);
                await SyncOpenedFolderAsync(account, client, serverFolder, cancellationToken, backgroundRefresh: true);
            }
            if (stalest.Count > 0)
                Log($"Background refresh touched: {string.Join(", ", stalest.Select(static f => f.FullName))}");
        }

        /// <summary>Syncs one folder's messages; reached through <see cref="KickFolderSync"/> when the user opens a folder.</summary>
        static async Task SyncFolderAsync(MailAccountData account, string folderFullName, CancellationToken cancellationToken = default)
        {
            using var syncScope = SyncScheduler.EnterSyncScope();
            await ResiliencePolicy.RunNetwork(async ct =>
            {
                using var client = await MailConnections.OpenImapAsync(account, ct);
                var folder = await client.GetFolderAsync(folderFullName, ct);
                await SyncOpenedFolderAsync(account, client, folder, ct);
                await client.DisconnectAsync(true, ct);
            }, cancellationToken, account);
        }

        /// <summary>Downloads the full MIME of one message into the cache and returns it parsed.</summary>
        public static async Task<MimeMessage> DownloadMessageAsync(MailAccountData account, string folderFullName, uint uid, CancellationToken cancellationToken = default)
        {
            return await ResiliencePolicy.RunNetwork(async ct =>
            {
                using var client = await MailConnections.OpenImapAsync(account, ct);
                var folder = await client.GetFolderAsync(folderFullName, ct);
                await folder.OpenAsync(FolderAccess.ReadOnly, ct);
                var message = await folder.GetMessageAsync(new UniqueId(uid), ct);

                using var buffer = new MemoryStream();
                await message.WriteToAsync(buffer, ct);
                MessageStore.SaveFullMessage(account.Id, folderFullName, uid, buffer.ToArray());

                await client.DisconnectAsync(true, ct);
                return message;
            }, cancellationToken, account);
        }

        /// <summary>
        /// Downloads the bodies of newly arrived messages that carry attachments, so opening them
        /// offline works. Only runs when the account asks for it; a failed prefetch is harmless —
        /// the reader downloads on demand exactly as before.
        /// </summary>
        static async Task PrefetchAttachmentBodiesAsync(MailAccountData account, string folderFullName,
            List<MailMessageSummary> arrived, CancellationToken cancellationToken)
        {
            if (!AccountStore.GetSettings(account.Id).DownloadAttachmentsAutomatically.Value) return;

            foreach (var summary in arrived.Where(static s => s.HasAttachments))
            {
                if (MessageStore.HasFullMessage(account.Id, folderFullName, summary.Uid)) continue;
                try
                {
                    await DownloadMessageAsync(account, folderFullName, summary.Uid, cancellationToken);
                    Log($"Prefetched attachment body for uid {summary.Uid} in '{folderFullName}'.");
                }
                catch (Exception ex)
                {
                    Log($"Attachment prefetch failed for uid {summary.Uid}: {ex.Message}", LogLevel.Warning);
                }
            }
        }


        /// <param name="backgroundRefresh">
        /// True for a folder the rotation picked rather than the user. Rules and notifications are
        /// skipped: a toast for mail landing in Sent or Trash is noise, and RuleEngine.ProcessIncoming
        /// is not folder-scoped, so a rotation pass would otherwise start executing move actions on
        /// Junk and Trash arrivals in the background with nobody watching. Rotation is a pure refresh.
        /// </param>
        static async Task SyncOpenedFolderAsync(MailAccountData account, ImapClient client, IMailFolder folder, CancellationToken cancellationToken, bool backgroundRefresh = false)
        {
            // A folder the policy does not pull is never opened at all — that is what makes
            // "drafts live on this machine" a rule rather than an accident of which code path ran.
            if (!FolderSyncPolicy.PullsFromServer(account.Id, folder.FullName))
            {
                Log($"'{folder.FullName}' is not fetched from the server ({FolderSyncPolicy.For(account.Id, folder.FullName)}).");
                return;
            }

            await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

            var cached = MessageStore.GetFolders(account.Id).FirstOrDefault(f => f.FullName == folder.FullName);
            uint lastSeenUid = cached?.LastSeenUid ?? 0;

            // A changed UIDVALIDITY invalidates every cached UID of the folder — start over.
            if (cached != null && cached.UidValidity != 0 && cached.UidValidity != folder.UidValidity)
            {
                MessageStore.RemoveMessages(account.Id, folder.FullName, [.. MessageStore.GetSummaries(account.Id, folder.FullName).Select(s => s.Uid)]);
                lastSeenUid = 0;
            }

            uint maxUid = lastSeenUid;
            // Backfilled from the cache when the field is missing, so folders filled before this
            // existed get a floor without a re-sync.
            uint oldestFetchedUid = cached?.OldestFetchedUid ?? 0;
            if (oldestFetchedUid == 0 && MessageStore.GetSummaries(account.Id, folder.FullName) is { Count: > 0 } cachedSummaries)
                oldestFetchedUid = cachedSummaries.Min(static s => s.Uid);
            List<MailMessageSummary> summaries = [];
            if (lastSeenUid == 0)
            {
                // First fill: newest slice first, each slice stored (and painted) as it lands
                // instead of after the whole fetch finishes.
                int startIndex = Math.Max(0, folder.Count - InitialFetchCount);
                for (int sliceEnd = folder.Count - 1; sliceEnd >= startIndex; sliceEnd -= FirstFillSliceSize)
                {
                    int sliceStart = Math.Max(startIndex, sliceEnd - FirstFillSliceSize + 1);
                    var slice = await folder.FetchAsync(sliceStart, sliceEnd, SummaryItems, cancellationToken);
                    List<MailMessageSummary> sliceSummaries = [.. slice.Where(static i => i.UniqueId.IsValid).Select(ToSummary)];
                    if (sliceSummaries.Count == 0) continue;
                    MessageStore.UpsertSummaries(account.Id, folder.FullName, sliceSummaries);
                    summaries.AddRange(sliceSummaries);
                    maxUid = Math.Max(maxUid, sliceSummaries.Max(static s => s.Uid));
                    uint sliceLowest = sliceSummaries.Min(static s => s.Uid);
                    oldestFetchedUid = oldestFetchedUid == 0 ? sliceLowest : Math.Min(oldestFetchedUid, sliceLowest);
                }
            }
            else
            {
                var range = new UniqueIdRange(new UniqueId(lastSeenUid + 1), UniqueId.MaxValue);
                var fetched = await folder.FetchAsync(range, SummaryItems, cancellationToken);
                foreach (var item in fetched)
                {
                    if (!item.UniqueId.IsValid) continue;
                    summaries.Add(ToSummary(item));
                    maxUid = Math.Max(maxUid, item.UniqueId.Id);
                }
            }

            // "Fetched" is not "arrived". An IMAP range of <lastSeenUid+1>:* ALWAYS returns the
            // last message in the mailbox, even when its uid is below the range (RFC 3501), so a
            // folder with nothing new still hands back one summary. Gating the rule pass on
            // summaries.Count meant the newest message was re-processed on every single sync: the
            // audit collected sixteen copies of the same four actions, mark-read/mute/sound were
            // re-applied forever, and a move whose target folder does not exist was retried 48
            // times in one session. Everything that must happen once per arrival hangs off THIS
            // list; nothing hangs off `summaries`.
            List<MailMessageSummary> arrived = lastSeenUid == 0
                ? []
                : [.. summaries.Where(s => s.Uid > lastSeenUid)];
            Log($"IMAP folder '{folder.FullName}': fetched {summaries.Count} summaries ({arrived.Count} new), server count {folder.Count}");

            // Incoming rules run on genuinely NEW mail only (never on the first bulk import).
            RuleProcessResult? ruleResult = null;
            if (arrived.Count > 0 && !backgroundRefresh)
            {
                // Before the rules: a reply to a muted conversation must never reach the
                // notification check as a normal arrival.
                MuteService.ApplyToIncoming(arrived);
                ruleResult = RuleEngine.ProcessIncoming(account, folder.FullName, arrived);
                MessageStore.UpsertSummaries(account.Id, folder.FullName, arrived);
            }

            var notificationCandidates = ruleResult == null ? arrived
                : NotificationService.ExcludeMutedRuleDestinations(account, arrived, ruleResult);
            if (ruleResult != null)
                await ExecuteRuleMovesAsync(account, client, folder, ruleResult, cancellationToken);

            MessageStore.SaveFolder(new MailFolderData
            {
                AccountId = account.Id,
                FullName = folder.FullName,
                DisplayName = folder.Name,
                Role = cached?.Role ?? (folder.FullName.Equals(MessageStore.InboxFullName, StringComparison.OrdinalIgnoreCase) ? FolderRole.Inbox : FolderRole.None),
                Delimiter = folder.DirectorySeparator,
                UidValidity = folder.UidValidity,
                LastSeenUid = maxUid,
                OldestFetchedUid = oldestFetchedUid,
                LastSyncedUtc = DateTime.UtcNow,
                TotalCount = folder.Count,
                UnreadCount = folder.Unread
            });

            if (arrived.Count > 0 && !backgroundRefresh)
            {
                NotificationService.NotifyNewMessages(account, folder.FullName, notificationCandidates);
                await PrefetchAttachmentBodiesAsync(account, folder.FullName, arrived, cancellationToken);
            }
        }

        /// <summary>Executes the move requests a rule pass produced — local ones via the store, remote ones over the still-open connection.</summary>
        static async Task ExecuteRuleMovesAsync(MailAccountData account, ImapClient client, IMailFolder folder, RuleProcessResult ruleResult, CancellationToken cancellationToken)
        {
            foreach (var (uid, localFolder) in ruleResult.LocalMoves)
                MessageStore.MoveCachedMessage(account.Id, folder.FullName, uid, MessageStore.LocalFolderPrefix + localFolder);

            if (ruleResult.RemoteMoves.Count == 0) return;

            // Off means "the server files it, this machine keeps its own filing" — the local copy
            // stays where it is instead of following the move.
            bool mirrorLocally = AccountStore.GetSettings(account.Id).MirrorRuleMovesLocally.Value;

            await folder.OpenAsync(FolderAccess.ReadWrite, cancellationToken);
            foreach (var moveGroup in ruleResult.RemoteMoves.GroupBy(static m => m.TargetFolder))
            {
                List<UniqueId> sourceUids = [.. moveGroup.Select(static m => new UniqueId(m.Uid))];
                try
                {
                    var targetFolder = await client.GetFolderAsync(moveGroup.Key, cancellationToken);
                    var moved = await folder.MoveToAsync(sourceUids, targetFolder, cancellationToken);
                    if (!mirrorLocally) continue;

                    foreach (var source in sourceUids)
                    {
                        // The message used to be deleted from the source and written nowhere, so
                        // it vanished from the app until the destination folder's own sync ran —
                        // and with the destination folder unlisted, that never happened at all.
                        if (moved.TryGetValue(source, out var destination))
                            MessageStore.MoveCachedMessage(account.Id, folder.FullName, source.Id, targetFolder.FullName, destination.Id);
                        else
                            // No UIDPLUS: the destination's uid is unknowable here, so the row is
                            // dropped and the destination folder's next sync brings it back.
                            MessageStore.RemoveMessages(account.Id, folder.FullName, [source.Id]);
                    }
                }
                catch (Exception ex)
                {
                    Log($"Rule move to '{moveGroup.Key}' failed: {ex.Message}", LogLevel.Error);
                }
            }
        }

        static MailMessageSummary ToSummary(IMessageSummary item)
        {
            var from = item.Envelope?.From?.Mailboxes?.FirstOrDefault();
            return new MailMessageSummary
            {
                Uid = item.UniqueId.Id,
                MessageId = item.Envelope?.MessageId ?? string.Empty,
                InReplyTo = item.Envelope?.InReplyTo ?? string.Empty,
                ReferenceIds = item.References == null ? [] : [.. item.References],
                Subject = item.Envelope?.Subject ?? string.Empty,
                FromName = from?.Name ?? string.Empty,
                FromAddress = from?.Address ?? string.Empty,
                ToAddresses = item.Envelope?.To == null ? string.Empty : string.Join(", ", item.Envelope.To.Mailboxes.Select(m => m.Address)),
                DateUtc = item.Envelope?.Date?.UtcDateTime ?? item.InternalDate?.UtcDateTime ?? DateTime.UtcNow,
                Flags = ToMailFlags(item.Flags),
                HasAttachments = item.Attachments.Any(),
                SizeBytes = item.Size ?? 0,
                PreviewText = item.PreviewText ?? string.Empty
            };
        }

        static MailFlags ToMailFlags(MessageFlags? flags)
        {
            if (flags == null) return MailFlags.None;
            var result = MailFlags.None;
            if (flags.Value.HasFlag(MessageFlags.Seen)) result |= MailFlags.Seen;
            if (flags.Value.HasFlag(MessageFlags.Answered)) result |= MailFlags.Answered;
            if (flags.Value.HasFlag(MessageFlags.Flagged)) result |= MailFlags.Flagged;
            if (flags.Value.HasFlag(MessageFlags.Deleted)) result |= MailFlags.Deleted;
            if (flags.Value.HasFlag(MessageFlags.Draft)) result |= MailFlags.Draft;
            return result;
        }
    }
}
