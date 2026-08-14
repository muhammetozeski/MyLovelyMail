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
        /// Default palette: midnight-violet aurora backdrop, bright indigo primary, warm coral secondary,
        /// amber accent. Designed for a generic UI template theme — readable, premium, warm.
        /// </summary>
        public static readonly AppTheme PlayfulStarlight = new()
        {
            Name = "Playful Starlight",

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
    }
}
