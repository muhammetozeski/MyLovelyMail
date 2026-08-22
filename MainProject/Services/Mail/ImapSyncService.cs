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
    public static class ImapSyncService
    {
        /// <summary>Newest messages fetched for a folder that has never been synced (older mail loads when scrolled later).</summary>
        const int InitialFetchCount = 300;

        /// <summary>First-fill fetches land in slices this big, each painted into the UI on arrival.</summary>
        const int FirstFillSliceSize = 50;

        const MessageSummaryItems SummaryItems =
            MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags |
            MessageSummaryItems.Size | MessageSummaryItems.BodyStructure | MessageSummaryItems.PreviewText |
            MessageSummaryItems.References;

        /// <summary>Raised when an on-demand folder sync starts or finishes (drives the folder loading state).</summary>
        public static event Action? OnFolderSyncStateChanged;

        /// <summary>Folders currently being synced on demand, keyed by <see cref="MessageStore.FolderKey"/>.</summary>
        static readonly HashSet<string> onDemandInFlight = [];

        public static bool IsFolderSyncing(string accountId, string folderFullName)
        {
            lock (onDemandInFlight)
                return onDemandInFlight.Contains(MessageStore.FolderKey(accountId, folderFullName));
        }

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
                }
                catch (Exception ex)
                {
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
            }, cancellationToken);
        }

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
            }, cancellationToken);
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
                .OrderByDescending(f => f.TotalCount != MessageStore.GetSummaries(account.Id, f.FullName).Count)
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

        /// <summary>
        /// Folders an account is expected to have. Created on the server when the role resolver
        /// finds no folder holding that role — never by name, so a mailbox whose junk folder is
        /// called "Önemsiz" does not get a second one called "Spam".
        /// </summary>
        static readonly (FolderRole Role, string Name)[] RequiredFolders =
        [
            (FolderRole.Drafts, "Drafts"),
            (FolderRole.Sent, "Sent"),
            (FolderRole.Junk, "Spam"),
            (FolderRole.Trash, "Trash")
        ];

        static async Task SyncFolderListAsync(MailAccountData account, ImapClient client, CancellationToken cancellationToken)
        {
            var serverFolders = await ListEveryFolderAsync(client, cancellationToken);
            await CreateMissingRequiredFoldersAsync(account, client, serverFolders, cancellationToken);

            var cachedFolders = MessageStore.GetFolders(account.Id);
            Dictionary<string, IMailFolder> byFullName = new(StringComparer.Ordinal);
            List<FolderRoleCandidate> candidates = [];

            foreach (var folder in serverFolders)
            {
                // A folder whose STATUS fails is still a folder. One throw here used to abandon the
                // whole pass, so every folder after the bad one silently vanished from the app —
                // which is how a rule could name a folder the app swore did not exist.
                try
                {
                    await folder.StatusAsync(StatusItems.Count | StatusItems.Unread | StatusItems.UidValidity, cancellationToken);
                }
                catch (Exception ex)
                {
                    Log($"STATUS failed for '{folder.FullName}' on {account.EmailAddress}: {ex.Message}", LogLevel.Warning);
                }

                byFullName[folder.FullName] = folder;
                candidates.Add(ToCandidate(client, folder));
            }

            foreach (var decision in FolderRoleResolver.Resolve(candidates))
            {
                var folder = byFullName[decision.FullName];
                var cached = cachedFolders.FirstOrDefault(f => f.FullName == folder.FullName);
                if (cached is { Role: not FolderRole.None } && decision.Role == FolderRole.None)
                    Log($"'{folder.FullName}' no longer holds the {cached.Role} role: {decision.Why}");

                MessageStore.SaveFolder(new MailFolderData
                {
                    AccountId = account.Id,
                    FullName = folder.FullName,
                    DisplayName = folder.Name,
                    Role = decision.Role,
                    // A \NoSelect folder is a real branch of the tree that simply cannot be opened.
                    // Dropping those rows orphaned their children, so a nested mailbox lost its parent.
                    Selectable = !folder.Attributes.HasFlag(FolderAttributes.NoSelect),
                    Delimiter = folder.DirectorySeparator,
                    UidValidity = folder.UidValidity,
                    // Refreshing counts must never erase sync progress: dropping LastSeenUid to 0
                    // here re-imported "the newest 300" as brand-new on EVERY pass, which both
                    // wasted traffic and kept the new-mail notification condition permanently false.
                    // OldestFetchedUid has to survive the same way, or every backfill would be
                    // undone by the next folder-list refresh and start over from the floor.
                    LastSeenUid = cached?.LastSeenUid ?? 0,
                    OldestFetchedUid = cached?.OldestFetchedUid ?? 0,
                    LastSyncedUtc = cached?.LastSyncedUtc,
                    TotalCount = folder.Count,
                    UnreadCount = folder.Unread
                });
            }

            // A folder deleted on the server used to live on in the cache forever, still offering
            // its stale count and still selectable in the move menu.
            foreach (var stale in cachedFolders.Where(f => !f.IsLocal
                && !f.FullName.StartsWith(MessageStore.LocalFolderPrefix, StringComparison.Ordinal)
                && !byFullName.ContainsKey(f.FullName)))
            {
                MessageStore.RemoveFolder(account.Id, stale.FullName);
                Log($"'{stale.FullName}' is gone from {account.EmailAddress}; dropped from the cache.");
            }
        }

        /// <summary>
        /// Every folder the account can reach, from every namespace the server declares. Asking
        /// only <c>PersonalNamespaces[0]</c> answers for one namespace, which is wrong for servers
        /// that put personal mail under an "INBOX." prefix and for any shared or delegated mailbox.
        /// </summary>
        static async Task<List<IMailFolder>> ListEveryFolderAsync(ImapClient client, CancellationToken cancellationToken)
        {
            List<IMailFolder> found = [];
            HashSet<string> seen = new(StringComparer.Ordinal);

            foreach (var space in client.PersonalNamespaces.Concat(client.SharedNamespaces).Concat(client.OtherNamespaces))
            {
                IList<IMailFolder> folders;
                try
                {
                    folders = await client.GetFoldersAsync(space, false, cancellationToken);
                }
                catch (Exception ex)
                {
                    Log($"Could not list the '{space.Path}' namespace: {ex.Message}", LogLevel.Warning);
                    continue;
                }

                foreach (var folder in folders)
                {
                    if (folder.Attributes.HasFlag(FolderAttributes.NonExistent)) continue;
                    if (seen.Add(folder.FullName)) found.Add(folder);
                }
            }

            // The inbox is not always listed by a namespace query, and an account without it has
            // nothing to show at all.
            if (seen.Add(client.Inbox.FullName)) found.Add(client.Inbox);
            return found;
        }

        /// <summary>Creates the standard folders the account has no holder for, and appends them to the list.</summary>
        static async Task CreateMissingRequiredFoldersAsync(MailAccountData account, ImapClient client,
            List<IMailFolder> folders, CancellationToken cancellationToken)
        {
            if (client.PersonalNamespaces.Count == 0) return;

            var held = FolderRoleResolver.Resolve([.. folders.Select(f => ToCandidate(client, f))])
                .Where(static d => d.Role != FolderRole.None)
                .Select(static d => d.Role)
                .ToHashSet();

            var parent = client.GetFolder(client.PersonalNamespaces[0]);
            foreach (var (role, name) in RequiredFolders)
            {
                if (held.Contains(role)) continue;
                try
                {
                    var created = await parent.CreateAsync(name, isMessageFolder: true, cancellationToken);
                    folders.Add(created);
                    Log($"Created the missing {role} folder '{created.FullName}' on {account.EmailAddress}.");
                }
                catch (Exception ex)
                {
                    Log($"Could not create a {role} folder on {account.EmailAddress}: {ex.Message}", LogLevel.Warning);
                }
            }
        }

        /// <summary>One folder exactly as the server described it, before anything is decided about it.</summary>
        public readonly record struct ServerFolderInfo(string FullName, string DisplayName, string Attributes, string Delimiter, FolderRole SpecialUse);

        /// <summary>
        /// The server's own folder list, untouched by the cache. Comparing this against
        /// <see cref="MessageStore.GetFolders"/> is the only honest way to answer "is the app
        /// missing a folder", which is a question that came up because it was.
        /// </summary>
        public static async Task<List<ServerFolderInfo>> ListServerFoldersAsync(MailAccountData account, CancellationToken cancellationToken = default) =>
            await ResiliencePolicy.RunNetwork(async ct =>
            {
                using var client = await MailConnections.OpenImapAsync(account, ct);
                var folders = await ListEveryFolderAsync(client, ct);
                List<ServerFolderInfo> result = [.. folders.Select(f => new ServerFolderInfo(
                    f.FullName, f.Name, f.Attributes.ToString(),
                    f.DirectorySeparator == '\0' ? string.Empty : f.DirectorySeparator.ToString(),
                    SpecialUseOf(f)))];
                await client.DisconnectAsync(true, ct);
                return result;
            }, cancellationToken);

        static FolderRoleCandidate ToCandidate(ImapClient client, IMailFolder folder) =>
            new(folder.FullName, folder.Name, SpecialUseOf(folder), folder == client.Inbox, folder.Count, DepthOf(folder));

        static int DepthOf(IMailFolder folder) =>
            folder.DirectorySeparator == '\0' ? 0 : folder.FullName.Count(c => c == folder.DirectorySeparator);

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

        /// <summary>
        /// The role the SERVER itself claims through RFC 6154 SPECIAL-USE (or the older Gmail
        /// XLIST), with no name guessing at all. Naming is <see cref="FolderRoleResolver"/>'s
        /// fallback, and only it can settle two folders claiming the same role.
        /// </summary>
        static FolderRole SpecialUseOf(IMailFolder folder)
        {
            if (folder.Attributes.HasFlag(FolderAttributes.Sent)) return FolderRole.Sent;
            if (folder.Attributes.HasFlag(FolderAttributes.Drafts)) return FolderRole.Drafts;
            if (folder.Attributes.HasFlag(FolderAttributes.Trash)) return FolderRole.Trash;
            if (folder.Attributes.HasFlag(FolderAttributes.Junk)) return FolderRole.Junk;
            if (folder.Attributes.HasFlag(FolderAttributes.Archive)) return FolderRole.Archive;
            if (folder.Attributes.HasFlag(FolderAttributes.Flagged)) return FolderRole.Flagged;
            if (folder.Attributes.HasFlag(FolderAttributes.All)) return FolderRole.AllMail;
            return FolderRole.None;
        }

        /// <param name="backgroundRefresh">
        /// True for a folder the rotation picked rather than the user. Rules and notifications are
        /// skipped: a toast for mail landing in Sent or Trash is noise, and RuleEngine.ProcessIncoming
        /// is not folder-scoped, so a rotation pass would otherwise start executing move actions on
        /// Junk and Trash arrivals in the background with nobody watching. Rotation is a pure refresh.
        /// </param>
        static async Task SyncOpenedFolderAsync(MailAccountData account, ImapClient client, IMailFolder folder, CancellationToken cancellationToken, bool backgroundRefresh = false)
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
                NotificationService.NotifyNewMessages(account, folder.FullName, arrived);
                await PrefetchAttachmentBodiesAsync(account, folder.FullName, arrived, cancellationToken);
            }
        }

        /// <summary>Executes the move requests a rule pass produced — local ones via the store, remote ones over the still-open connection.</summary>
        static async Task ExecuteRuleMovesAsync(MailAccountData account, ImapClient client, IMailFolder folder, RuleProcessResult ruleResult, CancellationToken cancellationToken)
        {
            foreach (var (uid, localFolder) in ruleResult.LocalMoves)
                MessageStore.MoveToLocalFolder(account.Id, folder.FullName, uid, localFolder);

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
                            MessageStore.MoveToServerFolder(account.Id, folder.FullName, source.Id, targetFolder.FullName, destination.Id);
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
