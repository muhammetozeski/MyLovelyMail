using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using MimeKit;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services.Tor;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services.Mail.FastMail;

/// <summary>
/// Synchronizes, downloads, marks read, and deletes messages for <see cref="IncomingProtocol.FastMail"/>
/// accounts through their <see cref="IFastMailProvider"/> strictly over Tor.
/// </summary>
public static class FastMailSyncService
{
    const bool IsLogEnabled = true;
    const string InboxFullName = MessageStore.InboxFullName;
    const string UidMapFileName = "fastmail-uids.json";

    static readonly ConcurrentDictionary<string, ConcurrentDictionary<uint, string>> uidMapsByAccount = new();

    static IFastMailProvider RequireProvider(MailAccountData account)
    {
        if (!account.TorOnly)
            account.TorOnly = true;

        var provider = FastMailProviderRegistry.GetById(account.FastMailProviderId);
        return provider ?? throw new InvalidOperationException(
            $"Fast Mail provider '{account.FastMailProviderId}' is not registered for account '{account.EmailAddress}'.");
    }

    public static async Task SyncAccountAsync(MailAccountData account, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        using var syncScope = SyncScheduler.EnterSyncScope();
        var provider = RequireProvider(account);
        Log($"FastMail sync started: {account.EmailAddress} ({provider.DisplayName})", Run: IsLogEnabled);

        try
        {
            var remoteItems = await ResiliencePolicy.RunNetwork(
                ct => provider.ListMessagesAsync(account, ct),
                cancellationToken,
                account);

            RecordRoute(account, provider);

            var folders = MessageStore.GetFolders(account.Id);
            var known = folders
                .Where(static folder => folder.Role is not (FolderRole.Drafts or FolderRole.Sent or FolderRole.Outbox))
                .SelectMany(folder => MessageStore.GetSummaries(account.Id, folder.FullName))
                .Select(static summary => summary.Uid)
                .ToHashSet();

            bool isFirstSync = known.Count == 0 &&
                folders.FirstOrDefault(static folder => folder.FullName == InboxFullName)?.LastSyncedUtc == null;

            var uidMap = LoadUidMap(account.Id);
            bool mapChanged = false;
            var allNew = new List<MailMessageSummary>();

            foreach (var item in remoteItems)
            {
                uint uid = StableHash.Fnv1a(item.RemoteId);
                if (!uidMap.TryGetValue(uid, out string? existingRemote) || existingRemote != item.RemoteId)
                {
                    uidMap[uid] = item.RemoteId;
                    mapChanged = true;
                }

                if (known.Contains(uid)) continue;

                var flags = MailFlags.None;
                if (item.IsSeen) flags |= MailFlags.Seen;

                allNew.Add(new MailMessageSummary
                {
                    Uid = uid,
                    MessageId = string.IsNullOrWhiteSpace(item.MessageIdHeader) ? item.RemoteId : item.MessageIdHeader,
                    Subject = item.Subject,
                    FromName = item.FromName,
                    FromAddress = item.FromAddress,
                    ToAddresses = item.ToAddresses,
                    DateUtc = item.DateUtc,
                    Flags = flags,
                    HasAttachments = item.HasAttachments,
                    SizeBytes = item.SizeBytes,
                    PreviewText = item.PreviewText
                });
            }

            if (mapChanged)
                SaveUidMap(account.Id, uidMap);

            if (allNew.Count > 0)
            {
                MessageStore.UpsertSummaries(account.Id, InboxFullName, allNew);

                if (!isFirstSync)
                {
                    MuteService.ApplyToIncoming(allNew);
                    var ruleResult = RuleEngine.ProcessIncoming(account, InboxFullName, allNew);
                    MessageStore.UpsertSummaries(account.Id, InboxFullName, allNew);
                    var notificationCandidates = NotificationService.ExcludeMutedRuleDestinations(account, allNew, ruleResult);
                    foreach (var (uid, localFolder) in ruleResult.LocalMoves)
                        MessageStore.MoveCachedMessage(account.Id, InboxFullName, uid, MessageStore.LocalFolderPrefix + localFolder);
                    NotificationService.NotifyNewMessages(account, InboxFullName, notificationCandidates);
                }
            }

            int totalInInbox = MessageStore.GetSummaries(account.Id, InboxFullName).Count;
            MessageStore.SaveFolder(new MailFolderData
            {
                AccountId = account.Id,
                FullName = InboxFullName,
                DisplayName = "Inbox",
                Role = FolderRole.Inbox,
                LastSyncedUtc = DateTime.UtcNow,
                TotalCount = Math.Max(totalInInbox, remoteItems.Count),
                UnreadCount = MessageStore.CountSummaries(account.Id, InboxFullName, static s => s.IsUnread)
            });

            Log($"FastMail sync finished: {account.EmailAddress}, {allNew.Count} new of {remoteItems.Count} on server, elapsed={sw.ElapsedMilliseconds}ms.", Run: IsLogEnabled);
        }
        catch (Exception ex)
        {
            Log($"FastMail sync failed for '{account.EmailAddress}' after {sw.ElapsedMilliseconds}ms:\n{ex}", LogLevel.Error, Run: IsLogEnabled);
            throw;
        }
    }

