using Microsoft.AspNetCore.Components;

namespace MyLovelyMail.MainProject.UI.Layout.Architecture
{
    public partial class PageBase : LayoutComponentBase
    {
        [Inject] public NavigationManager NavManager { get; set; } = default!;

    }
}
