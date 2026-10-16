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

        /// <summary>
        /// Accounts whose server refused to create a folder, as accountId + role. A server that
        /// answers "Folders are not supported" will answer that every time, and a failing operation
        /// repeated once per sync forever is exactly the pattern the rule-move retry storm was.
        /// Kept for the life of the process, so restarting the app tries again.
        /// </summary>
        static readonly HashSet<string> refusedFolderCreations = [];

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
                string refusalKey = $"{account.Id}/{role}";
                lock (refusedFolderCreations)
                    if (refusedFolderCreations.Contains(refusalKey)) continue;

                try
                {
                    var created = await parent.CreateAsync(name, isMessageFolder: true, cancellationToken);
                    folders.Add(created);
                    Log($"Created the missing {role} folder '{created.FullName}' on {account.EmailAddress}.");
                }
                catch (Exception ex)
                {
                    lock (refusedFolderCreations) refusedFolderCreations.Add(refusalKey);
                    Log($"Could not create a {role} folder on {account.EmailAddress}: {ex.Message} — not asked again this run.", LogLevel.Warning);
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
            }, cancellationToken, account);

        static FolderRoleCandidate ToCandidate(ImapClient client, IMailFolder folder) =>
            new(folder.FullName, folder.Name, SpecialUseOf(folder), folder == client.Inbox, folder.Count, DepthOf(folder));

        static int DepthOf(IMailFolder folder) =>
            folder.DirectorySeparator == '\0' ? 0 : folder.FullName.Count(c => c == folder.DirectorySeparator);
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
    }
}
