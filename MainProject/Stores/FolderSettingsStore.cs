using System.Text.Json;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;

namespace MyLovelyMail.MainProject.Stores;

/// <summary>Null entries inherit; MinValue explicitly enables notifications, MaxValue mutes indefinitely.</summary>
public sealed record FolderPreferences
{
    public DateTimeOffset? MutedUntilUtc { get; init; }
    public bool? ShowUnreadBadge { get; init; }
}

/// <summary>
/// Persists standard-folder defaults and exact account/folder overrides in UserData/folder-settings.json.
/// Mute affects notification delivery only. Trash never notifies or contributes unread badges.
/// </summary>
public static class FolderSettingsStore
{
    const bool IsLogEnabled = true;
    public const string FileName = "folder-settings.json";
    static readonly object SyncRoot = new();
    static FolderSettingsDocument? Document;
    static Exception? LoadError;

    /// <summary>The common server roles offered in global settings; custom folders have account settings only.</summary>
    public static IReadOnlyList<FolderRole> StandardRoles { get; } = Array.AsReadOnly<FolderRole>(
        [FolderRole.Inbox, FolderRole.Junk, FolderRole.Trash, FolderRole.Sent,
         FolderRole.Drafts, FolderRole.Archive, FolderRole.AllMail, FolderRole.Flagged]);

    /// <summary>On-disk scopes; folder paths use ordinal identity, matching MessageStore.</summary>
    public sealed class FolderSettingsDocument
    {
        public Dictionary<FolderRole, FolderPreferences> Defaults { get; init; } = [];
        public Dictionary<string, Dictionary<string, FolderPreferences>> Accounts { get; init; } = [];
    }

    /// <summary>Returns just the saved overrides, leaving inherited entries null.</summary>
    /// <param name="role">Standard role for the global scope.</param>
    /// <param name="accountId">Null selects global defaults.</param>
    /// <param name="fullName">Exact folder path for an account scope.</param>
    /// <returns>The immutable saved entry, or an empty entry.</returns>
    public static FolderPreferences GetOverrides(FolderRole role, string? accountId = null, string? fullName = null)
    {
        lock (SyncRoot)
        {
            var document = Read();
            return accountId == null
                ? document.Defaults.GetValueOrDefault(role) ?? new()
                : fullName != null && document.Accounts.TryGetValue(accountId, out var folders)
                    ? folders.GetValueOrDefault(fullName) ?? new() : new();
        }
    }

    /// <summary>Resolves each field separately: exact folder, standard-role default, then shipped default.</summary>
    /// <param name="role">Role assigned by the mail engine.</param>
    /// <param name="accountId">Optional account scope.</param>
    /// <param name="fullName">Exact folder path.</param>
    /// <returns>Effective notification and badge preferences; Trash is always disabled.</returns>
    public static FolderPreferences Resolve(FolderRole role, string? accountId = null, string? fullName = null)
    {
        if (role == FolderRole.Trash)
            return new() { MutedUntilUtc = DateTimeOffset.MaxValue, ShowUnreadBadge = false };
        lock (SyncRoot)
        {
            var defaults = GetOverrides(role);
            var overrides = accountId == null ? defaults : GetOverrides(role, accountId, fullName);
            return new()
            {
                MutedUntilUtc = overrides.MutedUntilUtc ?? defaults.MutedUntilUtc
                    ?? (role == FolderRole.Junk ? DateTimeOffset.MaxValue : DateTimeOffset.MinValue),
                ShowUnreadBadge = overrides.ShowUnreadBadge ?? defaults.ShowUnreadBadge ?? true
            };
        }
    }

    /// <summary>Reads the current deadline on every decision, so expiry needs no timer or message mutations.</summary>
    /// <param name="folder">Folder receiving the mail.</param>
    /// <returns>Whether all new-mail notifications for this folder are suppressed now.</returns>
    public static bool IsMuted(MailFolderData folder) =>
        Resolve(folder.Role, folder.AccountId, folder.FullName).MutedUntilUtc > DateTimeOffset.UtcNow;

    /// <summary>Badge policy is independent of mute; Trash cannot display or contribute an unread count.</summary>
    /// <param name="folder">Folder whose count is being displayed.</param>
    /// <returns>Whether its unread count may appear in folder/account badges.</returns>
    public static bool ShowsUnreadCount(MailFolderData folder) =>
        Resolve(folder.Role, folder.AccountId, folder.FullName).ShowUnreadBadge == true;

