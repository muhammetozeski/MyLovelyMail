using System.Text.Json;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;

namespace MyLovelyMail.MainProject.Stores;

/// <summary>
/// Persists the account folder tree in <c>UserData/account-folders.json</c>, including nested
/// parent relationships, sort order, and expanded/collapsed state across restarts.
/// </summary>
public static class AccountFolderStore
{
    public const string FileName = "account-folders.json";

    /// <summary>The permanent default folder GUID (all zeros). Identical to other folders except it cannot be deleted.</summary>
    public const string DefaultFolderId = "00000000-0000-0000-0000-000000000000";

    public const string DefaultFolderDefaultName = "Main";

    static readonly List<AccountFolderData> folders = [];

    public static event Action? OnFoldersChanged;

    public static IReadOnlyList<AccountFolderData> Folders => folders;

    static string FilePath => Path.Combine(AppPaths.UserData, FileName);

    public static bool IsDefaultFolder(string? folderId)
    {
        if (string.IsNullOrWhiteSpace(folderId)) return false;
        if (string.Equals(folderId.Trim(), DefaultFolderId, StringComparison.OrdinalIgnoreCase)) return true;
        return Guid.TryParse(folderId.Trim(), out Guid parsed) && parsed == Guid.Empty;
    }

    /// <summary>
    /// Normalizes any folder reference so no account ever sits outside a valid folder: empty, null,
    /// all-zero GUID variants, or deleted folder IDs all resolve to <see cref="DefaultFolderId"/>.
    /// </summary>
    public static string NormalizeFolderId(string? folderId)
    {
        if (string.IsNullOrWhiteSpace(folderId) || IsDefaultFolder(folderId))
            return DefaultFolderId;

        string trimmed = folderId.Trim();
        return folders.Any(f => string.Equals(f.Id, trimmed, StringComparison.OrdinalIgnoreCase))
            ? trimmed
            : DefaultFolderId;
    }

    public static void Load()
    {
        folders.Clear();
        string path = FilePath;
        if (File.Exists(path))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<List<AccountFolderData>>(File.ReadAllText(path), JsonDefaults.Indented);
                if (loaded != null)
                {
                    foreach (var folder in loaded)
                    {
                        if (folder == null) continue;
                        if (IsDefaultFolder(folder.Id))
                            folder.Id = DefaultFolderId;
                        folders.Add(folder);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Corrupt account-folders.json '{path}': {ex.Message}", LogLevel.Error);
            }
        }

        EnsureDefaultFolderAndSanitize();
    }

    static void EnsureDefaultFolderAndSanitize()
    {
        bool changed = false;
        var defaultFolder = folders.FirstOrDefault(f => IsDefaultFolder(f.Id));
        if (defaultFolder == null)
        {
            defaultFolder = new AccountFolderData
            {
                Id = DefaultFolderId,
                Name = DefaultFolderDefaultName,
                ParentFolderId = null,
                IsExpanded = true,
                SortOrder = 0
            };
            folders.Insert(0, defaultFolder);
            changed = true;
        }
        else if (defaultFolder.Id != DefaultFolderId)
        {
            defaultFolder.Id = DefaultFolderId;
            changed = true;
        }

        foreach (var folder in folders)
        {
            if (!string.IsNullOrWhiteSpace(folder.ParentFolderId))
            {
                if (IsDefaultFolder(folder.ParentFolderId))
                {
                    folder.ParentFolderId = DefaultFolderId;
                }
                else if (!folders.Any(f => string.Equals(f.Id, folder.ParentFolderId, StringComparison.OrdinalIgnoreCase)) ||
                         CreatesCycle(folder.Id, folder.ParentFolderId))
                {
                    folder.ParentFolderId = null;
                    changed = true;
                }
            }
            else if (folder.ParentFolderId != null)
            {
                folder.ParentFolderId = null;
            }
        }

        folders.Sort(CompareFolders);
        if (changed) SaveToDisk();
    }

    static int CompareFolders(AccountFolderData a, AccountFolderData b)
    {
        if (a.SortOrder != b.SortOrder)
            return a.SortOrder.CompareTo(b.SortOrder);
        if (IsDefaultFolder(a.Id) != IsDefaultFolder(b.Id))
            return IsDefaultFolder(a.Id) ? -1 : 1;
        return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }

