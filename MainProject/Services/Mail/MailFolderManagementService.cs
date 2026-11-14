using MailKit;
using MailKit.Net.Imap;
using MimeKit;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services.Mail;

/// <summary>
/// Creates, renames, soft-deletes (into <see cref="DeletedFolderStore"/>) and restores mailbox folders
/// across IMAP and local storage.
/// </summary>
public static class MailFolderManagementService
{
    const int MaxEmlDownloadsDuringSoftDelete = 500;

    public static bool CanRename(MailFolderData? folder)
    {
        if (folder == null || string.IsNullOrWhiteSpace(folder.FullName)) return false;
        if (folder.IsLocal)
            return folder.Role == FolderRole.None;
        return folder.Role == FolderRole.None
            && !string.Equals(folder.FullName, MessageStore.InboxFullName, StringComparison.OrdinalIgnoreCase);
    }

    public static bool CanDelete(MailFolderData? folder)
    {
        if (folder == null || string.IsNullOrWhiteSpace(folder.FullName)) return false;
        if (folder.IsLocal)
        {
            if (folder.Role == FolderRole.Outbox)
                return MessageStore.CountSummaries(folder.AccountId, folder.FullName) == 0;
            return true;
        }
        return folder.Role == FolderRole.None
            && !string.Equals(folder.FullName, MessageStore.InboxFullName, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<MailFolderData> CreateFolderAsync(
        MailAccountData account,
        string name,
        string? parentFullName = null,
        bool isLocal = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        string cleanName = SanitizeLeafName(name);
        if (string.IsNullOrWhiteSpace(cleanName))
            throw new ArgumentException("Enter a folder name.", nameof(name));

        if (isLocal || account.Protocol != IncomingProtocol.Imap)
        {
            string fullName = MessageStore.LocalFolderPrefix + cleanName;
            if (MessageStore.GetFolders(account.Id).Any(f => string.Equals(f.FullName, fullName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"A local folder named '{cleanName}' already exists.");

            var createdLocal = new MailFolderData
            {
                AccountId = account.Id,
                FullName = fullName,
                DisplayName = cleanName,
                Role = FolderRole.None,
                IsLocal = true,
                Selectable = true,
                IsExpanded = true
            };
            MessageStore.SaveFolder(createdLocal);
            MessageStore.InvalidateFolderCache(account.Id, fullName);
            Log($"Created local mail folder '{fullName}' on {account.EmailAddress}.");
            return createdLocal;
        }

        return await ResiliencePolicy.RunNetwork(async ct =>
        {
            using var client = await MailConnections.OpenImapAsync(account, ct);
            IMailFolder parentFolder;
            if (!string.IsNullOrWhiteSpace(parentFullName))
            {
                parentFolder = await client.GetFolderAsync(parentFullName, ct);
            }
            else if (client.PersonalNamespaces.Count > 0)
            {
                parentFolder = client.GetFolder(client.PersonalNamespaces[0]);
            }
            else
            {
                parentFolder = client.Inbox;
            }

            char sep = parentFolder.DirectorySeparator;
            if (sep != '\0')
                cleanName = cleanName.Replace(sep.ToString(), "-", StringComparison.Ordinal);

            var createdImap = await parentFolder.CreateAsync(cleanName, isMessageFolder: true, ct)
                ?? throw new IOException("The IMAP server did not return the created folder.");

            var createdData = new MailFolderData
            {
                AccountId = account.Id,
                FullName = createdImap.FullName,
                DisplayName = createdImap.Name,
                Role = FolderRole.None,
                IsLocal = false,
                Selectable = !createdImap.Attributes.HasFlag(FolderAttributes.NoSelect),
                Delimiter = createdImap.DirectorySeparator,
                IsExpanded = true
            };
            MessageStore.SaveFolder(createdData);
            MessageStore.InvalidateFolderCache(account.Id, createdData.FullName);
            await client.DisconnectAsync(true, ct);
            Log($"Created IMAP folder '{createdData.FullName}' on {account.EmailAddress}.");
            return createdData;
        }, cancellationToken, account);
    }

    public static async Task<MailFolderData> RenameFolderAsync(
        MailAccountData account,
        MailFolderData folder,
        string newLeafName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(folder);
        if (!CanRename(folder))
            throw new InvalidOperationException("System folders cannot be renamed.");

        string cleanName = SanitizeLeafName(newLeafName);
        if (string.IsNullOrWhiteSpace(cleanName))
            throw new ArgumentException("Enter a new folder name.", nameof(newLeafName));

        if (folder.IsLocal)
        {
            string newFullName = MessageStore.LocalFolderPrefix + cleanName;
            if (string.Equals(folder.FullName, newFullName, StringComparison.Ordinal))
                return folder;

            if (MessageStore.GetFolders(account.Id).Any(f => string.Equals(f.FullName, newFullName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"A local folder named '{cleanName}' already exists.");

            MoveCachedFolderDirectory(account.Id, folder, newFullName, cleanName);
            var updated = MessageStore.GetFolders(account.Id).First(f => f.FullName == newFullName);
            if (MailUiState.SelectedFolder?.AccountId == account.Id && MailUiState.SelectedFolder.FullName == folder.FullName)
                MailUiState.SelectFolder(updated);
            return updated;
        }

        return await ResiliencePolicy.RunNetwork(async ct =>
        {
            using var client = await MailConnections.OpenImapAsync(account, ct);
            var serverFolder = await client.GetFolderAsync(folder.FullName, ct);
            char sep = serverFolder.DirectorySeparator;
            if (sep != '\0')
                cleanName = cleanName.Replace(sep.ToString(), "-", StringComparison.Ordinal);

            IMailFolder parentImap;
            int cut = sep != '\0' ? folder.FullName.LastIndexOf(sep) : -1;
            if (cut > 0)
                parentImap = await client.GetFolderAsync(folder.FullName[..cut], ct);
            else if (client.PersonalNamespaces.Count > 0)
                parentImap = client.GetFolder(client.PersonalNamespaces[0]);
            else
                parentImap = client.Inbox;

            string oldFullName = folder.FullName;
            await serverFolder.RenameAsync(parentImap, cleanName, ct);
            string newFullName = serverFolder.FullName;
            string newDisplay = serverFolder.Name;
            await client.DisconnectAsync(true, ct);

            MoveCachedFolderDirectory(account.Id, folder, newFullName, newDisplay);
            var updated = MessageStore.GetFolders(account.Id).FirstOrDefault(f => f.FullName == newFullName)
                ?? new MailFolderData { AccountId = account.Id, FullName = newFullName, DisplayName = newDisplay, Delimiter = sep };
            if (MailUiState.SelectedFolder?.AccountId == account.Id && MailUiState.SelectedFolder.FullName == oldFullName)
                MailUiState.SelectFolder(updated);
            Log($"Renamed IMAP folder '{oldFullName}' -> '{newFullName}' on {account.EmailAddress}.");
            return updated;
        }, cancellationToken, account);
    }

    public static async Task<DeletedFolderEntry> MoveMailFolderToTrashAsync(
        MailAccountData account,
        MailFolderData folder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(folder);
        if (!CanDelete(folder))
            throw new InvalidOperationException($"Folder '{folder.DisplayName}' is a required system folder and cannot be deleted.");

        var allFolders = MessageStore.GetFolders(account.Id);
        List<MailFolderData> subtree = CollectSubtree(allFolders, folder);

        if (!folder.IsLocal && account.Protocol == IncomingProtocol.Imap)
        {
            await ResiliencePolicy.RunNetwork(async ct =>
            {
                using var client = await MailConnections.OpenImapAsync(account, ct);
                // Process deepest children first so parent deletion succeeds on every IMAP server.
                foreach (var item in subtree.AsEnumerable().Reverse())
                {
                    IMailFolder imapFolder;
                    try
                    {
                        imapFolder = await client.GetFolderAsync(item.FullName, ct);
                    }
                    catch (FolderNotFoundException)
                    {
                        continue;
                    }

                    if (!imapFolder.Attributes.HasFlag(FolderAttributes.NoSelect))
                    {
                        try
                        {
                            await EnsureFolderBackedUpLocallyAsync(account, item, imapFolder, ct);
                        }
                        catch (Exception ex)
                        {
                            Log($"Partial body backup before deleting '{item.FullName}': {ex.Message}", LogLevel.Warning);
                        }

                        if (imapFolder.IsOpen)
                            await imapFolder.CloseAsync(expunge: false, ct);
                    }

                    await imapFolder.DeleteAsync(ct);
                }
                await client.DisconnectAsync(true, ct);
            }, cancellationToken, account);
        }

        string entryId = Guid.NewGuid().ToString("N");
        string entryDir = DeletedFolderStore.EntryDirectory(entryId);
        List<ArchivedMailFolderSnapshot> snapshots = [];
        int totalMessages = 0;

        for (int i = 0; i < subtree.Count; i++)
        {
            var srcFolder = subtree[i];
            string relDir = Path.Combine("Folders", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            string destDir = Path.Combine(entryDir, relDir);
            Directory.CreateDirectory(destDir);

            int count = MessageStore.CountSummaries(account.Id, srcFolder.FullName);
            totalMessages += count;

            string srcCacheDir = MessageStore.FolderCachePath(account.Id, srcFolder.FullName);
            if (Directory.Exists(srcCacheDir))
                CopyDirectoryRecursive(srcCacheDir, destDir);
            else
                AtomicFile.WriteAllText(Path.Combine(destDir, MessageStore.FolderInfoFileName),
                    System.Text.Json.JsonSerializer.Serialize(srcFolder, JsonDefaults.Indented));

            snapshots.Add(new ArchivedMailFolderSnapshot
            {
                Folder = CloneFolderData(srcFolder),
                RelativeBackupFolder = relDir,
                MessageCount = count
            });
        }

        bool deletedWasSelected = MailUiState.SelectedFolder != null
            && MailUiState.SelectedFolder.AccountId == account.Id
            && subtree.Any(f => string.Equals(f.FullName, MailUiState.SelectedFolder.FullName, StringComparison.Ordinal));

        foreach (var item in subtree)
            MessageStore.RemoveFolder(account.Id, item.FullName);

        var entry = new DeletedFolderEntry
        {
            Id = entryId,
            Kind = DeletedFolderKind.MailFolder,
            DisplayName = folder.DisplayName,
            FullPathLabel = folder.IsLocal ? $"{folder.DisplayName} (this PC)" : folder.FullName,
            DeletedAtUtc = DateTime.UtcNow,
            AccountId = account.Id,
            AccountEmail = account.EmailAddress,
            MailFolders = snapshots,
            TotalMessageCount = totalMessages
        };
        DeletedFolderStore.Add(entry);

        if (deletedWasSelected)
        {
            var remaining = MessageStore.GetFolders(account.Id);
            var fallback = remaining.FirstOrDefault(static f => f.Role == FolderRole.Inbox && f.Selectable)
                ?? remaining.FirstOrDefault(static f => f.Selectable);
            MailUiState.SelectFolder(fallback);
        }

        Log($"Moved mail folder '{folder.FullName}' ({totalMessages} cached message(s)) to Deleted Folders Trash.");
        return entry;
    }

    public static async Task RestoreDeletedEntryAsync(DeletedFolderEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Kind == DeletedFolderKind.AccountGroup)
        {
            if (!AccountFolderStore.RestoreDeletedFolder(entry))
                throw new InvalidOperationException("Could not restore the account group folder.");
            return;
        }

        var account = AccountStore.GetById(entry.AccountId)
            ?? throw new InvalidOperationException($"Account '{entry.AccountEmail}' no longer exists on this device.");

        string entryDir = DeletedFolderStore.EntryDirectory(entry.Id);
        var orderedSnapshots = entry.MailFolders
            .OrderBy(static s => s.Folder.FullName.Length)
            .ToList();

        bool hasRemoteImap = account.Protocol == IncomingProtocol.Imap
            && orderedSnapshots.Any(static s => !s.Folder.IsLocal);

        if (hasRemoteImap)
        {
            await ResiliencePolicy.RunNetwork(async ct =>
            {
                using var client = await MailConnections.OpenImapAsync(account, ct);
                foreach (var snap in orderedSnapshots.Where(static s => !s.Folder.IsLocal))
                {
                    var imapFolder = await GetOrCreateImapPathAsync(client, snap.Folder, ct);
                    string backupDir = Path.Combine(entryDir, snap.RelativeBackupFolder);
                    await ReuploadBackedUpMessagesAsync(imapFolder, backupDir, ct);
                }
                await client.DisconnectAsync(true, ct);
            }, cancellationToken, account);
        }

        foreach (var snap in orderedSnapshots)
        {
            string backupDir = Path.Combine(entryDir, snap.RelativeBackupFolder);
            string destCacheDir = MessageStore.FolderCachePath(account.Id, snap.Folder.FullName);

            if (snap.Folder.IsLocal || account.Protocol != IncomingProtocol.Imap)
            {
                Directory.CreateDirectory(destCacheDir);
                if (Directory.Exists(backupDir))
                    CopyDirectoryRecursive(backupDir, destCacheDir);
                else
                    MessageStore.SaveFolder(CloneFolderData(snap.Folder));
            }
            else
            {
                var freshFolder = CloneFolderData(snap.Folder);
                freshFolder.LastSeenUid = 0;
                freshFolder.OldestFetchedUid = 0;
                MessageStore.SaveFolder(freshFolder);
            }

            MessageStore.InvalidateFolderCache(account.Id, snap.Folder.FullName);
        }

        DeletedFolderStore.RemovePermanently(entry.Id);

        if (hasRemoteImap && orderedSnapshots.FirstOrDefault(static s => !s.Folder.IsLocal) is { } primaryRemote)
            ImapSyncService.KickFolderSync(account, primaryRemote.Folder.FullName);

        Log($"Restored deleted mail folder '{entry.FullPathLabel}' for {account.EmailAddress}.");
    }

    static async Task EnsureFolderBackedUpLocallyAsync(
        MailAccountData account,
        MailFolderData localMeta,
        IMailFolder imapFolder,
        CancellationToken ct)
    {
        await imapFolder.OpenAsync(FolderAccess.ReadOnly, ct);
        if (imapFolder.Count == 0) return;

        var items = await imapFolder.FetchAsync(0, -1,
            MessageSummaryItems.UniqueId | MessageSummaryItems.Flags | MessageSummaryItems.Envelope | MessageSummaryItems.InternalDate | MessageSummaryItems.Size,
            ct);

        List<MailMessageSummary> fetchedSummaries = [];
        foreach (var item in items)
        {
            if (!item.UniqueId.IsValid) continue;
            var existing = MessageStore.GetSummary(account.Id, localMeta.FullName, item.UniqueId.Id);
            if (existing != null)
            {
                fetchedSummaries.Add(existing);
                continue;
            }

            fetchedSummaries.Add(new MailMessageSummary
            {
                Uid = item.UniqueId.Id,
                MessageId = item.Envelope?.MessageId ?? string.Empty,
                Subject = item.Envelope?.Subject ?? string.Empty,
                FromName = item.Envelope?.From.Mailboxes.FirstOrDefault()?.Name ?? string.Empty,
                FromAddress = item.Envelope?.From.Mailboxes.FirstOrDefault()?.Address ?? string.Empty,
                ToAddresses = item.Envelope == null ? string.Empty : string.Join(", ", item.Envelope.To.Mailboxes.Select(static m => m.Address)),
                DateUtc = item.InternalDate?.UtcDateTime ?? item.Envelope?.Date?.UtcDateTime ?? DateTime.UtcNow,
                SizeBytes = item.Size ?? 0,
                Flags = ToLocalFlags(item.Flags ?? MessageFlags.None)
            });
        }

        if (fetchedSummaries.Count > 0)
            MessageStore.UpsertSummaries(account.Id, localMeta.FullName, fetchedSummaries);

        int downloaded = 0;
        foreach (var item in items.OrderByDescending(static x => x.UniqueId.Id))
        {
            if (downloaded >= MaxEmlDownloadsDuringSoftDelete) break;
            if (!item.UniqueId.IsValid) continue;
            if (MessageStore.HasFullMessage(account.Id, localMeta.FullName, item.UniqueId.Id)) continue;

            using var ms = new MemoryStream();
            var mime = await imapFolder.GetMessageAsync(item.UniqueId, ct);
            await mime.WriteToAsync(ms, ct);
            MessageStore.SaveFullMessage(account.Id, localMeta.FullName, item.UniqueId.Id, ms.ToArray());
            downloaded++;
        }
    }

    static async Task<IMailFolder> GetOrCreateImapPathAsync(ImapClient client, MailFolderData meta, CancellationToken ct)
    {
        try
        {
            var existing = await client.GetFolderAsync(meta.FullName, ct);
            if (!existing.Attributes.HasFlag(FolderAttributes.NonExistent))
                return existing;
        }
        catch (FolderNotFoundException)
        {
        }

        IMailFolder current = client.PersonalNamespaces.Count > 0
            ? client.GetFolder(client.PersonalNamespaces[0])
            : client.Inbox;

        char sep = meta.Delimiter != '\0' ? meta.Delimiter : current.DirectorySeparator;
        string relativePath = meta.FullName;
        if (!string.IsNullOrEmpty(current.FullName) && sep != '\0'
            && relativePath.StartsWith(current.FullName + sep, StringComparison.OrdinalIgnoreCase))
        {
            relativePath = relativePath[(current.FullName.Length + 1)..];
        }

        string[] segments = sep == '\0' ? [relativePath] : relativePath.Split(sep, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segments.Length; i++)
        {
            string seg = segments[i];
            try
            {
                var sub = await current.GetSubfolderAsync(seg, ct);
                if (sub != null && !sub.Attributes.HasFlag(FolderAttributes.NonExistent))
                {
                    current = sub;
                    continue;
                }
            }
            catch (FolderNotFoundException)
            {
            }

            current = await current.CreateAsync(seg, isMessageFolder: true, ct)
                ?? throw new IOException($"The IMAP server did not return created folder '{seg}'.");
        }
        return current;
    }

    static async Task ReuploadBackedUpMessagesAsync(IMailFolder imapFolder, string backupDir, CancellationToken ct)
    {
        string indexPath = Path.Combine(backupDir, MessageStore.IndexFileName);
        string messagesDir = Path.Combine(backupDir, MessageStore.MessagesFolderName);
        if (!Directory.Exists(messagesDir)) return;

        Dictionary<uint, MailMessageSummary> byUid = [];
        if (File.Exists(indexPath))
        {
            foreach (string line in File.ReadLines(indexPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var s = System.Text.Json.JsonSerializer.Deserialize<MailMessageSummary>(line, JsonDefaults.SingleLine);
                    if (s != null) byUid[s.Uid] = s;
                }
                catch
                {
                }
            }
        }

        foreach (string emlFile in Directory.GetFiles(messagesDir, "*" + MessageStore.MessageExtension).OrderBy(static f => f, StringComparer.OrdinalIgnoreCase))
        {
            string stem = Path.GetFileNameWithoutExtension(emlFile);
            uint.TryParse(stem, out uint uid);
            byUid.TryGetValue(uid, out var summary);

            using var fs = File.OpenRead(emlFile);
            var mime = await MimeMessage.LoadAsync(fs, ct);
            MessageFlags imapFlags = ToImapFlags(summary?.Flags ?? MailFlags.Seen);
            DateTime dateUtc = summary?.DateUtc is { Year: > 1970 } dt
                ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
                : (mime.Date != DateTimeOffset.MinValue ? mime.Date.UtcDateTime : DateTime.UtcNow);

            var request = new AppendRequest(mime, imapFlags, new DateTimeOffset(dateUtc));
            await imapFolder.AppendAsync(request, ct);
        }
    }

    static MailFlags ToLocalFlags(MessageFlags flags)
    {
        MailFlags result = MailFlags.None;
        if (flags.HasFlag(MessageFlags.Seen)) result |= MailFlags.Seen;
        if (flags.HasFlag(MessageFlags.Answered)) result |= MailFlags.Answered;
        if (flags.HasFlag(MessageFlags.Flagged)) result |= MailFlags.Flagged;
        if (flags.HasFlag(MessageFlags.Draft)) result |= MailFlags.Draft;
        return result;
    }

    static MessageFlags ToImapFlags(MailFlags flags)
    {
        MessageFlags result = MessageFlags.None;
        if (flags.HasFlag(MailFlags.Seen)) result |= MessageFlags.Seen;
        if (flags.HasFlag(MailFlags.Answered)) result |= MessageFlags.Answered;
        if (flags.HasFlag(MailFlags.Flagged)) result |= MessageFlags.Flagged;
        if (flags.HasFlag(MailFlags.Draft)) result |= MessageFlags.Draft;
        return result;
    }

    static List<MailFolderData> CollectSubtree(List<MailFolderData> allFolders, MailFolderData root)
    {
        List<MailFolderData> list = [root];
        if (!root.IsLocal && root.Delimiter != '\0')
        {
            string prefix = root.FullName + root.Delimiter;
            list.AddRange(allFolders
                .Where(f => f.FullName.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(static f => f.FullName.Length));
        }
        return list;
    }

    static void MoveCachedFolderDirectory(string accountId, MailFolderData oldFolder, string newFullName, string newDisplayName)
    {
        string oldDir = MessageStore.FolderCachePath(accountId, oldFolder.FullName);
        string newDir = MessageStore.FolderCachePath(accountId, newFullName);
        string? parentDir = Path.GetDirectoryName(newDir);
        if (!string.IsNullOrEmpty(parentDir))
            Directory.CreateDirectory(parentDir);

        if (Directory.Exists(oldDir) && !string.Equals(oldDir, newDir, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(newDir))
                Directory.Delete(newDir, recursive: true);
            Directory.Move(oldDir, newDir);
        }

        var updated = CloneFolderData(oldFolder);
        updated.FullName = newFullName;
        updated.DisplayName = newDisplayName;
        MessageStore.RemoveFolder(accountId, oldFolder.FullName);
        MessageStore.SaveFolder(updated);
        MessageStore.InvalidateFolderCache(accountId, newFullName);
    }

    static void CopyDirectoryRecursive(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string destFile = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }
        foreach (string subDir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectoryRecursive(subDir, Path.Combine(targetDir, Path.GetFileName(subDir)));
        }
    }

    static MailFolderData CloneFolderData(MailFolderData src) =>
        System.Text.Json.JsonSerializer.Deserialize<MailFolderData>(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(src, JsonDefaults.SingleLine),
            JsonDefaults.SingleLine) ?? new MailFolderData { AccountId = src.AccountId, FullName = src.FullName, DisplayName = src.DisplayName };

    static string SanitizeLeafName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        string trimmed = raw.Trim();
        trimmed = trimmed.Replace('/', '-').Replace('\\', '-').Trim();
        return trimmed;
    }
}
