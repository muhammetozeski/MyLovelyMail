using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services;

public sealed record CatalogEntryDescriptor(
    string ActionId,
    string Label,
    string Category,
    bool IsFixed = false);

public sealed record SurfaceInspectionSnapshot(
    string SurfaceId,
    IReadOnlyList<CatalogEntryDescriptor> VisibleItems,
    IReadOnlyList<CatalogEntryDescriptor> HiddenDefaultItems,
    IReadOnlyList<CatalogEntryDescriptor> AvailablePromotableItems);

/// <summary>
/// Pure C# pointer drag-and-drop coordinator for reordering buttons and menu items,
/// transferring buttons into right-click menus, promoting submenu items to 1-click root menus,
/// and dragging context-menu items directly onto screen toolbars.
/// </summary>
public static class ActionDragService
{
    const double ActivationThresholdPx = 5.0;
    const long ClickSuppressionWindowMs = 350;

    static bool hasCandidate;
    static double startX;
    static double startY;
    static long lastDragEndedTick;

    public static bool IsDragging { get; private set; }
    public static string SourceSurfaceId { get; private set; } = string.Empty;
    public static string? SourceSubmenuParentId { get; private set; }
    public static string SourceActionId { get; private set; } = string.Empty;
    public static string SourceLabel { get; private set; } = string.Empty;
    public static bool SourceIsFixed { get; private set; }
    public static IReadOnlyList<string> SourceVisibleIds { get; private set; } = [];

    public static double PointerX { get; private set; }
    public static double PointerY { get; private set; }

    public static string HoverSurfaceId { get; private set; } = string.Empty;
    public static string? HoverSubmenuParentId { get; private set; }
    public static string HoverTargetActionId { get; private set; } = string.Empty;
    public static int HoverInsertIndex { get; private set; }
    public static bool HoverInsertAfter { get; private set; }
    public static bool HoverIsMenuDock { get; private set; }
    public static bool HoverHideZone { get; private set; }
    public static IReadOnlyList<string> HoverVisibleIds { get; private set; } = [];

    /// <summary>Fires when drag starts, hover target changes, or drag ends.</summary>
    public static event Action? OnStateChanged;

    /// <summary>Fires on pointer movement while dragging so only the floating ghost chip updates per frame.</summary>
    public static event Action? OnGhostMoved;

    /// <summary>Optional live surface inspector registered by the Mail workspace for <c>UiCustomizerModal</c>.</summary>
    public static Func<string, SurfaceInspectionSnapshot>? SurfaceInspector { get; set; }

    public static IReadOnlyList<string> GetEffectiveVisibleIds(string surfaceId)
    {
        if (SurfaceInspector != null)
        {
            var inspected = SurfaceInspector(surfaceId);
            if (inspected.VisibleItems.Count > 0)
                return [.. inspected.VisibleItems.Select(static x => x.ActionId)];
        }
        return UiCustomizationStore.GetSnapshot(surfaceId).OrderedIds;
    }

    public static bool SourceIsContextMenu => UiSurfaceIds.IsContextMenu(SourceSurfaceId);

