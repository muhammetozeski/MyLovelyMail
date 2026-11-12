using System.Text.Json;
using MyLovelyMail.MainProject.Storage;

namespace MyLovelyMail.MainProject.Stores;

/// <summary>
/// Canonical identifiers for every customizable toolbar, button bar, hover bar, and context menu surface.
/// </summary>
public static class UiSurfaceIds
{
    // Screen button groups & toolbars
    public const string SidebarTop = "SidebarTop";
    public const string SidebarFolderHover = "SidebarFolderHover";
    public const string SidebarAccountFolderHover = "SidebarAccountFolderHover";
    public const string ListToolbar = "ListToolbar";
    public const string QuickFiltersBar = "QuickFiltersBar";
    public const string BulkToolbar = "BulkToolbar";
    public const string MailRowHover = "MailRowHover";
    public const string SenderRowHover = "SenderRowHover";
    public const string ReaderHeaderNav = "ReaderHeaderNav";
    public const string ReaderToolbar = "ReaderToolbar";
    public const string PersonActionsBar = "PersonActionsBar";
    public const string ComposeQuoteBar = "ComposeQuoteBar";
    public const string ComposeFooterBar = "ComposeFooterBar";

    // Right-click context menus
    public const string MessageContextMenu = "MessageContextMenu";
    public const string SenderContextMenu = "SenderContextMenu";
    public const string FolderContextMenu = "FolderContextMenu";
    public const string AccountContextMenu = "AccountContextMenu";
    public const string AccountFolderContextMenu = "AccountFolderContextMenu";
    public const string WorkspaceContextMenu = "WorkspaceContextMenu";

    public static bool IsContextMenu(string surfaceId) => surfaceId is
        MessageContextMenu or
        SenderContextMenu or
        FolderContextMenu or
        AccountContextMenu or
        AccountFolderContextMenu or
        WorkspaceContextMenu;

    public static string PairedContextMenuOf(string toolbarSurfaceId) => toolbarSurfaceId switch
    {
        SidebarTop => AccountContextMenu,
        SidebarFolderHover => FolderContextMenu,
        SidebarAccountFolderHover => AccountFolderContextMenu,
        ListToolbar => WorkspaceContextMenu,
        QuickFiltersBar => WorkspaceContextMenu,
        BulkToolbar => MessageContextMenu,
        MailRowHover => MessageContextMenu,
        SenderRowHover => SenderContextMenu,
        ReaderHeaderNav => MessageContextMenu,
        ReaderToolbar => MessageContextMenu,
        PersonActionsBar => SenderContextMenu,
        ComposeQuoteBar => WorkspaceContextMenu,
        ComposeFooterBar => WorkspaceContextMenu,
        _ => WorkspaceContextMenu
    };

    public static string DefaultPairedToolbarOf(string menuSurfaceId) => menuSurfaceId switch
    {
        MessageContextMenu => ReaderToolbar,
        SenderContextMenu => SenderRowHover,
        FolderContextMenu => SidebarFolderHover,
        AccountContextMenu => SidebarTop,
        AccountFolderContextMenu => SidebarAccountFolderHover,
        WorkspaceContextMenu => ListToolbar,
        _ => ListToolbar
    };

    public static string DisplayNameOf(string surfaceId) => surfaceId switch
    {
        SidebarTop => "Sidebar Top Actions",
        SidebarFolderHover => "Folder Row Quick Actions",
        SidebarAccountFolderHover => "Account Group Quick Actions",
        ListToolbar => "Mail List Top Toolbar",
        QuickFiltersBar => "Search & Filter Pills",
        BulkToolbar => "Multi-Select Bulk Bar",
        MailRowHover => "Mail Row Hover Actions",
        SenderRowHover => "Sender Row Hover Actions",
        ReaderHeaderNav => "Reader Thread Navigation",
        ReaderToolbar => "Reader Message Toolbar",
        PersonActionsBar => "Person Sheet Actions",
        ComposeQuoteBar => "Compose Quote Bar",
        ComposeFooterBar => "Compose Footer Actions",
        MessageContextMenu => "Mail Right-Click Menu",
        SenderContextMenu => "Sender Right-Click Menu",
        FolderContextMenu => "Folder Right-Click Menu",
        AccountContextMenu => "Account Right-Click Menu",
        AccountFolderContextMenu => "Account Group Right-Click Menu",
        WorkspaceContextMenu => "Workspace Right-Click Menu",
        _ => surfaceId
    };

