using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services.Mail
{
    public static partial class ImapSyncService
    {
        /// <summary>Raised when an on-demand folder sync starts or finishes (drives the folder loading state).</summary>
        public static event Action? OnFolderSyncStateChanged;

        /// <summary>Folders currently being synced on demand, keyed by <see cref="MessageStore.FolderKey"/>.</summary>
        static readonly HashSet<string> onDemandInFlight = [];

        public static bool IsFolderSyncing(string accountId, string folderFullName)
        {
            lock (onDemandInFlight)
                return onDemandInFlight.Contains(MessageStore.FolderKey(accountId, folderFullName));
        }

        /// <summary>What went wrong the last time a folder was fetched on demand, and when.</summary>
        public sealed record FolderSyncFailure(string Message, DateTime WhenUtc);

        /// <summary>Last foreground folder failure, kept for this process so the empty-folder UI can distinguish a failed fetch from an empty mailbox.</summary>
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, FolderSyncFailure> onDemandFailures = new();

        /// <summary>The last on-demand fetch failure for this folder, or null when the last one worked.</summary>
        public static FolderSyncFailure? LastFolderFailure(string accountId, string folderFullName) =>
            onDemandFailures.GetValueOrDefault(MessageStore.FolderKey(accountId, folderFullName));

        /// <summary>Starts one folder operation, ignoring duplicates while that folder is already busy.</summary>
        /// <param name="account">The account whose server and credentials are used.</param>
        /// <param name="folderFullName">The server folder to synchronize.</param>
        /// <param name="resetCache">Discard the cached folder before a full refill.</param>
        /// <param name="backfillCount">Fetch this many older messages instead of checking for new mail.</param>
        /// <param name="cancellationToken">Cancels the operation without recording a server failure.</param>
        public static void KickFolderSync(MailAccountData account, string folderFullName,
            bool resetCache = false, int? backfillCount = null, CancellationToken cancellationToken = default)
        {
            if (backfillCount is <= 0)
                throw new ArgumentOutOfRangeException(nameof(backfillCount));
            if (resetCache && backfillCount.HasValue)
                throw new ArgumentException("A cache refill cannot also be an older-message fetch.");

            string key = MessageStore.FolderKey(account.Id, folderFullName);
            lock (onDemandInFlight)
                if (!onDemandInFlight.Add(key)) return;

            OnFolderSyncStateChanged?.Invoke();
            _ = Task.Run(async () =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (backfillCount is { } count)
                    {
                        int landed = await BackfillFolderAsync(account, folderFullName, count, cancellationToken);
                        Log($"Backfill of '{folderFullName}': {landed} older summaries added for {account.EmailAddress}.");
                    }
                    else
                    {
                        if (resetCache) MessageStore.ClearFolderCache(account.Id, folderFullName);
                        await SyncFolderAsync(account, folderFullName, cancellationToken);
                        onDemandFailures.TryRemove(key, out _);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Log($"Folder sync canceled for '{folderFullName}' on {account.EmailAddress}.");
                }
                catch (Exception ex)
                {
                    if (!backfillCount.HasValue)
                        onDemandFailures[key] = new FolderSyncFailure(ex.Message, DateTime.UtcNow);
                    string operation = backfillCount.HasValue ? "Backfill" : "On-demand sync";
                    Log($"{operation} of '{folderFullName}' failed for {account.EmailAddress}: {ex.Message}", LogLevel.Error);
                }
                finally
                {
                    lock (onDemandInFlight)
                        onDemandInFlight.Remove(key);
                    OnFolderSyncStateChanged?.Invoke();
                }
            });
        }

        /// <summary>The backfill itself; returns how many summaries landed. Awaited by the debug API.</summary>
        public static async Task<int> BackfillFolderAsync(MailAccountData account, string folderFullName, int count = InitialFetchCount,
            CancellationToken cancellationToken = default)
        {
            var cached = MessageStore.GetFolders(account.Id).FirstOrDefault(f => f.FullName == folderFullName);
            // Nothing fetched yet means there is no floor to dig below; a normal sync goes first.
            if (cached is not { OldestFetchedUid: > 1 }) return 0;

            using var syncScope = SyncScheduler.EnterSyncScope();
            return await ResiliencePolicy.RunNetwork(async ct =>
            {
                using var client = await MailConnections.OpenImapAsync(account, ct);
                var folder = await client.GetFolderAsync(folderFullName, ct);
                await folder.OpenAsync(FolderAccess.ReadOnly, ct);

                var below = new UniqueIdRange(UniqueId.MinValue, new UniqueId(cached.OldestFetchedUid - 1));
                var olderUids = await folder.SearchAsync(SearchQuery.Uids(below), ct);
                // Newest of the older ones first: the user is walking backwards through the folder.
                var wanted = olderUids.OrderByDescending(static u => u.Id).Take(count).ToList();
                if (wanted.Count == 0)
                {
                    await client.DisconnectAsync(true, ct);
                    return 0;
                }

                var fetched = await folder.FetchAsync(wanted, SummaryItems, ct);
                List<MailMessageSummary> older = [.. fetched.Where(static i => i.UniqueId.IsValid).Select(ToSummary)];
                if (older.Count > 0)
                {
                    MessageStore.UpsertSummaries(account.Id, folderFullName, older);
                    cached.OldestFetchedUid = older.Min(static s => s.Uid);
                    MessageStore.SaveFolder(cached);
                }

                await client.DisconnectAsync(true, ct);
                return older.Count;
            }, cancellationToken, account);
        }
    }
}