    public static bool CanAcceptDrop(string targetSurfaceId, string? targetSubmenuParentId = null)
    {
        if (!IsDragging || string.IsNullOrWhiteSpace(targetSurfaceId)) return false;

        bool sameBucket = SourceSurfaceId.Equals(targetSurfaceId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(SourceSubmenuParentId ?? string.Empty, targetSubmenuParentId ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        if (sameBucket) return true;

        // Structural fixed buttons can reorder within their own bar, but cannot be moved away to another surface.
        if (SourceIsFixed && !SourceIsContextMenu) return false;

        // Submenu items inside a context menu can always be promoted onto the root of that same context menu
        // OR dragged out onto a compatible screen toolbar!
        if (SourceIsContextMenu && targetSurfaceId.Equals(SourceSurfaceId, StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(targetSubmenuParentId))
            return true;

        // Compact per-row hover bars only accept drops from their paired context menu or matching domain actions,
        // preventing accidental cross-domain drops while moving the cursor across sidebar/list rows.
        if (targetSurfaceId is UiSurfaceIds.SidebarFolderHover or UiSurfaceIds.SidebarAccountFolderHover or UiSurfaceIds.MailRowHover or UiSurfaceIds.SenderRowHover)
        {
            string pairedMenu = UiSurfaceIds.PairedContextMenuOf(targetSurfaceId);
            if (SourceSurfaceId.Equals(pairedMenu, StringComparison.OrdinalIgnoreCase))
                return true;

            return targetSurfaceId switch
            {
                UiSurfaceIds.SidebarFolderHover => SourceActionId.StartsWith("Folder.", StringComparison.OrdinalIgnoreCase),
                UiSurfaceIds.SidebarAccountFolderHover => SourceActionId.StartsWith("AccGroup.", StringComparison.OrdinalIgnoreCase),
                UiSurfaceIds.MailRowHover => SourceActionId.StartsWith("Msg.", StringComparison.OrdinalIgnoreCase),
                UiSurfaceIds.SenderRowHover => SourceActionId.StartsWith("Sender.", StringComparison.OrdinalIgnoreCase),
                _ => false
            };
        }

        // Any context menu item can be dragged onto any main toolbar, and any non-fixed toolbar item can be dragged into a context menu or another toolbar.
        return true;
    }

    public static void BeginCandidate(
        string surfaceId,
        string? submenuParentId,
        string actionId,
        string label,
        bool isFixed,
        IReadOnlyList<string> currentVisibleIds,
        double clientX,
        double clientY)
    {
        if (string.IsNullOrWhiteSpace(surfaceId) || string.IsNullOrWhiteSpace(actionId))
            return;

        hasCandidate = true;
        IsDragging = false;
        SourceSurfaceId = surfaceId;
        SourceSubmenuParentId = submenuParentId;
        SourceActionId = actionId;
        SourceLabel = label;
        SourceIsFixed = isFixed;
        SourceVisibleIds = currentVisibleIds;
        startX = clientX;
        startY = clientY;
        PointerX = clientX;
        PointerY = clientY;

        HoverSurfaceId = string.Empty;
        HoverSubmenuParentId = null;
        HoverTargetActionId = string.Empty;
        HoverInsertIndex = 0;
        HoverInsertAfter = false;
        HoverIsMenuDock = false;
        HoverHideZone = false;
        HoverVisibleIds = [];
    }

    public static void UpdatePointer(double clientX, double clientY)
    {
        if (!hasCandidate) return;

        PointerX = clientX;
        PointerY = clientY;

        if (!IsDragging)
        {
            double dx = clientX - startX;
            double dy = clientY - startY;
            if (Math.Sqrt(dx * dx + dy * dy) < ActivationThresholdPx)
                return;

            IsDragging = true;
            OnStateChanged?.Invoke();
            OnGhostMoved?.Invoke();
            return;
        }

        OnGhostMoved?.Invoke();
    }

    public static void HoverSlot(
        string targetSurfaceId,
        string? targetSubmenuParentId,
        string targetActionId,
        int slotIndex,
        IReadOnlyList<string> targetVisibleIds,
        bool insertAfter)
    {
        if (!hasCandidate) return;
        if (!IsDragging)
        {
            // Support fast drags where pointermove lands directly on a sibling slot
            UpdatePointer(PointerX, PointerY);
            if (!IsDragging) return;
        }
        if (!CanAcceptDrop(targetSurfaceId, targetSubmenuParentId)) return;

        bool sameItem = SourceSurfaceId.Equals(targetSurfaceId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(SourceSubmenuParentId ?? string.Empty, targetSubmenuParentId ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            && SourceActionId.Equals(targetActionId, StringComparison.OrdinalIgnoreCase);

        if (sameItem)
        {
            if (HoverSurfaceId == targetSurfaceId
                && HoverSubmenuParentId == targetSubmenuParentId
                && string.IsNullOrEmpty(HoverTargetActionId)
                && HoverInsertIndex == slotIndex
                && !HoverIsMenuDock
                && !HoverHideZone)
            {
                return;
            }
            HoverSurfaceId = targetSurfaceId;
            HoverSubmenuParentId = targetSubmenuParentId;
            HoverTargetActionId = string.Empty;
            HoverInsertIndex = slotIndex;
            HoverInsertAfter = false;
            HoverIsMenuDock = false;
            HoverHideZone = false;
            HoverVisibleIds = targetVisibleIds;
            OnStateChanged?.Invoke();
            return;
        }

        int computedIndex = insertAfter ? slotIndex + 1 : slotIndex;
        if (HoverSurfaceId == targetSurfaceId
            && HoverSubmenuParentId == targetSubmenuParentId
            && HoverTargetActionId == targetActionId
            && HoverInsertIndex == computedIndex
            && HoverInsertAfter == insertAfter
            && !HoverIsMenuDock
            && !HoverHideZone)
        {
            return;
        }

        HoverSurfaceId = targetSurfaceId;
        HoverSubmenuParentId = targetSubmenuParentId;
        HoverTargetActionId = targetActionId;
        HoverInsertIndex = computedIndex;
        HoverInsertAfter = insertAfter;
        HoverIsMenuDock = false;
        HoverHideZone = false;
        HoverVisibleIds = targetVisibleIds;
        OnStateChanged?.Invoke();
    }

    public static void HoverSurfaceContainer(
        string targetSurfaceId,
        string? targetSubmenuParentId,
        IReadOnlyList<string> targetVisibleIds)
    {
        if (!IsDragging || !CanAcceptDrop(targetSurfaceId, targetSubmenuParentId)) return;

        // If already hovering a specific slot inside this exact surface/submenu, keep the finer slot target!
        if (HoverSurfaceId == targetSurfaceId
            && HoverSubmenuParentId == targetSubmenuParentId
            && !string.IsNullOrEmpty(HoverTargetActionId)
            && !HoverIsMenuDock
            && !HoverHideZone)
        {
            return;
        }

        int endIndex = targetVisibleIds.Count;
        if (HoverSurfaceId == targetSurfaceId
            && HoverSubmenuParentId == targetSubmenuParentId
            && string.IsNullOrEmpty(HoverTargetActionId)
            && HoverInsertIndex == endIndex
            && !HoverIsMenuDock
            && !HoverHideZone)
        {
            return;
        }

        HoverSurfaceId = targetSurfaceId;
        HoverSubmenuParentId = targetSubmenuParentId;
        HoverTargetActionId = string.Empty;
        HoverInsertIndex = endIndex;
        HoverInsertAfter = true;
        HoverIsMenuDock = false;
        HoverHideZone = false;
        HoverVisibleIds = targetVisibleIds;
        OnStateChanged?.Invoke();
    }

    public static void HoverSendToMenuDock(string pairedMenuSurfaceId)
    {
        if (!IsDragging || SourceIsFixed || string.IsNullOrWhiteSpace(pairedMenuSurfaceId)) return;
        if (HoverIsMenuDock && HoverSurfaceId == pairedMenuSurfaceId) return;

        HoverSurfaceId = pairedMenuSurfaceId;
        HoverSubmenuParentId = null;
        HoverTargetActionId = string.Empty;
        HoverInsertIndex = 0;
        HoverInsertAfter = false;
        HoverIsMenuDock = true;
        HoverHideZone = false;
        HoverVisibleIds = GetEffectiveVisibleIds(pairedMenuSurfaceId);
        OnStateChanged?.Invoke();
    }

    public static void HoverHideTarget()
    {
        if (!IsDragging || SourceIsFixed) return;
        if (HoverHideZone) return;

        HoverHideZone = true;
        HoverIsMenuDock = false;
        HoverSurfaceId = string.Empty;
        HoverSubmenuParentId = null;
        HoverTargetActionId = string.Empty;
        OnStateChanged?.Invoke();
    }

    public static void CompleteDrop()
    {
        if (!hasCandidate) return;

        if (!IsDragging)
        {
            ResetInternal();
            return;
        }

        string srcSurface = SourceSurfaceId;
        string? srcSubmenu = SourceSubmenuParentId;
        string actionId = SourceActionId;
        string label = SourceLabel;
        bool srcFixed = SourceIsFixed;
        bool srcIsMenu = SourceIsContextMenu;

        string dstSurface = HoverSurfaceId;
        string? dstSubmenu = HoverSubmenuParentId;
        int dstIndex = HoverInsertIndex;
        var dstVisible = HoverVisibleIds;
        bool isMenuDock = HoverIsMenuDock;
        bool isHide = HoverHideZone;

        lastDragEndedTick = Environment.TickCount64;
        ResetInternal();
        OnStateChanged?.Invoke();

        if (isHide && !srcFixed)
        {
            UiCustomizationStore.SetVisible(srcSurface, actionId, visible: false, submenuParentId: srcSubmenu);
            MouseActionService.ReportStatus($"👁️ Hidden '{label}' (restore anytime via Customize Layout)");
            return;
        }

        if (string.IsNullOrWhiteSpace(dstSurface))
            return;

        bool sameBucket = srcSurface.Equals(dstSurface, StringComparison.OrdinalIgnoreCase)
            && string.Equals(srcSubmenu ?? string.Empty, dstSubmenu ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        if (sameBucket)
        {
            UiCustomizationStore.Reorder(dstSurface, dstVisible, actionId, dstIndex, dstSubmenu);
            MouseActionService.ReportStatus($"↕️ Reordered '{label}'");
            return;
        }

        bool dstIsMenu = UiSurfaceIds.IsContextMenu(dstSurface);
        bool hideFromSource = !srcFixed && (
            !srcIsMenu ||
            (!string.IsNullOrWhiteSpace(srcSubmenu) ? false : UiCustomizationStore.IsAdded(srcSurface, actionId)));

        UiCustomizationStore.TransferAction(
            srcSurface,
            srcSubmenu,
            dstSurface,
            dstSubmenu,
            actionId,
            dstVisible,
            dstIndex,
            hideFromSource);

        if (srcIsMenu && !dstIsMenu)
        {
            ContextMenuService.Close();
            MouseActionService.ReportStatus($"📌 Placed '{label}' on {UiSurfaceIds.DisplayNameOf(dstSurface)}");
        }
        else if (isMenuDock || (!srcIsMenu && dstIsMenu))
        {
            MouseActionService.ReportStatus($"📥 Moved '{label}' to {UiSurfaceIds.DisplayNameOf(dstSurface)}");
        }
        else if (srcIsMenu && dstIsMenu && !string.IsNullOrWhiteSpace(srcSubmenu) && string.IsNullOrWhiteSpace(dstSubmenu))
        {
            MouseActionService.ReportStatus($"⚡ Promoted '{label}' to 1-click right-click menu");
        }
        else
        {
            MouseActionService.ReportStatus($"📌 Moved '{label}' to {UiSurfaceIds.DisplayNameOf(dstSurface)}");
        }
    }

    public static void Cancel()
    {
        if (!hasCandidate) return;
        bool wasDragging = IsDragging;
        ResetInternal();
        if (wasDragging)
        {
            lastDragEndedTick = Environment.TickCount64;
            OnStateChanged?.Invoke();
            MouseActionService.ReportStatus("Cancelled drag");
        }
    }

    /// <summary>Returns true once if a pointer drag just finished, preventing a trailing click from firing the button's action.</summary>
    public static bool ConsumeSuppressedClick()
    {
        if (lastDragEndedTick == 0) return false;
        long elapsed = Environment.TickCount64 - lastDragEndedTick;
        if (elapsed <= ClickSuppressionWindowMs)
        {
            lastDragEndedTick = 0;
            return true;
        }
        lastDragEndedTick = 0;
        return false;
    }

    static void ResetInternal()
    {
        hasCandidate = false;
        IsDragging = false;
        SourceSurfaceId = string.Empty;
        SourceSubmenuParentId = null;
        SourceActionId = string.Empty;
        SourceLabel = string.Empty;
        SourceIsFixed = false;
        SourceVisibleIds = [];
        HoverSurfaceId = string.Empty;
        HoverSubmenuParentId = null;
        HoverTargetActionId = string.Empty;
        HoverInsertIndex = 0;
        HoverInsertAfter = false;
        HoverIsMenuDock = false;
        HoverHideZone = false;
        HoverVisibleIds = [];
    }
}
