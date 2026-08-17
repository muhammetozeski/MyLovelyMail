using MailKit;
using MailKit.Net.Imap;
using MimeKit;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>
    /// IMAP synchronization: mirrors the account's folder tree into <see cref="MessageStore"/>,
    /// fetches message summaries incrementally by UID, and downloads full MIME bodies on demand.
    /// All server calls run through <see cref="ResiliencePolicy.RunNetwork"/>.
    /// </summary>
    public static class ImapSyncService
    {
        /// <summary>Newest messages fetched for a folder that has never been synced (older mail loads when scrolled later).</summary>
        const int InitialFetchCount = 300;

        const MessageSummaryItems SummaryItems =
            MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags |
            MessageSummaryItems.Size | MessageSummaryItems.BodyStructure | MessageSummaryItems.PreviewText;

        /// <summary>Raised after a sync stored NEW messages: (accountId, folderFullName, newMessageCount).</summary>
        public static event Action<string, string, int>? OnNewMail;

        /// <summary>Refreshes the folder list and the Inbox contents of the account.</summary>
        public static async Task SyncAccountAsync(MailAccountData account, CancellationToken cancellationToken = default)
        {
            Log($"IMAP sync started: {account.EmailAddress}");
            await ResiliencePolicy.RunNetwork(async ct =>
            {
                using var client = await MailConnections.OpenImapAsync(account, ct);
                await SyncFolderListAsync(account, client, ct);
                await SyncOpenedFolderAsync(account, client, client.Inbox, ct);
                await client.DisconnectAsync(true, ct);
            }, cancellationToken);
            Log($"IMAP sync finished: {account.EmailAddress}");
        }

        /// <summary>Syncs one folder's messages (used when the user opens a folder).</summary>
        public static async Task SyncFolderAsync(MailAccountData account, string folderFullName, CancellationToken cancellationToken = default)
        {
            await ResiliencePolicy.RunNetwork(async ct =>
            {
                using var client = await MailConnections.OpenImapAsync(account, ct);
                var folder = await client.GetFolderAsync(folderFullName, ct);
                await SyncOpenedFolderAsync(account, client, folder, ct);
                await client.DisconnectAsync(true, ct);
            }, cancellationToken);
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
            }, cancellationToken);
        }

        static async Task SyncFolderListAsync(MailAccountData account, ImapClient client, CancellationToken cancellationToken)
        {
            var serverFolders = await client.GetFoldersAsync(client.PersonalNamespaces[0], false, cancellationToken);
            var cachedFolders = MessageStore.GetFolders(account.Id);

            foreach (var folder in serverFolders)
            {
                if (folder.Attributes.HasFlag(FolderAttributes.NonExistent) || folder.Attributes.HasFlag(FolderAttributes.NoSelect))
                    continue;

                var status = StatusItems.Count | StatusItems.Unread | StatusItems.UidValidity;
                await folder.StatusAsync(status, cancellationToken);

                MessageStore.SaveFolder(new MailFolderData
                {
                    AccountId = account.Id,
                    FullName = folder.FullName,
                    DisplayName = folder.Name,
                    Role = ResolveRole(client, folder),
                    UidValidity = folder.UidValidity,
                    // Refreshing counts must never erase sync progress: dropping LastSeenUid to 0
                    // here re-imported "the newest 300" as brand-new on EVERY pass, which both
                    // wasted traffic and kept the new-mail notification condition permanently false.
                    LastSeenUid = cachedFolders.FirstOrDefault(f => f.FullName == folder.FullName)?.LastSeenUid ?? 0,
                    TotalCount = folder.Count,
                    UnreadCount = folder.Unread
                });
            }
        }

        static FolderRole ResolveRole(ImapClient client, IMailFolder folder)
        {
            if (folder == client.Inbox) return FolderRole.Inbox;
            if (folder.Attributes.HasFlag(FolderAttributes.Sent)) return FolderRole.Sent;
            if (folder.Attributes.HasFlag(FolderAttributes.Drafts)) return FolderRole.Drafts;
            if (folder.Attributes.HasFlag(FolderAttributes.Trash)) return FolderRole.Trash;
            if (folder.Attributes.HasFlag(FolderAttributes.Junk)) return FolderRole.Junk;
            if (folder.Attributes.HasFlag(FolderAttributes.Archive)) return FolderRole.Archive;
            if (folder.Attributes.HasFlag(FolderAttributes.Flagged)) return FolderRole.Flagged;
            if (folder.Attributes.HasFlag(FolderAttributes.All)) return FolderRole.AllMail;
            return GuessRoleFromName(folder.Name);
        }

        /// <summary>
        /// Servers without SPECIAL-USE (RFC 6154) mark nothing, leaving every folder role-less —
        /// which duplicates Sent handling and loses icons. Fall back to the near-universal names.
        /// </summary>
        internal static FolderRole GuessRoleFromName(string folderName) => folderName.ToLowerInvariant() switch
        {
            "sent" or "sent items" or "sent mail" or "sent messages" => FolderRole.Sent,
            "drafts" or "draft" => FolderRole.Drafts,
            "trash" or "deleted" or "deleted items" or "bin" => FolderRole.Trash,
            "junk" or "spam" or "junk e-mail" => FolderRole.Junk,
            "archive" or "archives" => FolderRole.Archive,
            "outbox" => FolderRole.Outbox,
            _ => FolderRole.None
        };

        static async Task SyncOpenedFolderAsync(MailAccountData account, ImapClient client, IMailFolder folder, CancellationToken cancellationToken)
        {
            await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

            var cached = MessageStore.GetFolders(account.Id).FirstOrDefault(f => f.FullName == folder.FullName);
            uint lastSeenUid = cached?.LastSeenUid ?? 0;

            // A changed UIDVALIDITY invalidates every cached UID of the folder — start over.
            if (cached != null && cached.UidValidity != 0 && cached.UidValidity != folder.UidValidity)
            {
                MessageStore.RemoveMessages(account.Id, folder.FullName, [.. MessageStore.GetSummaries(account.Id, folder.FullName).Select(s => s.Uid)]);
                lastSeenUid = 0;
            }

            IList<IMessageSummary> fetched;
            if (lastSeenUid == 0)
            {
                int startIndex = Math.Max(0, folder.Count - InitialFetchCount);
                fetched = folder.Count == 0
                    ? []
                    : await folder.FetchAsync(startIndex, -1, SummaryItems, cancellationToken);
            }
            else
            {
                var range = new UniqueIdRange(new UniqueId(lastSeenUid + 1), UniqueId.MaxValue);
                fetched = await folder.FetchAsync(range, SummaryItems, cancellationToken);
            }

            int newCount = 0;
            uint maxUid = lastSeenUid;
            List<MailMessageSummary> summaries = [];
            foreach (var item in fetched)
            {
                if (!item.UniqueId.IsValid) continue;
                summaries.Add(ToSummary(item));
                maxUid = Math.Max(maxUid, item.UniqueId.Id);
                if (item.UniqueId.Id > lastSeenUid) newCount++;
            }
            Log($"IMAP folder '{folder.FullName}': fetched {summaries.Count} summaries ({newCount} new), server count {folder.Count}");

            // Incoming rules run on genuinely NEW mail only (never on the first bulk import).
            RuleProcessResult? ruleResult = null;
            if (lastSeenUid > 0 && summaries.Count > 0)
                ruleResult = RuleEngine.ProcessIncoming(account, folder.FullName, summaries);

            if (summaries.Count > 0)
                MessageStore.UpsertSummaries(account.Id, folder.FullName, summaries);

            if (ruleResult != null)
                await ExecuteRuleMovesAsync(account, client, folder, ruleResult, cancellationToken);

            MessageStore.SaveFolder(new MailFolderData
            {
                AccountId = account.Id,
                FullName = folder.FullName,
                DisplayName = folder.Name,
                Role = cached?.Role ?? (folder.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase) ? FolderRole.Inbox : FolderRole.None),
                UidValidity = folder.UidValidity,
                LastSeenUid = maxUid,
                TotalCount = folder.Count,
                UnreadCount = folder.Unread
            });

            if (newCount > 0 && lastSeenUid > 0)
            {
                OnNewMail?.Invoke(account.Id, folder.FullName, newCount);
                NotificationService.NotifyNewMessages(account, folder.FullName,
                    [.. summaries.Where(s => s.Uid > lastSeenUid)]);
            }
        }

        /// <summary>Executes the move requests a rule pass produced — local ones via the store, remote ones over the still-open connection.</summary>
        static async Task ExecuteRuleMovesAsync(MailAccountData account, ImapClient client, IMailFolder folder, RuleProcessResult ruleResult, CancellationToken cancellationToken)
        {
            foreach (var (uid, localFolder) in ruleResult.LocalMoves)
                MessageStore.MoveToLocalFolder(account.Id, folder.FullName, uid, localFolder);

            if (ruleResult.RemoteMoves.Count == 0) return;

            await folder.OpenAsync(FolderAccess.ReadWrite, cancellationToken);
            foreach (var moveGroup in ruleResult.RemoteMoves.GroupBy(m => m.TargetFolder))
            {
                try
                {
                    var targetFolder = await client.GetFolderAsync(moveGroup.Key, cancellationToken);
                    await folder.MoveToAsync([.. moveGroup.Select(m => new UniqueId(m.Uid))], targetFolder, cancellationToken);
                    MessageStore.RemoveMessages(account.Id, folder.FullName, moveGroup.Select(m => m.Uid));
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
