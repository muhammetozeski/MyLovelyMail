using MimeKit;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>
    /// POP3 synchronization. POP3 has a single mailbox, mirrored as one Inbox folder; server
    /// UIDs are strings, so the summary's numeric Uid is a stable FNV-1a hash of that string
    /// (the original string is kept in the summary's MessageId when no Message-Id header exists).
    /// </summary>
    public static class Pop3Service
    {
        public const string InboxFullName = "INBOX";

        /// <summary>Newest messages fetched on the first sync of an account.</summary>
        const int InitialFetchCount = 300;

        /// <summary>Summaries are persisted (and hit the UI) every this many fetched messages, so a long first sync fills the list live and survives a mid-way drop.</summary>
        const int PersistBatchSize = 25;

        /// <summary>
        /// Full-mailbox sync. POP3 has no batched header command, so this issues one TOP
        /// round-trip per NEW message (up to <see cref="InitialFetchCount"/>). Instead of one
        /// giant timeout around the whole session, the connect is retried via the normal
        /// pipeline, each round-trip is individually guarded, and progress is persisted in
        /// batches — if the session drops half-way, everything fetched so far is kept and the
        /// next sync continues from there (already-known uids are skipped).
        /// </summary>
        public static async Task SyncAccountAsync(MailAccountData account, CancellationToken cancellationToken = default)
        {
            Log($"POP3 sync started: {account.EmailAddress}");
            using var client = await ResiliencePolicy.RunNetwork(
                ct => MailConnections.OpenPop3Async(account, ct), cancellationToken);

            var uids = await ResiliencePolicy.GuardStep(client.GetMessageUidsAsync(cancellationToken), cancellationToken);
            var known = MessageStore.GetSummaries(account.Id, InboxFullName).Select(s => s.Uid).ToHashSet();
            bool isFirstSync = known.Count == 0;

            List<MailMessageSummary> allNew = [];
            List<MailMessageSummary> pendingBatch = [];
            int startIndex = Math.Max(0, uids.Count - InitialFetchCount);
            for (int i = startIndex; i < uids.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                uint uid = Fnv1aHash(uids[i]);
                if (known.Contains(uid)) continue;

                var headers = await ResiliencePolicy.GuardStep(client.GetMessageHeadersAsync(i, cancellationToken), cancellationToken);
                var from = MailboxAddress.TryParse(headers[HeaderId.From], out var parsedFrom) ? parsedFrom : null;
                DateTimeOffset.TryParse(headers[HeaderId.Date], out var date);

                var summary = new MailMessageSummary
                {
                    Uid = uid,
                    MessageId = headers[HeaderId.MessageId] ?? uids[i],
                    Subject = headers[HeaderId.Subject] ?? string.Empty,
                    FromName = from?.Name ?? string.Empty,
                    FromAddress = from?.Address ?? headers[HeaderId.From] ?? string.Empty,
                    ToAddresses = headers[HeaderId.To] ?? string.Empty,
                    DateUtc = date == default ? DateTime.UtcNow : date.UtcDateTime,
                    Flags = MailFlags.None,
                    SizeBytes = 0
                };
                allNew.Add(summary);
                pendingBatch.Add(summary);

                if (pendingBatch.Count >= PersistBatchSize)
                {
                    MessageStore.UpsertSummaries(account.Id, InboxFullName, pendingBatch);
                    pendingBatch = [];
                }
            }

            // POP3 has no server folders: remote-move actions cannot apply; local ones can.
            RuleProcessResult? ruleResult = null;
            if (!isFirstSync && allNew.Count > 0)
                ruleResult = RuleEngine.ProcessIncoming(account, InboxFullName, allNew);

            // Rules mutate flags/tags in place, so when they ran, re-persist EVERYTHING fetched
            // this session (mid-loop batches included); otherwise only the unpersisted tail.
            if (ruleResult != null)
                MessageStore.UpsertSummaries(account.Id, InboxFullName, allNew);
            else if (pendingBatch.Count > 0)
                MessageStore.UpsertSummaries(account.Id, InboxFullName, pendingBatch);

            if (ruleResult != null)
                foreach (var (uid, localFolder) in ruleResult.LocalMoves)
                    MessageStore.MoveToLocalFolder(account.Id, InboxFullName, uid, localFolder);

            if (!isFirstSync && allNew.Count > 0)
                NotificationService.NotifyNewMessages(account, InboxFullName, allNew);

            MessageStore.SaveFolder(new MailFolderData
            {
                AccountId = account.Id,
                FullName = InboxFullName,
                DisplayName = "Inbox",
                Role = FolderRole.Inbox,
                TotalCount = uids.Count,
                UnreadCount = MessageStore.GetSummaries(account.Id, InboxFullName).Count(s => s.IsUnread)
            });

            await ResiliencePolicy.GuardStep(client.DisconnectAsync(true, cancellationToken), cancellationToken);
            Log($"POP3 sync finished: {account.EmailAddress}, {allNew.Count} new of {uids.Count} on server");
        }

        /// <summary>Downloads one full message (found by its hashed uid) into the cache and returns it parsed.</summary>
        public static async Task<MimeMessage> DownloadMessageAsync(MailAccountData account, uint uid, CancellationToken cancellationToken = default)
        {
            return await ResiliencePolicy.RunNetwork(async ct =>
            {
                using var client = await MailConnections.OpenPop3Async(account, ct);
                var uids = await client.GetMessageUidsAsync(ct);

                for (int i = 0; i < uids.Count; i++)
                {
                    if (Fnv1aHash(uids[i]) != uid) continue;

                    var message = await client.GetMessageAsync(i, ct);
                    using var buffer = new MemoryStream();
                    await message.WriteToAsync(buffer, ct);
                    MessageStore.SaveFullMessage(account.Id, InboxFullName, uid, buffer.ToArray());
                    await client.DisconnectAsync(true, ct);
                    return message;
                }

                throw new InvalidOperationException("The message no longer exists on the POP3 server.");
            }, cancellationToken);
        }

        /// <summary>Stable 32-bit FNV-1a hash mapping a POP3 string UID onto the numeric summary Uid.</summary>
        internal static uint Fnv1aHash(string text)
        {
            uint hash = 2166136261;
            foreach (char c in text)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return hash == 0 ? 1u : hash;
        }
    }
}
