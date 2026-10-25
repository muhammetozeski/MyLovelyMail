using Microsoft.AspNetCore.Components;

namespace MyLovelyMail.MainProject.Services;

/// <summary>A menu action with optional children; an action plus children renders a split button.</summary>
/// <param name="Label">Visible action name.</param>
/// <param name="Run">Null for a children-only entry.</param>
/// <param name="Enabled">Whether the action can currently run.</param>
/// <param name="Children">Optional child actions, such as folder mute durations.</param>
public sealed record ContextMenuItem(string Label, Func<Task>? Run = null, bool Enabled = true,
    IReadOnlyList<ContextMenuItem>? Children = null)
{
    /// <summary>Adapts synchronous page commands to the same awaited execution path.</summary>
    /// <param name="label">Visible action name.</param>
    /// <param name="run">Existing command.</param>
    /// <param name="enabled">Whether it can run now.</param>
    /// <param name="children">Optional child choices.</param>
    public ContextMenuItem(string label, Action run, bool enabled = true, IReadOnlyList<ContextMenuItem>? children = null)
        : this(label, () => { run(); return Task.CompletedTask; }, enabled, children) { }
}

/// <summary>One open menu and its owning component, so callbacks repaint the same view that opened it.</summary>
/// <param name="Owner">Blazor event receiver.</param>
/// <param name="Title">Menu heading.</param>
/// <param name="Items">Captured actions and targets.</param>
/// <param name="X">Viewport horizontal coordinate.</param>
/// <param name="Y">Viewport vertical coordinate.</param>
public sealed record ContextMenuData(object Owner, string Title, IReadOnlyList<ContextMenuItem> Items, double X, double Y);

/// <summary>Owns one app-wide menu; business actions remain in their existing services and page owners.</summary>
public static class ContextMenuService
{
    const bool IsLogEnabled = true;
    public static ContextMenuData? Current { get; private set; }
    public static event Action? OnChanged;

    /// <summary>Replaces the open menu with captured actions for a particular component and target.</summary>
    /// <param name="owner">Receiver whose Blazor event handling must repaint after the action.</param>
    /// <param name="title">Visible heading.</param>
    /// <param name="items">Actions in display order.</param>
    /// <param name="x">Viewport horizontal coordinate.</param>
    /// <param name="y">Viewport vertical coordinate.</param>
    public static void Open(object owner, string title, IReadOnlyList<ContextMenuItem> items, double x = 0, double y = 0)
    {
        Current = new(owner, title, items, x, y);
        Log($"Context menu opened: title='{title}'; items={items.Count}.", Run: IsLogEnabled);
        OnChanged?.Invoke();
    }

    /// <summary>Drops captured targets and component references when the menu is dismissed.</summary>
    public static void Close()
    {
        if (Current == null) return;
        Current = null;
        OnChanged?.Invoke();
    }

    /// <summary>Executes one captured action through its component's event receiver and catches final UI failures.</summary>
    /// <param name="item">Chosen menu entry.</param>
    public static async Task RunAsync(ContextMenuItem item)
    {
        if (Current is not { } menu || !item.Enabled || item.Run == null) return;
        Close();
        try
        {
            Log($"Context action started: menu='{menu.Title}'; action='{item.Label}'.", Run: IsLogEnabled);
            await EventCallback.Factory.Create(menu.Owner, item.Run).InvokeAsync();
        }
        catch (Exception exception)
        {
            Log($"Context action failed: menu='{menu.Title}'; action='{item.Label}'; {exception}", LogLevel.Error, Run: IsLogEnabled);
            MouseActionService.ReportStatus($"Could not complete {item.Label}: {exception.Message}");
        }
    }
}
