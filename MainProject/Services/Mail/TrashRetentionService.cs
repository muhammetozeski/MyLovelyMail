using System.Globalization;
using MailKit;
using MailKit.Net.Imap;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services.Mail;

/// <summary>
/// Resolves Trash and Deleted Folders retention policies, enforces local auto-purge of expired
/// items, and exchanges server-side Trash auto-expunge metadata via RFC 5464 IMAP <c>METADATA</c>
/// when supported by the mail server.
/// </summary>
public static class TrashRetentionService
{
    public const int MinRetentionDays = 1;
    public const int MaxRetentionDays = 3650;

    static readonly MetadataTag CyrusPrivateExpireTag = new("/private/vendor/cmu/cyrus-imapd/expire");
    static readonly MetadataTag CyrusSharedExpireTag = new("/shared/vendor/cmu/cyrus-imapd/expire");
    static readonly MetadataTag DovecotPrivateExpireTag = new("/private/vendor/dovecot/expire");
    static readonly MetadataTag DovecotSharedExpireTag = new("/shared/vendor/dovecot/expire");

    static readonly MetadataTag[] ExpireReadTags =
    [
        CyrusPrivateExpireTag,
        CyrusSharedExpireTag,
        DovecotPrivateExpireTag,
        DovecotSharedExpireTag
    ];

    static readonly HashSet<string> nonWritableMetadataAccounts = new(StringComparer.Ordinal);
    static readonly Lock nonWritableLock = new();

    public static event Action? OnRetentionChanged;

    public static int ClampDays(int days, int fallback) =>
        days <= 0 ? fallback : Math.Clamp(days, MinRetentionDays, MaxRetentionDays);

    /// <summary>Resolves effective (AutoEmpty, Days) for a normal mail Trash folder.</summary>
    public static (bool AutoEmpty, int Days) ResolveMailTrashPolicy(string? accountId, string? folderFullName = null)
    {
        bool autoEmpty = Settings.AutoEmptyTrash.Value;
        int days = ClampDays(Settings.TrashRetentionDays.Value, Settings.DefaultTrashRetentionDays);

        var globalRoleOverrides = FolderSettingsStore.GetOverrides(FolderRole.Trash);
        if (globalRoleOverrides.AutoEmptyTrash is { } gAuto) autoEmpty = gAuto;
        if (globalRoleOverrides.TrashRetentionDays is { } gDays) days = ClampDays(gDays, days);

        if (!string.IsNullOrWhiteSpace(accountId))
        {
            var accSettings = AccountStore.GetSettings(accountId);
            if (accSettings.AutoEmptyTrash.IsOverridden)
                autoEmpty = accSettings.AutoEmptyTrash.Value;
            if (accSettings.TrashRetentionDays.IsOverridden)
                days = ClampDays(accSettings.TrashRetentionDays.Value, days);

            if (!string.IsNullOrWhiteSpace(folderFullName))
            {
                var folderOverrides = FolderSettingsStore.GetOverrides(FolderRole.Trash, accountId, folderFullName);
                if (folderOverrides.AutoEmptyTrash is { } fAuto) autoEmpty = fAuto;
                if (folderOverrides.TrashRetentionDays is { } fDays) days = ClampDays(fDays, days);
            }
        }

        return (autoEmpty, days);
    }

    /// <summary>Resolves effective (AutoPurge, Days) for the Deleted Folders Trash bin.</summary>
    public static (bool AutoPurge, int Days) ResolveDeletedFoldersPolicy(string? accountId = null)
    {
        bool autoPurge = Settings.AutoPurgeDeletedFolders.Value;
        int days = ClampDays(Settings.DeletedFolderRetentionDays.Value, Settings.DefaultDeletedFolderRetentionDays);

        if (!string.IsNullOrWhiteSpace(accountId))
        {
            var accSettings = AccountStore.GetSettings(accountId);
            if (accSettings.AutoPurgeDeletedFolders.IsOverridden)
                autoPurge = accSettings.AutoPurgeDeletedFolders.Value;
            if (accSettings.DeletedFolderRetentionDays.IsOverridden)
                days = ClampDays(accSettings.DeletedFolderRetentionDays.Value, days);
        }

        return (autoPurge, days);
    }

