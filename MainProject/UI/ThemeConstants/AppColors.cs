using MyLovelyMail.MainProject.Constants.ThemeConstants;

namespace MyLovelyMail.MainProject.Constants.ThemeConstants
{
    /// <summary>
    /// Project-wide color accessor. Every property delegates to <see cref="ThemeManager.Current"/> so
    /// swapping the active <see cref="AppTheme"/> reskins every consumer on the next render.
    /// </summary>
    public static class AppColors
    {
        /// <summary>
        /// Identity colors that must stay stable across themes: account dots and tag chips both
        /// pick from this one list round-robin, so an account and a tag never end up with two
        /// different "pinks" and a palette edit reaches both features at once.
        /// </summary>
        public static readonly string[] IdentityPalette =
            ["#EC6FA9", "#7C7BFF", "#FFB86B", "#34B27B", "#4FB6E8", "#F4714A", "#B79CEF", "#E9B949"];

        /// <summary>Near-black plum used as ink on light identity colors; dark enough to clear 4.5:1 on every palette entry.</summary>
        public static readonly Color IdentityInkDark = Color.FromArgb("#1A0F18");

        /// <summary>
        /// Readable text color for anything painted on an identity color (account badges, tag
        /// chips, thread pills): white or the dark plum, whichever contrasts more. The palette is
        /// pastel, so in practice the dark ink wins everywhere — which is the point, since white
        /// text on these colors sits between 1.7:1 and 3.4:1 and fails WCAG AA outright.
        /// </summary>
        public static Color InkOn(string backgroundHex)
        {
            var background = Color.FromArgb(backgroundHex);
            return ContrastRatio(background, Colors.White) >= ContrastRatio(background, IdentityInkDark)
                ? Colors.White
                : IdentityInkDark;
        }

        /// <summary>WCAG 2.1 contrast ratio (1:1 identical, 21:1 black on white).</summary>
        public static double ContrastRatio(Color first, Color second)
        {
            double a = RelativeLuminance(first), b = RelativeLuminance(second);
            return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
        }

