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

        /// <summary>
        /// The last on-demand failure per folder, keyed by <see cref="MessageStore.FolderKey"/>.
        /// <para>
        /// The failure of a folder opened by the user was reported nowhere but the log: the catch
        /// below only wrote a line, and <see cref="SyncHealthService"/> is marked from the
        /// scheduled pass and the IDLE loop, never from here. So a folder whose fetch had just been
        /// refused looked exactly like a folder with no mail in it, and nothing on screen said
        /// otherwise. In RAM on purpose — it describes this run, and a stale error read back from
        /// disk after a restart would be a worse lie than none.
        /// </para>
        /// </summary>
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, FolderSyncFailure> onDemandFailures = new();

        /// <summary>The last on-demand fetch failure for this folder, or null when the last one worked.</summary>
        public static FolderSyncFailure? LastFolderFailure(string accountId, string folderFullName) =>
            onDemandFailures.GetValueOrDefault(MessageStore.FolderKey(accountId, folderFullName));

        /// <summary>
        /// Fire-and-forget sync of one folder, used when the user opens it. The scheduled pass only
        /// fills the Inbox, so without this every other server folder would stay empty forever.
        /// Repeat calls while a sync is already running are ignored.
        /// </summary>
        public static void KickFolderSync(MailAccountData account, string folderFullName)
        {
            string key = MessageStore.FolderKey(account.Id, folderFullName);
            lock (onDemandInFlight)
                if (!onDemandInFlight.Add(key)) return;

            OnFolderSyncStateChanged?.Invoke();
            _ = Task.Run(async () =>
            {
                try
                {
                    await SyncFolderAsync(account, folderFullName);
                    onDemandFailures.TryRemove(key, out _);
                }
                catch (Exception ex)
                {
                    onDemandFailures[key] = new FolderSyncFailure(ex.Message, DateTime.UtcNow);
                    Log($"On-demand sync of '{folderFullName}' failed for {account.EmailAddress}: {ex.Message}", LogLevel.Error);
                }
                finally
                {
                    lock (onDemandInFlight)
                        onDemandInFlight.Remove(key);
                    OnFolderSyncStateChanged?.Invoke();
                }
            });
        }

        /// <summary>
        /// Throws the folder's cache away and refills it. Incremental sync only ever asks for uids
        /// ABOVE LastSeenUid, so a folder whose cache was truncated can never heal itself — this is
        /// the way back. The refill lands in the usual slices, so the list repaints while it runs.
        /// </summary>
        public static void KickFolderResync(MailAccountData account, string folderFullName)
        {
            MessageStore.ClearFolderCache(account.Id, folderFullName);
            KickFolderSync(account, folderFullName);
        }

        /// <summary>
        /// Fetches the next <paramref name="count"/> messages BELOW the folder's oldest fetched
        /// uid — the only way to reach mail past the first fill, since incremental sync asks for
        /// uids above LastSeenUid and a resync just re-fetches the same newest slice.
        /// Never touches LastSeenUid, so the new-mail path and its notification stay untouched.
        /// </summary>
        public static void KickFolderBackfill(MailAccountData account, string folderFullName, int count)
        {
            string key = MessageStore.FolderKey(account.Id, folderFullName);
            lock (onDemandInFlight)
                if (!onDemandInFlight.Add(key)) return;

            OnFolderSyncStateChanged?.Invoke();
            _ = Task.Run(async () =>
            {
                try
                {
                    int landed = await BackfillFolderAsync(account, folderFullName, count);
                    Log($"Backfill of '{folderFullName}': {landed} older summaries added for {account.EmailAddress}.");
                }
                catch (Exception ex)
                {
                    Log($"Backfill of '{folderFullName}' failed for {account.EmailAddress}: {ex.Message}", LogLevel.Error);
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
        public static async Task<int> BackfillFolderAsync(MailAccountData account, string folderFullName, int count,
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
