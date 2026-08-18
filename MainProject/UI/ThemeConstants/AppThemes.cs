namespace MyLovelyMail.MainProject.Constants.ThemeConstants
{
    /// <summary>
    /// Ships every concrete <see cref="AppTheme"/> instance the app can switch into. Adding a new palette
    /// is a single new <c>public static readonly AppTheme</c> field here — never edit individual color
    /// constants scattered across the UI.
    /// </summary>
    public static class AppThemes
    {
        //https://gemini.google.com/app/273756111760fb9a

        /// <summary>
        /// Default palette of MyLovelyMail: a LIGHT pastel look — warm cream backdrop with soft
        /// pink/peach/lavender blooms, rose primary, peach secondary, apricot accent, plum text.
        /// Frosted surfaces are translucent white so cards read as milky glass over the pastel sky.
        /// </summary>
        public static readonly AppTheme LovelyBloom = new()
        {
            Name = "Lovely Bloom",

            BackgroundDeep = Color.FromRgba("#FFE8EEFF"),
            BackgroundBase = Color.FromRgba("#FFF7F2FF"),

            Primary = Color.FromRgba("#EC6FA9FF"),
            PrimaryLight = Color.FromRgba("#F9A8D4FF"),
            PrimaryDark = Color.FromRgba("#D14D8BFF"),
            Secondary = Color.FromRgba("#FF9E80FF"),
            SecondaryDark = Color.FromRgba("#F4714AFF"),
            Accent = Color.FromRgba("#FFB86BFF"),
            AccentDark = Color.FromRgba("#F08C1AFF"),

            TextPrimary = Color.FromRgba("#43293AFF"),
            TextSecondary = Color.FromRgba("#7D5A6EFF"),
            TextMuted = Color.FromRgba("#B08FA1FF"),

            SurfaceSubtle = Colors.White.WithAlpha(0.45f),
            SurfaceNormal = Colors.White.WithAlpha(0.60f),
            SurfaceStrong = Colors.White.WithAlpha(0.78f),
            SurfaceTintPrimary = Color.FromRgba("#EC6FA9FF").WithAlpha(0.14f),
            SurfaceTintAccent = Color.FromRgba("#FFB86BFF").WithAlpha(0.16f),
            SurfaceDarken = Colors.Black.WithAlpha(0.08f),

            GlassBorderTop = Colors.White.WithAlpha(0.90f),
            GlassBorderBottom = Colors.White.WithAlpha(0.50f),
            GlassBorderDefault = Color.FromRgba("#EC6FA9FF").WithAlpha(0.18f),

            TooltipBackground = Color.FromRgba("#FFF0F6FF"),
            PaywallBackground = Color.FromRgba("#FFE3ECFF"),

            Success = Color.FromRgba("#34B27BFF"),
            SuccessDark = Color.FromRgba("#1F8F5FFF"),
            Error = Color.FromRgba("#E85D75FF"),
            ErrorDark = Color.FromRgba("#D23B57FF"),
            Warning = Color.FromRgba("#F5A623FF"),

            Gold = Color.FromRgba("#E9B949FF"),
            GoldDark = Color.FromRgba("#B8860BFF"),
            PremiumText = Color.FromRgba("#8A6508FF"),
            Diamond = Color.FromRgba("#4FB6E8FF"),
            DiamondDark = Color.FromRgba("#1E88C9FF"),
            Silver = Color.FromRgba("#C0C0C0FF"),
            SilverDark = Color.FromRgba("#808080FF"),
            Bronze = Color.FromRgba("#CD7F32FF"),
            BronzeDark = Color.FromRgba("#8B4513FF"),

            MonthGradients =
            [
                (Color.FromRgba("#F9A8D4FF"), Color.FromRgba("#EC6FA9FF")),
                (Color.FromRgba("#FFC9B3FF"), Color.FromRgba("#FF9E80FF")),
                (Color.FromRgba("#E3D0FFFF"), Color.FromRgba("#B79CEFFF")),
                (Color.FromRgba("#B9E8CFFF"), Color.FromRgba("#6FCF97FF")),
                (Color.FromRgba("#FFE0B3FF"), Color.FromRgba("#FFB86BFF")),
                (Color.FromRgba("#BBE3F8FF"), Color.FromRgba("#6FB9E8FF")),
                (Color.FromRgba("#FFC2CCFF"), Color.FromRgba("#F4718DFF")),
                (Color.FromRgba("#C2F0E4FF"), Color.FromRgba("#5FC9B0FF")),
                (Color.FromRgba("#FFD1B3FF"), Color.FromRgba("#F49355FF")),
                (Color.FromRgba("#D9C2F0FF"), Color.FromRgba("#A47FDBFF")),
                (Color.FromRgba("#F5E3B3FF"), Color.FromRgba("#E9B949FF")),
                (Color.FromRgba("#C2D8FFFF"), Color.FromRgba("#7FA8EFFF"))
            ],

            AuroraStops =
            [
                new(Color.FromRgba("#FFC4D8FF"), "20% 15%", "55% 60%"),
                new(Color.FromRgba("#FFD9C2FF"), "85% 35%", "60% 55%"),
                new(Color.FromRgba("#E3D0FFFF"), "30% 95%", "60% 50%")
            ]
        };

        /// <summary>
        /// Default palette: midnight-violet aurora backdrop, bright indigo primary, warm coral secondary,
        /// amber accent. Designed for a generic UI template theme — readable, premium, warm.
        /// </summary>
        public static readonly AppTheme PlayfulStarlight = new()
        {
            Name = "Playful Starlight",
            IsDark = true,

            BackgroundDeep = Color.FromArgb("#070A1A"),
            BackgroundBase = Color.FromArgb("#0E1230"),

            Primary = Color.FromArgb("#7C7BFF"),
            PrimaryLight = Color.FromArgb("#A78BFA"),
            PrimaryDark = Color.FromArgb("#4F46E5"),
            Secondary = Color.FromArgb("#FF7A6B"),
            SecondaryDark = Color.FromArgb("#E5563F"),
            Accent = Color.FromArgb("#FFB547"),
            AccentDark = Color.FromArgb("#E0901B"),

            TextPrimary = Color.FromArgb("#F4F6FF"),
            TextSecondary = Color.FromArgb("#A8B1D9"),
            TextMuted = Color.FromArgb("#6B73A3"),

            // [Rule] sen, yapay zeka gerizekalı olduğu için FromArgb fonksiyonunun ARGB şeklinde hex kodu beklediğini anlayamıyorsun ve RGBA giriyorsun.
            // bu yüzden FromArgb yerine FromRgba fonksiyonunu kullan. string girebilirsin ona.

            SurfaceSubtle = Colors.White.WithAlpha(0.05f),
            SurfaceNormal = Colors.White.WithAlpha(0.08f),
            SurfaceStrong = Colors.White.WithAlpha(0.14f),
            SurfaceTintPrimary = Color.FromArgb("#7C7BFF").WithAlpha(0.12f),
            SurfaceTintAccent = Color.FromArgb("#FFB547").WithAlpha(0.12f),
            SurfaceDarken = Colors.Black.WithAlpha(0.25f),

            GlassBorderTop = Colors.White.WithAlpha(0.20f),
            GlassBorderBottom = Colors.White.WithAlpha(0.08f),
            GlassBorderDefault = Colors.White.WithAlpha(0.12f),

            TooltipBackground = Color.FromArgb("#1E1B3A"),
            PaywallBackground = Color.FromArgb("#0F0C29"),

            Success = Color.FromArgb("#10B981"),
            SuccessDark = Color.FromArgb("#059669"),
            Error = Color.FromArgb("#EF4444"),
            ErrorDark = Color.FromArgb("#DC2626"),
            Warning = Color.FromArgb("#FFB547"),



            Gold = Color.FromArgb("#FFD700"),
            GoldDark = Color.FromArgb("#B8860B"),
            PremiumText = Color.FromArgb("#FFF6D9"),
            Diamond = Color.FromRgba("#38BDF8FF"),
            DiamondDark = Color.FromRgba("#0284C7FF"),
            Silver = Color.FromArgb("#C0C0C0"),
            SilverDark = Color.FromArgb("#808080"),
            Bronze = Color.FromArgb("#CD7F32"),
            BronzeDark = Color.FromArgb("#8B4513"),

            MonthGradients =
            [
                (Color.FromArgb("#4F46E5"), Color.FromArgb("#2563EB")),
                (Color.FromArgb("#FF7A6B"), Color.FromArgb("#E5563F")),
                (Color.FromArgb("#A78BFA"), Color.FromArgb("#7C3AED")),
                (Color.FromArgb("#34D399"), Color.FromArgb("#059669")),
                (Color.FromArgb("#FFB547"), Color.FromArgb("#E0901B")),
                (Color.FromArgb("#38BDF8"), Color.FromArgb("#0284C7")),
                (Color.FromArgb("#FB7185"), Color.FromArgb("#E11D48")),
                (Color.FromArgb("#2DD4BF"), Color.FromArgb("#0D9488")),
                (Color.FromArgb("#F97316"), Color.FromArgb("#C2410C")),
                (Color.FromArgb("#8B5CF6"), Color.FromArgb("#6D28D9")),
                (Color.FromArgb("#F59E0B"), Color.FromArgb("#B45309")),
                (Color.FromArgb("#60A5FA"), Color.FromArgb("#1D4ED8"))
            ],

            AuroraStops =
            [
                new(Color.FromArgb("#3B2A8C"), "20% 15%", "55% 60%"),
                new(Color.FromArgb("#5B2E94"), "85% 35%", "60% 55%"),
                new(Color.FromArgb("#7A2350"), "30% 95%", "60% 50%")
            ]
        };

        /// <summary>Every shipped palette, in the order the theme picker lists them.</summary>
        public static readonly AppTheme[] All = [LovelyBloom, PlayfulStarlight];
    }
}
