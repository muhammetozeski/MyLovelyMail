using MyLovelyMail.MainProject.Constants.ThemeConstants;

namespace MyLovelyMail.MainProject.Constants.ThemeConstants
{
    /// <summary>
    /// Project-wide color accessor. Every property delegates to <see cref="ThemeManager.Current"/> so
    /// swapping the active <see cref="AppTheme"/> reskins every consumer on the next render.
    /// </summary>
    public static class AppColors
    {
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