    public static async Task<MimeMessage> DownloadMessageAsync(MailAccountData account, uint uid, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var provider = RequireProvider(account);
        Log($"FastMail.DownloadMessageAsync started: account='{account.EmailAddress}', uid={uid}", Run: IsLogEnabled);

        try
        {
            return await ResiliencePolicy.RunNetwork(async ct =>
            {
                string remoteId = await ResolveRemoteIdAsync(account, provider, uid, ct);
                var message = await provider.DownloadMessageAsync(account, remoteId, ct);
                RecordRoute(account, provider);

                using var buffer = new MemoryStream();
                await message.WriteToAsync(buffer, ct);
                MessageStore.SaveFullMessage(account.Id, InboxFullName, uid, buffer.ToArray());
                Log($"FastMail.DownloadMessageAsync finished: account='{account.EmailAddress}', uid={uid}, remoteId='{remoteId}', bytes={buffer.Length}, elapsed={sw.ElapsedMilliseconds}ms", Run: IsLogEnabled);
                return message;
            }, cancellationToken, account);
        }
        catch (Exception ex)
        {
            Log($"FastMail.DownloadMessageAsync failed for '{account.EmailAddress}', uid={uid} after {sw.ElapsedMilliseconds}ms:\n{ex}", LogLevel.Error, Run: IsLogEnabled);
            throw;
        }
    }

    public static async Task SetSeenAsync(MailAccountData account, IReadOnlyList<uint> uids, bool seen, CancellationToken cancellationToken = default)
    {
        if (uids.Count == 0) return;
        var provider = RequireProvider(account);
        var remoteIds = ResolveKnownRemoteIds(account.Id, uids);
        if (remoteIds.Count == 0) return;

        await ResiliencePolicy.RunNetwork(
            ct => provider.MarkReadAsync(account, remoteIds, seen, ct),
            cancellationToken,
            account);
    }

    public static async Task DeleteMessagesAsync(MailAccountData account, IReadOnlyList<uint> uids, CancellationToken cancellationToken = default)
    {
        if (uids.Count == 0) return;
        var provider = RequireProvider(account);
        var remoteIds = ResolveKnownRemoteIds(account.Id, uids);
        if (remoteIds.Count == 0) return;

        await ResiliencePolicy.RunNetwork(
            ct => provider.DeleteMessagesAsync(account, remoteIds, ct),
            cancellationToken,
            account);
    }

    static void RecordRoute(MailAccountData account, IFastMailProvider provider)
    {
        var endpoint = TorService.Current;
        SyncHealthService.MarkRoute(
            account.Id,
            $"{endpoint?.ToString() ?? "Tor SOCKS5"} via {provider.DisplayName} HTTPS (isolated connections)");
    }

    static async Task<string> ResolveRemoteIdAsync(MailAccountData account, IFastMailProvider provider, uint uid, CancellationToken cancellationToken)
    {
        var map = LoadUidMap(account.Id);
        if (map.TryGetValue(uid, out string? remoteId) && !string.IsNullOrWhiteSpace(remoteId))
            return remoteId;

        var items = await provider.ListMessagesAsync(account, cancellationToken);
        foreach (var item in items)
        {
            uint candidateUid = StableHash.Fnv1a(item.RemoteId);
            map[candidateUid] = item.RemoteId;
        }
        SaveUidMap(account.Id, map);

        if (map.TryGetValue(uid, out string? found) && !string.IsNullOrWhiteSpace(found))
            return found;

        throw new InvalidOperationException("The message no longer exists on the Fast Mail server.");
    }

    static List<string> ResolveKnownRemoteIds(string accountId, IReadOnlyList<uint> uids)
    {
        var map = LoadUidMap(accountId);
        var list = new List<string>(uids.Count);
        foreach (uint uid in uids)
        {
            if (map.TryGetValue(uid, out string? remoteId) && !string.IsNullOrWhiteSpace(remoteId))
                list.Add(remoteId);
        }
        return list;
    }

    static string UidMapPath(string accountId) =>
        Path.Combine(AppPaths.UserData, AccountSettings.AccountsFolderName, accountId, UidMapFileName);

    static ConcurrentDictionary<uint, string> LoadUidMap(string accountId) =>
        uidMapsByAccount.GetOrAdd(accountId, static id =>
        {
            var dict = new ConcurrentDictionary<uint, string>();
            string path = UidMapPath(id);
            if (!File.Exists(path)) return dict;
            try
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<uint, string>>(File.ReadAllText(path));
                if (loaded != null)
                {
                    foreach (var kv in loaded)
                        dict[kv.Key] = kv.Value;
                }
            }
            catch (Exception ex)
            {
                Log($"Could not read fastmail-uids.json for '{id}':\n{ex}", LogLevel.Warning, Run: IsLogEnabled);
            }
            return dict;
        });

    static void SaveUidMap(string accountId, ConcurrentDictionary<uint, string> map)
    {
        try
        {
            string path = UidMapPath(accountId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(map, JsonDefaults.Indented));
        }
        catch (Exception ex)
        {
            Log($"Could not write fastmail-uids.json for '{accountId}':\n{ex}", LogLevel.Warning, Run: IsLogEnabled);
        }
    }
}
