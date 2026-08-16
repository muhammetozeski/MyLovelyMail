using Microsoft.AspNetCore.Components;
using MyLovelyMail.MainProject.Events;

namespace MyLovelyMail.MainProject.UI.Layout.Architecture
{
    /// <summary>
    /// Base class for every parameterless *Css component (and any component whose markup bakes in
    /// theme colors). Blazor skips re-rendering children whose parameters did not change, so a
    /// theme swap would leave their emitted CSS on the old palette; this base re-renders itself
    /// whenever the "OnThemeChanged" event fires.
    /// </summary>
    public class ThemedComponentBase : ComponentBase, IDisposable
    {
        protected override void OnInitialized() => MainEvents.OnDataChanged += HandleDataChanged;

        void HandleDataChanged(string eventName, object? data)
        {
            if (eventName == "OnThemeChanged")
                InvokeAsync(StateHasChanged);
        }

        public void Dispose() => MainEvents.OnDataChanged -= HandleDataChanged;
    }
}