    /// <summary>Compact label displayed right on the Trash folder row in the sidebar.</summary>
    public static string BuildSidebarTrashBadge(MailFolderData folder)
    {
        var (autoEmpty, appDays) = ResolveMailTrashPolicy(folder.AccountId, folder.FullName);
        string appPart = autoEmpty ? $"App {appDays}d" : "App off";
        if (folder.ServerRetentionDays is > 0 and int srvDays)
            return $"Srv {srvDays}d · {appPart}";
        return appPart;
    }

    /// <summary>Detailed header line displayed at the top of a Trash folder view and in its tooltip.</summary>
    public static string BuildTrashHeaderDescription(MailFolderData folder)
    {
        var (autoEmpty, appDays) = ResolveMailTrashPolicy(folder.AccountId, folder.FullName);
        string appText = autoEmpty ? $"App: {appDays} day(s)" : "App: Off";

        if (folder.ServerRetentionDays is > 0 and int srvDays)
            return $"Auto-delete — Server: {srvDays} day(s) · {appText}";

        if (folder.ServerSupportsRetentionMetadata)
            return $"Auto-delete — Server: none set · {appText}";

        return $"Auto-delete — {appText}";
    }

    /// <summary>Compact badge displayed on the Deleted Folders Trash row in the sidebar.</summary>
    public static string BuildDeletedFoldersBadge(string? accountId = null)
    {
        var (autoPurge, days) = ResolveDeletedFoldersPolicy(accountId);
        return autoPurge ? $"{days}d" : "off";
    }

    /// <summary>Formats remaining time before a <see cref="DeletedFolderEntry"/> is permanently purged.</summary>
    public static string FormatRemainingForDeletedFolder(DeletedFolderEntry entry)
    {
        string? scopeAccount = entry.Kind == DeletedFolderKind.MailFolder ? entry.AccountId : null;
        var (autoPurge, days) = ResolveDeletedFoldersPolicy(scopeAccount);
        if (!autoPurge)
            return "Auto-delete off";

        TimeSpan remaining = entry.DeletedAtUtc.AddDays(days) - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
            return "Expiring now";
        if (remaining.TotalDays >= 1)
            return $"{(int)remaining.TotalDays}d {remaining.Hours}h left";
        if (remaining.TotalHours >= 1)
            return $"{(int)remaining.TotalHours}h {remaining.Minutes}m left";
        return $"{Math.Max(1, remaining.Minutes)}m left";
    }

    public static void NotifySettingsUpdated(string? specificAccountId = null, string? specificFolderFullName = null)
    {
        RunMaintenance();
        OnRetentionChanged?.Invoke();

        if (!string.IsNullOrWhiteSpace(specificAccountId))
        {
            if (AccountStore.GetById(specificAccountId) is { Protocol: IncomingProtocol.Imap, Enabled: true } acc)
                PushTrashRetentionToServerInBackground(acc, specificFolderFullName);
        }
        else
        {
            foreach (var acc in AccountStore.Accounts.Where(static a => a.Protocol == IncomingProtocol.Imap && a.Enabled))
            {
                var accSettings = AccountStore.GetSettings(acc.Id);
                if (!accSettings.AutoEmptyTrash.IsOverridden && !accSettings.TrashRetentionDays.IsOverridden)
                    PushTrashRetentionToServerInBackground(acc, null);
            }
        }
    }

