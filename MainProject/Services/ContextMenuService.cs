using MyLovelyMail.MainProject.UI.Pages;

namespace MyLovelyMail.MainProject.Services;

/// <summary>One item in the shared custom context menu; items with children open a submenu instead of closing.</summary>
/// <param name="Label">Visible English command text.</param>
/// <param name="Action">Async callback run after the menu closes.</param>
/// <param name="Enabled">Disabled commands stay visible so the user sees what exists and what requires another state.</param>
/// <param name="Children">Submenu commands.</param>
/// <param name="PinActionId">Optional reader toolbar action ID that can be pinned/unpinned via middle-click or right-click.</param>
public sealed record ContextMenuItem(
    string Label,
    Func<Task>? Action = null,
    bool Enabled = true,
    IReadOnlyList<ContextMenuItem>? Children = null,
    string? PinActionId = null)
{
    public ContextMenuItem(string label, Action action, bool enabled = true, string? pinActionId = null)
        : this(label, () => { action(); return Task.CompletedTask; }, enabled, null, pinActionId) { }

    public bool HasChildren => Children is { Count: > 0 };
}

/// <summary>Holds the single open custom context menu and keeps ownership scoped to the surface that opened it.</summary>
public static class ContextMenuService
{
    public static bool IsOpen { get; private set; }
    public static object? Owner { get; private set; }
    public static string Title { get; private set; } = string.Empty;
    public static double X { get; private set; }
    public static double Y { get; private set; }
    public static IReadOnlyList<ContextMenuItem> Items { get; private set; } = [];
    public static event Action? OnChanged;

    /// <summary>Opens the menu at the pointer, clamping later in CSS/layout so long menus stay inside the window.</summary>
    public static void Open(object owner, string title, IReadOnlyList<ContextMenuItem> items, double x, double y)
    {
        if (items.Count == 0) return;
        Owner = owner;
        Title = title;
        Items = items;
        X = Math.Max(8, x);
        Y = Math.Max(8, y);
        IsOpen = true;
        OnChanged?.Invoke();
    }

    /// <summary>Closes the active menu, optionally only when the disposing component owns it.</summary>
    public static void Close(object? owner = null)
    {
        if (!IsOpen || owner != null && !ReferenceEquals(Owner, owner)) return;
        IsOpen = false;
        Owner = null;
        Title = string.Empty;
        Items = [];
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