        static double RelativeLuminance(Color color)
        {
            static double Channel(float raw)
            {
                double value = raw;
                return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(color.Red) + 0.7152 * Channel(color.Green) + 0.0722 * Channel(color.Blue);
        }

        /// <summary>
        /// The reader frame paints mail on a LIGHT canvas in every theme, because mail HTML is
        /// authored for white backgrounds and turns unreadable on a dark one. Both the iframe
        /// element (MailCss) and the document inside it (MailBodyRenderer) read these, so the two
        /// halves of the same surface can never drift apart.
        /// </summary>
        public static class MailCanvas
        {
            public const string Background = "#ffffff";
            public const string Text = "#33333a";
            public const string Link = "#d14d8b";
            public const string QuoteBorder = "#f0c0d4";
            public const string QuoteText = "#7d5a6e";
        }

        /// <inheritdoc cref="AppTheme.Primary"/>
        public static Color Primary => ThemeManager.Current.Primary;

        /// <inheritdoc cref="AppTheme.PrimaryLight"/>
        public static Color PrimaryLight => ThemeManager.Current.PrimaryLight;

        /// <inheritdoc cref="AppTheme.PrimaryDark"/>
        public static Color PrimaryDark => ThemeManager.Current.PrimaryDark;

        /// <inheritdoc cref="AppTheme.Secondary"/>
        public static Color Secondary => ThemeManager.Current.Secondary;

        /// <inheritdoc cref="AppTheme.SecondaryDark"/>
        public static Color SecondaryDark => ThemeManager.Current.SecondaryDark;

        /// <inheritdoc cref="AppTheme.Accent"/>
        public static Color Accent => ThemeManager.Current.Accent;

        /// <inheritdoc cref="AppTheme.AccentDark"/>
        public static Color AccentDark => ThemeManager.Current.AccentDark;

        /// <inheritdoc cref="AppTheme.Background"/>
        public static Color Background => ThemeManager.Current.Background;

        /// <inheritdoc cref="AppTheme.BackgroundDeep"/>
        public static Color BackgroundDeep => ThemeManager.Current.BackgroundDeep;

        /// <inheritdoc cref="AppTheme.BackgroundBase"/>
        public static Color BackgroundBase => ThemeManager.Current.BackgroundBase;

        /// <inheritdoc cref="AppTheme.Surface"/>
        public static Color Surface => ThemeManager.Current.Surface;

        /// <inheritdoc cref="AppTheme.SurfaceDark"/>
        public static Color SurfaceDark => ThemeManager.Current.SurfaceDark;

        /// <inheritdoc cref="AppTheme.SurfaceSubtle"/>
        public static Color SurfaceSubtle => ThemeManager.Current.SurfaceSubtle;

        /// <inheritdoc cref="AppTheme.SurfaceNormal"/>
        public static Color SurfaceNormal => ThemeManager.Current.SurfaceNormal;

        /// <inheritdoc cref="AppTheme.SurfaceStrong"/>
        public static Color SurfaceStrong => ThemeManager.Current.SurfaceStrong;

        /// <inheritdoc cref="AppTheme.SurfaceTintPrimary"/>
        public static Color SurfaceTintPrimary => ThemeManager.Current.SurfaceTintPrimary;

        /// <inheritdoc cref="AppTheme.SurfaceTintAccent"/>
        public static Color SurfaceTintAccent => ThemeManager.Current.SurfaceTintAccent;

        /// <inheritdoc cref="AppTheme.SurfaceDarken"/>
        public static Color SurfaceDarken => ThemeManager.Current.SurfaceDarken;

        /// <inheritdoc cref="AppTheme.PaywallBackground"/>
        public static Color PaywallBackground => ThemeManager.Current.PaywallBackground;

        /// <inheritdoc cref="AppTheme.TooltipBackground"/>
        public static Color TooltipBackground => ThemeManager.Current.TooltipBackground;

        /// <inheritdoc cref="AppTheme.GlassBorderTop"/>
        public static Color GlassBorderTop => ThemeManager.Current.GlassBorderTop;

        /// <inheritdoc cref="AppTheme.GlassBorderBottom"/>
        public static Color GlassBorderBottom => ThemeManager.Current.GlassBorderBottom;

        /// <inheritdoc cref="AppTheme.GlassBorderDefault"/>
        public static Color GlassBorderDefault => ThemeManager.Current.GlassBorderDefault;

        /// <inheritdoc cref="AppTheme.TextPrimary"/>
        public static Color TextPrimary => ThemeManager.Current.TextPrimary;

        /// <inheritdoc cref="AppTheme.TextSecondary"/>
        public static Color TextSecondary => ThemeManager.Current.TextSecondary;

        /// <inheritdoc cref="AppTheme.TextMuted"/>
        public static Color TextMuted => ThemeManager.Current.TextMuted;

        /// <inheritdoc cref="AppTheme.TextOnFilledSurface"/>
        public static Color TextOnFilledSurface => ThemeManager.Current.TextOnFilledSurface;

        /// <inheritdoc cref="AppTheme.TextOnAccent"/>
        public static Color TextOnAccent => ThemeManager.Current.TextOnAccent;

        /// <inheritdoc cref="AppTheme.TextOnDanger"/>
        public static Color TextOnDanger => ThemeManager.Current.TextOnDanger;

        /// <inheritdoc cref="AppTheme.TextOnSuccess"/>
        public static Color TextOnSuccess => ThemeManager.Current.TextOnSuccess;

        /// <inheritdoc cref="AppTheme.TextOnWarning"/>
        public static Color TextOnWarning => ThemeManager.Current.TextOnWarning;

        /// <inheritdoc cref="AppTheme.TextLink"/>
        public static Color TextLink => ThemeManager.Current.TextLink;

        /// <inheritdoc cref="AppTheme.TextDanger"/>
        public static Color TextDanger => ThemeManager.Current.TextDanger;

        /// <inheritdoc cref="AppTheme.TextSuccess"/>
        public static Color TextSuccess => ThemeManager.Current.TextSuccess;

        /// <inheritdoc cref="AppTheme.TextWarning"/>
        public static Color TextWarning => ThemeManager.Current.TextWarning;

        /// <inheritdoc cref="AppTheme.EmojiOnAccent"/>
        public static Color EmojiOnAccent => ThemeManager.Current.EmojiOnAccent;

        /// <inheritdoc cref="AppTheme.EmojiOnDanger"/>
        public static Color EmojiOnDanger => ThemeManager.Current.EmojiOnDanger;

        /// <inheritdoc cref="AppTheme.EmojiOnSuccess"/>
        public static Color EmojiOnSuccess => ThemeManager.Current.EmojiOnSuccess;

        /// <inheritdoc cref="AppTheme.EmojiOnWarning"/>
        public static Color EmojiOnWarning => ThemeManager.Current.EmojiOnWarning;

        /// <inheritdoc cref="AppTheme.EmojiAccent"/>
        public static Color EmojiAccent => ThemeManager.Current.EmojiAccent;

        /// <inheritdoc cref="AppTheme.EmojiPrimary"/>
        public static Color EmojiPrimary => ThemeManager.Current.EmojiPrimary;

        /// <inheritdoc cref="AppTheme.EmojiWarning"/>
        public static Color EmojiWarning => ThemeManager.Current.EmojiWarning;

        /// <inheritdoc cref="AppTheme.EmojiSuccess"/>
        public static Color EmojiSuccess => ThemeManager.Current.EmojiSuccess;

        /// <inheritdoc cref="AppTheme.EmojiDanger"/>
        public static Color EmojiDanger => ThemeManager.Current.EmojiDanger;

        /// <inheritdoc cref="AppTheme.Success"/>
        public static Color Success => ThemeManager.Current.Success;

        /// <inheritdoc cref="AppTheme.SuccessDark"/>
        public static Color SuccessDark => ThemeManager.Current.SuccessDark;

        /// <inheritdoc cref="AppTheme.Error"/>
        public static Color Error => ThemeManager.Current.Error;

        /// <inheritdoc cref="AppTheme.ErrorDark"/>
        public static Color ErrorDark => ThemeManager.Current.ErrorDark;

        /// <inheritdoc cref="AppTheme.Warning"/>
        public static Color Warning => ThemeManager.Current.Warning;



        /// <inheritdoc cref="AppTheme.Gold"/>
        public static Color Gold => ThemeManager.Current.Gold;

        /// <inheritdoc cref="AppTheme.GoldDark"/>
        public static Color GoldDark => ThemeManager.Current.GoldDark;

        /// <inheritdoc cref="AppTheme.PremiumText"/>
        public static Color PremiumText => ThemeManager.Current.PremiumText;

        /// <inheritdoc cref="AppTheme.Diamond"/>
        public static Color Diamond => ThemeManager.Current.Diamond;

        /// <inheritdoc cref="AppTheme.DiamondDark"/>
        public static Color DiamondDark => ThemeManager.Current.DiamondDark;

        /// <inheritdoc cref="AppTheme.Silver"/>
        public static Color Silver => ThemeManager.Current.Silver;

        /// <inheritdoc cref="AppTheme.SilverDark"/>
        public static Color SilverDark => ThemeManager.Current.SilverDark;

        /// <inheritdoc cref="AppTheme.Bronze"/>
        public static Color Bronze => ThemeManager.Current.Bronze;

        /// <inheritdoc cref="AppTheme.BronzeDark"/>
        public static Color BronzeDark => ThemeManager.Current.BronzeDark;

        /// <inheritdoc cref="AppTheme.MonthGradients"/>
        public static (Color Start, Color End)[] MonthGradients => ThemeManager.Current.MonthGradients;

        /// <inheritdoc cref="AppTheme.AuroraStops"/>
        public static AuroraStop[] AuroraStops => ThemeManager.Current.AuroraStops;
    }
}