    /// <summary>
    /// Purges expired folders from <see cref="DeletedFolderStore"/> and expired messages from
    /// every account's <see cref="FolderRole.Trash"/> folders.
    /// </summary>
    public static (int PurgedFolders, int PurgedMessages) RunMaintenance()
    {
        int purgedFolders = PurgeExpiredDeletedFolders();
        int purgedMessages = PurgeExpiredTrashMessages();
        if (purgedFolders > 0 || purgedMessages > 0)
            Log($"Trash retention pass: purged {purgedFolders} expired folder(s) and {purgedMessages} expired Trash message(s).");
        return (purgedFolders, purgedMessages);
    }

    public static int PurgeExpiredDeletedFolders()
    {
        if (!DeletedFolderStore.HasAny) return 0;

        DateTime nowUtc = DateTime.UtcNow;
        List<string> expiredIds = [];
        foreach (var entry in DeletedFolderStore.GetAll())
        {
            string? scopeAccount = entry.Kind == DeletedFolderKind.MailFolder ? entry.AccountId : null;
            var (autoPurge, days) = ResolveDeletedFoldersPolicy(scopeAccount);
            if (!autoPurge) continue;

            if (nowUtc >= entry.DeletedAtUtc.AddDays(days))
                expiredIds.Add(entry.Id);
        }

        return expiredIds.Count == 0 ? 0 : DeletedFolderStore.RemoveManyPermanently(expiredIds);
    }

    public static int PurgeExpiredTrashMessages()
    {
        DateTime nowUtc = DateTime.UtcNow;
        int totalRemoved = 0;

        foreach (var account in AccountStore.Accounts)
        {
            foreach (var trashFolder in MessageStore.GetFolders(account.Id).Where(static f => f.Role == FolderRole.Trash && f.Selectable))
            {
                var (autoEmpty, days) = ResolveMailTrashPolicy(account.Id, trashFolder.FullName);
                if (!autoEmpty || days <= 0) continue;

                DateTime cutoffUtc = nowUtc.AddDays(-days);
                var expired = MessageStore.GetSummaries(account.Id, trashFolder.FullName)
                    .Where(s => (s.TrashedAtUtc ?? s.DateUtc) <= cutoffUtc)
                    .ToList();

                if (expired.Count > 0)
                    totalRemoved += MessageActions.Delete(account, trashFolder.FullName, expired, DeleteBehavior.DeletePermanently);
            }
        }

        return totalRemoved;
    }

    /// <summary>
    /// Queries RFC 5464 IMAP <c>METADATA</c> on a Trash folder during folder-list sync to learn
    /// whether the server exposes its own auto-expunge retention in days.
    /// </summary>
    public static async Task<(bool SupportsMetadata, int? RetentionDays)> TryQueryServerRetentionAsync(
        ImapClient client,
        IMailFolder folder,
        CancellationToken cancellationToken)
    {
        if (!client.Capabilities.HasFlag(ImapCapabilities.Metadata))
            return (false, null);

        try
        {
            var options = new MetadataOptions { Depth = 0 };
            var values = await folder.GetMetadataAsync(options, ExpireReadTags, cancellationToken);
            foreach (var item in values)
            {
                if (TryParseExpireDays(item.Value, out int parsedDays))
                    return (true, parsedDays);
            }
            return (true, null);
        }
        catch (Exception ex)
        {
            Log($"IMAP METADATA query on '{folder.FullName}' not available: {ex.Message}", LogLevel.Warning);
            return (false, null);
        }
    }

