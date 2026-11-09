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

            var cachedFolders = MessageStore.GetFolders(account.Id).ToDictionary(static folder => folder.FullName, StringComparer.Ordinal);
            Dictionary<string, IMailFolder> byFullName = new(StringComparer.Ordinal);
            List<FolderRoleCandidate> candidates = [];

            foreach (var folder in serverFolders)
            {
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
                cachedFolders.TryGetValue(folder.FullName, out var cached);
                if (cached is { Role: not FolderRole.None } && decision.Role == FolderRole.None)
                    Log($"'{folder.FullName}' no longer holds the {cached.Role} role: {decision.Why}");

                bool selectable = !folder.Attributes.HasFlag(FolderAttributes.NoSelect);
                if (cached != null
                    && cached.DisplayName == folder.Name
                    && cached.Role == decision.Role
                    && cached.Selectable == selectable
                    && cached.Delimiter == folder.DirectorySeparator
                    && cached.UidValidity == folder.UidValidity
                    && cached.TotalCount == folder.Count
                    && cached.UnreadCount == folder.Unread)
                {
                    continue;
                }

                MessageStore.SaveFolder(new MailFolderData
                {
                    AccountId = account.Id,
                    FullName = folder.FullName,
                    DisplayName = folder.Name,
                    Role = decision.Role,
                    Selectable = selectable,
                    Delimiter = folder.DirectorySeparator,
                    UidValidity = folder.UidValidity,
                    LastSeenUid = cached?.LastSeenUid ?? 0,
                    OldestFetchedUid = cached?.OldestFetchedUid ?? 0,
                    LastSyncedUtc = cached?.LastSyncedUtc,
                    TotalCount = folder.Count,
                    UnreadCount = folder.Unread,
                    SortOrder = cached?.SortOrder ?? 0,
                    IsExpanded = cached?.IsExpanded ?? true
                });
            }

            foreach (var stale in cachedFolders.Values.Where(f => !f.IsLocal
                && !f.FullName.StartsWith(MessageStore.LocalFolderPrefix, StringComparison.Ordinal)
                && !byFullName.ContainsKey(f.FullName)))
            {
                MessageStore.RemoveFolder(account.Id, stale.FullName);
                Log($"'{stale.FullName}' is gone from {account.EmailAddress}; dropped from the cache.");
            }
        }

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

            if (seen.Add(client.Inbox.FullName)) found.Add(client.Inbox);
            return found;
        }

        static readonly HashSet<string> refusedFolderCreations = [];

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
                    if (created == null)
                        throw new IOException($"The server did not return the created {role} folder.");
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

        public readonly record struct ServerFolderInfo(string FullName, string DisplayName, string Attributes, string Delimiter, FolderRole SpecialUse);

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
