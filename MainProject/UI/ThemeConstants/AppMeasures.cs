namespace MyLovelyMail.MainProject.Constants.ThemeConstants
{
    public static class AppMeasures
    {
        /// <summary> Corner radii in CSS px (Circular/Pill are special — see their notes). </summary>
        public static class Radius
        {
            public const int Px2 = 2;
            public const int Px4 = 4;
            public const int Px6 = 6;
            public const int Px16 = 16;
            public const int Px32 = 32;
            public const int Small = 8;
            public const int Medium = 12;
            public const int Large = 20;
            public const int XLarge = 24;
            /// <summary> Used as a percentage (border-radius: 50%) to turn a square box into a circle. </summary>
            public const int Circular = 50;
            /// <summary> A huge px radius (border-radius: 9999px) that fully rounds the short edges into a pill/stadium. </summary>
            public const int Pill = 9999;
        }

        /// <summary> Backdrop-filter blur strengths in CSS px for acrylic surfaces. </summary>
        public static class Blur
        {
            public const int Light = 4;
            public const int Subtle = 8;
            public const int Normal = 16;
            public const int Strong = 24;
            public const int Heavy = 32;
        }

        /// <summary> Spacing (padding / margin / gap / position offsets) ladder in CSS px. Px* are the raw
        /// steps; close one-off values are merged to the nearest step. T-shirt names are aliases (b = a). </summary>
        public static class Space
        {
            public const int Px2 = 2;
            public const int Px4 = 4;
            public const int Px6 = 6;
            public const int Px8 = 8;
            public const int Px10 = 10;
            public const int Px12 = 12;
            public const int Px14 = 14;
            public const int Px16 = 16;
            public const int Px20 = 20;
            public const int Px24 = 24;
            public const int Px28 = 28;
            public const int Px32 = 32;
            public const int Px40 = 40;
            public const int Px50 = 50;
            public const int Px60 = 60;
            public const int Px80 = 80;
            public const int Px90 = 90;
            public const int Px100 = 100;
            public const int Px120 = 120;
            public const int Px150 = 150;

            public const int XSmall = Px4;
            public const int Small = Px8;
            public const int Medium = Px16;
            public const int Large = Px24;
            public const int XLarge = Px32;

            /// <summary> Top padding that pushes page headers below the device status bar / notch. </summary>
            public const int ScreenTopInset = 50;

            /// <summary> Bottom padding on scroll containers so the last item clears the floating bottom nav: reads the nav's own height from the single source. </summary>
            public const int ScrollBottomInset = Size.BottomNavHeight;
        }

        /// <summary> Element dimensions (width / height / min / max) in CSS px. Px* are exact so layouts do not shift. </summary>
        public static class Size
        {
            public const int Px1 = 1;
            public const int Px4 = 4;
            public const int Px5 = 5;
            public const int Px12 = 12;
            public const int Px14 = 14;
            public const int Px16 = 16;
            public const int Px20 = 20;
            public const int Px22 = 22;
            public const int Px24 = 24;
            public const int Px28 = 28;
            public const int Px32 = 32;
            public const int Px36 = 36;
            public const int Px40 = 40;
            public const int Px44 = 44;
            public const int Px48 = 48;
            public const int Px50 = 50;
            public const int Px60 = 60;
            public const int Px64 = 64;
            public const int Px70 = 70;
            public const int Px80 = 80;
            public const int Px100 = 100;
            public const int Px110 = 110;
            public const int Px120 = 120;
            public const int Px140 = 140;
            public const int Px180 = 180;
            public const int Px200 = 200;
            public const int Px220 = 220;
            public const int Px260 = 260;
            public const int Px280 = 280;
            public const int Px300 = 300;
            public const int Px320 = 320;
            public const int Px360 = 360;
            public const int Px400 = 400;
            public const int Px420 = 420;

            /// <summary> Height of the floating bottom navigation bar. Single source: the nav sets its height from this and the scroll bottom-inset reads it, so they can never drift apart. </summary>
            public const int BottomNavHeight = 70;
        }

        /// <summary> Percentage measures. The number is unitless here; append the '%' sign at the call site. </summary>
        public static class Percent
        {
            public const int Half = 50;
            public const int Most = 88;
            public const int Full = 100;
            public const int Double = 200;
        }

        /// <summary> Viewport-relative sizes. Append the 'vh' suffix at the call site. </summary>
        public static class Viewport
        {
            public const int Most = 80;
            public const int Full = 100;
            public const int Overflow = 110;
        }

        /// <summary> Border / outline thickness in CSS px. </summary>
        public static class Border
        {
            public const int Thin = 1;
            public const int Medium = 2;
            public const int Thick = 3;
            public const int Heavy = 6;
        }

        /// <summary> Letter-spacing (tracking) in CSS px. Strings (not int) so the decimal renders as "0.5" regardless of OS culture. </summary>
        public static class Tracking
        {
            public const string Normal = "0.5";
            public const string Wide = "0.8";
            public const string Widest = "1.5";
        }

        /// <summary> Font sizes in CSS px. </summary>
        public static class Font
        {
            public const int XSmall = 10;
            public const int Small = 12;
            public const int Normal = 14;
            public const int Medium = 16;
            public const int Large = 20;
            public const int Title = 24;
            public const int Hero = 32;
            public const int Headline = 36;
            public const int Display = 40;
            public const int Giant = 48;
            public const int Mega = 64;
        }

        /// <summary> Font weights (the unitless numbers the CSS font-weight property expects). </summary>
        public static class Weight
        {
            public const int Light = 300;
            public const int Normal = 400;
            public const int Semibold = 500;
            public const int Medium = 600;
            public const int Heavy = 700;
            public const int Bold = 800;
            public const int Black = 900;
        }

        /// <summary> Line-height multipliers (unitless ratio of line box height to font size). </summary>
        public static class LineHeight
        {
            public const string None = "1";
            public const string Tight = "1.2";
            public const string Snug = "1.4";
            public const string Normal = "1.5";
            public const string Relaxed = "1.7";
        }

        /// <summary> Opacity levels (0 = invisible, 1 = solid). Strings so the decimal survives any OS culture. </summary>
        public static class Opacity
        {
            public const string Disabled = "0.5";
            public const string Dim = "0.6";
            public const string Muted = "0.7";
            public const string Soft = "0.8";
            public const string Bright = "0.9";
        }

        /// <summary> z-index layers, ordered low to high, naming the kind of UI element that sits at each level. </summary>
        public static class ZLayer
        {
            public const int Base = 1;
            public const int Raised = 2;
            public const int Float = 5;
            public const int Dropdown = 10;
            public const int Sticky = 50;
            public const int Overlay = 100;
            public const int Modal = 1000;
            public const int Popover = 1500;
            public const int Toast = 2000;
            /// <summary> Visual effects (e.g. the profile showcase) sit above everything else so content can never clip them. </summary>
            public const int Effect = 2500;
        }

        //TODO: animasyonların  constant'larını ayrı bir sınıfta barındır. float animasyonAdıSüresi = duration.slow gibi.

        // bunları float yap
        /// <summary> Animation / transition lengths. Append the 's' (seconds) unit at the call site. </summary>
        public static class Duration
        {
            public const string Instant = "0.15";
            public const string Fast = "0.2";
            public const string Normal = "0.3";
            public const string Medium = "0.4";
            public const string Slow = "0.6";
            public const string Slower = "0.8";
            public const string Long = "1";
            public const string Longer = "1.5";
            public const string VerySlow = "2";
            public const string Slowest = "3";
        }

        /// <summary> The single throbber design every spinning loader reuses, so the whole app spins identically. </summary>
        public static class Spinner
        {
            public const int SizePx = 44;
            public const int BorderWidthPx = 4;
            public const int SpinDurationMs = 1000;
        }
    }
}
