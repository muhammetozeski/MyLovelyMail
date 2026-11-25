using System.Text;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services;

/// <summary>One item in the shared custom context menu; items with children open a submenu instead of closing.</summary>
/// <param name="Label">Visible English command text.</param>
/// <param name="Action">Async callback run after the menu closes.</param>
/// <param name="Enabled">Disabled commands stay visible so the user sees what exists and what requires another state.</param>
/// <param name="Children">Submenu commands.</param>
/// <param name="PinActionId">Optional legacy reader toolbar action ID.</param>
/// <param name="ActionId">Canonical customization action ID shared across toolbars and menus.</param>
/// <param name="IsFixed">True when this item is structural and cannot be hidden.</param>
public sealed record ContextMenuItem(
    string Label,
    Func<Task>? Action = null,
    bool Enabled = true,
    IReadOnlyList<ContextMenuItem>? Children = null,
    string? PinActionId = null,
    string? ActionId = null,
    bool IsFixed = false,
    bool SeparatorBefore = false)
{
    public ContextMenuItem(string label, Action action, bool enabled = true, string? pinActionId = null, string? actionId = null, bool isFixed = false, bool separatorBefore = false)
        : this(label, () => { action(); return Task.CompletedTask; }, enabled, null, pinActionId, actionId, isFixed, separatorBefore) { }

    public bool HasChildren => Children is { Count: > 0 };

    public string EffectiveActionId
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ActionId)) return ActionId;
            if (!string.IsNullOrWhiteSpace(PinActionId))
                return UiCustomizationStore.MapLegacyReaderActionId(PinActionId) ?? PinActionId;
            return BuildStableSlug(Label);
        }
    }

    public static string BuildStableSlug(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "Menu.Item";
        var sb = new StringBuilder("Menu.");
        bool prevDot = true;
        foreach (char ch in label)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                prevDot = false;
            }
            else if (ch == '(')
            {
                // Ignore dynamic counts like "(3)" at the end of labels so IDs remain stable across state changes.
                break;
            }
            else if (!prevDot && sb.Length > 5)
            {
                sb.Append('_');
                prevDot = true;
            }
        }
        return sb.ToString().TrimEnd('_');
    }
}

/// <summary>Holds the single open custom context menu and resolves user layout customizations per menu surface.</summary>
public static class ContextMenuService
{
    public static bool IsOpen { get; private set; }
    public static object? Owner { get; private set; }
    public static string Title { get; private set; } = string.Empty;
    public static string SurfaceId { get; private set; } = string.Empty;
    public static bool IsCustomizable => !string.IsNullOrWhiteSpace(SurfaceId);
    public static bool IsEditMode { get; private set; }
    public static double X { get; private set; }
    public static double Y { get; private set; }
    public static double LastPointerX { get; private set; }
    public static double LastPointerY { get; private set; }
    public static IReadOnlyList<ContextMenuItem> RawItems { get; private set; } = [];
    public static Func<string, ContextMenuItem?>? AddedItemResolver { get; private set; }

    public static IReadOnlyList<ContextMenuItem> Items => ResolveVisibleItems(includeHidden: IsEditMode);

    public static event Action? OnChanged;

    /// <summary>Records the latest non-zero pointer coordinate so keyboard/synthetic button clicks open near the button instead of (12, 12).</summary>
    public static void TrackPointer(double x, double y)
    {
        if (x > 0 || y > 0)
        {
            LastPointerX = x;
            LastPointerY = y;
        }
    }

    static ContextMenuService()
    {
        UiCustomizationStore.OnChanged += () =>
        {
            if (IsOpen) OnChanged?.Invoke();
        };
    }

    /// <summary>Opens the menu at the pointer, optionally bound to a customizable <paramref name="surfaceId"/>.</summary>
    public static void Open(
        object owner,
        string title,
        IReadOnlyList<ContextMenuItem> items,
        double x,
        double y,
        string? surfaceId = null,
        Func<string, ContextMenuItem?>? addedItemResolver = null)
    {
        if (items.Count == 0 && string.IsNullOrWhiteSpace(surfaceId)) return;
        if (x <= 0 && y <= 0)
        {
            x = LastPointerX > 0 ? LastPointerX : 360;
            y = LastPointerY > 0 ? LastPointerY : 120;
        }
        else
        {
            TrackPointer(x, y);
        }
        Owner = owner;
        Title = title;
        RawItems = items;
        SurfaceId = surfaceId ?? string.Empty;
        AddedItemResolver = addedItemResolver;
        IsEditMode = false;
        X = Math.Max(8, x);
        Y = Math.Max(8, y);
        IsOpen = true;
        OnChanged?.Invoke();
    }

