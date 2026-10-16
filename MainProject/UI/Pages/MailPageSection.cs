using Microsoft.AspNetCore.Components;

namespace MyLovelyMail.MainProject.UI.Pages;

/// <summary>A presentation section sharing the mail page's state and event lifecycle.</summary>
public abstract class MailPageSection : ComponentBase, IHandleEvent
{
    /// <summary>The owning page; sections do not keep a second copy of its selection or draft.</summary>
    [Parameter, EditorRequired] public Mail View { get; set; } = default!;

    // Lambdas declared in a section must refresh the owning page, including its other sections.
    Task IHandleEvent.HandleEventAsync(EventCallbackWorkItem callback, object? argument) =>
        ((IHandleEvent)View).HandleEventAsync(callback, argument);
}
