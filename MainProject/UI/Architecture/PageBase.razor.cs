using Microsoft.AspNetCore.Components;
using MyLovelyMail.MainProject.Services;

namespace MyLovelyMail.MainProject.UI.Layout.Architecture
{
    public partial class PageBase : LayoutComponentBase
    {
        [Inject] public NavigationManager NavManager { get; set; } = default!;

        public bool HasNavigatedAway { get; private set; }

        // Auth Requirements
        [Parameter] public bool RequireAuth { get; set; }
        [Parameter] public bool RequireOnboarding { get; set; }

        protected override void OnInitialized()
        {
            NavManager.LocationChanged += (s, e) => { HasNavigatedAway = true; };
        }

        protected void RecoverFromError()
        {
            NavManager.NavigateTo("/", true);
        }
    }
}
