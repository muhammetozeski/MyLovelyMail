using MailKit.Net.Pop3;
using MimeKit;
using MimeKit.Utils;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>
    /// POP3 synchronization. POP3 has a single mailbox, mirrored as one Inbox folder; server
    /// UIDs are strings, so the summary's numeric Uid is a stable FNV-1a hash of that string
    /// (the original string is kept in the summary's MessageId when no Message-Id header exists).
    /// </summary>
    public static class Pop3Service
    {
        const string InboxFullName = MessageStore.InboxFullName;

        /// <summary>Extra POP3 sessions opened for parallel header fetching. Servers that lock the
        /// maildrop to one session simply refuse the extras and the sync continues on fewer.</summary>
        const int ParallelConnections = 3;

        /// <summary>
        /// Full-mailbox sync. RFC 1939 does not promise any message ordering and real servers
        /// disagree (Yandex lists newest FIRST), so the order is probed from the Date headers of
        /// both ends and never assumed. New mail is detected against the WHOLE uid list — not a
        /// fixed window — so a message arriving at either end of the list is always found.
        /// Candidates are fetched newest-first over parallel connections into a
        /// <see cref="SummaryPump"/>, which paints each arrival burst into the UI immediately.
        /// <see cref="AccountSettings.Pop3FetchLimit"/> caps one sync (0 = fetch everything).
        /// </summary>
        public static async Task SyncAccountAsync(MailAccountData account, CancellationToken cancellationToken = default)
        {
            using var syncScope = SyncScheduler.EnterSyncScope();
            Log($"POP3 sync started: {account.EmailAddress}");
            using var client = await ResiliencePolicy.RunNetwork(
                ct => MailConnections.OpenPop3Async(account, ct), cancellationToken, account);

            var uids = await ResiliencePolicy.GuardStep(client.GetMessageUidsAsync(cancellationToken), cancellationToken, account);
            var known = MessageStore.GetSummaries(account.Id, InboxFullName).Select(static s => s.Uid).ToHashSet();
            bool isFirstSync = known.Count == 0;

            bool newestAtEnd = await ProbeNewestAtEndAsync(account, client, uids.Count, cancellationToken);

            // Newest-first candidate indexes over the WHOLE list, cut to the user's fetch limit.
            int fetchLimit = AccountStore.GetSettings(account.Id).Pop3FetchLimit.Value;
            IEnumerable<int> indexesNewestFirst = newestAtEnd
                ? Enumerable.Range(0, uids.Count).Reverse()
                : Enumerable.Range(0, uids.Count);
            List<int> candidates = [.. indexesNewestFirst.Where(i => !known.Contains(Fnv1aHash(uids[i])))];
            if (fetchLimit > 0 && candidates.Count > fetchLimit)
                candidates = candidates[..fetchLimit];

            var pump = new SummaryPump(account.Id, InboxFullName);
            var drainTask = pump.DrainToStoreAsync(cancellationToken);
            await FetchInParallelAsync(account, client, uids, candidates, pump, cancellationToken);
            pump.Complete();
            var allNew = await drainTask;

            // POP3 has no server folders: remote-move actions cannot apply; local ones can.
            RuleProcessResult? ruleResult = null;
            if (!isFirstSync && allNew.Count > 0)
            {
                MuteService.ApplyToIncoming(allNew);
                ruleResult = RuleEngine.ProcessIncoming(account, InboxFullName, allNew);
                // Rules mutate flags/tags in place, so re-persist everything they saw.
                MessageStore.UpsertSummaries(account.Id, InboxFullName, allNew);
                foreach (var (uid, localFolder) in ruleResult.LocalMoves)
                    MessageStore.MoveToLocalFolder(account.Id, InboxFullName, uid, localFolder);
                NotificationService.NotifyNewMessages(account, InboxFullName, allNew);
            }

            MessageStore.SaveFolder(new MailFolderData
            {
                AccountId = account.Id,
                FullName = InboxFullName,
                DisplayName = "Inbox",
                Role = FolderRole.Inbox,
                TotalCount = uids.Count,
                UnreadCount = MessageStore.GetSummaries(account.Id, InboxFullName).Count(static s => s.IsUnread)
            });

            await ResiliencePolicy.GuardStep(client.DisconnectAsync(true, cancellationToken), cancellationToken, account);
            Log($"POP3 sync finished: {account.EmailAddress}, {allNew.Count} new of {uids.Count} on server (newestAtEnd={newestAtEnd})");
        }

        /// <summary>
        /// Compares the Date headers of the first and last message to learn which end of the
        /// server's list is newest. Two TOP round-trips; lists shorter than 2 default to true
        /// (the RFC-conventional append order).
        /// </summary>
        static async Task<bool> ProbeNewestAtEndAsync(MailAccountData account, Pop3Client client, int messageCount, CancellationToken cancellationToken)
        {
            if (messageCount < 2) return true;
            var firstHeaders = await ResiliencePolicy.GuardStep(client.GetMessageHeadersAsync(0, cancellationToken), cancellationToken, account);
            var lastHeaders = await ResiliencePolicy.GuardStep(client.GetMessageHeadersAsync(messageCount - 1, cancellationToken), cancellationToken, account);
            DateTimeOffset.TryParse(firstHeaders[HeaderId.Date], out var firstDate);
            DateTimeOffset.TryParse(lastHeaders[HeaderId.Date], out var lastDate);
            return lastDate >= firstDate;
        }

        /// <summary>
        /// Splits the candidate indexes round-robin over the main connection plus up to
        /// <see cref="ParallelConnections"/>-1 extra sessions. An extra session the server refuses
        /// (single-session maildrop lock) hands its share back to the main connection.
        /// </summary>
        static async Task FetchInParallelAsync(MailAccountData account, Pop3Client mainClient, IList<string> uids,
            List<int> candidates, SummaryPump pump, CancellationToken cancellationToken)
        {
            if (candidates.Count == 0) return;

            List<Pop3Client> extraClients = [];
            try
            {
                // One extra connection is pointless for a handful of messages.
                int wantedExtras = Math.Min(ParallelConnections - 1, candidates.Count / 20);
                for (int i = 0; i < wantedExtras; i++)
                {
                    try
                    {
                        extraClients.Add(await MailConnections.OpenPop3Async(account, cancellationToken));
                    }
                    catch (Exception ex)
                    {
                        Log($"POP3 extra connection refused (continuing with {extraClients.Count + 1}): {ex.Message}", LogLevel.Warning);
                        break;
                    }
                }

                List<Pop3Client> workers = [mainClient, .. extraClients];
                var shares = candidates.Select((index, position) => (index, position))
                    .GroupBy(pair => pair.position % workers.Count)
                    .Select(group => group.Select(pair => pair.index).ToList())
                    .ToList();

                await Task.WhenAll(shares.Select((share, workerIndex) =>
                    FetchShareAsync(account, workers[workerIndex], uids, share, pump, cancellationToken)));
            }
            finally
            {
                foreach (var extra in extraClients)
                {
                    try { await extra.DisconnectAsync(true, cancellationToken); } catch { }
                    extra.Dispose();
                }
            }
        }

        /// <summary>One worker: sequential TOP fetches on its own connection, each summary dropped into the pump on arrival.</summary>
        static async Task FetchShareAsync(MailAccountData account, Pop3Client client, IList<string> uids, List<int> share,
            SummaryPump pump, CancellationToken cancellationToken)
        {
            foreach (int index in share)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var headers = await ResiliencePolicy.GuardStep(client.GetMessageHeadersAsync(index, cancellationToken), cancellationToken, account);
                pump.Add(BuildSummary(headers, uids[index]));
            }
        }

        /// <summary>Maps TOP headers onto the RAM summary. Ids are normalized (bracket-free) so POP-side ids match IMAP envelope ids in threading.</summary>
        static MailMessageSummary BuildSummary(HeaderList headers, string uidText)
        {
            var from = MailboxAddress.TryParse(headers[HeaderId.From], out var parsedFrom) ? parsedFrom : null;
            DateTimeOffset.TryParse(headers[HeaderId.Date], out var date);
            return new MailMessageSummary
            {
                Uid = Fnv1aHash(uidText),
                MessageId = MimeUtils.EnumerateReferences(headers[HeaderId.MessageId] ?? string.Empty).FirstOrDefault() ?? uidText,
                InReplyTo = MimeUtils.EnumerateReferences(headers[HeaderId.InReplyTo] ?? string.Empty).FirstOrDefault() ?? string.Empty,
                ReferenceIds = [.. MimeUtils.EnumerateReferences(headers[HeaderId.References] ?? string.Empty)],
                Subject = headers[HeaderId.Subject] ?? string.Empty,
                FromName = from?.Name ?? string.Empty,
                FromAddress = from?.Address ?? headers[HeaderId.From] ?? string.Empty,
                ToAddresses = headers[HeaderId.To] ?? string.Empty,
                DateUtc = date == default ? DateTime.UtcNow : date.UtcDateTime,
                Flags = MailFlags.None,
                SizeBytes = 0
            };
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
            }, cancellationToken, account);
        }

        /// <summary>Maps a POP3 string UID onto the numeric summary Uid. Kept as a named step because
        /// "the POP3 uid is a hash of the server's string uid" is the fact worth reading here.</summary>
        internal static uint Fnv1aHash(string text) => StableHash.Fnv1a(text);
    }
}
