using Microsoft.AspNetCore.Components;
using MyLovelyMail.MainProject.Services;
using MyLovelyMail.MainProject.UI.Layout.Architecture;

namespace MyLovelyMail.MainProject.UI.Elements.Modals;

/// <summary>
/// Hierarchical base class for all modal elements and modal views in the application.
/// Inherits <see cref="ThemedComponentBase"/> so every modal reacts automatically to theme changes
/// and shares unified visibility, header, and navigation-stack behavior.
/// </summary>
public abstract class ModalBase : ThemedComponentBase
{
    [Parameter] public bool IsVisible { get; set; } = true;
    [Parameter] public RenderFragment? ChildContent { get; set; }
    [Parameter] public EventCallback OnBackdropClick { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
    [Parameter] public EventCallback OnBack { get; set; }
    [Parameter] public string? Title { get; set; }
    [Parameter] public bool ShowBackButton { get; set; }
    [Parameter] public bool ShowCloseButton { get; set; } = true;
    [Parameter] public int? MaxWidthPx { get; set; }

    protected bool CanNavigateBack => ShowBackButton || OnBack.HasDelegate || ModalService.CanGoBack;

    protected Task TriggerBackAsync()
    {
        if (OnBack.HasDelegate)
            return OnBack.InvokeAsync();
        ModalService.GoBack();
        return Task.CompletedTask;
    }

    protected Task TriggerCloseAsync()
    {
        if (OnClose.HasDelegate)
            return OnClose.InvokeAsync();
        if (OnBackdropClick.HasDelegate)
            return OnBackdropClick.InvokeAsync();
        ModalService.CloseAll();
        return Task.CompletedTask;
    }
}
