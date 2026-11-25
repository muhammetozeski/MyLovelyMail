using System.Collections.Concurrent;
using System.Text.Json;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;

namespace MyLovelyMail.MainProject.Stores;

/// <summary>
/// Persists per-account mail folder sort order and collapsed state in <c>UserData/folder-layout.json</c>.
/// Storing layout in <c>UserData</c> (instead of only inside disposable <c>UserCache/.../folder.json</c>)
/// ensures that cache cleanup, folder resyncs, or IMAP folder-list refreshes never reset custom folder order
/// or push INBOX beneath local folders.
/// </summary>
public static class FolderLayoutStore
{
    const bool IsLogEnabled = true;
    const string LayoutFileName = "folder-layout.json";

    sealed class AccountFolderLayout
    {
        public List<string> OrderedFullNames { get; set; } = [];
        public HashSet<string> CollapsedFullNames { get; set; } = new(StringComparer.Ordinal);
    }

    static readonly Lock gate = new();
    static readonly ConcurrentDictionary<string, AccountFolderLayout> layouts = Load();

    static string LayoutPath => Path.Combine(AppPaths.UserData, LayoutFileName);

    /// <summary>True when the user has explicitly saved a custom folder order for this account.</summary>
    public static bool HasCustomOrder(string accountId) =>
        !string.IsNullOrWhiteSpace(accountId)
        && layouts.TryGetValue(accountId, out var layout)
        && layout.OrderedFullNames.Count > 0;

    /// <summary>Saves the complete ordered list of folder full names for an account.</summary>
    public static void SaveOrder(string accountId, IReadOnlyList<string> orderedFullNames)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return;
        lock (gate)
        {
            var layout = layouts.GetOrAdd(accountId, static _ => new AccountFolderLayout());
            layout.OrderedFullNames = [.. orderedFullNames.Where(static n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal)];
            PersistLocked();
        }
        Log($"Saved folder order for account {accountId} ({orderedFullNames.Count} folder(s)).", Run: IsLogEnabled);
    }

    /// <summary>Persists whether a folder's subtree is expanded in the sidebar.</summary>
    public static void SetExpanded(string accountId, string folderFullName, bool isExpanded)
    {
        if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(folderFullName)) return;
        lock (gate)
        {
            var layout = layouts.GetOrAdd(accountId, static _ => new AccountFolderLayout());
            if (isExpanded) layout.CollapsedFullNames.Remove(folderFullName);
            else layout.CollapsedFullNames.Add(folderFullName);
            PersistLocked();
        }
    }

    /// <summary>Applies saved UserData sort order and collapse state to a loaded folder list.</summary>
    public static void ApplyTo(string accountId, IList<MailFolderData> folders)
    {
        if (string.IsNullOrWhiteSpace(accountId) || folders.Count == 0) return;

        AccountFolderLayout? layout;
        lock (gate)
            layouts.TryGetValue(accountId, out layout);

        if (layout != null && layout.OrderedFullNames.Count > 0)
        {
            Dictionary<string, int> rankByName = new(StringComparer.Ordinal);
            for (int i = 0; i < layout.OrderedFullNames.Count; i++)
                rankByName[layout.OrderedFullNames[i]] = i + 1;

            foreach (var folder in folders)
            {
                folder.SortOrder = rankByName.TryGetValue(folder.FullName, out int rank) ? rank : 0;
                folder.IsExpanded = !layout.CollapsedFullNames.Contains(folder.FullName);
            }
            return;
        }

        // Guard against legacy partial SortOrder where local folders (in UserData) retained SortOrder > 0
        // after UserCache was cleared, which previously forced INBOX (SortOrder = 0) below Trash (this PC).
        bool hasUnorderedInbox = folders.Any(static f => f.Role == FolderRole.Inbox && f.SortOrder == 0);
        bool hasOrderedLocal = folders.Any(static f => f.IsLocal && f.SortOrder > 0);
        if (hasUnorderedInbox && hasOrderedLocal)
        {
            foreach (var folder in folders)
                folder.SortOrder = 0;
        }

        if (layout != null && layout.CollapsedFullNames.Count > 0)
        {
            foreach (var folder in folders)
                folder.IsExpanded = !layout.CollapsedFullNames.Contains(folder.FullName);
        }
    }

    /// <summary>Applies saved order and collapse state to a single folder before writing it.</summary>
    public static void ApplyTo(MailFolderData folder)
    {
        if (folder == null || string.IsNullOrWhiteSpace(folder.AccountId) || string.IsNullOrWhiteSpace(folder.FullName))
            return;

        lock (gate)
        {
            if (!layouts.TryGetValue(folder.AccountId, out var layout)) return;
            if (layout.OrderedFullNames.Count > 0)
            {
                int idx = layout.OrderedFullNames.FindIndex(n => string.Equals(n, folder.FullName, StringComparison.Ordinal));
                if (idx >= 0) folder.SortOrder = idx + 1;
            }
            if (layout.CollapsedFullNames.Contains(folder.FullName))
                folder.IsExpanded = false;
        }
    }

    static ConcurrentDictionary<string, AccountFolderLayout> Load()
    {
        try
        {
            string path = LayoutPath;
            if (!File.Exists(path)) return new(StringComparer.Ordinal);
            var data = JsonSerializer.Deserialize<Dictionary<string, AccountFolderLayout>>(File.ReadAllText(path), JsonDefaults.Indented);
            if (data == null) return new(StringComparer.Ordinal);
            var dict = new ConcurrentDictionary<string, AccountFolderLayout>(StringComparer.Ordinal);
            foreach (var (key, value) in data)
            {
                if (value == null) continue;
                value.OrderedFullNames ??= [];
                value.CollapsedFullNames = new HashSet<string>(value.CollapsedFullNames ?? [], StringComparer.Ordinal);
                dict[key] = value;
            }
            return dict;
        }
        catch (Exception ex)
        {
            Log($"Could not read folder layout: {ex.Message}", LogLevel.Warning, Run: IsLogEnabled);
            return new(StringComparer.Ordinal);
        }
    }

    static void PersistLocked()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.UserData);
            AtomicFile.WriteAllText(LayoutPath, JsonSerializer.Serialize(layouts, JsonDefaults.Indented));
        }
        catch (Exception ex)
        {
            Log($"Could not write folder layout: {ex.Message}", LogLevel.Warning, Run: IsLogEnabled);
        }
    }
}
