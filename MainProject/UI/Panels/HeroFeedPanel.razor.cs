using Microsoft.AspNetCore.Components;

namespace MyLovelyMail.MainProject.UI.Panels
{
    /// <summary>
    /// Vertical page shape with three named slots: a visual <see cref="Hero"/> on top, a textual
    /// <see cref="Intro"/> in the middle (title plus action buttons), and a scrollable
    /// <see cref="Feed"/> below. Any screen needing the "store-like" layout drops content into the
    /// slots and inherits consistent spacing without re-coding the structure.
    /// </summary>
    public partial class HeroFeedPanel : ComponentBase
    {
        /// <summary> Top visual area: typically an image or hero card. </summary>
        [Parameter] public RenderFragment? Hero { get; set; }

        /// <summary> Middle area: title, description, and action buttons that introduce the feed. </summary>
        [Parameter] public RenderFragment? Intro { get; set; }

        /// <summary> Bottom area: a feed of items rendered by <see cref="UI.Layout.Architecture.FeedView{TItem}"/> or a custom grid. </summary>
        [Parameter] public RenderFragment? Feed { get; set; }
    }

}
