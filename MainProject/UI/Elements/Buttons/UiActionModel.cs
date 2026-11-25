using Microsoft.AspNetCore.Components.Web;
using MyLovelyMail.MainProject.Services;

namespace MyLovelyMail.MainProject.UI.Elements.Buttons;

public enum ActionVisualKind
{
    Pill,
    ActionBtn,
    RowQuick,
    SidebarQuick,
    SidebarNewFolder,
    ComposeBtn,
    GlowPlusOrb
}

/// <summary>
/// Declarative C# model for a button rendered inside <see cref="CustomizableActionBar"/>.
/// Every button shares drag-reordering, visibility customization, right-click options, and menu&lt;-&gt;toolbar transfer.
/// </summary>
public sealed record UiActionModel(
    string ActionId,
    string Label,
    Func<MouseEventArgs, Task>? OnClick = null,
    string? Title = null,
    bool IsActive = false,
    bool IsDanger = false,
    bool IsPinned = false,
    bool IsIcon = false,
    bool IsCompact = false,
    bool Disabled = false,
    bool IsFixed = false,
    ActionVisualKind VisualKind = ActionVisualKind.Pill,
    string? CustomClass = null,
    string? InlineStyle = null,
    string? Mouse4Action = null,
    string? Mouse5Action = null,
    IReadOnlyList<ContextMenuItem>? ExtraContextMenuItems = null)
{
    public static UiActionModel CreateSync(
        string actionId,
        string label,
        Action onClick,
        string? title = null,
        bool isActive = false,
        bool isDanger = false,
        bool isPinned = false,
        bool isIcon = false,
        bool isCompact = false,
        bool disabled = false,
        bool isFixed = false,
        ActionVisualKind visualKind = ActionVisualKind.Pill,
        string? customClass = null,
        string? inlineStyle = null,
        string? mouse4Action = null,
        string? mouse5Action = null,
        IReadOnlyList<ContextMenuItem>? extraContextMenuItems = null) =>
        new(
            actionId,
            label,
            _ => { onClick(); return Task.CompletedTask; },
            title,
            isActive,
            isDanger,
            isPinned,
            isIcon,
            isCompact,
            disabled,
            isFixed,
            visualKind,
            customClass,
            inlineStyle,
            mouse4Action,
            mouse5Action,
            extraContextMenuItems);

    public static UiActionModel CreateMouseSync(
        string actionId,
        string label,
        Action<MouseEventArgs> onClick,
        string? title = null,
        bool isActive = false,
        bool isDanger = false,
        bool isPinned = false,
        bool isIcon = false,
        bool isCompact = false,
        bool disabled = false,
        bool isFixed = false,
        ActionVisualKind visualKind = ActionVisualKind.Pill,
        string? customClass = null,
        string? inlineStyle = null,
        string? mouse4Action = null,
        string? mouse5Action = null,
        IReadOnlyList<ContextMenuItem>? extraContextMenuItems = null) =>
        new(
            actionId,
            label,
            e => { onClick(e); return Task.CompletedTask; },
            title,
            isActive,
            isDanger,
            isPinned,
            isIcon,
            isCompact,
            disabled,
            isFixed,
            visualKind,
            customClass,
            inlineStyle,
            mouse4Action,
            mouse5Action,
            extraContextMenuItems);
}