    public static readonly string[] AllSurfaces =
    [
        ListToolbar,
        QuickFiltersBar,
        ReaderToolbar,
        ReaderHeaderNav,
        BulkToolbar,
        MailRowHover,
        SenderRowHover,
        SidebarTop,
        SidebarFolderHover,
        SidebarAccountFolderHover,
        PersonActionsBar,
        ComposeQuoteBar,
        ComposeFooterBar,
        MessageContextMenu,
        SenderContextMenu,
        FolderContextMenu,
        AccountContextMenu,
        AccountFolderContextMenu,
        WorkspaceContextMenu
    ];
}

public sealed class SurfaceLayoutData
{
    public List<string> OrderedIds { get; set; } = [];
    public List<string> HiddenIds { get; set; } = [];
    public List<string> AddedIds { get; set; } = [];
    public Dictionary<string, SubmenuLayoutData> Submenus { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class SubmenuLayoutData
{
    public List<string> OrderedIds { get; set; } = [];
    public List<string> HiddenIds { get; set; } = [];
    public List<string> AddedIds { get; set; } = [];
}

public sealed class UiLayoutDocument
{
    public int Version { get; set; } = 1;
    public Dictionary<string, SurfaceLayoutData> Surfaces { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Persists user layout customizations (ordering, visibility, cross-surface toolbar/context-menu transfers,
/// and promoted 1-click actions) in <c>UserData/ui-layout.json</c>.
/// </summary>
public static class UiCustomizationStore
{
    public const string FileName = "ui-layout.json";

    static readonly Lock gate = new();
    static UiLayoutDocument document = new();

    public static event Action? OnChanged;

    static string FilePath => Path.Combine(AppPaths.UserData, FileName);

    public static void Load()
    {
        lock (gate)
        {
            document = new UiLayoutDocument();
            if (File.Exists(FilePath))
            {
                try
                {
                    string raw = File.ReadAllText(FilePath);
                    var parsed = JsonSerializer.Deserialize<UiLayoutDocument>(raw, JsonDefaults.Indented);
                    if (parsed != null)
                    {
                        document = NormalizeDocument(parsed);
                    }
                }
                catch (Exception ex)
                {
                    Log($"Could not read {FileName}; starting with default UI layout: {ex.Message}", LogLevel.Warning);
                }
            }

            MigrateLegacyPinnedReaderActionsLocked();
        }
        OnChanged?.Invoke();
    }

    static UiLayoutDocument NormalizeDocument(UiLayoutDocument source)
    {
        var clean = new UiLayoutDocument { Version = source.Version };
        foreach (var (key, val) in source.Surfaces)
        {
            if (string.IsNullOrWhiteSpace(key) || val == null) continue;
            var surface = new SurfaceLayoutData
            {
                OrderedIds = DistinctClean(val.OrderedIds),
                HiddenIds = DistinctClean(val.HiddenIds),
                AddedIds = DistinctClean(val.AddedIds)
            };
            if (val.Submenus != null)
            {
                foreach (var (subKey, subVal) in val.Submenus)
                {
                    if (string.IsNullOrWhiteSpace(subKey) || subVal == null) continue;
                    surface.Submenus[subKey] = new SubmenuLayoutData
                    {
                        OrderedIds = DistinctClean(subVal.OrderedIds),
                        HiddenIds = DistinctClean(subVal.HiddenIds),
                        AddedIds = DistinctClean(subVal.AddedIds)
                    };
                }
            }
            clean.Surfaces[key] = surface;
        }
        return clean;
    }

    static List<string> DistinctClean(IEnumerable<string>? items) =>
        items == null
            ? []
            : [.. items.Where(static s => !string.IsNullOrWhiteSpace(s)).Select(static s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)];

    static void MigrateLegacyPinnedReaderActionsLocked()
    {
        string raw = Settings.PinnedReaderActions.Value ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return;

        var legacyTokens = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (legacyTokens.Length == 0) return;

        var readerSurface = GetOrCreateSurfaceLocked(UiSurfaceIds.ReaderToolbar);
        bool changed = false;
        foreach (string token in legacyTokens)
        {
            string? mapped = MapLegacyReaderActionId(token);
            if (mapped != null && !ContainsIgnoreCase(readerSurface.AddedIds, mapped))
            {
                readerSurface.AddedIds.Add(mapped);
                changed = true;
            }
        }

        Settings.PinnedReaderActions.Set(string.Empty);
        SettingsManager.SaveSettings();
        if (changed) SaveLocked();
    }

    public static string? MapLegacyReaderActionId(string legacyId) => legacyId.Trim().ToLowerInvariant() switch
    {
        "toggle-read" or "msg.toggle-read" => "Msg.ToggleRead",
        "reply" or "msg.reply" => "Msg.Reply",
        "reply-all" or "msg.reply-all" => "Msg.ReplyAll",
        "forward" or "msg.forward" => "Msg.Forward",
        "forward-attachment" or "msg.forward-eml" => "Msg.ForwardAttachment",
        "markers" or "msg.markers" => "Msg.MarkersModal",
        "star" or "msg.star" => "Msg.ToggleStar",
        "important" or "msg.important" => "Msg.ToggleImportant",
        "mute" or "msg.mute" => "Msg.ToggleMute",
        "archive" or "msg.archive" => "Msg.Archive",
        "move" or "msg.move-modal" => "Msg.MoveModal",
        "snooze" or "msg.snooze-modal" => "Msg.SnoozeModal",
        "wake" or "msg.wake" => "Msg.Wake",
        "trash" or "msg.trash" => "Msg.Trash",
        "spam" or "msg.spam" => "Msg.Spam",
        "restore" or "msg.restore" => "Msg.Restore",
        "save" or "msg.save" => "Msg.SaveModal",
        "save-eml" or "msg.save-eml" => "Msg.SaveEml",
        "save-html" or "msg.save-html" => "Msg.SaveHtml",
        "msg.download-attachments" => "Msg.DownloadAttachments",
        "source" or "msg.source" => "Msg.ViewSource",
        "msg.copy-from" => "Msg.CopySender",
        "msg.copy-subject" => "Msg.CopySubject",
        "msg.copy-link" => "Msg.CopyLink",
        "find-sender" or "msg.filter-sender" => "Msg.FilterSender",
        "person-details" or "msg.person" => "Msg.OpenPerson",
        "msg.delete-permanent" => "Msg.DeletePermanent",
        _ => null
    };

    static SurfaceLayoutData GetOrCreateSurfaceLocked(string surfaceId)
    {
        if (!document.Surfaces.TryGetValue(surfaceId, out var data))
        {
            data = new SurfaceLayoutData();
            document.Surfaces[surfaceId] = data;
        }
        return data;
    }

    static SubmenuLayoutData GetOrCreateSubmenuLocked(string surfaceId, string submenuParentId)
    {
        var surface = GetOrCreateSurfaceLocked(surfaceId);
        if (!surface.Submenus.TryGetValue(submenuParentId, out var sub))
        {
            sub = new SubmenuLayoutData();
            surface.Submenus[submenuParentId] = sub;
        }
        return sub;
    }

    static void SaveLocked()
    {
        try
        {
            AppPaths.EnsureCreated();
            string json = JsonSerializer.Serialize(document, JsonDefaults.Indented);
            AtomicFile.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            Log($"Failed to save {FileName}: {ex.Message}", LogLevel.Error);
        }
    }

    public static bool HasAnyCustomization
    {
        get
        {
            lock (gate)
            {
                return document.Surfaces.Values.Any(static s =>
                    s.OrderedIds.Count > 0 ||
                    s.HiddenIds.Count > 0 ||
                    s.AddedIds.Count > 0 ||
                    s.Submenus.Values.Any(static sub => sub.OrderedIds.Count > 0 || sub.HiddenIds.Count > 0 || sub.AddedIds.Count > 0));
            }
        }
    }

    public static bool IsSurfaceCustomized(string surfaceId)
    {
        lock (gate)
        {
            if (!document.Surfaces.TryGetValue(surfaceId, out var s)) return false;
            return s.OrderedIds.Count > 0 || s.HiddenIds.Count > 0 || s.AddedIds.Count > 0 ||
                   s.Submenus.Values.Any(static sub => sub.OrderedIds.Count > 0 || sub.HiddenIds.Count > 0 || sub.AddedIds.Count > 0);
        }
    }

    public static SurfaceLayoutData GetSnapshot(string surfaceId)
    {
        lock (gate)
        {
            if (!document.Surfaces.TryGetValue(surfaceId, out var s))
                return new SurfaceLayoutData();

            var copy = new SurfaceLayoutData
            {
                OrderedIds = [.. s.OrderedIds],
                HiddenIds = [.. s.HiddenIds],
                AddedIds = [.. s.AddedIds]
            };
            foreach (var (k, v) in s.Submenus)
            {
                copy.Submenus[k] = new SubmenuLayoutData
                {
                    OrderedIds = [.. v.OrderedIds],
                    HiddenIds = [.. v.HiddenIds],
                    AddedIds = [.. v.AddedIds]
                };
            }
            return copy;
        }
    }

    public static List<T> ApplyLayout<T>(
        string surfaceId,
        IReadOnlyList<T> defaultItems,
        Func<T, string> getId,
        Func<T, bool> isFixed,
        Func<string, T?>? resolveAdded = null,
        string? submenuParentId = null,
        bool includeHidden = false)
    {
        List<string> orderedIds;
        List<string> hiddenIds;
        List<string> addedIds;

        lock (gate)
        {
            if (!document.Surfaces.TryGetValue(surfaceId, out var s))
            {
                orderedIds = [];
                hiddenIds = [];
                addedIds = [];
            }
            else if (!string.IsNullOrWhiteSpace(submenuParentId))
            {
                if (s.Submenus.TryGetValue(submenuParentId, out var sub))
                {
                    orderedIds = [.. sub.OrderedIds];
                    hiddenIds = [.. sub.HiddenIds];
                    addedIds = [.. sub.AddedIds];
                }
                else
                {
                    orderedIds = [];
                    hiddenIds = [];
                    addedIds = [];
                }
            }
            else
            {
                orderedIds = [.. s.OrderedIds];
                hiddenIds = [.. s.HiddenIds];
                addedIds = [.. s.AddedIds];
            }
        }

        var map = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        var naturalOrder = new List<string>();

        foreach (var item in defaultItems)
        {
            string id = getId(item);
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (map.TryAdd(id, item))
                naturalOrder.Add(id);
        }

        if (resolveAdded != null)
        {
            foreach (string addedId in addedIds)
            {
                if (map.ContainsKey(addedId)) continue;
                var resolved = resolveAdded(addedId);
                if (resolved != null)
                {
                    map[addedId] = resolved;
                    naturalOrder.Add(addedId);
                }
            }
        }

        var hiddenSet = new HashSet<string>(hiddenIds, StringComparer.OrdinalIgnoreCase);
        var allowedIds = new List<string>();
        foreach (string id in naturalOrder)
        {
            var item = map[id];
            if (!includeHidden && hiddenSet.Contains(id) && !isFixed(item))
                continue;
            allowedIds.Add(id);
        }

        var allowedSet = new HashSet<string>(allowedIds, StringComparer.OrdinalIgnoreCase);
        var finalIds = new List<string>(allowedIds.Count);
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string id in orderedIds)
        {
            if (allowedSet.Contains(id) && placed.Add(id))
                finalIds.Add(id);
        }

        foreach (string id in allowedIds)
        {
            if (placed.Add(id))
                finalIds.Add(id);
        }

        var result = new List<T>(finalIds.Count);
        foreach (string id in finalIds)
        {
            if (map.TryGetValue(id, out var item))
                result.Add(item);
        }
        return result;
    }

    public static bool IsHidden(string surfaceId, string actionId, string? submenuParentId = null)
    {
        lock (gate)
        {
            if (!document.Surfaces.TryGetValue(surfaceId, out var s)) return false;
            if (!string.IsNullOrWhiteSpace(submenuParentId))
                return s.Submenus.TryGetValue(submenuParentId, out var sub) && ContainsIgnoreCase(sub.HiddenIds, actionId);
            return ContainsIgnoreCase(s.HiddenIds, actionId);
        }
    }

    public static bool IsAdded(string surfaceId, string actionId, string? submenuParentId = null)
    {
        lock (gate)
        {
            if (!document.Surfaces.TryGetValue(surfaceId, out var s)) return false;
            if (!string.IsNullOrWhiteSpace(submenuParentId))
                return s.Submenus.TryGetValue(submenuParentId, out var sub) && ContainsIgnoreCase(sub.AddedIds, actionId);
            return ContainsIgnoreCase(s.AddedIds, actionId);
        }
    }

    public static void Reorder(
        string surfaceId,
        IReadOnlyList<string> currentVisibleIds,
        string movedActionId,
        int targetIndex,
        string? submenuParentId = null)
    {
        if (string.IsNullOrWhiteSpace(surfaceId) || string.IsNullOrWhiteSpace(movedActionId)) return;

        lock (gate)
        {
            var list = currentVisibleIds
                .Where(static s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            int oldIndex = list.FindIndex(s => s.Equals(movedActionId, StringComparison.OrdinalIgnoreCase));
            if (oldIndex >= 0)
            {
                list.RemoveAt(oldIndex);
                if (targetIndex > oldIndex) targetIndex--;
            }

            int clamped = Math.Clamp(targetIndex, 0, list.Count);
            list.Insert(clamped, movedActionId);

            if (!string.IsNullOrWhiteSpace(submenuParentId))
            {
                var sub = GetOrCreateSubmenuLocked(surfaceId, submenuParentId);
                sub.OrderedIds = list;
            }
            else
            {
                var surface = GetOrCreateSurfaceLocked(surfaceId);
                surface.OrderedIds = list;
            }

            SaveLocked();
        }
        OnChanged?.Invoke();
    }

    public static void SetVisible(
        string surfaceId,
        string actionId,
        bool visible,
        IReadOnlyList<string>? currentVisibleIds = null,
        int? insertIndex = null,
        bool markAsAddedWhenShown = false,
        string? submenuParentId = null)
    {
        if (string.IsNullOrWhiteSpace(surfaceId) || string.IsNullOrWhiteSpace(actionId)) return;

        lock (gate)
        {
            List<string> ordered;
            List<string> hidden;
            List<string> added;

            if (!string.IsNullOrWhiteSpace(submenuParentId))
            {
                var sub = GetOrCreateSubmenuLocked(surfaceId, submenuParentId);
                ordered = sub.OrderedIds;
                hidden = sub.HiddenIds;
                added = sub.AddedIds;
            }
            else
            {
                var surface = GetOrCreateSurfaceLocked(surfaceId);
                ordered = surface.OrderedIds;
                hidden = surface.HiddenIds;
                added = surface.AddedIds;
            }

            if (visible)
            {
                RemoveIgnoreCase(hidden, actionId);
                if (markAsAddedWhenShown && !ContainsIgnoreCase(added, actionId))
                    added.Add(actionId);

                if (currentVisibleIds != null)
                {
                    var nextOrder = currentVisibleIds
                        .Where(s => !s.Equals(actionId, StringComparison.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    int idx = Math.Clamp(insertIndex ?? nextOrder.Count, 0, nextOrder.Count);
                    nextOrder.Insert(idx, actionId);
                    ordered.Clear();
                    ordered.AddRange(nextOrder);
                }
                else if (insertIndex.HasValue)
                {
                    RemoveIgnoreCase(ordered, actionId);
                    int idx = Math.Clamp(insertIndex.Value, 0, ordered.Count);
                    ordered.Insert(idx, actionId);
                }
            }
            else
            {
                RemoveIgnoreCase(added, actionId);
                if (!ContainsIgnoreCase(hidden, actionId))
                    hidden.Add(actionId);
            }

            SaveLocked();
        }
        OnChanged?.Invoke();
    }

    public static void TransferAction(
        string fromSurfaceId,
        string? fromSubmenuParentId,
        string toSurfaceId,
        string? toSubmenuParentId,
        string actionId,
        IReadOnlyList<string> targetVisibleIds,
        int targetIndex,
        bool hideFromSource)
    {
        if (string.IsNullOrWhiteSpace(actionId) || string.IsNullOrWhiteSpace(toSurfaceId)) return;

        bool sameBucket = fromSurfaceId.Equals(toSurfaceId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(fromSubmenuParentId ?? string.Empty, toSubmenuParentId ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        if (sameBucket)
        {
            Reorder(toSurfaceId, targetVisibleIds, actionId, targetIndex, toSubmenuParentId);
            return;
        }

        lock (gate)
        {
            if (!string.IsNullOrWhiteSpace(fromSurfaceId))
            {
                if (!string.IsNullOrWhiteSpace(fromSubmenuParentId))
                {
                    var fromSub = GetOrCreateSubmenuLocked(fromSurfaceId, fromSubmenuParentId);
                    if (hideFromSource)
                    {
                        RemoveIgnoreCase(fromSub.AddedIds, actionId);
                        if (!ContainsIgnoreCase(fromSub.HiddenIds, actionId))
                            fromSub.HiddenIds.Add(actionId);
                    }
                }
                else
                {
                    var fromSurface = GetOrCreateSurfaceLocked(fromSurfaceId);
                    bool wasAddedOnSource = RemoveIgnoreCase(fromSurface.AddedIds, actionId);
                    if (hideFromSource && !wasAddedOnSource)
                    {
                        if (!ContainsIgnoreCase(fromSurface.HiddenIds, actionId))
                            fromSurface.HiddenIds.Add(actionId);
                    }
                    else if (!hideFromSource && wasAddedOnSource)
                    {
                        // Keep it on source if hideFromSource is false
                        fromSurface.AddedIds.Add(actionId);
                    }
                }
            }

            List<string> toOrdered;
            List<string> toHidden;
            List<string> toAdded;

            if (!string.IsNullOrWhiteSpace(toSubmenuParentId))
            {
                var toSub = GetOrCreateSubmenuLocked(toSurfaceId, toSubmenuParentId);
                toOrdered = toSub.OrderedIds;
                toHidden = toSub.HiddenIds;
                toAdded = toSub.AddedIds;
            }
            else
            {
                var toSurface = GetOrCreateSurfaceLocked(toSurfaceId);
                toOrdered = toSurface.OrderedIds;
                toHidden = toSurface.HiddenIds;
                toAdded = toSurface.AddedIds;
            }

            RemoveIgnoreCase(toHidden, actionId);
            if (!ContainsIgnoreCase(toAdded, actionId))
                toAdded.Add(actionId);

            var nextOrder = targetVisibleIds
                .Where(s => !s.Equals(actionId, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            int clamped = Math.Clamp(targetIndex, 0, nextOrder.Count);
            nextOrder.Insert(clamped, actionId);

            toOrdered.Clear();
            toOrdered.AddRange(nextOrder);

            SaveLocked();
        }
        OnChanged?.Invoke();
    }

    public static void ResetSurface(string surfaceId)
    {
        lock (gate)
        {
            if (document.Surfaces.Remove(surfaceId))
                SaveLocked();
        }
        OnChanged?.Invoke();
    }

    public static void ResetAll()
    {
        lock (gate)
        {
            document.Surfaces.Clear();
            SaveLocked();
        }
        OnChanged?.Invoke();
    }

    static bool ContainsIgnoreCase(List<string> list, string value) =>
        list.Any(s => s.Equals(value, StringComparison.OrdinalIgnoreCase));

    static bool RemoveIgnoreCase(List<string> list, string value) =>
        list.RemoveAll(s => s.Equals(value, StringComparison.OrdinalIgnoreCase)) > 0;
}