    /// <summary>
    /// Pushes our configured Trash retention days to the IMAP server via RFC 5464 <c>SETMETADATA</c>
    /// when the server advertises <c>METADATA</c> support. If unsupported or refused, silently leaves
    /// local retention in charge ("first to delete wins").
    /// </summary>
    public static void PushTrashRetentionToServerInBackground(MailAccountData account, string? specificFolderFullName = null)
    {
        if (account.Protocol != IncomingProtocol.Imap) return;
        lock (nonWritableLock)
        {
            if (nonWritableMetadataAccounts.Contains(account.Id))
                return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await PushTrashRetentionToServerAsync(account, specificFolderFullName, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log($"Could not push Trash retention metadata to {account.EmailAddress}: {ex.Message}", LogLevel.Warning);
            }
        });
    }

    public static async Task<bool> PushTrashRetentionToServerAsync(
        MailAccountData account,
        string? specificFolderFullName = null,
        CancellationToken cancellationToken = default)
    {
        if (account.Protocol != IncomingProtocol.Imap) return false;

        var targetMeta = MessageStore.GetFolders(account.Id)
            .FirstOrDefault(f => !f.IsLocal && f.Role == FolderRole.Trash
                && (specificFolderFullName == null || string.Equals(f.FullName, specificFolderFullName, StringComparison.Ordinal)));

        if (targetMeta == null) return false;

        var (autoEmpty, days) = ResolveMailTrashPolicy(account.Id, targetMeta.FullName);
        string? desiredValue = autoEmpty ? days.ToString(CultureInfo.InvariantCulture) : null;

        return await ResiliencePolicy.RunNetwork(async ct =>
        {
            using var client = await MailConnections.OpenImapAsync(account, ct);
            if (!client.Capabilities.HasFlag(ImapCapabilities.Metadata))
            {
                lock (nonWritableLock) nonWritableMetadataAccounts.Add(account.Id);
                await client.DisconnectAsync(true, ct);
                return false;
            }

            var imapFolder = await client.GetFolderAsync(targetMeta.FullName, ct);
            bool pushed = false;
            try
            {
                var col = new MetadataCollection { new Metadata(CyrusPrivateExpireTag, desiredValue!) };
                await imapFolder.SetMetadataAsync(col, ct);
                pushed = true;
            }
            catch (Exception ex)
            {
                Log($"Server refused SETMETADATA ({CyrusPrivateExpireTag.Id}) on '{targetMeta.FullName}' ({account.EmailAddress}): {ex.Message}", LogLevel.Warning);
                lock (nonWritableLock) nonWritableMetadataAccounts.Add(account.Id);
            }

            var (supports, serverDays) = await TryQueryServerRetentionAsync(client, imapFolder, ct);
            await client.DisconnectAsync(true, ct);

            targetMeta.ServerSupportsRetentionMetadata = supports;
            targetMeta.ServerRetentionDays = pushed ? (autoEmpty ? days : null) : serverDays;
            MessageStore.SaveFolder(targetMeta);
            MessageStore.InvalidateFolderCache(account.Id, targetMeta.FullName);
            OnRetentionChanged?.Invoke();

            if (pushed)
                Log($"Notified server '{account.IncomingHost}' of Trash retention ({(autoEmpty ? $"{days}d" : "off")}) on '{targetMeta.FullName}'.");
            return pushed;
        }, cancellationToken, account);
    }

    internal static bool TryParseExpireDays(string? rawValue, out int days)
    {
        days = 0;
        if (string.IsNullOrWhiteSpace(rawValue)) return false;

        string s = rawValue.Trim();
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int plain) && plain > 0)
        {
            days = Math.Clamp(plain, MinRetentionDays, MaxRetentionDays);
            return true;
        }

        int numEnd = 0;
        while (numEnd < s.Length && char.IsDigit(s[numEnd]))
            numEnd++;
        if (numEnd == 0 || !int.TryParse(s[..numEnd], NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount) || amount <= 0)
            return false;

        string unit = s[numEnd..].Trim().ToLowerInvariant();
        days = unit switch
        {
            "d" or "day" or "days" => amount,
            "w" or "week" or "weeks" => amount * 7,
            "m" or "mo" or "month" or "months" => amount * 30,
            "h" or "hr" or "hour" or "hours" => Math.Max(1, (int)Math.Ceiling(amount / 24.0)),
            "s" or "sec" or "second" or "seconds" => Math.Max(1, (int)Math.Ceiling(amount / 86400.0)),
            _ => 0
        };
        if (days <= 0) return false;
        days = Math.Clamp(days, MinRetentionDays, MaxRetentionDays);
        return true;
    }
}
