using MailKit;
using MailKit.Search;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;

namespace MyLovelyMail.MainProject.Services.Mail;

public static partial class ImapSyncService
{
    /// <summary>Imports only this sender's complete accessible server history before an explicit filing operation.</summary>
    public static async Task<int> CacheSenderHistoryAsync(MailAccountData account, string senderAddress,
        bool includeBodies, string destinationFolder, CancellationToken cancellationToken = default)
    {
        using var syncScope = SyncScheduler.EnterSyncScope();
        var ct = cancellationToken;
        using var client = await ResiliencePolicy.RunNetwork(token => MailConnections.OpenImapAsync(account, token), ct, account);
        var serverFolders = await ResiliencePolicy.GuardStep(ListEveryFolderAsync(client, ct), ct, account);
        var roles = FolderRoleResolver.Resolve([.. serverFolders.Select(folder => ToCandidate(client, folder))])
            .ToDictionary(role => role.FullName, role => role.Role, StringComparer.Ordinal);
        int imported = 0;
        foreach (var folder in serverFolders.Where(folder => !folder.Attributes.HasFlag(FolderAttributes.NoSelect)
            && folder.FullName != destinationFolder))
        {
            ct.ThrowIfCancellationRequested();
            await ResiliencePolicy.GuardStep(folder.OpenAsync(FolderAccess.ReadOnly, ct), ct, account);
            var cachedFolder = MessageStore.GetFolders(account.Id).FirstOrDefault(cached => cached.FullName == folder.FullName);
            if (cachedFolder is { UidValidity: > 0 } && cachedFolder.UidValidity != folder.UidValidity)
            {
                MessageStore.ClearFolderCache(account.Id, folder.FullName);
                cachedFolder.LastSeenUid = 0;
                cachedFolder.OldestFetchedUid = 0;
            }
            var metadata = cachedFolder ?? new MailFolderData
            {
                AccountId = account.Id, FullName = folder.FullName, DisplayName = folder.Name,
                Role = roles.GetValueOrDefault(folder.FullName), Delimiter = folder.DirectorySeparator
            };
            metadata.UidValidity = folder.UidValidity;
            metadata.TotalCount = folder.Count;
            MessageStore.SaveFolder(metadata);

            // IMAP FROM searches substrings, so verify the envelope's actual first mailbox below.
            var candidates = await ResiliencePolicy.GuardStep(folder.SearchAsync(SearchQuery.FromContains(senderAddress), ct), ct, account);
            foreach (var batch in candidates.Chunk(100))
            {
                var fetched = await ResiliencePolicy.GuardStep(folder.FetchAsync(batch, SummaryItems, ct), ct, account);
                var matches = fetched.Where(item => item.UniqueId.IsValid
                    && string.Equals(item.Envelope?.From?.Mailboxes.FirstOrDefault()?.Address, senderAddress,
                        StringComparison.OrdinalIgnoreCase)).Select(ToSummary).ToList();
                foreach (var summary in matches)
                {
                    if (includeBodies && !MessageStore.HasFullMessage(account.Id, folder.FullName, summary.Uid))
                    {
                        using var message = await ResiliencePolicy.GuardStep(folder.GetMessageAsync(new UniqueId(summary.Uid), ct), ct, account);
                        using var buffer = new MemoryStream();
                        await message.WriteToAsync(buffer, ct);
                        MessageStore.SaveFullMessage(account.Id, folder.FullName, summary.Uid, buffer.ToArray());
                    }
                }
                MessageStore.UpsertSummaries(account.Id, folder.FullName, matches);
                imported += matches.Count;
            }
            await ResiliencePolicy.GuardStep(folder.CloseAsync(false, ct), ct, account);
        }
        await ResiliencePolicy.GuardStep(client.DisconnectAsync(true, ct), ct, account);
        return imported;
    }
}
