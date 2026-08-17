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

        public static async Task SyncAccountAsync(MailAccountData account, CancellationToken cancellationToken = default)
        {
            await ResiliencePolicy.RunNetwork(async ct =>
            {
                using var client = await MailConnections.OpenPop3Async(account, ct);

                var uids = await client.GetMessageUidsAsync(ct);
                var known = MessageStore.GetSummaries(account.Id, InboxFullName).Select(s => s.Uid).ToHashSet();

                List<MailMessageSummary> summaries = [];
                int startIndex = Math.Max(0, uids.Count - InitialFetchCount);
                for (int i = startIndex; i < uids.Count; i++)
                {
                    uint uid = Fnv1aHash(uids[i]);
                    if (known.Contains(uid)) continue;

                    var headers = await client.GetMessageHeadersAsync(i, ct);
                    var from = MailboxAddress.TryParse(headers[HeaderId.From], out var parsedFrom) ? parsedFrom : null;
                    DateTimeOffset.TryParse(headers[HeaderId.Date], out var date);

                    summaries.Add(new MailMessageSummary
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
                    });
                }

                // POP3 has no server folders: remote-move actions cannot apply; local ones can.
                RuleProcessResult? ruleResult = null;
                if (known.Count > 0 && summaries.Count > 0)
                    ruleResult = RuleEngine.ProcessIncoming(account, InboxFullName, summaries);

                if (summaries.Count > 0)
                    MessageStore.UpsertSummaries(account.Id, InboxFullName, summaries);

                if (ruleResult != null)
                    foreach (var (uid, localFolder) in ruleResult.LocalMoves)
                        MessageStore.MoveToLocalFolder(account.Id, InboxFullName, uid, localFolder);

                if (known.Count > 0 && summaries.Count > 0)
                    NotificationService.NotifyNewMessages(account, InboxFullName, summaries);

                MessageStore.SaveFolder(new MailFolderData
                {
                    AccountId = account.Id,
                    FullName = InboxFullName,
                    DisplayName = "Inbox",
                    Role = FolderRole.Inbox,
                    TotalCount = uids.Count,
                    UnreadCount = MessageStore.GetSummaries(account.Id, InboxFullName).Count(s => s.IsUnread)
                });

                await client.DisconnectAsync(true, ct);
            }, cancellationToken);
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
