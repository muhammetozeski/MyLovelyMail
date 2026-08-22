using System.Globalization;
using System.Runtime.CompilerServices;

namespace MyLovelyMail.MainProject.Constants
{
    /// <summary>
    /// Pins every format call in the app to English. The interface is written in English, but
    /// number and date formatting followed the machine's locale, so a Turkish Windows rendered
    /// "9.624" for nine thousand and "18 Ağu" inside otherwise English sentences.
    /// <para>
    /// A module initializer rather than a startup call: MauiProgram, the Web host and
    /// MigrationService all start the app, and a fourth entry point would eventually forget.
    /// </para>
    /// </summary>
    public static class UiCulture
    {
        /// <summary>The one culture the interface formats in.</summary>
        public static readonly CultureInfo Display = CultureInfo.GetCultureInfo("en-US");

        [ModuleInitializer]
        internal static void Apply()
        {
            CultureInfo.DefaultThreadCurrentCulture = Display;
            CultureInfo.DefaultThreadCurrentUICulture = Display;
            CultureInfo.CurrentCulture = Display;
            CultureInfo.CurrentUICulture = Display;
        }
    }
}
