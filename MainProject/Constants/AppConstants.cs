using System.Reflection;

namespace MyLovelyMail.MainProject.Constants
{
    public static class AppConstants
    {
        /// <summary> Geliştirici modunu ve test araçlarını açıp kapatır. CANLIYA ÇIKARKEN FALSE YAPILMALI. </summary>
        public const bool TestBuild = false;

        public const string AppName = "MyLovelyMail";

        /// <summary>The one source of the visible app name: window title, tray tooltip, single-instance lookup.</summary>
        public const string AppNameHumanReadable = "My Lovely Mail";

        /// <summary>Launcher sitting at the deploy root; it applies pending updates and starts AppData's exe.</summary>
        public const string LauncherFileName = AppName + ".exe";

        public static int BuildNumber => BuildInfo.BuildNumber;

        public static string CommitHash => BuildInfo.CommitHash;

        public static string SemVer { get; } = ReadSemVer();

        /// <summary>
        /// Shown at the foot of Settings and written into the run lock. Read from the assembly
        /// that is actually running, whose number comes from the single &lt;Version&gt; element in
        /// Directory.Build.props that Release.ps1 rewrites while publishing, combined with the
        /// monotonically increasing build number and commit hash updated on every build.
        /// </summary>
        public static string AppVersion { get; } = $"{SemVer} (Build {BuildInfo.BuildNumber} · {BuildInfo.CommitHash})";

        static string ReadSemVer()
        {
            try
            {
                var assembly = typeof(AppConstants).Assembly;
                // Informational first: it keeps a pre-release suffix ("1.38.0-rc1") that the
                // four-part AssemblyVersion cannot carry. The SDK appends "+<commit sha>" when
                // source link is on, and the foot of a settings page is no place for a hash.
                string? version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                    .InformationalVersion.Split('+')[0];

                if (string.IsNullOrWhiteSpace(version))
                    version = assembly.GetName().Version?.ToString(3);

                return string.IsNullOrWhiteSpace(version) ? UnknownVersion : "v" + version;
            }
            catch (Exception ex)
            {
                Log($"Could not read the assembly version: {ex.Message}", LogLevel.Warning);
                return UnknownVersion;
            }
        }

        /// <summary>Printed when the assembly carries no version at all — visibly wrong rather than a plausible-looking lie.</summary>
        public const string UnknownVersion = "v0.0.0-unknown";
    }
}