    public static void ToggleEditMode()
    {
        if (!IsOpen || !IsCustomizable) return;
        IsEditMode = !IsEditMode;
        OnChanged?.Invoke();
    }

    public static IReadOnlyList<ContextMenuItem> ResolveVisibleItems(bool includeHidden = false)
    {
        if (string.IsNullOrWhiteSpace(SurfaceId))
            return RawItems;

        var subtreeCatalog = new Dictionary<string, ContextMenuItem>(StringComparer.OrdinalIgnoreCase);
        IndexLeafItemsRecursive(RawItems, subtreeCatalog);

        ContextMenuItem? ResolveAdded(string id)
        {
            if (subtreeCatalog.TryGetValue(id, out var found))
                return found;
            return AddedItemResolver?.Invoke(id);
        }

        var rootResolved = UiCustomizationStore.ApplyLayout(
            SurfaceId,
            RawItems,
            static item => item.EffectiveActionId,
            static item => item.IsFixed,
            ResolveAdded,
            submenuParentId: null,
            includeHidden: includeHidden);

        var finalList = new List<ContextMenuItem>(rootResolved.Count);
        foreach (var item in rootResolved)
        {
            if (item.Children is { Count: > 0 } children)
            {
                string parentId = item.EffectiveActionId;
                var resolvedChildren = ResolveSubmenuRecursive(SurfaceId, parentId, children, ResolveAdded, includeHidden);
                finalList.Add(item with { Children = resolvedChildren });
            }
            else
            {
                finalList.Add(item);
            }
        }
        return finalList;
    }

    static IReadOnlyList<ContextMenuItem> ResolveSubmenuRecursive(
        string surfaceId,
        string parentId,
        IReadOnlyList<ContextMenuItem> children,
        Func<string, ContextMenuItem?> resolveAdded,
        bool includeHidden)
    {
        var applied = UiCustomizationStore.ApplyLayout(
            surfaceId,
            children,
            static c => c.EffectiveActionId,
            static c => c.IsFixed,
            resolveAdded,
            submenuParentId: parentId,
            includeHidden: includeHidden);

        var list = new List<ContextMenuItem>(applied.Count);
        foreach (var child in applied)
        {
            if (child.Children is { Count: > 0 } grandChildren)
            {
                var resolvedGrand = ResolveSubmenuRecursive(surfaceId, child.EffectiveActionId, grandChildren, resolveAdded, includeHidden);
                list.Add(child with { Children = resolvedGrand });
            }
            else
            {
                list.Add(child);
            }
        }
        return list;
    }

    public static void IndexLeafItemsRecursive(IEnumerable<ContextMenuItem> source, Dictionary<string, ContextMenuItem> sink)
    {
        foreach (var item in source)
        {
            if (item.Children is { Count: > 0 } children)
            {
                IndexLeafItemsRecursive(children, sink);
            }
            else
            {
                string id = item.EffectiveActionId;
                if (!string.IsNullOrWhiteSpace(id))
                    sink.TryAdd(id, item);
            }
        }
    }

    /// <summary>Closes the active menu, optionally only when the disposing component owns it.</summary>
    public static void Close(object? owner = null)
    {
        if (!IsOpen || owner != null && !ReferenceEquals(Owner, owner)) return;
        IsOpen = false;
        IsEditMode = false;
        Owner = null;
        Title = string.Empty;
        SurfaceId = string.Empty;
        RawItems = [];
        AddedItemResolver = null;
        OnChanged?.Invoke();
    }

    /// <summary>Closes the menu first, then executes the selected command through the shared input error boundary.</summary>
    public static async Task InvokeAsync(ContextMenuItem item)
    {
        if (!item.Enabled || item.Action == null) return;
        Close();
        try { await item.Action(); }
        catch (Exception exception)
        {
            Log($"Context menu command '{item.Label}' failed: {exception}", LogLevel.Error);
            MouseActionService.ReportStatus($"Could not complete '{item.Label}': {exception.Message}");
        }
    }
}