    /// <summary>Saves before replacing live state. A failed write retains both the previous file and preferences.</summary>
    /// <param name="role">Global standard role or account folder role.</param>
    /// <param name="preferences">Overrides to save; null removes the scope's overrides.</param>
    /// <param name="accountId">Null selects global defaults.</param>
    /// <param name="fullName">Exact account folder path.</param>
    /// <returns>False after logging the complete failure; callers must show failure rather than success.</returns>
    public static bool Save(FolderRole role, FolderPreferences? preferences, string? accountId = null, string? fullName = null)
    {
        if (preferences == new FolderPreferences()) preferences = null;
        try
        {
            lock (SyncRoot)
            {
                var current = Read();
                if (LoadError != null)
                {
                    // Retry a previously failed read before refusing this edit; a transient file lock may have cleared.
                    Reload();
                    current = Read();
                }
                if (LoadError != null)
                    throw new InvalidOperationException("The existing folder settings could not be read; refusing to overwrite them.", LoadError);
                if (accountId == null && !StandardRoles.Contains(role))
                    throw new ArgumentException("Global folder settings require a standard role.", nameof(role));
                if (accountId != null && string.IsNullOrWhiteSpace(fullName))
                    throw new ArgumentException("Account folder settings require a full folder name.", nameof(fullName));

                var next = new FolderSettingsDocument
                {
                    Defaults = new(current.Defaults),
                    Accounts = new(current.Accounts)
                };
                if (accountId == null)
                {
                    if (preferences == null) next.Defaults.Remove(role);
                    else next.Defaults[role] = preferences;
                }
                else
                {
                    var folders = new Dictionary<string, FolderPreferences>(current.Accounts.GetValueOrDefault(accountId) ?? []);
                    if (preferences == null) folders.Remove(fullName!);
                    else folders[fullName!] = preferences;
                    if (folders.Count == 0) next.Accounts.Remove(accountId);
                    else next.Accounts[accountId] = folders;
                }

                Directory.CreateDirectory(AppPaths.UserData);
                AtomicFile.WriteAllText(Path.Combine(AppPaths.UserData, FileName), JsonSerializer.Serialize(next, JsonDefaults.Indented));
                Document = next;
                Log($"Folder settings saved: account={accountId ?? "global"}; folder={fullName ?? role.ToString()}; preferences={preferences}", Run: IsLogEnabled);
                return true;
            }
        }
        catch (Exception exception)
        {
            Log($"Folder settings save failed: account={accountId ?? "global"}; folder={fullName ?? role.ToString()}; {exception}", LogLevel.Error, Run: IsLogEnabled);
            return false;
        }
    }

    /// <summary>Invalidates cached preferences after a migration import.</summary>
    public static void Reload()
    {
        lock (SyncRoot)
        {
            Document = null;
            LoadError = null;
        }
    }

    /// <summary>Loads once under SyncRoot. Failed reads use shipped defaults but prevent destructive saves.</summary>
    /// <returns>The cached document, including an empty first-run document.</returns>
    static FolderSettingsDocument Read()
    {
        if (Document != null) return Document;
        string path = Path.Combine(AppPaths.UserData, FileName);
        try
        {
            Document = File.Exists(path)
                ? JsonSerializer.Deserialize<FolderSettingsDocument>(File.ReadAllText(path), JsonDefaults.Indented)
                    ?? throw new JsonException("Folder settings contain null instead of a document.")
                : new();
            if (Document.Defaults == null || Document.Accounts == null
                || Document.Defaults.Values.Any(static value => value == null)
                || Document.Accounts.Values.Any(static folders => folders == null || folders.Values.Any(static value => value == null)))
                throw new JsonException("Folder settings contain null scopes or preferences.");
            Log($"Folder settings loaded from '{path}': defaults={Document.Defaults.Count}; accounts={Document.Accounts.Count}.", Run: IsLogEnabled);
        }
        catch (Exception exception)
        {
            LoadError = exception;
            Document = new();
            Log($"Folder settings load failed for '{path}'; using shipped defaults without overwriting the file: {exception}", LogLevel.Error, Run: IsLogEnabled);
        }
        return Document;
    }
}