    static void SaveToDisk()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.UserData);
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(folders, JsonDefaults.Indented));
        }
        catch (Exception ex)
        {
            Log($"Could not write account-folders.json: {ex.Message}", LogLevel.Error);
        }
    }

    public static AccountFolderData? GetById(string? folderId)
    {
        string normalized = NormalizeFolderId(folderId);
        return folders.FirstOrDefault(f => string.Equals(f.Id, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<AccountFolderData> GetRootFolders() =>
        folders
            .Where(f => string.IsNullOrEmpty(f.ParentFolderId))
            .OrderBy(f => f.SortOrder)
            .ThenBy(f => IsDefaultFolder(f.Id) ? 0 : 1)
            .ToList();

    public static IReadOnlyList<AccountFolderData> GetChildFolders(string parentFolderId) =>
        folders
            .Where(f => string.Equals(f.ParentFolderId, parentFolderId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.SortOrder)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Returns all folders in hierarchical pre-order with their nesting depth for dropdown selectors.</summary>
    public static IReadOnlyList<(AccountFolderData Folder, int Depth)> GetFlattenedTree()
    {
        var result = new List<(AccountFolderData, int)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(AccountFolderData folder, int depth)
        {
            if (!visited.Add(folder.Id)) return;
            result.Add((folder, depth));
            foreach (var child in GetChildFolders(folder.Id))
                Visit(child, depth + 1);
        }

        foreach (var root in GetRootFolders())
            Visit(root, 0);

        foreach (var remaining in folders)
        {
            if (!visited.Contains(remaining.Id))
                Visit(remaining, 0);
        }

        return result;
    }

    public static AccountFolderData CreateFolder(string name, string? parentFolderId = null)
    {
        if (folders.Count == 0) EnsureDefaultFolderAndSanitize();

        string trimmedName = string.IsNullOrWhiteSpace(name) ? "New Folder" : name.Trim();
        string? normalizedParent = string.IsNullOrWhiteSpace(parentFolderId) ? null : NormalizeFolderId(parentFolderId);

        int maxSort = folders
            .Where(f => string.Equals(f.ParentFolderId ?? string.Empty, normalizedParent ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.SortOrder)
            .DefaultIfEmpty(0)
            .Max();

        var created = new AccountFolderData
        {
            Id = Guid.NewGuid().ToString("D"),
            Name = trimmedName,
            ParentFolderId = normalizedParent,
            IsExpanded = true,
            SortOrder = maxSort + 1
        };

        folders.Add(created);
        folders.Sort(CompareFolders);
        SaveToDisk();
        OnFoldersChanged?.Invoke();
        return created;
    }

    public static void RenameFolder(string folderId, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return;
        var folder = folders.FirstOrDefault(f => string.Equals(f.Id, folderId, StringComparison.OrdinalIgnoreCase));
        if (folder == null) return;

        folder.Name = newName.Trim();
        SaveToDisk();
        OnFoldersChanged?.Invoke();
    }

    public static void ToggleExpanded(string folderId)
    {
        var folder = GetById(folderId);
        if (folder == null) return;
        SetExpanded(folder.Id, !folder.IsExpanded);
    }

    public static void SetExpanded(string folderId, bool isExpanded)
    {
        var folder = GetById(folderId);
        if (folder == null || folder.IsExpanded == isExpanded) return;

        folder.IsExpanded = isExpanded;
        SaveToDisk();
        OnFoldersChanged?.Invoke();
    }

    public static bool DeleteFolder(string folderId)
    {
        if (IsDefaultFolder(folderId)) return false;

        var target = folders.FirstOrDefault(f => string.Equals(f.Id, folderId, StringComparison.OrdinalIgnoreCase));
        if (target == null) return false;

        string fallbackFolderId = string.IsNullOrWhiteSpace(target.ParentFolderId)
            ? DefaultFolderId
            : NormalizeFolderId(target.ParentFolderId);

        // Re-parent child folders so nothing becomes orphaned.
        foreach (var child in folders.Where(f => string.Equals(f.ParentFolderId, target.Id, StringComparison.OrdinalIgnoreCase)))
        {
            child.ParentFolderId = target.ParentFolderId;
        }

        folders.Remove(target);
        SaveToDisk();

        // Move accounts in this folder to the fallback folder (default or parent).
        AccountStore.ReassignFolder(target.Id, fallbackFolderId);
        OnFoldersChanged?.Invoke();
        return true;
    }

    public static bool CreatesCycle(string folderId, string? candidateParentId)
    {
        if (string.IsNullOrWhiteSpace(candidateParentId)) return false;
        string? current = candidateParentId;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { folderId };
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (!seen.Add(current)) return true;
            var parent = folders.FirstOrDefault(f => string.Equals(f.Id, current, StringComparison.OrdinalIgnoreCase));
            current = parent?.ParentFolderId;
        }
        return false;
    }

    /// <summary>
    /// Moves <paramref name="draggedFolderId"/> either inside <paramref name="targetParentFolderId"/>
    /// or before <paramref name="insertBeforeFolderId"/> among its siblings.
    /// </summary>
    public static void MoveFolder(string draggedFolderId, string? targetParentFolderId, string? insertBeforeFolderId = null)
    {
        var dragged = folders.FirstOrDefault(f => string.Equals(f.Id, draggedFolderId, StringComparison.OrdinalIgnoreCase));
        if (dragged == null) return;

        string? newParent = string.IsNullOrWhiteSpace(targetParentFolderId) ? null : NormalizeFolderId(targetParentFolderId);
        if (string.Equals(dragged.Id, newParent, StringComparison.OrdinalIgnoreCase) || CreatesCycle(dragged.Id, newParent))
            return;

        dragged.ParentFolderId = newParent;

        var siblings = folders
            .Where(f => string.Equals(f.ParentFolderId ?? string.Empty, newParent ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(f.Id, dragged.Id, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.SortOrder)
            .ToList();

        int insertIndex = siblings.Count;
        if (!string.IsNullOrWhiteSpace(insertBeforeFolderId))
        {
            int found = siblings.FindIndex(f => string.Equals(f.Id, insertBeforeFolderId, StringComparison.OrdinalIgnoreCase));
            if (found >= 0) insertIndex = found;
        }

        siblings.Insert(insertIndex, dragged);
        for (int i = 0; i < siblings.Count; i++)
            siblings[i].SortOrder = i + 1;

        folders.Sort(CompareFolders);
        SaveToDisk();
        OnFoldersChanged?.Invoke();
    }
}
