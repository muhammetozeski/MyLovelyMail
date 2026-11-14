using System.Text.Json;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;

namespace MyLovelyMail.MainProject.Stores;

/// <summary>
/// Manages the dedicated Trash bin for deleted folders (<c>UserData/DeletedFolders</c>).
/// Exists on disk and in the UI ONLY while at least one deleted folder is present; as soon as
/// the last deleted folder is restored or permanently purged, the Folder Trash bin deletes itself.
/// </summary>
public static class DeletedFolderStore
{
    public const string FolderName = "DeletedFolders";
    public const string ManifestFileName = "manifest.json";

    static readonly Lock SyncLock = new();
    static readonly List<DeletedFolderEntry> entries = [];

    public static event Action? OnChanged;

    public static string RootPath => Path.Combine(AppPaths.UserData, FolderName);
    static string ManifestPath => Path.Combine(RootPath, ManifestFileName);

    public static string EntryDirectory(string entryId) => Path.Combine(RootPath, entryId);

    public static bool HasAny
    {
        get
        {
            lock (SyncLock)
                return entries.Count > 0;
        }
    }

    public static int TotalCount
    {
        get
        {
            lock (SyncLock)
                return entries.Count;
        }
    }

    public static IReadOnlyList<DeletedFolderEntry> GetAll()
    {
        lock (SyncLock)
            return [.. entries.OrderByDescending(static e => e.DeletedAtUtc).ThenBy(static e => e.DisplayName, StringComparer.OrdinalIgnoreCase)];
    }

    public static IReadOnlyList<DeletedFolderEntry> GetAccountGroups()
    {
        lock (SyncLock)
            return [.. entries
                .Where(static e => e.Kind == DeletedFolderKind.AccountGroup)
                .OrderByDescending(static e => e.DeletedAtUtc)
                .ThenBy(static e => e.DisplayName, StringComparer.OrdinalIgnoreCase)];
    }

    public static IReadOnlyList<DeletedFolderEntry> GetForAccount(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return [];
        lock (SyncLock)
            return [.. entries
                .Where(e => e.Kind == DeletedFolderKind.MailFolder && string.Equals(e.AccountId, accountId, StringComparison.Ordinal))
                .OrderByDescending(static e => e.DeletedAtUtc)
                .ThenBy(static e => e.DisplayName, StringComparer.OrdinalIgnoreCase)];
    }

    public static bool HasForAccount(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return false;
        lock (SyncLock)
            return entries.Any(e => e.Kind == DeletedFolderKind.MailFolder && string.Equals(e.AccountId, accountId, StringComparison.Ordinal));
    }

    public static bool HasAccountGroups
    {
        get
        {
            lock (SyncLock)
                return entries.Any(static e => e.Kind == DeletedFolderKind.AccountGroup);
        }
    }

    public static DeletedFolderEntry? GetById(string entryId)
    {
        if (string.IsNullOrWhiteSpace(entryId)) return null;
        lock (SyncLock)
            return entries.FirstOrDefault(e => string.Equals(e.Id, entryId, StringComparison.OrdinalIgnoreCase));
    }

    public static void Load()
    {
        lock (SyncLock)
        {
            entries.Clear();
            string manifest = ManifestPath;
            if (File.Exists(manifest))
            {
                try
                {
                    var loaded = JsonSerializer.Deserialize<List<DeletedFolderEntry>>(File.ReadAllText(manifest), JsonDefaults.Indented);
                    if (loaded != null)
                    {
                        foreach (var item in loaded)
                        {
                            if (item != null && !string.IsNullOrWhiteSpace(item.Id))
                                entries.Add(item);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"Corrupt deleted-folders manifest '{manifest}': {ex.Message}", LogLevel.Warning);
                }
            }

            if (entries.Count == 0)
                RemoveTrashContainerIfEmptyNoLock();
        }
    }

    public static void Add(DeletedFolderEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (SyncLock)
        {
            entries.RemoveAll(e => string.Equals(e.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
            entries.Add(entry);
            SaveOrRemoveContainerNoLock();
        }
        OnChanged?.Invoke();
    }

    public static bool RemovePermanently(string entryId)
    {
        bool removed;
        lock (SyncLock)
        {
            removed = entries.RemoveAll(e => string.Equals(e.Id, entryId, StringComparison.OrdinalIgnoreCase)) > 0;
            DeleteEntryDirectoryNoLock(entryId);
            SaveOrRemoveContainerNoLock();
        }
        if (removed)
            OnChanged?.Invoke();
        return removed;
    }

    public static int RemoveManyPermanently(IEnumerable<string> entryIds)
    {
        int count = 0;
        lock (SyncLock)
        {
            foreach (string id in entryIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (entries.RemoveAll(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)) > 0)
                    count++;
                DeleteEntryDirectoryNoLock(id);
            }
            SaveOrRemoveContainerNoLock();
        }
        if (count > 0)
            OnChanged?.Invoke();
        return count;
    }

    public static int RemoveAllForAccount(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return 0;
        List<string> ids;
        lock (SyncLock)
        {
            ids = [.. entries
                .Where(e => e.Kind == DeletedFolderKind.MailFolder && string.Equals(e.AccountId, accountId, StringComparison.Ordinal))
                .Select(static e => e.Id)];
        }
        return ids.Count == 0 ? 0 : RemoveManyPermanently(ids);
    }

    public static int EmptyAll(string? accountIdFilter = null, DeletedFolderKind? kindFilter = null)
    {
        List<string> ids;
        lock (SyncLock)
        {
            ids = [.. entries
                .Where(e => (accountIdFilter == null || string.Equals(e.AccountId, accountIdFilter, StringComparison.Ordinal))
                         && (kindFilter == null || e.Kind == kindFilter.Value))
                .Select(static e => e.Id)];
        }
        return ids.Count == 0 ? 0 : RemoveManyPermanently(ids);
    }

    static void DeleteEntryDirectoryNoLock(string entryId)
    {
        if (string.IsNullOrWhiteSpace(entryId)) return;
        try
        {
            string dir = EntryDirectory(entryId);
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            Log($"Could not delete archived folder entry '{entryId}': {ex.Message}", LogLevel.Warning);
        }
    }

    static void SaveOrRemoveContainerNoLock()
    {
        if (entries.Count == 0)
        {
            RemoveTrashContainerIfEmptyNoLock();
            return;
        }

        try
        {
            Directory.CreateDirectory(RootPath);
            AtomicFile.WriteAllText(ManifestPath, JsonSerializer.Serialize(entries, JsonDefaults.Indented));
        }
        catch (Exception ex)
        {
            Log($"Could not save deleted-folders manifest: {ex.Message}", LogLevel.Error);
        }
    }

    static void RemoveTrashContainerIfEmptyNoLock()
    {
        try
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
                Log("Deleted Folders Trash bin is now empty and was removed.");
            }
        }
        catch (Exception ex)
        {
            Log($"Could not remove empty DeletedFolders directory: {ex.Message}", LogLevel.Warning);
        }
    }
}
