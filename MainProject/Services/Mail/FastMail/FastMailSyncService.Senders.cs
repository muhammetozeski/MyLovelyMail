using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;

namespace MyLovelyMail.MainProject.Services.Mail.FastMail;

public static partial class FastMailSyncService
{
    /// <summary>Caches this sender's currently retained provider history and bodies before local filing.</summary>
    public static async Task<int> CacheSenderHistoryAsync(MailAccountData account, string senderAddress,
        CancellationToken cancellationToken = default)
    {
        using var operation = await FastMailAccountOperations.EnterAsync(account, cancellationToken);
        cancellationToken = operation.Token;
        using var syncScope = SyncScheduler.EnterSyncScope();
        var provider = RequireProvider(account);
        var ct = cancellationToken;
        var items = await ResiliencePolicy.RunNetwork(token => provider.ListMessagesAsync(account, token), ct, account);
        var map = LoadUidMap(account.Id);
        var known = MessageStore.GetFolders(account.Id).SelectMany(folder => MessageStore.GetSummaries(account.Id, folder.FullName)
            .Select(summary => (summary.Uid, Folder: folder.FullName))).GroupBy(item => item.Uid)
            .ToDictionary(group => group.Key, group => group.First().Folder);
        int imported = 0;
        foreach (var item in items.Where(item => item.FromAddress.Equals(senderAddress, StringComparison.OrdinalIgnoreCase)))
        {
            uint uid = StableHash.Fnv1a(item.RemoteId);
            if (map.TryGetValue(uid, out string? mapped) && mapped != item.RemoteId)
                throw new InvalidOperationException("Two provider messages have the same local identifier; sender filing stopped.");
            map[uid] = item.RemoteId;
            string sourceFolder = known.GetValueOrDefault(uid) ?? InboxFullName;
            if (!MessageStore.HasFullMessage(account.Id, sourceFolder, uid))
            {
                using var message = await ResiliencePolicy.RunNetwork(token => provider.DownloadMessageAsync(account, item.RemoteId, token), ct, account);
                using var buffer = new MemoryStream();
                await message.WriteToAsync(buffer, ct);
                MessageStore.SaveFullMessage(account.Id, sourceFolder, uid, buffer.ToArray());
            }
            if (!known.ContainsKey(uid))
            {
                MessageStore.UpsertSummaries(account.Id, sourceFolder, [new MailMessageSummary
                {
                    Uid = uid, MessageId = string.IsNullOrWhiteSpace(item.MessageIdHeader) ? item.RemoteId : item.MessageIdHeader,
                    Subject = item.Subject, FromName = item.FromName, FromAddress = item.FromAddress,
                    ToAddresses = item.ToAddresses, DateUtc = item.DateUtc, Flags = item.IsSeen ? MailFlags.Seen : MailFlags.None,
                    HasAttachments = item.HasAttachments, SizeBytes = item.SizeBytes, PreviewText = item.PreviewText
                }]);
            }
            imported++;
        }
        SaveUidMap(account.Id, map);
        RecordRoute(account, provider);
        return imported;
    }
}
