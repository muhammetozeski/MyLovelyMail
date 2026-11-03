using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;

namespace MyLovelyMail.MainProject.Services.Mail;

public static partial class Pop3Service
{
    /// <summary>Scans provider-retained headers without changing the account fetch limit; downloads only exact sender matches.</summary>
    public static async Task<int> CacheSenderHistoryAsync(MailAccountData account, string senderAddress,
        CancellationToken cancellationToken = default)
    {
        using var syncScope = SyncScheduler.EnterSyncScope();
        var ct = cancellationToken;
        using var client = await ResiliencePolicy.RunNetwork(token => MailConnections.OpenPop3Async(account, token), ct, account);
        var uids = await ResiliencePolicy.GuardStep(client.GetMessageUidsAsync(ct), ct, account);
        var known = MessageStore.GetFolders(account.Id).SelectMany(folder => MessageStore.GetSummaries(account.Id, folder.FullName)
            .Select(summary => (summary.Uid, Folder: folder.FullName))).GroupBy(item => item.Uid)
            .ToDictionary(group => group.Key, group => group.First().Folder);
        int imported = 0;
        for (int index = 0; index < uids.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var uid = Fnv1aHash(uids[index]);
            if (known.TryGetValue(uid, out string? existingFolder)
                && MessageStore.HasFullMessage(account.Id, existingFolder, uid)) continue;
            var headers = await ResiliencePolicy.GuardStep(client.GetMessageHeadersAsync(index, ct), ct, account);
            var summary = BuildSummary(headers, uids[index]);
            if (!summary.FromAddress.Equals(senderAddress, StringComparison.OrdinalIgnoreCase)) continue;
            string sourceFolder = existingFolder ?? InboxFullName;
            using var message = await ResiliencePolicy.GuardStep(client.GetMessageAsync(index, ct), ct, account);
            using var buffer = new MemoryStream();
            await message.WriteToAsync(buffer, ct);
            MessageStore.SaveFullMessage(account.Id, sourceFolder, uid, buffer.ToArray());
            if (existingFolder == null)
            {
                summary.HasAttachments = message.Attachments.Any();
                summary.SizeBytes = buffer.Length;
                MessageStore.UpsertSummaries(account.Id, sourceFolder, [summary]);
            }
            imported++;
        }
        await ResiliencePolicy.GuardStep(client.DisconnectAsync(true, ct), ct, account);
        return imported;
    }
}
