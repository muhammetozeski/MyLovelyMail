using Microsoft.AspNetCore.Components;
using MyLovelyMail.MainProject.Constants.ThemeConstants;

namespace MyLovelyMail.MainProject.UI.Elements.Structure
{
    /// <summary>
    /// One-dimensional flexbox container. Sets <c>display:flex</c>, <c>flex-direction</c>,
    /// <c>gap</c>, <c>align-items</c>, <c>justify-content</c> and <c>flex-wrap</c> on a single
    /// inline-styled div so callers never write raw flex CSS at the call site. Direct children
    /// receive padding from <see cref="ItemPaddingTop"/>..<see cref="ItemPaddingLeft"/> via CSS
    /// custom properties cascaded from the Stack itself.
    /// </summary>
    public partial class Stack : ComponentBase
    {
        [Parameter] public RenderFragment? ChildContent { get; set; }

        /// <summary> CSS <c>flex-direction</c>. Use values from <see cref="Directions"/>. </summary>
        [Parameter] public string Direction { get; set; } = Directions.Column;

        /// <summary> Gap between children in CSS pixels. Pulls defaults from <see cref="AppMeasures.Space"/>. </summary>
        [Parameter] public int Space { get; set; } = AppMeasures.Space.Medium;

        /// <summary> CSS <c>align-items</c>. Use values from <see cref="Aligns"/>. </summary>
        [Parameter] public string Align { get; set; } = Aligns.Stretch;

        /// <summary> CSS <c>justify-content</c>. Use values from <see cref="Justifies"/>. </summary>
        [Parameter] public string Justify { get; set; } = Justifies.Start;

        /// <summary> Null means: wrap when laying out a row, do not wrap when laying out a column. Explicit true/false overrides. </summary>
        [Parameter] public bool? Wrap { get; set; }

        /// <summary> CSS pixels added inside the top edge of every direct child. Use values from <see cref="ItemPaddings"/>. Loses to inline padding set by the child itself. </summary>
        [Parameter] public int ItemPaddingTop { get; set; } = ItemPaddings.Medium;
        /// <summary> CSS pixels added inside the right edge of every direct child. Use values from <see cref="ItemPaddings"/>. Loses to inline padding set by the child itself. </summary>
        [Parameter] public int ItemPaddingRight { get; set; } = ItemPaddings.Medium;
        /// <summary> CSS pixels added inside the bottom edge of every direct child. Use values from <see cref="ItemPaddings"/>. Loses to inline padding set by the child itself. </summary>
        [Parameter] public int ItemPaddingBottom { get; set; } = ItemPaddings.Medium;
        /// <summary> CSS pixels added inside the left edge of every direct child. Use values from <see cref="ItemPaddings"/>. Loses to inline padding set by the child itself. </summary>
        [Parameter] public int ItemPaddingLeft { get; set; } = ItemPaddings.Medium;

        [Parameter] public string CustomClass { get; set; } = string.Empty;

        bool EffectiveWrap => Wrap ?? Direction == Directions.Row;

        string _computedStyle =>
            $"display:flex;" +
            $"flex-direction:{Direction};" +
            $"gap:{Space}px;" +
            $"align-items:{Align};" +
            $"justify-content:{Justify};" +
            $"flex-wrap:{(EffectiveWrap ? "wrap" : "nowrap")};" +
            $"--stack-item-pad-top:{ItemPaddingTop}px;" +
            $"--stack-item-pad-right:{ItemPaddingRight}px;" +
            $"--stack-item-pad-bottom:{ItemPaddingBottom}px;" +
            $"--stack-item-pad-left:{ItemPaddingLeft}px;";

        public static class Directions
        {
            public const string Row = "row";
            public const string Column = "column";
            public const string RowReverse = "row-reverse";
            public const string ColumnReverse = "column-reverse";
        }

        public static class Aligns
        {
            public const string Start = "flex-start";
            public const string Center = "center";
            public const string End = "flex-end";
            public const string Stretch = "stretch";
            public const string Baseline = "baseline";
        }

        public static class Justifies
        {
            public const string Start = "flex-start";
            public const string Center = "center";
            public const string End = "flex-end";
            public const string SpaceBetween = "space-between";
            public const string SpaceAround = "space-around";
            public const string SpaceEvenly = "space-evenly";
        }

        /// <summary> Four named padding tiers (in CSS pixels) for <see cref="ItemPaddingTop"/>..<see cref="ItemPaddingLeft"/>. Pulled from <see cref="AppMeasures.Space"/> so the design system stays the single source of truth. </summary>
        public static class ItemPaddings
        {
            public const int None = 0;
            public const int Small = AppMeasures.Space.Small;
            public const int Medium = AppMeasures.Space.Medium;
            public const int Large = AppMeasures.Space.Large;
        }
    }
}
